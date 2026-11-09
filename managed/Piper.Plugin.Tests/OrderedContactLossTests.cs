using LuckyRobots.Piper.Plugin;

internal static class OrderedContactLossTests
{
    public static void Run()
    {
        var first = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 30, 20, true)).ToArray();
        double[] Joints(double q) => [q, 0, 0, 0, 0, 0];
        SimulationFollowHardware Real(double opening, double q = 0) => new("can-a", 7, true, true, true, Joints(q), limits, opening,
            new("can-a", 7, 77, 0, Joints(q)));
        OrderedSimulationFrame Frame(ulong cursor, double time, double q, string phase, double command = 30, double measured = 38,
            bool final = false) => new(cursor, (long)(time * 1e9), time, Joints(q), Joints(q), command, measured, "episode-a", 0, phase, final, final);
        OrderedSimulationExecution Qualified(out double now)
        {
            var execution = new OrderedSimulationExecution(first, Real(80), 1);
            execution.Path.Append([
                Frame(1, 1, 0, "gripper:1", 80, 80), Frame(2, 1.1, 0, "gripper:1"),
                Frame(3, 1.2, 0, "arm:2"), Frame(4, 1.3, 1, "arm:2"),
                Frame(5, 1.4, 1, "wait:3"), Frame(6, 1.5, 1, "wait:3"),
                Frame(7, 1.6, 1, "gripper:4", 80, 80), Frame(8, 1.7, 1, "gripper:4", 80, 80),
                Frame(9, 1.8, 1, "home:5", 80, 80), Frame(10, 1.9, 0, "home:5", 80, 80),
                Frame(11, 2, 0, "finish:6", 80, 80, true)]);
            execution.Step(0, Real(38), true, 0, 1);
            now = execution.ActivePhase!.DurationSeconds + .001;
            execution.Step(now, Real(38), true, 0, 2); now += .11;
            execution.Step(now, Real(38), true, 0, 3); now += .01;
            Check(execution.QualifiedContactOpeningMm == 38 && !execution.PhysicalGraspMissed,
                "Loss fixture never established its original qualified contact");
            return execution;
        }
        void Finish(OrderedSimulationExecution execution, ref double now, double opening, double q)
        {
            execution.Step(now, Real(opening, q), true, 0, 100 + now);
            now += execution.ActivePhase!.DurationSeconds + .001;
            execution.Step(now, Real(opening, q), true, 0, 100 + now);
            now += .11; execution.Step(now, Real(opening, q), true, 0, 100 + now); now += .01;
        }

        var value = Qualified(out double time);
        var firstTarget = value.Step(time, Real(35.5), false, -.05, 4);
        Check(value.QualifyingLostContact && !value.PhysicalGraspMissed && value.QualifiedContactOpeningMm == 38 && firstTarget.OpeningMm == 30,
            "Loss qualification changed the existing close command or claimed a miss before endpoint evidence");
        value.Step(time + .04, Real(30, .1), false, 0, 5);
        var afterLoss = value.Step(time + .15, Real(30, .3), false, 0, 6);
        Check(!value.QualifyingLostContact && value.PhysicalGraspMissed && value.QualifiedContactOpeningMm is null &&
            value.ExpectedMeasuredOpeningMm == 30 && value.CompletedCursor == 2 && afterLoss.OpeningMm == 30,
            "A fresh settled empty close while the arm was moving failed to reanchor as a non-contact missed trial");
        time += .16;
        Finish(value, ref time, 30, 1); Finish(value, ref time, 30, 1);
        Finish(value, ref time, 80, 1); Finish(value, ref time, 80, 0); Finish(value, ref time, 80, 0);
        Check(value.Path.Drained && value.ReturnedHome(Real(80), true, 0), "A qualified physical drop did not finish the unchanged ordered release/home path");

        value = Qualified(out time); value.Step(time, Real(35), false, -.05, 4);
        Reject(() => value.Step(time + .01, Real(37.01), false, 0, 5), "A reversal greater than 2mm was treated as monotonic closing");
        value = Qualified(out time);
        Reject(() => value.Step(time, Real(40.01), false, 0, 4), "Opening away from contact beyond 2mm entered the soft-loss path");
        value = Qualified(out time);
        Reject(() => value.Step(time, Real(28.99), false, 0, 4), "A sample below the close endpoint minus 1mm entered soft-loss qualification");
        value = Qualified(out time);
        Reject(() => value.Step(time, Real(35), false, .03, 4), "Reported opening velocity was accepted as motion toward the close endpoint");

        value = Qualified(out time); value.Step(time, Real(30), false, 0, 4);
        value.Step(time + .11, Real(30), false, 0, 4);
        value.Step(time + .249, Real(30), false, 0, 4);
        Check(value.QualifyingLostContact && !value.PhysicalGraspMissed && value.QualifiedContactOpeningMm == 38,
            "Repeated cached jaw feedback qualified lost contact without a second fresh observation");
        Reject(() => value.Step(time + .251, Real(30), false, 0, 5), "The 250ms loss-qualification deadline was renewed by repeated observations");
        value = Qualified(out time); value.Step(time, Real(35), false, -.05, 4);
        Reject(() => value.Step(time + .251, Real(35), false, 0, 5), "A partial close that never reached its endpoint became a soft miss");
        value = Qualified(out time); value.Step(time, Real(30), false, -.03, 4);
        value.Step(time + .04, Real(30), false, 0, 5); value.Step(time + .13, Real(30), false, 0, 6);
        Check(value.QualifyingLostContact && !value.PhysicalGraspMissed, "Moving jaws were included in the 100ms settled endpoint dwell");
        value.Step(time + .15, Real(30), false, 0, 7);
        Check(value.PhysicalGraspMissed && !value.QualifyingLostContact, "The same bounded qualification could not finish after the jaw stopped moving");

        foreach (string failure in new[] { "arm-stale", "jaw-stale", "native-tracking", "command-change" })
        {
            value = Qualified(out time); value.Step(time, Real(30), false, 0, 4);
            if (failure == "arm-stale") Reject(() => value.Step(time + .05, Real(30) with { FeedbackFresh = false }, false, 0, 5), "Soft loss bypassed stale arm feedback");
            if (failure == "jaw-stale") Reject(() => value.Step(time + .05, Real(30) with { GripperFresh = false }, false, 0, 5), "Soft loss bypassed stale jaw feedback");
            if (failure == "native-tracking") Reject(() => value.Step(time + .05, Real(30) with { NativeReference = Real(30, 6).NativeReference }, false, 0, 5), "Soft loss bypassed native arm tracking");
            if (failure == "command-change")
            {
                // The source phase is immutable in production. Corrupt its
                // retained command here to exercise the invariant directly.
                var jawField = typeof(OrderedSimulationExecution).GetField("jaw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                jawField.SetValue(value, (JawReference)jawField.GetValue(value)! with { Command = 31 });
                Reject(() => value.Step(time + .05, Real(30), false, 0, 5), "A changed close-command identity retained the old loss qualification");
            }
        }

        value = Qualified(out time); value.Step(time, Real(38), false, 0, 4);
        time += value.ActivePhase!.DurationSeconds + .001;
        value.Step(time, Real(30, 1), true, 0, 5);
        Check(value.CompletedCursor == 2 && value.Target.OpeningMm == 30 && value.QualifyingLostContact,
            "A phase endpoint started the next jaw operation before its pending contact loss resolved");
        value.Step(time + .11, Real(30, 1), true, 0, 6);
        value.Step(time + .22, Real(30, 1), true, 0, 7);
        Check(value.CompletedCursor == 4 && value.PhysicalGraspMissed && !value.QualifyingLostContact,
            "Confirmed empty closure never released its preceding arm endpoint barrier");

        foreach (double opening in new[] { 29d, 31, 31.5, 32 })
        {
            value = Qualified(out time); value.Step(time, Real(opening), false, 0, 4);
            value.Step(time + .11, Real(opening), false, 0, 5);
            Check(value.PhysicalGraspMissed && !value.QualifyingLostContact && value.QualifiedContactOpeningMm is null &&
                value.ExpectedMeasuredOpeningMm == opening && value.Target.OpeningMm == 30,
                "The carry-loss 1–2mm gray band could neither qualify contact nor finish as an unconfirmed trial");
        }
        value = Qualified(out time); value.Step(time, Real(32.0001), false, 0, 4);
        value.Step(time + .11, Real(32.0001), false, 0, 5);
        Check(!value.PhysicalGraspMissed && value.QualifyingLostContact, "Loss handling widened the existing 2mm unconfirmed-contact threshold");
        Reject(() => value.Step(time + .251, Real(32.0001), false, 0, 6),
            "The widened gray band renewed the bounded 250ms carry-loss deadline");
    }
}
