using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>Independent per-device agents. Selecting a robot never changes,
/// closes or transfers another robot's gripper connection or power evidence.</summary>
public sealed class PikaSessionRegistry : IPikaSession
{
    private sealed record Entry(string Device, string? Distribution, IPikaSession Session);
    private readonly object gate = new();
    private readonly SemaphoreSlim admission = new(1);
    private readonly Dictionary<string, Entry> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> boundAliases = new(StringComparer.Ordinal);
    private readonly IPikaDeviceDiscovery discovery;
    private readonly Func<string, string?, IPikaSession> factory;
    private string defaultDevice;
    private string? defaultDistribution;
    private readonly double commandPeriodSeconds;

    public PikaSessionRegistry(string device, string agentPath, string? distribution = null,
        double commandPeriodSeconds = PikaFollowSession.MinimumCommandPeriodSeconds,
        IPikaDeviceDiscovery? discovery = null, Func<string, string?, IPikaSession>? factory = null)
    {
        _ = new PikaFollowSession(_ => { }, commandPeriodSeconds);
        this.defaultDevice = device; this.defaultDistribution = distribution; this.commandPeriodSeconds = commandPeriodSeconds;
        this.discovery = discovery ?? new PikaDeviceDiscovery(agentPath);
        this.factory = factory ?? ((path, distro) => new PikaProcessService(path, agentPath, distro, commandPeriodSeconds));
    }

    private static string Key(string device, string? distribution) => (distribution?.ToUpperInvariant() ?? "") + "\n" + device;

    public JsonObject Snapshot(bool includeRawEvidence = false)
    {
        lock (gate) return Snapshot(defaultDevice, defaultDistribution, includeRawEvidence);
    }

    public JsonArray Snapshots(bool includeRawEvidence = false)
    {
        lock (gate) return new JsonArray(sessions.Values.Select(entry => (JsonNode)Decorate(entry, includeRawEvidence)).ToArray());
    }

    public async Task<JsonObject> HoldOwnedAsync(string owner, CancellationToken cancellation)
    {
        if (!Guid.TryParse(owner, out _)) throw new ArgumentException("A dashboard Pika owner ID is required");
        // Freeze identities before dispatch. Every owned session gets an
        // independent attempt, even when another USB device fails to reply.
        var owned = Snapshots().OfType<JsonObject>().Where(p => p["owner"]?.GetValue<string>() == owner).ToArray();
        var results = await Task.WhenAll(owned.Select(async p => {
            string device = p["device"]!.GetValue<string>();
            try
            {
                using var input = JsonDocument.Parse(new JsonObject { ["owner"] = owner, ["pika_device"] = device,
                    ["pika_distribution"] = p["distribution"]?.DeepClone() }.ToJsonString());
                var result = await ExecuteAsync("hold", input.RootElement, cancellation);
                return new JsonObject { ["device"] = device, ["ok"] = result["ok"]?.GetValue<bool>() == true,
                    ["message"] = result["message"]?.DeepClone() };
            }
            catch (Exception error) { return new JsonObject { ["device"] = device, ["ok"] = false, ["message"] = error.Message }; }
        }));
        bool ok = results.All(r => r["ok"]!.GetValue<bool>());
        return new() { ["ok"] = ok, ["message"] = ok ? "All owned Pika holds acknowledged." : "One or more Pika holds could not be verified.",
            ["sessions"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()) };
    }

    public JsonObject Snapshot(string device, string? distribution, bool includeRawEvidence = false)
    {
        lock (gate)
        {
            string path = boundAliases.GetValueOrDefault(Key(device, distribution), device);
            return sessions.TryGetValue(Key(path, distribution), out var entry)
                ? Decorate(entry, includeRawEvidence) : Empty(device, distribution);
        }
    }

    public async Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation)
    {
        var result = await discovery.DiscoverAsync(distribution ?? defaultDistribution, cancellation);
        lock (gate)
            if (result["devices"] is JsonArray devices)
                foreach (var item in devices.OfType<JsonObject>())
                {
                    string path = item["device"]?.GetValue<string>() ?? "";
                    string? distro = item["distribution"]?.GetValue<string>();
                    var entry = sessions.GetValueOrDefault(Key(path, distro));
                    var snapshot = entry?.Session.Snapshot();
                    item["connected"] = snapshot?["connected"]?.GetValue<bool>() == true;
                    item["owner"] = snapshot?["owner"]?.DeepClone();
                }
        return result;
    }

    public async Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
    {
        bool explicitDevice = input.TryGetProperty("pika_device", out var selector);
        if (explicitDevice && selector.ValueKind != JsonValueKind.String) throw new ArgumentException("pika_device must be a Linux device identity.");
        string device = explicitDevice ? selector.GetString()! : defaultDevice;
        string? distribution = ReadDistribution(input, "pika_distribution", defaultDistribution);
        if (action == "devices") return await DiscoverAsync(distribution, cancellation);
        if (!input.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String || !Guid.TryParse(owner.GetString(), out _))
            throw new ArgumentException("A dashboard Pika owner ID is required");
        if (action == "configure")
        {
            if (!input.TryGetProperty("device", out var configured) || configured.ValueKind != JsonValueKind.String)
                throw new ArgumentException("device must be a Linux device identity.");
            device = configured.GetString()!;
            distribution = ReadDistribution(input, "distribution", distribution);
        }
        PikaProcessService.ValidateConfiguration(device, distribution);
        if (device.Length == 0)
        {
            if (action != "configure") throw new InvalidOperationException("No Pika is assigned to this robot. Choose a gripper in Settings.");
            if (!explicitDevice)
            {
                if (Snapshot()["configuration_blocked"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException("Verify disabled feedback and disconnect the assigned Pika before clearing its assignment.");
                lock (gate) { defaultDevice = device; defaultDistribution = distribution; }
            }
            return new JsonObject { ["ok"] = true, ["device"] = device, ["distribution"] = distribution, ["message"] = "No gripper assigned; no transport changed." };
        }
        // Serialize only connection admission, not serial updates: two grippers
        // continue processing independently even if one agent's ACK is slow.
        bool needsAdmission = action is "connect" or "configure";
        if (needsAdmission) await admission.WaitAsync(cancellation);
        try
        {
            string canonical = await CanonicalizeAsync(device, distribution, cancellation);
            if (!explicitDevice && action == "configure")
            {
                var current = Snapshot();
                if (current["configuration_blocked"]?.GetValue<bool>() == true &&
                    (current["device"]?.GetValue<string>() != canonical || current["distribution"]?.GetValue<string>() != distribution))
                    throw new InvalidOperationException("Verify disabled feedback and disconnect the assigned Pika before changing its serial device.");
            }
            Entry entry;
            lock (gate)
            {
                if (!sessions.TryGetValue(Key(canonical, distribution), out entry!))
                {
                    if (!needsAdmission) throw new InvalidOperationException("Connect this robot's assigned Pika first.");
                    if (sessions.Count >= 16) throw new InvalidOperationException("Too many configured Pika devices; restart the idle backend to clear unused assignments.");
                    entry = new(canonical, distribution, factory(canonical, distribution));
                    sessions.Add(Key(canonical, distribution), entry);
                }
                if (action == "connect" && sessions.Values.Any(other => other.Device == canonical &&
                    !ReferenceEquals(other, entry) && other.Session.Snapshot()["configuration_blocked"]?.GetValue<bool>() == true))
                    throw new InvalidOperationException("This physical Pika already has a session in another WSL distribution. Disconnect that session first.");
                boundAliases.TryAdd(Key(device, distribution), canonical);
                if (!explicitDevice && action == "configure") { defaultDevice = canonical; defaultDistribution = distribution; }
            }
            if (action == "configure")
                return new JsonObject { ["ok"] = true, ["device"] = canonical, ["distribution"] = distribution,
                    ["message"] = "Gripper assignment registered independently; no serial port opened or motor command sent." };
            var result = await entry.Session.ExecuteAsync(action, input, cancellation);
            result["device"] = canonical; result["distribution"] = distribution;
            return result;
        }
        finally { if (needsAdmission) admission.Release(); }
    }

    private async Task<string> CanonicalizeAsync(string device, string? distribution, CancellationToken cancellation)
    {
        lock (gate)
        {
            if (boundAliases.TryGetValue(Key(device, distribution), out string? bound)) return bound;
            if (sessions.ContainsKey(Key(device, distribution))) return device;
        }
        if (device.StartsWith(PikaSerialDevices.HostPrefix, StringComparison.Ordinal))
        {
            string[] bus = device[PikaSerialDevices.HostPrefix.Length..].Split('-');
            if (bus.Length != 2 || !ushort.TryParse(bus[0], out ushort host) || host == 0 || !ushort.TryParse(bus[1], out ushort port) || port == 0)
                throw new ArgumentException("Invalid Pika host USB identity.");
            return device;
        }
        var inventory = await discovery.DiscoverAsync(distribution, cancellation);
        var devices = (inventory["devices"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        // These devices do not expose unique serial numbers. With several
        // attached, a by-id link silently changes its target during attach.
        if (devices.Length > 1 && device.Contains("/by-id/", StringComparison.Ordinal))
            throw new InvalidOperationException("Several identical Pika grippers are connected. Select this robot's specific USB gripper in Settings; the shared by-id name is ambiguous.");
        var matching = devices.Where(item => item["device"]?.GetValue<string>() == device ||
            item["aliases"] is JsonArray aliases && aliases.Any(alias => alias?.GetValue<string>() == device)).ToArray();
        if (matching.Length != 1 || matching[0]["available"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The assigned Pika is unavailable or ambiguous. Refresh gripper devices in Settings; another gripper will not be substituted.");
        return matching[0]["device"]!.GetValue<string>();
    }

    private static string? ReadDistribution(JsonElement input, string key, string? fallback)
    {
        if (!input.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(key + " must be a distribution name or null.");
        return value.GetString();
    }

    private JsonObject Decorate(Entry entry, bool raw)
    {
        var result = entry.Session.Snapshot(raw);
        result["device"] = entry.Device; result["distribution"] = entry.Distribution;
        result["aliases"] = new JsonArray(boundAliases.Where(pair => pair.Value == entry.Device &&
            pair.Key.StartsWith((entry.Distribution?.ToUpperInvariant() ?? "") + "\n", StringComparison.Ordinal))
            .Select(pair => (JsonNode)JsonValue.Create(pair.Key[(pair.Key.IndexOf('\n') + 1)..])!).ToArray());
        return result;
    }

    private JsonObject Empty(string device, string? distribution) => new()
    {
        ["configured"] = device.Length > 0, ["device"] = device, ["distribution"] = distribution,
        ["connected"] = false, ["fresh"] = false, ["owner"] = null, ["enabled"] = false, ["pending"] = false,
        ["power_requested"] = false, ["power_uncertain"] = false, ["configuration_blocked"] = false,
        ["transport"] = distribution is null ? "linux-serial" : "wsl-linux-serial",
        ["age_s"] = null, ["message"] = device.Length == 0 ? "No Pika assigned." : "Connect to read this gripper's telemetry.",
        ["follow_minimum_command_period_s"] = commandPeriodSeconds, ["follow_maximum_command_hz"] = 1 / commandPeriodSeconds,
        ["physical_replay_available"] = false, ["measured_hold_resume_supported"] = true
    };

    public async ValueTask DisposeAsync()
    {
        await admission.WaitAsync();
        try
        {
            Entry[] entries; lock (gate) entries = sessions.Values.ToArray();
            // Each agent independently disables only power it requested.
            await Task.WhenAll(entries.Select(async entry => await entry.Session.DisposeAsync()));
        }
        finally { admission.Release(); admission.Dispose(); }
    }
}
