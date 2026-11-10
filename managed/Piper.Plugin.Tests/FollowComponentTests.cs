using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>The Follow component on its own: Plugin-B as a table, the session
/// slot and its epochs, the status line, Plugin-C, and the marks a session and
/// its lanes keep (Plugin-A).</summary>
internal static class FollowComponentTests
{
    private sealed class StepClock : TimeProvider
    {
        private long ticks = 1_000_000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Volatile.Read(ref ticks);
        public void AdvanceMilliseconds(long milliseconds) => Interlocked.Add(ref ticks, milliseconds);
    }

    private static GroupSession NewSession(FollowComponent follow, params GroupLane[] lanes) =>
        new("backend", "scene", "entity", "asset", "revision", lanes, follow.InputEpoch, follow.Clock);

    private static GroupLane NewLane(TimeProvider clock)
    {
        var profile = new RobotProfile("piper-1", "First", CanDeviceId: "can-a", RobotId: "robot-a");
        var joints = new JsonArray();
        for (int joint = 1; joint <= 8; joint++) joints.Add(new JsonObject { ["name"] = "joint" + joint, ["unit"] = "rad" });
        var robot = new RobotLink("scene", "robot-a", joints, new JsonObject { ["id"] = "can-a", ["generation"] = 7L });
        return new(profile, robot, new FollowControlApproval("backend", "can-a", 7, "robot-a", "", "", null), null,
            new FollowLaneTelemetry(profile, 7, null, () => null, clock),
            new FollowLaneController("backend", "owner", profile, 7, (_, _, _, _) => Task.FromResult(new JsonObject { ["ok"] = true })));
    }

    /// <summary>Plugin-B: every outer transition, including the ones that must not happen.</summary>
    public static void RunLifecycle()
    {
        var follow = new FollowComponent(TimeProvider.System);
        Check(follow.Stage == GroupFollowStage.Idle && ReferenceEquals(follow.Outcome, GroupFollowOutcome.Idle) && !follow.Active && follow.Session is null &&
            follow.InputEpoch == 0 && follow.ControlEpoch == 0 && ReferenceEquals(follow.Status, FollowStatus.Initial) &&
            ReferenceEquals(follow.Viewport.Current, ViewportDrag.Off), "A new Follow component was not idle");

        follow.AbandonDiscovery();
        Check(follow.Stage == GroupFollowStage.Idle, "Abandoning with no discovery changed the stage");
        Check(follow.BeginDiscovery() == 0 && follow.Stage == GroupFollowStage.Discovering, "Arming did not enter Discovering under the current input epoch");
        follow.AbandonDiscovery();
        Check(follow.Stage == GroupFollowStage.Idle, "A cancelled discovery did not return to Idle");

        follow.BeginDiscovery();
        follow.FailSetup("No robot is ready");
        Check(follow.Outcome is { Stage: GroupFollowStage.SetupFailed, Summary: "Follow not armed\nNo robot is ready" } &&
            follow.Status == new FollowStatus("Follow not armed", "No robot is ready"), "A setup failure did not record its reason");
        follow.AbandonDiscovery();
        Check(follow.Stage == GroupFollowStage.SetupFailed, "A late discovery cancel erased the setup failure");
        follow.ClearOutcome();
        Check(follow.Stage == GroupFollowStage.Idle && follow.Status.Headline == "Follow not armed", "A selection change did not clear the outcome only");

        var first = NewSession(follow);
        var second = NewSession(follow);
        follow.BeginDiscovery();
        Check(follow.TryInstall(first) && ReferenceEquals(follow.Session, first) && follow.Active && follow.Stage == GroupFollowStage.Session &&
            follow.ControlEpoch == 1, "A free slot refused a session or did not advance the control epoch");
        follow.AbandonDiscovery();
        Check(follow.Outcome.Stage == GroupFollowStage.Discovering, "A discovery cancel that lost to the install rewrote the outcome under a session");
        Check(!follow.TryInstall(second) && ReferenceEquals(follow.Session, first) && follow.ControlEpoch == 1, "A second session replaced the installed one");
        Check(!follow.Retire(second) && ReferenceEquals(follow.Session, first) && follow.ControlEpoch == 1, "A session that does not hold the slot freed it");

        follow.Summarize("Follow ended\nStopped");
        Check(follow.Stage == GroupFollowStage.Session, "A summary was shown while its session was still installed");
        Check(follow.Retire(first) && follow.Session is null && follow.ControlEpoch == 2 &&
            follow.Outcome is { Stage: GroupFollowStage.Summary, Summary: "Follow ended\nStopped" }, "Retirement did not free the slot and show the summary");
        Check(!follow.Retire(first) && follow.ControlEpoch == 2, "A session was retired twice");
        Check(follow.TryInstall(second) && follow.ControlEpoch == 3 && follow.Stage == GroupFollowStage.Session, "The slot was not reusable after retirement");
        follow.FailSetup("unreachable");
        Check(follow.Stage == GroupFollowStage.Session, "The stage was not derived from the installed session");
    }

    /// <summary>One session at a time under contention, and exactly one control-epoch edge for it.</summary>
    public static void RunInstallRace()
    {
        for (int round = 0; round < 50; round++)
        {
            var follow = new FollowComponent(TimeProvider.System);
            var sessions = Enumerable.Range(0, 8).Select(_ => NewSession(follow)).ToArray();
            var won = new bool[sessions.Length];
            using var start = new Barrier(sessions.Length);
            var threads = sessions.Select((session, index) => new Thread(() => { start.SignalAndWait(); won[index] = follow.TryInstall(session); })).ToArray();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            int winner = Array.IndexOf(won, true);
            Check(won.Count(value => value) == 1 && ReferenceEquals(follow.Session, sessions[winner]) && follow.ControlEpoch == 1,
                "Concurrent arming installed more than one session or advanced the control epoch more than once");
            Check(sessions.Where((_, index) => index != winner).All(session => !follow.Retire(session)) && follow.ControlEpoch == 1 &&
                follow.Retire(sessions[winner]) && follow.ControlEpoch == 2, "A losing session could retire the winner");
        }
    }

    /// <summary>STOP and Stop following: the input epoch, the status line and the session's retirement.</summary>
    public static void RunCancelInput()
    {
        var follow = new FollowComponent(TimeProvider.System);
        long discovery = follow.BeginDiscovery();
        Check(follow.CancelInput() is null && follow.InputEpoch == discovery + 1 && ReferenceEquals(follow.Status, FollowStatus.Initial) &&
            follow.Stage == GroupFollowStage.Discovering, "Cancelling with no session did more than retire the discovery's input epoch");

        var session = NewSession(follow);
        Check(follow.TryInstall(session), "The fixture could not install a session");
        follow.Report("Following", "Two robots follow the simulation.");
        follow.Report("Matching pose");
        Check(follow.Status == new FollowStatus("Matching pose", "Two robots follow the simulation."), "A headline update lost the explanation");

        Check(ReferenceEquals(follow.CancelInput(), session) && session.Phase == GroupFollowPhase.Retiring && session.Stop.IsCancellationRequested &&
            session.InputEpoch < follow.InputEpoch, "Cancelling did not begin the installed session's retirement under a newer input epoch");
        Check(follow.Status == new FollowStatus("Following canceled", "Two robots follow the simulation.") && ReferenceEquals(follow.Session, session) &&
            follow.ControlEpoch == 1, "Cancelling freed the slot before the session's coordinator retired it, or lost the status");
        long epoch = follow.InputEpoch;
        Check(ReferenceEquals(follow.CancelInput("Emergency stop"), session) && follow.InputEpoch == epoch + 1 && session.Phase == GroupFollowPhase.Retiring &&
            follow.Status.Headline == "Emergency stop", "A second cancel did not stay Retiring under a newer epoch");
        session.Advance(GroupFollowPhase.Retired);
        Check(ReferenceEquals(follow.CancelInput(), session) && session.Phase == GroupFollowPhase.Retired, "A cancel revived a retired session");
        Check(follow.Retire(session) && follow.CancelInput() is null, "A retired session was still cancellable");

        // A STOP on another thread while the coordinator reports progress: the
        // slot keeps one session, and a status is never torn.
        var racing = NewSession(follow);
        Check(follow.TryInstall(racing), "The fixture could not install the racing session");
        follow.Report("Following", "detail");
        using var start = new Barrier(2);
        var reporter = new Thread(() => { start.SignalAndWait(); for (int i = 0; i < 2000; i++) follow.Report("Following " + i); });
        var stopper = new Thread(() => { start.SignalAndWait(); for (int i = 0; i < 2000; i++) follow.CancelInput("Emergency stop"); });
        reporter.Start(); stopper.Start(); reporter.Join(); stopper.Join();
        Check(follow.Status.Detail == "detail" && racing.Phase == GroupFollowPhase.Retiring && racing.Stop.IsCancellationRequested &&
            ReferenceEquals(follow.Session, racing), "Concurrent progress and STOP tore the status or lost the retirement");
    }

    /// <summary>Plugin-C: every drag transition, and the gesture gate it ends.</summary>
    public static void RunViewportDrag()
    {
        var viewport = new ViewportDragSession();
        JsonObject Gesture(string phase, long number, long sequence) => new()
        {
            ["phase"] = phase, ["gesture"] = number.ToString(), ["sequence"] = sequence, ["timestampUnixMilliseconds"] = 1000L
        };
        var off = viewport.Current;
        Check(!viewport.Teleoperating && viewport.FollowRequest == 0 && off is { Phase: ViewportDragPhase.Off, Joint: "joint6", Request: 0 }, "A new drag was not Off");
        Check(!viewport.TryBeginFollowing(off, 7, "joint3") && ReferenceEquals(viewport.Current, off), "Following began without teleoperation");
        viewport.EndGesture(off);
        Check(ReferenceEquals(viewport.Current, off), "A gesture end changed an Off drag");

        Check(viewport.TrySelectJoint(off, "joint3") && viewport.Current is { Phase: ViewportDragPhase.Teleoperating, Joint: "joint3", Request: 0 } &&
            viewport.Teleoperating && viewport.FollowRequest == 0, "Selecting a joint did not enter Teleoperating");
        Check(!viewport.TrySelectJoint(off, "joint5") && viewport.Current.Joint == "joint3", "A stale observer selected a joint");
        var teleoperating = viewport.Current;
        Check(viewport.TrySelectJoint(teleoperating, "joint2") && viewport.Current is { Phase: ViewportDragPhase.Teleoperating, Joint: "joint2" },
            "Teleoperation could not change joint");
        teleoperating = viewport.Current;
        Check(!viewport.TryBeginFollowing(teleoperating, 0, "joint2") && ReferenceEquals(viewport.Current, teleoperating), "Following began without a request");
        Check(viewport.TryBeginFollowing(teleoperating, 7, "joint4") &&
            viewport.Current is { Phase: ViewportDragPhase.Following, Joint: "joint2", Request: 7, FollowJoint: "joint4" } && viewport.FollowRequest == 7,
            "A follow-start acknowledgement did not enter Following");
        var following = viewport.Current;
        Check(!viewport.TryBeginFollowing(following, 8, "joint4") && !viewport.TryBeginFollowing(teleoperating, 8, "joint4") && viewport.FollowRequest == 7,
            "A second follow request replaced the one in progress");
        viewport.EndGesture(teleoperating);
        Check(ReferenceEquals(viewport.Current, following), "A gesture end observed before Following ended the gesture's follow request");
        viewport.EndGesture(following);
        Check(viewport.Current is { Phase: ViewportDragPhase.Teleoperating, Joint: "joint2", Request: 0 } && viewport.Teleoperating && viewport.FollowRequest == 0,
            "A gesture end did not return to Teleoperating");

        viewport.Stop();
        Check(viewport.Current is { Phase: ViewportDragPhase.Off, Joint: "joint2", Request: 0 } && !viewport.Teleoperating, "Stop did not leave teleoperation and remember the joint");
        viewport.Stop();
        Check(viewport.Current is { Phase: ViewportDragPhase.Off, Joint: "joint2" }, "A second Stop changed an Off drag");

        viewport.Accept(Gesture("begin", 1, 1), 1000);
        viewport.Accept(Gesture("update", 1, 2), 1000);
        viewport.Stop();
        Reject(() => viewport.Accept(Gesture("update", 1, 3), 1000), "Stop left its gesture able to send targets");
        viewport.Accept(Gesture("begin", 2, 1), 1000);
        viewport.EndGesture(viewport.Current);
        Reject(() => viewport.Accept(Gesture("update", 2, 2), 1000), "A gesture end left the gesture able to send targets");
        Reject(() => viewport.Accept(Gesture("begin", 2, 1), 1000), "A completed gesture restarted");

        // STOP against a gesture writer on another thread: whichever order they
        // land in, the drag never ends in Following after the STOP.
        for (int round = 0; round < 200; round++)
        {
            var observed = viewport.Teleoperate("joint3");
            using var start = new Barrier(2);
            var writer = new Thread(() => { start.SignalAndWait(); viewport.TryBeginFollowing(observed, 7, "joint3"); viewport.EndGesture(viewport.Current); });
            var stopper = new Thread(() => { start.SignalAndWait(); viewport.Stop(); });
            writer.Start(); stopper.Start(); writer.Join(); stopper.Join();
            Check(viewport.Current is { Phase: ViewportDragPhase.Off, Joint: "joint3", Request: 0 },
                "A gesture writer overwrote a concurrent STOP");
        }
    }

    /// <summary>A session's phase and the marks its coordinator leaves.</summary>
    public static void RunSession()
    {
        var clock = new StepClock();
        var follow = new FollowComponent(clock);
        var session = NewSession(follow);
        Check(session.Phase == GroupFollowPhase.Arming && !session.IsRetiring && !session.CanPrime && session.RunId.Length == 0 && session.FirstFailure.Length == 0 &&
            session.EndReason.Length == 0 && session.AcknowledgedEpisode.Length == 0 && session.Publisher is null && session.Heartbeats is null &&
            session.Primer is null && session.Observation is null && ReferenceEquals(session.Clock, clock) && session.InputEpoch == follow.InputEpoch,
            "A new session carried marks");

        Reject(() => session.Advance(GroupFollowPhase.Matching), "A session skipped Armed");
        Reject(session.RequireTargetPublication, "An arming session could publish targets");
        session.Advance(GroupFollowPhase.Armed); session.Require(GroupFollowPhase.Armed);
        Reject(() => session.Require(GroupFollowPhase.Matching), "Require accepted another phase");
        session.Advance(GroupFollowPhase.Matching); session.Advance(GroupFollowPhase.Admitting);
        Check(session.CanPrime, "An admitting session could not prime");
        session.RequireTargetPublication();
        session.Advance(GroupFollowPhase.Activating); session.Advance(GroupFollowPhase.DrainingPrimer);
        Check(!session.CanPrime, "A draining session could start another priming batch");
        session.RequireTargetPublication();
        session.RecordValidatedSource(clock.GetTimestamp());
        session.Advance(GroupFollowPhase.Following);
        clock.AdvanceMilliseconds(249);
        session.RequireTargetPublication(); session.RequireOrderedSourceLease();
        clock.AdvanceMilliseconds(1);
        Reject(session.RequireTargetPublication, "A following session published against an expired source");
        Reject(() => session.Advance(GroupFollowPhase.Retired), "A session reached Retired without Retiring");

        session.BindRun("run-1"); session.EndWith("Stopped"); session.AcknowledgeEpisode("episode-1");
        var worker = Task.CompletedTask;
        session.AttachHeartbeats(worker); session.AttachPrimer(worker); session.AttachPublisher(worker);
        Check(session.RunId == "run-1" && session.EndReason == "Stopped" && session.AcknowledgedEpisode == "episode-1" &&
            ReferenceEquals(session.Heartbeats, worker) && ReferenceEquals(session.Primer, worker) && ReferenceEquals(session.Publisher, worker),
            "A session mark was not recorded");
        Check(session.TryRecordFailure("first") && !session.TryRecordFailure("second") && session.FirstFailure == "first", "A later failure replaced the first");

        session.BeginRetirement();
        Check(session.Phase == GroupFollowPhase.Retiring && session.IsRetiring && session.Stop.IsCancellationRequested, "Retirement did not cancel the session");
        Reject<OperationCanceledException>(() => session.Advance(GroupFollowPhase.Following), "A retiring session advanced");
        Reject<OperationCanceledException>(session.RequireTargetPublication, "A retiring session could publish targets");
        session.BeginRetirement();
        session.Advance(GroupFollowPhase.Retired);
        session.BeginRetirement();
        Check(session.Phase == GroupFollowPhase.Retired && session.IsRetiring, "Retired was not final");

        // Retirement from another thread always beats the coordinator's next phase.
        for (int round = 0; round < 200; round++)
        {
            var racing = NewSession(follow);
            using var start = new Barrier(2);
            var coordinator = new Thread(() =>
            {
                start.SignalAndWait();
                try { racing.Advance(GroupFollowPhase.Armed); racing.Advance(GroupFollowPhase.Matching); racing.Advance(GroupFollowPhase.Admitting); }
                catch (OperationCanceledException) { }
            });
            var stopper = new Thread(() => { start.SignalAndWait(); racing.BeginRetirement(); });
            coordinator.Start(); stopper.Start(); coordinator.Join(); stopper.Join();
            Check(racing.Phase == GroupFollowPhase.Retiring && racing.Stop.IsCancellationRequested, "A coordinator phase overwrote a concurrent retirement");
        }
    }

    /// <summary>Plugin-A: a lane's arm and Pika admission, and its counters.</summary>
    public static void RunLane()
    {
        var lane = NewLane(TimeProvider.System);
        Check(ReferenceEquals(lane.Arm, LaneArm.Untouched) && ReferenceEquals(lane.Pika, LanePika.Untouched) && lane.Arm.CurrentRequest == 0 && lane.Sequence == 0 &&
            lane.ArmAdmissionRetiredTicks == 0 && lane.Diagnostics is null && lane.First is null && lane.LastSample is null && lane.Execution is null &&
            lane.LastStep is null && lane.RejectedPikaAudit is null, "A new lane carried marks");

        lane.Matched();
        Check(ReferenceEquals(lane.Arm, LaneArm.Untouched), "An untouched lane was marked matched");
        lane.TouchArm(); lane.TouchArm();
        Check(ReferenceEquals(lane.Arm, LaneArm.Touched) && lane.Arm.CurrentRequest == 0, "A sent pose did not touch the arm");
        lane.MatchRequested(5);
        Check(lane.Arm is LaneArm.Matching { MatchRequest: 5 } && lane.Arm.CurrentRequest == 5, "An acknowledged pose did not own its request");
        lane.TouchArm();
        Check(lane.Arm is LaneArm.Matching, "A later send demoted a matching arm to Touched");
        lane.Matched();
        Check(ReferenceEquals(lane.Arm, LaneArm.MatchedPose) && lane.Arm.CurrentRequest == 0, "A settled pose kept its request");
        lane.FollowAdmitted(9, 1234, 3);
        Check(lane.Arm is LaneArm.Following { FollowRequest: 9, AdmissionTicks: 1234, AdmissionSequence: 3 } && lane.Arm.CurrentRequest == 9,
            "A follow-start acknowledgement did not own its request");
        lane.TouchArm(); lane.Matched();
        Check(lane.Arm is LaneArm.Following, "A following arm was demoted");

        lane.TouchPika(); lane.TouchPika();
        Check(ReferenceEquals(lane.Pika, LanePika.TouchedOnly), "A sent Pika command did not touch the channel");
        lane.PikaFollowing("pika-session");
        lane.TouchPika();
        Check(lane.Pika == new LanePika(true, "pika-session"), "A later Pika send dropped the session");

        Check(lane.NextSequence() == 1 && lane.NextSequence() == 2 && lane.Sequence == 2, "The lane's target sequence did not count up");
        lane.ArmAdmissionReturned(77);
        Check(lane.ArmAdmissionRetiredTicks == 77, "The arm admission return was not recorded");
    }
}
