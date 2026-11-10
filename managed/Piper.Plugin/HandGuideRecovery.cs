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

/// <summary>Immutable physical proof for one recorded stop: whether a green-light
/// hand-guiding cycle was seen on exactly that stop's arm, connection and record.</summary>
public sealed record HandGuideRecoveryGate(string Identity, bool SawGuiding = false)
{
    /// <summary>The gate after one observation: hand guiding on the same identity is recorded.</summary>
    public HandGuideRecoveryGate Observe(string currentIdentity, TeachingState mode) =>
        currentIdentity == Identity && mode.HandGuiding && !SawGuiding ? this with { SawGuiding = true } : this;

    /// <summary>True when a seen cycle has ended and the controller qualifies this pose.</summary>
    public bool Qualifies(string currentIdentity, TeachingState mode, bool qualified) =>
        currentIdentity == Identity && SawGuiding && !mode.Active && qualified;
}

/// <summary>Plugin-3 recovery guide, replaced as one immutable value. Each state
/// carries only its own data:
/// <code>
///   Closed (null) → Open → Armed(Gate) → Attempted(Gate) → CommandSent → Closed
///                   Open ──card return──────────────────→ CommandSent
///   Armed/Attempted ──recorded stop changed──→ Open
/// </code>
/// Armed waits for a green-light cycle (<see cref="HandGuideRecoveryGate.SawGuiding"/>).
/// Attempted has claimed its one same-pose return and is never retried. A supported
/// STOP recovery records the original stop in <see cref="SupportedStop"/>. The guide
/// closes on Cancel/STOP, on a connection change and on verified holding.</summary>
internal abstract record RecoveryGuide(string Scope, string Message, JsonObject? SupportedStop)
{
    internal sealed record Open(string Scope, string Message = "", JsonObject? SupportedStop = null) : RecoveryGuide(Scope, Message, SupportedStop);
    internal sealed record Armed(string Scope, HandGuideRecoveryGate Gate, string Message = "", JsonObject? SupportedStop = null)
        : RecoveryGuide(Scope, Message, SupportedStop);
    internal sealed record Attempted(string Scope, HandGuideRecoveryGate Gate, string Message = "", JsonObject? SupportedStop = null)
        : RecoveryGuide(Scope, Message, SupportedStop);
    internal sealed record CommandSent(string Scope, string Message = "", JsonObject? SupportedStop = null) : RecoveryGuide(Scope, Message, SupportedStop);

    /// <summary>The hand-guide proof while one is armed or claimed.</summary>
    public HandGuideRecoveryGate? HandGuideGate => this switch { Armed armed => armed.Gate, Attempted attempted => attempted.Gate, _ => null };
    public RecoveryGuide Sent(string message) => new CommandSent(Scope, message, SupportedStop);
    public RecoveryGuide Disarmed(string message) => new Open(Scope, message, SupportedStop);
}

public sealed partial class PipersPlugin
{
    private RecoveryGuide? CurrentGuide => recovery.Guide;

    private void SetRecoveryGuideMessage(string text) => recovery.ShowGuideMessage(text);

    private string RecoveryConnectionIdentity(JsonObject device) => token + ":" + ActiveProfile.Id + ":" +
        device["id"]?.GetValue<string>() + ":" + device["generation"]?.GetValue<long>();

    /// <summary>The connection identity plus the recorded stop that the guide recovers from.</summary>
    private string RecoveryIdentity(JsonObject device) => RecoveryConnectionIdentity(device) + ":" +
        (device["stop_event"]?["request_id"]?.ToJsonString() ?? device["stop_reason"]?.GetValue<string>() ?? "");

    private bool RecoveryGuideFor(JsonObject? device) => device is not null && CurrentGuide?.Scope == RecoveryConnectionIdentity(device);

    private RecoveryWorkspaceState RecoveryCardState(JsonObject? device) => RecoveryWorkspaceState.Inspect(device,
        state?["motion"]?["active"]?.GetValue<bool>() == true, SupportedRecoveryBusy && RecoveryGuideFor(device));

    private bool RecoveryCardCanRun(RecoveryWorkspaceState view) => view.CanAct && !switchingProfile && CurrentRecovery is null && !GroupFollowActive;

    private static bool VerifiesOnly(RecoveryWorkspaceState view) => view.NextAction is RecoveryWorkspaceAction.VerifyReset or RecoveryWorkspaceAction.VerifyCurrent;

    /// <summary>The single recovery entry point. The Controls card runs its next
    /// step; Diagnostics' "Verify controller state" (verifyOnly) may run only the
    /// card's verification steps, which change neither motor power nor position.</summary>
    private async Task RunRecoveryCard(CancellationToken cancellation, bool verifyOnly = false)
    {
        if (!verifyOnly && Navigation.Page != WorkspacePage.Controls) throw new InvalidOperationException("Return to Controls to use the recovery guide.");
        long permissionEpoch = CommandAuthority.Epoch;
        var device = Device();
        var view = RecoveryCardState(device);
        if (!RecoveryCardCanRun(view)) throw new InvalidOperationException(view.CanAct ? "Wait for the current robot action to finish before continuing recovery." : view.NextStep);
        if (verifyOnly && !VerifiesOnly(view)) throw new InvalidOperationException("Use the recovery card on Controls for this step. " + view.NextStep);
        if (!verifyOnly)
        {
            recovery.OpenGuide(RecoveryConnectionIdentity(device));
        }
        string? progress = null;
        try
        {
            switch (view.NextAction)
            {
                case RecoveryWorkspaceAction.SupportedStop:
                    StartSupportedStopRecovery(device, permissionEpoch, cancellation);
                    break;
                case RecoveryWorkspaceAction.Disable:
                    // Forget first: a remembered approval could otherwise be restored
                    // by the telemetry thread between the two steps.
                    ForgetControlApproval(); approved = linkCommandsEnabled = false;
                    await Command("disable", null, cancellation);
                    progress = "Disable requested. Keep supporting the arm until all six motors report off; the next recovery action appears here.";
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
                        : new ArmCommandFields(ButtonOff: true, SamePoseConfirmed: true);
                    await Command(view.Action!, flags, cancellation, teaching ? CommandPurpose.TeachingReturn : CommandPurpose.Recovery);
                    progress = "Restoring holding control at the measured pose. No homing, replay or power change.";
                    recovery.MarkCommandSent(progress);
                    break;
                case RecoveryWorkspaceAction.VerifyReset:
                    // B1: ask; ConfirmResetCompletion sends verify-reset only on Confirm.
                    RequestResetCompletion(new RobotCommandScope(ActiveProfile.Id, token, device["id"]!.GetValue<string>(),
                        device["generation"]!.GetValue<long>()), permissionEpoch);
                    progress = "Confirm that the controller reset or power cycle completed. Cancel sends nothing.";
                    break;
                case RecoveryWorkspaceAction.VerifyCurrent:
                    await Command(view.Action!, null, cancellation, CommandPurpose.Recovery);
                    progress = "Controller recovery verification requested.";
                    break;
            }
            if (progress is not null) SetRecoveryGuideMessage(progress);
            if ((progress ?? CurrentGuide?.Message) is { Length: > 0 } shown) message = shown;
        }
        catch (Exception error) { SetRecoveryGuideMessage("Recovery did not advance: " + error.Message); throw; }
    }

    private void BeginHandGuideRecovery(JsonObject device)
    {
        const string instructions = "Single-click the physical arm button for a solid green light. Move the arm clear of the obstacle, then single-click again and hold still. Controls return at that pose; no homing or replay.";
        recovery.ArmGuide(RecoveryConnectionIdentity(device),
            new(RecoveryIdentity(device), device["teaching"]?["recovery_teach_cycle_complete"]?.GetValue<bool>() == true), instructions);
        message = instructions;
    }

    /// <summary>Plugin-3's one reset. STOP calls it: it takes no lock.</summary>
    private void CancelHandGuideRecovery() => recovery.Cancel();

    internal async Task ObserveHandGuideRecovery(CancellationToken cancellation)
    {
        if (CurrentRecovery is not null || GroupFollowActive || CurrentGuide is null || switchingProfile || calibrationOpen || !HasFreshControlTelemetry(out _)) return;
        long expectedPermission = CommandAuthority.Epoch;
        var device = Device();
        if (CurrentGuide is not { } guide) return;
        if (guide.Scope != RecoveryConnectionIdentity(device)) { CancelHandGuideRecovery(); return; }
        if (guide is RecoveryGuide.CommandSent)
        {
            if (device["recovery_required"]?.GetValue<bool>() != true && TeachingCanResume(device))
            {
                RememberControlApproval(expectedPermission, activate: true);
                recovery.Teaching.Release();
                CancelHandGuideRecovery();
                message = "Ready · holding the hand-guided pose · interrupted movement discarded";
            }
            else if (state?["motion"]?["active"]?.GetValue<bool>() != true && device["recovery_required"]?.GetValue<bool>() == true)
                SetRecoveryGuideMessage(state?["message"]?.GetValue<string>() ?? "The controller could not verify return to holding. Open Settings → Diagnostics for the exact reason.");
            return;
        }
        if (guide.HandGuideGate is not { } gate) return;
        if (gate.Identity != RecoveryIdentity(device))
        {
            // Reset retires the old obstruction evidence, but the same arm's
            // visible guide must remain available for range/holding checks.
            recovery.DisarmGuide("The previous recovery record changed. Use the current step shown above; no earlier target or hand-guiding proof will be reused.");
            return;
        }
        if (guide is not RecoveryGuide.Armed) return; // The one return was already claimed; never retried.
        var mode = TeachingState.Inspect(device);
        var observed = gate.Observe(RecoveryIdentity(device), mode);
        if (observed != gate) recovery.ObserveGate(gate, observed);
        if (mode.HandGuiding) SetRecoveryGuideMessage("Hand guiding active. Move to a clear pose, then single-click the arm button to turn the green light off.");
        else if (observed.SawGuiding) SetRecoveryGuideMessage(device["teaching"]?["recover_at_pose_reason"]?.GetValue<string>() is { Length: > 0 } why
            ? why : "Hold still while the controller checks the hand-guided pose.");
        if (!await commands.WaitAsync(0, cancellation)) return;
        try
        {
            device = Device();
            if (GroupFollowActive || CurrentGuide is not RecoveryGuide.Armed armed ||
                !armed.Gate.Qualifies(RecoveryIdentity(device), TeachingState.Inspect(device), device["teaching"]?["can_recover_at_pose"]?.GetValue<bool>() == true))
                return;
            // Claim the single return before sending, so a failure is never retried.
            if (!recovery.TryClaimReturn(armed)) return;
            await Command("recover-at-pose", new(ButtonOff: true, SamePoseConfirmed: true), cancellation,
                CommandPurpose.Recovery, expectedPermissionEpoch: expectedPermission);
            recovery.MarkClaimedReturnSent("Restoring holding control at the measured pose · no power change, homing or replay");
        }
        catch (InvalidOperationException error) { SetRecoveryGuideMessage(error.Message); }
        finally { commands.Release(); }
    }

    private void StartSupportedStopRecovery(JsonObject initial, long permissionEpoch, CancellationToken cancellation)
    {
        var operation = BeginRecovery(RecoveryKind.HoldHere, initial, permissionEpoch, cancellation);
        var scope = operation.Scope;
        const string starting = "Supported recovery starting. Keep the arm supported; Cancel or Disable interrupts this sequence.";
        var stopped = initial["stop_event"] is JsonObject recorded ? (JsonObject)recorded.DeepClone() : new JsonObject { ["reason"] = initial["stop_reason"]?.DeepClone() };
        recovery.RecordSupportedStop(stopped, starting);
        operation.Attach(Task.Run(async () =>
        {
            bool entered = false;
            bool completedHolding = false;
            SupportedRecoveryOutcome? outcome = null;
            string finalStatus = starting;
            bool SameIdentity()
            {
                var receipt = telemetry.Latest;
                return RecoveryScopeCurrent(operation) && receipt is not null &&
                    TelemetryScope.Read(receipt.State, scope.Device) == new TelemetryScope(scope.Backend, scope.Device, scope.Generation);
            }
            void Progress(string status)
            {
                finalStatus = status;
                if (SameIdentity()) SetRecoveryGuideMessage(status);
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
                    operation.BeginStep(action == "reset" ? Stopwatch.GetTimestamp() : 0);
                    // The runner never sends verify-reset; Command would refuse it without the operator's answer.
                    var result = await Command(action, null, ct, action == "disable" ? CommandPurpose.Control : CommandPurpose.Recovery,
                        scope, operation.Epoch);
                    return new(result["request_id"]?.GetValue<long>() ?? 0, telemetry.Latest?.Sequence ?? 0, Stopwatch.GetTimestamp());
                }
                outcome = await SupportedStopRecovery.Run(Read, Send, async ct =>
                {
                    _ = Read();
                    GrantRecoveryHolding(operation, ct);
                    await Enable(ct, includePika: false, scope, operation.Epoch);
                }, Progress, operation.Token);
                if (SameIdentity())
                {
                    if (outcome == SupportedRecoveryOutcome.Holding)
                    {
                        CompleteRecoveryHolding(operation);
                        completedHolding = true;
                        recovery.CloseGuide();
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
                if (outcome == SupportedRecoveryOutcome.ResetConfirmationRequired)
                    RequestResetCompletion(scope, operation.Epoch);
            }
        }, CancellationToken.None));
    }
}
