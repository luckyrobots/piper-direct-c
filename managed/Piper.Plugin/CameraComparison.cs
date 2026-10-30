using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationCameraPreferences(string SceneId, string CameraId, bool Overlay = false);

public sealed partial class PipersPlugin
{
    private JsonObject? simulationCameras;

    private string[] SimulationCameraNames() => new[] { "Choose simulation counterpart" }.Concat(
        simulationCameras?["cameras"]?.AsArray().Select(c => c?["name"]?.GetValue<string>() ?? "Camera") ?? []).ToArray();

    private int SimulationCameraIndex(string id)
    {
        var saved = preferences.SimulationCameras?.GetValueOrDefault(id);
        if (saved is null || simulationCameras is null) return 0;
        var cameras = simulationCameras["cameras"]!.AsArray();
        for (int i = 0; i < cameras.Count; i++) if (cameras[i]?["entityId"]?.GetValue<string>() == saved.CameraId) return i + 1;
        return 0;
    }

    private async Task DiscoverSimulationCameras(CancellationToken cancellation)
    {
        var discovered = JsonNode.Parse((await engine.RequestAsync("scene.cameras", cancellation: cancellation)).GetRawText())!.AsObject();
        Materialize(discovered);
        simulationCameras = discovered;
        string sceneId = simulationCameras["sceneId"]!.GetValue<string>();
        // Entity UUIDs survive scene reload; session scene IDs do not. Refresh
        // every profile's valid camera bindings, including inactive robots.
        preferences = RobotProfileRouting.RefreshCameraBindings(preferences, sceneId,
            simulationCameras["cameras"]!.AsArray().Select(c => c!["entityId"]!.GetValue<string>()));
        await Save(cancellation);
    }

    private async Task CameraComparisonEvent(string camera, string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "simrefresh") { await DiscoverSimulationCameras(cancellation); await Render(cancellation); return; }
        var bindings = new Dictionary<string, SimulationCameraPreferences>(preferences.SimulationCameras ?? []);
        if (action == "simselect")
        {
            int index = value.GetInt32() - 1;
            if (index < 0) bindings.Remove(camera);
            else if (simulationCameras is not null && index < simulationCameras["cameras"]!.AsArray().Count)
                bindings[camera] = new(simulationCameras["sceneId"]!.GetValue<string>(), simulationCameras["cameras"]![index]!["entityId"]!.GetValue<string>());
        }
        else if (action == "simoverlay" && camera != "depth" && bindings.TryGetValue(camera, out var saved))
            bindings[camera] = saved with { Overlay = !saved.Overlay };
        preferences = preferences with { SimulationCameras = bindings };
        await Save(cancellation);
    }

    private void AddCameraComparisonPatches(Dictionary<string, JsonObject> patches)
    {
        foreach (string id in new[] { "bench", "gripper", "depth" })
        {
            var saved = preferences.SimulationCameras?.GetValueOrDefault(id);
            var size = cameraDimensions.GetValueOrDefault(id, (Width: 640, Height: 480));
            cameraCalibration.TryGetValue(id, out var calibration);
            string identity = id == "depth" ? preferences.DepthSerial : CameraSettings(id).Device;
            if (id == "depth" && string.IsNullOrEmpty(identity)) identity = calibration?["device"]?.GetValue<string>() ?? "";
            bool matches = calibration is not null && CameraProfile.Matches(calibration, identity, size.Width, size.Height, id == "depth");
            patches[$"camera.{id}.sim"] = new()
            {
                ["sceneId"] = saved?.SceneId ?? "", ["cameraId"] = saved?.CameraId ?? "",
                ["overlayImageId"] = saved?.Overlay == true && id != "depth" && CameraFrameReady(id) ? CameraImageId(id) : "",
                ["captureWidth"] = size.Width, ["captureHeight"] = size.Height, ["blend"] = .5,
                ["streamKind"] = id == "depth" ? "depth" : "color", ["lensModel"] = id == "gripper" ? "fisheye" : "pinhole",
                ["intrinsics"] = matches ? calibration!.DeepClone() : null
            };
            patches[$"camera.{id}.sim.status"] = new() { ["text"] = saved is null
                ? "Choose a counterpart using ⚙ → Find simulation cameras"
                : teleoperating ? "SIM leader pose · not time synchronized with measured camera"
                : matches ? "Lens profile active · mount alignment / exposure sync unverified"
                : calibration is not null ? "Profile mismatch · check camera device and resolution"
                : id == "gripper" ? "Estimated 150° fisheye · use ⚙ to calibrate lens and mount"
                : "Estimated pose/lens · calibration not verified · preview clocks not synchronized" };
            patches[$"camera.{id}.simoverlay"] = new() { ["text"] = saved?.Overlay == true ? "Show simulation only" : "Real / simulation overlay" };
        }
    }
}
