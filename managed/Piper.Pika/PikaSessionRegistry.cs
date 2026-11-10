using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>Independent per-device agents. Selecting a robot never changes,
/// closes or transfers another robot's gripper connection or power evidence.</summary>
public sealed class PikaSessionRegistry : IAsyncDisposable
{
    // Pika-C registry assignment. Per (device, distribution) identity:
    //   Unassigned ──configure or connect──▶ Registered (entry created; no transport opened)
    //   Registered ──selected through another spelling──▶ Aliased (alias bound to the canonical device)
    // The default pair is Unassigned while defaultDevice is "". Entries and alias
    // bindings are append-only for the backend's lifetime, by design: once bound,
    // an alias must never be redirected to another identical serial-number-free
    // adapter after USB reordering. MaximumSessions bounds that growth.
    private readonly record struct SessionKey(string Device, string? Distribution)
    {
        // WSL distribution names are case-insensitive; null is native Linux.
        public static SessionKey Of(string device, string? distribution) => new(device, distribution?.ToUpperInvariant());
    }

    private sealed record Entry(SessionKey Key, string Device, string? Distribution, IPikaSession Session);
    private const int MaximumSessions = 16;
    private readonly object gate = new();
    private readonly SemaphoreSlim admission = new(1);
    private readonly Dictionary<SessionKey, Entry> sessions = new();
    private readonly Dictionary<SessionKey, string> boundAliases = new();
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

    /// <summary>The current default gripper assignment (device "" when none).</summary>
    public (string Device, string? Distribution) DefaultAssignment
    {
        get { lock (gate) return (defaultDevice, defaultDistribution); }
    }

    // P4: the registry gate guards identities only. What it knows about one
    // identity is copied under the gate; the session is asked for its snapshot
    // after the gate is released. A snapshot that is slow, or never returns,
    // then delays only its own reader: routing, a heartbeat and the emergency
    // hold of every gripper never wait behind it.
    private readonly record struct Selected(string Device, string? Distribution, IPikaSession? Session, string[] Aliases);

    public JsonObject Snapshot(bool includeRawEvidence = false)
    {
        Selected selected;
        lock (gate) selected = Select(defaultDevice, defaultDistribution);
        return Sample(selected, includeRawEvidence);
    }

    public JsonArray Snapshots()
    {
        Selected[] all;
        lock (gate) all = sessions.Values.Select(Select).ToArray();
        return new JsonArray(all.Select(selected => (JsonNode)Sample(selected, raw: false)).ToArray());
    }

    /// <summary>The default gripper and the inventory from one sample of each session.</summary>
    internal (JsonObject Default, JsonArray Sessions) SnapshotPair()
    {
        Selected[] all; Selected selected;
        lock (gate)
        {
            all = sessions.Values.Select(Select).ToArray();
            selected = Select(defaultDevice, defaultDistribution);
        }
        JsonObject? defaultSample = null;
        var inventory = new JsonArray();
        foreach (var item in all)
        {
            var sample = Sample(item, raw: false);
            if (ReferenceEquals(item.Session, selected.Session)) defaultSample = sample.DeepClone().AsObject();
            inventory.Add(sample);
        }
        return (defaultSample ?? Sample(selected, raw: false), inventory);
    }

    /// <summary>The gripper leg of an emergency STOP: every session, whoever owns it.</summary>
    public async Task<JsonObject> HoldAllAsync(CancellationToken cancellation)
    {
        // Freeze identities before dispatch. Every session gets an independent
        // attempt, even when another USB device fails to reply.
        Entry[] entries; lock (gate) entries = sessions.Values.ToArray();
        var results = (await Task.WhenAll(entries.Select(entry => HoldAsync(entry, cancellation)))).OfType<JsonObject>().ToArray();
        bool ok = results.All(r => r["ok"]!.GetValue<bool>());
        string message = ok ? "All Pika holds acknowledged." : "Pika hold unverified: " +
            string.Join("; ", results.Where(result => result["ok"]?.GetValue<bool>() != true)
                .Select(result => result["device"]?.GetValue<string>() + " — " + result["message"]?.GetValue<string>()));
        return new() { ["ok"] = ok, ["message"] = message,
            ["sessions"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()) };
    }

    private static async Task<JsonObject?> HoldAsync(Entry entry, CancellationToken cancellation)
    {
        PikaHoldResult hold;
        try { hold = await entry.Session.HoldAsync(cancellation); }
        catch (Exception error) { hold = new(PikaHoldOutcome.Unverified, error.Message); }
        if (hold.Outcome == PikaHoldOutcome.Skipped) return null;
        return new() { ["device"] = entry.Device, ["ok"] = hold.Outcome == PikaHoldOutcome.Acknowledged, ["message"] = hold.Message };
    }

    public JsonObject Snapshot(string device, string? distribution, bool includeRawEvidence = false)
    {
        Selected selected;
        lock (gate) selected = Select(device, distribution);
        return Sample(selected, includeRawEvidence);
    }

    // Registry gate must be held. Resolves a bound alias to its canonical entry.
    private Entry? Find(string device, string? distribution)
    {
        string path = boundAliases.GetValueOrDefault(SessionKey.Of(device, distribution), device);
        return sessions.GetValueOrDefault(SessionKey.Of(path, distribution));
    }

    // Registry gate must be held.
    private Selected Select(string device, string? distribution) =>
        Find(device, distribution) is { } entry ? Select(entry) : new(device, distribution, null, []);

    // Registry gate must be held.
    private Selected Select(Entry entry) => new(entry.Device, entry.Distribution, entry.Session, boundAliases
        .Where(pair => pair.Value == entry.Device && pair.Key.Distribution == entry.Key.Distribution)
        .Select(pair => pair.Key.Device).ToArray());

    // Must not be called under the registry gate: this is where a session is read.
    private JsonObject Sample(Selected selected, bool raw)
    {
        var snapshot = selected.Session?.Snapshot(raw) ?? Unopened(selected.Device, selected.Distribution);
        snapshot["device"] = selected.Device; snapshot["distribution"] = selected.Distribution;
        snapshot["aliases"] = new JsonArray(selected.Aliases.Select(alias => (JsonNode)JsonValue.Create(alias)!).ToArray());
        return snapshot;
    }

    // An identity this backend holds no session for. The session fields are the
    // one definition at its never-opened defaults. This backend has enabled
    // nothing and has nothing pending for it; that is not proof of a disabled
    // motor, which always needs fresh feedback as well (PikaDisabledEvidence).
    private JsonObject Unopened(string device, string? distribution)
    {
        var snapshot = new JsonObject { ["enabled"] = false, ["pending"] = false };
        new PikaSessionFields(device, distribution, commandPeriodSeconds)
        {
            Message = device.Length == 0 ? PikaSessionFields.Unassigned : PikaSessionFields.Unopened
        }.WriteTo(snapshot);
        return snapshot;
    }

    /// <summary>Lists grippers for exactly this distribution (null is native
    /// Linux), annotated with each registered session's connection and owner.
    /// Never opens a serial port or registers a session.</summary>
    public async Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation)
    {
        var result = await discovery.DiscoverAsync(distribution, cancellation);
        if (result["devices"] is not JsonArray devices) return result;
        Dictionary<SessionKey, Entry> registered;
        lock (gate) registered = new(sessions);
        foreach (var item in devices.OfType<JsonObject>())
        {
            var status = registered.GetValueOrDefault(SessionKey.Of(item["device"]?.GetValue<string>() ?? "", item["distribution"]?.GetValue<string>()))?.Session.Status;
            item["connected"] = status?.Connected == true;
            item["owner"] = status?.Owner;
        }
        return result;
    }

    /// <summary>Routes one gripper action. Configuration is not accepted here:
    /// it must pass the arm-idle gate through <see cref="ConfigureAsync"/>.</summary>
    public Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation) =>
        action == "configure"
            ? Task.FromException<JsonObject>(new ArgumentException("Pika configuration must be committed through ConfigureAsync."))
            : ExecuteCoreAsync(action, input, cancellation, null);

    /// <summary>Changes a gripper assignment. <paramref name="authorizeCommit"/>
    /// runs the final registry commit under the caller's own gate (the Host
    /// commits only while every arm is idle); it is required.</summary>
    public Task<JsonObject> ConfigureAsync(JsonElement input, CancellationToken cancellation, Func<Func<JsonObject>, JsonObject> authorizeCommit)
    {
        ArgumentNullException.ThrowIfNull(authorizeCommit);
        return ExecuteCoreAsync("configure", input, cancellation, authorizeCommit);
    }

    private async Task<JsonObject> ExecuteCoreAsync(string action, JsonElement input, CancellationToken cancellation,
        Func<Func<JsonObject>, JsonObject>? authorizeCommit)
    {
        Debug.Assert((action == "configure") == (authorizeCommit is not null), "Configuration must carry its commit authorizer.");
        bool explicitDevice = input.TryGetProperty("pika_device", out var selector);
        if (explicitDevice && selector.ValueKind != JsonValueKind.String) throw new ArgumentException("pika_device must be a Linux device identity.");
        // Serialize only connection admission, not serial updates: two grippers
        // continue processing independently even if one agent's ACK is slow.
        bool needsAdmission = action is "connect" or "configure";
        if (needsAdmission) await admission.WaitAsync(cancellation);
        try
        {
            var assignment = DefaultAssignment;
            string device = explicitDevice ? selector.GetString()! : assignment.Device;
            // An explicit null distribution is native Linux; only an absent one means the default.
            string? distribution = ReadDistribution(input, "pika_distribution", assignment.Distribution);
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
            if (device.Length == 0 && action != "configure")
                throw new InvalidOperationException("No Pika is assigned to this robot. Choose a gripper in Settings.");
            string canonical = device.Length == 0 ? "" : await CanonicalizeAsync(device, distribution, cancellation);
            if (action == "configure")
                return authorizeCommit!(() => CommitConfiguration(device, canonical, distribution, explicitDevice, cancellation));
            Entry entry;
            lock (gate) entry = Route(action, device, canonical, distribution);
            var result = await entry.Session.ExecuteAsync(action, input, cancellation);
            result["device"] = canonical; result["distribution"] = distribution;
            return result;
        }
        finally { if (needsAdmission) admission.Release(); }
    }

    // Registry gate must be held. Only connect may register a new identity.
    private Entry Route(string action, string device, string canonical, string? distribution)
    {
        var entry = Session(canonical, distribution, create: action == "connect");
        if (action == "connect" && sessions.Values.Any(other => other.Device == canonical &&
            !ReferenceEquals(other, entry) && other.Session.Status.ConfigurationBlocked))
            throw new InvalidOperationException("This physical Pika already has a session in another WSL distribution. Disconnect that session first.");
        boundAliases.TryAdd(SessionKey.Of(device, distribution), canonical);
        return entry;
    }

    // Runs inside the caller's commit authorizer. An empty path clears the assignment.
    private JsonObject CommitConfiguration(string requested, string path, string? distribution, bool explicitDevice, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            bool clear = path.Length == 0;
            if (!explicitDevice)
            {
                if (Find(defaultDevice, defaultDistribution) is { } current && current.Session.Status.ConfigurationBlocked &&
                    (clear || current.Device != path || current.Distribution != distribution))
                    throw new InvalidOperationException(clear
                        ? "Verify disabled feedback and disconnect the assigned Pika before clearing its assignment."
                        : "Verify disabled feedback and disconnect the assigned Pika before changing its serial device.");
            }
            if (!clear)
            {
                _ = Session(path, distribution, create: true);
                boundAliases.TryAdd(SessionKey.Of(requested, distribution), path);
            }
            if (!explicitDevice) { defaultDevice = path; defaultDistribution = distribution; }
            return new JsonObject { ["ok"] = true, ["device"] = path, ["distribution"] = distribution,
                ["message"] = clear ? "No gripper assigned; no transport changed." :
                    "Gripper assignment registered independently; no serial port opened or motor command sent." };
        }
    }

    // Registry gate must be held. Construction reserves an identity and never
    // opens its transport.
    private Entry Session(string canonical, string? distribution, bool create)
    {
        var key = SessionKey.Of(canonical, distribution);
        if (sessions.TryGetValue(key, out var entry)) return entry;
        if (!create) throw new InvalidOperationException("Connect this robot's assigned Pika first.");
        if (sessions.Count >= MaximumSessions) throw new InvalidOperationException("Too many configured Pika devices; restart the idle backend to clear unused assignments.");
        entry = new(key, canonical, distribution, factory(canonical, distribution));
        sessions.Add(key, entry);
        return entry;
    }

    private async Task<string> CanonicalizeAsync(string device, string? distribution, CancellationToken cancellation)
    {
        var key = SessionKey.Of(device, distribution);
        lock (gate)
        {
            if (boundAliases.TryGetValue(key, out string? bound)) return bound;
            if (sessions.ContainsKey(key)) return device;
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
