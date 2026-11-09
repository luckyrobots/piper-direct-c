using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class OrderedPhysicalContactTests
{
    public static void Run(Action<bool, string> check)
    {
        var raw = SimulationFollowTests.Pose();
        const double gap = 94.0443824782372;
        foreach (var joint in raw["joints"]!.AsArray().OfType<JsonObject>())
        {
            if (joint["name"]!.GetValue<string>() == "joint7") joint["position"] = (80 - gap) / 2000;
            if (joint["name"]!.GetValue<string>() == "joint8") joint["position"] = (gap - 80) / 2000;
        }
        raw["actuators"]!.AsArray().OfType<JsonObject>().Single(j => j["name"]!.GetValue<string>() == "gripper")["control"] = (80 - gap) / 2000;
        var first = SimulationFollowSample.Read(raw);
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 30, 20, true)).ToArray();
        SimulationFollowHardware Real(double opening, double q = 0) => new("can-a", 7, true, true, true,
            first.CommandDegrees.Select((value, joint) => joint == 0 ? q : value).ToArray(), limits, opening,
            new("can-a", 7, 77, 0, first.CommandDegrees.Select((value, joint) => joint == 0 ? q : value).ToArray()));
        OrderedSimulationFrame Frame(ulong cursor, double time, double q, string phase, double command, double measured, bool final = false) =>
            new(cursor, (long)(time * 1e9), time, Real(0, q).JointDegrees, Real(0, q).JointDegrees, command, measured, "episode-a", 0, phase, final, final);
        OrderedSimulationExecution Scenario(double contact = 41.4, double laterContact = 39.2)
        {
            var result = new OrderedSimulationExecution(first, Real(80), 1);
            result.Path.Append([
                Frame(1, 1, 0, "gripper:1", 80, 80), Frame(2, 1.1, 0, "gripper:1", 36, contact),
                Frame(3, 1.2, 0, "arm:2", 36, contact), Frame(4, 1.3, 1, "arm:2", 36, laterContact),
                Frame(5, 1.4, 1, "wait:3", 36, laterContact), Frame(6, 1.5, 1, "wait:3", 36, laterContact),
                Frame(7, 1.6, 1, "gripper:4", 80, 80), Frame(8, 1.7, 1, "gripper:4", 80, 80),
                Frame(9, 1.8, 1, "finish:5", 80, 80, true)]);
            return result;
        }
        void Finish(OrderedSimulationExecution value, ref double time, double opening, double q)
        {
            value.Step(time, Real(opening, q), true, 0, 100 + time);
            time += value.ActivePhase!.DurationSeconds + .001;
            value.Step(time, Real(opening, q), true, 0, 100 + time);
            time += .11;
            value.Step(time, Real(opening, q), true, 0, 100 + time);
            time += .01;
        }
        void Reject(Action action, string reason)
        {
            try { action(); } catch (InvalidOperationException) { check(true, reason); return; }
            check(false, reason);
        }

        var execution = Scenario(); double time = 0;
        Finish(execution, ref time, 43, 0);
        check(execution.CompletedCursor == 2 && execution.QualifiedContactOpeningMm == 43 && execution.Target.OpeningMm == 36,
            "A qualified 43mm physical contact was not retained separately from the 36mm command and 41.4mm source aperture");
        Finish(execution, ref time, 43, 1);
        check(execution.CompletedCursor == 4 && execution.QualifiedContactOpeningMm == 43 && execution.ExpectedMeasuredOpeningMm == 43,
            "The same closed command demanded further squeezing merely because simulated contact varied to 39.2mm during an arm phase");
        Finish(execution, ref time, 43, 1);
        check(execution.CompletedCursor == 6 && execution.QualifiedContactOpeningMm == 43,
            "A stationary hold lost same-command physical contact continuity");
        execution.Step(time, Real(43, 1), true, 0, 100 + time);
        check(execution.ActivePhase?.Kind == OrderedSimulationPhaseKind.Gripper && execution.QualifiedContactOpeningMm is null,
            "Opening the jaws reused the old physical contact baseline");
        time += execution.ActivePhase!.DurationSeconds + .001;
        execution.Step(time, Real(80, 1), true, 0, 100 + time); time += .11;
        execution.Step(time, Real(80, 1), true, 0, 100 + time);
        check(execution.CompletedCursor == 8 && execution.QualifiedContactOpeningMm is null,
            "An opening command fabricated a new contact baseline");
        Reject(() => execution.Step(time + .01, Real(43, 1), true, 0, 101 + time),
            "The previous 43mm baseline survived reopening and bypassed the new 80mm target checks");

        execution = Scenario(); time = 0; Finish(execution, ref time, 43, 0);
        Reject(() => execution.Step(time, Real(45.01), true, 0, 100 + time),
            "Opening away from qualified contact beyond 2mm was silently treated as contact while carrying");
        check(execution.CompletedCursor == 2, "An unexplained contact-loss sample advanced to the next arm endpoint");
        execution = Scenario(); time = 0; Finish(execution, ref time, 43, 0);
        execution.Step(time, Real(36), true, 0, 100 + time);
        check(execution.QualifyingLostContact && execution.QualifiedContactOpeningMm == 43 && !execution.PhysicalGraspMissed,
            "Closure to the unchanged endpoint claimed an empty jaw without bounded fresh settling");
        execution.Step(time + .11, Real(36), true, 0, 101 + time);
        check(!execution.QualifyingLostContact && execution.PhysicalGraspMissed && execution.QualifiedContactOpeningMm is null && execution.CompletedCursor == 2,
            "A settled empty endpoint after lost contact did not become a missed trial while preserving arm progress");

        execution = Scenario(); time = 0; Finish(execution, ref time, 43, 0);
        execution.Step(time, Real(43), true, 0, 100 + time);
        double armDeadline = time + execution.ActivePhase!.DurationSeconds + 15.01;
        Reject(() => execution.Step(armDeadline, Real(43), true, 0, 100 + armDeadline),
            "Qualified jaw contact bypassed actual arm endpoint arrival and its deadline");
        check(execution.CompletedCursor == 2, "Contact continuity advanced an arm that did not reach its endpoint");

        execution = Scenario(laterContact: 31); time = 0; Finish(execution, ref time, 43, 0);
        Reject(() => execution.Step(time, Real(43), true, 0, 100 + time),
            "A physical contact baseline bypassed the existing 10mm source tracking backstop");

        foreach (double noncontact in new[] { 36d, 37, 37.5, 38 })
        {
            execution = Scenario(noncontact); time = 0; Finish(execution, ref time, noncontact, 0);
            check(execution.CompletedCursor == 2 && execution.QualifiedContactOpeningMm is null,
                "A noncontact closing phase or only 2mm source-command difference was mislabeled as qualified contact");
        }

        execution = Scenario();
        execution.Step(0, Real(43), true, 0, 1);
        double duration = execution.ActivePhase!.DurationSeconds;
        execution.Step(duration + .001, Real(43), true, .03, 2);
        execution.Step(duration + .12, Real(43), true, 0, 3);
        execution.Step(duration + .25, Real(43), true, 0, 3);
        check(execution.CompletedCursor == 0 && execution.QualifiedContactOpeningMm is null,
            "Moving jaws or repeated cached feedback qualified contact without two settled fresh samples");
        Reject(() => execution.Step(duration + .26, Real(43) with { GripperFresh = false }, true, 0, 4),
            "Contact qualification ignored expired gripper feedback");
        execution.Step(duration + .27, Real(47), true, 0, 5);
        check(execution.QualifiedContactOpeningMm is null && execution.CompletedCursor == 0,
            "The physical latch bypassed the bounded initial source contact 5mm qualification");

        execution = Scenario(); time = 0; Finish(execution, ref time, 38.13, 0);
        check(execution.CompletedCursor == 2 && execution.QualifiedContactOpeningMm == 38.13 && execution.ExpectedMeasuredOpeningMm == 38.13,
            "Measured 38.13mm physical contact could not qualify against 41.4mm simulation/36mm command inside the bounded 5mm model window");
        execution.Step(time, Real(36), true, 0, 100 + time);
        check(execution.QualifyingLostContact && !execution.PhysicalGraspMissed,
            "The wider model window bypassed bounded qualification of a real closure to the empty endpoint");
        Reject(() => execution.Step(time + .01, Real(38.01), true, 0, 101 + time),
            "Loss qualification loosened the 2mm reversal guard");
        foreach (var (sourceContact, physicalOpening) in new[] { (41.4, 36d), (39.2, 36d), (41.4, 38d) })
        {
            execution = Scenario(sourceContact);
            execution.Step(0, Real(physicalOpening), true, 0, 1);
            duration = execution.ActivePhase!.DurationSeconds;
            execution.Step(duration + .001, Real(physicalOpening), true, 0, 2);
            execution.Step(duration + .12, Real(physicalOpening), true, 0, 3);
            check(execution.QualifiedContactOpeningMm is null && execution.CompletedCursor == 2 && execution.PhysicalGraspMissed,
                "A physical opening at/below command+2mm qualified contact merely because it was inside the source 5mm window");
        }

        execution = new(first, Real(80), 1);
        execution.Path.Append([Frame(1, 1, 0, "gripper:1", 80, 80), Frame(2, 1.1, 0, "gripper:1", 36, 41.4),
            Frame(3, 1.2, 0, "gripper:2", 35, 41.4), Frame(4, 1.3, 0, "gripper:2", 35, 41.4),
            Frame(5, 1.4, 0, "finish:3", 35, 41.4, true)]);
        time = 0; Finish(execution, ref time, 43, 0);
        execution.Step(time, Real(43), true, 0, 100 + time);
        check(execution.QualifiedContactOpeningMm is null && execution.Target.OpeningMm == 35,
            "A different closing command retained contact qualified for 36mm");
        time += execution.ActivePhase!.DurationSeconds + .001;
        execution.Step(time, Real(41.4), true, 0, 100 + time); time += .11;
        execution.Step(time, Real(41.4), true, 0, 100 + time);
        check(execution.CompletedCursor == 4 && execution.QualifiedContactOpeningMm == 41.4 && execution.Target.OpeningMm == 35,
            "A new closing command could not independently requalify its settled physical contact");

        // Actual five-step release from the successful tube episode. Source
        // servo lag is several millimetres at these short semantic boundaries.
        execution = new(first, Real(80), 1);
        double[] openCommands = [44.8, 53.6, 62.4, 71.2, 80];
        double[] sourceOpenings = [43.01, 49.64, 57.97, 66.68, 75.46];
        var release = new List<OrderedSimulationFrame>
        { Frame(1, 1, 0, "gripper:1", 80, 80), Frame(2, 1.1, 0, "gripper:1", 36, 41.4) };
        ulong cursor = 3;
        for (int step = 0; step < openCommands.Length; step++)
        {
            release.Add(Frame(cursor++, 1.2 + step * .2, 0, "gripper:" + (step + 2), openCommands[step], sourceOpenings[step]));
            release.Add(Frame(cursor++, 1.3 + step * .2, 0, "gripper:" + (step + 2), openCommands[step], sourceOpenings[step]));
        }
        release.Add(Frame(13, 2.2, 0, "wait:7", 80, 75.46));
        release.Add(Frame(14, 2.3, 0, "wait:7", 80, 75.46));
        release.Add(Frame(15, 2.4, 0, "home:8", 80, 75.46));
        release.Add(Frame(16, 2.5, 1, "home:8", 80, 75.46));
        release.Add(Frame(17, 2.6, 1, "finish:9", 80, 75.46, true));
        execution.Path.Append(release); time = 0; Finish(execution, ref time, 43, 0);
        foreach (double command in openCommands)
        {
            Finish(execution, ref time, command, 0);
            check(execution.QualifiedContactOpeningMm is null && execution.ExpectedMeasuredOpeningMm == command,
                "A correct physical opening endpoint was compared to transient simulated lag or mislabeled contact");
        }
        check(execution.CompletedCursor == 12, "The actual five-step release did not complete every ordered endpoint");
        Finish(execution, ref time, 80, 0);
        check(execution.CompletedCursor == 14 && execution.ExpectedMeasuredOpeningMm == 80,
            "The next same-command hold forgot the resolved 80mm jaw reference and waited for 75.46mm simulation lag");
        Finish(execution, ref time, 80, 1);
        check(execution.CompletedCursor == 16 && execution.ExpectedMeasuredOpeningMm == 80 && execution.QualifiedContactOpeningMm is null,
            "Same-command home motion discarded the qualified opening endpoint or fabricated secure contact evidence");
        Reject(() => execution.Step(time, Real(77.9, 1), true, 0, 100 + time),
            "An opening reference allowed subsequent physical drift beyond 2mm");
    }
}
