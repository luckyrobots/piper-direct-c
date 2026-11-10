using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static class MeasuredTrackingTests
{
    public static async Task Run()
    {
        var assigned = new RobotProfile("piper-1", "Fixture", "can-a", RobotId: "robot-a");
        Check(MeasuredTrackingPolicy.Enabled(assigned), "Existing fully assigned profiles did not default to passive measured tracking");
        Check(!MeasuredTrackingPolicy.Enabled(assigned with { RobotId = "" }) && !MeasuredTrackingPolicy.Enabled(assigned with { CanDeviceId = "" }),
            "Unassigned profiles opted into automatic robot selection");
        Check(!MeasuredTrackingPolicy.Enabled(assigned with { RememberMeasuredLink = false }), "Explicit Unlink was ignored");
        var roundTrip = JsonSerializer.Deserialize<RobotProfile>(JsonSerializer.Serialize(assigned with { RememberMeasuredLink = false }))!;
        Check(roundTrip.RememberMeasuredLink == false, "Explicit Unlink did not survive preference serialization");

        using (var delayed = new Fixture(started: true) { RequestDelayMilliseconds = 250 })
        {
            await delayed.Restore();
            Check(delayed.Plugin.link is not null && delayed.Requests.Count == 3,
                "A healthy three-RPC measured-link handshake shared one 400ms budget under editor load");
        }
        using (var retry = new Fixture(started: true))
        {
            await retry.Restore();
            var previous = retry.Plugin.link;
            retry.Plugin.measuredTrackingSceneUnverified = true; retry.Plugin.measuredTrackingAttemptAt = 0L;
            retry.FailRequest = "robot.subscribe"; retry.Fresh();
            await retry.Restore();
            Check(ReferenceEquals(previous, retry.Plugin.link) && !retry.Plugin.linkCommandsEnabled,
                "A transient same-scene subscribe failure destroyed the verified measured binding or restored target permission");
        }

        using (var f = new Fixture())
        {
            await f.Restore();
            Check(f.Requests.Count == 0, "An unstarted fixture began passive background discovery");
            f.Plugin.passiveMeasuredTrackingStarted = true;
            f.Snapshot["devices"]![0]!["status"]!["control"] = 2;
            f.Snapshot["devices"]![0]!["status"]!["teach_state"] = 1;
            f.Fresh();
            await f.Restore();
            Check(f.Requests.Select(r => r.Method).SequenceEqual(new[] { "scene.robots", "robot.feedback", "robot.subscribe" }),
                "Passive linking did not validate/apply/ack/subscribe in order during hand-guiding");
            Check(f.Plugin.link is { EntityId: "robot-a", DeviceId: "can-a", Generation: 7 } &&
                !f.Plugin.approved && !f.Plugin.linkCommandsEnabled && f.Plugin.viewportDrag.Phase == ViewportDragPhase.Off && f.Authority.Current.Lease is null,
                "Passive measured linking granted control, teleoperation or a previous motion lease");
            var feedback = f.Requests.Single(r => r.Method == "robot.feedback").Body;
            Check(feedback["joints"]?["joint7"]?.GetValue<double>() is double jaw && Math.Abs(jaw + .047) < 1e-8,
                "Passive linking omitted the matching physical Pika's measured closed jaws");
            int before = f.Requests.Count;
            await f.Restore();
            Check(f.Requests.Count == before, "Automatic scene discovery ran more than once per second");
            await f.DrainMonitor();
            f.Plugin.telemetry.Pika.ExpireBackoff();
            f.Plugin.RetryLinkedPikaMonitor();
            await f.DrainMonitor();
            Check(f.Rpc.Routes.Count == 1 && f.Rpc.Routes.Single().Route == "/api/pika/connect" &&
                f.Rpc.Routes.Single().Body["take_over"]?.GetValue<bool>() == false,
                "Automatic tracking sent a physical request beyond Pika's monitor-only connection");
            Check(f.Plugin.telemetry.Pika.Role == PikaSessionRole.Monitoring, "Monitoring silently acquired gripper motor control");

            f.Plugin.measuredTrackingAttemptAt = 0L; f.Fresh(); before = f.Requests.Count;
            await f.Restore();
            Check(f.Requests.Count == before, "A healthy measured link polled scene discovery while holding the input gate");
            f.SceneId = "scene-b"; f.Fresh(); f.Plugin.measuredTrackingAttemptAt = 0L;
            await f.Plugin.PublishMeasuredScene(CancellationToken.None);
            Check(f.Plugin.measuredTrackingSceneUnverified, "A reloaded scene's rejected feedback did not request measured-link rediscovery");
            await f.Restore();
            Check(f.Plugin.link?.SceneId == "scene-b" && f.Requests.Count(r => r.Method == "robot.subscribe") == 2,
                "Loading a different scene did not restore the exact assigned robot's measured link");
            Check(!f.Plugin.linkCommandsEnabled, "Scene reload restored viewport target permission");
        }

        foreach (string invalid in new[] { "selected", "stale", "missing", "offline", "generation" })
        {
            using var f = new Fixture(started: true);
            if (invalid == "selected") f.Snapshot["selected"] = "can-other";
            if (invalid == "stale") f.Snapshot["devices"]![0]!["status"]!["age_s"] = 1.0;
            if (invalid == "offline") f.Snapshot["devices"]![0]!["present"] = false;
            if (invalid == "generation") f.Snapshot["devices"]![0]!["generation"] = 0L;
            if (invalid == "missing") f.SetPreferences(f.Profile with { RobotId = "unknown-robot" });
            f.Fresh(); await f.Restore();
            Check(f.Plugin.link is null && !f.Requests.Any(r => r.Method is "robot.feedback" or "robot.subscribe") && f.Rpc.Routes.Count == 0,
                "An unknown, stale or incorrectly scoped assignment reached measured pose binding: " + invalid);
        }

        foreach (string awaitPoint in new[] { "scene.robots", "robot.feedback", "robot.subscribe" })
        foreach (string changed in new[] { "backend", "generation", "profile", "permission", "disposing" })
        {
            using var f = new Fixture(started: true);
            f.AfterRequest = method =>
            {
                if (method != awaitPoint) return;
                if (changed == "backend") { f.Snapshot["token"] = "backend-b"; f.Plugin.telemetry.AdoptBackend("backend-b"); }
                if (changed == "generation") f.Snapshot["devices"]![0]!["generation"] = 8L;
                if (changed == "profile") f.SetPreferences(f.Profile with { Id = "other-profile" });
                if (changed == "permission") f.Authority.Revoke();
                if (changed == "disposing") f.Plugin.passiveMeasuredTrackingStarted = false;
                f.Fresh();
            };
            await f.Restore();
            Check(f.Plugin.link is null && !f.Plugin.approved && !f.Plugin.linkCommandsEnabled && f.Rpc.Routes.Count == 0,
                "A delayed " + awaitPoint + " acknowledgement survived changed " + changed + " scope");
            if (awaitPoint != "robot.subscribe") Check(!f.Requests.Any(r => r.Method == "robot.subscribe"), "Scope changed before subscribe but the stale robot was subscribed");
        }

        using (var f = new Fixture(started: true))
        {
            await f.Commands.WaitAsync();
            try { await f.Restore().WaitAsync(TimeSpan.FromSeconds(1)); }
            finally { f.Commands.Release(); }
            Check(f.Requests.Count == 0, "Passive linking queued behind an active operator action");
        }
        foreach (string active in new[] { "teleoperation", "follow", "motion" })
        {
            using var f = new Fixture(started: true);
            if (active == "teleoperation") f.Plugin.viewportDrag = new ViewportDrag(ViewportDragPhase.Teleoperating);
            if (active == "follow") f.Plugin.viewportDrag = new ViewportDrag(ViewportDragPhase.Following, Request: 7);
            if (active == "motion") f.Snapshot["motion"]!["active"] = true;
            f.Fresh(); await f.Restore();
            Check(f.Requests.Count == 0 && f.Commands.CurrentCount == 1, "Passive discovery interfered with active " + active + " inputs");
        }
        using (var f = new Fixture(started: true))
        {
            f.WrongAcknowledgement = true;
            await f.Restore();
            Check(f.Plugin.link is null && !f.Requests.Any(r => r.Method == "robot.subscribe"),
                "A wrong scene/entity acknowledgement was promoted to a measured binding");
        }
        using (var f = new Fixture(started: true))
        {
            // Exercise the real public Link toggle without StartAsync, cameras
            // or a real server. Persisted false must survive a new instance.
            await f.Restore(); await f.DrainMonitor();
            f.Plugin.telemetry.Pika.Enter(PikaSessionRole.None); f.Plugin.telemetry.Pika.Stop();
            await f.LinkEvent();
            var saved = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(Path.Combine(f.Folder, "preferences.json")), PluginProtocol.Json)!;
            Check(RobotProfileRouting.Active(saved).RememberMeasuredLink == false && f.Plugin.link is null,
                "Explicit Unlink did not persist passive tracking off for the active profile");
            f.Plugin.measuredTrackingAttemptAt = 0L; f.Fresh();
            int before = f.Requests.Count;
            await f.Restore();
            Check(f.Requests.Count == before, "A refresh immediately relinked after explicit Unlink");
            await f.LinkEvent();
            saved = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(Path.Combine(f.Folder, "preferences.json")), PluginProtocol.Json)!;
            Check(RobotProfileRouting.Active(saved).RememberMeasuredLink == true && f.Plugin.link is not null,
                "Explicit Link did not persist automatic measured tracking back on");
            Check(f.Rpc.Routes.All(r => r.Route == "/api/pika/connect"), "Link/Unlink sent power, reset, target, configuration or teleoperation requests");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PipersPlugin plugin = new();
        public PipersPlugin Plugin => plugin;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "piper-passive-link-" + Guid.NewGuid().ToString("N"));
        public FakeCalls Rpc { get; } = new();
        public List<(string Method, JsonObject Body)> Requests { get; } = [];
        public string SceneId = "scene-a";
        public bool WrongAcknowledgement;
        public int RequestDelayMilliseconds;
        public string FailRequest = "";
        public Action<string>? AfterRequest;
        public JsonObject Snapshot { get; }
        public RobotProfile Profile => RobotProfileRouting.Active(plugin.preferences);
        public SemaphoreSlim Commands => plugin.commands;
        public RobotCommandAuthority Authority => plugin.CommandAuthority;
        public Task Restore() => plugin.RestoreMeasuredTracking(CancellationToken.None);
        public void SetPreferences(RobotProfile profile) => plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [profile], ActiveRobotId: profile.Id));
        public void Fresh()
        {
            plugin.telemetry.PublishState(Snapshot);
            plugin.telemetry.Publish(new(Snapshot, Snapshot["token"]!.GetValue<string>(), 1, Stopwatch.GetTimestamp(), 0));
        }
        public async Task DrainMonitor()
        {
            if (plugin.telemetry.Pika.Attempt?.Worker is { } worker) await worker.WaitAsync(TimeSpan.FromSeconds(1));
        }
        public Task LinkEvent() => plugin.HandleEventAsync(new("fixture", WorkspaceEventScope.Wrap(plugin.workspace.Epoch, Profile.Id, "link"),
            "click", JsonSerializer.SerializeToElement(true)), default);

        public Fixture(bool started = false)
        {
            Directory.CreateDirectory(Folder);
            SetPreferences(new("piper-1", "Fixture", "can-a", RobotId: "robot-a", PikaDevice: "/dev/pika-a"));
            plugin.telemetry.AdoptBackend("backend-a"); plugin.client = new PiperControl.PiperControlClient(Rpc);
            plugin.passiveMeasuredTrackingStarted = started;
            Snapshot = new JsonObject
            {
                ["token"] = "backend-a", ["selected"] = "can-a", ["motion"] = new JsonObject { ["active"] = false },
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "can-a", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
                    ["status"] = new JsonObject { ["age_s"] = 0.001, ["control"] = 1, ["teach_state"] = 0 },
                    ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
                    { ["index"] = i, ["actual_deg"] = i * 1.0, ["fresh"] = true, ["age_s"] = .001 }).ToArray())
                }),
                ["pika"] = new JsonObject
                {
                    ["configured"] = true, ["device"] = "/dev/pika-a", ["distribution"] = PikaDistribution.ForCurrentPlatform(null), ["owner"] = "",
                    ["connected"] = false, ["fresh"] = true, ["power_requested"] = false, ["power_uncertain"] = false,
                    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = 0.0 } }
                }
            };
            // A connected external device can already provide shared read-only
            // feedback before this plugin opens its monitor session.
            Snapshot["pika"]!["connected"] = true;
            Fresh();
            plugin.engine = Fixtures.Context(new PluginStart("fixture", Folder, Folder),
                    (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, body, cancellation) =>
                    {
                        Requests.Add((method, (JsonObject)body.DeepClone()));
                        if (RequestDelayMilliseconds > 0) { await Task.Delay(RequestDelayMilliseconds, cancellation); Fresh(); }
                        if (method == FailRequest) throw new TimeoutException("Fixture editor acknowledgement delayed");
                        if (method == "robot.feedback" && body["sceneId"]?.GetValue<string>() != SceneId)
                            throw new InvalidOperationException("Scene changed; select and link the robot again");
                        JsonObject pose = new() { ["sceneId"] = WrongAcknowledgement ? "wrong-scene" : SceneId, ["entityId"] = "robot-a" };
                        if (method == "scene.robots")
                        {
                            pose["joints"] = Model();
                            var response = JsonSerializer.SerializeToElement(new JsonObject { ["sceneId"] = SceneId, ["robots"] = new JsonArray(pose) });
                            AfterRequest?.Invoke(method); return response;
                        }
                        pose["joints"] = new JsonArray((body["joints"] as JsonObject ?? new()).Select(pair => (JsonNode)new JsonObject
                        { ["name"] = pair.Key, ["position"] = pair.Value!.DeepClone() }).ToArray());
                        var reply = JsonSerializer.SerializeToElement(method == "robot.feedback" ? new JsonObject { ["applied"] = true, ["pose"] = pose } : pose);
                        AfterRequest?.Invoke(method); return reply;
                    }));
        }

        private static JsonArray Model() => new(Enumerable.Range(1, 8).Select(i => (JsonNode)(i < 7
            ? new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }
            : new JsonObject { ["name"] = "joint" + i, ["unit"] = "m", ["limited"] = true, ["min"] = i == 7 ? -.047 : 0.0, ["max"] = i == 7 ? 0.0 : .047 })).ToArray());
        public void Dispose()
        {
            plugin.telemetry.Pika.Stop();
            plugin.shutdown.Cancel();
            DrainMonitor().GetAwaiter().GetResult();
            plugin.channel.Dispose(); Commands.Dispose();
        }
    }

    private sealed class FakeCalls : CallInvoker
    {
        public ConcurrentQueue<(string Route, JsonObject Body)> Routes { get; } = new();
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fixture RPC");
            Routes.Enqueue((command.Route, JsonNode.Parse(command.Json)!.AsObject()));
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{}" }), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
