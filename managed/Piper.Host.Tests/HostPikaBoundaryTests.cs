using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Host;
using LuckyRobots.Piper.Pika;

internal static class HostPikaBoundaryTests
{
    internal static async Task Run()
    {
        const string first = "/dev/pika/by-host/11-4", peer = "/dev/pika/by-host/12-4", replacement = "/dev/pika/by-host/13-4";
        const string alias = "/dev/ttyUSB5", arm = "simulation:one";
        // The platform's own distribution: a WSL name on Windows, native Linux (null) elsewhere.
        string? distribution = OperatingSystem.IsWindows() ? "Ubuntu" : null;
        string owner = Guid.NewGuid().ToString();
        var directory = Directory.CreateTempSubdirectory("piper-host-pika-boundaries-");
        try
        {
            using var controller = new ControllerService(simulated: true, directory.FullName, simulatedRobotCount: 2);
            var clock = new FixtureClock();
            var discovery = new PausedDiscovery(alias, replacement);
            var agents = new Dictionary<string, FakeSession>();
            await using var registry = new PikaSessionRegistry(first, "never-launched", distribution, discovery: discovery,
                factory: (path, _) => agents[path] = new(path == first ? () => clock.Utc += TimeSpan.FromMilliseconds(80) : null));
            var maintenance = new BackendMaintenance(ArmState, () => registry.Snapshots(),
                () => throw new InvalidOperationException("Fixture cannot restart a backend."), Environment.ProcessId);
            // Native is always simulated. Permit the dispatcher to exercise
            // its production Pika routes against the injected in-memory fakes.
            var context = new PiperRpcContext(controller, registry, false, owner, maintenance) { Clock = clock };
            var initial = ArmState();
            Check(initial["simulated"]?.GetValue<bool>() == true, "Host/Pika boundary fixture is not simulated.");
            await registry.ConfigureAsync(JsonSerializer.SerializeToElement(new { owner, device = first }), default, commit => commit());
            await registry.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner, pika_device = peer }), default);

            DateTimeOffset beforeSample = clock.Utc;
            var frame = context.Snapshot();
            var state = JsonNode.Parse(frame.Json)!;
            Check(clock.Utc == beforeSample.AddMilliseconds(80) && agents[first].Snapshots == 1 && agents[peer].Snapshots == 1,
                "Frame assembly sampled the same physical gripper twice.");
            var defaultEntry = state["pikas"]!.AsArray().Single(item => item?["device"]?.GetValue<string>() == first);
            Check(JsonNode.DeepEquals(state["pika"], defaultEntry) &&
                frame.TimestampUnixMicroseconds == (beforeSample.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10 &&
                frame.ClockDomain == "host-utc", "Frame composition disagreed about default Pika or discarded source aging.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var armDevice = initial["devices"]!.AsArray().Single(device => device?["id"]?.GetValue<string>() == arm)!;
            long generation = armDevice["generation"]!.GetValue<long>();
            Check((await Send("/api/query", new { owner, device_id = arm, generation })).Status == 202, "Simulator query did not start.");
            await Poll(state => state["any_query_active"]?.GetValue<bool>() == false &&
                state["devices"]![0]?["firmware_supported"]?.GetValue<bool>() == true &&
                state["devices"]![0]!["status"]?["pose_stationary_s"]?.GetValue<double>() >= .4);
            var enable = await Send("/api/command", new { owner, device_id = arm, generation, action = "enable",
                clear = true, supported = true, standalone = true });
            Check(enable.Status == 202, "Simulator enable did not start: " + enable.RawJson);
            long request = JsonNode.Parse(enable.RawJson!)!["request_id"]!.GetValue<long>();
            await Poll(state => state["any_motion_active"]?.GetValue<bool>() == false &&
                state["devices"]![0]!["joints"]!.AsArray().All(joint => joint?["enabled"]?.GetValue<bool>() == true) &&
                state["devices"]![0]!["status"]?["pose_stationary_s"]?.GetValue<double>() >= .4, request);

            var configuring = Send("/api/pika/configure", new { owner, device = alias });
            await discovery.Entered.Task.WaitAsync(deadline.Token);
            try
            {
                Check((await Send("/api/pika/position", new { owner, pika_device = peer, value = .5 }).WaitAsync(deadline.Token)).Status == 200 &&
                    agents[peer].Actions.Contains("position"), "Pika discovery blocked another gripper's command.");
                Check((await Send("/api/stop", new { owner, device_id = "simulation:2" }).WaitAsync(deadline.Token)).Status == 200,
                    "Pika discovery blocked STOP.");
                var move = await Send("/api/command", new { owner, device_id = arm, generation, action = "joint",
                    joint = 6, degrees = 30, speed_percent = 1, tolerance_deg = .2, clear = true, standalone = true });
                Check(move.Status == 202 && ControllerActivity.AnyMotion(ArmState()), "Simulator motion did not begin during discovery: " + move.RawJson);
            }
            finally { discovery.Release.TrySetResult(); }
            var rejected = await configuring.WaitAsync(deadline.Token);
            Check(rejected.Status == 409 && rejected.Error == "Finish active arm movements before changing Pika assignment" &&
                registry.Snapshot()["device"]?.GetValue<string>() == first && !agents.ContainsKey(replacement),
                "Pika configuration committed after an arm started moving.");
            var stopped = await Send(HostRoutes.CoordinatedStop, new { owner });
            Check(stopped.Status == 200 && stopped.Payload is CoordinatedStopResult { Ok: true } &&
                agents[peer].Actions.Count(action => action == "hold") == 1 && !agents[first].Actions.Contains("hold"),
                "Coordinated STOP after denied configuration did not hold only the owned gripper.");

            var accepted = await Send("/api/pika/configure", new { owner, device = alias });
            Check(accepted.Status == 200 && registry.Snapshot()["device"]?.GetValue<string>() == replacement &&
                agents[replacement].Actions.Count == 0, "Idle configuration did not register without opening or writing to the gripper.");
            var moved = await Send("/api/pika/configure", new { owner, device = replacement, distribution = "Debian" });
            if (OperatingSystem.IsWindows())
                Check(moved.Status == 200 && context.Control.PikaState(replacement, null)["distribution"]?.GetValue<string>() == "Debian" &&
                    context.Control.PikaState(replacement, distribution)["distribution"]?.GetValue<string>() == distribution,
                    "HTTP Pika state ignored the current registry distribution after configure.");
            else
                Check(moved.Status == 409 && moved.Error?.Contains("only on Windows", StringComparison.Ordinal) == true &&
                    registry.DefaultAssignment == (replacement, null) && context.Control.PikaState(replacement, null)["distribution"] is null,
                    "A WSL distribution was configured on a non-Windows host: " + moved.Error);
            _ = await context.Control.DiscoverPikaAsync(null, deadline.Token);
            Check(discovery.Distributions[^1] == (OperatingSystem.IsWindows() ? "Debian" : null),
                "HTTP Pika inventory did not use the current registry distribution after configure.");
            Console.WriteLine("Host Pika boundaries passed: same-sample snapshot, source clock, idle commit, and independent STOP/peer writes.");

            JsonObject ArmState() => controller.SnapshotJson();

            async Task<HostControlResult> Send(string route, object body)
            {
                var input = JsonSerializer.SerializeToElement(body);
                using var admission = context.Control.Admit(route, input);
                return admission.Reply ?? await context.Control.ExecuteAsync(route, input.GetRawText(), input, deadline.Token);
            }

            async Task Poll(Func<JsonObject, bool> ready, long? requestId = null)
            {
                while (true)
                {
                    var latest = ArmState();
                    if (ready(latest)) return;
                    if (requestId is { } id && ControllerActivity.AnyMotion(latest))
                    {
                        var heartbeat = await Send("/api/heartbeat", new { owner, device_id = arm, generation, request_id = id });
                        if (heartbeat.Status != 200)
                        {
                            // The simulator can finish Enable between the snapshot and the renewal.
                            var motion = ArmState()["devices"]![0]?["motion"];
                            Check(heartbeat.Status == 409 && heartbeat.RawJson is { } reply &&
                                JsonNode.Parse(reply)?["error"]?.GetValue<string>() == "No matching active control lease." &&
                                motion?["request_id"]?.GetValue<long>() == id && motion["active"]?.GetValue<bool>() == false &&
                                motion["phase"]?.GetValue<string>() == "complete",
                                $"Simulator heartbeat rejected ({heartbeat.Status}): {heartbeat.RawJson ?? heartbeat.Error}");
                        }
                    }
                    await Task.Delay(10, deadline.Token);
                }
            }
        }
        finally { directory.Delete(recursive: true); }
        await ControllerFailureIsConflict();
        await FaultedLoopStillStops();
    }

    // P4: a faulted tick loop refuses ordinary actions but still attempts STOP,
    // alone and as the arm leg of a coordinated STOP.
    private static async Task FaultedLoopStillStops()
    {
        var directory = Directory.CreateTempSubdirectory("piper-host-faulted-");
        try
        {
            using var controller = new ControllerService(simulated: true, directory.FullName);
            await using var registry = new PikaSessionRegistry("", "never-launched");
            var maintenance = new BackendMaintenance(() => new JsonObject(), () => [], () => { }, Environment.ProcessId);
            var context = new PiperRpcContext(controller, registry, false, "", maintenance);
            controller.FaultLoopForTest("fixture tick fault");
            var input = JsonSerializer.SerializeToElement(new { });
            var refused = await context.Control.ExecuteAsync("/api/reconnect", input.GetRawText(), input, default);
            Check(refused.Status == 409 && refused.Error?.Contains("fixture tick fault") == true,
                "A faulted controller loop admitted an ordinary action.");
            var stopped = await context.Control.ExecuteAsync("/api/stop", input.GetRawText(), input, default);
            Check(stopped.Status == 200 && JsonNode.Parse(stopped.ToJson())?["ok"]?.GetValue<bool>() != false,
                "A faulted controller loop refused STOP instead of attempting it: " + stopped.ToJson());
            var owner = JsonSerializer.SerializeToElement(new { owner = Guid.NewGuid().ToString() });
            var coordinated = await context.Control.ExecuteAsync("/api/stop/coordinated", owner.GetRawText(), owner, default);
            Check(JsonNode.Parse(coordinated.ToJson())?["arm"]?["ok"]?.GetValue<bool>() == true,
                "A faulted controller loop failed the arm leg of a coordinated STOP: " + coordinated.ToJson());
        }
        finally { directory.Delete(recursive: true); }
    }

    // B23: a controller-operation exception maps to 409 in the dispatcher,
    // which HTTP and gRPC share; HTTP previously reported it as 500.
    private static async Task ControllerFailureIsConflict()
    {
        var directory = Directory.CreateTempSubdirectory("piper-host-disposed-");
        try
        {
            var controller = new ControllerService(simulated: true, directory.FullName);
            await using var registry = new PikaSessionRegistry("", "never-launched");
            var maintenance = new BackendMaintenance(() => new JsonObject(), () => [], () => { }, Environment.ProcessId);
            var context = new PiperRpcContext(controller, registry, false, "", maintenance);
            controller.Dispose();
            controller.Dispose(); // idempotent
            var input = JsonSerializer.SerializeToElement(new { action = "hold" });
            var result = await context.Control.ExecuteAsync("/api/command", input.GetRawText(), input, default);
            Check(result.Status == 409 && result.Error is { Length: > 0 } &&
                JsonNode.Parse(result.ToJson())?["ok"]?.GetValue<bool>() == false, "A disposed controller was not a 409 control error.");
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class PausedDiscovery(string alias, string device) : IPikaDeviceDiscovery
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string?> Distributions { get; } = [];

        public async Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation)
        {
            Distributions.Add(distribution);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellation);
            return new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["device"] = device,
                ["aliases"] = new JsonArray(alias), ["available"] = true }) };
        }
    }

    private sealed class FakeSession(Action? onSnapshot) : IPikaSession
    {
        private string? owner;
        internal int Snapshots;
        internal List<string> Actions { get; } = [];

        public JsonObject Snapshot(bool includeRawEvidence = false)
        {
            onSnapshot?.Invoke();
            return new JsonObject { ["sample"] = ++Snapshots, ["configuration_blocked"] = Actions.Contains("connect") };
        }

        public Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
        {
            if (action == "connect") owner = input.GetProperty("owner").GetString();
            if (action == "disconnect") owner = null;
            Actions.Add(action);
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        public Task<PikaHoldResult> HoldForOwnerAsync(string requestedOwner, CancellationToken cancellation)
        {
            if (owner != requestedOwner) return Task.FromResult(new PikaHoldResult(PikaHoldOutcome.Skipped));
            Actions.Add("hold");
            return Task.FromResult(new PikaHoldResult(PikaHoldOutcome.Acknowledged));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
