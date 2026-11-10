using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationCameraPreferences(string SceneId, string CameraId, bool Overlay = false);

public sealed partial class PipersPlugin
{
    internal JsonObject? simulationCameras => calibration.Cameras.SimulationCameras;
    private bool CameraDiscoveryRunning => passiveMeasuredTrackingStarted && !calibration.Cameras.Retired;

    private async Task MaintainCameraDiscovery(CancellationToken cancellation)
    {
        if (!CameraDiscoveryRunning || switchingProfile)
            return;
        try
        {
            if (!await commands.WaitAsync(0, cancellation))
                return;
            bool refreshSettings;
            try
            {
                if (!CameraDiscoveryRunning || switchingProfile)
                    return;
                // Manual refresh and preference changes also use the command gate.
                // Probe completion only queues evidence; this tick applies it.
                QueueSimulationCameraRefresh(cancellation);
                QueueCameraDeviceRefresh(cancellation);
                if (!calibration.Cameras.SimulationProbe.HasPending && !calibration.Cameras.DeviceProbe.HasPending)
                    return;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, calibration.Cameras.Shutdown);
                if (calibration.Cameras.SimulationProbe.Take() is { } discovered && discovered.Epoch == calibration.Cameras.Epoch)
                    await ApplySimulationCameraDiscovery(discovered.Cameras, lifetime.Token);
                refreshSettings = await ApplyCameraDeviceInventory(lifetime.Token);
            }
            finally { commands.Release(); }
            if (refreshSettings && CameraDiscoveryRunning && Navigation.Page == WorkspacePage.Settings)
                await Render(cancellation);
        }
        catch (OperationCanceledException) when (calibration.Cameras.Retired && !cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Camera maintenance: {0}", error.Message);
        }
    }

    // Stops both discovery probes for shutdown and drops any unapplied result.
    private Task RetireCameraDiscovery() => calibration.Cameras.Retire();

    private void QueueSimulationCameraRefresh(CancellationToken cancellation)
    {
        if (!CameraDiscoveryRunning || switchingProfile) return;
        long epoch = calibration.Cameras.Epoch;
        calibration.Cameras.SimulationProbe.Queue(() => ReadSimulationCameraDiscovery(epoch, cancellation));
    }

    private async Task<SimulationCameraDiscovery?> ReadSimulationCameraDiscovery(long epoch, CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, calibration.Cameras.Shutdown);
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
            return new(epoch, discovered);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Simulation camera discovery delayed: {0}", error.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        return null;
    }

    private async Task DiscoverSimulationCameras(CancellationToken cancellation)
    {
        // An explicit refresh supersedes an older periodic read still in flight.
        calibration.Cameras.Supersede();
        var discovered = JsonNode.Parse((await engine.RequestAsync("scene.cameras", cancellation: cancellation)).GetRawText())!.AsObject();
        Materialize(discovered);
        await ApplySimulationCameraDiscovery(discovered, cancellation);
    }

    private async Task ApplySimulationCameraDiscovery(JsonObject discovered, CancellationToken cancellation)
    {
        calibration.Cameras.ApplySimulation(discovered);
        if (discovered["robotInventory"] is JsonObject robots)
            scene = robots.DeepClone().AsObject();
        // Entity UUIDs survive scene reload; session scene IDs do not. Refresh
        // every profile's valid camera bindings, including inactive robots.
        string before = JsonSerializer.Serialize(preferences, LuckyEngine.PluginSdk.PluginProtocol.Json);
        preferences = SimulationCameraRouting.Refresh(preferences, discovered);
        if (before != JsonSerializer.Serialize(preferences, LuckyEngine.PluginSdk.PluginProtocol.Json))
            await Save(cancellation);
    }

    private async Task CameraComparisonEvent(string camera, string action, JsonElement value, string wireId, CancellationToken cancellation)
    {
        if (action == "simrefresh") { QueueCameraDeviceRefresh(cancellation, force: true); await DiscoverSimulationCameras(cancellation); await Render(cancellation); return; }
        var bindings = new Dictionary<string, SimulationCameraPreferences>(preferences.SimulationCameras ?? []);
        if (action == "simselect")
        {
            var view = workspace.Confirmed(CurrentWorkspaceViewContext);
            var inventory = simulationCameras;
            int selection = value.GetInt32();
            const string changed = "Simulation cameras changed. Click Refresh cameras and select the counterpart again.";
            if (view is null ||
                view.Context is not { Page: WorkspacePage.Settings, SettingsCategory: SettingsCategory.Cameras } || view.Context.SettingsCamera != camera ||
                wireId != WorkspaceEventScope.Wrap(view.Epoch, view.Context.Profile, "camera." + camera + ".simselect") ||
                view.CameraChoices.SceneId != (inventory?["sceneId"]?.GetValue<string>() ?? "") ||
                selection < 0 || selection > view.CameraChoices.Cameras.Length)
                throw new InvalidOperationException(changed);
            if (selection == 0) bindings.Remove(camera);
            else
            {
                var selected = new SimulationCameraPreferences(view.CameraChoices.SceneId, view.CameraChoices.Cameras[selection - 1].Id);
                if (!SimulationCameraRouting.IsAvailable(selected, inventory)) throw new InvalidOperationException(changed);
                bindings[camera] = selected;
            }
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
            var size = calibration.Capture.Size(id) ?? new(640, 480);
            var profile = calibration.Lens.Profile(id);
            bool metricDepth = D405View.MetricDepth(id, CameraSettings(id));
            string streamKind = metricDepth ? "depth" : "color";
            string identity = id == "depth" ? preferences.DepthSerial : CameraSettings(id).Device;
            if (id == "depth" && string.IsNullOrEmpty(identity)) identity = profile?["device"]?.GetValue<string>() ?? "";
            bool matches = profile is not null && (id == "depth"
                ? CameraProfile.MatchesD405(profile, identity, size.Width, size.Height, streamKind)
                : CameraProfile.MatchesFisheye(profile, identity, size.Width, size.Height));
            patches[$"camera.{id}.sim"] = new()
            {
                ["sceneId"] = saved?.SceneId ?? "", ["cameraId"] = saved?.CameraId ?? "",
                ["visible"] = available,
                ["overlayImageId"] = saved?.Overlay == true && !metricDepth && CameraFrameReady(id) ? CameraImageId(id) : "",
                ["captureWidth"] = size.Width, ["captureHeight"] = size.Height, ["blend"] = .5,
                ["streamKind"] = streamKind, ["lensModel"] = id == "gripper" ? "fisheye" : "pinhole",
                ["intrinsics"] = matches ? profile!.DeepClone() : null
            };
            patches[$"camera.{id}.sim.status"] = new() { ["text"] = !available
                ? "Simulation camera unavailable · choose one in Camera settings"
                : teleoperating ? "SIM leader pose · not time synchronized with measured camera"
                : matches ? "Lens profile active · mount alignment / exposure sync unverified"
                : id == "depth" && !metricDepth && profile?["model"]?.GetValue<string>() == "realsense-distorted"
                    ? "Real color uses factory distortion · sim lens/mount alignment unverified"
                : profile is not null ? "Profile mismatch · check camera device and resolution"
                : id == "gripper" ? "Estimated 150° fisheye · use ⚙ to calibrate lens and mount"
                : "Estimated pose/lens · calibration not verified · preview clocks not synchronized" };
            patches[$"camera.{id}.simoverlay"] = new() { ["text"] = saved?.Overlay == true ? "Show simulation only" : "Real / simulation overlay", ["enabled"] = !metricDepth };
        }
    }
}
