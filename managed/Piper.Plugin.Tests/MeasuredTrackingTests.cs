using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class MeasuredTrackingTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var assigned = new RobotProfile("piper-1", "Fixture", "can-a", RobotId: "robot-a");
        check(MeasuredTrackingPolicy.Enabled(assigned), "Existing fully assigned profiles did not default to passive measured tracking");
        check(!MeasuredTrackingPolicy.Enabled(assigned with { RobotId = "" }) && !MeasuredTrackingPolicy.Enabled(assigned with { CanDeviceId = "" }),
            "Unassigned profiles opted into automatic robot selection");
        check(!MeasuredTrackingPolicy.Enabled(assigned with { RememberMeasuredLink = false }), "Explicit Unlink was ignored");
        var roundTrip = JsonSerializer.Deserialize<RobotProfile>(JsonSerializer.Serialize(assigned with { RememberMeasuredLink = false }))!;
        check(roundTrip.RememberMeasuredLink == false, "Explicit Unlink did not survive preference serialization");

        using (var delayed = new Fixture(started: true) { RequestDelayMilliseconds = 250 })
        {
            await delayed.Restore();
            check(delayed.Get<RobotLink?>("link") is not null && delayed.Requests.Count == 3,
                "A healthy three-RPC measured-link handshake shared one 400ms budget under editor load");
        }
        using (var retry = new Fixture(started: true))
        {
            await retry.Restore();
            var previous = retry.Get<RobotLink?>("link");
            retry.Set("measuredTrackingSceneUnverified", true); retry.Set("measuredTrackingAttemptAt", 0L);
            retry.FailRequest = "robot.subscribe"; retry.Fresh();
            await retry.Restore();
            check(ReferenceEquals(previous, retry.Get<RobotLink?>("link")) && !retry.Get<bool>("linkCommandsEnabled"),
                "A transient same-scene subscribe failure destroyed the verified measured binding or restored target permission");
        }

        using (var f = new Fixture())
        {
            await f.Restore();
            check(f.Requests.Count == 0, "An unstarted fixture began passive background discovery");
            f.Set("passiveMeasuredTrackingStarted", true);
            f.Snapshot["devices"]![0]!["status"]!["control"] = 2;
            f.Snapshot["devices"]![0]!["status"]!["teach_state"] = 1;
            f.Fresh();
            await f.Restore();
            check(f.Requests.Select(r => r.Method).SequenceEqual(new[] { "scene.robots", "robot.feedback", "robot.subscribe" }),
                "Passive linking did not validate/apply/ack/subscribe in order during hand-guiding");
            check(f.Get<RobotLink?>("link") is { EntityId: "robot-a", DeviceId: "can-a", Generation: 7 } &&
                !f.Get<bool>("approved") && !f.Get<bool>("linkCommandsEnabled") && !f.Get<bool>("teleoperating") && f.Get<long>("lease") == 0,
                "Passive measured linking granted control, teleoperation or a previous motion lease");
            var feedback = f.Requests.Single(r => r.Method == "robot.feedback").Body;
            check(feedback["joints"]?["joint7"]?.GetValue<double>() is double jaw && Math.Abs(jaw + .047) < 1e-8,
                "Passive linking omitted the matching physical Pika's measured closed jaws");
            int before = f.Requests.Count;
            await f.Restore();
            check(f.Requests.Count == before, "Automatic scene discovery ran more than once per second");
            await f.DrainMonitor();
            f.Set("pikaMonitorAttemptAt", 0L);
            f.InvokeVoid("RetryLinkedPikaMonitor");
            await f.DrainMonitor();
            check(f.Rpc.Routes.Count == 1 && f.Rpc.Routes.Single().Route == "/api/pika/connect" &&
                f.Rpc.Routes.Single().Body["take_over"]?.GetValue<bool>() == false,
                "Automatic tracking sent a physical request beyond Pika's monitor-only connection");
            check(!f.Get<bool>("pikaOwned") && f.Get<bool>("pikaMonitoring"), "Monitoring silently acquired gripper motor control");

            f.Set("measuredTrackingAttemptAt", 0L); f.Fresh(); before = f.Requests.Count;
            await f.Restore();
            check(f.Requests.Count == before, "A healthy measured link polled scene discovery while holding the input gate");
            f.SceneId = "scene-b"; f.Fresh(); f.Set("measuredTrackingAttemptAt", 0L);
            await f.Invoke("PublishMeasuredScene", CancellationToken.None);
            check(f.Get<bool>("measuredTrackingSceneUnverified"), "A reloaded scene's rejected feedback did not request measured-link rediscovery");
            await f.Restore();
            check(f.Get<RobotLink?>("link")?.SceneId == "scene-b" && f.Requests.Count(r => r.Method == "robot.subscribe") == 2,
                "Loading a different scene did not restore the exact assigned robot's measured link");
            check(!f.Get<bool>("linkCommandsEnabled"), "Scene reload restored viewport target permission");
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
            check(f.Get<RobotLink?>("link") is null && !f.Requests.Any(r => r.Method is "robot.feedback" or "robot.subscribe") && f.Rpc.Routes.Count == 0,
                "An unknown, stale or incorrectly scoped assignment reached measured pose binding: " + invalid);
        }

        foreach (string awaitPoint in new[] { "scene.robots", "robot.feedback", "robot.subscribe" })
        foreach (string changed in new[] { "backend", "generation", "profile", "permission", "disposing" })
        {
            using var f = new Fixture(started: true);
            f.AfterRequest = method =>
            {
                if (method != awaitPoint) return;
                if (changed == "backend") { f.Snapshot["token"] = "backend-b"; f.Set("token", "backend-b"); }
                if (changed == "generation") f.Snapshot["devices"]![0]!["generation"] = 8L;
                if (changed == "profile") f.SetPreferences(f.Profile with { Id = "other-profile" });
                if (changed == "permission") f.Set("targetPermissionEpoch", 10L);
                if (changed == "disposing") f.Set("passiveMeasuredTrackingStarted", false);
                f.Fresh();
            };
            await f.Restore();
            check(f.Get<RobotLink?>("link") is null && !f.Get<bool>("approved") && !f.Get<bool>("linkCommandsEnabled") && f.Rpc.Routes.Count == 0,
                "A delayed " + awaitPoint + " acknowledgement survived changed " + changed + " scope");
            if (awaitPoint != "robot.subscribe") check(!f.Requests.Any(r => r.Method == "robot.subscribe"), "Scope changed before subscribe but the stale robot was subscribed");
        }

        using (var f = new Fixture(started: true))
        {
            await f.Commands.WaitAsync();
            try { await f.Restore().WaitAsync(TimeSpan.FromSeconds(1)); }
            finally { f.Commands.Release(); }
            check(f.Requests.Count == 0, "Passive linking queued behind an active operator action");
        }
        foreach (string active in new[] { "teleoperation", "follow", "motion" })
        {
            using var f = new Fixture(started: true);
            if (active == "teleoperation") f.Set("teleoperating", true);
            if (active == "follow") f.Set("followRequest", 7L);
            if (active == "motion") f.Snapshot["motion"]!["active"] = true;
            f.Fresh(); await f.Restore();
            check(f.Requests.Count == 0 && f.Commands.CurrentCount == 1, "Passive discovery interfered with active " + active + " inputs");
        }
        using (var f = new Fixture(started: true))
        {
            f.WrongAcknowledgement = true;
            await f.Restore();
            check(f.Get<RobotLink?>("link") is null && !f.Requests.Any(r => r.Method == "robot.subscribe"),
                "A wrong scene/entity acknowledgement was promoted to a measured binding");
        }
        using (var f = new Fixture(started: true))
        {
            // Exercise the real public Link toggle without StartAsync, cameras
            // or a real server. Persisted false must survive a new instance.
            await f.Restore(); await f.DrainMonitor();
            f.Set("pikaMonitoring", false); f.Set("pikaMonitorStopping", true);
            await f.LinkEvent();
            var saved = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(Path.Combine(f.Folder, "preferences.json")), PluginProtocol.Json)!;
            check(RobotProfileRouting.Active(saved).RememberMeasuredLink == false && f.Get<RobotLink?>("link") is null,
                "Explicit Unlink did not persist passive tracking off for the active profile");
            f.Set("measuredTrackingAttemptAt", 0L); f.Fresh();
            int before = f.Requests.Count;
            await f.Restore();
            check(f.Requests.Count == before, "A refresh immediately relinked after explicit Unlink");
            await f.LinkEvent();
            saved = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(Path.Combine(f.Folder, "preferences.json")), PluginProtocol.Json)!;
            check(RobotProfileRouting.Active(saved).RememberMeasuredLink == true && f.Get<RobotLink?>("link") is not null,
                "Explicit Link did not persist automatic measured tracking back on");
            check(f.Rpc.Routes.All(r => r.Route == "/api/pika/connect"), "Link/Unlink sent power, reset, target, configuration or teleoperation requests");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin = new();
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "piper-passive-link-" + Guid.NewGuid().ToString("N"));
        public FakeCalls Rpc { get; } = new();
        public List<(string Method, JsonObject Body)> Requests { get; } = [];
        public string SceneId = "scene-a";
        public bool WrongAcknowledgement;
        public int RequestDelayMilliseconds;
        public string FailRequest = "";
        public Action<string>? AfterRequest;
        public JsonObject Snapshot { get; }
        public RobotProfile Profile => RobotProfileRouting.Active(Get<Preferences>("preferences"));
        public SemaphoreSlim Commands => Get<SemaphoreSlim>("commands");
        public void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, Flags)!.SetValue(plugin, value);
        public T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, Flags)!.GetValue(plugin)!;
        public Task Invoke(string name, params object[] arguments) => (Task)typeof(PipersPlugin).GetMethod(name, Flags)!.Invoke(plugin, arguments)!;
        public void InvokeVoid(string name) => typeof(PipersPlugin).GetMethod(name, Flags)!.Invoke(plugin, null);
        public Task Restore() => Invoke("RestoreMeasuredTracking", CancellationToken.None);
        public void SetPreferences(RobotProfile profile) => Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [profile], ActiveRobotId: profile.Id)));
        public void Fresh()
        {
            Set("state", Snapshot);
            Get<LatestTelemetry>("telemetry").Publish(new(Snapshot, Snapshot["token"]!.GetValue<string>(), 1, Stopwatch.GetTimestamp(), 0));
        }
        public async Task DrainMonitor()
        {
            if (Get<Task?>("pikaMonitorWorker") is { } worker) await worker.WaitAsync(TimeSpan.FromSeconds(1));
        }
        public Task LinkEvent() => plugin.HandleEventAsync(new("fixture", WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), Profile.Id, "link"),
            "click", JsonSerializer.SerializeToElement(true)), default);

        public Fixture(bool started = false)
        {
            Directory.CreateDirectory(Folder);
            SetPreferences(new("piper-1", "Fixture", "can-a", RobotId: "robot-a", PikaDevice: "/dev/pika-a"));
            Set("token", "backend-a"); Set("client", new PiperControl.PiperControlClient(Rpc));
            Set("passiveMeasuredTrackingStarted", started);
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
                    ["configured"] = true, ["device"] = "/dev/pika-a", ["distribution"] = "Ubuntu", ["owner"] = "",
                    ["connected"] = false, ["fresh"] = true, ["power_requested"] = false, ["power_uncertain"] = false,
                    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = 0.0 } }
                }
            };
            // A connected external device can already provide shared read-only
            // feedback before this plugin opens its monitor session.
            Snapshot["pika"]!["connected"] = true;
            Fresh();
            Set("engine", (PluginContext)Activator.CreateInstance(typeof(PluginContext), Flags, null,
                [new PluginStart("fixture", Folder, Folder),
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
                    })], null)!);
        }

        private static JsonArray Model() => new(Enumerable.Range(1, 8).Select(i => (JsonNode)(i < 7
            ? new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }
            : new JsonObject { ["name"] = "joint" + i, ["unit"] = "m", ["limited"] = true, ["min"] = i == 7 ? -.047 : 0.0, ["max"] = i == 7 ? 0.0 : .047 })).ToArray());
        public void Dispose()
        {
            Set("pikaMonitorStopping", true);
            Get<CancellationTokenSource>("shutdown").Cancel();
            DrainMonitor().GetAwaiter().GetResult();
            ((IDisposable)Get<object>("channel")).Dispose(); Commands.Dispose();
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
