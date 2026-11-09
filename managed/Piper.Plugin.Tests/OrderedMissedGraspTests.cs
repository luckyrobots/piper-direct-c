using LuckyRobots.Piper.Plugin;

internal static class OrderedMissedGraspTests
{
    public static void Run()
    {
        var first = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 30, 20, true)).ToArray();
        double[] Joints(double q) => [q, 0, 0, 0, 0, 0];
        SimulationFollowHardware Real(double opening, double q = 0) => new("can-a", 7, true, true, true, Joints(q), limits, opening,
            new("can-a", 7, 77, 0, Joints(q)));
        OrderedSimulationFrame Frame(ulong cursor, double time, double q, string phase, double command = 36, double measured = 41.4,
            bool final = false, bool success = true) => new(cursor, (long)(time * 1e9), time, Joints(q), Joints(q), command, measured,
                "episode-a", 0, phase, final, success);
        OrderedSimulationFrame[] Cycle(bool failedSource = false) => [
            Frame(1, 1, 0, "gripper:1", 80, 80), Frame(2, 1.1, 0, "gripper:1"),
            Frame(3, 1.2, 0, failedSource ? "missed-grasp-arm:2" : "arm:2"),
            Frame(4, 1.3, 1, failedSource ? "missed-grasp-arm:2" : "arm:2"),
            Frame(5, 1.4, 1, failedSource ? "missed-grasp-gripper:3" : "gripper:3", 50, 49),
            Frame(6, 1.5, 1, failedSource ? "missed-grasp-gripper:3" : "gripper:3", 50, 49),
            Frame(7, 1.6, 1, failedSource ? "missed-grasp-home:4" : "home:4", 50, 49),
            Frame(8, 1.7, 0, failedSource ? "missed-grasp-home:4" : "home:4", 50, 49),
            Frame(9, 1.8, 0, failedSource ? "missed-grasp-finish:5" : "finish:5", 50, 49, true, !failedSource)];
        void Finish(OrderedSimulationExecution execution, ref double now, double opening, double q)
        {
            execution.Step(now, Real(opening, q), true, 0, 100 + now);
            now += execution.ActivePhase!.DurationSeconds + .001;
            execution.Step(now, Real(opening, q), true, 0, 100 + now);
            now += .11; execution.Step(now, Real(opening, q), true, 0, 100 + now); now += .01;
        }
        foreach (var (failedSource, closeOpening) in new[] { (false, 36d), (true, 36d), (true, 43d) })
        {
            var execution = new OrderedSimulationExecution(first, Real(80), 1);
            execution.Path.Append(Cycle(failedSource));
            Check(execution.Path.FrameCount == 9 && !execution.Path.Drained && execution.Path.SourceMissedGrasp == failedSource,
                "An explicitly failed-at-home source discarded its ordered carry/release/home frames or completed before physical arrival");
            double now = 0; Finish(execution, ref now, closeOpening, 0);
            Check(execution.CompletedCursor == 2 && execution.PhysicalGraspMissed == (closeOpening == 36) &&
                (execution.QualifiedContactOpeningMm is null) == (closeOpening == 36),
                "An empty settled close stopped mid-cycle or falsely qualified a contact candidate");
            Finish(execution, ref now, closeOpening, 1);
            Check(execution.CompletedCursor == 4 && execution.ExpectedMeasuredOpeningMm == closeOpening,
                "The empty close did not retain its non-contact jaw reference through the remaining source carry");
            Finish(execution, ref now, 50, 1);
            Finish(execution, ref now, 50, 0);
            Finish(execution, ref now, 50, 0);
            Check(execution.Path.Drained && execution.CompletedCursor == 9 && execution.MissedGrasp && execution.ReturnedHome(Real(50), true, 0),
                "A missed grasp did not drain every original source waypoint and verify generic 50mm open/zero endpoint before ending");
            Check(!execution.ReturnedHome(Real(50, .251), true, 0) && !execution.ReturnedHome(Real(47.9), true, 0) &&
                !execution.ReturnedHome(Real(50), false, 0) && !execution.ReturnedHome(Real(50) with { GripperFresh = false }, true, 0),
                "Returned-home reporting bypassed actual zero/open/stationary/fresh proof");
        }
        var path = new OrderedSimulationPath(limits);
        Reject(() => path.Append([Frame(1, 1, 0, "finish:1", final: true, success: false)]),
            "An unknown failed episode was softened into a missed-grasp continuation");
        Reject(() => new OrderedSimulationPath(limits).Append([Frame(1, 1, 0, "missed-grasp-finish:1", final: true, success: false)]),
            "A missed-grasp terminal without an ordered home was accepted");
        Reject(() => new OrderedSimulationPath(limits).Append([Frame(1, 1, 0, "missed-grasp-home:1"), Frame(2, 1.1, 0, "driver-fault-finish:2", final: true, success: false)]),
            "A source driver fault was accepted merely because a previous phase mentioned missed grasp");
        Reject(() => new OrderedSimulationPath(limits).Append([Frame(1, 1, 0, "missed-grasp-home:x"), Frame(2, 1.1, 0, "missed-grasp-finish:2", final: true, success: false)]),
            "An unrecognized soft-failure marker bypassed explicit ordered-home classification");

        var settling = new OrderedSimulationExecution(first, Real(80), 1); settling.Path.Append(Cycle());
        settling.Step(0, Real(36), true, 0, 1); double duration = settling.ActivePhase!.DurationSeconds;
        settling.Step(duration + .01, Real(36), true, 0, 2);
        settling.Step(duration + .2, Real(36), true, 0, 2);
        Check(!settling.PhysicalGraspMissed && settling.CompletedCursor == 0, "Cached jaw feedback fabricated a settled empty-close result");
        Reject(() => settling.Step(duration + .21, Real(36) with { FeedbackFresh = false }, true, 0, 3), "A missed grasp softened expired arm feedback");
        Reject(() => settling.Step(duration + .22, Real(36) with { GripperFresh = false }, true, 0, 3), "A missed grasp softened expired jaw feedback");
        settling.Step(duration + .23, Real(36), false, 0, 3);
        Check(!settling.PhysicalGraspMissed && settling.CompletedCursor == 0, "A moving arm qualified an empty close");
        settling.Step(duration + .24, Real(36), true, .03, 4);
        Check(!settling.PhysicalGraspMissed && settling.CompletedCursor == 0, "Moving jaws qualified an empty close");

        var guarded = new OrderedSimulationExecution(first, Real(80), 1); guarded.Path.Append(Cycle());
        double time = 0; Finish(guarded, ref time, 36, 0);
        Reject(() => guarded.Step(time, Real(38.01), true, 0, 100 + time), "An empty-close reference loosened the existing 2mm jaw continuity guard");
        guarded = new(first, Real(80), 1); guarded.Path.Append(Cycle()); time = 0; Finish(guarded, ref time, 36, 0);
        Reject(() => guarded.Step(time, Real(36) with { NativeReference = Real(36, 6).NativeReference }, true, 0, 100 + time),
            "A missed grasp bypassed the native-reference arm tracking guard");

        // Once a miss is explicitly known, model contact may differ from the
        // settled real reference by more than 10mm. It is still the exact same
        // jaw command, and actual deviation from the reference remains 2mm.
        guarded = new(first, Real(80), 1);
        guarded.Path.Append(Cycle().Select(frame => frame.Cursor is 2 or 3 or 4 ? frame with { MeasuredOpeningMm = 55 } : frame).ToArray());
        time = 0; Finish(guarded, ref time, 36, 0); guarded.Step(time, Real(36, 1), true, 0, 100 + time);
        Check(guarded.ActivePhase?.Kind == OrderedSimulationPhaseKind.Arm && guarded.PhysicalGraspMissed && guarded.ExpectedMeasuredOpeningMm == 36,
            "A known empty real close stopped the original cycle because the simulated contact was more than 10mm away");
        Reject(() => guarded.Step(time + .01, Real(38.01, 1), true, 0, 101 + time),
            "Model-mismatch continuation removed the 2mm actual empty-jaw continuity guard");
        guarded = new(first, Real(80), 1);
        guarded.Path.Append(Cycle(true).Select(frame => frame.Cursor == 2 ? frame with { MeasuredOpeningMm = 59 }
            : frame.Cursor is 3 or 4 ? frame with { MeasuredOpeningMm = 36 } : frame).ToArray());
        time = 0; Finish(guarded, ref time, 60, 0); Finish(guarded, ref time, 60, 1);
        Check(guarded.CompletedCursor == 4 && !guarded.PhysicalGraspMissed && guarded.QualifiedContactOpeningMm == 60,
            "An explicit simulated miss discarded intact qualified real contact merely because the simulated aperture collapsed");

        // Actual forced-miss run: source45.2 / command42.9 / real44.4.
        // Contact needs strictly > 2mm command separation. Every smaller gap
        // down to the established 1mm undershoot allowance is unconfirmed.
        foreach (double gap in new[] { -1d, 0, 1, 1.5, 2, 2.000001 })
        {
            var gray = new OrderedSimulationExecution(first, Real(80), 1);
            gray.Path.Append(Cycle().Select(frame => frame with
            {
                CommandOpeningMm = frame.CommandOpeningMm == 36 ? 42.9 : frame.CommandOpeningMm,
                MeasuredOpeningMm = frame.MeasuredOpeningMm == 41.4 ? 45.2 : frame.MeasuredOpeningMm
            }).ToArray());
            double now = 0, actual = 42.9 + gap; Finish(gray, ref now, actual, 0);
            Check(gray.CompletedCursor == 2 && gray.PhysicalGraspMissed == (gap <= 2) &&
                (gray.QualifiedContactOpeningMm is null) == (gap <= 2),
                "The initial 1–2mm contact gray band timed out or crossed the strictly > 2mm contact threshold");
            if (gap == 1.5)
            {
                Finish(gray, ref now, actual, 1); Finish(gray, ref now, 50, 1);
                Finish(gray, ref now, 50, 0); Finish(gray, ref now, 50, 0);
                Check(gray.Path.Drained && gray.ReturnedHome(Real(50), true, 0),
                    "The exact44.4/42.9mm live gray-band case did not complete its unchanged home cycle");
            }
        }
        foreach (double actual in new[] { 41.8999, 50.2001 })
        {
            var outside = new OrderedSimulationExecution(first, Real(80), 1);
            outside.Path.Append(Cycle().Select(frame => frame with
            {
                CommandOpeningMm = frame.CommandOpeningMm == 36 ? 42.9 : frame.CommandOpeningMm,
                MeasuredOpeningMm = frame.MeasuredOpeningMm == 41.4 ? 45.2 : frame.MeasuredOpeningMm
            }).ToArray());
            outside.Step(0, Real(actual), true, 0, 1);
            double end = outside.ActivePhase!.DurationSeconds;
            outside.Step(end + .001, Real(actual), true, 0, 2); outside.Step(end + .12, Real(actual), true, 0, 3);
            Check(outside.CompletedCursor == 0 && !outside.PhysicalGraspMissed && outside.QualifiedContactOpeningMm is null,
                "Initial gray-band handling accepted more than 1mm undershoot or widened the 5mm source contact window");
            Reject(() => outside.Step(end + 15.01, Real(actual), true, 0, 4),
                "An unexplained close outside both existing endpoint/contact bounds bypassed its deadline");
        }
    }
}
