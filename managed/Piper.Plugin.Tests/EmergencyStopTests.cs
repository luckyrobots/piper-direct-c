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
        foreach (string invalidation in new[] { "cancel", "stop", "profile", "generation" })
        {
            using var fixture = new Fixture();
            await fixture.Plugin.Render(CancellationToken.None);
            string Wire(string action) => WorkspaceEventScope.Wrap(fixture.Plugin.workspaceViewEpoch, "piper-1", action);
            await fixture.Event(Wire("safety.accept"), default);
            check(!fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "An Enable acknowledgement without a presented prompt reached the hardware transport");
            await fixture.Event(Wire("enable"), default);
            var opened = fixture.Authority.Current.Confirmation;
            string oldAccept = Wire("safety.accept");
            check(opened is not null && !fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "Opening a clearance prompt approved or sent an action");
            switch (invalidation)
            {
                case "cancel":
                    await fixture.Event(Wire("safety.cancel"), default);
                    break;
                case "stop":
                    await fixture.Event("robot.piper-1.stop", default);
                    break;
                case "profile":
                    var preferences = fixture.Plugin.preferences;
                    var second = new RobotProfile("piper-2", "Second", "can-two");
                    fixture.Plugin.preferences = RobotProfileRouting.Activate(preferences with { Robots = [.. preferences.Robots!, second] }, second.Id);
                    await fixture.Plugin.Render(CancellationToken.None);
                    break;
                case "generation":
                    const string backend = "fake-backend-session";
                    var telemetry = fixture.Get<LatestTelemetry>("telemetry");
                    var first = telemetry.Latest!;
                    var next = first.State.DeepClone().AsObject();
                    next["devices"]![0]!["generation"] = 2L;
                    var replacementReceipt = first with { State = next, Sequence = 2 };
                    fixture.Set("telemetryScope", new TelemetryScope(backend, "can-one", 1));
                    fixture.InvokeSync("ApplyIncomingScope", replacementReceipt);
                    fixture.Plugin.state = next;
                    telemetry.Publish(replacementReceipt);
                    break;
            }
            await fixture.Event(oldAccept, default);
            check(fixture.Authority.Current.Confirmation is null && !fixture.Plugin.approved &&
                fixture.Rpc.Routes.All(route => route == "/api/stop/coordinated"),
                "An old workspace confirmation survived " + invalidation);
            if (invalidation != "cancel") continue;
            await fixture.Event(Wire("enable"), default);
            var reopened = fixture.Authority.Current.Confirmation;
            await fixture.Event(oldAccept, default);
            check(reopened is not null && !ReferenceEquals(opened, reopened) &&
                ReferenceEquals(reopened, fixture.Authority.Current.Confirmation) && !fixture.Plugin.approved && fixture.Rpc.Routes.IsEmpty,
                "A delayed Accept consumed a newly opened prompt");
        }

        var authority = new RobotCommandAuthority();
        var original = new RobotCommandScope("piper-1", "original-backend", "can-one", 1);
        long originalEpoch = authority.Epoch;
        check(authority.TryAcknowledge(originalEpoch, original, 17), "A current acknowledgement could not publish its lease");
        var previous = authority.Current.Lease!;
        long nextEpoch = authority.Revoke();
        var replacement = original with { Backend = "replacement-backend", Generation = 2 };
        check(!authority.TryAcknowledge(originalEpoch, original, 17) && authority.Current is { Lease: null, LastCommand: null },
            "An acknowledgement for a retired epoch republished a command");
        check(authority.TryAcknowledge(nextEpoch, replacement, 17), "A new scoped request could not reuse the controller's request number");
        authority.EndLease(previous);
        authority.EndStatus(previous);
        check(authority.Current.Lease?.Scope == replacement && authority.Current.LastCommand?.Scope == replacement,
            "Retiring the old command cleared a different connection's reused request number");
        var approved = new FollowControlApproval(replacement.Backend, replacement.Device, replacement.Generation, "sim", "", "pika-a", "");
        var partner = approved with { Device = "can-two", PikaDevice = "pika-b" };
        check(authority.TryOpenConfirmation(nextEpoch, "piper-1", approved), "Current authority could not open a workspace confirmation");
        var confirmation = authority.Current.Confirmation!;
        int activations = 0;
        check(!authority.TryConsumeConfirmation(confirmation, "piper-2", partner, () => activations++) &&
            authority.TryConsumeConfirmation(confirmation, "piper-1", approved, () => activations++) &&
            !authority.TryConsumeConfirmation(confirmation, "piper-1", approved, () => activations++) && activations == 1,
            "Workspace clearance was accepted for another scope or consumed more than once");
        check(authority.TrySaveApproval(nextEpoch, "piper-1", approved) && authority.TrySaveApproval(nextEpoch, "piper-2", partner),
            "Approvals for independent robots could not coexist");
        authority.Revoke("piper-1");
        bool restored = false;
        check(!authority.TrySaveApproval(nextEpoch, "piper-1", approved) &&
            !authority.TryRestoreApproval(authority.Epoch, "piper-1", approved, () => restored = true) &&
            !restored && authority.Current.Approvals.TryGetValue("piper-2", out var retained) && retained == partner,
            "A revoked robot approval returned, or revocation removed a different robot's approval");

        using (var fixture = new Fixture())
        {
            const string backend = "fake-backend-session";
            var telemetry = fixture.Get<LatestTelemetry>("telemetry");
            var first = telemetry.Latest!;
            var profile = RobotProfileRouting.Active(fixture.Plugin.preferences);
            fixture.Set("telemetryScope", new TelemetryScope(backend, "can-one", 1));
            fixture.Authority.TrySaveApproval(fixture.Authority.Epoch, profile.Id,
                new(backend, "can-one", 1, profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution));
            fixture.Plugin.approved = true;
            var next = first.State.DeepClone().AsObject();
            next["devices"]![0]!["generation"] = 2L;
            var replacementReceipt = first with { State = next, Sequence = 2 };
            fixture.InvokeSync("ApplyIncomingScope", replacementReceipt);
            fixture.InvokeSync("RestoreControlApproval");
            check(telemetry.Latest?.Connected == false && !fixture.Plugin.approved,
                "Old measured evidence restored approval between scope revocation and telemetry publication");
            fixture.Plugin.state = next;
            telemetry.Publish(replacementReceipt);
            fixture.InvokeSync("RestoreControlApproval");
            check(!fixture.Plugin.approved && fixture.Authority.Current.Approvals.Count == 0,
                "The replacement CAN generation inherited the old approval");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Plugin.Render(CancellationToken.None);
            var preferences = fixture.Plugin.preferences;
            var captured = RobotProfileRouting.Active(preferences);
            var second = new RobotProfile("piper-2", "Second", "can-two", PikaDevice: "/dev/pika-two");
            fixture.Plugin.preferences = RobotProfileRouting.Activate(preferences with { Robots = [captured, second] }, second.Id);
            await fixture.Event("robot.piper-1.stop", default);
            var body = JsonNode.Parse(fixture.Rpc.RequestBodies.Single());
            check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated" }) &&
                body?["device_id"]?.GetValue<string>() == "can-one" && body?["pika_device"]?.GetValue<string>() == "/dev/pika-one",
                "STOP mixed captured arm/gripper identity with a newly selected profile");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Commands.WaitAsync();
            long before = fixture.Authority.Epoch;
            fixture.Plugin.approved = true;
            fixture.Authority.TryAcknowledge(before, new("piper-1", "fake-backend-session", "can-one", 1), 17);
            using var cancelledCaller = new CancellationTokenSource(); cancelledCaller.Cancel();
            try
            {
                await fixture.Event("robot.piper-1.stop", cancelledCaller.Token).WaitAsync(TimeSpan.FromSeconds(1));
                check(fixture.Commands.CurrentCount == 0 && fixture.Rpc.Routes.SequenceEqual(new[] { "/api/stop/coordinated" }),
                    "Priority STOP waited for or released the ordinary UI command semaphore");
                check(fixture.Rpc.RequestTokensWereLive && fixture.Authority.Epoch > before &&
                    !fixture.Plugin.approved && fixture.Authority.Current.Lease is null,
                    "Caller cancellation or an old permission/lease defeated a requested STOP");
                check(fixture.Plugin.message.Contains("Arm STOP acknowledged") && fixture.Plugin.message.Contains("Pika hold unverified"),
                    "Partial coordinated STOP reported failure of one channel as failure of both, or claimed an unverified gripper hold");
            }
            finally { fixture.Commands.Release(); }
        }

        foreach (string wire in new[] { "robot.other.stop", "stop", "view.999.robot.piper-1.stop" })
        {
            using var fixture = new Fixture();
            await fixture.Commands.WaitAsync();
            long before = fixture.Authority.Epoch;
            Task foreign = fixture.Event(wire, default);
            await Task.Delay(20);
            check(fixture.Rpc.Routes.IsEmpty && fixture.Authority.Epoch == before,
                "A foreign, unscoped or malformed STOP entered the priority hardware path: " + wire);
            fixture.Commands.Release();
            await foreign.WaitAsync(TimeSpan.FromSeconds(1));
            check(fixture.Rpc.Routes.IsEmpty, "A rejected STOP reached transport after the UI gate became available");
        }

        using (var fixture = new Fixture())
        {
            fixture.Plugin.approved = true;
            fixture.Rpc.DelayCommand = true;
            Task pending = fixture.Plugin.Command("jog", new JsonObject { ["joint"] = 6, ["delta_deg"] = 1.0 },
                CancellationToken.None, CommandPurpose.Control, null, null, null);
            try
            {
                await fixture.Rpc.CommandReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Rpc.CommandReply.TrySetResult(new ControlReply { Status = 202, Json = "{\"request_id\":17}" });
                bool rejected = false;
                try { await pending; } catch (InvalidOperationException) { rejected = true; }
                check(rejected && fixture.Authority.Current is { Lease: null, LastCommand: null } &&
                    fixture.Rpc.Routes.SequenceEqual(new[] { "/api/command", "/api/stop/coordinated" }),
                    "A motion acknowledgement received after priority STOP revived its local lease or retried the command");
            }
            finally { fixture.Rpc.CommandReply.TrySetCanceled(); }
        }

        using (var fixture = new Fixture())
        {
            fixture.Rpc.DelayConnect = true;
            fixture.Plugin.approved = true;
            await fixture.Commands.WaitAsync();
            Task enable = fixture.Plugin.Enable(CancellationToken.None, true, null, null);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await fixture.Event("robot.piper-1.stop", default).WaitAsync(TimeSpan.FromSeconds(1));
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                bool rejected = false;
                try { await enable; } catch (InvalidOperationException) { rejected = true; }
                check(rejected && !fixture.Rpc.Routes.Contains("/api/pika/enable") && !fixture.Plugin.approved,
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
            check(fixture.Plugin.message.Contains("Arm STOP acknowledged") && fixture.Plugin.message.Contains("gripper state unverified"),
                "Legacy fallback claimed a verified Pika hold");
        }
        foreach (string change in new[] { "physical STOP", "hand guiding" })
        {
            using var fixture = new Fixture();
            fixture.Rpc.DelayConnect = true;
            await fixture.Plugin.Render(CancellationToken.None);
            string Wire(string action) => WorkspaceEventScope.Wrap(fixture.Plugin.workspaceViewEpoch, "piper-1", action);
            await fixture.Event(Wire("enable"), default);
            Task accept = fixture.Event(Wire("safety.accept"), default);
            try
            {
                await fixture.Rpc.ConnectReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var telemetry = fixture.Get<LatestTelemetry>("telemetry");
                var receipt = telemetry.Latest!;
                var changed = receipt.State.DeepClone().AsObject();
                var status = changed["devices"]![0]!["status"]!;
                if (change == "physical STOP") status["arm_status"] = 1;
                else status["teach_state"] = 1;
                var newReceipt = receipt with { State = changed, Sequence = 2 };
                fixture.InvokeSync("ApplyIncomingScope", newReceipt);
                telemetry.Publish(newReceipt);
                fixture.Plugin.state = changed;
                fixture.Rpc.ConnectReply.TrySetResult(new ControlReply { Status = 200, Json = "{\"ok\":true}" });
                await accept.WaitAsync(TimeSpan.FromSeconds(2));
                check(fixture.Rpc.Routes.SequenceEqual(new[] { "/api/pika/connect" }) &&
                    fixture.Authority.Current.Confirmation is null && fixture.Authority.Current.Approvals.Count == 0,
                    "A delayed Enable powered Pika after fresh feedback showed " + change);
            }
            finally { fixture.Rpc.ConnectReply.TrySetCanceled(); }
        }
        using (var fixture = new Fixture())
        {
            var telemetry = fixture.Get<LatestTelemetry>("telemetry");
            var receipt = telemetry.Latest!;
            var snapshot = receipt.State.DeepClone().AsObject();
            var gripper = snapshot["pika"]!.AsObject();
            gripper["aliases"] = new JsonArray("/dev/pika-one");
            gripper["device"] = "/dev/pika/by-host/fixture";
            snapshot["pikas"] = new JsonArray(gripper.DeepClone());
            fixture.Plugin.state = snapshot;
            telemetry.Publish(receipt with { State = snapshot, Sequence = 2 });
            fixture.Plugin.approved = true;
            fixture.Plugin.SetPikaRole(PikaSessionRole.Owned);
            await fixture.Plugin.Enable(CancellationToken.None, true, null, null);
            check(fixture.Rpc.Routes.IsEmpty && fixture.Authority.Current.Approvals.Count == 1,
                "An assigned Pika alias was found in telemetry but Enable reconfigured the already-owned canonical gripper");
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
        public PipersPlugin Plugin => plugin;
        public FakeCalls Rpc { get; } = new();
        public RobotCommandAuthority Authority => plugin.CommandAuthority;
        public SemaphoreSlim Commands => plugin.commands;
        public T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, Fields)!.GetValue(plugin)!;
        public void Set(string field, object? value) => typeof(PipersPlugin).GetField(field, Fields)!.SetValue(plugin, value);
        public Task Invoke(string method, params object?[] arguments) => (Task)typeof(PipersPlugin).GetMethod(method, Fields)!.Invoke(plugin, arguments)!;
        public void InvokeSync(string method, params object?[] arguments) => typeof(PipersPlugin).GetMethod(method, Fields)!.Invoke(plugin, arguments);
        public Task Event(string wire, CancellationToken cancellation) => plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), cancellation);

        public Fixture()
        {
            plugin.client = new PiperControl.PiperControlClient(Rpc);
            plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
                [new("piper-1", "Fixture", "can-one", PikaDevice: "/dev/pika-one")], ActiveRobotId: "piper-1"));
            plugin.token = "fake-backend-session";
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
                    ["configured"] = true, ["device"] = "/dev/pika-one", ["distribution"] = "Ubuntu", ["owner"] = plugin.owner,
                    ["fresh"] = true, ["connected"] = true, ["armed"] = true, ["enabled"] = true, ["pending"] = false,
                    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = .1, ["Speed"] = 0.0 } }
                }
            };
            plugin.state = snapshot;
            Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "fake-backend-session", 1, Stopwatch.GetTimestamp(), 0));
            var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), Fields, null,
                [new PluginStart("fixture", Path.Combine(Path.GetTempPath(), "piper-emergency-" + Guid.NewGuid().ToString("N")), Path.GetTempPath()),
                    (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })))], null)!;
            plugin.engine = context;
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
        public ConcurrentQueue<string> RequestBodies { get; } = new();
        public bool DelayConnect, DelayCommand, CoordinatedMissing;
        public StatusCode? CoordinatedRpcFailure;
        public bool RequestTokensWereLive { get; private set; } = true;
        public TaskCompletionSource ConnectReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlReply> ConnectReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ControlReply> CommandReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fake RPC");
            Routes.Enqueue(command.Route); RequestBodies.Enqueue(command.Json);
            RequestTokensWereLive &= !options.CancellationToken.IsCancellationRequested;
            Task<ControlReply> reply;
            if (command.Route == "/api/pika/connect" && DelayConnect)
            { ConnectReached.TrySetResult(); reply = ConnectReply.Task; }
            else if (command.Route == "/api/command" && DelayCommand)
            { CommandReached.TrySetResult(); reply = CommandReply.Task; }
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
