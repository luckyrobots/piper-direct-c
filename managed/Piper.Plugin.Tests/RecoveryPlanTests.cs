using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Reflection;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class RecoveryPlanTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonObject Stopped() => new()
        {
            ["fresh"] = true, ["reset_pending"] = true, ["recovery_required"] = true,
            ["stop_reason"] = "Reset timed out.",
            ["status"] = new JsonObject { ["arm_status"] = 1, ["control"] = 2, ["motion_mode"] = 1,
                ["teach_state"] = 1, ["error"] = 0, ["stationary_s"] = .4 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject
                { ["fresh"] = true, ["enabled"] = false, ["fault"] = 0 }).ToArray())
        };
        var retry = RecoveryPlan.Inspect(Stopped(), false);
        check(retry.Action == "reset" && retry.RetryFailedReset && retry.ButtonText.Contains("retry failed reset") &&
            retry.NextStep.Contains("Keep supporting") && retry.NextStep.Contains("verify automatically"),
            "Failed idle STOP reset with residual teach state did not offer an explicit supported retry");
        check(RecoveryPlan.Inspect(Stopped(), true).Action is null,
            "An in-flight reset or active arm action offered a second reset");
        foreach (int teach in new[] { 0, 1, 2, 6 })
        {
            var device = Stopped(); device["status"]!["teach_state"] = teach;
            check(RecoveryPlan.Inspect(device, false) is { Action: "reset", RetryFailedReset: true },
                "A stationary STOP/all-off failed reset was trapped waiting with teach state " + teach);
        }
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("stale controller", d => d["fresh"] = false),
            ("stale joint", d => d["joints"]![2]!["fresh"] = false),
            ("missing motor-off proof", d => d["joints"]![2]!.AsObject().Remove("enabled")),
            ("one motor still on", d => d["joints"]![2]!["enabled"] = true),
            ("driver fault", d => d["joints"]![2]!["fault"] = 1),
            ("controller error", d => d["status"]!["error"] = 1),
            ("moving feedback", d => d["status"]!["stationary_s"] = .399),
            ("invalid stationary time", d => d["status"]!["stationary_s"] = double.NaN),
            ("active hand guiding", d => d["status"]!["arm_status"] = 11),
            ("onboard playback", d => d["status"]!["teach_state"] = 3),
            ("unsupported control", d => d["status"]!["control"] = 3),
            ("unsupported motion mode", d => d["status"]!["motion_mode"] = 2)
        })
        {
            var device = Stopped(); mutate(device);
            check(RecoveryPlan.Inspect(device, false).Action is null,
                "Failed reset retry ignored " + name);
        }
        var verified = Stopped();
        verified["status"]!["arm_status"] = 0; verified["status"]!["control"] = 1; verified["status"]!["teach_state"] = 0;
        check(RecoveryPlan.Inspect(verified, false) is { Action: "verify-reset", RetryFailedReset: false },
            "Healthy all-off pending reset retried instead of verifying without motor commands");
        verified["reset_pending"] = false;
        check(RecoveryPlan.Inspect(verified, false).Action == "verify-current",
            "Healthy current feedback unnecessarily requested a reset");
        var ordinary = Stopped(); ordinary["reset_pending"] = false;
        check(RecoveryPlan.Inspect(ordinary, false) is { Action: "reset", RetryFailedReset: false },
            "Residual teach flag in verified STOP/all-off blocked an ordinary supported reset");
        JsonObject Confirmations(string action, bool reset)
        {
            var body = new JsonObject();
            PipersPlugin.AddOperatorConfirmations(body, action, reset);
            return body;
        }
        bool Flags(JsonObject body, bool clear, bool supported, bool completed) => body["standalone"]?.GetValue<bool>() == true &&
            body.ContainsKey("clear") == clear && body.ContainsKey("supported") == supported && body.ContainsKey("reset_completed") == completed;
        check(Flags(Confirmations("verify-reset", true), false, true, true) && Flags(Confirmations("verify-reset", false), false, true, false),
            "verify-reset carried a reset confirmation without an operator-acknowledged recovery");
        check(Flags(Confirmations("reset", true), true, true, false) && Flags(Confirmations("verify-current", true), true, true, false),
            "Reset or current-state verification carried the wrong operator confirmations");
        check(Flags(Confirmations("enable", false), true, true, false) && Flags(Confirmations("disable", false), false, true, false),
            "Motor-power commands carried the wrong operator confirmations");
        check(Flags(Confirmations("joint", false), true, false, false) && Flags(Confirmations("move-to-zero", false), true, false, false) &&
            Flags(Confirmations("teach-return", false), true, true, false), "Motion or teaching return carried the wrong operator confirmations");
        ordinary["status"]!["arm_status"] = 11;
        check(RecoveryPlan.Inspect(ordinary, false).Action is null,
            "Genuine hand guiding with all motors off was mistaken for residual stopped teaching");
    }

    public static async Task RunObservation(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        var calls = new RecoveryCalls();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, flags)!.GetValue(plugin)!;
        Set("client", new PiperControl.PiperControlClient(calls));
        Set("token", "recovery-fixture");
        Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1")));
        long sequence = 0;
        void Publish(int armStatus, int teach, bool active)
        {
            var device = new JsonObject
            {
                ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true, ["reset_pending"] = true, ["recovery_required"] = true,
                ["status"] = new JsonObject { ["arm_status"] = armStatus, ["teach_state"] = teach, ["control"] = armStatus == 0 ? 1 : 2,
                    ["motion_mode"] = 1, ["error"] = 0, ["stationary_s"] = 1d, ["age_s"] = .001 },
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject
                    { ["fresh"] = true, ["enabled"] = false, ["fault"] = 0, ["age_s"] = .001 }).ToArray())
            };
            var snapshot = new JsonObject { ["token"] = "recovery-fixture", ["selected"] = "can-a",
                ["devices"] = new JsonArray(device), ["motion"] = new JsonObject { ["active"] = active, ["request_id"] = 77L } };
            Set("state", snapshot); Set("recoveryInProgress", true);
            Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "recovery-fixture", ++sequence, Stopwatch.GetTimestamp(), 0));
        }
        Task Observe() => (Task)typeof(PipersPlugin).GetMethod("ObserveRecovery", flags)!.Invoke(plugin, [CancellationToken.None])!;
        try
        {
            Publish(1, 1, false); await Observe();
            check(!Get<bool>("recoveryInProgress") && calls.Actions.Count == 0 && Get<string>("message").Contains("Retry failed reset"),
                "Retired failed reset kept the UI waiting forever or automatically resent reset");
            Publish(1, 1, false);
            Set("recoveryRequestId", 77L); Set("recoveryAcknowledgedSequence", sequence);
            Set("recoveryAcknowledgedTicks", Stopwatch.GetTimestamp());
            await Observe();
            check(Get<bool>("recoveryInProgress") && calls.Actions.Count == 0,
                "Cached pre-reset idle feedback released the recovery waiting latch immediately after acknowledgement");
            await Task.Delay(2); Publish(1, 1, false); await Observe();
            check(!Get<bool>("recoveryInProgress") && calls.Actions.Count == 0,
                "Post-acknowledgement retired reset feedback did not restore the explicit retry button");
            Publish(1, 1, true); await Observe();
            check(Get<bool>("recoveryInProgress") && calls.Actions.Count == 0,
                "An in-flight reset released the waiting latch or sent another reset");
            Publish(11, 1, false); await Observe();
            check(Get<bool>("recoveryInProgress") && calls.Actions.Count == 0,
                "Genuine hand guiding was mistaken for a safely retired failed reset");
            Publish(0, 0, false); await Observe();
            check(!Get<bool>("recoveryInProgress") && calls.Actions.SequenceEqual(["verify-reset"]),
                "Healthy all-off reset feedback lost automatic verification or issued a power/movement command");
        }
        finally
        {
            Get<CancellationTokenSource>("shutdown").Cancel();
            ((IDisposable)Get<object>("channel")).Dispose(); Get<SemaphoreSlim>("commands").Dispose();
        }
    }

    private sealed class RecoveryCalls : CallInvoker
    {
        public List<string> Actions { get; } = [];
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected recovery test RPC");
            var body = JsonNode.Parse(command.Json)!;
            if (command.Route != "/api/command") throw new InvalidOperationException("Recovery sent an unexpected route");
            Actions.Add(body["action"]!.GetValue<string>());
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{\"message\":\"verified\"}" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
