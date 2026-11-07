using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private const string DefaultPikaTeachingStatus = "Arm teaching records six joints. Free the USB Pika jaws explicitly to capture their hand-guided motion in the plugin.";
    private sealed record PikaTeachingWorkspaceKey(string ProfileId, string CanDeviceId, string PikaDevice, string Distribution);
    private sealed record PikaTeachingWorkspaceState(string DisableRequest, string Status, string SavedPath, PikaTeachingReport? Unsaved);
    private readonly Dictionary<PikaTeachingWorkspaceKey, PikaTeachingWorkspaceState> pikaTeachingWorkspaces = new();
    private readonly object pikaTeachingSync = new();
    private PikaTeachingRecorder? pikaTeachingRecorder;
    private PikaTeachingReport? pikaTeachingUnsaved;
    private Task? pikaTeachingSaveWorker;
    private bool pikaTeachingConfirmation;
    private string pikaTeachingConfirmationIdentity = "";
    private string pikaTeachingStatus = DefaultPikaTeachingStatus;
    private string pikaTeachingPath = "";
    private string pikaTeachingDisableRequest = "";
    private bool pikaTeachingTransition;

    private bool PikaTeachingRecording { get { lock (pikaTeachingSync) return pikaTeachingRecorder is not null; } }
    private bool PikaTeachingControlsVisible
    {
        get { lock (pikaTeachingSync) return pikaTeachingRecorder is not null || pikaTeachingDisableRequest.Length > 0 ||
            pikaTeachingUnsaved is not null || pikaTeachingSaveWorker is { IsCompleted: false } || pikaTeachingTransition ||
            pikaTeachingConfirmation || pikaTeachingPath.Length > 0; }
    }

    private static PikaTeachingWorkspaceKey PikaTeachingKey(RobotProfile profile) =>
        new(profile.Id, profile.CanDeviceId, profile.PikaDevice, OperatingSystem.IsWindows() ? profile.WslDistribution : "");

    private void StorePikaTeachingWorkspace(RobotProfile profile)
    {
        lock (pikaTeachingSync)
        {
            // SwitchProfile first finishes/awaits recording and disk work. An
            // unfinished worker must never publish its result into a new scope.
            if (pikaTeachingRecorder is not null || pikaTeachingSaveWorker is { IsCompleted: false } || pikaTeachingTransition)
                throw new InvalidOperationException("Finish the current Pika teaching transition or save before changing robot profiles.");
            pikaTeachingWorkspaces[PikaTeachingKey(profile)] = new(pikaTeachingDisableRequest, pikaTeachingStatus, pikaTeachingPath, pikaTeachingUnsaved);
            // A release confirmation is a pending gesture, never portable state.
            pikaTeachingConfirmation = false; pikaTeachingConfirmationIdentity = "";
        }
    }

    private void RestorePikaTeachingWorkspace(RobotProfile profile)
    {
        lock (pikaTeachingSync)
        {
            if (pikaTeachingRecorder is not null || pikaTeachingSaveWorker is { IsCompleted: false } || pikaTeachingTransition)
                throw new InvalidOperationException("The previous Pika teaching operation has not finished.");
            var saved = pikaTeachingWorkspaces.GetValueOrDefault(PikaTeachingKey(profile));
            pikaTeachingDisableRequest = saved?.DisableRequest ?? "";
            pikaTeachingStatus = saved?.Status ?? DefaultPikaTeachingStatus;
            pikaTeachingPath = saved?.SavedPath ?? "";
            pikaTeachingUnsaved = saved?.Unsaved;
            pikaTeachingSaveWorker = null;
            pikaTeachingConfirmation = false; pikaTeachingConfirmationIdentity = "";
        }
    }

    public static string MeasuredPikaHoldStatus(bool recording, bool handGuiding) =>
        "Pika is holding its measured opening; fresh enabled feedback confirmed. " + (handGuiding
            ? recording ? "Hand guiding and observation recording continue. Choose Free jaws to release with confirmation."
                : "Arm hand guiding is active, but observation recording is stopped. Choose Free jaws to start a new recording."
            : recording ? "Arm hand guiding has ended; the current observation session is finishing. No recording or replay was restarted."
                : "Arm hand guiding has ended. Recording is stopped; no recording or replay was restarted.");

    private Widget PikaTeachingControls() => Widget.Column("teaching.pika",
        Widget.Label("teaching.pika.title", "Arm + Pika hand guiding") with { Appearance = "heading", Height = 28 },
        Widget.Label("teaching.pika.status", pikaTeachingStatus) with { Height = 36, Hint = pikaTeachingStatus },
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
        if (action == "cancel") { pikaTeachingConfirmation = false; await Update(cancellation); return; }
        if (action == "finish") { await FinishPikaTeaching("Operator finished", cancellation); await Update(cancellation); return; }
        if (action == "hold") { await ResumePikaMeasuredHold(cancellation); await Update(cancellation); return; }
        if (action == "retry")
        {
            lock (pikaTeachingSync) { if (pikaTeachingUnsaved is not null) QueuePikaTeachingSaveLocked(pikaTeachingUnsaved); }
            await Update(cancellation); return;
        }
        if (workspacePage != "controls" || calibrationOpen || switchingProfile)
            throw new InvalidOperationException("Open Controls before freeing Pika jaws for teaching.");
        JsonObject device = Device();
        if (!TeachingState.Inspect(device).HandGuiding)
            throw new InvalidOperationException("Single-click the arm's teach button first, so its light is solid green. Then free Pika jaws here.");
        lock (pikaTeachingSync)
            if (pikaTeachingUnsaved is not null || pikaTeachingSaveWorker is { IsCompleted: false } || pikaTeachingTransition)
                throw new InvalidOperationException("Finish and save the current Pika teaching observations first.");
        var pika = state?["pika"] as JsonObject;
        if (!PikaMatchesProfile() || pika?["owner"]?.GetValue<string>() != owner || pika["connected"]?.GetValue<bool>() != true || pika["fresh"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("This plugin must own the selected robot's fresh Pika connection before freeing its jaws.");
        string confirmationIdentity = ActiveProfile.Id + "\n" + device["id"]!.GetValue<string>() + "\n" + device["generation"]!.GetValue<long>() + "\n" + token;
        if (action == "free")
        {
            pikaTeachingConfirmation = true; pikaTeachingConfirmationIdentity = confirmationIdentity;
            await Update(cancellation); return;
        }
        if (action != "confirm" || !pikaTeachingConfirmation) throw new InvalidOperationException("Confirm that the jaws are empty or their grasp is supported first.");
        pikaTeachingConfirmation = false;
        if (pikaTeachingConfirmationIdentity != confirmationIdentity)
            throw new InvalidOperationException("The robot/control session changed. Choose Free Pika jaws and review its release confirmation again.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        string expectedDevice = device["id"]!.GetValue<string>(); long generation = device["generation"]!.GetValue<long>();
        long permissionEpoch = CommandAuthority.Epoch;
        // Existing disable retires both following and ordinary position targets.
        // No new protocol command, automatic release, enable or target is added.
        var reply = await PikaTeachingMotorCall("/api/pika/disable", new JsonObject { ["owner"] = owner }, false, cancellation, expectedToken);
        pikaOwned = false; pikaMonitoring = true;
        string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
        pikaTeachingDisableRequest = request;
        if (request.Length == 0)
            throw new InvalidOperationException("Disable was requested, but this backend cannot identify its acknowledgement. Jaws may be free; update the backend before recording.");
        pikaTeachingStatus = "Disable requested. Waiting for fresh motor feedback before recording; do not move the jaws yet.";
        long started = Stopwatch.GetTimestamp();
        bool disabledVerified = false;
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
            if (PikaMatchesProfile() && PikaTeachingRecorder.DisabledAfterRequest(pika, owner, request))
            {
                lock (pikaTeachingSync)
                {
                    if (pikaTeachingRecorder is not null)
                    {
                        disabledVerified = true;
                        pikaTeachingStatus = "Pika jaws free again · same arm/Pika observation session continues. Move the jaws by hand; choose Hold jaws here to retain a grasp.";
                        break;
                    }
                }
                var identity = PikaTeachingIdentity.Create(expectedProfile, expectedDevice, generation, ActiveProfile.PikaDevice, PikaReferenceDistribution, expectedToken);
                double? reference = PikaDisplayReferenceAngle(pika);
                var recorder = new PikaTeachingRecorder(identity, request, reference, receipt.ReceivedTicks, DateTimeOffset.UtcNow);
                long utc = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, utc, reference));
                lock (pikaTeachingSync) { pikaTeachingRecorder = recorder; pikaTeachingPath = ""; }
                pikaTeachingStatus = "Pika jaws are free. Move the arm and jaws by hand; the plugin is capturing their measured motion. End arm teaching or choose Finish & save.";
                await Update(cancellation); return;
            }
            await Task.Delay(50, cancellation);
        }
        if (disabledVerified && PikaTeachingRecording) { await Update(cancellation); return; }
        pikaTeachingStatus = "Pika disabled feedback was not confirmed in time. No recording, re-enable or replay was started.";
        throw new InvalidOperationException(pikaTeachingStatus);
    }

    private async Task ResumePikaMeasuredHold(CancellationToken cancellation)
    {
        var device = Device();
        if (workspacePage != "controls" || calibrationOpen || state?["motion"]?["active"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Return to idle Controls before holding the jaws here.");
        var pika = state?["pika"] as JsonObject;
        if (!PikaMatchesProfile() || !PikaTeachingRecorder.DisabledAfterRequest(pika, owner, pikaTeachingDisableRequest) ||
            pika?["measured_hold_resume_supported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Fresh matching disabled-jaw feedback and an updated backend are required. No enable command was sent.");
        if (pika["telemetry"]?["motor"]?["Position"] is not JsonValue raw || !raw.TryGetValue<double>(out double angle) || !double.IsFinite(angle) ||
            pika["telemetry"]?["motor"]?["Speed"] is not JsonValue measuredSpeed || !measuredSpeed.TryGetValue<double>(out double speed) || !double.IsFinite(speed) || Math.Abs(speed) > .02)
            throw new InvalidOperationException("Hold the Pika jaws still before choosing Hold jaws here.");
        if (!PikaMeasuredHoldResume.ValidMeasuredAngle(angle))
            throw new InvalidOperationException("The measured raw jaw opening is outside the supported 0–90 mm hold range (with up to 1 mm closed-reading noise). Move the free jaws back into range by hand. No motor command was sent.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        string expectedDevice = device["id"]!.GetValue<string>(); long generation = device["generation"]!.GetValue<long>();
        long permissionEpoch = CommandAuthority.Epoch;
        double expectedFeedback = pika["feedback_time_s"]!.GetValue<double>();
        var reply = await PikaTeachingMotorCall("/api/pika/resume-hold", new JsonObject { ["owner"] = owner, ["clear"] = true,
            ["expected_angle_rad"] = angle, ["expected_feedback_time_s"] = expectedFeedback }, true, cancellation, expectedToken);
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
                pikaOwned = true; pikaMonitoring = false; pikaTeachingDisableRequest = "";
                pikaTeachingStatus = MeasuredPikaHoldStatus(PikaTeachingRecording, TeachingState.Inspect(device).HandGuiding);
                return;
            }
            if (pika?["last_motor_request"]?.GetValue<string>() == request && pika["resuming_hold"]?.GetValue<bool>() == false &&
                pika["armed"]?.GetValue<bool>() == false && pika["enabled"]?.GetValue<bool>() == false)
            {
                pikaTeachingDisableRequest = request;
                lock (pikaTeachingSync) pikaTeachingRecorder?.RequestMotorMode(request, false, Stopwatch.GetTimestamp());
                throw new InvalidOperationException(pika["message"]?.GetValue<string>() ?? "Measured jaw hold failed; the agent requested disable once.");
            }
            await Task.Delay(50, cancellation);
        }
        throw new InvalidOperationException("Measured-hold acknowledgement timed out. No replay or further automatic enable was sent; inspect Pika feedback.");
    }

    private async Task<JsonObject> PikaTeachingMotorCall(string route, JsonObject body, bool enabled, CancellationToken cancellation, string expectedToken)
    {
        lock (pikaTeachingSync)
        {
            pikaTeachingTransition = true;
            pikaTeachingRecorder?.Gap(Stopwatch.GetTimestamp(), enabled ? "Explicit measured-hold transition" : "Explicit free-jaw transition");
        }
        try
        {
            var reply = await Call(route, body, cancellation, expectedToken);
            string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
            lock (pikaTeachingSync)
            {
                if (request.Length > 0) pikaTeachingRecorder?.RequestMotorMode(request, enabled, Stopwatch.GetTimestamp());
                else if (pikaTeachingRecorder is { } recorder)
                { recorder.Finish("Motor transition acknowledgement cannot be identified"); CompletePikaTeachingLocked(recorder); }
            }
            return reply;
        }
        catch
        {
            lock (pikaTeachingSync)
                if (pikaTeachingRecorder is { } recorder)
                { recorder.Finish("Motor transition request failed; inspect Pika state before starting another recording"); CompletePikaTeachingLocked(recorder); }
            throw;
        }
        finally { lock (pikaTeachingSync) pikaTeachingTransition = false; }
    }

    // Called by the telemetry reader, independent of renderer/editor latency.
    private void ObservePikaTeachingReceipt(TelemetryReceipt receipt, long receivedUtcMicroseconds)
    {
        lock (pikaTeachingSync)
        {
            var recorder = pikaTeachingRecorder;
            if (recorder is null) return;
            try
            {
                if (ActiveProfile.Id != recorder.Identity.ProfileId || ActiveProfile.PikaDevice != recorder.Identity.PikaDevice || PikaReferenceDistribution != recorder.Identity.Distribution)
                {
                    recorder.Finish("Selected robot profile or Pika assignment changed");
                    CompletePikaTeachingLocked(recorder); return;
                }
                var device = RobotProfileRouting.Device(preferences, receipt.State);
                var identity = PikaTeachingIdentity.Create(ActiveProfile.Id, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>(),
                    ActiveProfile.PikaDevice, PikaReferenceDistribution, receipt.BackendToken);
                var pika = receipt.State["pika"] as JsonObject;
                if (identity != recorder.Identity || !PikaMatchesProfile()) recorder.Finish("Robot, Pika or backend identity changed");
                else if (device["fresh"]?.GetValue<bool>() == true && !TeachingState.Inspect(device).HandGuiding)
                    recorder.Finish("Arm hand guiding ended");
                else if (device["status"]?["error"]?.GetValue<int>() is > 0 || device["joints"]?.AsArray().Any(j => j?["fault"]?.GetValue<int>() is > 0) == true)
                    recorder.Finish("Controller or joint fault observed");
                else if (PikaTeachingRecorder.AssessMotor(pika, owner, recorder.CurrentMotorRequestId, recorder.ExpectedMotorEnabled, pikaTeachingTransition) == PikaTeachingMotorDisposition.End)
                    recorder.Finish("Pika ownership or motor command changed; passive teaching ended");
                else if (PikaTeachingRecorder.AssessMotor(pika, owner, recorder.CurrentMotorRequestId, recorder.ExpectedMotorEnabled, pikaTeachingTransition) == PikaTeachingMotorDisposition.Gap)
                    recorder.Gap(receipt.ReceivedTicks, "Requested Pika motor mode is stale, transitioning or unverified");
                else
                {
                    double? reference = recorder.ClosedDisplayReferenceAngleRad;
                    recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, receivedUtcMicroseconds, reference));
                }
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { recorder.Gap(receipt.ReceivedTicks, "Incomplete robot/Pika observation omitted"); }
            if (recorder.Complete) CompletePikaTeachingLocked(recorder);
        }
    }

    private void PikaTeachingTelemetryInterrupted()
    {
        lock (pikaTeachingSync)
        {
            if (pikaTeachingRecorder is not { } recorder) return;
            recorder.Gap(Stopwatch.GetTimestamp(), "Controller telemetry stream interrupted");
            recorder.Finish("Controller telemetry stream interrupted; no automatic recording resume");
            CompletePikaTeachingLocked(recorder);
        }
    }

    private void CompletePikaTeachingLocked(PikaTeachingRecorder recorder)
    {
        pikaTeachingRecorder = null;
        QueuePikaTeachingSaveLocked(recorder.Snapshot(DateTimeOffset.UtcNow));
    }

    private void QueuePikaTeachingSaveLocked(PikaTeachingReport report)
    {
        if (pikaTeachingSaveWorker is { IsCompleted: false }) return;
        pikaTeachingUnsaved = report;
        pikaTeachingStatus = $"Saving {report.Frames.Length} arm/Pika observations. Motor power is unchanged; no automatic replay.";
        string path = Path.Combine(engine.Start.SettingsDirectory, "pika-teaching", report.Identity.ProfileId,
            report.StartedUtc.ToString("yyyyMMdd-HHmmss") + "-" + report.SessionId + ".json");
        pikaTeachingSaveWorker = Task.Run(async () =>
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(output, report, PluginProtocol.Json);
                File.Move(temporary, path, false);
                lock (pikaTeachingSync)
                {
                    pikaTeachingUnsaved = null; pikaTeachingPath = path;
                    pikaTeachingStatus = $"Saved {report.Frames.Length} measured arm/Pika observations · {report.GapCount} gaps. Motor power is unchanged; paired physical replay is unavailable.";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { lock (pikaTeachingSync) pikaTeachingStatus = "Teaching observations are unsaved: " + error.Message + " Use Retry save. Motor power is unchanged."; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Trace.TraceWarning("Pika teaching temporary-file cleanup: {0}", error.Message); }
            }
        });
    }

    private async Task FinishPikaTeaching(string reason, CancellationToken cancellation)
    {
        Task? saving;
        lock (pikaTeachingSync)
        {
            if (pikaTeachingRecorder is { } recorder) { recorder.Finish(reason); CompletePikaTeachingLocked(recorder); }
            saving = pikaTeachingSaveWorker;
        }
        if (saving is not null) await saving.WaitAsync(cancellation);
    }

    private void AddPikaTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        lock (pikaTeachingSync)
        {
            bool recording = pikaTeachingRecorder is not null;
            bool guiding = TeachingState.Inspect(device).HandGuiding;
            patches["teaching.pika"] = new() { ["visible"] = guiding || recording || pikaTeachingPath.Length > 0 || pikaTeachingDisableRequest.Length > 0 || pikaTeachingUnsaved is not null };
            string status = recording
                ? $"Pika {(pikaTeachingRecorder!.ExpectedMotorEnabled ? "holding measured opening" : "jaws free")} · capturing six measured joints + Pika · {pikaTeachingRecorder.Frames}/9000 observations · {pikaTeachingRecorder.GapCount} gaps · maximum 3 minutes"
                : pikaTeachingStatus;
            patches["teaching.pika.status"] = new() { ["text"] = status, ["hint"] = status };
            patches["teaching.pika.free"] = new() { ["enabled"] = guiding && !pikaTeachingTransition &&
                (!recording || pikaTeachingRecorder!.ExpectedMotorEnabled) && pikaTeachingUnsaved is null && pikaTeachingSaveWorker is not { IsCompleted: false } };
            patches["teaching.pika.finish"] = new() { ["enabled"] = recording };
            var pika = state?["pika"] as JsonObject;
            patches["teaching.pika.hold"] = new() { ["enabled"] = !pikaTeachingTransition && pika?["measured_hold_resume_supported"]?.GetValue<bool>() == true &&
                PikaMatchesProfile() && PikaTeachingRecorder.DisabledAfterRequest(pika, owner, pikaTeachingDisableRequest),
                ["visible"] = pikaTeachingDisableRequest.Length > 0 };
            patches["teaching.pika.retry"] = new() { ["visible"] = pikaTeachingUnsaved is not null && pikaTeachingSaveWorker is not { IsCompleted: false } };
            patches["teaching.pika.confirmation"] = new() { ["visible"] = pikaTeachingConfirmation && guiding && !pikaTeachingTransition };
            patches["teaching.pika.saved"] = new() { ["visible"] = pikaTeachingPath.Length > 0,
                ["text"] = pikaTeachingPath.Length == 0 ? "" : "Saved observations: " + pikaTeachingPath };
        }
    }
}
