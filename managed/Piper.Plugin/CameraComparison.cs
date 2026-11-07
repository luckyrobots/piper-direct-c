using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationCameraPreferences(string SceneId, string CameraId, bool Overlay = false);

public sealed partial class PipersPlugin
{
    private JsonObject? simulationCameras;

    private long simulationCameraRefreshAttempt;
    private Task? simulationCameraRefreshWorker;
    private JsonObject? pendingSimulationCameraDiscovery;
    private readonly CancellationTokenSource cameraDiscoveryShutdown = new();

    private async Task StopCameraDiscoveryOnDispose()
    {
        await cameraDiscoveryShutdown.CancelAsync();
        var tasks = new[] { simulationCameraRefreshWorker, cameraDeviceDiscoveryWorker }.OfType<Task>().ToArray();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        Interlocked.Exchange(ref pendingSimulationCameraDiscovery, null);
        Interlocked.Exchange(ref pendingCameraDeviceInventory, null);
        cameraDiscoveryShutdown.Dispose();
    }

    private async Task RefreshSimulationCameraBindings(CancellationToken cancellation)
    {
        QueueSimulationCameraRefresh(cancellation);
        var discovered = Interlocked.Exchange(ref pendingSimulationCameraDiscovery, null);
        if (discovered is null || switchingProfile)
            return;
        await ApplySimulationCameraDiscovery(discovered, cancellation);
    }

    private void QueueSimulationCameraRefresh(CancellationToken cancellation)
    {
        if (!passiveMeasuredTrackingStarted || cameraDiscoveryShutdown.IsCancellationRequested || switchingProfile || simulationCameraRefreshWorker is { IsCompleted: false } ||
            simulationCameraRefreshAttempt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(simulationCameraRefreshAttempt).TotalSeconds < 2)
            return;
        simulationCameraRefreshAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        simulationCameraRefreshWorker = ReadSimulationCameraDiscovery(cancellation);
    }

    private async Task ReadSimulationCameraDiscovery(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cameraDiscoveryShutdown.Token);
        cancellation = lifetime.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(600);
            var discovered = JsonNode.Parse((await engine.RequestAsync("scene.cameras", cancellation: timeout.Token)).GetRawText())!.AsObject();
            Materialize(discovered);
            if (scene?["sceneId"]?.GetValue<string>() != discovered["sceneId"]?.GetValue<string>())
            {
                var robots = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: timeout.Token)).GetRawText())!.AsObject();
                Materialize(robots);
                if (robots["sceneId"]?.GetValue<string>() == discovered["sceneId"]?.GetValue<string>())
                    discovered["robotInventory"] = robots;
            }
            Interlocked.Exchange(ref pendingSimulationCameraDiscovery, discovered);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Simulation camera discovery delayed: {0}", error.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private string[] SimulationCameraNames() => new[] { "Automatic counterpart" }.Concat(
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
        await ApplySimulationCameraDiscovery(discovered, cancellation);
    }

    private async Task ApplySimulationCameraDiscovery(JsonObject discovered, CancellationToken cancellation)
    {
        simulationCameras = discovered;
        if (discovered["robotInventory"] is JsonObject robots)
            scene = robots.DeepClone().AsObject();
        // Entity UUIDs survive scene reload; session scene IDs do not. Refresh
        // every profile's valid camera bindings, including inactive robots.
        string before = JsonSerializer.Serialize(preferences, LuckyEngine.PluginSdk.PluginProtocol.Json);
        preferences = SimulationCameraRouting.Refresh(preferences, simulationCameras);
        if (before != JsonSerializer.Serialize(preferences, LuckyEngine.PluginSdk.PluginProtocol.Json))
            await Save(cancellation);
    }

    private async Task CameraComparisonEvent(string camera, string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "simrefresh") { QueueCameraDeviceRefresh(cancellation, force: true); await DiscoverSimulationCameras(cancellation); await Render(cancellation); return; }
        var bindings = new Dictionary<string, SimulationCameraPreferences>(preferences.SimulationCameras ?? []);
        if (action == "simselect")
        {
            int index = value.GetInt32() - 1;
            if (index < 0) bindings.Remove(camera);
            else if (simulationCameras is not null && index < simulationCameras["cameras"]!.AsArray().Count)
                bindings[camera] = new(simulationCameras["sceneId"]!.GetValue<string>(), simulationCameras["cameras"]![index]!["entityId"]!.GetValue<string>());
        }
        else if (action == "simoverlay" && !D405View.MetricDepth(camera, CameraSettings(camera)) && bindings.TryGetValue(camera, out var saved))
            bindings[camera] = saved with { Overlay = !saved.Overlay };
        preferences = preferences with { SimulationCameras = bindings };
        await Save(cancellation);
    }

    private void AddCameraComparisonPatches(Dictionary<string, JsonObject> patches)
    {
        foreach (string id in new[] { "bench", "gripper", "depth" })
        {
            var saved = preferences.SimulationCameras?.GetValueOrDefault(id);
            bool available = SimulationCameraRouting.IsAvailable(saved, simulationCameras);
            var size = cameraDimensions.GetValueOrDefault(id, (Width: 640, Height: 480));
            cameraCalibration.TryGetValue(id, out var calibration);
            bool metricDepth = D405View.MetricDepth(id, CameraSettings(id));
            string streamKind = metricDepth ? "depth" : "color";
            string identity = id == "depth" ? preferences.DepthSerial : CameraSettings(id).Device;
            if (id == "depth" && string.IsNullOrEmpty(identity)) identity = calibration?["device"]?.GetValue<string>() ?? "";
            bool matches = calibration is not null && (id == "depth"
                ? CameraProfile.MatchesD405(calibration, identity, size.Width, size.Height, streamKind)
                : CameraProfile.Matches(calibration, identity, size.Width, size.Height, false));
            patches[$"camera.{id}.sim"] = new()
            {
                ["sceneId"] = saved?.SceneId ?? "", ["cameraId"] = saved?.CameraId ?? "",
                ["visible"] = available,
                ["overlayImageId"] = saved?.Overlay == true && !metricDepth && CameraFrameReady(id) ? CameraImageId(id) : "",
                ["captureWidth"] = size.Width, ["captureHeight"] = size.Height, ["blend"] = .5,
                ["streamKind"] = streamKind, ["lensModel"] = id == "gripper" ? "fisheye" : "pinhole",
                ["intrinsics"] = matches ? calibration!.DeepClone() : null
            };
            patches[$"camera.{id}.sim.status"] = new() { ["text"] = !available
                ? "Simulation camera unavailable · choose one in Camera settings"
                : teleoperating ? "SIM leader pose · not time synchronized with measured camera"
                : matches ? "Lens profile active · mount alignment / exposure sync unverified"
                : id == "depth" && !metricDepth && calibration?["model"]?.GetValue<string>() == "realsense-distorted"
                    ? "Real color uses factory distortion · sim lens/mount alignment unverified"
                : calibration is not null ? "Profile mismatch · check camera device and resolution"
                : id == "gripper" ? "Estimated 150° fisheye · use ⚙ to calibrate lens and mount"
                : "Estimated pose/lens · calibration not verified · preview clocks not synchronized" };
            patches[$"camera.{id}.simoverlay"] = new() { ["text"] = saved?.Overlay == true ? "Show simulation only" : "Real / simulation overlay", ["enabled"] = !metricDepth };
        }
    }
}
