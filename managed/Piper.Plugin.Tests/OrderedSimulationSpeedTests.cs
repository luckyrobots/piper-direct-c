using LuckyRobots.Piper.Plugin;

internal static class OrderedSimulationSpeedTests
{
    public static void Run(Action<bool, string> check)
    {
        double[] sourceTimes = [0, .02, .04, .24, .26, .28, 1.28, 1.3];
        double[] positions = [0, 4, 8, 8.001, 8.002, 9, -3, -4];
        OrderedSimulationFrame Frame(int row, string phase = "arm:1", double opening = 80) => new((ulong)row + 1,
            (long)Math.Round(sourceTimes[row] * 1e9), sourceTimes[row], new[] { positions[row], 0d, 0d, 0d, 0d, 0d },
            new[] { positions[row], 0d, 0d, 0d, 0d, 0d }, opening, opening, "speed-episode", 0, phase, row == positions.Length - 1, true);
        var frames = Enumerable.Range(0, positions.Length).Select(row => Frame(row)).ToArray();
        var limits = Enumerable.Range(1, 6).Select(j => new EpisodeReplayJointLimit(j, -180, 180, 50, 20, true)).ToArray();
        var full = new OrderedSimulationPhase(frames, frames.Length, limits);
        double previousDuration = double.PositiveInfinity;
        foreach (double speed in new[] { .01, .1, .2, .5, .9, 1d })
        {
            var phase = new OrderedSimulationPhase(frames, frames.Length, limits, speed);
            check(phase.DurationSeconds <= previousDuration && Math.Abs(phase.DurationSeconds * speed - full.DurationSeconds) < 1e-9 &&
                phase.RetimingIterations == full.RetimingIterations,
                "Increasing joint speed reran a different local stretch solution or failed predictable inverse timing");
            previousDuration = phase.DurationSeconds;
            check(phase.PeakVelocityDegreesPerSecond.Zip(full.PeakVelocityDegreesPerSecond, (a, b) => Math.Abs(a - b * speed)).All(e => e < 1e-9) &&
                phase.PeakAccelerationDegreesPerSecondSquared.Zip(full.PeakAccelerationDegreesPerSecondSquared, (a, b) => Math.Abs(a - b * speed * speed)).All(e => e < 1e-9),
                "Slower joint playback did not preserve the analytic velocity/acceleration scaling");
            check(phase.PeakVelocityDegreesPerSecond.All(v => v <= 50 * speed + 1e-9) &&
                phase.PeakAccelerationDegreesPerSecondSquared.All(a => a <= 20 * speed * speed + 1e-9),
                "Uniform joint clock dilation exceeded the reported physical bounds");
            for (int node = 0; node < frames.Length; node++)
            {
                check(Math.Abs(phase.PhysicalWaypointTimes[node] * speed - full.PhysicalWaypointTimes[node]) < 1e-9 &&
                    phase.At(phase.PhysicalWaypointTimes[node]).JointDegrees.Zip(frames[node].CommandDegrees, (a, b) => Math.Abs(a - b)).All(e => e < 1e-8),
                    "A speed setting dropped, reordered, clipped or independently retimed a source waypoint");
            }
            for (int sample = 0; sample <= 40; sample++)
            {
                double normalized = sample / 40d;
                check(phase.At(phase.DurationSeconds * normalized).JointDegrees.Zip(full.At(full.DurationSeconds * normalized).JointDegrees,
                    (a, b) => Math.Abs(a - b)).All(e => e < 1e-8),
                    "The joint speed setting changed the geometric C1 path between source knots");
            }
        }
        var stationary = frames.Take(2).Select(f => f with { CommandDegrees = new double[6], MeasuredDegrees = new double[6] }).ToArray();
        foreach (bool opening in new[] { false, true })
        {
            var other = stationary.Select((f, row) => f with { CommandOpeningMm = opening ? 30 + row * 50 : 80 }).ToArray();
            var normal = new OrderedSimulationPhase(other, other.Length, limits);
            var slow = new OrderedSimulationPhase(other, other.Length, limits, .01);
            check(slow.DurationSeconds == normal.DurationSeconds && slow.At(slow.DurationSeconds).OpeningMm == normal.End.CommandOpeningMm,
                "The joint-speed knob added an unrelated hold/jaw delay without controlling gripper velocity");
        }
        foreach (double speed in new[] { 0, -.01, 1.01, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false;
            try { _ = new OrderedSimulationPath(limits, speed); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "An invalid or zero ordered speed was silently clamped into motion");
        }
    }
}
