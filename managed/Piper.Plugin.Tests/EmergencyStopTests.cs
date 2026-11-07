using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class EmergencyStopTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            await fixture.Commands.WaitAsync();
            long before = fixture.Get<long>("targetPermissionEpoch");
            fixture.Set("approved", true); fixture.Set("lease", 17L);
            using var cancelledCaller = new CancellationTokenSource(); cancelledCaller.Cancel();
            try
            {
                await fixture.Event("robot.piper-1.stop", cancelledCaller.Token).WaitAsync(TimeSpan.FromSeconds(1));
                check(fixture.Commands.CurrentCount == 0 && fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated" }),
                    "Priority STOP waited for or released the ordinary UI command semaphore");
                check(fixture.Rpc.RequestTokensWereLive && fixture.Get<long>("targetPermissionEpoch") > before &&
                    !fixture.Get<bool>("approved") && fixture.Get<long>("lease") == 0,
                    "Caller cancellation or an old permission/lease defeated a requested STOP");
                check(fixture.Get<string>("message").Contains("Arm STOP acknowledged") && fixture.Get<string>("message").Contains("Pika hold unverified"),
                    "Partial coordinated STOP reported failure of one channel as failure of both, or claimed an unverified gripper hold");
            }
            finally { fixture.Commands.Release(); }
        }

        foreach (string wire in new[] { "robot.other.stop", "stop", "view.999.robot.piper-1.stop" })
        {
            using var fixture = new Fixture();
            await fixture.Commands.WaitAsync();
            long before = fixture.Get<long>("targetPermissionEpoch");
            Task foreign = fixture.Event(wire, default);
            await Task.Delay(20);
            check(fixture.Rpc.Routes.IsEmpty && fixture.Get<long>("targetPermissionEpoch") == before,
                "A foreign, unscoped or malformed STOP entered the priority hardware path: " + wire);
            fixture.Commands.Release();
            await foreign.WaitAsync(TimeSpan.FromSeconds(1));
            check(fixture.Rpc.Routes.IsEmpty, "A rejected STOP reached transport after the UI gate became available");
        }

        using (var fixture = new Fixture())
        {
            fixture.Rpc.DelayConnect = true;
            fixture.Set("approved", true);
            await fixture.Commands.WaitAsync();
            Task enable = fixture.Invoke("Enable", CancellationToken.None, true, null, null, null);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                bool rejected = false;
                try { await enable; } catch (InvalidOperationException) { rejected = true; }
                check(rejected && !fixture.Rpc.Routes.Contains("/api/pika/enable") && !fixture.Get<bool>("approved"),
                    "A delayed Enable continuation powered Pika after priority STOP invalidated its permission epoch");
                check(fixture.Rpc.Routes.Contains("/api/stop/coordinated"), "Blocked Enable suppressed the real priority STOP request");
            }
            finally
            {
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                fixture.Commands.Release();
            }
        }

        using (var fixture = new Fixture())
        {
            fixture.Rpc.CoordinatedMissing = true;
            await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
            check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated", "/api/stop" }),
                "Missing coordinated-stop support prevented the independent arm STOP fallback");
            check(fixture.Get<string>("message").Contains("Arm STOP acknowledged") && fixture.Get<string>("message").Contains("gripper state unverified"),
                "Legacy fallback claimed a verified Pika hold");
        }
        foreach (StatusCode status in new[] { StatusCode.InvalidArgument, StatusCode.Unimplemented })
        {
            using var fixture = new Fixture();
            fixture.Rpc.CoordinatedRpcFailure = status;
            await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
            check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated", "/api/stop" }),
                "A legacy gRPC coordinator rejection prevented the arm STOP fallback: " + status);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin = new();
        public FakeCalls Rpc { get; } = new();
        public SemaphoreSlim Commands => Get<SemaphoreSlim>("commands");
        public T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, Fields)!.GetValue(plugin)!;
        public void Set(string field, object? value) => typeof(PipersPlugin).GetField(field, Fields)!.SetValue(plugin, value);
        public Task Invoke(string method, params object?[] arguments) => (Task)typeof(PipersPlugin).GetMethod(method, Fields)!.Invoke(plugin, arguments)!;
        public Task Event(string wire, CancellationToken cancellation) => plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), cancellation);

        public Fixture()
        {
            Set("client", new PiperControl.PiperControlClient(Rpc));
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots:
                [new("piper-1", "Fixture", "can-one", PikaDevice: "/dev/pika-one")], ActiveRobotId: "piper-1")));
            Set("token", "fake-backend-session");
            var snapshot = new JsonObject
            {
                ["token"] = "fake-backend-session", ["selected"] = "can-one", ["motion"] = new JsonObject { ["active"] = false, ["request_id"] = 0L },
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "can-one", ["name"] = "Fixture", ["generation"] = 1L, ["present"] = true, ["up"] = true, ["fresh"] = true,
                    ["recovery_required"] = false, ["reset_pending"] = false, ["blocker"] = "", ["firmware_supported"] = true,
                    ["status"] = new JsonObject { ["control"] = 1, ["teach_state"] = 0, ["arm_status"] = 0, ["error"] = 0, ["age_s"] = .001 },
                    ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
                    {
                        ["index"] = i, ["fresh"] = true, ["enabled"] = true, ["fault"] = 0, ["actual_deg"] = 0.0,
                        ["age_s"] = .001, ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
                    }).ToArray())
                }),
                ["pika"] = new JsonObject
                {
                    ["configured"] = true, ["device"] = "/dev/pika-one", ["distribution"] = "Ubuntu", ["owner"] = Get<string>("owner"),
                    ["fresh"] = true, ["connected"] = true, ["armed"] = true, ["enabled"] = true, ["pending"] = false,
                    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = .1, ["Speed"] = 0.0 } }
                }
            };
            Set("state", snapshot);
            Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "fake-backend-session", 1, Stopwatch.GetTimestamp(), 0));
            var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), Fields, null,
                [new PluginStart("fixture", Path.Combine(Path.GetTempPath(), "piper-emergency-" + Guid.NewGuid().ToString("N")), Path.GetTempPath()),
                    (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })))], null)!;
            Set("engine", context);
        }

        public void Dispose()
        {
            // StartAsync is never called: no workers or cameras exist. Dispose
            // only the inert channel/gate, without invoking shutdown commands.
            ((IDisposable)typeof(PipersPlugin).GetField("channel", Fields)!.GetValue(plugin)!).Dispose();
            Commands.Dispose();
        }
    }

    internal sealed class FakeCalls : CallInvoker
    {
        public ConcurrentQueue<string> Routes { get; } = new();
        public bool DelayConnect, CoordinatedMissing;
        public StatusCode? CoordinatedRpcFailure;
        public bool RequestTokensWereLive { get; private set; } = true;
        public TaskCompletionSource ConnectReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlReply> ConnectReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fake RPC");
            Routes.Enqueue(command.Route); RequestTokensWereLive &= !options.CancellationToken.IsCancellationRequested;
            Task<ControlReply> reply;
            if (command.Route == "/api/pika/connect" && DelayConnect)
            { ConnectReached.TrySetResult(); reply = ConnectReply.Task; }
            else if (command.Route == "/api/stop/coordinated" && CoordinatedRpcFailure is StatusCode failure)
                reply = Task.FromException<ControlReply>(new RpcException(new Status(failure, "Synthetic legacy unsupported route")));
            else reply = Task.FromResult(command.Route == "/api/stop/coordinated"
                ? new ControlReply { Status = CoordinatedMissing ? 404 : 409, Json = "{\"arm\":{\"ok\":true},\"pika\":{\"ok\":false}}" }
                : new ControlReply { Status = 200, Json = "{\"ok\":true}" });
            var response = reply.ContinueWith(result => (TResponse)(object)result.GetAwaiter().GetResult(), TaskScheduler.Default);
            return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
