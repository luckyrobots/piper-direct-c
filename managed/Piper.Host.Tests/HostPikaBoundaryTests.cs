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
        const string alias = "/dev/ttyUSB5", distribution = "Ubuntu", arm = "simulation:one";
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
            await registry.ExecuteAsync("configure", JsonSerializer.SerializeToElement(new { owner, device = first }), default);
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
            Check((await Send("/api/stop", new { owner, device_id = arm })).Status == 200, "Simulator STOP failed after denied configuration.");

            var accepted = await Send("/api/pika/configure", new { owner, device = alias });
            Check(accepted.Status == 200 && registry.Snapshot()["device"]?.GetValue<string>() == replacement &&
                agents[replacement].Actions.Count == 0, "Idle configuration did not register without opening or writing to the gripper.");
            Console.WriteLine("Host Pika boundaries passed: same-sample snapshot, source clock, idle commit, and independent STOP/peer writes.");

            JsonObject ArmState() => JsonNode.Parse(controller.Snapshot())!.AsObject();

            async Task<HostControlResult> Send(string route, object body)
            {
                var input = JsonSerializer.SerializeToElement(body);
                using var admission = context.Control.Admit(route, input, HostControlTransport.Http);
                if (admission.Error is { } error) return new(409, Error: error);
                return await context.Control.ExecuteAsync(route, input.GetRawText(), input, HostControlTransport.Http, deadline.Token);
            }

            async Task Poll(Func<JsonObject, bool> ready, long? requestId = null)
            {
                while (true)
                {
                    var latest = ArmState();
                    if (ready(latest)) return;
                    if (requestId is { } id && ControllerActivity.AnyMotion(latest))
                        Check((await Send("/api/heartbeat", new { owner, device_id = arm, generation, request_id = id })).Status == 200,
                            "Simulator heartbeat rejected.");
                    await Task.Delay(10, deadline.Token);
                }
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
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

        public async Task<JsonObject> DiscoverAsync(string? distribution, CancellationToken cancellation)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellation);
            return new JsonObject { ["devices"] = new JsonArray(new JsonObject { ["device"] = device,
                ["aliases"] = new JsonArray(alias), ["available"] = true }) };
        }
    }

    private sealed class FakeSession(Action? onSnapshot) : IPikaSession
    {
        internal int Snapshots;
        internal List<string> Actions { get; } = [];

        public JsonObject Snapshot(bool includeRawEvidence = false)
        {
            onSnapshot?.Invoke();
            return new JsonObject { ["sample"] = ++Snapshots, ["configuration_blocked"] = Actions.Contains("connect") };
        }

        public Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
        {
            Actions.Add(action);
            return Task.FromResult(new JsonObject { ["ok"] = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
