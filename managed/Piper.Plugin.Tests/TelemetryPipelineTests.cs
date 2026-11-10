using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class TelemetryPipelineTests
{
    public static async Task Run()
    {
        long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, ticks = Stopwatch.GetTimestamp();
        long At(double ms) => ticks + (long)(Stopwatch.Frequency * ms / 1000);
        JsonObject State(string token = "session-a", long generation = 7)
        {
            var arm = Fixtures.Arm(generation: generation, ageSeconds: .005, joint: (_, joint) => joint["current_a"] = 0.0);
            arm["status"]!["motion_mode"] = 1;
            return Fixtures.Snapshot(token, arm, motion: new JsonObject { ["active"] = true, ["request_id"] = 7L, ["owner"] = "fixture-owner" });
        }
        var inbox = new LatestTelemetry();
        bool Prepare(JsonObject state, long sequence, long source, string clock, long now, long monotonic, out TelemetryReceipt? receipt) =>
            inbox.TryPrepare(state, sequence, source, clock, now, monotonic, out receipt, out _);
        Check(Prepare(State(), 1, utc - 100_000, "host-utc", utc, ticks, out var first), "Fresh source-stamped telemetry was rejected");
        inbox.Publish(first!);
        Check(first!.IsFresh(At(400)) && !first.IsFresh(At(401)), "Source age plus receipt age did not enforce the unchanged 500 ms window");
        var agedDevice = State()["devices"]![0]!.AsObject();
        Check(first.HasFreshDevice(agedDevice, At(300)), "Valid device sample age plus transport age was rejected");
        agedDevice["joints"]![2]!["age_s"] = .25;
        Check(!first.HasFreshDevice(agedDevice, At(300)), "Fresh-at-source joint flags hid a sample older than 500 ms after transport delay");
        agedDevice["joints"]![2]!["age_s"] = .005; agedDevice["status"]!["age_s"] = .25;
        Check(!first.HasFreshDevice(agedDevice, At(300)), "Fresh-at-source status hid accumulated transport delay");
        agedDevice["status"]!["age_s"] = double.NaN;
        Check(!first.HasFreshDevice(agedDevice, ticks), "An invalid sample age passed command freshness");
        var ackReceipt = new TelemetryReceipt(State(), "session-a", 3, ticks, 0);
        var ackDevice = State()["devices"]![0]!.AsObject();
        ackDevice["status"]!["age_s"] = .45; ackDevice["joints"]![4]!["age_s"] = .45;
        Check(ackReceipt.HasFreshDevice(ackDevice, ticks) && ackReceipt.IsFresh(At(300)), "Acknowledgement fixture did not isolate source-sample age from wrapper age");
        Check(Math.Abs(ackReceipt.DeviceEvidenceAgeMilliseconds(ackDevice, At(300)) - 750) < .001 && !ackReceipt.HasFreshDevice(ackDevice, At(300)),
            "A 450ms source sample plus 300ms editor wait was accepted/stamped as a fresh acknowledgement");
        var pikaState = State();
        var pika = pikaState["pika"]!.AsObject();
        pika["connected"] = true; pika["fresh"] = true; pika["age_s"] = .15;
        var pikaReceipt = new TelemetryReceipt(pikaState, "session-a", 4, ticks, 100);
        double pikaDeadline = SimulationFollowPolicy.MaximumGripperFeedbackAgeMilliseconds;
        Check(Math.Abs(pikaReceipt.PikaEvidenceAgeMilliseconds(ticks) - 250) < .001 && pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline),
            "Pika age did not include its source sample age and envelope delay at the 250ms boundary");
        Check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, At(1), pikaDeadline), "An aged Pika receipt renewed its 250ms evidence window");
        pika["age_s"] = .3;
        Check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A true Pika fresh flag hid a sample too old for physical following");
        foreach (JsonNode? invalidAge in new JsonNode?[] { null, JsonValue.Create(-.001), JsonValue.Create(double.NaN), JsonValue.Create(double.PositiveInfinity), JsonValue.Create("0.01") })
        {
            pika["age_s"] = invalidAge;
            Check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "Missing or invalid Pika source age authorized following");
        }
        pika["age_s"] = .001; pika["connected"] = false;
        Check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "Disconnected Pika feedback authorized following");
        pika["connected"] = true; pika["fresh"] = false;
        Check(!pikaReceipt.HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A false Pika fresh flag authorized following");
        pika["fresh"] = true;
        Check(!(pikaReceipt with { Connected = false }).HasFreshPika(pikaReceipt.State["pika"] as JsonObject, ticks, pikaDeadline), "A disconnected telemetry envelope authorized Pika following");
        Check(!Prepare(State(), 2, utc - 501_000, "host-utc", utc, ticks, out _), "A queued old frame was stamped fresh on arrival");
        Check(ReferenceEquals(inbox.Latest, first), "Rejecting an aged frame replaced the last verified receipt");
        Check(!Prepare(State(), 2, utc + 101_000, "host-utc", utc, ticks, out _), "An unbounded future source clock passed freshness checks");
        Check(!Prepare(State(), 2, utc, "unknown", utc, ticks, out _), "Unverified source clock passed freshness checks");
        Check(!Prepare(State(), 0, utc, "host-utc", utc, ticks, out _), "A missing sequence passed freshness checks");
        Check(!Prepare(State(), 1, utc, "host-utc", utc, ticks, out _), "Duplicate sequence replaced current feedback");
        Check(!Prepare(State(), 0, utc, "host-utc", utc, ticks, out _), "A reversed sequence replaced current feedback");
        Check(Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out var restarted), "A new backend could not establish a fresh sequence");
        inbox.Publish(restarted!); inbox.Disconnect();
        Check(inbox.Latest?.IsFresh(ticks) == false, "An interrupted stream retained command freshness");
        Check(Prepare(State("session-b"), 10000, utc, "host-utc", utc, ticks, out var highSequence), "High same-stream sequence was rejected");
        inbox.Publish(highSequence!); inbox.Disconnect(); inbox.BeginStream();
        Check(inbox.Latest?.IsFresh(ticks) == false, "Beginning a stream renewed an old disconnected receipt");
        Check(!Prepare(State("session-b"), 1, utc - 600_000, "host-utc", utc, ticks, out _), "New stream epoch bypassed source-age validation");
        Check(Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out var reconnected), "Same-backend stream reconnect waited for the old sequence count");
        inbox.Publish(reconnected!);
        Check(!Prepare(State("session-b"), 1, utc, "host-utc", utc, ticks, out _), "A duplicate in the reconnected stream was accepted");
        Check(TelemetryScope.Read(State(), "can-a") != TelemetryScope.Read(State(generation: 8), "can-a"), "CAN reconnection inherited old identity scope");
        Check(TelemetryScope.Read(State(), "can-a") != TelemetryScope.Read(State("session-b"), "can-a"), "Backend restart inherited old identity scope");
        Check(TelemetryScope.Read(State(), "can-b") is null, "A foreign CAN selection was accepted as the assigned robot");

        // Exercise the real four-loop plugin with an in-memory gRPC stream and a
        // deliberately slow fake editor. No socket, camera, CAN or Pika is opened.
        var plugin = new PipersPlugin();
        var pipe = Channel.CreateBounded<StateFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var rpc = new FakeCalls(pipe.Reader);
        plugin.client = new PiperControl.PiperControlClient(rpc);
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new RobotProfile("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1"));
        plugin.preferences = prefs; plugin.token = "session-a";
        plugin.telemetryScope = new TelemetryScope("session-a", "can-a", 7);
        string sessionOwner = plugin.owner;
        var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
        var device = State()["devices"]![0]!.AsObject();
        var binding = new RobotLink("fixture-scene", "fixture-robot", model, device);
        var authority = plugin.CommandAuthority;
        var commandScope = new RobotCommandScope("piper-1", "session-a", "can-a", 7);
        plugin.link = binding; plugin.linkBackendToken = "session-a"; plugin.approved = true; plugin.linkCommandsEnabled = true;
        authority.TryAcknowledge(authority.Epoch, commandScope, 7);
        int sceneRequests = 0, editorMode = 0;
        var context = Fixtures.Context(new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
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
                }));
        plugin.engine = context;
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
        var watching = plugin.Watch(lifetime.Token);
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
            var current = plugin.telemetry.Latest!;
            Check(current.IsFresh(Stopwatch.GetTimestamp()) && current.Sequence >= 5, "Slow editor requests stopped fresh state consumption");
            Check(rpc.Heartbeats >= 3, "Slow editor requests stopped controller heartbeats");
            Check(ReferenceEquals(plugin.link, binding), "A rendering timeout destroyed a healthy same-scope scene binding");
            Check(plugin.state is not null && plugin.token == "session-a", "A rendering timeout was classified as controller disconnection");
            Check(!plugin.linkCommandsEnabled, "A failed scene acknowledgement automatically armed viewport targets");
            int beforeModeChange = Volatile.Read(ref sceneRequests);
            Volatile.Write(ref editorMode, 1);
            await Until(() => Volatile.Read(ref sceneRequests) > beforeModeChange && plugin.sceneTrackingStatus.StartsWith("Measured scene update deferred", StringComparison.Ordinal),
                "The slow fake request did not drain before the leader/ghost check");
            plugin.viewportDrag = new ViewportDrag(ViewportDragPhase.Following, Request: 7); plugin.linkCommandsEnabled = true;
            authority.TryAcknowledge(authority.Epoch, commandScope, 7);
            int beforeGhost = Volatile.Read(ref sceneRequests);
            await Until(() => Volatile.Read(ref sceneRequests) > beforeGhost && plugin.sceneTrackingStatus.StartsWith("Leader target + measured ghost", StringComparison.Ordinal), "Native leader/ghost feedback was not recognized");
            Check(plugin.viewportDrag.Phase != ViewportDragPhase.Off && plugin.linkCommandsEnabled && plugin.viewportDrag is { Phase: ViewportDragPhase.Following, Request: 7 } && authority.Current.Lease?.Request == 7,
                "Expected native applied:false leader/ghost feedback stopped a valid drag or finishing target");
            Check(plugin.acknowledgedScenePose is null,
                "Leader/ghost feedback was incorrectly promoted to a calibration pose acknowledgement");
            Volatile.Write(ref editorMode, 2);
            await Until(() => plugin.viewportDrag.Phase == ViewportDragPhase.Off, "A remote teleoperation stop did not pause local following");
            Check(!plugin.linkCommandsEnabled && plugin.viewportDrag.Phase != ViewportDragPhase.Following && authority.Current.Lease is null,
                "Unexpected remote editor deferral kept viewport targets armed");
            await feeding.CancelAsync(); await producer;
            await Until(() => !plugin.approved && authority.Current.Lease is null, "Real telemetry loss did not disarm commands/deadlines", 1500);
            Check(!plugin.telemetry.Latest!.IsFresh(Stopwatch.GetTimestamp()), "Genuine telemetry loss remained fresh");
            Check(ReferenceEquals(plugin.link, binding), "A genuine gap discarded a same-identity read-only binding instead of pausing it");
            Feed();
            await Until(() => plugin.telemetry.Latest!.IsFresh(Stopwatch.GetTimestamp()), "Same-scope fresh telemetry did not resume reading");
            Check(!plugin.approved && !plugin.linkCommandsEnabled, "Fresh telemetry silently rearmed old target permission");
            currentGeneration = 8; Feed();
            await Until(() => plugin.link is null, "A new CAN generation inherited the previous physical scene binding");
            Check(!plugin.approved, "CAN generation change retained target permission");
            var rebound = new RobotLink("fixture-scene", "fixture-robot", model, State(generation: 8)["devices"]![0]!.AsObject());
            plugin.link = rebound; plugin.linkBackendToken = "session-a"; plugin.approved = true;
            currentToken = "session-b"; Feed();
            await Until(() => plugin.link is null && plugin.token == "session-b", "Backend session change inherited old link/authorization");
            Check(!plugin.approved, "Backend session change retained target permission");
            Check(rpc.Routes.All(route => route == "/api/heartbeat"), "Telemetry/render recovery issued a power, target, reset or configuration action");

            // A response to an action prepared before telemetry loss must not
            // recreate the discarded lease after fresh feedback returns.
            plugin.approved = true; Feed();
            await Until(() => plugin.telemetry.Latest!.BackendToken == "session-b" && plugin.telemetry.Latest!.IsFresh(Stopwatch.GetTimestamp()), "Fixture did not restore fresh input for acknowledgement test");
            rpc.DelayedCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = plugin.Command("jog", new JsonObject { ["joint"] = 6, ["delta_deg"] = 1.0 }, lifetime.Token, CommandPurpose.Control, null, null, null);
            await Until(() => rpc.CommandReached.Task.IsCompleted, "Synthetic authorized action did not reach the fake transport");
            plugin.DisarmTelemetryTargets("Fixture telemetry loss", false, null, false);
            rpc.DelayedCommand.SetResult(new ControlReply { Status = 202, Json = "{\"request_id\":99,\"message\":\"accepted\"}" });
            bool discarded = false;
            try { await pending; } catch (InvalidOperationException) { discarded = true; }
            Check(discarded && authority.Current is { Lease: null, LastCommand: null },
                "A delayed old acknowledgement renewed its discarded operation after telemetry loss");
            int sentBefore = rpc.Routes.Count;
            long previousPermission = authority.Epoch - 1;
            pending = plugin.Command("teach-return", new JsonObject { ["button_off"] = true, ["same_pose_confirmed"] = true }, lifetime.Token,
                    CommandPurpose.TeachingReturn, null, previousPermission, null);
            discarded = false;
            try { await pending; } catch (InvalidOperationException) { discarded = true; }
            Check(discarded && rpc.Routes.Count == sentBefore,
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
            var shutdown = plugin.shutdown;
            var recovery = new PipersPlugin.RecoveryOperation(PipersPlugin.RecoveryKind.ReturnToZero, commandScope,
                authority.Epoch, recoveryCancellation) { Worker = recoveryFinished.Task };
            plugin.recoveryOperation = recovery;
            Task disposal = plugin.DisposeAsync().AsTask();
            try
            {
                await recoveryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check(!disposal.IsCompleted && !shutdown.IsCancellationRequested,
                    "Plugin shutdown advanced before the active recovery worker drained");
            }
            finally
            {
                recoveryFinished.TrySetResult();
                await disposal;
            }
            Check(shutdown.IsCancellationRequested, "Disposal did not finish shutdown after recovery drained");
        }
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
