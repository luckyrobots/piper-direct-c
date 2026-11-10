using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

/// <summary>The session half of a gripper snapshot has one definition, and a
/// snapshot that never returns delays only its own reader (P4).</summary>
internal static class PikaSnapshotTests
{
    // The platform's own distribution: a WSL name on Windows, native Linux (null) elsewhere.
    private static readonly string? Distro = OperatingSystem.IsWindows() ? "Ubuntu" : null;

    // Every field PikaSessionFields writes. A field added there must be added here.
    private static readonly string[] SessionFields =
    [
        "device", "distribution", "follow_minimum_command_period_s", "configured", "transport", "follow_maximum_command_hz",
        "verified_maximum_opening_speed_mm_s", "physical_replay_available", "measured_hold_resume_supported", "owner",
        "power_requested", "power_uncertain", "post_request_feedback_verified", "configuration_blocked", "agent_phase",
        "connected", "read_only_adoptable", "age_s", "fresh", "message"
    ];

    public static async Task Run()
    {
        await VerifyOneSessionSchema();
        await VerifyStalledSnapshot();
        Console.WriteLine("Pika snapshot tests passed: one session-field definition for a live session and the registry placeholder, and a stalled snapshot that blocks no hold, heartbeat or other reader.");
    }

    private static async Task VerifyOneSessionSchema()
    {
        const string device = "/dev/pika/by-host/11-4";
        string owner = Guid.NewGuid().ToString();
        // The production factory: a real PikaProcessService that is registered and never opened.
        await using var registry = new PikaSessionRegistry(device, "never-launched", Distro);
        var placeholder = registry.Snapshot();
        await registry.ConfigureAsync(JsonSerializer.SerializeToElement(new { owner, device }), default, commit => commit());
        var registered = registry.Snapshot();
        Check(Keys(registered).SetEquals([.. SessionFields, "aliases"]),
            "A never-opened session's snapshot is not exactly the session fields: " + string.Join(",", Keys(registered).Order()));
        Check(Keys(placeholder).SetEquals([.. SessionFields, "aliases", "enabled", "pending"]),
            "The registry placeholder drifted from the session fields: " + string.Join(",", Keys(placeholder).Order()));
        Check(SessionFields.Where(field => field != "message").All(field => JsonNode.DeepEquals(placeholder[field], registered[field])) &&
            registered["agent_phase"]!.GetValue<string>() == "idle" && registered["configured"]!.GetValue<bool>() &&
            !registered["connected"]!.GetValue<bool>() && registered["owner"] is null && registered["age_s"] is null &&
            placeholder["message"]!.GetValue<string>().StartsWith("Connect", StringComparison.Ordinal),
            "The placeholder for an identity without a session disagrees with a session that was never opened.");
        await using var unassigned = new PikaSessionRegistry("", "never-launched");
        var none = unassigned.Snapshot();
        Check(Keys(none).SetEquals(Keys(placeholder)) && !none["configured"]!.GetValue<bool>() && !none["connected"]!.GetValue<bool>() &&
            !none["power_uncertain"]!.GetValue<bool>() && none["message"]!.GetValue<string>() == "No Pika assigned.",
            "The unassigned default gripper does not use the session fields.");

        // The agent's line is passed through, but it cannot speak for the session.
        await using var service = new PikaProcessService(device, "never-launched", Distro);
        service.SetEvidenceForTest(PikaPowerEvidence.None, owner, "", """
            {"type":"state","fresh":true,"age_s":0,"enabled":false,"armed":false,"owner":"agent","connected":true,"agent_phase":"ready",
             "power_requested":true,"power_uncertain":true,"configuration_blocked":true,"read_only_adoptable":true,"device":"/dev/other"}
            """);
        var spoken = service.Snapshot();
        Check(spoken["owner"] is null && !spoken["connected"]!.GetValue<bool>() && spoken["agent_phase"]!.GetValue<string>() == "idle" &&
            !spoken["power_requested"]!.GetValue<bool>() && !spoken["power_uncertain"]!.GetValue<bool>() &&
            !spoken["configuration_blocked"]!.GetValue<bool>() && !spoken["read_only_adoptable"]!.GetValue<bool>() &&
            !spoken["fresh"]!.GetValue<bool>() && spoken["device"]!.GetValue<string>() == device &&
            spoken["enabled"]!.GetValue<bool>() == false && spoken["armed"]!.GetValue<bool>() == false,
            "An agent state line overrode a session-owned field, or its own fields were not passed through.");

        static HashSet<string> Keys(JsonObject snapshot) => snapshot.Select(field => field.Key).ToHashSet();
    }

    // Fault injection: one gripper's snapshot is entered and never returns while
    // two real fixture agents are enabled. Everything that keeps a gripper
    // supervised must still complete: the emergency hold of every session, each
    // heartbeat, routing, the inventory and another reader of another gripper.
    private static async Task VerifyStalledSnapshot()
    {
        const string a = "/dev/pika-fixture", b = "/dev/pika-second-fixture";
        string ownerA = Guid.NewGuid().ToString(), ownerB = Guid.NewGuid().ToString();
        var sessions = new Dictionary<string, StallingSession>();
        await using var registry = new PikaSessionRegistry(a, "never-launched", Distro, discovery: new Inventory(a, b),
            factory: (device, _) => sessions[device] = new(new PikaProcessService(device, Environment.ProcessPath!, directLaunch: true)));
        foreach (var (device, owner) in new[] { (a, ownerA), (b, ownerB) })
        {
            await registry.ExecuteAsync("connect", Input(device, owner), default);
            await registry.ExecuteAsync("enable", Input(device, owner), default);
        }
        Check(registry.Snapshots().All(pika => pika!["enabled"]!.GetValue<bool>() && pika["fresh"]!.GetValue<bool>()),
            "The fixture grippers were not enabled.");

        var stalled = sessions[a];
        stalled.Open.Reset();
        var reader = Task.Run(() => registry.Snapshots());
        try
        {
            // Each call runs on its own task: one that waits behind the stalled
            // snapshot fails its check at the liveness limit instead of hanging the runner.
            await Eventually(() => stalled.Readers == 1, () => "The stalled snapshot was never entered.");
            var hold = Task.Run(() => registry.HoldAllAsync(default));
            await Eventually(() => hold.IsCompleted, () => "A stalled snapshot blocked the emergency hold of every gripper.");
            var held = await hold;
            Check(held["ok"]!.GetValue<bool>() && held["sessions"]!.AsArray().Count == 2,
                "The emergency hold did not reach both grippers while a snapshot was stalled: " + held.ToJsonString());
            foreach (var (device, owner) in new[] { (a, ownerA), (b, ownerB) })
            {
                var heartbeat = Task.Run(() => registry.ExecuteAsync("heartbeat", Input(device, owner), default));
                await Eventually(() => heartbeat.IsCompleted, () => "A stalled snapshot blocked a heartbeat of " + device);
                Check((await heartbeat)["ok"]!.GetValue<bool>(), "A heartbeat was refused while a snapshot was stalled.");
            }
            var other = Task.Run(() => registry.Snapshot(b, Distro));
            await Eventually(() => other.IsCompleted, () => "A stalled snapshot of one gripper blocked a reader of another.");
            var peer = await other;
            Check(peer["connected"]!.GetValue<bool>() && peer["enabled"]!.GetValue<bool>() && peer["owner"]!.GetValue<string>() == ownerB &&
                peer["power_requested"]!.GetValue<bool>(), "The other gripper's snapshot lost its state while one was stalled.");
            var inventory = Task.Run(() => registry.DiscoverAsync(Distro, default));
            await Eventually(() => inventory.IsCompleted, () => "A stalled snapshot blocked the gripper inventory.");
            Check((await inventory)["devices"]![0]!["owner"]!.GetValue<string>() == ownerA && stalled.Status.Connected,
                "The inventory or the routing status of the stalled gripper was wrong.");
            Check(!reader.IsCompleted && stalled.Readers == 1, "The fixture did not keep its snapshot stalled.");
        }
        finally { stalled.Open.Set(); }
        var released = await reader;
        Check(released.Count == 2 && released.All(pika => pika!["connected"]!.GetValue<bool>() && pika["enabled"]!.GetValue<bool>()),
            "The released reader did not receive both sessions.");
        foreach (var (device, owner) in new[] { (a, ownerA), (b, ownerB) })
        {
            await registry.ExecuteAsync("disable", Input(device, owner), default);
            await registry.ExecuteAsync("disconnect", Input(device, owner), default);
        }
        Check(registry.Snapshots().All(pika => !pika!["connected"]!.GetValue<bool>() && !pika["power_uncertain"]!.GetValue<bool>()),
            "A gripper that was disabled and disconnected after the stall kept unresolved power.");
    }

    private static JsonElement Input(string device, string owner) =>
        JsonSerializer.SerializeToElement(new { owner, pika_device = device, pika_distribution = Distro, clear = true });

    /// <summary>A real session whose snapshot can be made to wait forever.</summary>
    private sealed class StallingSession(IPikaSession inner) : IPikaSession
    {
        private int readers;
        public readonly ManualResetEventSlim Open = new(true);
        public int Readers => Volatile.Read(ref readers);

        public JsonObject Snapshot(bool includeRawEvidence = false)
        {
            Interlocked.Increment(ref readers);
            try { Open.Wait(); return inner.Snapshot(includeRawEvidence); }
            finally { Interlocked.Decrement(ref readers); }
        }

        public PikaSessionStatus Status => inner.Status;
        public Task<PikaHoldResult> HoldAsync(CancellationToken cancellation) => inner.HoldAsync(cancellation);
        public Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation) =>
            inner.ExecuteAsync(action, input, cancellation);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Inventory(params string[] devices) : IPikaDeviceDiscovery
    {
        public Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation) =>
            Task.FromResult(new JsonObject
            {
                ["devices"] = new JsonArray(devices.Select(device => (JsonNode)new JsonObject
                {
                    ["device"] = device, ["distribution"] = distribution, ["aliases"] = new JsonArray(), ["available"] = true
                }).ToArray())
            });
    }
}
