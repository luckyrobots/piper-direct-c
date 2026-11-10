using System.Diagnostics;
using LuckyRobots.Piper.Processes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

public interface IPikaDeviceDiscovery
{
    Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation);
}

/// <summary>Inventory never opens serial ports, binds USB, attaches USB or powers a motor.</summary>
public sealed class PikaDeviceDiscovery(string agentPath) : IPikaDeviceDiscovery
{
    public async Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation)
    {
        PikaProcessService.ValidateConfiguration("", distribution);
        if (OperatingSystem.IsLinux() && distribution is null)
            return new JsonObject { ["ok"] = true, ["devices"] = new JsonArray(PikaSerialDevices.EnumerateLinux().Select(item => (JsonNode)Entry(item, null)).ToArray()) };
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("WSL gripper discovery is available on Windows only.");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        if (!File.Exists(executable)) throw new IOException("Install usbipd-win to share Pika grippers with WSL. Native Windows COM is disabled.");
        using var usb = JsonDocument.Parse(await RunAsync(executable, ["state"], cancellation));
        IReadOnlyList<PikaSerialDevice> linux = [];
        string? warning = null;
        try
        {
            string agent = agentPath.StartsWith('/') ? agentPath : await PikaProcessService.ResolveWslPath(agentPath, distribution!, cancellation);
            string text = await RunAsync("wsl.exe", ["--distribution", distribution!, "--exec", agent, "--list-devices"], cancellation);
            linux = JsonSerializer.Deserialize<PikaSerialDevice[]>(text) ?? [];
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or JsonException)
        { warning = "WSL inventory unavailable: " + error.Message; }
        return MergeWindows(usb.RootElement, linux, distribution!, warning);
    }

    public static JsonObject MergeWindows(JsonElement usbState, IReadOnlyList<PikaSerialDevice> linux, string distribution, string? warning = null)
    {
        var devices = new JsonArray();
        if (!usbState.TryGetProperty("Devices", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new IOException("usbipd-win returned an unsupported inventory.");
        foreach (var usb in entries.EnumerateArray())
        {
            string instance = Text(usb, "InstanceId");
            if (!instance.StartsWith(PikaProtocol.WindowsInstanceIdPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string bus = Text(usb, "BusId");
            var matching = linux.Where(item => item.HostBusId == bus && bus.Length > 0).ToArray();
            var found = matching.Length == 1 ? matching[0] : null;
            bool shared = usb.TryGetProperty("IsForced", out var forced) && forced.ValueKind == JsonValueKind.True;
            bool attached = Text(usb, "ClientIPAddress").Length > 0;
            string description = Text(usb, "Description");
            var entry = found is null ? new JsonObject { ["device"] = bus.Length == 0 ? "" : PikaSerialDevices.HostPrefix + bus,
                ["aliases"] = new JsonArray() } : Entry(found, distribution);
            entry["identity"] = instance;
            entry["name"] = description.Length > 0 ? description : "Pika gripper · USB " + bus;
            entry["distribution"] = distribution;
            entry["bus_id"] = bus;
            entry["windows_instance_id"] = instance;
            entry["shared"] = shared;
            entry["attached"] = attached;
            entry["available"] = found is not null;
            entry["message"] = found is not null ? "Ready; no serial port opened." : !shared
                ? "USB setup required once: share this gripper with WSL as administrator." : !attached
                ? "Shared; attach this gripper to WSL." : warning ?? "Attached, but no unique Linux serial device was found; refresh after attachment.";
            devices.Add(entry);
        }
        return new JsonObject { ["ok"] = true, ["devices"] = devices, ["warning"] = warning };
    }

    private static string Text(JsonElement source, string key) => source.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static JsonObject Entry(PikaSerialDevice item, string? distribution) => new()
    {
        ["identity"] = item.Device, ["name"] = "Pika gripper · " + (item.HostBusId ?? Path.GetFileName(item.SystemDevice)),
        ["device"] = item.Device, ["system_device"] = item.SystemDevice, ["distribution"] = distribution,
        ["bus_id"] = item.HostBusId, ["available"] = true, ["attached"] = true,
        ["aliases"] = new JsonArray(item.Aliases.Select(alias => (JsonNode)JsonValue.Create(alias)!).ToArray()),
        ["message"] = "Ready; no serial port opened."
    };

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken cancellation)
    {
        var result = await BoundedProcess.RunAsync(executable, arguments, "Could not read Pika USB inventory.", "Pika USB inventory timed out.", cancellation);
        if (result.ExitCode != 0) throw new IOException(result.Error.Trim().Length > 0 ? result.Error.Trim() : "Pika inventory process failed.");
        if (result.Output.Length > 1024 * 1024) throw new IOException("Pika inventory exceeds its size limit.");
        return result.Output;
    }
}
