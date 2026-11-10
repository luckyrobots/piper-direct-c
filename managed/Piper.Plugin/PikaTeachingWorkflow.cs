using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private const string DefaultPikaTeachingStatus = "Arm teaching records six joints. Free the USB Pika jaws explicitly to capture their hand-guided motion in the plugin.";
    private sealed record PikaTeachingWorkspaceKey(string ProfileId, string CanDeviceId, string PikaDevice, string? Distribution);

    /// <summary>Plugin-G Pika teaching workflow: one immutable state, replaced only
    /// under <see cref="pikaTeachingSync"/>.
    /// <code>
    ///   Idle --free--> Confirming(identity) --confirm--> Disabling(Transition) --disabled verified--> Recording(Recorder)
    ///   Recording --free/hold--> Transition --> Recording;  Recording --finish, end, fault, telemetry loss--> Saving(Unsaved, SaveWorker)
    ///   Saving --written--> Saved(Path) | --failed--> Unsaved --retry--> Saving
    /// </code>
    /// A pending confirmation and a motor transition share one <see cref="PikaTeachingGesture"/>,
    /// and a saved path and an unsaved report share one <see cref="PikaTeachingOutcome"/>,
    /// so neither combination can be represented, by a constructor or by <c>with</c>.</summary>
    internal sealed record PikaTeachingState(PikaTeachingRecorder? Recorder = null, PikaTeachingOutcome? Outcome = null, Task? SaveWorker = null,
        PikaTeachingGesture? Gesture = null, string DisableRequest = "", string Status = DefaultPikaTeachingStatus)
    {
        public PikaTeachingReport? Unsaved => (Outcome as PikaTeachingOutcome.Unsaved)?.Report;
        public string Path => (Outcome as PikaTeachingOutcome.Saved)?.Path ?? "";
        public string? ConfirmationIdentity => (Gesture as PikaTeachingGesture.Confirming)?.Identity;
        public bool Transition => Gesture is PikaTeachingGesture.Transitioning;
        public bool Saving => SaveWorker is { IsCompleted: false };
        /// <summary>Recording, saving or a motor transition is in progress.</summary>
        public bool Busy => Recorder is not null || Saving || Transition;
        /// <summary>Entering a transition replaces a pending confirmation; leaving it ends only the transition.</summary>
        public PikaTeachingState InTransition(bool active) =>
            active ? this with { Gesture = PikaTeachingGesture.Transitioning.Instance } : Transition ? this with { Gesture = null } : this;
        /// <summary>A confirmation cannot be opened during a motor transition.</summary>
        public PikaTeachingState Confirming(string identity) => Transition ? this : this with { Gesture = new PikaTeachingGesture.Confirming(identity) };
        public PikaTeachingState WithoutConfirmation() => Gesture is PikaTeachingGesture.Confirming ? this with { Gesture = null } : this;
        public PikaTeachingState SavingReport(PikaTeachingReport report, Task worker, string status) =>
            this with { Outcome = new PikaTeachingOutcome.Unsaved(report), SaveWorker = worker, Status = status };
        public PikaTeachingState Saved(string path, string status) => this with { Outcome = new PikaTeachingOutcome.Saved(path), Status = status };
        /// <summary>What survives a profile switch: never a pending gesture or worker.</summary>
        public PikaTeachingState Portable() => this with { Recorder = null, SaveWorker = null, Gesture = null };
    }

    /// <summary>The operator gesture in progress: a release awaiting confirmation, or a motor transition.</summary>
    internal abstract record PikaTeachingGesture
    {
        private PikaTeachingGesture() { }
        internal sealed record Confirming(string Identity) : PikaTeachingGesture;
        internal sealed record Transitioning : PikaTeachingGesture { public static readonly Transitioning Instance = new(); }
    }

    /// <summary>A finished recording's report: written to a path, or not yet saved.</summary>
    internal abstract record PikaTeachingOutcome
    {
        private PikaTeachingOutcome() { }
        internal sealed record Saved(string Path) : PikaTeachingOutcome;
        internal sealed record Unsaved(PikaTeachingReport Report) : PikaTeachingOutcome;
    }

    private readonly Dictionary<PikaTeachingWorkspaceKey, PikaTeachingState> pikaTeachingWorkspaces = new();
    private readonly object pikaTeachingSync = new();
    internal PikaTeachingState pikaTeaching = new();

    private PikaTeachingState PikaTeaching { get { lock (pikaTeachingSync) return pikaTeaching; } }
    private void UpdatePikaTeaching(Func<PikaTeachingState, PikaTeachingState> change) { lock (pikaTeachingSync) pikaTeaching = change(pikaTeaching); }
    private string pikaTeachingDisableRequest => PikaTeaching.DisableRequest;
    private bool PikaTeachingRecording => PikaTeaching.Recorder is not null;
    internal bool PikaTeachingControlsVisible
    {
        get
        {
            var current = PikaTeaching;
            return current.Recorder is not null || current.DisableRequest.Length > 0 || current.Unsaved is not null || current.Saving ||
                current.Transition || current.ConfirmationIdentity is not null || current.Path.Length > 0;
        }
    }

    private static PikaTeachingWorkspaceKey PikaTeachingKey(RobotProfile profile) =>
        new(profile.Id, profile.CanDeviceId, profile.PikaDevice, profile.PikaDistribution);

    internal void StorePikaTeachingWorkspace(RobotProfile profile)
    {
        lock (pikaTeachingSync)
        {
            // SwitchProfile first finishes/awaits recording and disk work. An
            // unfinished worker must never publish its result into a new scope.
            if (pikaTeaching.Busy)
                throw new InvalidOperationException("Finish the current Pika teaching transition or save before changing robot profiles.");
            pikaTeachingWorkspaces[PikaTeachingKey(profile)] = pikaTeaching.Portable();
            // A release confirmation is a pending gesture, never portable state.
            pikaTeaching = pikaTeaching.WithoutConfirmation();
        }
    }

    internal void RestorePikaTeachingWorkspace(RobotProfile profile)
    {
        lock (pikaTeachingSync)
        {
            if (pikaTeaching.Busy)
                throw new InvalidOperationException("The previous Pika teaching operation has not finished.");
            pikaTeaching = pikaTeachingWorkspaces.GetValueOrDefault(PikaTeachingKey(profile)) ?? new();
        }
    }

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
        if (action == "cancel") { UpdatePikaTeaching(s => s.WithoutConfirmation()); await Update(cancellation); return; }
        if (action == "finish") { await FinishPikaTeaching("Operator finished", cancellation); await Update(cancellation); return; }
        if (action == "hold") { await ResumePikaMeasuredHold(cancellation); await Update(cancellation); return; }
        if (action == "retry")
        {
            lock (pikaTeachingSync) { if (pikaTeaching is { Unsaved: { } unsaved, Saving: false }) QueuePikaTeachingSaveLocked(unsaved); }
            await Update(cancellation); return;
        }
        if (Navigation.Page != WorkspacePage.Controls || switchingProfile)
            throw new InvalidOperationException("Open Controls before freeing Pika jaws for teaching.");
        JsonObject device = Device();
        if (!TeachingState.Inspect(device).HandGuiding)
            throw new InvalidOperationException("Single-click the arm's teach button first, so its light is solid green. Then free Pika jaws here.");
        lock (pikaTeachingSync)
            if (pikaTeaching.Unsaved is not null || pikaTeaching.Saving || pikaTeaching.Transition)
                throw new InvalidOperationException("Finish and save the current Pika teaching observations first.");
        var pika = state?["pika"] as JsonObject;
        if (!PikaMatchesProfile() || pika?["owner"]?.GetValue<string>() != owner || pika["connected"]?.GetValue<bool>() != true || pika["fresh"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("This plugin must own the selected robot's fresh Pika connection before freeing its jaws.");
        string confirmationIdentity = ActiveProfile.Id + "\n" + device["id"]!.GetValue<string>() + "\n" + device["generation"]!.GetValue<long>() + "\n" + token;
        if (action == "free")
        {
            UpdatePikaTeaching(s => s.Confirming(confirmationIdentity));
            await Update(cancellation); return;
        }
        string? confirmed;
        lock (pikaTeachingSync) { confirmed = pikaTeaching.ConfirmationIdentity; pikaTeaching = pikaTeaching.WithoutConfirmation(); }
        if (action != "confirm" || confirmed is null) throw new InvalidOperationException("Confirm that the jaws are empty or their grasp is supported first.");
        if (confirmed != confirmationIdentity)
            throw new InvalidOperationException("The robot/control session changed. Choose Free Pika jaws and review its release confirmation again.");
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        string expectedDevice = device["id"]!.GetValue<string>(); long generation = device["generation"]!.GetValue<long>();
        long permissionEpoch = CommandAuthority.Epoch;
        // Existing disable retires both following and ordinary position targets.
        // No new protocol command, automatic release, enable or target is added.
        var role = telemetry.Pika.Observe();
        var reply = await PikaTeachingMotorCall("/api/pika/disable", new JsonObject { ["owner"] = owner }, false, cancellation, expectedToken);
        // A release that landed during the call stands; freed jaws never re-create a session.
        telemetry.Pika.TryAdvance(role, PikaSessionRole.Monitoring);
        string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
        UpdatePikaTeaching(s => s with { DisableRequest = request });
        if (request.Length == 0)
            throw new InvalidOperationException("Disable was requested, but this backend cannot identify its acknowledgement. Jaws may be free; update the backend before recording.");
        UpdatePikaTeaching(s => s with { Status = "Disable requested. Waiting for fresh motor feedback before recording; do not move the jaws yet." });
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
            bool continuing;
            lock (pikaTeachingSync)
            {
                continuing = pikaTeaching.Recorder is not null;
                if (continuing) pikaTeaching = pikaTeaching with { Status = "Pika jaws free again · same arm/Pika observation session continues. Move the jaws by hand; choose Hold jaws here to retain a grasp." };
            }
            if (!continuing)
            {
                var identity = PikaTeachingIdentity.Create(expectedProfile, expectedDevice, generation, ActiveProfile.PikaDevice, ActiveProfile.PikaDistribution, expectedToken);
                double? reference = PikaDisplayReferenceAngle(pika);
                var recorder = new PikaTeachingRecorder(identity, request, reference, receipt.ReceivedTicks, DateTimeOffset.UtcNow);
                long utc = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, utc, reference));
                UpdatePikaTeaching(s => s with { Recorder = recorder, Outcome = null,
                    Status = "Pika jaws are free. Move the arm and jaws by hand; the plugin is capturing their measured motion. End arm teaching or choose Finish & save." });
            }
            await Update(cancellation); return;
        }
        UpdatePikaTeaching(s => s with { Status = "Pika disabled feedback was not confirmed in time. No recording, re-enable or replay was started." });
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
                if (!telemetry.Pika.TryAdvance(role, PikaSessionRole.Owned))
                    throw new InvalidOperationException("The Pika session was released while its measured hold was verified. Control remains unarmed.");
                bool handGuiding = TeachingState.Inspect(device).HandGuiding;
                UpdatePikaTeaching(s => s with { DisableRequest = "", Status = MeasuredPikaHoldStatus(s.Recorder is not null, handGuiding) });
                return;
            }
            if (pika?["last_motor_request"]?.GetValue<string>() == request && pika["resuming_hold"]?.GetValue<bool>() == false &&
                pika["armed"]?.GetValue<bool>() == false && pika["enabled"]?.GetValue<bool>() == false)
            {
                lock (pikaTeachingSync)
                {
                    pikaTeaching = pikaTeaching with { DisableRequest = request };
                    pikaTeaching.Recorder?.RequestMotorMode(request, false, Stopwatch.GetTimestamp());
                }
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
            pikaTeaching = pikaTeaching.InTransition(true);
            pikaTeaching.Recorder?.Gap(Stopwatch.GetTimestamp(), enabled ? "Explicit measured-hold transition" : "Explicit free-jaw transition");
        }
        try
        {
            var reply = await Call(route, body, cancellation, expectedToken);
            string request = reply["motor_request_id"]?.GetValue<string>() ?? "";
            lock (pikaTeachingSync)
            {
                if (request.Length > 0) pikaTeaching.Recorder?.RequestMotorMode(request, enabled, Stopwatch.GetTimestamp());
                else if (pikaTeaching.Recorder is { } recorder)
                { recorder.Finish("Motor transition acknowledgement cannot be identified"); CompletePikaTeachingLocked(recorder); }
            }
            return reply;
        }
        catch
        {
            lock (pikaTeachingSync)
                if (pikaTeaching.Recorder is { } recorder)
                { recorder.Finish("Motor transition request failed; inspect Pika state before starting another recording"); CompletePikaTeachingLocked(recorder); }
            throw;
        }
        finally { UpdatePikaTeaching(s => s.InTransition(false)); }
    }

    // Called by the telemetry reader, independent of renderer/editor latency.
    private void ObservePikaTeachingReceipt(TelemetryReceipt receipt, long receivedUtcMicroseconds)
    {
        lock (pikaTeachingSync)
        {
            if (pikaTeaching.Recorder is not { } recorder) return;
            try
            {
                if (ActiveProfile.Id != recorder.Identity.ProfileId || ActiveProfile.PikaDevice != recorder.Identity.PikaDevice || ActiveProfile.PikaDistribution != recorder.Identity.Distribution)
                {
                    recorder.Finish("Selected robot profile or Pika assignment changed");
                    CompletePikaTeachingLocked(recorder); return;
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
                else switch (PikaTeachingRecorder.AssessMotor(pika, owner, recorder.CurrentMotorRequestId, recorder.ExpectedMotorEnabled, pikaTeaching.Transition))
                {
                    case PikaTeachingMotorDisposition.End: recorder.Finish("Pika ownership or motor command changed; passive teaching ended"); break;
                    case PikaTeachingMotorDisposition.Gap: recorder.Gap(receipt.ReceivedTicks, "Requested Pika motor mode is stale, transitioning or unverified"); break;
                    default: recorder.Observe(PikaTeachingRecorder.Read(receipt, device, pika!, identity, receivedUtcMicroseconds, recorder.ClosedDisplayReferenceAngleRad)); break;
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
            if (pikaTeaching.Recorder is not { } recorder) return;
            recorder.Gap(Stopwatch.GetTimestamp(), "Controller telemetry stream interrupted");
            recorder.Finish("Controller telemetry stream interrupted; no automatic recording resume");
            CompletePikaTeachingLocked(recorder);
        }
    }

    private void CompletePikaTeachingLocked(PikaTeachingRecorder recorder)
    {
        pikaTeaching = pikaTeaching with { Recorder = null };
        QueuePikaTeachingSaveLocked(recorder.Snapshot(DateTimeOffset.UtcNow));
    }

    private void QueuePikaTeachingSaveLocked(PikaTeachingReport report)
    {
        // A recording cannot start while a report is unsaved or saving, and retry
        // queues only when idle, so a second save would silently drop a report.
        if (pikaTeaching.Saving) throw new InvalidOperationException("A Pika teaching save is already running; the report was not replaced.");
        string path = Path.Combine(engine.Start.SettingsDirectory, "pika-teaching", report.Identity.ProfileId,
            report.StartedUtc.ToString("yyyyMMdd-HHmmss") + "-" + report.SessionId + ".json");
        // The worker updates state under the lock this caller holds, so it
        // cannot publish its result before the assignment below.
        var worker = Task.Run(async () =>
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(output, report, PluginProtocol.Json);
                File.Move(temporary, path, false);
                UpdatePikaTeaching(s => s.Saved(path,
                    $"Saved {report.Frames.Length} measured arm/Pika observations · {report.GapCount} gaps. Motor power is unchanged; paired physical replay is unavailable."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { UpdatePikaTeaching(s => s with { Status = "Teaching observations are unsaved: " + error.Message + " Use Retry save. Motor power is unchanged." }); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Trace.TraceWarning("Pika teaching temporary-file cleanup: {0}", error.Message); }
            }
        });
        pikaTeaching = pikaTeaching.SavingReport(report, worker,
            $"Saving {report.Frames.Length} arm/Pika observations. Motor power is unchanged; no automatic replay.");
    }

    private async Task FinishPikaTeaching(string reason, CancellationToken cancellation)
    {
        Task? saving;
        lock (pikaTeachingSync)
        {
            if (pikaTeaching.Recorder is { } recorder) { recorder.Finish(reason); CompletePikaTeachingLocked(recorder); }
            saving = pikaTeaching.SaveWorker;
        }
        if (saving is not null) await saving.WaitAsync(cancellation);
    }

    internal void AddPikaTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        lock (pikaTeachingSync) AddPikaTeachingPatchesLocked(patches, device, pikaTeaching);
    }

    private void AddPikaTeachingPatchesLocked(Dictionary<string, JsonObject> patches, JsonObject? device, PikaTeachingState current)
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
