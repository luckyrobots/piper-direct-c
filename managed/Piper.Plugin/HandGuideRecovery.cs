using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>A recovery explanation and one explicit next action, derived from the selected arm's live evidence.</summary>
public sealed record RecoveryWorkspaceState(string Cause, string NextStep, string Requirements, string? Action, string ActionText, bool CanAct)
{
    public static RecoveryWorkspaceState Inspect(JsonObject? device, bool moving, bool waiting = false)
    {
        string cause = device?["stop_event"]?["reason"]?.GetValue<string>() is { Length: > 0 } recorded ? recorded
            : device?["stop_reason"]?.GetValue<string>() is { Length: > 0 } stopped ? stopped : "Robot control needs recovery.";
        string range = RangeRequirements(device);
        double strict = Number(device?["status"]?["stationary_s"]), pose = Number(device?["status"]?["pose_stationary_s"]);
        string evidence = $"Measured pose stable: {pose:F2} s · power/return stillness: {strict:F2} s (reset requires 0.40 s).";
        string requirements = string.Join("\n", new[] { range, evidence }.Where(line => line.Length > 0));
        RecoveryWorkspaceState Step(string next, string? action = null, string label = "Waiting for controller feedback", bool enabled = false) =>
            new(cause, next, requirements, action, label, enabled);
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
            if (!healthyDrivers) return Step(plan.NextStep, power.EnabledCount > 0 ? "disable" : null,
                power.EnabledCount > 0 ? "Arm supported — Disable arm motors" : "Inspect the driver fault first", power.EnabledCount > 0);
            return Step("Rest or support the arm and clear its surroundings, then click Arm supported — recover. One acknowledgement disables the motors, clears STOP once, verifies recovery and restores holding only at a legal measured pose. The green button alone cannot clear controller STOP.",
                "supported-stop", "Arm supported — recover", true);
        }
        if (teaching.HandGuiding)
            return Step("Green light is on: move the arm to a clear pose within the ranges shown below. Single-click its button again to turn the light off, then keep it still.", label: "Turn the green light off when ready");
        if (teaching.Active) return Step("Onboard playback still owns the arm. End playback using the physical controls before recovering.", label: "End onboard playback first");
        bool recovery = device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true;
        if (power.AllEnabled && device["teaching"]?["can_recover_at_pose"]?.GetValue<bool>() == true &&
            device["teaching"]?["recovery_teach_cycle_complete"]?.GetValue<bool>() == true)
            return Step("The controller verified the completed green-light hand-guiding cycle and this measured pose. Resume holds here and discards the interrupted target.",
                "recover-at-pose", "Resume controls at this pose", true);
        if (recovery && plan.Action is "verify-reset" or "verify-current")
            return Step(plan.NextStep, plan.Action, "Verify controller recovery", true);
        if (recovery)
        {
            string why = device["teaching"]?["recover_at_pose_reason"]?.GetValue<string>() ?? device["blocker"]?.GetValue<string>() ?? plan.NextStep;
            return Step("Single-click the physical button for green hand guiding, move clear, then click it again to finish. " + why,
                label: "Waiting for a qualified hand-guided pose");
        }
        if (teaching.ReturnRequired)
            return Step(teaching.CanReturn ? teaching.SamePoseReturn ? "The green light is off. Resume controls to hold this measured pose without homing or replay."
                    : "The controller verified its required zero pose. Complete the supported return to CAN; no homing or replay will run." : teaching.NextStep,
                teaching.CanReturn ? "teach-return" : null, teaching.SamePoseReturn ? "Resume controls at this pose" : "Return to controls at verified zero", teaching.CanReturn);
        if (power.EnabledCount == 0)
        {
            bool legal = range.Length == 0 && string.IsNullOrEmpty(device["blocker"]?.GetValue<string>());
            return Step(legal ? "Controller recovery is complete. Keep the arm supported and enable holding at its current measured pose."
                : "Controller STOP is cleared and all motors are off. Support the arm. Use the physical green-light hand-guiding mode to bring the listed joints into range, then turn the light off. " + (device["blocker"]?.GetValue<string>() ?? ""),
                legal ? "enable" : null, "Arm supported — Enable holding here", legal);
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
    private volatile bool supportedRecoveryBusy;
    private CancellationTokenSource? supportedRecoveryCancellation;
    private Task? supportedRecoveryWorker;
    private JsonObject? supportedRecoveryStop;
    private volatile bool supportedRecoveryResetting;
    private string supportedRecoveryBackend = "", supportedRecoveryDevice = "", supportedRecoveryProfile = "";
    private long supportedRecoveryGeneration, supportedRecoveryPermission, supportedRecoveryResetAt;

    private bool SupportedRecoveryExpectedGap(TelemetryReceipt? receipt)
    {
        long now = Stopwatch.GetTimestamp();
        if (!supportedRecoveryBusy || !supportedRecoveryResetting || supportedRecoveryCancellation?.IsCancellationRequested == true || supportedRecoveryResetAt == 0 ||
            Stopwatch.GetElapsedTime(supportedRecoveryResetAt, now).TotalSeconds > 5 ||
            token != supportedRecoveryBackend || ActiveProfile.Id != supportedRecoveryProfile ||
            supportedRecoveryPermission != Interlocked.Read(ref targetPermissionEpoch) ||
            receipt is null || receipt.BackendToken != supportedRecoveryBackend || !receipt.IsFresh(now) ||
            receipt.State["selected"]?.GetValue<string>() != supportedRecoveryDevice) return false;
        var device = receipt.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == supportedRecoveryDevice);
        return device?["generation"]?.GetValue<long>() == supportedRecoveryGeneration && device["present"]?.GetValue<bool>() == true &&
            device["up"]?.GetValue<bool>() == true && device["status"]?["error"]?.GetValue<int>() == 0 &&
            device["status"]?["arm_status"]?.GetValue<int>() is 0 or 1 && device["joints"] is JsonArray { Count: 6 } joints &&
            joints.All(j => j?["fault"]?.GetValue<int>() == 0) && !receipt.HasFreshDevice(device, now);
    }

    private string RecoveryConnectionIdentity(JsonObject device) => token + ":" + ActiveProfile.Id + ":" +
        device["id"]?.GetValue<string>() + ":" + device["generation"]?.GetValue<long>();

    private bool RecoveryGuideMatches(JsonObject? device) => device is not null && recoveryGuideOpen && recoveryGuideScope == RecoveryConnectionIdentity(device);

    private async Task RunRecoveryCard(CancellationToken cancellation)
    {
        if (workspacePage != "controls" || calibrationOpen) throw new InvalidOperationException("Return to Controls to use the recovery guide.");
        var device = Device();
        var view = RecoveryWorkspaceState.Inspect(device, state?["motion"]?["active"]?.GetValue<bool>() == true, recoveryInProgress);
        if (!view.CanAct || view.Action is null) throw new InvalidOperationException(view.NextStep);
        recoveryGuideOpen = true; recoveryGuideScope = RecoveryConnectionIdentity(device);
        try
        {
            if (view.Action == "supported-stop") StartSupportedStopRecovery(device);
            else if (view.Action == "disable")
            {
                approved = linkCommandsEnabled = false; ForgetControlApproval();
                await Command("disable", null, cancellation);
                handGuideRecoveryMessage = "Disable requested. Keep supporting the arm until all six motors report off; the next recovery action appears here.";
            }
            else if (view.Action == "enable")
            {
                followAfterEnable = false; approved = true;
                await RunPrimaryAction(CompleteRobotEnable, cancellation);
                CancelHandGuideRecovery();
            }
            else if (view.Action is "teach-return" or "recover-at-pose")
            {
                var flags = view.Action == "teach-return" ? TeachingState.Inspect(device).ReturnFlags()
                    : new JsonObject { ["button_off"] = true, ["same_pose_confirmed"] = true };
                await Command(view.Action, flags, cancellation,
                    recoveryAcknowledged: view.Action == "recover-at-pose", teachingAcknowledged: view.Action == "teach-return");
                handGuideRecoveryCommandSent = true;
                handGuideRecoveryMessage = "Restoring holding control at the measured pose. No homing, replay or power change.";
            }
            else
            {
                var result = await Command(view.Action, null, cancellation, recoveryAcknowledged: true);
                Interlocked.Exchange(ref recoveryRequestId, result["request_id"]?.GetValue<long>() ?? 0);
                Interlocked.Exchange(ref recoveryAcknowledgedSequence, telemetry.Latest?.Sequence ?? 0);
                Interlocked.Exchange(ref recoveryAcknowledgedTicks, Stopwatch.GetTimestamp());
                recoveryInProgress = view.Action == "reset";
                handGuideRecoveryMessage = view.Action == "reset" ? "Reset requested. Keep the arm supported while disabled-motor feedback is verified. No motor enable or position target will be sent." : "Controller recovery verification requested.";
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
        try { supportedRecoveryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        supportedRecoveryStop = null;
        recoveryGuideOpen = false; recoveryGuideScope = "";
        handGuideRecovery = null;
        handGuideRecoveryCommandSent = false;
        handGuideRecoveryMessage = "";
    }

    private async Task ObserveHandGuideRecovery(CancellationToken cancellation)
    {
        if (supportedRecoveryBusy || (!recoveryGuideOpen && handGuideRecovery is null) || switchingProfile || calibrationOpen || !HasFreshControlTelemetry(out _)) return;
        var device = Device();
        if (recoveryGuideScope != RecoveryConnectionIdentity(device)) { CancelHandGuideRecovery(); return; }
        if (handGuideRecoveryCommandSent)
        {
            if (device["recovery_required"]?.GetValue<bool>() != true && TeachingCanResume(device))
            {
                approved = true; linkCommandsEnabled = link is not null;
                RememberControlApproval();
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
            if (!gate.Observe(RecoveryIdentity(device), TeachingState.Inspect(device), device["teaching"]?["can_recover_at_pose"]?.GetValue<bool>() == true)) return;
            await Command("recover-at-pose", new() { ["button_off"] = true, ["same_pose_confirmed"] = true }, cancellation, recoveryAcknowledged: true);
            handGuideRecoveryCommandSent = true;
            handGuideRecoveryMessage = "Restoring holding control at the measured pose · no power change, homing or replay";
        }
        catch (InvalidOperationException error) { handGuideRecoveryMessage = error.Message; }
        finally { commands.Release(); }
    }

    private void StartSupportedStopRecovery(JsonObject initial)
    {
        if (supportedRecoveryBusy) throw new InvalidOperationException("Supported recovery is already in progress.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id, expectedDevice = initial["id"]!.GetValue<string>();
        long expectedGeneration = initial["generation"]!.GetValue<long>(), expectedPermission = Interlocked.Read(ref targetPermissionEpoch);
        supportedRecoveryBackend = expectedToken; supportedRecoveryDevice = expectedDevice; supportedRecoveryProfile = expectedProfile;
        supportedRecoveryGeneration = expectedGeneration; supportedRecoveryPermission = expectedPermission;
        supportedRecoveryResetting = false; supportedRecoveryResetAt = 0;
        supportedRecoveryStop = initial["stop_event"] is JsonObject stopped ? (JsonObject)stopped.DeepClone() : new JsonObject { ["reason"] = initial["stop_reason"]?.DeepClone() };
        supportedRecoveryCancellation?.Dispose();
        supportedRecoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var operation = supportedRecoveryCancellation;
        supportedRecoveryBusy = true;
        approved = linkCommandsEnabled = false; ForgetControlApproval();
        lease = followRequest = lastCommand = 0; followInput.End();
        handGuideRecoveryMessage = "Supported recovery starting. Keep the arm supported; Cancel or Disable interrupts this sequence.";
        supportedRecoveryWorker = Task.Run(async () =>
        {
            bool entered = false;
            string finalStatus = handGuideRecoveryMessage;
            bool SameIdentity()
            {
                var receipt = telemetry.Latest;
                if (token != expectedToken || ActiveProfile.Id != expectedProfile || ActiveProfile.CanDeviceId != expectedDevice ||
                    expectedPermission != Interlocked.Read(ref targetPermissionEpoch) || receipt is null || receipt.BackendToken != expectedToken ||
                    receipt.State["selected"]?.GetValue<string>() != expectedDevice) return false;
                return receipt.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == expectedDevice)?["generation"]?.GetValue<long>() == expectedGeneration;
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
                    if (ActiveProfile.Id != expectedProfile || token != expectedToken || expectedPermission != Interlocked.Read(ref targetPermissionEpoch))
                        throw new InvalidOperationException("Recovery cancelled because robot identity or control permission changed. No previous target will resume.");
                    var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for live recovery feedback.");
                    bool expectedGap = SupportedRecoveryExpectedGap(receipt);
                    var device = expectedGap ? RobotProfileRouting.Device(preferences, receipt.State) : Device(expectedDevice, expectedGeneration, expectedToken, receipt);
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
                    if (action == "reset") { supportedRecoveryResetAt = Stopwatch.GetTimestamp(); supportedRecoveryResetting = true; }
                    else supportedRecoveryResetting = false;
                    var result = await Command(action, null, ct, recoveryAcknowledged: action != "disable",
                        expectedDeviceId: expectedDevice, expectedGeneration: expectedGeneration, expectedToken: expectedToken,
                        expectedPermissionEpoch: expectedPermission);
                    return new(result["request_id"]?.GetValue<long>() ?? 0, telemetry.Latest?.Sequence ?? 0, Stopwatch.GetTimestamp());
                }
                bool held = await SupportedStopRecovery.Run(Read, Send, async ct =>
                {
                    _ = Read(); approved = true;
                    await Enable(ct, includePika: false, expectedDeviceId: expectedDevice, expectedGeneration: expectedGeneration, expectedToken: expectedToken);
                    _ = Read(); RememberControlApproval();
                }, Progress, operation.Token);
                if (SameIdentity())
                {
                    if (held) { handGuideRecovery = null; handGuideRecoveryCommandSent = false; recoveryGuideOpen = false; }
                    message = finalStatus;
                }
            }
            catch (OperationCanceledException) { Progress("Supported recovery cancelled or timed out. No reset retry, homing or old target will run. Inspect the current state above."); }
            catch (Exception error) { Progress("Recovery paused: " + error.Message); }
            finally
            {
                supportedRecoveryResetting = false; supportedRecoveryResetAt = 0; supportedRecoveryBusy = false;
                if (entered) commands.Release();
            }
        }, CancellationToken.None);
    }

    private async Task StopSupportedRecoveryOnDispose()
    {
        try { supportedRecoveryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        if (supportedRecoveryWorker is { } worker)
            try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Supported recovery shutdown: {0}", error.Message); }
        supportedRecoveryCancellation?.Dispose();
    }
}
