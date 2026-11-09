using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationCameraPreferences(string SceneId, string CameraId, bool Overlay = false);

public sealed partial class PipersPlugin
{
    private JsonObject? simulationCameras;

    private long simulationCameraRefreshAttempt;
    private Task? simulationCameraRefreshWorker;
    private long simulationCameraDiscoveryEpoch;
    private sealed record SimulationCameraDiscovery(long Epoch, JsonObject Cameras);
    private SimulationCameraDiscovery? pendingSimulationCameraDiscovery;
    private readonly CancellationTokenSource cameraDiscoveryShutdown = new();

    private async Task MaintainCameraDiscovery(CancellationToken cancellation)
    {
        if (!passiveMeasuredTrackingStarted || cameraDiscoveryShutdown.IsCancellationRequested || switchingProfile)
            return;
        try
        {
            if (!await commands.WaitAsync(0, cancellation))
                return;
            bool refreshSettings;
            try
            {
                if (!passiveMeasuredTrackingStarted || cameraDiscoveryShutdown.IsCancellationRequested || switchingProfile)
                    return;
                // Manual refresh and preference changes also use the command gate.
                // Probe completion only queues evidence; this tick applies it.
                QueueSimulationCameraRefresh(cancellation);
                QueueCameraDeviceRefresh(cancellation);
                if (Volatile.Read(ref pendingSimulationCameraDiscovery) is null &&
                    Volatile.Read(ref pendingCameraDeviceInventory) is null)
                    return;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cameraDiscoveryShutdown.Token);
                var discovered = Interlocked.Exchange(ref pendingSimulationCameraDiscovery, null);
                if (discovered is not null && discovered.Epoch == Interlocked.Read(ref simulationCameraDiscoveryEpoch))
                    await ApplySimulationCameraDiscovery(discovered.Cameras, lifetime.Token);
                refreshSettings = await ApplyCameraDeviceInventory(lifetime.Token);
            }
            finally { commands.Release(); }
            if (refreshSettings && passiveMeasuredTrackingStarted && !cameraDiscoveryShutdown.IsCancellationRequested && Navigation.Page == WorkspacePage.Settings)
                await Render(cancellation);
        }
        catch (OperationCanceledException) when (cameraDiscoveryShutdown.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Camera maintenance: {0}", error.Message);
        }
    }

    private void QueueSimulationCameraRefresh(CancellationToken cancellation)
    {
        if (!passiveMeasuredTrackingStarted || cameraDiscoveryShutdown.IsCancellationRequested || switchingProfile || simulationCameraRefreshWorker is { IsCompleted: false } ||
            simulationCameraRefreshAttempt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(simulationCameraRefreshAttempt).TotalSeconds < 2)
            return;
        simulationCameraRefreshAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        simulationCameraRefreshWorker = ReadSimulationCameraDiscovery(Interlocked.Read(ref simulationCameraDiscoveryEpoch), cancellation);
    }

    private async Task ReadSimulationCameraDiscovery(long epoch, CancellationToken cancellation)
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
            Interlocked.Exchange(ref pendingSimulationCameraDiscovery, new(epoch, discovered));
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Simulation camera discovery delayed: {0}", error.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task DiscoverSimulationCameras(CancellationToken cancellation)
    {
        // An explicit refresh supersedes an older periodic read still in flight.
        Interlocked.Increment(ref simulationCameraDiscoveryEpoch);
        Interlocked.Exchange(ref pendingSimulationCameraDiscovery, null);
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

    private async Task CameraComparisonEvent(string camera, string action, JsonElement value, string wireId, CancellationToken cancellation)
    {
        if (action == "simrefresh") { QueueCameraDeviceRefresh(cancellation, force: true); await DiscoverSimulationCameras(cancellation); await Render(cancellation); return; }
        var bindings = new Dictionary<string, SimulationCameraPreferences>(preferences.SimulationCameras ?? []);
        if (action == "simselect")
        {
            var view = Volatile.Read(ref publishedWorkspaceView);
            var inventory = simulationCameras;
            int selection = value.GetInt32();
            const string changed = "Simulation cameras changed. Click Refresh cameras and select the counterpart again.";
            if (view is null || Volatile.Read(ref workspacePublicationUncertain) != 0 || view.Context != CurrentWorkspaceViewContext ||
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
