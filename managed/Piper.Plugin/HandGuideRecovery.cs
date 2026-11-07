using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

internal enum RecoveryWorkspaceAction { None, SupportedStop, Disable, EnableHold, TeachReturn, RecoverAtPose, VerifyReset, VerifyCurrent }

/// <summary>A recovery explanation and one explicit next action, derived from the selected arm's live evidence.</summary>
public sealed record RecoveryWorkspaceState(string Cause, string NextStep, string Requirements, string? Action, string ActionText, bool CanAct)
{
    internal RecoveryWorkspaceAction NextAction { get; init; }

    public static RecoveryWorkspaceState Inspect(JsonObject? device, bool moving, bool waiting = false)
    {
        string cause = device?["stop_event"]?["reason"]?.GetValue<string>() is { Length: > 0 } recorded ? recorded
            : device?["stop_reason"]?.GetValue<string>() is { Length: > 0 } stopped ? stopped : "Robot control needs recovery.";
        string range = RangeRequirements(device);
        double strict = Number(device?["status"]?["stationary_s"]), pose = Number(device?["status"]?["pose_stationary_s"]);
        string evidence = $"Measured pose stable: {pose:F2} s · power/return stillness: {strict:F2} s (reset requires 0.40 s).";
        string requirements = string.Join("\n", new[] { range, evidence }.Where(line => line.Length > 0));
        RecoveryWorkspaceState Step(string next, RecoveryWorkspaceAction action = RecoveryWorkspaceAction.None, string label = "Waiting for controller feedback")
        {
            string? route = action switch
            {
                RecoveryWorkspaceAction.SupportedStop => "supported-stop",
                RecoveryWorkspaceAction.Disable => "disable",
                RecoveryWorkspaceAction.EnableHold => "enable",
                RecoveryWorkspaceAction.TeachReturn => "teach-return",
                RecoveryWorkspaceAction.RecoverAtPose => "recover-at-pose",
                RecoveryWorkspaceAction.VerifyReset => "verify-reset",
                RecoveryWorkspaceAction.VerifyCurrent => "verify-current",
                _ => null
            };
            return new(cause, next, requirements, route, label, action != RecoveryWorkspaceAction.None) { NextAction = action };
        }
        if (device?["fresh"]?.GetValue<bool>() != true)
            return Step("Waiting for live feedback from this robot. Recovery will not send a command using an old pose.");
        var power = RobotPowerState.Inspect(device);
        if (!power.EnabledCount.HasValue) return Step("Waiting for fresh power feedback from all six arm motors.");
        if (moving || waiting) return Step("The current controller action is finishing. Keep the arm supported; this card updates when fresh feedback confirms its result.", label: "Checking recovery result…");
        bool controllerStopped = device["status"]?["arm_status"]?.GetValue<int>() == 1;
        var teaching = TeachingState.Inspect(device);
        var plan = RecoveryPlan.Inspect(device, false);
        if (controllerStopped)
        {
            cause = "Controller STOP is latched. " + cause;
            bool healthyDrivers = device["status"]?["error"]?.GetValue<int>() == 0 && device["joints"]!.AsArray().All(j => j?["fault"]?.GetValue<int>() == 0);
            if (!healthyDrivers) return Step(plan.NextStep, power.EnabledCount > 0 ? RecoveryWorkspaceAction.Disable : RecoveryWorkspaceAction.None,
                power.EnabledCount > 0 ? "Arm supported — Disable arm motors" : "Inspect the driver fault first");
            return Step("Rest or support the arm and clear its surroundings, then click Arm supported — recover. One acknowledgement disables the motors, clears STOP once, verifies recovery and restores holding only at a legal measured pose. The green button alone cannot clear controller STOP.",
                RecoveryWorkspaceAction.SupportedStop, "Arm supported — recover");
        }
        if (teaching.HandGuiding)
            return Step("Green light is on: move the arm to a clear pose within the ranges shown below. Single-click its button again to turn the light off, then keep it still.", label: "Turn the green light off when ready");
        if (teaching.Active) return Step("Onboard playback still owns the arm. End playback using the physical controls before recovering.", label: "End onboard playback first");
        bool recovery = device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true;
        if (power.AllEnabled && device["teaching"]?["can_recover_at_pose"]?.GetValue<bool>() == true &&
            device["teaching"]?["recovery_teach_cycle_complete"]?.GetValue<bool>() == true)
            return Step("The controller verified the completed green-light hand-guiding cycle and this measured pose. Resume holds here and discards the interrupted target.",
                RecoveryWorkspaceAction.RecoverAtPose, "Resume controls at this pose");
        if (recovery)
        {
            if (plan.Action == "verify-reset") return Step(plan.NextStep, RecoveryWorkspaceAction.VerifyReset, "Verify controller recovery");
            if (plan.Action == "verify-current") return Step(plan.NextStep, RecoveryWorkspaceAction.VerifyCurrent, "Verify controller recovery");
            string why = device["teaching"]?["recover_at_pose_reason"]?.GetValue<string>() ?? device["blocker"]?.GetValue<string>() ?? plan.NextStep;
            return Step("Single-click the physical button for green hand guiding, move clear, then click it again to finish. " + why,
                label: "Waiting for a qualified hand-guided pose");
        }
        if (teaching.ReturnRequired)
            return Step(teaching.CanReturn ? teaching.SamePoseReturn ? "The green light is off. Resume controls to hold this measured pose without homing or replay."
                    : "The controller verified its required zero pose. Complete the supported return to CAN; no homing or replay will run." : teaching.NextStep,
                teaching.CanReturn ? RecoveryWorkspaceAction.TeachReturn : RecoveryWorkspaceAction.None,
                teaching.SamePoseReturn ? "Resume controls at this pose" : "Return to controls at verified zero");
        if (power.EnabledCount == 0)
        {
            bool legal = range.Length == 0 && string.IsNullOrEmpty(device["blocker"]?.GetValue<string>());
            return Step(legal ? "Controller recovery is complete. Keep the arm supported and enable holding at its current measured pose."
                : "Controller STOP is cleared and all motors are off. Support the arm. Use the physical green-light hand-guiding mode to bring the listed joints into range, then turn the light off. " + (device["blocker"]?.GetValue<string>() ?? ""),
                legal ? RecoveryWorkspaceAction.EnableHold : RecoveryWorkspaceAction.None, "Arm supported — Enable holding here");
        }
        return Step("Recovery is complete. All six motors hold the measured pose. The interrupted movement was discarded.", label: "Recovery complete");
    }

    public static string RangeRequirements(JsonObject? device)
    {
        var lines = new List<string>();
        if (device?["joints"] is not JsonArray { Count: 6 } joints) return "";
        for (int index = 0; index < joints.Count; ++index)
        {
            var joint = joints[index];
            if (joint?["limits_fresh"]?.GetValue<bool>() != true) { lines.Add($"J{index + 1}: waiting for reported limits."); continue; }
            if (!TryNumber(joint?["actual_deg"], out double actual) || !TryNumber(joint?["min_deg"], out double minimum) ||
                !TryNumber(joint?["max_deg"], out double maximum) || minimum >= maximum)
            { lines.Add($"J{index + 1}: waiting for a valid measured angle and range."); continue; }
            if (actual < minimum - .1 || actual > maximum + .1)
                lines.Add($"J{index + 1}: {actual:F3}° is outside {minimum:0.###}° to {maximum:0.###}°. Hand-guide this joint back into range.");
        }
        return string.Join("\n", lines);
    }

    private static double Number(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : 0;
    private static bool TryNumber(JsonNode? value, out double number)
    { number = double.NaN; return value is JsonValue scalar && scalar.TryGetValue<double>(out number) && double.IsFinite(number); }
}

public sealed class HandGuideRecoveryGate(string identity, bool observedGuiding = false)
{
    public string Identity { get; } = identity;
    public bool SawGuiding { get; private set; } = observedGuiding;
    public bool Attempted { get; private set; }
    public bool Observe(string currentIdentity, TeachingState mode, bool qualified)
    {
        if (currentIdentity != Identity) return false;
        if (mode.HandGuiding) SawGuiding = true;
        if (!SawGuiding || Attempted || mode.Active || !qualified) return false;
        Attempted = true;
        return true;
    }
}

public sealed partial class PipersPlugin
{
    private HandGuideRecoveryGate? handGuideRecovery;
    private string handGuideRecoveryMessage = "";
    private bool handGuideRecoveryCommandSent;
    private bool recoveryGuideOpen;
    private string recoveryGuideScope = "";
    private JsonObject? supportedRecoveryStop;

    private string RecoveryConnectionIdentity(JsonObject device) => token + ":" + ActiveProfile.Id + ":" +
        device["id"]?.GetValue<string>() + ":" + device["generation"]?.GetValue<long>();

    private bool RecoveryGuideFor(JsonObject? device) => device is not null && recoveryGuideOpen && recoveryGuideScope == RecoveryConnectionIdentity(device);

    private RecoveryWorkspaceState RecoveryCardState(JsonObject? device) => RecoveryWorkspaceState.Inspect(device,
        state?["motion"]?["active"]?.GetValue<bool>() == true, recoveryInProgress || supportedRecoveryBusy && RecoveryGuideFor(device));

    private bool RecoveryCardCanRun(RecoveryWorkspaceState view) => view.CanAct && !switchingProfile && CurrentRecovery is null && !GroupFollowActive;

    private async Task RunRecoveryCard(CancellationToken cancellation)
    {
        if (workspacePage != "controls" || calibrationOpen) throw new InvalidOperationException("Return to Controls to use the recovery guide.");
        long permissionEpoch = CommandAuthority.Epoch;
        var device = Device();
        var view = RecoveryCardState(device);
        if (!RecoveryCardCanRun(view)) throw new InvalidOperationException(view.CanAct ? "Wait for the current robot action to finish before continuing recovery." : view.NextStep);
        recoveryGuideOpen = true; recoveryGuideScope = RecoveryConnectionIdentity(device);
        try
        {
            switch (view.NextAction)
            {
                case RecoveryWorkspaceAction.SupportedStop:
                    StartSupportedStopRecovery(device, permissionEpoch, cancellation);
                    break;
                case RecoveryWorkspaceAction.Disable:
                    approved = linkCommandsEnabled = false; ForgetControlApproval();
                    await Command("disable", null, cancellation);
                    handGuideRecoveryMessage = "Disable requested. Keep supporting the arm until all six motors report off; the next recovery action appears here.";
                    break;
                case RecoveryWorkspaceAction.EnableHold:
                    var scope = ControlScope(ActiveProfile, token, device);
                    if (!CommandAuthority.TryInEpoch(permissionEpoch, () => approved = true))
                        throw new InvalidOperationException("Holding setup was cancelled by STOP or a control-scope change.");
                    await RunPrimaryAction(ct => CompleteRobotEnable(permissionEpoch, scope, ct), cancellation);
                    CancelHandGuideRecovery();
                    break;
                case RecoveryWorkspaceAction.TeachReturn:
                case RecoveryWorkspaceAction.RecoverAtPose:
                    bool teaching = view.NextAction == RecoveryWorkspaceAction.TeachReturn;
                    var flags = teaching ? TeachingState.Inspect(device).ReturnFlags()
                        : new JsonObject { ["button_off"] = true, ["same_pose_confirmed"] = true };
                    await Command(view.Action!, flags, cancellation,
                        recoveryAcknowledged: !teaching, teachingAcknowledged: teaching);
                    handGuideRecoveryCommandSent = true;
                    handGuideRecoveryMessage = "Restoring holding control at the measured pose. No homing, replay or power change.";
                    break;
                case RecoveryWorkspaceAction.VerifyReset:
                case RecoveryWorkspaceAction.VerifyCurrent:
                    var result = await Command(view.Action!, null, cancellation, recoveryAcknowledged: true);
                    Interlocked.Exchange(ref recoveryRequestId, result["request_id"]?.GetValue<long>() ?? 0);
                    Interlocked.Exchange(ref recoveryAcknowledgedSequence, telemetry.Latest?.Sequence ?? 0);
                    Interlocked.Exchange(ref recoveryAcknowledgedTicks, Stopwatch.GetTimestamp());
                    handGuideRecoveryMessage = "Controller recovery verification requested.";
                    break;
            }
            message = handGuideRecoveryMessage.Length > 0 ? handGuideRecoveryMessage : message;
        }
        catch (Exception error) { handGuideRecoveryMessage = "Recovery did not advance: " + error.Message; throw; }
    }

    private string RecoveryIdentity(JsonObject device) => token + ":" + ActiveProfile.Id + ":" +
        device["id"]?.GetValue<string>() + ":" + device["generation"]?.GetValue<long>() + ":" +
        (device["stop_event"]?["request_id"]?.ToJsonString() ?? device["stop_reason"]?.GetValue<string>() ?? "");

    private void BeginHandGuideRecovery(JsonObject device)
    {
        recoveryGuideOpen = true; recoveryGuideScope = RecoveryConnectionIdentity(device);
        handGuideRecovery = new(RecoveryIdentity(device), device["teaching"]?["recovery_teach_cycle_complete"]?.GetValue<bool>() == true);
        handGuideRecoveryCommandSent = false;
        handGuideRecoveryMessage = "Single-click the physical arm button for a solid green light. Move the arm clear of the obstacle, then single-click again and hold still. Controls return at that pose; no homing or replay.";
        message = handGuideRecoveryMessage;
    }

    private void CancelHandGuideRecovery()
    {
        CurrentRecovery?.Cancel();
        supportedRecoveryStop = null;
        recoveryGuideOpen = false; recoveryGuideScope = "";
        handGuideRecovery = null;
        handGuideRecoveryCommandSent = false;
        handGuideRecoveryMessage = "";
    }

    private async Task ObserveHandGuideRecovery(CancellationToken cancellation)
    {
        if (CurrentRecovery is not null || GroupFollowActive || (!recoveryGuideOpen && handGuideRecovery is null) || switchingProfile || calibrationOpen || !HasFreshControlTelemetry(out _)) return;
        long expectedPermission = CommandAuthority.Epoch;
        var device = Device();
        if (recoveryGuideScope != RecoveryConnectionIdentity(device)) { CancelHandGuideRecovery(); return; }
        if (handGuideRecoveryCommandSent)
        {
            if (device["recovery_required"]?.GetValue<bool>() != true && TeachingCanResume(device))
            {
                RememberControlApproval(expectedPermission, activate: true);
                teachingSeen = approvalBeforeTeaching = false;
                CancelHandGuideRecovery();
                message = "Ready · holding the hand-guided pose · interrupted movement discarded";
            }
            else if (state?["motion"]?["active"]?.GetValue<bool>() != true && device["recovery_required"]?.GetValue<bool>() == true)
                handGuideRecoveryMessage = state?["message"]?.GetValue<string>() ?? "The controller could not verify return to holding. Open Settings → Diagnostics for the exact reason.";
            return;
        }
        if (handGuideRecovery is not { } gate) return;
        if (gate.Identity != RecoveryIdentity(device))
        {
            // Reset retires the old obstruction evidence, but the same arm's
            // visible guide must remain available for range/holding checks.
            handGuideRecovery = null; handGuideRecoveryCommandSent = false;
            handGuideRecoveryMessage = "The previous recovery record changed. Use the current step shown above; no earlier target or hand-guiding proof will be reused.";
            return;
        }
        var mode = TeachingState.Inspect(device);
        if (mode.HandGuiding) handGuideRecoveryMessage = "Hand guiding active. Move to a clear pose, then single-click the arm button to turn the green light off.";
        else if (gate.SawGuiding) handGuideRecoveryMessage = device["teaching"]?["recover_at_pose_reason"]?.GetValue<string>() is { Length: > 0 } why
            ? why : "Hold still while the controller checks the hand-guided pose.";
        if (!await commands.WaitAsync(0, cancellation)) return;
        try
        {
            device = Device();
            if (GroupFollowActive || !ReferenceEquals(handGuideRecovery, gate) ||
                !gate.Observe(RecoveryIdentity(device), TeachingState.Inspect(device), device["teaching"]?["can_recover_at_pose"]?.GetValue<bool>() == true)) return;
            await Command("recover-at-pose", new() { ["button_off"] = true, ["same_pose_confirmed"] = true }, cancellation,
                recoveryAcknowledged: true, expectedPermissionEpoch: expectedPermission);
            handGuideRecoveryCommandSent = true;
            handGuideRecoveryMessage = "Restoring holding control at the measured pose · no power change, homing or replay";
        }
        catch (InvalidOperationException error) { handGuideRecoveryMessage = error.Message; }
        finally { commands.Release(); }
    }

    private void StartSupportedStopRecovery(JsonObject initial, long permissionEpoch, CancellationToken cancellation)
    {
        var operation = BeginRecovery(RecoveryKind.HoldHere, initial, permissionEpoch, cancellation);
        var scope = operation.Scope;
        supportedRecoveryStop = initial["stop_event"] is JsonObject stopped ? (JsonObject)stopped.DeepClone() : new JsonObject { ["reason"] = initial["stop_reason"]?.DeepClone() };
        handGuideRecoveryMessage = "Supported recovery starting. Keep the arm supported; Cancel or Disable interrupts this sequence.";
        operation.Worker = Task.Run(async () =>
        {
            bool entered = false;
            bool completedHolding = false;
            string finalStatus = handGuideRecoveryMessage;
            bool SameIdentity()
            {
                var receipt = telemetry.Latest;
                return RecoveryScopeCurrent(operation) && receipt is not null &&
                    TelemetryScope.Read(receipt.State, scope.Device) == new TelemetryScope(scope.Backend, scope.Device, scope.Generation);
            }
            void Progress(string status)
            {
                finalStatus = status;
                if (SameIdentity()) handGuideRecoveryMessage = status;
            }
            try
            {
                await commands.WaitAsync(operation.Token); entered = true;
                SupportedRecoverySnapshot Read()
                {
                    if (!RecoveryScopeCurrent(operation))
                        throw new InvalidOperationException("Recovery cancelled because robot identity or control permission changed. No previous target will resume.");
                    var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for live recovery feedback.");
                    bool expectedGap = RecoveryExpectedGap(receipt);
                    var device = expectedGap ? RobotProfileRouting.Device(preferences, receipt.State) : Device(scope.Device, scope.Generation, scope.Backend, receipt);
                    if (expectedGap) { device = (JsonObject)device.DeepClone(); device["fresh"] = false; }
                    return new(device, receipt.State["motion"]?["active"]?.GetValue<bool>() == true,
                        receipt.State["motion"]?["request_id"]?.GetValue<long>() ?? 0, receipt.Sequence, receipt.ReceivedTicks, expectedGap);
                }
                async Task<SupportedRecoveryAcknowledgement> Send(string action, CancellationToken ct)
                {
                    _ = Read();
                    // Only reset's bounded missing-feedback interval is expected.
                    // Every command below still calls Device() and rejects stale
                    // evidence; only the runner's wait can consume a gap marker.
                    Volatile.Write(ref operation.ResetAt, action == "reset" ? Stopwatch.GetTimestamp() : 0);
                    var result = await Command(action, null, ct, recoveryAcknowledged: action != "disable",
                        expectedDeviceId: scope.Device, expectedGeneration: scope.Generation, expectedToken: scope.Backend,
                        expectedPermissionEpoch: operation.Epoch);
                    return new(result["request_id"]?.GetValue<long>() ?? 0, telemetry.Latest?.Sequence ?? 0, Stopwatch.GetTimestamp());
                }
                bool held = await SupportedStopRecovery.Run(Read, Send, async ct =>
                {
                    _ = Read();
                    GrantRecoveryHolding(operation, ct);
                    await Enable(ct, includePika: false, expectedDeviceId: scope.Device, expectedGeneration: scope.Generation,
                        expectedToken: scope.Backend, expectedPermissionEpoch: operation.Epoch);
                }, Progress, operation.Token);
                if (SameIdentity())
                {
                    if (held)
                    {
                        CompleteRecoveryHolding(operation);
                        completedHolding = true;
                        handGuideRecovery = null; handGuideRecoveryCommandSent = false; recoveryGuideOpen = false;
                    }
                    message = finalStatus;
                }
            }
            catch (OperationCanceledException) { Progress("Supported recovery cancelled or timed out. No reset retry, homing or old target will run. Inspect the current state above."); }
            catch (Exception error) { Progress("Recovery paused: " + error.Message); }
            finally
            {
                if (!completedHolding)
                    CommandAuthority.TryInEpoch(operation.Epoch, () =>
                    {
                        approved = linkCommandsEnabled = false;
                        CommandAuthority.ForgetApproval(scope.ProfileId);
                    });
                RetireRecovery(operation);
                if (entered) commands.Release();
            }
        }, CancellationToken.None);
    }
}
