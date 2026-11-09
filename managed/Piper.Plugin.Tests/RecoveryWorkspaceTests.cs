using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class RecoveryWorkspaceTests
{
    private static JsonObject Stopped() => new()
    {
        ["id"] = "can-b", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
        ["recovery_required"] = true, ["reset_pending"] = false,
        ["stop_event"] = new JsonObject { ["reason"] = "Unexpected joint movement during range entry.", ["request_id"] = 11L },
        ["blocker"] = "The arm reports a fault or stop.",
        ["status"] = new JsonObject { ["control"] = 0, ["arm_status"] = 1, ["teach_state"] = 0,
            ["motion_mode"] = 1, ["error"] = 0, ["stationary_s"] = .54, ["pose_stationary_s"] = 600d, ["age_s"] = .001 },
        ["teaching"] = new JsonObject { ["phase"] = "stopped", ["can_recover_at_pose"] = false,
            ["recovery_teach_cycle_complete"] = false, ["recover_at_pose_reason"] = "Controller STOP must be cleared first." },
        ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(index => (JsonNode)new JsonObject
        {
            ["index"] = index, ["fresh"] = true, ["enabled"] = true, ["fault"] = 0, ["age_s"] = .001,
            ["actual_deg"] = index == 2 ? -1.339 : index == 3 ? 18.901 : 0,
            ["limits_fresh"] = true, ["min_deg"] = index == 2 ? 0d : -170d, ["max_deg"] = index == 3 ? 0d : 180d
        }).ToArray())
    };

    public static void Run(Action<bool, string> check)
    {
        var device = Stopped();
        var view = RecoveryWorkspaceState.Inspect(device, false);
        check(view is { Action: "supported-stop", NextAction: RecoveryWorkspaceAction.SupportedStop, CanAct: true } && view.Cause.Contains("Unexpected joint movement", StringComparison.Ordinal) &&
            view.NextStep.Contains("cannot clear", StringComparison.Ordinal) && view.ActionText.Contains("supported", StringComparison.Ordinal),
            "A latched STOP did not provide its cause and one explicitly supported recovery acknowledgement");
        check(view.Requirements.Contains("J2: -1.339", StringComparison.Ordinal) && view.Requirements.Contains("J3: 18.901", StringComparison.Ordinal) &&
            view.Requirements.Contains("600.00", StringComparison.Ordinal), "Recovery hid the affected joints or confused stable position with strict power/return feedback");
        foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
        view = RecoveryWorkspaceState.Inspect(device, false);
        check(view is { Action: "supported-stop", NextAction: RecoveryWorkspaceAction.SupportedStop, CanAct: true } && view.NextStep.Contains("One acknowledgement", StringComparison.Ordinal),
            "All-six-off STOP required another reset/verify acknowledgement");
        device["status"]!["stationary_s"] = 0d;
        view = RecoveryWorkspaceState.Inspect(device, false);
        check(view.NextAction == RecoveryWorkspaceAction.SupportedStop && view.Requirements.Contains("0.00 s", StringComparison.Ordinal),
            "The recovery acknowledgement hid the strict stillness that its runner must wait for");
        device["status"]!["stationary_s"] = .54; device["status"]!["arm_status"] = 0;
        device["recovery_required"] = false; device["blocker"] = ""; device["teaching"]!["phase"] = "off";
        view = RecoveryWorkspaceState.Inspect(device, false);
        check(!view.CanAct && view.NextStep.Contains("green-light", StringComparison.Ordinal) && view.Requirements.Contains("J3", StringComparison.Ordinal),
            "Clearing STOP hid range correction or exposed enable at an illegal pose");
        device["joints"]![1]!["actual_deg"] = .5; device["joints"]![2]!["actual_deg"] = -.5;
        check(RecoveryWorkspaceState.Inspect(device, false) is { Action: "enable", NextAction: RecoveryWorkspaceAction.EnableHold, CanAct: true },
            "A legal supported pose could not reach the holding-enable step");
        device["recovery_required"] = true; device["status"]!["control"] = 2; device["status"]!["teach_state"] = 2;
        device["teaching"]!["phase"] = "ended"; device["teaching"]!["can_recover_at_pose"] = true;
        foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = true;
        check(RecoveryWorkspaceState.Inspect(device, false).NextAction != RecoveryWorkspaceAction.RecoverAtPose, "UI accepted a recovery capability without a completed native teaching cycle");
        device["teaching"]!["recovery_teach_cycle_complete"] = true;
        check(RecoveryWorkspaceState.Inspect(device, false) is { Action: "recover-at-pose", NextAction: RecoveryWorkspaceAction.RecoverAtPose, CanAct: true },
            "A qualified completed guide cycle could not resume at its measured pose");
        check(RecoveryWorkspaceState.Inspect(device, false, waiting: true) is { Action: null, NextAction: RecoveryWorkspaceAction.None },
            "A recovery already waiting for an acknowledgement exposed another action");
        device["fresh"] = false;
        check(!RecoveryWorkspaceState.Inspect(device, false).CanAct, "Stale recovery feedback exposed a command");
    }

    public static async Task RunEvents(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        string folder = Path.Combine(Path.GetTempPath(), "piper-recovery-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        void Set(string field, object? value) => typeof(PipersPlugin).GetField(field, flags)!.SetValue(plugin, value);
        T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, flags)!.GetValue(plugin)!;
        PipersPlugin.RecoveryOperation SimulatedRecovery(PipersPlugin.RecoveryKind kind)
        {
            var operation = new PipersPlugin.RecoveryOperation(kind, new("piper-2", "recovery-ui-fixture", "can-b", 7),
                plugin.CommandAuthority.Epoch, new());
            Set("recoveryOperation", operation);
            return operation;
        }
        var calls = new Calls();
        var device = Stopped();
        var snapshot = new JsonObject { ["token"] = "recovery-ui-fixture", ["selected"] = "can-b", ["devices"] = new JsonArray(device),
            ["motion"] = new JsonObject { ["active"] = false, ["request_id"] = 77L } };
        Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-2", "Piper 2", "can-b")], ActiveRobotId: "piper-2")));
        Set("token", "recovery-ui-fixture"); Set("client", new PiperControl.PiperControlClient(calls)); Set("state", snapshot);
        Set("engine", (PluginContext)Activator.CreateInstance(typeof(PluginContext), flags, null,
            [new PluginStart("fixture", folder, folder), (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })))], null)!);
        void Fresh() => Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "recovery-ui-fixture", 1, Stopwatch.GetTimestamp(), 0));
        Task Click(string id) => plugin.HandleEventAsync(new("fixture", WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-2", id), "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        try
        {
            Fresh(); await Click("enable");
            check(calls.Actions.Count == 0 && Get<RecoveryGuide?>("recoveryGuide") is not null, "Generic Recover click removed torque instead of opening its supported guide");
            Fresh(); await Click("recovery.run");
            check(calls.Actions.Count == 0, "Diagnostics' Verify controller state ran a power-changing supported STOP recovery");
            using (SimulatedRecovery(PipersPlugin.RecoveryKind.ReturnToZero))
            {
                Fresh(); await Click("disable");
                check(calls.Actions.SequenceEqual(["disable"]), "A visible Disable action was blocked by the STOP teaching presentation or sent a reset/target");
                Set("recoveryOperation", null);
            }
            calls.Actions.Clear();
            device["status"]!["arm_status"] = 0; device["status"]!["control"] = 2; device["status"]!["teach_state"] = 2;
            device["teaching"]!["phase"] = "ended"; device["teaching"]!["can_recover_at_pose"] = true;
            device["teaching"]!["recovery_teach_cycle_complete"] = true;
            device["joints"]![1]!["actual_deg"] = .5; device["joints"]![2]!["actual_deg"] = -.5;
            using (SimulatedRecovery(PipersPlugin.RecoveryKind.ReturnToZero))
            {
                Fresh(); await Click("recovery.card.run");
                check(calls.Actions.Count == 0, "A queued recovery click ran while its card was disabled by another recovery action");
                Set("recoveryOperation", null);
            }
            Fresh(); await Click("recovery.card.run");
            check(calls.Actions.SequenceEqual(["recover-at-pose"]) && Get<RecoveryGuide?>("recoveryGuide")?.CommandSent == true,
                "A qualified green/off cycle did not admit the explicit same-pose return");
            calls.Actions.Clear(); Set("recoveryGuide", Get<RecoveryGuide>("recoveryGuide") with { CommandSent = false });
            device["status"]!["arm_status"] = 0; device["teaching"]!["phase"] = "off";
            device["status"]!["control"] = 1; device["status"]!["teach_state"] = 0;
            foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
            device["recovery_required"] = false; device["blocker"] = ""; device.Remove("stop_event");
            Fresh();
            await (Task)typeof(PipersPlugin).GetMethod("ObserveHandGuideRecovery", flags)!.Invoke(plugin, [CancellationToken.None])!;
            check(Get<RecoveryGuide?>("recoveryGuide") is { Gate: null } && calls.Actions.Count == 0,
                "Reset's retired stop record closed the visible range/holding guide or reused old teaching proof");
            using var reset = SimulatedRecovery(PipersPlugin.RecoveryKind.HoldHere);
            reset.ResetAt = Stopwatch.GetTimestamp();
            device["fresh"] = false; device["joints"]![0]!["fresh"] = false; Fresh();
            bool Gap() => (bool)typeof(PipersPlugin).GetMethod("RecoveryExpectedGap", flags)!.Invoke(plugin, [Get<LatestTelemetry>("telemetry").Latest])!;
            check(Gap(), "A bounded same-device reset feedback gap invalidated supported recovery");
            device["generation"] = 8L; Fresh(); check(!Gap(), "Another CAN generation inherited a reset-gap exception"); device["generation"] = 7L;
            device["present"] = false; Fresh(); check(!Gap(), "A disconnected adapter inherited a reset-gap exception"); device["present"] = true;
            device["joints"]![0]!["fault"] = 1; Fresh(); check(!Gap(), "Reset-gap handling masked a known motor-driver fault"); device["joints"]![0]!["fault"] = 0;
            reset.ResetAt = 0; Fresh(); check(!Gap(), "Stale feedback outside reset was treated as an expected gap");
            reset.ResetAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 6; Fresh(); check(!Gap(), "The reset-gap exception outlived its five-second bound");
            reset.ResetAt = Stopwatch.GetTimestamp();
            Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "recovery-ui-fixture", 1, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2, 0));
            check(!Gap(), "An old telemetry envelope was accepted as a fresh reset gap");
            Fresh(); plugin.CommandAuthority.Revoke(revokeLocalApproval: () => Set("approved", false));
            check(!Gap(), "A STOP/permission change retained the reset-gap exception");
            bool retiredGrant = false;
            try { typeof(PipersPlugin).GetMethod("GrantRecoveryHolding", flags)!.Invoke(plugin, [reset, CancellationToken.None]); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { retiredGrant = true; }
            check(retiredGrant && !Get<bool>("approved") && calls.Actions.Count == 0, "A recovery continuation granted holding permission after STOP");
            Set("recoveryOperation", null);
            device["fresh"] = true; device["joints"]![0]!["fresh"] = true;
            device["reset_pending"] = true; device["recovery_required"] = true;
            Fresh(); await Click("recovery.run");
            check(calls.Actions.SequenceEqual(["verify-reset"]), "Diagnostics' Verify controller state did not run the recovery card's verification");
            calls.Actions.Clear(); device["reset_pending"] = false; device["recovery_required"] = false;
            device["blocker"] = "Another controller is sending commands on this CAN bus.";
            Set("approved", true);
            Fresh(); await Click("enable");
            check(calls.Actions.Count == 0 && Get<PipersPlugin.PrimaryActionState>("primaryAction").Status.Contains("Another controller", StringComparison.Ordinal),
                "A queued primary click sent Enable even though the current header reported a controller blocker");
        }
        finally
        {
            Get<CancellationTokenSource>("shutdown").Cancel();
            ((IDisposable)Get<object>("channel")).Dispose(); Get<SemaphoreSlim>("commands").Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    public static async Task RunSupportedSequence(Action<bool, string> check)
    {
        foreach (bool legal in new[] { false, true })
        {
            var device = Stopped();
            if (legal) { device["joints"]![1]!["actual_deg"] = .5; device["joints"]![2]!["actual_deg"] = -.5; }
            var actions = new List<string>(); long sequence = 0, request = 0; int resetGapReads = 0;
            SupportedRecoverySnapshot Read()
            {
                ++sequence;
                if (actions.LastOrDefault() == "reset" && resetGapReads++ < 2)
                {
                    var gap = (JsonObject)device.DeepClone(); gap["fresh"] = false;
                    return new(gap, false, request, sequence, sequence, ExpectedResetGap: true);
                }
                return new(device, false, request, sequence, sequence);
            }
            Task<SupportedRecoveryAcknowledgement> Send(string action, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); actions.Add(action); ++request;
                if (action == "disable") foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
                if (action == "reset")
                {
                    device["status"]!["arm_status"] = 0; device["status"]!["control"] = 1; device["teaching"]!["phase"] = "off";
                    device["reset_pending"] = true; device["blocker"] = "";
                }
                if (action == "verify-reset") { device["reset_pending"] = false; device["recovery_required"] = false; }
                return Task.FromResult(new SupportedRecoveryAcknowledgement(action == "verify-reset" ? 0 : request, sequence, sequence));
            }
            bool held = await SupportedStopRecovery.Run(Read, Send, _ =>
            {
                actions.Add("enable"); foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = true;
                return Task.CompletedTask;
            }, _ => { }, CancellationToken.None);
            check(held == legal && actions.SequenceEqual(legal ? ["disable", "reset", "verify-reset", "enable"] : ["disable", "reset", "verify-reset"]),
                "One supported STOP acknowledgement did not tolerate its bounded reset gap, recover once, or preserve the range guard");
        }
        foreach (string interruption in new[] { "identity", "stale", "driver", "cancel", "cached-ack" })
        {
            var device = Stopped(); var actions = new List<string>(); long sequence = 0;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            SupportedRecoverySnapshot Read()
            {
                if (actions.Count > 0 && interruption == "identity") throw new InvalidOperationException("Captured robot identity changed");
                if (actions.Count > 0 && interruption == "stale") device["fresh"] = false;
                if (actions.Count > 0 && interruption == "driver") device["joints"]![0]!["fault"] = 1;
                return new(device, false, 1, interruption == "cached-ack" ? 0 : ++sequence, sequence);
            }
            bool rejected = false;
            try
            {
                await SupportedStopRecovery.Run(Read, (action, _) =>
                {
                    actions.Add(action); foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
                    if (interruption == "cancel") cancel.Cancel();
                    return Task.FromResult(new SupportedRecoveryAcknowledgement(1, sequence, sequence));
                }, _ => throw new InvalidOperationException("Unexpected enable"), _ => { }, cancel.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { rejected = true; }
            check(rejected && actions.SequenceEqual(["disable"]), "Recovery continued to reset/enable after " + interruption);
        }
        foreach (bool identityChanged in new[] { false, true })
        {
            var device = Stopped(); var actions = new List<string>(); long sequence = 0, request = 0;
            using var cancel = new CancellationTokenSource();
            bool rejected = false;
            try
            {
                await SupportedStopRecovery.Run(() =>
                {
                    ++sequence;
                    if (actions.LastOrDefault() == "reset")
                    {
                        if (identityChanged) throw new InvalidOperationException("STOP changed the captured recovery permission");
                        cancel.Cancel();
                        var gap = (JsonObject)device.DeepClone(); gap["fresh"] = false;
                        return new SupportedRecoverySnapshot(gap, false, request, sequence, sequence, ExpectedResetGap: true);
                    }
                    return new(device, false, request, sequence, sequence);
                }, (action, _) =>
                {
                    actions.Add(action); ++request;
                    foreach (var joint in device["joints"]!.AsArray()) joint!["enabled"] = false;
                    return Task.FromResult(new SupportedRecoveryAcknowledgement(request, sequence, sequence));
                }, _ => throw new InvalidOperationException("Unexpected holding enable after cancelled reset"), _ => { }, cancel.Token);
            }
            catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { rejected = true; }
            check(rejected && actions.SequenceEqual(["disable", "reset"]), "Recovery enabled or verified after cancellation/identity change during its reset feedback gap");
        }
    }

    private sealed class Calls : CallInvoker
    {
        public List<string> Actions { get; } = [];
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected recovery fixture call");
            if (command.Route != "/api/command") throw new InvalidOperationException("Recovery fixture sent another route");
            Actions.Add(JsonNode.Parse(command.Json)!["action"]!.GetValue<string>());
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 202, Json = "{\"request_id\":77}" }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
