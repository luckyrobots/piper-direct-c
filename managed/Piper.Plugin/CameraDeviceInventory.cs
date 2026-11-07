using System.Text.Json;
using System.Text.RegularExpressions;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed record ColorCameraDevice(string Name, string Path, string UsbLocation = "");
public sealed record DepthCameraDevice(string Name, string Serial);
public sealed record CameraDeviceChoice(string Id, string Label);

public static class CameraDeviceChoices
{
    public static CameraDeviceChoice[] Merge(CameraDeviceChoice[] previous, ColorCameraDevice[] current)
    {
        var ids = previous.Select(choice => choice.Id).Concat(current.Select(Id)).Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal).Take(256).ToArray();
        return ids.Select(id =>
        {
            var device = current.SingleOrDefault(item => Id(item) == id);
            if (device is null)
            {
                string label = previous.FirstOrDefault(choice => choice.Id == id)?.Label ?? "Saved camera";
                return new CameraDeviceChoice(id, label.EndsWith(" · disconnected", StringComparison.Ordinal) ? label : label + " · disconnected");
            }
            string name = Regex.Replace(device.Name, @"\s+", " ").Trim();
            string suffix = device.UsbLocation.Length > 0 ? " · USB " + device.UsbLocation :
                current.Count(item => item.Name == device.Name) > 1 ? " · camera " + (Array.FindIndex(ids, candidate => candidate == id) + 1) : "";
            return new(id, name + suffix);
        }).ToArray();
    }

    public static string Id(ColorCameraDevice device) => device.Path.Length > 0 ? device.Path : device.Name;
    public static string ResolveSaved(string saved, string pikaDevice, ColorCameraDevice[] devices)
    {
        if (devices.Any(device => Id(device) == saved)) return saved;
        var matches = devices.Where(device => device.Name == saved).ToArray();
        if (matches.Length == 1) return Id(matches[0]);
        var host = Regex.Match(pikaDevice, @"^/dev/pika/by-host/(\d+)-\d+$", RegexOptions.CultureInvariant);
        if (matches.Length > 1 && host.Success)
        {
            var sameHub = matches.Where(device => device.UsbLocation.Split('-')[0] == host.Groups[1].Value).ToArray();
            if (sameHub.Length == 1) return Id(sameHub[0]);
        }
        return saved;
    }

    public static int Index(CameraDeviceChoice[] choices, string id)
    {
        int index = Array.FindIndex(choices, choice => choice.Id == id);
        return index < 0 ? 0 : index + 1;
    }

    public static string Resolve(CameraDeviceChoice[] choices, JsonElement value, string unchanged)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        int index = value.GetInt32();
        if (index == 0) return unchanged;
        if (index < 1 || index > choices.Length) throw new InvalidOperationException("Camera list changed; refresh and choose the camera again.");
        return choices[index - 1].Id;
    }
}

public sealed partial class PipersPlugin
{
    private sealed record CameraDeviceInventory(ColorCameraDevice[]? Color, DepthCameraDevice[]? Depth);
    private CameraDeviceChoice[] cameraDeviceChoices = [], depthDeviceChoices = [];
    private Task? cameraDeviceDiscoveryWorker;
    private CameraDeviceInventory? pendingCameraDeviceInventory;
    private long cameraDeviceDiscoveryAttempt;
    private string cameraDeviceDiscoveryStatus = "Camera inventory is loading…";

    private void QueueCameraDeviceRefresh(CancellationToken cancellation, bool force = false)
    {
        if (!passiveMeasuredTrackingStarted || cameraDiscoveryShutdown.IsCancellationRequested || cameraDeviceDiscoveryWorker is { IsCompleted: false } ||
            !force && cameraDeviceDiscoveryAttempt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(cameraDeviceDiscoveryAttempt).TotalSeconds < 15)
            return;
        cameraDeviceDiscoveryAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        cameraDeviceDiscoveryWorker = ReadCameraDevices(cancellation);
    }

    private async Task ReadCameraDevices(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cameraDiscoveryShutdown.Token);
        cancellation = lifetime.Token;
        try
        {
            var color = RunCameraTool(["devices"], TimeSpan.FromSeconds(8), cancellation);
            var depth = RunCameraTool(["d405-profiles"], TimeSpan.FromSeconds(8), cancellation);
            await Task.WhenAll(color, depth);
            if (color.Result.ExitCode != 0 && depth.Result.ExitCode != 0) throw new IOException(color.Result.Error + " " + depth.Result.Error);
            var inventory = new CameraDeviceInventory(
                color.Result.ExitCode == 0 ? JsonSerializer.Deserialize<ColorCameraDevice[]>(color.Result.Output, PluginProtocol.Json) ?? [] : null,
                depth.Result.ExitCode == 0 ? JsonSerializer.Deserialize<DepthCameraDevice[]>(depth.Result.Output, PluginProtocol.Json) ?? [] : null);
            Interlocked.Exchange(ref pendingCameraDeviceInventory, inventory);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { cameraDeviceDiscoveryStatus = "Camera discovery: " + error.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task RefreshCameraDeviceInventory(CancellationToken cancellation)
    {
        QueueCameraDeviceRefresh(cancellation);
        var inventory = Interlocked.Exchange(ref pendingCameraDeviceInventory, null);
        if (inventory is null) return;
        string previous = JsonSerializer.Serialize(cameraDeviceChoices, PluginProtocol.Json) + JsonSerializer.Serialize(depthDeviceChoices, PluginProtocol.Json);
        if (inventory.Color is { } colorInventory) cameraDeviceChoices = CameraDeviceChoices.Merge(cameraDeviceChoices, colorInventory);
        if (inventory.Depth is { } depthInventory)
            depthDeviceChoices = CameraDeviceChoices.Merge(depthDeviceChoices,
                depthInventory.Select(device => new ColorCameraDevice(device.Name + " · " + device.Serial, device.Serial)).ToArray());
        cameraDeviceDiscoveryStatus = $"Video inventory: {(inventory.Color is null ? "temporarily unavailable" : inventory.Color.Length)} · D405 inventory: {(inventory.Depth is null ? "temporarily unavailable" : inventory.Depth.Length)}";
        string oldGripper = CameraSettings("gripper").Device, oldBench = CameraSettings("bench").Device;
        string previousPreferences = JsonSerializer.Serialize(preferences, PluginProtocol.Json);
        preferences = RobotProfileRouting.Capture(preferences);
        var profiles = preferences.Robots!.Select(profile =>
        {
            var settings = profile.Gripper ?? new CameraPreferences(OperatingSystem.IsWindows() ? "DECXIN  CAMERA" : "", OperatingSystem.IsWindows());
            string unique = CameraDeviceChoices.ResolveSaved(settings.Device, profile.PikaDevice, inventory.Color ?? []);
            return unique == settings.Device ? profile : profile with { Gripper = settings with { Device = unique } };
        }).ToArray();
        var active = profiles.Single(profile => profile.Id == preferences.ActiveRobotId);
        var bench = CameraSettings("bench");
        preferences = preferences with { Robots = profiles, Gripper = active.Gripper,
            Bench = bench with { Device = CameraDeviceChoices.ResolveSaved(bench.Device, "", inventory.Color ?? []) } };
        if (previousPreferences != JsonSerializer.Serialize(preferences, PluginProtocol.Json))
            await Save(cancellation);
        if (oldGripper != CameraSettings("gripper").Device || oldBench != CameraSettings("bench").Device)
        {
            if (oldGripper != CameraSettings("gripper").Device) await RestartCamera("gripper");
            if (oldBench != CameraSettings("bench").Device) await RestartCamera("bench");
        }
        string current = JsonSerializer.Serialize(cameraDeviceChoices, PluginProtocol.Json) + JsonSerializer.Serialize(depthDeviceChoices, PluginProtocol.Json);
        if (current != previous && workspacePage == "settings") await Render(cancellation);
    }

    private Widget CameraDeviceInput(string id, CameraPreferences settings) => cameraDeviceChoices.Length > 0
        ? new Widget("dropdown", "camera." + id + ".device") { Options = new[] { "Choose camera" }.Concat(cameraDeviceChoices.Select(choice => choice.Label)).ToArray(),
            Value = CameraDeviceChoices.Index(cameraDeviceChoices, settings.Device), Hint = "Selections use stable device identities. Duplicate camera names are separated by their USB connection." }
        : new Widget("input", "camera." + id + ".device") { Text = settings.Device, Hint = cameraDeviceDiscoveryStatus };

    private Widget DepthCameraInput() => depthDeviceChoices.Length > 0
        ? new Widget("dropdown", "camera.depth.serial") { Options = new[] { "Automatic · one D405 only" }.Concat(depthDeviceChoices.Select(choice => choice.Label)).ToArray(),
            Value = CameraDeviceChoices.Index(depthDeviceChoices, preferences.DepthSerial), Hint = "Choose this robot's D405. Its factory serial is retained across sessions." }
        : new Widget("input", "camera.depth.serial") { Text = preferences.DepthSerial, Hint = cameraDeviceDiscoveryStatus };

    private string ResolveCameraDeviceValue(string id, JsonElement value) => CameraDeviceChoices.Resolve(cameraDeviceChoices, value, CameraSettings(id).Device);
    private string ResolveDepthSerialValue(JsonElement value) => CameraDeviceChoices.Resolve(depthDeviceChoices, value, "");
}
