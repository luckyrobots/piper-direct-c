using System.Diagnostics;
using LuckyRobots.Piper.Plugin;

/// <summary>Plugin-G on its own: the Pika teaching session, driven only through
/// its transitions, with the test in control of when a save ends.</summary>
internal static class PikaTeachingWorkspaceTests
{
    private static readonly RobotProfile A = new("a", "First", "CAN-A", PikaDevice: "/dev/pika/by-host/11-4");
    private static readonly RobotProfile B = new("b", "Second", "CAN-B", PikaDevice: "/dev/pika/by-host/15-4");
    private static readonly PikaTeachingIdentity Identity =
        PikaTeachingIdentity.Create(A.Id, A.CanDeviceId, 7, A.PikaDevice, A.PikaDistribution, "test-backend-token");

    private static PikaTeachingRecorder Recorder(string request) => new(Identity, request, null, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);

    /// <summary>What one robot profile leaves behind never reaches another, and
    /// nothing is swapped while a gesture, recording or save is unfinished.</summary>
    public static async Task Run()
    {
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PikaTeachingSession(() => "settings", (_, _) => save.Task);
        bool Visible() => session.Current.ControlsVisible;
        void RejectSwap(Action<RobotProfile> swap, RobotProfile profile) =>
            Reject(() => swap(profile), "A pending teaching operation allowed profile state to be swapped: " + swap.Method.Name);

        // The value itself cannot hold a saved path and an unsaved report together.
        var report = Recorder("free-A").Snapshot(DateTimeOffset.UtcNow);
        var saved = new PikaTeachingState().Saved("A/prior-report.json", "saved");
        Check(saved is { Path: "A/prior-report.json", Unsaved: null }, "A saved path kept an unsaved report");
        Check(saved with { Outcome = new PikaTeachingOutcome.Unsaved(report) } is { Path: "", Unsaved: not null }, "An unsaved report kept an earlier saved path");

        Check(!Visible() && session.Current.Status == PikaTeachingState.DefaultStatus, "A fresh profile displayed unrelated teaching work.");
        session.DisableRequested("free-A");
        Check(Visible(), "A completed arm-teach session hid its free-jaw recovery controls.");

        // A's recording ends and its save fails: the report stays, unsaved.
        session.StartRecording(Recorder("free-A"), "recording A");
        save.SetException(new IOException("disk full"));
        await session.Finish("Operator finished")!;
        Check(session.Current is { Recorder: null, Saving: false, Path: "", Unsaved.FinishReason: "Operator finished" } &&
            session.Current.Status.Contains("disk full", StringComparison.Ordinal), "A failed save did not keep its report unsaved and say why");
        session.Report("A remains deliberately free");
        session.AskConfirmation("pending-A-confirmation");
        session.Store(A);
        session.Restore(B);
        Check(session.Current.DisableRequest == "" && session.Current.Path == "" && session.Current.Unsaved is null && !Visible(),
            "Second-profile enable inherited the first gripper's free-jaw request, save or display state.");
        Check(session.Current.ConfirmationIdentity is null, "A pending motor-release gesture survived a profile switch.");

        session.DisableRequested("free-B");
        session.Report("B remains deliberately free");
        session.Store(B);
        session.Restore(A with { Name = "First renamed" });
        Check(session.Current.DisableRequest == "free-A" && session.Current.Path == "" &&
            session.Current.Status == "A remains deliberately free" && Visible(),
            "Returning to the exact first assignment lost its resume provenance or saved status.");
        Check(session.Current.ConfirmationIdentity is null, "A stored profile kept its pending release confirmation.");
        Check(session.Current.Unsaved!.Identity == Identity && session.Current.Unsaved!.DisabledMotorRequestId == "free-A",
            "An unsaved report was relabelled as another arm/Pika/backend session.");
        session.Store(A);
        foreach (var reassigned in new[] { A with { CanDeviceId = "CAN-other" }, A with { PikaDevice = "/dev/pika/by-host/20-1" }, A with { Id = "new-profile" } })
        {
            session.Restore(reassigned);
            Check(session.Current.DisableRequest == "" && session.Current.Unsaved is null,
                "A changed hardware/profile assignment inherited old motor-release authority.");
        }
        if (OperatingSystem.IsWindows())
        {
            session.Restore(A with { WslDistribution = "Debian" });
            Check(session.Current.DisableRequest == "", "Another WSL distribution inherited a release nonce.");
        }
        session.Restore(B);
        Check(session.Current.DisableRequest == "free-B" && session.Current.Status == "B remains deliberately free",
            "Restoring first-profile state overwrote the independent second-profile state.");

        // Actual profile switching awaits the save before stashing any state.
        // Store and Restore reject a premature swap, so a delayed save cannot
        // write B's completion into A's fields.
        save = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StartRecording(Recorder("free-B"), "recording B");
        Task saving = session.Finish("Robot profile or hardware assignment changed")!;
        Check(Visible() && session.Current is { Saving: true, Recorder: null, Unsaved: not null }, "An outstanding teaching save became invisible.");
        RejectSwap(session.Store, B);
        RejectSwap(session.Restore, A);
        session.RetrySave();
        Check(ReferenceEquals(session.Current.SaveWorker, saving), "Retry started a second save beside the running one");
        Check(session.Current.DisableRequest == "free-B", "Rejected pending-save switch mutated current provenance.");
        save.SetResult();
        await saving;
        Check(session.Current is { Saving: false, Unsaved: null } && session.Current.Path.EndsWith(".json", StringComparison.Ordinal) &&
            session.Current.Path.StartsWith(Path.Combine("settings", "pika-teaching", A.Id), StringComparison.Ordinal),
            "A written report was not recorded as saved under the settings directory");
        session.Store(B);
        session.Restore(A);
        Check(session.Current.SaveWorker is null && session.Current.DisableRequest == "free-A",
            "An old completed save worker was attached to the restored profile.");

        session.AskConfirmation("pending");
        session.BeginMotorTransition(true);
        Check(session.Current is { Transition: true, ConfirmationIdentity: null }, "A release confirmation coexisted with a motor transition");
        session.AskConfirmation("late");
        Check(session.Current is { Transition: true, ConfirmationIdentity: null }, "A release confirmation was opened during a motor transition");
        RejectSwap(session.Store, A); RejectSwap(session.Restore, B);
        session.EndMotorTransition();
        Check(session.Current.Unsaved!.Identity == Identity, "A rejected swap during a transition erased the unsaved first-profile artifact.");

        // Retry writes the same report; a recording that is still running blocks a swap.
        save = new(); save.SetResult();
        session.RetrySave();
        await session.Finish("nothing is recording")!;
        Check(session.Current is { Unsaved: null, Saving: false } && session.Current.Path.Length > 0, "Retry did not save the unsaved report");
        session.RetrySave();
        Check(session.Current.Saving == false && session.Current.Path.Length > 0, "Retry with nothing unsaved started a save");
        session.StartRecording(Recorder("free-A"), "recording A again");
        Check(session.Current is { Path: "", Unsaved: null }, "A new recording kept the previous recording's saved path");
        RejectSwap(session.Store, A); RejectSwap(session.Restore, B);
        Check(session.Current is { Recorder: not null, DisableRequest: "free-A" }, "A rejected swap during a recording changed the session");
        save = new(); save.SetResult();
        await session.Finish("done")!;
    }

    /// <summary>Every transition of the session that the profile test does not walk through.</summary>
    public static async Task RunTransitions()
    {
        var written = new List<PikaTeachingReport>();
        var session = new PikaTeachingSession(() => "settings", (report, _) => { lock (written) written.Add(report); return Task.CompletedTask; });
        async Task<PikaTeachingReport> Saved(Task? saving, string step)
        {
            if (saving is null) throw new Exception(step + ": nothing was saved");
            await saving;
            Check(session.Current is { Recorder: null, Unsaved: null, Saving: false } && session.Current.Path.Length > 0, step + ": the report was not saved");
            lock (written) return written[^1];
        }

        Check(session.Finish("nothing") is null && session.TakeConfirmation() is null, "An idle session had something to save or confirm");
        session.Interrupt(); session.MotorRequestFailed(); session.MotorRequested("", false); session.RetrySave();
        session.Observe((_, _) => Check(false, "An idle session observed telemetry"));
        Check(session.Current == new PikaTeachingState() && !session.ContinueRecording("no"), "A transition for a running recording changed an idle session");

        session.AskConfirmation("one");
        Check(session.TakeConfirmation() == "one" && session.Current.ConfirmationIdentity is null && session.TakeConfirmation() is null,
            "A confirmation was not taken exactly once");
        session.AskConfirmation("two"); session.CancelConfirmation();
        Check(session.TakeConfirmation() is null, "A cancelled confirmation could still be taken");

        // Free jaws -> recording -> hold -> free again -> finish.
        session.BeginMotorTransition(false);
        session.MotorRequested("free-1", false);
        session.EndMotorTransition();
        session.DisableRequested("free-1");
        var recorder = Recorder("free-1");
        session.StartRecording(recorder, "recording");
        Check(session.Current is { Recorder: not null, DisableRequest: "free-1", Status: "recording", Busy: true, Transition: false }, "The recording did not start");
        session.BeginMotorTransition(true);
        session.MotorRequested("hold-1", true);
        session.Inspect(current => Check(current.Transition && recorder is { GapCount: 1, CurrentMotorRequestId: "hold-1", ExpectedMotorEnabled: true },
            "A hold request was not noted on the recording"));
        session.EndMotorTransition();
        session.HoldVerified(recording => recording ? "holding while recording" : "holding");
        Check(session.Current is { DisableRequest: "", Status: "holding while recording", Recorder: not null }, "A verified hold kept the free-jaw request");
        session.HoldFailed("free-2");
        session.Inspect(current => Check(current.DisableRequest == "free-2" && recorder is { CurrentMotorRequestId: "free-2", ExpectedMotorEnabled: false },
            "A failed hold did not record that the jaws are free again"));
        Check(session.ContinueRecording("free again") && session.Current.Status == "free again" && ReferenceEquals(session.Current.Recorder, recorder),
            "Freeing the jaws again did not continue the same recording");
        int observed = 0;
        session.Observe((running, current) => { observed++; Check(ReferenceEquals(running, recorder) && !current.Transition, "Telemetry met another recorder"); });
        Check(observed == 1 && session.Current.Recorder is not null, "An observation that did not finish the recorder ended the recording");
        var report = await Saved(session.Finish("Operator finished"), "Finish");
        Check(report is { FinishReason: "Operator finished", GapCount: 1, DisabledMotorRequestId: "free-1" } &&
            report.MotorEvents.Select(e => e.MotorRequestId).SequenceEqual(["hold-1", "free-2"]), "The saved report lost the session's motor events");
        Check(session.Current.DisableRequest == "free-2", "Finishing the recording forgot that the jaws are free");

        // Each way a recording ends by itself is saved with its reason.
        session.StartRecording(Recorder("free-2"), "recording");
        session.Observe((running, _) => running.Finish("Arm hand guiding ended"));
        Check((await Saved(session.Current.SaveWorker, "Observe")).FinishReason == "Arm hand guiding ended", "A recorder finished by telemetry was not saved");

        session.StartRecording(Recorder("free-2"), "recording");
        session.Interrupt();
        Check((await Saved(session.Current.SaveWorker, "Interrupt")) is { GapCount: 1 } interrupted &&
            interrupted.FinishReason.StartsWith("Controller telemetry stream interrupted", StringComparison.Ordinal), "A telemetry loss was not saved as the end of the recording");

        session.StartRecording(Recorder("free-2"), "recording");
        session.BeginMotorTransition(true);
        session.MotorRequested("", true);
        session.EndMotorTransition();
        Check((await Saved(session.Current.SaveWorker, "Unidentified reply")).FinishReason == "Motor transition acknowledgement cannot be identified",
            "A motor reply that cannot be identified did not end the recording");

        session.StartRecording(Recorder("free-2"), "recording");
        session.BeginMotorTransition(false);
        session.MotorRequestFailed();
        session.EndMotorTransition();
        Check((await Saved(session.Current.SaveWorker, "Failed request")).FinishReason.StartsWith("Motor transition request failed", StringComparison.Ordinal) &&
            !session.Current.Transition, "A failed motor request did not end the recording");
        session.HoldVerified(recording => recording ? "holding while recording" : "holding");
        Check(session.Current is { DisableRequest: "", Status: "holding" } && written.Count == 5, "A hold after the recording was described as recording");
    }
}
