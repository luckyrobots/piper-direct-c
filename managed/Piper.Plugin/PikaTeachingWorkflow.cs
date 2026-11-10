using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // Plugin-G's state belongs to recovery.PikaTeaching (PikaTeachingSession).
    // This file is the workflow around it: the guards, the motor calls and the
    // feedback waits that decide which of its transitions happens, and the view.
    private PikaTeachingState PikaTeaching => recovery.PikaTeaching.Current;
    private string pikaTeachingDisableRequest => PikaTeaching.DisableRequest;
    private bool PikaTeachingRecording => PikaTeaching.Recorder is not null;
    internal bool PikaTeachingControlsVisible => PikaTeaching.ControlsVisible;

    public static string MeasuredPikaHoldStatus(bool recording, bool handGuiding) =>
        "Pika is holding its measured opening; fresh enabled feedback confirmed. " + (handGuiding
            ? recording ? "Hand guiding and observation recording continue. Choose Free jaws to release with confirmation."
                : "Arm hand guiding is active, but observation recording is stopped. Choose Free jaws to start a new recording."
            : recording ? "Arm hand guiding has ended; the current observation session is finishing. No recording or replay was restarted."
                : "Arm hand guiding has ended. Recording is stopped; no recording or replay was restarted.");

    internal Widget PikaTeachingControls() => Widget.Column("teaching.pika",
        Widget.Label("teaching.pika.title", "Arm + Pika hand guiding") with { Appearance = "heading", Height = 28 },
        Widget.Label("teaching.pika.status", PikaTeaching.Status) with { Height = 36, Hint = PikaTeaching.Status },
        Widget.Row("teaching.pika.actions",
            Widget.Button("teaching.pika.free", "Free Pika jaws for teaching") with { Width = 230,
                Hint = "Turns off only the USB Pika motor after one confirmation. This releases its holding force. Wait for verified disabled feedback before moving the jaws by hand." },
            Widget.Button("teaching.pika.finish", "Finish & save") with { Width = 140 },
            Widget.Button("teaching.pika.hold", "Hold jaws here") with { Width = 150,
                Hint = "Explicitly power the stationary jaws at their measured opening. Previous targets are discarded. This does not replay a recording." },
            Widget.Button("teaching.pika.retry", "Retry save") with { Width = 120, Visible = false }),
        Widget.Column("teaching.pika.confirmation", Widget.Label("teaching.pika.warning",
            "Empty the jaws or support anything they hold. Freeing the jaws removes their holding force. The plugin records measured motion; it does not automatically power the jaws again.") with { Height = 60 },
            Widget.Row("teaching.pika.confirm.buttons", Widget.Button("teaching.pika.confirm", "Free jaws & record") with { Width = 190 },
                Widget.Button("teaching.pika.cancel", "Cancel") with { Width = 90 })) with { Visible = false },
        Widget.Label("teaching.pika.saved", "") with { Height = 44, Appearance = "muted", Visible = false });

    private async Task PikaTeachingEvent(string action, CancellationToken cancellation)
    {
        if (action == "cancel") { recovery.PikaTeaching.CancelConfirmation(); await Update(cancellation); return; }
        if (action == "finish") { await FinishPikaTeaching("Operator finished", cancellation); await Update(cancellation); return; }
        if (action == "hold") { await ResumePikaMeasuredHold(cancellation); await Update(cancellation); return; }
        if (action == "retry")
        {
            recovery.PikaTeaching.RetrySave();
            await Update(cancellation); return;
        }
        if (Navigation.Page != WorkspacePage.Controls || switchingProfile)
            throw new InvalidOperationException("Open Controls before freeing Pika jaws for teaching.");
        JsonObject device = Device();
        if (!TeachingState.Inspect(device).HandGuiding)
            throw new InvalidOperationException("Single-click the arm's teach button first, so its light is solid green. Then free Pika jaws here.");
        if (PikaTeaching is { Unsaved: not null } or { Saving: true } or { Transition: true })
            throw new InvalidOperationException("Finish and save the current Pika teaching observations first.");
        var pika = state?["pika"] as JsonObject;
        if (!PikaMatchesProfile() || pika?["owner"]?.GetValue<string>() != owner || pika["connected"]?.GetValue<bool>() != true || pika["fresh"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("This plugin must own the selected robot's fresh Pika connection before freeing its jaws.");
        string confirmationIdentity = ActiveProfile.Id + "\n" + device["id"]!.GetValue<string>() + "\n" + device["generation"]!.GetValue<long>() + "\n" + token;
        if (action == "free")
        {
            recovery.PikaTeaching.AskConfirmation(confirmationIdentity);
            await Update(cancellation); return;
        }
        string? confirmed = recovery.PikaTeaching.TakeConfirmation();
        if (action != "confirm" || confirmed is null) throw new InvalidOperationException("Confirm that the jaws are empty or their grasp is supported first.");
        if (confirmed != confirmationIdentity)
            throw new InvalidOperationException("The robot/control session changed. Choose Free Pika jaws and review its release confirmation again.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        string expectedDevice = device["id"]!.GetValue<string>(); long generation = device["generation"]!.GetValue<long>();
        long permissionEpoch = CommandAuthority.Epoch;
        // Existing disable retires both following and ordinary position targets.
        // No new protocol command, automatic release, enable or target is added.
        var role = telemetry.Pika.Observe();
        var reply = await PikaTeachingMotorCall(PiperApi.Requests.PikaDisable(owner, ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution), false, cancellation, expectedToken);
        // A release that landed during the call stands; freed jaws never re-create a session.
        telemetry.Pika.TryAdvance(role, PikaSessionRole.Monitoring);
        string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
        recovery.PikaTeaching.DisableRequested(request);
        if (request.Length == 0)
            throw new InvalidOperationException("Disable was requested, but this backend cannot identify its acknowledgement. Jaws may be free; update the backend before recording.");
        recovery.PikaTeaching.Report("Disable requested. Waiting for fresh motor feedback before recording; do not move the jaws yet.");
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ActiveProfile.Id != expectedProfile || permissionEpoch != CommandAuthority.Epoch)
                throw new InvalidOperationException("Control session changed after freeing the jaws. No recording or re-enable was started.");
            device = Device(expectedDevice, generation, expectedToken);
            if (!TeachingState.Inspect(device).HandGuiding)
                throw new InvalidOperationException("Arm hand guiding ended before disabled Pika feedback was confirmed. Jaws remain free; no recording or re-enable was started.");
            var receipt = telemetry.Latest!;
            pika = receipt.State["pika"] as JsonObject;
            if (!PikaMatchesProfile() || !PikaTeachingRecorder.DisabledAfterRequest(pika, owner, request))
            {
                await Task.Delay(50, cancellation);
                continue;
            }
            // Freeing the jaws again during a recording continues that session.
            if (!recovery.PikaTeaching.ContinueRecording("Pika jaws free again · same arm/Pika observation session continues. Move the jaws by hand; choose Hold jaws here to retain a grasp."))
            {
                var identity = PikaTeachingIdentity.Create(expectedProfile, expectedDevice, generation, ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution, expectedToken);
                double? reference = PikaDisplayReferenceAngle(pika);
                var recorder = new PikaTeachingRecorder(identity, request, reference, receipt.ReceivedTicks, DateTimeOffset.UtcNow);
                long utc = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, utc, reference));
                recovery.PikaTeaching.StartRecording(recorder,
                    "Pika jaws are free. Move the arm and jaws by hand; the plugin is capturing their measured motion. End arm teaching or choose Finish & save.");
            }
            await Update(cancellation); return;
        }
        recovery.PikaTeaching.Report("Pika disabled feedback was not confirmed in time. No recording, re-enable or replay was started.");
        throw new InvalidOperationException(PikaTeaching.Status);
    }

    private async Task ResumePikaMeasuredHold(CancellationToken cancellation)
    {
        var device = Device();
        if (Navigation.Page != WorkspacePage.Controls || state?["motion"]?["active"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Return to idle Controls before holding the jaws here.");
        var pika = state?["pika"] as JsonObject;
        if (!PikaMatchesProfile() || !PikaTeachingRecorder.DisabledAfterRequest(pika, owner, pikaTeachingDisableRequest) ||
            pika?["measured_hold_resume_supported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Fresh matching disabled-jaw feedback and an updated backend are required. No enable command was sent.");
        if (pika["telemetry"]?["motor"]?["Position"] is not JsonValue raw || !raw.TryGetValue<double>(out double angle) || !double.IsFinite(angle) ||
            pika["telemetry"]?["motor"]?["Speed"] is not JsonValue measuredSpeed || !measuredSpeed.TryGetValue<double>(out double speed) || !double.IsFinite(speed) ||
            Math.Abs(speed) > PikaProtocol.StationarySpeedRadiansPerSecond)
            throw new InvalidOperationException("Hold the Pika jaws still before choosing Hold jaws here.");
        if (!PikaMeasuredHoldResume.ValidMeasuredAngle(angle))
            throw new InvalidOperationException($"The measured raw jaw opening is outside the supported {PikaProtocol.MinimumOpeningMm}–{PikaProtocol.MaximumOpeningMm} mm hold range (with up to 1 mm closed-reading noise). Move the free jaws back into range by hand. No motor command was sent.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        string expectedDevice = device["id"]!.GetValue<string>(); long generation = device["generation"]!.GetValue<long>();
        long permissionEpoch = CommandAuthority.Epoch;
        double expectedFeedback = pika["feedback_time_s"]!.GetValue<double>();
        var role = telemetry.Pika.Observe();
        var reply = await PikaTeachingMotorCall(PiperApi.Requests.PikaResumeHold(owner, clear: true, expectedAngleRad: angle, expectedFeedbackTimeS: expectedFeedback,
            ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution), true, cancellation, expectedToken);
        string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
        if (request.Length == 0) throw new InvalidOperationException("Measured hold was requested but its acknowledgement cannot be identified. Control remains unarmed.");
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ActiveProfile.Id != expectedProfile || permissionEpoch != CommandAuthority.Epoch)
                throw new InvalidOperationException("Control scope changed while verifying measured jaw hold; old targets will not resume.");
            device = Device(expectedDevice, generation, expectedToken);
            pika = telemetry.Latest!.State["pika"] as JsonObject;
            if (pika?["last_motor_request"]?.GetValue<string>() == request && pika["fresh"]?.GetValue<bool>() == true &&
                pika["owner"]?.GetValue<string>() == owner && pika["armed"]?.GetValue<bool>() == true && pika["enabled"]?.GetValue<bool>() == true &&
                pika["feedback_time_s"]?.GetValue<double>() > pika["request_time_s"]?.GetValue<double>())
            {
                if (!telemetry.Pika.TryAdvance(role, PikaSessionRole.Owned))
                    throw new InvalidOperationException("The Pika session was released while its measured hold was verified. Control remains unarmed.");
                bool handGuiding = TeachingState.Inspect(device).HandGuiding;
                recovery.PikaTeaching.HoldVerified(recording => MeasuredPikaHoldStatus(recording, handGuiding));
                return;
            }
            if (pika?["last_motor_request"]?.GetValue<string>() == request && pika["resuming_hold"]?.GetValue<bool>() == false &&
                pika["armed"]?.GetValue<bool>() == false && pika["enabled"]?.GetValue<bool>() == false)
            {
                recovery.PikaTeaching.HoldFailed(request);
                throw new InvalidOperationException(pika["message"]?.GetValue<string>() ?? "Measured jaw hold failed; the agent requested disable once.");
            }
            await Task.Delay(50, cancellation);
        }
        throw new InvalidOperationException("Measured-hold acknowledgement timed out. No replay or further automatic enable was sent; inspect Pika feedback.");
    }

    private async Task<JsonObject> PikaTeachingMotorCall(ApiRequest request, bool enabled, CancellationToken cancellation, string expectedToken)
    {
        recovery.PikaTeaching.BeginMotorTransition(enabled);
        try
        {
            var reply = await Call(request, cancellation, expectedToken);
            recovery.PikaTeaching.MotorRequested(reply["motor_request_id"]?.GetValue<string>() ?? "", enabled);
            return reply;
        }
        catch
        {
            recovery.PikaTeaching.MotorRequestFailed();
            throw;
        }
        finally { recovery.PikaTeaching.EndMotorTransition(); }
    }

    // Called by the telemetry reader, independent of renderer/editor latency.
    private void ObservePikaTeachingReceipt(TelemetryReceipt receipt, long receivedUtcMicroseconds) =>
        recovery.PikaTeaching.Observe((recorder, current) =>
        {
            try
            {
                if (ActiveProfile.Id != recorder.Identity.ProfileId || ActiveProfile.PikaDevice != recorder.Identity.PikaDevice || ActiveProfile.PikaDistribution != recorder.Identity.Distribution)
                {
                    recorder.Finish("Selected robot profile or Pika assignment changed");
                    return;
                }
                var device = RobotProfileRouting.Device(preferences, receipt.State);
                var identity = PikaTeachingIdentity.Create(ActiveProfile.Id, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>(),
                    ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution, receipt.BackendToken);
                var pika = receipt.State["pika"] as JsonObject;
                if (identity != recorder.Identity || !PikaMatchesProfile()) recorder.Finish("Robot, Pika or backend identity changed");
                else if (device["fresh"]?.GetValue<bool>() == true && !TeachingState.Inspect(device).HandGuiding)
                    recorder.Finish("Arm hand guiding ended");
                else if (device["status"]?["error"]?.GetValue<int>() is > 0 || device["joints"]?.AsArray().Any(j => j?["fault"]?.GetValue<int>() is > 0) == true)
                    recorder.Finish("Controller or joint fault observed");
                else switch (PikaTeachingRecorder.AssessMotor(pika, owner, recorder.CurrentMotorRequestId, recorder.ExpectedMotorEnabled, current.Transition))
                {
                    case PikaTeachingMotorDisposition.End: recorder.Finish("Pika ownership or motor command changed; passive teaching ended"); break;
                    case PikaTeachingMotorDisposition.Gap: recorder.Gap(receipt.ReceivedTicks, "Requested Pika motor mode is stale, transitioning or unverified"); break;
                    default: recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, receivedUtcMicroseconds, recorder.ClosedDisplayReferenceAngleRad)); break;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { recorder.Gap(receipt.ReceivedTicks, "Incomplete robot/Pika observation omitted"); }
        });

    private void PikaTeachingTelemetryInterrupted() => recovery.PikaTeaching.Interrupt();

    private async Task FinishPikaTeaching(string reason, CancellationToken cancellation)
    {
        if (recovery.PikaTeaching.Finish(reason) is { } saving) await saving.WaitAsync(cancellation);
    }

    internal void AddPikaTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device) =>
        recovery.PikaTeaching.Inspect(current => AddPikaTeachingPatches(patches, device, current));

    private void AddPikaTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device, PikaTeachingState current)
    {
        bool recording = current.Recorder is not null;
        bool guiding = TeachingState.Inspect(device).HandGuiding;
        patches["teaching.pika"] = new() { ["visible"] = guiding || recording || current.Path.Length > 0 || current.DisableRequest.Length > 0 || current.Unsaved is not null };
        string status = current.Recorder is { } recorder
                ? $"Pika {(recorder.ExpectedMotorEnabled ? "holding measured opening" : "jaws free")} · capturing six measured joints + Pika · {recorder.Frames}/9000 observations · {recorder.GapCount} gaps · maximum 3 minutes"
                : current.Status;
        patches["teaching.pika.status"] = new() { ["text"] = status, ["hint"] = status };
        patches["teaching.pika.free"] = new() { ["enabled"] = guiding && !current.Transition &&
            (!recording || current.Recorder!.ExpectedMotorEnabled) && current.Unsaved is null && !current.Saving };
        patches["teaching.pika.finish"] = new() { ["enabled"] = recording };
        var pika = state?["pika"] as JsonObject;
        patches["teaching.pika.hold"] = new() { ["enabled"] = !current.Transition && pika?["measured_hold_resume_supported"]?.GetValue<bool>() == true &&
            PikaMatchesProfile() && PikaTeachingRecorder.DisabledAfterRequest(pika, owner, current.DisableRequest),
            ["visible"] = current.DisableRequest.Length > 0 };
        patches["teaching.pika.retry"] = new() { ["visible"] = current.Unsaved is not null && !current.Saving };
        patches["teaching.pika.confirmation"] = new() { ["visible"] = current.ConfirmationIdentity is not null && guiding && !current.Transition };
        patches["teaching.pika.saved"] = new() { ["visible"] = current.Path.Length > 0,
            ["text"] = current.Path.Length == 0 ? "" : "Saved observations: " + current.Path };
    }
}
