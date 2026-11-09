using System.Globalization;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record OrderedSimulationFrame(ulong Cursor, long SceneTimeNs, double PhysicsTimeSeconds,
    IReadOnlyList<double> CommandDegrees, IReadOnlyList<double> MeasuredDegrees, double CommandOpeningMm,
    double MeasuredOpeningMm, string EpisodeId, long EpisodeOrdinal, string Phase, bool EpisodeComplete, bool EpisodeSucceeded)
{
    public static OrderedSimulationFrame Read(JsonObject frame)
    {
        static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? "";
        static double Number(JsonObject value, string key) => value[key] is JsonValue number && number.TryGetValue<double>(out double result) && double.IsFinite(result)
            ? result : throw new InvalidOperationException("A source frame has no finite " + key);
        static IReadOnlyList<double> Joints(JsonObject value, string key)
        {
            if (value[key] is not JsonArray { Count: 6 } array) throw new InvalidOperationException("A source frame needs six " + key);
            var result = array.Select(item => item is JsonValue number && number.TryGetValue<double>(out double radians) && double.IsFinite(radians)
                ? radians * 180 / Math.PI : throw new InvalidOperationException("A source joint value is invalid")).ToArray();
            return Array.AsReadOnly(result);
        }
        if (!ulong.TryParse(Text(frame, "cursor"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong cursor) || cursor == 0 ||
            !long.TryParse(Text(frame, "simulationTimeNs"), NumberStyles.None, CultureInfo.InvariantCulture, out long sceneTime))
            throw new InvalidOperationException("A source frame cursor or clock is invalid.");
        double physics = Number(frame, "mujocoTimeSeconds"), command = Number(frame, "commandOpeningMm"), measured = Number(frame, "measuredOpeningMm");
        long ordinal = frame["episodeOrdinal"]?.GetValue<long>() ?? 0;
        if (physics < 0 || sceneTime < 0 || command is < -.000001 or > 90.000001 || measured is < -.25 or > 90.25 || ordinal < 0)
            throw new InvalidOperationException("A source frame clock, episode or gripper range is invalid.");
        return new(cursor, sceneTime, physics, Joints(frame, "commandJoints"), Joints(frame, "measuredJoints"), Math.Clamp(command, 0, 90), Math.Clamp(measured, 0, 90),
            Text(frame, "episodeId"), ordinal, Text(frame, "phase"), frame["episodeComplete"]?.GetValue<bool>() == true,
            frame["episodeSucceeded"]?.GetValue<bool>() == true);
    }
}

public enum OrderedSimulationPhaseKind { Hold, Arm, Gripper }
public sealed record OrderedSimulationTarget(IReadOnlyList<double> JointDegrees, double OpeningMm);

/// <summary>A closed semantic phase. Every command waypoint is retained; C1
/// monotone cubics replace instantaneous ZOH jumps, with zero velocity only at
/// phase barriers. Local timing uses analytic extrema, never clipped poses.</summary>
public sealed class OrderedSimulationPhase
{
    private readonly double[] times;
    private readonly OrderedSimulationFrame[] nodes;
    private readonly Cubic[,] curves;
    public IReadOnlyList<OrderedSimulationFrame> Frames { get; }
    public int SourceFrameCount { get; }
    public OrderedSimulationPhaseKind Kind { get; }
    public double DurationSeconds { get; }
    public double SourceDurationSeconds { get; }
    /// <summary>Physical arrival time of each retained source frame, including metadata-only duplicates.</summary>
    public IReadOnlyList<double> PhysicalWaypointTimes { get; }
    public int RetimingIterations { get; }
    public OrderedSimulationFrame End => Frames[^1];
    public double ExpectedMeasuredOpeningMm => End.MeasuredOpeningMm;
    private readonly record struct Cubic(double A, double B, double C, double D, double Duration)
    {
        public double Position(double u) => ((A * u + B) * u + C) * u + D;
        public double Velocity(double u) => (3 * A * u * u + 2 * B * u + C) / Duration;
        public double Acceleration(double u) => (6 * A * u + 2 * B) / (Duration * Duration);
        public (double Velocity, double Acceleration) Peaks()
        {
            double v = Math.Max(Math.Abs(Velocity(0)), Math.Abs(Velocity(1)));
            if (A != 0) { double u = -B / (3 * A); if (u is > 0 and < 1) v = Math.Max(v, Math.Abs(Velocity(u))); }
            return (v, Math.Max(Math.Abs(Acceleration(0)), Math.Abs(Acceleration(1))));
        }
    }

    public OrderedSimulationPhase(IReadOnlyList<OrderedSimulationFrame> frames, int sourceFrameCount,
        IReadOnlyList<EpisodeReplayJointLimit> limits, double selectedSpeedFraction = 1)
    {
        if (frames.Count < 1 || sourceFrameCount < 1 || limits.Count != 6 || !double.IsFinite(selectedSpeedFraction) || selectedSpeedFraction is <= 0 or > 1)
            throw new InvalidOperationException("A closed phase needs ordered source frames and fresh physical limits.");
        frames = frames.Select(f => f with { CommandDegrees = Array.AsReadOnly(f.CommandDegrees.ToArray()), MeasuredDegrees = Array.AsReadOnly(f.MeasuredDegrees.ToArray()) }).ToArray();
        Frames = Array.AsReadOnly(frames.ToArray()); SourceFrameCount = sourceFrameCount;
        // Native may seal episode metadata between two physics ticks. Retain
        // that event, while using a single identical pose at each spline knot.
        var unique = new List<OrderedSimulationFrame>();
        foreach (var frame in frames)
        {
            if (unique.Count > 0 && frame.PhysicsTimeSeconds == unique[^1].PhysicsTimeSeconds)
            {
                if (!frame.CommandDegrees.SequenceEqual(unique[^1].CommandDegrees) || Math.Abs(frame.CommandOpeningMm - unique[^1].CommandOpeningMm) > 1e-8)
                    throw new InvalidOperationException("Source controls changed without a physics step.");
                unique[^1] = frame;
            }
            else unique.Add(frame);
        }
        nodes = unique.ToArray();
        double[] sourceTimes = nodes.Select(f => f.PhysicsTimeSeconds - nodes[0].PhysicsTimeSeconds).ToArray();
        if (sourceTimes.Any(time => !double.IsFinite(time) || time < 0))
            throw new InvalidOperationException("A phase has an invalid source duration.");
        SourceDurationSeconds = sourceTimes[^1];
        const string invalidLimit = "A phase target or physical speed/acceleration limit is invalid; no clipping was performed.";
        for (int joint = 0; joint < 6; joint++)
        {
            var limit = limits[joint];
            if (limit.JointNumber != joint + 1 || !limit.AccelerationFresh || !double.IsFinite(limit.MaximumDegreesPerSecond) ||
                !double.IsFinite(limit.MaximumDegreesPerSecondSquared) || limit.MaximumDegreesPerSecond <= 0 || limit.MaximumDegreesPerSecondSquared <= 0 ||
                !double.IsFinite(limit.MinimumDegrees) || !double.IsFinite(limit.MaximumDegrees) || limit.MinimumDegrees >= limit.MaximumDegrees)
                throw new InvalidOperationException(invalidLimit);
        }
        for (int row = 0; row < frames.Count; row++)
        {
            var frame = frames[row];
            if (frame.CommandDegrees.Count != 6 || frame.MeasuredDegrees.Count != 6 || frame.MeasuredDegrees.Any(q => !double.IsFinite(q)) ||
                !double.IsFinite(frame.PhysicsTimeSeconds) || frame.PhysicsTimeSeconds < 0 || row > 0 && frame.PhysicsTimeSeconds < frames[row - 1].PhysicsTimeSeconds ||
                !double.IsFinite(frame.CommandOpeningMm) || frame.CommandOpeningMm is < 0 or > 90)
                throw new InvalidOperationException("A phase has invalid or unordered source data.");
            for (int joint = 0; joint < 6; joint++)
                if (!double.IsFinite(frame.CommandDegrees[joint]) || frame.CommandDegrees[joint] < limits[joint].MinimumDegrees ||
                    frame.CommandDegrees[joint] > limits[joint].MaximumDegrees)
                    throw new InvalidOperationException(invalidLimit);
        }
        bool arm = frames.Any(f => f.CommandDegrees.Where((q, joint) => Math.Abs(q - frames[0].CommandDegrees[joint]) > .001).Any());
        bool jaws = frames.Any(f => Math.Abs(f.CommandOpeningMm - frames[0].CommandOpeningMm) > .01);
        if (arm && jaws) throw new InvalidOperationException("This phase moves the arm and gripper together. Separate their phases or verify a physical Pika rate before coupled replay.");
        Kind = arm ? OrderedSimulationPhaseKind.Arm : jaws ? OrderedSimulationPhaseKind.Gripper : OrderedSimulationPhaseKind.Hold;
        if (Kind == OrderedSimulationPhaseKind.Gripper)
        {
            int direction = Math.Sign(End.CommandOpeningMm - frames[0].CommandOpeningMm);
            for (int row = 1; row < frames.Count; row++)
                if ((frames[row].CommandOpeningMm - frames[row - 1].CommandOpeningMm) * direction < -.01)
                    throw new InvalidOperationException("A jaw phase reverses direction; publish separate ordered gripper phases.");
        }
        double[] intervals = Enumerable.Range(0, Math.Max(0, nodes.Length - 1)).Select(row => sourceTimes[row + 1] - sourceTimes[row]).ToArray();
        double[] originalIntervals = intervals.ToArray();
        if (intervals.Any(interval => !double.IsFinite(interval) || interval <= 0))
            throw new InvalidOperationException("A phase has unordered physical knots.");
        curves = new Cubic[intervals.Length, 6];
        if (Kind == OrderedSimulationPhaseKind.Arm)
        {
            const int maximumIterations = 500;
            const double margin = 1e-6;
            while (true)
            {
                curves = BuildCurves(nodes, intervals);
                double[] factors = Enumerable.Repeat(1d, intervals.Length).ToArray();
                for (int row = 0; row < intervals.Length; row++)
                {
                    for (int joint = 0; joint < 6; joint++)
                    {
                        var peaks = curves[row, joint].Peaks();
                        if (!double.IsFinite(peaks.Velocity) || !double.IsFinite(peaks.Acceleration))
                            throw new InvalidOperationException("The phase has unbounded velocity or acceleration.");
                        factors[row] = Math.Max(factors[row], Math.Max(peaks.Velocity / limits[joint].MaximumDegreesPerSecond,
                            Math.Sqrt(peaks.Acceleration / limits[joint].MaximumDegreesPerSecondSquared)));
                    }
                }
                if (factors.All(factor => factor <= 1 + 1e-10)) break;
                if (++RetimingIterations > maximumIterations)
                    throw new InvalidOperationException("Local phase retiming did not converge; no physical path was admitted.");
                // Stretch neighboring intervals too, then rebuild every shared tangent.
                // This avoids moving a sharp timing change to the adjacent knot.
                for (int row = 0; row < intervals.Length; row++)
                {
                    double factor = factors[row];
                    if (row > 0) factor = Math.Max(factor, Math.Sqrt(factors[row - 1]));
                    if (row + 1 < intervals.Length) factor = Math.Max(factor, Math.Sqrt(factors[row + 1]));
                    if (factor > 1 + 1e-10) intervals[row] *= factor * (1 + margin);
                    if (!double.IsFinite(intervals[row]) || intervals[row] / originalIntervals[row] > 1000)
                        throw new InvalidOperationException("The phase requires unsupported local replay timing; inspect its source path.");
                }
            }
            // Solve the physical path once at 100%, then slow its clock. Feeding
            // the selected percentage into the local solver can produce a
            // different, non-monotonic stretch solution at each slider value.
            // Uniform dilation preserves every knot and the exact curve shape;
            // velocity scales by f and acceleration by f². Jaw/hold timing is
            // independent of this joint-speed control.
            for (int row = 0; row < intervals.Length; row++)
            {
                intervals[row] /= selectedSpeedFraction;
                for (int joint = 0; joint < 6; joint++)
                    curves[row, joint] = curves[row, joint] with { Duration = intervals[row] };
            }
        }
        times = new double[nodes.Length];
        for (int row = 0; row < intervals.Length; row++) times[row + 1] = times[row] + intervals[row];
        DurationSeconds = times[^1];
        if (!double.IsFinite(DurationSeconds)) throw new InvalidOperationException("The phase has an unsupported physical duration.");
        PhysicalWaypointTimes = Array.AsReadOnly(frames.Select(frame => times[Array.BinarySearch(sourceTimes, frame.PhysicsTimeSeconds - nodes[0].PhysicsTimeSeconds)]).ToArray());
    }

    private static Cubic[,] BuildCurves(IReadOnlyList<OrderedSimulationFrame> nodes, double[] intervals)
    {
        var result = new Cubic[intervals.Length, 6];
        for (int joint = 0; joint < 6; joint++)
        {
            double[] slopes = new double[intervals.Length], tangents = new double[nodes.Count];
            for (int row = 0; row < slopes.Length; row++)
                slopes[row] = (nodes[row + 1].CommandDegrees[joint] - nodes[row].CommandDegrees[joint]) / intervals[row];
            for (int row = 1; row < nodes.Count - 1; row++)
            {
                double first = slopes[row - 1], second = slopes[row];
                if (first * second <= 0) continue;
                double previous = intervals[row - 1], next = intervals[row];
                double w1 = 2 * next + previous, w2 = next + 2 * previous;
                tangents[row] = (w1 + w2) / (w1 / first + w2 / second);
            }
            for (int row = 0; row < slopes.Length; row++)
            {
                double h = intervals[row], first = nodes[row].CommandDegrees[joint], second = nodes[row + 1].CommandDegrees[joint];
                result[row, joint] = new(2 * first - 2 * second + h * (tangents[row] + tangents[row + 1]),
                    -3 * first + 3 * second - h * (2 * tangents[row] + tangents[row + 1]), h * tangents[row], first, h);
            }
        }
        return result;
    }

    public OrderedSimulationTarget At(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (Kind != OrderedSimulationPhaseKind.Arm || nodes.Length == 1 || elapsedSeconds >= DurationSeconds)
            return new(End.CommandDegrees, End.CommandOpeningMm);
        int row = Array.BinarySearch(times, elapsedSeconds); if (row < 0) row = ~row - 1;
        row = Math.Clamp(row, 0, nodes.Length - 2);
        double u = (elapsedSeconds - times[row]) / (times[row + 1] - times[row]);
        return new(Array.AsReadOnly(Enumerable.Range(0, 6).Select(j => curves[row, j].Position(u)).ToArray()), Frames[0].CommandOpeningMm);
    }
}

/// <summary>Retains all source nodes until their semantic phase physically
/// completes. A cursor gap/overflow/new uncompleted episode is never skipped.</summary>
public sealed class OrderedSimulationPath
{
    public const int MaximumFrames = 4096;
    public const int HighWaterFrames = 3072;
    public const int LowWaterFrames = 2048;
    private readonly Queue<OrderedSimulationPhase> closed = new();
    private readonly List<OrderedSimulationFrame> open = [];
    private readonly IReadOnlyList<EpisodeReplayJointLimit> limits;
    private readonly double speedFraction;
    private OrderedSimulationFrame? anchor, last;
    private bool missedGraspSeen, missedGraspHomeSeen;
    private int openSourceCount;
    public int FrameCount { get; private set; }
    public ulong LastCursor => last?.Cursor ?? 0;
    public bool SourceComplete => last?.EpisodeComplete == true;
    public bool Drained => SourceComplete && FrameCount == 0;
    public OrderedSimulationFrame? LastSource => last;
    public OrderedSimulationPhase? Current => closed.TryPeek(out var phase) ? phase : null;
    public bool SourceMissedGrasp => missedGraspSeen;

    public OrderedSimulationPath(IReadOnlyList<EpisodeReplayJointLimit> limits, double speedFraction = 1)
    {
        if (!double.IsFinite(speedFraction) || speedFraction is <= 0 or > 1)
            throw new InvalidOperationException("Choose a joint speed above 0% and at most 100% before preparing the ordered follower.");
        this.limits = limits; this.speedFraction = speedFraction;
    }

    public void Append(IReadOnlyList<OrderedSimulationFrame> frames)
    {
        if (frames.Count > MaximumFrames - FrameCount) throw new InvalidOperationException("Ordered follower buffer is full; source frames were not discarded.");
        ulong cursor = LastCursor;
        foreach (var frame in frames)
        {
            if (cursor == ulong.MaxValue || frame.Cursor != cursor + 1) throw new InvalidOperationException("Ordered source cursor gap; missing path frames cannot be replayed.");
            cursor = frame.Cursor;
        }
        foreach (var frame in frames)
        {
            bool episodeChanged = last is not null && frame.EpisodeId != last.EpisodeId;
            if (episodeChanged && last!.EpisodeId.Length > 0 && (!last.EpisodeComplete || FrameCount != 0))
                throw new InvalidOperationException("A new source episode arrived before the real follower finished the previous one.");
            if (episodeChanged) missedGraspSeen = missedGraspHomeSeen = false;
            if (IsMissedGraspPhase(frame.Phase))
            {
                missedGraspSeen = true;
                if (PhaseKind(frame.Phase) == "home") missedGraspHomeSeen = true;
            }
            if (frame.EpisodeComplete && !frame.EpisodeSucceeded &&
                (!IsMissedGraspPhase(frame.Phase) || PhaseKind(frame.Phase) != "finish" || !missedGraspSeen || !missedGraspHomeSeen))
                throw new InvalidOperationException("The source episode failed without an ordered missed-grasp home; queued physical motion stopped.");
            if (last is not null && !episodeChanged && (frame.PhysicsTimeSeconds < last.PhysicsTimeSeconds || frame.SceneTimeNs < last.SceneTimeNs ||
                frame.PhysicsTimeSeconds == last.PhysicsTimeSeconds && !frame.EpisodeComplete && frame.Phase == last.Phase))
                throw new InvalidOperationException("Source physics frames or their scene clock went backwards.");
            // Every semantic phase closes at its own endpoint barrier.
            if (last is not null && (episodeChanged || frame.Phase != last.Phase)) CloseOpen();
            // The initial unlabelled prepared frame still anchors the first
            // episode's first command. Retire the old anchor only at a completed
            // episode boundary, where a source physics clock may restart.
            if (episodeChanged && last!.EpisodeId.Length > 0) anchor = null;
            if (open.Count == 0 && anchor is not null && frame.PhysicsTimeSeconds >= anchor.PhysicsTimeSeconds) open.Add(anchor);
            open.Add(frame); openSourceCount++; FrameCount++; last = frame;
            if (frame.EpisodeComplete) CloseOpen();
        }
        if (FrameCount >= HighWaterFrames && closed.Count == 0)
            throw new InvalidOperationException("A source phase exceeds bounded lookahead; split the phase before physical following.");
    }

    private void CloseOpen()
    {
        if (openSourceCount == 0) return;
        closed.Enqueue(new(open.ToArray(), openSourceCount, limits, speedFraction));
        anchor = open[^1]; open.Clear(); openSourceCount = 0;
    }

    public static string PhaseKind(string phase)
    {
        string kind = phase.Split(':', 2)[0];
        return IsMissedGraspPhase(phase) ? kind["missed-grasp-".Length..] : kind;
    }

    private static bool IsMissedGraspPhase(string phase)
    {
        const string prefix = "missed-grasp-";
        int separator = phase.IndexOf(':');
        return separator > prefix.Length && phase.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(phase.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) && ordinal > 0 &&
            phase[prefix.Length..separator] is "arm" or "gripper" or "wait" or "verify-lift" or "verify-carry" or "verify-place" or "home" or "finish";
    }

    public void Complete(OrderedSimulationPhase phase)
    {
        if (!closed.TryPeek(out var current) || !ReferenceEquals(current, phase)) throw new InvalidOperationException("Physical phase completion arrived out of order.");
        closed.Dequeue(); FrameCount -= phase.SourceFrameCount;
    }
}
