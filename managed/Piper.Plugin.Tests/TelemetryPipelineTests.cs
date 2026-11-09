using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class TelemetryPipelineTests
{
    public static async Task Run(Action<bool, string> check)
    {
        long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, ticks = Stopwatch.GetTimestamp();
        long At(double ms) => ticks + (long)(Stopwatch.Frequency * ms / 1000);
        JsonObject State(string token = "session-a", long generation = 7) => new()
        {
            ["token"] = token, ["selected"] = "can-a",
            ["devices"] = new JsonArray(new JsonObject
            {
                ["id"] = "can-a", ["name"] = "Fixture", ["generation"] = generation, ["fresh"] = true, ["present"] = true, ["up"] = true,
                ["firmware_supported"] = true, ["recovery_required"] = false, ["reset_pending"] = false, ["blocker"] = "",
                ["status"] = new JsonObject { ["control"] = 1, ["teach_state"] = 0, ["motion_mode"] = 1, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0, ["age_s"] = .005 },
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
                {
                    ["index"] = i, ["actual_deg"] = 0.0, ["fresh"] = true, ["enabled"] = true, ["fault"] = 0, ["age_s"] = .005,
                    ["current_a"] = 0.0, ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
                }).ToArray())
            }),
            ["motion"] = new JsonObject { ["active"] = true, ["request_id"] = 7L, ["owner"] = "fixture-owner" },
            ["pika"] = new JsonObject { ["configured"] = false, ["fresh"] = false, ["connected"] = false }
        };
        var inbox = new LatestTelemetry();
        bool Prepare(JsonObject state, long sequence, long source, string clock, long now, long monotonic, out TelemetryReceipt? receipt) =>
            inbox.TryPrepare(state, sequence, source, clock, now, monotonic, out receipt, out _);
        check(Prepare(State(), 1, utc - 100_000, "host-utc", utc, ticks, out var first), "Fresh source-stamped telemetry was rejected");
        inbox.Publish(first!);
        check(first!.IsFresh(At(400)) && !first.IsFresh(At(401)), "Source age plus receipt age did not enforce the unchanged 500 ms window");
        var agedDevice = State()["devices"]![0]!.AsObject();
        check(first.HasFreshDevice(agedDevice, At(300)), "Valid device sample age plus transport age was rejected");
        agedDevice["joints"]![2]!["age_s"] = .25;
        check(!first.HasFreshDevice(agedDevice, At(300)), "Fresh-at-source joint flags hid a sample older than 500 ms after transport delay");
        agedDevice["joints"]![2]!["age_s"] = .005; agedDevice["status"]!["age_s"] = .25;
        check(!first.HasFreshDevice(agedDevice, At(300)), "Fresh-at-source status hid accumulated transport delay");
        agedDevice["status"]!["age_s"] = double.NaN;
        check(!first.HasFreshDevice(agedDevice, ticks), "An invalid sample age passed command freshness");
        var ackReceipt = new TelemetryReceipt(State(), "session-a", 3, ticks, 0);
        var ackDevice = State()["devices"]![0]!.AsObject();
        ackDevice["status"]!["age_s"] = .45; ackDevice["joints"]![4]!["age_s"] = .45;
        check(ackReceipt.HasFreshDevice(ackDevice, ticks) && ackReceipt.IsFresh(At(300)), "Acknowledgement fixture did not isolate source-sample age from wrapper age");
        check(Math.Abs(ackReceipt.DeviceEvidenceAgeMilliseconds(ackDevice, At(300)) - 750) < .001 && !ackReceipt.HasFreshDevice(ackDevice, At(300)),
            "A 450ms source sample plus300ms editor wait was accepted/stamped as a fresh acknowledgement");
        var pikaState = State();
        var pika = pikaState["pika"]!.AsObject();
        pika["connected"] = true; pika["fresh"] = true; pika["age_s"] = .15;
        var pikaReceipt = new TelemetryReceipt(pikaState, "session-a", 4, ticks, 100);
        double pikaDeadline = SimulationFollowPolicy.MaximumGripperFeedbackAgeMilliseconds;
        check(Math.Abs(pikaReceipt.PikaEvidenceAgeMilliseconds(ticks) - 250) < .001 && pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline),
            "Pika age did not include its source sample age and envelope delay at the 250ms boundary");
        check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, At(1), pikaDeadline), "An aged Pika receipt renewed its 250ms evidence window");
        pika["age_s"] = .3;
        check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A true Pika fresh flag hid a sample too old for physical following");
        foreach (JsonNode? invalidAge in new JsonNode?[] { null, JsonValue.Create(-.001), JsonValue.Create(double.NaN), JsonValue.Create(double.PositiveInfinity), JsonValue.Create("0.01") })
        {
            pika["age_s"] = invalidAge;
            check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "Missing or invalid Pika source age authorized following");
        }
        pika["age_s"] = .001; pika["connected"] = false;
        check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "Disconnected Pika feedback authorized following");
        pika["connected"] = true; pika["fresh"] = false;
        check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A false Pika fresh flag authorized following");
        pika["fresh"] = true;
        check(!(pikaReceipt with { Connected = false }).HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A disconnected telemetry envelope authorized Pika following");
        check(!Prepare(State(), 2, utc - 501_000, "host-utc", utc, ticks, out _), "A queued old frame was stamped fresh on arrival");
        check(ReferenceEquals(inbox.Latest, first), "Rejecting an aged frame replaced the last verified receipt");
        check(!Prepare(State(), 2, utc + 101_000, "host-utc", utc, ticks, out _), "An unbounded future source clock passed freshness checks");
        check(!Prepare(State(), 2, utc, "unknown", utc, ticks, out _), "Unverified source clock passed freshness checks");
        check(!Prepare(State(), 0, utc, "host-utc", utc, ticks, out _), "A missing sequence passed freshness checks");
        check(!Prepare(State(), 1, utc, "host-utc", utc, ticks, out _), "Duplicate sequence replaced current feedback");
        check(!Prepare(State(), 0, utc, "host-utc", utc, ticks, out _), "A reversed sequence replaced current feedback");
        check(Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out var restarted), "A new backend could not establish a fresh sequence");
        inbox.Publish(restarted!); inbox.Disconnect();
        check(inbox.Latest?.IsFresh(ticks) == false, "An interrupted stream retained command freshness");
        check(Prepare(State("session-b"), 10000, utc, "host-utc", utc, ticks, out var highSequence), "High same-stream sequence was rejected");
        inbox.Publish(highSequence!); inbox.Disconnect(); inbox.BeginStream();
        check(inbox.Latest?.IsFresh(ticks) == false, "Beginning a stream renewed an old disconnected receipt");
        check(!Prepare(State("session-b"), 1, utc - 600_000, "host-utc", utc, ticks, out _), "New stream epoch bypassed source-age validation");
        check(Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out var reconnected), "Same-backend stream reconnect waited for the old sequence count");
        inbox.Publish(reconnected!);
        check(!Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out _), "A duplicate in the reconnected stream was accepted");
        check(TelemetryScope.Read(State(), "can-a") != TelemetryScope.Read(State(generation: 8), "can-a"), "CAN reconnection inherited old identity scope");
        check(TelemetryScope.Read(State(), "can-a") != TelemetryScope.Read(State("session-b"), "can-a"), "Backend restart inherited old identity scope");
        check(TelemetryScope.Read(State(), "can-b") is null, "A foreign CAN selection was accepted as the assigned robot");

        // Exercise the real four-loop plugin with an in-memory gRPC stream and a
        // deliberately slow fake editor. No socket, camera, CAN or Pika is opened.
        var plugin = new PipersPlugin();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, flags)!.GetValue(plugin)!;
        var pipe = Channel.CreateBounded<StateFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var rpc = new FakeCalls(pipe.Reader);
        Set("client", new PiperControl.PiperControlClient(rpc));
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new RobotProfile("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1"));
        Set("preferences", prefs); Set("token", "session-a");
        Set("telemetryScope", new TelemetryScope("session-a", "can-a", 7));
        string sessionOwner = Get<string>("owner");
        var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
        var device = State()["devices"]![0]!.AsObject();
        var binding = new RobotLink("fixture-scene", "fixture-robot", model, device);
        var authority = plugin.CommandAuthority;
        var commandScope = new RobotCommandScope("piper-1", "session-a", "can-a", 7);
        Set("link", binding); Set("linkBackendToken", "session-a"); Set("approved", true); Set("linkCommandsEnabled", true);
        authority.TryAcknowledge(authority.Epoch, commandScope, 7);
        int sceneRequests = 0, editorMode = 0;
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), flags, null,
            [new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
                (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, _, cancellation) =>
                {
                    if (method == "robot.feedback")
                    {
                        Interlocked.Increment(ref sceneRequests);
                        int selectedMode = Volatile.Read(ref editorMode);
                        if (selectedMode == 0) await Task.Delay(750, cancellation);
                        else return JsonSerializer.SerializeToElement(new { applied = false, teleoperating = selectedMode == 1 });
                    }
                    return JsonSerializer.SerializeToElement(new { applied = false });
                })], null)!;
        Set("engine", context);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var feeding = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long sequence = 0;
        string currentToken = "session-a"; long currentGeneration = 7;
        void Feed()
        {
            var state = State(currentToken, currentGeneration);
            state["motion"]!["owner"] = sessionOwner;
            pipe.Writer.TryWrite(new StateFrame { Json = state.ToJsonString(), Sequence = Interlocked.Increment(ref sequence),
                TimestampUnixMicroseconds = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10, ClockDomain = "host-utc" });
        }
        Feed();
        var producer = Task.Run(async () =>
        {
            try { while (!feeding.IsCancellationRequested) { Feed(); await Task.Delay(20, feeding.Token); } }
            catch (OperationCanceledException) when (feeding.IsCancellationRequested) { }
        });
        var watching = (Task)typeof(PipersPlugin).GetMethod("Watch", flags)!.Invoke(plugin, [lifetime.Token])!;
        async Task Until(Func<bool> ready, string failure, int maximumMs = 5000)
        {
            long started = Stopwatch.GetTimestamp();
            while (!ready())
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > maximumMs) throw new Exception(failure);
                await Task.Delay(15);
            }
        }
        try
        {
            await Until(() => Volatile.Read(ref sceneRequests) >= 2 && rpc.Heartbeats >= 3, "Slow rendering blocked ingestion or independent heartbeats");
            var current = Get<LatestTelemetry>("telemetry").Latest!;
            check(current.IsFresh(Stopwatch.GetTimestamp()) && current.Sequence >= 5, "Slow editor requests stopped fresh state consumption");
            check(rpc.Heartbeats >= 3, "Slow editor requests stopped controller heartbeats");
            check(ReferenceEquals(Get<RobotLink?>("link"), binding), "A rendering timeout destroyed a healthy same-scope scene binding");
            check(Get<JsonObject?>("state") is not null && Get<string>("token") == "session-a", "A rendering timeout was classified as controller disconnection");
            check(!Get<bool>("linkCommandsEnabled"), "A failed scene acknowledgement automatically armed viewport targets");
            int beforeModeChange = Volatile.Read(ref sceneRequests);
            Volatile.Write(ref editorMode, 1);
            await Until(() => Volatile.Read(ref sceneRequests) > beforeModeChange && Get<string>("sceneTrackingStatus").StartsWith("Measured scene update deferred", StringComparison.Ordinal),
                "The slow fake request did not drain before the leader/ghost check");
            Set("teleoperating", true); Set("linkCommandsEnabled", true); Set("followRequest", 7L);
            authority.TryAcknowledge(authority.Epoch, commandScope, 7);
            int beforeGhost = Volatile.Read(ref sceneRequests);
            await Until(() => Volatile.Read(ref sceneRequests) > beforeGhost && Get<string>("sceneTrackingStatus").StartsWith("Leader target + measured ghost", StringComparison.Ordinal), "Native leader/ghost feedback was not recognized");
            check(Get<bool>("teleoperating") && Get<bool>("linkCommandsEnabled") && Get<long>("followRequest") == 7 && authority.Current.Lease?.Request == 7,
                "Expected native applied:false leader/ghost feedback stopped a valid drag or finishing target");
            check(Get<JsonObject?>("acknowledgedScenePose") is null,
                "Leader/ghost feedback was incorrectly promoted to a calibration pose acknowledgement");
            Volatile.Write(ref editorMode, 2);
            await Until(() => !Get<bool>("teleoperating"), "A remote teleoperation stop did not pause local following");
            check(!Get<bool>("linkCommandsEnabled") && Get<long>("followRequest") == 0 && authority.Current.Lease is null,
                "Unexpected remote editor deferral kept viewport targets armed");
            await feeding.CancelAsync(); await producer;
            await Until(() => !Get<bool>("approved") && authority.Current.Lease is null, "Real telemetry loss did not disarm commands/deadlines", 1500);
            check(!Get<LatestTelemetry>("telemetry").Latest!.IsFresh(Stopwatch.GetTimestamp()), "Genuine telemetry loss remained fresh");
            check(ReferenceEquals(Get<RobotLink?>("link"), binding), "A genuine gap discarded a same-identity read-only binding instead of pausing it");
            Feed();
            await Until(() => Get<LatestTelemetry>("telemetry").Latest!.IsFresh(Stopwatch.GetTimestamp()), "Same-scope fresh telemetry did not resume reading");
            check(!Get<bool>("approved") && !Get<bool>("linkCommandsEnabled"), "Fresh telemetry silently rearmed old target permission");
            currentGeneration = 8; Feed();
            await Until(() => Get<RobotLink?>("link") is null, "A new CAN generation inherited the previous physical scene binding");
            check(!Get<bool>("approved"), "CAN generation change retained target permission");
            var rebound = new RobotLink("fixture-scene", "fixture-robot", model, State(generation: 8)["devices"]![0]!.AsObject());
            Set("link", rebound); Set("linkBackendToken", "session-a"); Set("approved", true);
            currentToken = "session-b"; Feed();
            await Until(() => Get<RobotLink?>("link") is null && Get<string>("token") == "session-b", "Backend session change inherited old link/authorization");
            check(!Get<bool>("approved"), "Backend session change retained target permission");
            check(rpc.Routes.All(route => route == "/api/heartbeat"), "Telemetry/render recovery issued a power, target, reset or configuration action");

            // A response to an action prepared before telemetry loss must not
            // recreate the discarded lease after fresh feedback returns.
            Set("approved", true); Feed();
            await Until(() => Get<LatestTelemetry>("telemetry").Latest!.BackendToken == "session-b" && Get<LatestTelemetry>("telemetry").Latest!.IsFresh(Stopwatch.GetTimestamp()), "Fixture did not restore fresh input for acknowledgement test");
            rpc.DelayedCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = (Task)typeof(PipersPlugin).GetMethod("Command", flags)!.Invoke(plugin,
                ["jog", new JsonObject { ["joint"] = 6, ["delta_deg"] = 1.0 }, lifetime.Token, false, false, null, null, null, null, null])!;
            await Until(() => rpc.CommandReached.Task.IsCompleted, "Synthetic authorized action did not reach the fake transport");
            typeof(PipersPlugin).GetMethod("DisarmTelemetryTargets", flags)!.Invoke(plugin, ["Fixture telemetry loss", false, null, false]);
            rpc.DelayedCommand.SetResult(new ControlReply { Status = 202, Json = "{\"request_id\":99,\"message\":\"accepted\"}" });
            bool discarded = false;
            try { await pending; } catch (InvalidOperationException) { discarded = true; }
            check(discarded && authority.Current is { Lease: null, LastCommand: null },
                "A delayed old acknowledgement renewed its discarded operation after telemetry loss");
            int sentBefore = rpc.Routes.Count;
            long previousPermission = authority.Epoch - 1;
            pending = (Task)typeof(PipersPlugin).GetMethod("Command", flags)!.Invoke(plugin,
                ["teach-return", new JsonObject { ["button_off"] = true, ["same_pose_confirmed"] = true }, lifetime.Token,
                    false, true, null, null, null, previousPermission, null])!;
            discarded = false;
            try { await pending; } catch (InvalidOperationException) { discarded = true; }
            check(discarded && rpc.Routes.Count == sentBefore,
                "A teaching return prepared before a reader-side disarm reached the command transport");
        }
        finally
        {
            await lifetime.CancelAsync(); await feeding.CancelAsync();
            try { await producer; } catch (OperationCanceledException) { }
            try { await watching; } catch (OperationCanceledException) { }

            // Keep the shared recovery worker pending until we can verify it is drained.
            using var recoveryCancellation = new CancellationTokenSource();
            var recoveryCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveryFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = recoveryCancellation.Token.Register(() => recoveryCancelled.TrySetResult());
            var shutdown = Get<CancellationTokenSource>("shutdown");
            var recovery = new PipersPlugin.RecoveryOperation(PipersPlugin.RecoveryKind.ReturnToZero, commandScope,
                authority.Epoch, recoveryCancellation) { Worker = recoveryFinished.Task };
            Set("recoveryOperation", recovery);
            Task disposal = plugin.DisposeAsync().AsTask();
            try
            {
                await recoveryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                check(!disposal.IsCompleted && !shutdown.IsCancellationRequested,
                    "Plugin shutdown advanced before the active recovery worker drained");
            }
            finally
            {
                recoveryFinished.TrySetResult();
                await disposal;
            }
            check(shutdown.IsCancellationRequested, "Disposal did not finish shutdown after recovery drained");
        }
        await PikaShutdownTests.Run(check);
    }

    private sealed class FakeCalls(ChannelReader<StateFrame> frames) : CallInvoker
    {
        public int Heartbeats;
        public System.Collections.Concurrent.ConcurrentBag<string> Routes { get; } = [];
        public TaskCompletionSource<ControlReply>? DelayedCommand;
        public TaskCompletionSource CommandReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (request is not ControlRequest command) throw new InvalidOperationException("Unexpected fixture unary method");
            Routes.Add(command.Route);
            if (command.Route == "/api/heartbeat") Interlocked.Increment(ref Heartbeats);
            Task<TResponse> response;
            if (command.Route == "/api/command" && DelayedCommand is { } delayed)
            {
                CommandReached.TrySetResult();
                response = delayed.Task.ContinueWith(result => (TResponse)(object)result.GetAwaiter().GetResult(), TaskScheduler.Default);
            }
            else response = Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{\"ok\":true}" });
            return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            new(new Frames<TResponse>(frames), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class Frames<T>(ChannelReader<StateFrame> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await reader.WaitToReadAsync(cancellationToken))
                if (reader.TryRead(out var value)) { Current = (T)(object)value; return true; }
            return false;
        }
    }
}
