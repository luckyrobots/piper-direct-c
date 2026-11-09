using LuckyRobots.Piper.Plugin;

internal static class OrderedSimulationPathTests
{
    public static void Run(Action<bool, string> check)
    {
        static OrderedSimulationFrame Frame(ulong cursor, double time, double position, string phase = "arm:1", bool complete = false) =>
            new(cursor, (long)Math.Round(time * 1e9), time, new[] { position, 0d, 0d, 0d, 0d, 0d }, new[] { position, 0d, 0d, 0d, 0d, 0d },
                80, 80, "episode-a", 0, phase, complete, true);
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 20, 10, true)).ToArray();
        double[] sourceTimes = [0, 1, 2, 2.02, 2.04, 3.04, 4.04];
        double[] positions = [0, 5, 10, 10.001, 10.002, 15, 20];
        var frames = positions.Select((position, row) => Frame((ulong)row + 1, sourceTimes[row], position)).ToArray();
        var phase = new OrderedSimulationPhase(frames, frames.Length, limits);
        check(phase.RetimingIterations is > 0 and <= 500 && phase.DurationSeconds > phase.SourceDurationSeconds && phase.PhysicalWaypointTimes.Count == frames.Length,
            "A local acceleration peak must converge within the bounded iteration count and retain every source knot");
        var intervalScales = Enumerable.Range(1, frames.Length - 1).Select(row =>
            (phase.PhysicalWaypointTimes[row] - phase.PhysicalWaypointTimes[row - 1]) / (sourceTimes[row] - sourceTimes[row - 1])).ToArray();
        check(intervalScales.Max() - intervalScales.Min() > .1,
            "One local peak must not force a uniform slowdown of the entire semantic arm phase");
        check(WithinSampledLimits(phase, 20, 10),
            "Analytic extrema after all tangent rebuilds must respect every physical velocity and acceleration limit");
        for (int row = 0; row < frames.Length; row++)
        {
            var target = phase.At(phase.PhysicalWaypointTimes[row]);
            check(Math.Abs(target.JointDegrees[0] - positions[row]) < 1e-9,
                "Variable physical time must reach the exact ordered source command without clipping or dropping it");
        }
        for (int row = 1; row < frames.Length - 1; row++)
        {
            double t = phase.PhysicalWaypointTimes[row];
            double epsilon = Math.Min(t - phase.PhysicalWaypointTimes[row - 1], phase.PhysicalWaypointTimes[row + 1] - t) * 1e-5;
            double centre = phase.At(t).JointDegrees[0];
            double left = (centre - phase.At(t - epsilon).JointDegrees[0]) / epsilon;
            double right = (phase.At(t + epsilon).JointDegrees[0] - centre) / epsilon;
            check(Math.Abs(left - right) <= 10 * epsilon * 2 + 1e-7,
                "Adjacent retimed intervals must share the same velocity instead of restarting at every source frame");
        }
        double interior = phase.PhysicalWaypointTimes[1], delta = .0001;
        check((phase.At(interior + delta).JointDegrees[0] - phase.At(interior - delta).JointDegrees[0]) / (2 * delta) > .1,
            "Monotone interior knots must keep moving; only semantic endpoints or direction reversals may stop");
        for (int row = 1; row < frames.Length; row++)
        {
            double a = phase.PhysicalWaypointTimes[row - 1], b = phase.PhysicalWaypointTimes[row];
            for (int sample = 1; sample < 20; sample++)
            {
                double t = a + (b - a) * sample / 20;
                double epsilon = (b - a) * 1e-4;
                double q = phase.At(t).JointDegrees[0];
                double before = phase.At(t - epsilon).JointDegrees[0], after = phase.At(t + epsilon).JointDegrees[0];
                double v = (after - before) / (2 * epsilon), acceleration = (after - 2 * q + before) / (epsilon * epsilon);
                check(q >= positions[row - 1] - 1e-9 && q <= positions[row] + 1e-9 && Math.Abs(v) <= 20 + 1e-5 && Math.Abs(acceleration) <= 10 + 1e-4,
                    "Independent interior samples must remain monotone and within the reported velocity/acceleration bounds");
            }
        }
        var slower = new OrderedSimulationPhase(frames, frames.Length, limits, .1);
        check(WithinSampledLimits(slower, 2, 10),
            "The selected speed percentage must limit local velocity while retaining the physical acceleration bound");

        var metadata = frames.Concat([frames[^1] with { Cursor = 8, Phase = "finish:2", EpisodeComplete = true }]).ToArray();
        var sealedPhase = new OrderedSimulationPhase(metadata, metadata.Length, limits);
        check(sealedPhase.Frames.Count == metadata.Length && sealedPhase.PhysicalWaypointTimes[^1] == sealedPhase.PhysicalWaypointTimes[^2] &&
            sealedPhase.At(sealedPhase.DurationSeconds).JointDegrees.SequenceEqual(frames[^1].CommandDegrees),
            "A metadata-only terminal event must survive without inventing a zero-duration movement segment");
        var flat = new OrderedSimulationPhase([Frame(1, 0, 0), Frame(2, 1, 0)], 2, limits);
        check(flat.DurationSeconds == 1 && flat.SourceDurationSeconds == 1 && flat.RetimingIterations == 0,
            "A stationary hold retains its source duration without artificial slowdown");

        var initialPath = new OrderedSimulationPath(limits);
        initialPath.Append([
            Frame(1, 0, 0, "") with { EpisodeId = "" },
            Frame(2, .02, 1),
            Frame(3, .04, 1, "gripper:2") with { CommandOpeningMm = 70, MeasuredOpeningMm = 70 },
            Frame(4, .06, 1, "finish:3", true) with { CommandOpeningMm = 70, MeasuredOpeningMm = 70 }
        ]);
        initialPath.Complete(initialPath.Current!);
        var firstArm = initialPath.Current!;
        check(firstArm.Kind == OrderedSimulationPhaseKind.Arm && firstArm.Frames[0].EpisodeId.Length == 0 && firstArm.Frames[0].CommandDegrees[0] == 0 &&
            firstArm.End.CommandDegrees[0] == 1 && firstArm.DurationSeconds > 0,
            "The unlabelled prepared pose must anchor the first episode rather than instantly jumping to its first moving command");

        void Reject(Action action, string reason)
        {
            try { action(); } catch (InvalidOperationException) { check(true, reason); return; }
            check(false, reason);
        }
        Reject(() => new OrderedSimulationPhase(frames, frames.Length, limits.Select(j => j with { MaximumDegreesPerSecondSquared = 1e-12 }).ToArray()),
            "Unsupported local interval expansion must fail closed rather than bypass the physical limits");
        Reject(() => new OrderedSimulationPhase([Frame(1, 0, 0), Frame(2, 1e-300, 1)], 2, limits),
            "Non-finite analytic acceleration must fail before admitting a physical path");
        Reject(() => new OrderedSimulationPhase([Frame(1, 0, 0), Frame(2, 0, 1)], 2, limits),
            "Changed commands at an identical physics timestamp cannot be smoothed into invented source time");
        Reject(() => new OrderedSimulationPhase(frames, frames.Length, limits.Select(j => j with { AccelerationFresh = false }).ToArray()),
            "Local retiming must never invent an acceleration limit when the robot has not reported one");
        Reject(() => new OrderedSimulationPhase([Frame(1, 0, 0), Frame(2, 1, 181)], 2, limits),
            "An out-of-range path must be rejected without clipping its source waypoint");
    }

    /// <summary>Dense central-difference samples of every joint; the phase exposes only positions.</summary>
    internal static bool WithinSampledLimits(OrderedSimulationPhase phase, double velocityLimit, double accelerationLimit)
    {
        double duration = phase.DurationSeconds, epsilon = duration * 1e-4;
        for (int sample = 1; sample < 4000; sample++)
        {
            double t = Math.Clamp(duration * sample / 4000, epsilon, duration - epsilon);
            var before = phase.At(t - epsilon).JointDegrees; var centre = phase.At(t).JointDegrees; var after = phase.At(t + epsilon).JointDegrees;
            for (int joint = 0; joint < 6; joint++)
            {
                double v = (after[joint] - before[joint]) / (2 * epsilon);
                double a = (after[joint] - 2 * centre[joint] + before[joint]) / (epsilon * epsilon);
                if (Math.Abs(v) > velocityLimit * (1 + 1e-3) + 1e-7 || Math.Abs(a) > accelerationLimit * (1 + 1e-3) + 1e-7) return false;
            }
        }
        return true;
    }
}
