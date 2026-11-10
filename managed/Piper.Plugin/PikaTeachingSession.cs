using System.Diagnostics;
using System.Text.Json;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-G Pika teaching: one immutable state.
/// <code>
///   Idle --free--> Confirming(identity) --confirm--> Disabling(Transition) --disabled verified--> Recording(Recorder)
///   Recording --free/hold--> Transition --> Recording;  Recording --finish, end, fault, telemetry loss--> Saving(Unsaved, SaveWorker)
///   Saving --written--> Saved(Path) | --failed--> Unsaved --retry--> Saving
/// </code>
/// A pending confirmation and a motor transition share one <see cref="PikaTeachingGesture"/>,
/// and a saved path and an unsaved report share one <see cref="PikaTeachingOutcome"/>,
/// so neither combination can be represented, by a constructor or by <c>with</c>.</summary>
internal sealed record PikaTeachingState(PikaTeachingRecorder? Recorder = null, PikaTeachingOutcome? Outcome = null, Task? SaveWorker = null,
    PikaTeachingGesture? Gesture = null, string DisableRequest = "", string Status = PikaTeachingState.DefaultStatus)
{
    public const string DefaultStatus = "Arm teaching records six joints. Free the USB Pika jaws explicitly to capture their hand-guided motion in the plugin.";

    public PikaTeachingReport? Unsaved => (Outcome as PikaTeachingOutcome.Unsaved)?.Report;
    public string Path => (Outcome as PikaTeachingOutcome.Saved)?.Path ?? "";
    public string? ConfirmationIdentity => (Gesture as PikaTeachingGesture.Confirming)?.Identity;
    public bool Transition => Gesture is PikaTeachingGesture.Transitioning;
    public bool Saving => SaveWorker is { IsCompleted: false };
    /// <summary>Recording, saving or a motor transition is in progress.</summary>
    public bool Busy => Recorder is not null || Saving || Transition;
    /// <summary>Something the operator still has to see: the teaching controls stay on the panel.</summary>
    public bool ControlsVisible => Recorder is not null || DisableRequest.Length > 0 || Unsaved is not null || Saving ||
        Transition || ConfirmationIdentity is not null || Path.Length > 0;
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

/// <summary>Plugin-G, a part of the Recovery component: the Pika teaching state,
/// the state each robot profile left behind, and the save of a finished
/// recording. Nothing outside this class can assign them.
/// <code>
///   field       written by                                     readers see
///   state       every transition below, under <c>sync</c>:     one volatile reference to an
///               the UI actor (gestures, motor transitions,     immutable PikaTeachingState,
///               start, hold, retry, store, restore, finish),   read without the lock
///               the telemetry loop (Observe, Interrupt), the   (<see cref="Current"/>)
///               STOP follow-up and shutdown (Finish) and the
///               save worker (its own result)
///   workspaces  the UI actor (Store), under <c>sync</c>        nothing: Restore copies one out
///   Recorder    the same transitions, under <c>sync</c>; it    its frame and gap counts only
///               is mutable and not safe to read outside it     through <see cref="Inspect"/>
/// </code>
/// The lock is not on the STOP path: STOP's handler never calls this class.
/// Its detached follow-up calls <see cref="Finish"/>.
///
/// The workflows that decide when a transition happens (the motor calls, the
/// feedback waits and their guards) stay in <c>PipersPlugin</c>: they read
/// permission, telemetry and the selected profile.</summary>
internal sealed class PikaTeachingSession(Func<string> settingsDirectory, Func<PikaTeachingReport, string, Task>? write = null)
{
    private sealed record WorkspaceKey(string ProfileId, string CanDeviceId, string PikaDevice, string? Distribution);

    private readonly Func<PikaTeachingReport, string, Task> write = write ?? WriteReport;
    private readonly Dictionary<WorkspaceKey, PikaTeachingState> workspaces = new();
    private readonly object sync = new();
    private volatile PikaTeachingState state = new();

    public PikaTeachingState Current => state;

    /// <summary>Reads the state together with its recorder's counters, which are only stable under the lock.</summary>
    public void Inspect(Action<PikaTeachingState> read) { lock (sync) read(state); }

    // ---- the release gesture ----

    public void AskConfirmation(string identity) => Change(s => s.Confirming(identity));

    public void CancelConfirmation() => Change(s => s.WithoutConfirmation());

    /// <summary>Removes the pending confirmation and returns what it was given for; null when none was pending.</summary>
    public string? TakeConfirmation()
    {
        lock (sync)
        {
            string? confirmed = state.ConfirmationIdentity;
            state = state.WithoutConfirmation();
            return confirmed;
        }
    }

    // ---- motor power ----

    /// <summary>A free or hold request is about to be sent; the recording notes the gap.</summary>
    public void BeginMotorTransition(bool enabled)
    {
        lock (sync)
        {
            state = state.InTransition(true);
            state.Recorder?.Gap(Stopwatch.GetTimestamp(), enabled ? "Explicit measured-hold transition" : "Explicit free-jaw transition");
        }
    }

    /// <summary>The backend answered. A reply that cannot be identified ends the recording.</summary>
    public void MotorRequested(string request, bool enabled)
    {
        lock (sync)
        {
            if (request.Length > 0) state.Recorder?.RequestMotorMode(request, enabled, Stopwatch.GetTimestamp());
            else if (state.Recorder is { } recorder)
            { recorder.Finish("Motor transition acknowledgement cannot be identified"); CompleteLocked(recorder); }
        }
    }

    public void MotorRequestFailed()
    {
        lock (sync)
            if (state.Recorder is { } recorder)
            { recorder.Finish("Motor transition request failed; inspect Pika state before starting another recording"); CompleteLocked(recorder); }
    }

    public void EndMotorTransition() => Change(s => s.InTransition(false));

    /// <summary>The jaws were asked to go free under <paramref name="request"/>; empty when the backend could not name it.</summary>
    public void DisableRequested(string request) => Change(s => s with { DisableRequest = request });

    /// <summary>Fresh enabled feedback confirmed the measured hold: the jaws are no longer free.</summary>
    public void HoldVerified(Func<bool, string> statusWhenRecording) =>
        Change(s => s with { DisableRequest = "", Status = statusWhenRecording(s.Recorder is not null) });

    /// <summary>The measured hold failed and the agent disabled the motor again under <paramref name="request"/>.</summary>
    public void HoldFailed(string request)
    {
        lock (sync)
        {
            state = state with { DisableRequest = request };
            state.Recorder?.RequestMotorMode(request, false, Stopwatch.GetTimestamp());
        }
    }

    public void Report(string status) => Change(s => s with { Status = status });

    // ---- recording ----

    /// <summary>Freeing the jaws again during a recording continues that session. False when none is running.</summary>
    public bool ContinueRecording(string status)
    {
        lock (sync)
        {
            if (state.Recorder is null) return false;
            state = state with { Status = status };
            return true;
        }
    }

    public void StartRecording(PikaTeachingRecorder recorder, string status) =>
        Change(s => s with { Recorder = recorder, Outcome = null, Status = status });

    /// <summary>The telemetry loop feeds one receipt to the running recorder; a recorder
    /// that <paramref name="observe"/> finished is saved. Nothing happens when none is running.</summary>
    public void Observe(Action<PikaTeachingRecorder, PikaTeachingState> observe)
    {
        lock (sync)
        {
            if (state.Recorder is not { } recorder) return;
            observe(recorder, state);
            if (recorder.Complete) CompleteLocked(recorder);
        }
    }

    /// <summary>The telemetry stream broke: the recording ends and is never resumed automatically.</summary>
    public void Interrupt()
    {
        lock (sync)
        {
            if (state.Recorder is not { } recorder) return;
            recorder.Gap(Stopwatch.GetTimestamp(), "Controller telemetry stream interrupted");
            recorder.Finish("Controller telemetry stream interrupted; no automatic recording resume");
            CompleteLocked(recorder);
        }
    }

    /// <summary>Ends a running recording and returns the save to wait for; null when there is nothing to save.</summary>
    public Task? Finish(string reason)
    {
        lock (sync)
        {
            if (state.Recorder is { } recorder) { recorder.Finish(reason); CompleteLocked(recorder); }
            return state.SaveWorker;
        }
    }

    /// <summary>Saves the unsaved report again, unless a save is already running.</summary>
    public void RetrySave()
    {
        lock (sync) { if (state is { Unsaved: { } unsaved, Saving: false }) QueueSaveLocked(unsaved); }
    }

    // ---- robot profiles ----

    /// <summary>Keeps this profile's state for its return. The caller has finished
    /// the recording and awaited the save: an unfinished worker must never
    /// publish its result into another profile.</summary>
    public void Store(RobotProfile profile)
    {
        lock (sync)
        {
            if (state.Busy)
                throw new InvalidOperationException("Finish the current Pika teaching transition or save before changing robot profiles.");
            workspaces[Key(profile)] = state.Portable();
            // A release confirmation is a pending gesture, never portable state.
            state = state.WithoutConfirmation();
        }
    }

    /// <summary>Shows what the exact same profile, adapter and Pika left behind, or a fresh state.</summary>
    public void Restore(RobotProfile profile)
    {
        lock (sync)
        {
            if (state.Busy)
                throw new InvalidOperationException("The previous Pika teaching operation has not finished.");
            state = workspaces.GetValueOrDefault(Key(profile)) ?? new();
        }
    }

    private static WorkspaceKey Key(RobotProfile profile) => new(profile.Id, profile.CanDeviceId, profile.PikaDevice, profile.PikaDistribution);

    private void Change(Func<PikaTeachingState, PikaTeachingState> change) { lock (sync) state = change(state); }

    private void CompleteLocked(PikaTeachingRecorder recorder)
    {
        state = state with { Recorder = null };
        QueueSaveLocked(recorder.Snapshot(DateTimeOffset.UtcNow));
    }

    private void QueueSaveLocked(PikaTeachingReport report)
    {
        // A recording cannot start while a report is unsaved or saving, and retry
        // queues only when idle, so a second save would silently drop a report.
        if (state.Saving) throw new InvalidOperationException("A Pika teaching save is already running; the report was not replaced.");
        string path = Path.Combine(settingsDirectory(), "pika-teaching", report.Identity.ProfileId,
            report.StartedUtc.ToString("yyyyMMdd-HHmmss") + "-" + report.SessionId + ".json");
        // The worker updates state under the lock this caller holds, so it
        // cannot publish its result before the assignment below.
        var worker = Task.Run(async () =>
        {
            try
            {
                await write(report, path);
                Change(s => s.Saved(path,
                    $"Saved {report.Frames.Length} measured arm/Pika observations · {report.GapCount} gaps. Motor power is unchanged; paired physical replay is unavailable."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { Change(s => s with { Status = "Teaching observations are unsaved: " + error.Message + " Use Retry save. Motor power is unchanged." }); }
        });
        state = state.SavingReport(report, worker,
            $"Saving {report.Frames.Length} arm/Pika observations. Motor power is unchanged; no automatic replay.");
    }

    /// <summary>Writes the report beside its final name and moves it into place, so a partial file never carries that name.</summary>
    private static async Task WriteReport(PikaTeachingReport report, string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, report, PluginProtocol.Json);
            File.Move(temporary, path, false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Pika teaching temporary-file cleanup: {0}", error.Message); }
        }
    }
}
