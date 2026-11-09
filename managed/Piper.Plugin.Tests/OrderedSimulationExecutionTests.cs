using LuckyRobots.Piper.Plugin;

internal static class OrderedSimulationExecutionTests
{
    public static void Run(Action<bool, string> check)
    {
        var first = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        var hardware = new SimulationFollowHardware("can-a", 7, true, true, true, first.CommandDegrees,
            Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 30, 20, true)).ToArray(), 20,
            new("can-a", 7, 77, 0, first.CommandDegrees));
        OrderedSimulationFrame Frame(ulong cursor, double time, double q, string phase, double opening = 20, double measured = 20, bool complete = false) =>
            new(cursor, (long)(time * 1e9), time, first.CommandDegrees.Select((v, i) => i == 0 ? q : v).ToArray(), first.MeasuredDegrees,
                opening, measured, "episode-a", 0, phase, complete, complete);
        void Reject(Action action, string reason)
        {
            try { action(); } catch (InvalidOperationException) { check(true, reason); return; }
            check(false, reason);
        }
        var pausedJson = SimulationFollowTests.Pose(); pausedJson["mode"] = "pause";
        var paused = SimulationFollowSample.Read(pausedJson);
        var activationJson = SimulationFollowTests.Pose(sequence: 2, elapsedMs: 20); activationJson["timeScale"] = 1d;
        var activation = SimulationFollowSample.Read(activationJson);
        SimulationFollowPolicy.ValidateStart(paused, paused.Identity, hardware, 1_000_000, 10);
        SimulationFollowPolicy.ValidateActivation(paused, hardware, activation, hardware, 1_000_020, 20);
        check(true, "Queued following could not activate the exact first frame at the authored 1x source rate");
        Reject(() => SimulationFollowPolicy.ValidateActivation(activation, hardware, activation, hardware, 1_000_020, 20),
            "Activation accepted an acknowledgement that did not advance past the prepared frame");
        Reject(() => SimulationFollowPolicy.ValidateActivation(paused, hardware, activation, hardware with { Generation = 8 }, 1_000_020, 20),
            "Activation accepted a replaced CAN connection");
        var empty = new OrderedSimulationExecution(first, hardware, 1);
        Reject(() => empty.Step(0, hardware with { GripperOpeningMm = 0 }, true, 0, 1), "Buffered following ignored a genuine Pika drift/raw-zero tracking violation");
        var lagged = new OrderedSimulationExecution(first, hardware, 1);
        lagged.Path.Append([Frame(1, 1, 0, "arm:1"), Frame(2, 2, 20, "arm:1"), Frame(3, 2.02, 20, "finish:2", complete: true)]);
        lagged.Step(0, hardware, false, 0, 1);
        double lagTime = lagged.ActivePhase!.DurationSeconds;
        var advanced = lagged.Step(lagTime, hardware, true, 0, 2);
        check(advanced.JointDegrees[0] == 20 && hardware.JointDegrees[0] == 0 && lagged.CompletedCursor == 0,
            "A planned path may lead measured joints by more than 5deg while the actual controller reference is healthy; endpoint arrival must still wait");
        Reject(() => lagged.Step(lagTime + .01, hardware with { JointDegrees = new[] { 6d, 0, 0, 0, 0, 0 } }, true, 0, 3),
            "Actual encoder divergence from the controller's transmitted reference must still stop queued following");
        Reject(() => new OrderedSimulationExecution(first, hardware, 1).Step(0, hardware with { NativeReference = null }, true, 0, 1),
            "Missing native reference evidence must never fall back to the ahead planned target or bypass tracking checks");
        Reject(() => new OrderedSimulationExecution(first, hardware, 1).Step(0, hardware with { NativeReference = hardware.NativeReference! with { JointDegrees = new[] { double.NaN, 0, 0, 0, 0, 0 } } }, true, 0, 1),
            "Nonfinite native reference evidence must not permit queued physical targets");
        var sequenceGuard = new OrderedSimulationExecution(first, hardware, 1);
        sequenceGuard.Step(0, hardware with { NativeReference = hardware.NativeReference! with { Sequence = 5 } }, true, 0, 1);
        Reject(() => sequenceGuard.Step(.01, hardware with { NativeReference = hardware.NativeReference! with { Sequence = 4 } }, true, 0, 2),
            "An older native reference sequence must not replace the current controller reference");
        Reject(() => sequenceGuard.Step(.01, hardware with { NativeReference = hardware.NativeReference! with { RequestId = 78 } }, true, 0, 2),
            "A different native control request must not retain the previous physical execution progress");
        var execute = new OrderedSimulationExecution(first, hardware, 1);
        execute.Path.Append([Frame(1, 1, 0, "arm:1"), Frame(2, 1.02, .1, "arm:1"), Frame(3, 1.04, .2, "finish:2", complete: true)]);
        var arm = execute.Path.Current!;
        for (double time = 0; time < arm.DurationSeconds + .5 && execute.CompletedCursor == 0; time += .01)
        {
            var desired = arm.At(time);
            execute.Step(time, hardware with { JointDegrees = desired.JointDegrees }, time >= arm.DurationSeconds, 0, 100 + time);
        }
        check(execute.CompletedCursor == 2 && execute.Path.Current?.Kind == OrderedSimulationPhaseKind.Arm,
            "The endpoint barrier did not retire the exact first semantic phase before the terminal boundary");
        var terminal = execute.Path.Current!;
        double began = arm.DurationSeconds + .6;
        for (double time = began; time < began + terminal.DurationSeconds + .5; time += .01)
            execute.Step(time, hardware with { JointDegrees = terminal.At(time - began).JointDegrees }, true, 0, 100 + time);
        check(execute.Path.Drained && execute.CompletedCursor == 3, "A fully arrived successful tape did not drain to its exact final cursor");

        execute = new(first, hardware, 1);
        execute.Path.Append([Frame(1, 1, 0, "gripper:1"), Frame(2, 1.1, 0, "gripper:1", 10, 12),
            Frame(3, 1.2, 0, "arm:2", 10, 12), Frame(4, 1.3, .2, "arm:2", 10, 12), Frame(5, 1.4, .2, "finish:3", 10, 12, true)]);
        check(execute.Path.Current!.Kind == OrderedSimulationPhaseKind.Gripper, "The ordered jaw phase was skipped");
        var jaw = execute.Step(0, hardware, true, 0, 1);
        check(jaw.OpeningMm == 10 && execute.ActivePhase!.ExpectedMeasuredOpeningMm == 12,
            "A jaw contact target was replaced by measured opening or lost its expected source contact result");
        execute.Step(.5, hardware, true, 0, 2);
        check(execute.CompletedCursor == 0, "The next arm phase started before the physical jaws arrived");
        execute.Step(.6, hardware with { GripperOpeningMm = 12 }, false, 0, 3);
        execute.Step(.8, hardware with { GripperOpeningMm = 12 }, true, .1, 4);
        check(execute.CompletedCursor == 0, "Moving arm or jaws bypassed the ordered arrival barrier");
        execute.Step(.9, hardware with { GripperOpeningMm = 12 }, true, 0, 5);
        execute.Step(1.1, hardware with { GripperOpeningMm = 12 }, true, 0, 5);
        check(execute.CompletedCursor == 0, "Repeated cached Pika feedback was accepted as a second settled sample");
        execute.Step(1.2, hardware with { GripperOpeningMm = 12 }, true, 0, 6);
        check(execute.CompletedCursor == 2 && execute.ActivePhase is null, "Fresh measured contact did not release the exact jaw barrier");
        execute.Step(1.3, hardware with { GripperOpeningMm = 12 }, true, 0, 7);
        check(execute.ActivePhase?.Kind == OrderedSimulationPhaseKind.Arm, "The ordered arm phase did not begin after physical jaw completion");
        Reject(() => execute.Step(1.4, hardware with { Generation = 8 }, true, 0, 8), "A replacement CAN generation retained physical progress");
        Reject(() => execute.Step(1.2, hardware with { GripperOpeningMm = 12 }, true, 0, 8), "A regressed physical progress clock was accepted");
        var newLimits = hardware.JointLimits.Select(l => l with { MaximumDegreesPerSecond = 29 }).ToArray();
        Reject(() => execute.Step(1.4, hardware with { JointLimits = newLimits }, true, 0, 8), "Changed reported physical limits did not retire the old phase");
        Reject(() => execute.Step(1.4, hardware with { GripperFresh = false }, true, 0, 8), "Queued motion ignored expired Pika feedback");

        // Runtime envelopes remain fresh and ordered while queued phases run;
        // only explicit source backpressure/episode sealing exempts physics stall.
        var initial = SimulationFollowSample.Read(SimulationFollowTests.Pose());
        var observe = new OrderedSimulationObservation(initial);
        SimulationFollowSample Sample(double physics, long seq, long wall, string mode = "play")
        { var raw = SimulationFollowTests.Pose(physics, seq, wall); raw["mode"] = mode; raw["timeScale"] = 1d; return SimulationFollowSample.Read(raw); }
        observe.Validate(Sample(1.02, 2, 20), 1_000_020, 20, false, false, "episode-a", false);
        observe.Validate(Sample(1.02, 3, 100), 1_000_100, 20, false, false, "episode-a", false);
        Reject(() => observe.Validate(Sample(1.02, 4, 300), 1_000_300, 20, false, false, "episode-a", false), "Unchanged queued-source physics rebased its 250ms stall anchor");
        observe = new(initial);
        observe.Validate(Sample(1.02, 2, 20, "pause"), 1_000_020, 20, true, false, "episode-a", false);
        observe.Validate(Sample(1.02, 3, 1000, "pause"), 1_001_000, 20, true, false, "episode-a", false);
        check(true, "An explicitly paused source could not hold while its ordered physical buffer drained");
        Reject(() => observe.Validate(Sample(1.02, 4, 1020, "pause"), 1_001_020, 20, false, false, "episode-a", false), "A manual source pause was treated as follower-owned backpressure");
        Reject(() => observe.Validate(Sample(1.04, 4, 1020), 1_001_020, 151, false, false, "episode-a", false), "Queued following relaxed its 150ms source request deadline");
        Reject(() => observe.Validate(Sample(1.04, 4, 1020), 1_001_300, 20, false, false, "episode-a", false), "Queued following accepted a source envelope older than 250ms");
        observe.Validate(Sample(1.04, 4, 1020), 1_001_020, 20, false, true, "episode-a", false);
        observe.Validate(Sample(1.04, 5, 2020), 1_002_020, 20, false, true, "episode-a", false);
        Reject(() => observe.Validate(Sample(0, 6, 2040), 1_002_040, 20, false, false, "episode-b", false), "A new episode reset before physical completion was accepted");
        observe.Validate(Sample(0, 6, 2040), 1_002_040, 20, false, false, "episode-b", true);
        check(true, "Exact acknowledged episode transition could not reset the source physics clock");
    }
}
