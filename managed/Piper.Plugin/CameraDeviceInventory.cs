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
    private readonly ThrottledProbe<CameraDeviceInventory> cameraDeviceProbe = new(TimeSpan.FromSeconds(15));
    private string cameraDeviceDiscoveryStatus = "Camera inventory is loading…";

    private void QueueCameraDeviceRefresh(CancellationToken cancellation, bool force = false)
    {
        if (!CameraDiscoveryRunning) return;
        cameraDeviceProbe.Queue(() => ReadCameraDevices(cancellation), force);
    }

    private async Task<CameraDeviceInventory?> ReadCameraDevices(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, cameraDiscoveryShutdown.Token);
        cancellation = lifetime.Token;
        try
        {
            var color = RunCameraTool(["devices"], TimeSpan.FromSeconds(8), cancellation);
            var depth = RunCameraTool(["d405-profiles"], TimeSpan.FromSeconds(8), cancellation);
            await Task.WhenAll(color, depth);
            if (color.Result.ExitCode != 0 && depth.Result.ExitCode != 0) throw new IOException(color.Result.Error + " " + depth.Result.Error);
            return new CameraDeviceInventory(
                color.Result.ExitCode == 0 ? JsonSerializer.Deserialize<ColorCameraDevice[]>(color.Result.Output, PluginProtocol.Json) ?? [] : null,
                depth.Result.ExitCode == 0 ? JsonSerializer.Deserialize<DepthCameraDevice[]>(depth.Result.Output, PluginProtocol.Json) ?? [] : null);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        { cameraDeviceDiscoveryStatus = "Camera discovery: " + error.Message; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        return null;
    }

    private async Task<bool> ApplyCameraDeviceInventory(CancellationToken cancellation)
    {
        var inventory = cameraDeviceProbe.Take();
        if (inventory is null) return false;
        cancellation.ThrowIfCancellationRequested();
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
        foreach (var (id, old) in new[] { ("gripper", oldGripper), ("bench", oldBench) })
        {
            if (old == CameraSettings(id).Device) continue;
            cancellation.ThrowIfCancellationRequested();
            await RestartCamera(id);
        }
        string current = JsonSerializer.Serialize(cameraDeviceChoices, PluginProtocol.Json) + JsonSerializer.Serialize(depthDeviceChoices, PluginProtocol.Json);
        return current != previous;
    }

}
