namespace LuckyRobots.Piper.Plugin;

/// <summary>Review-only checks of an exported trajectory against reported joint
/// limits. Physical playback is unavailable, so nothing here plans motion.</summary>
public static class EpisodeReplayPlanner
{
    /// <summary>Joint-only timing assessment for review when verified Pika limits are unavailable.</summary>
    public static EpisodeReplayJointTiming AssessJointTiming(EpisodeReplayArtifact artifact, IReadOnlyList<EpisodeReplayJointLimit> limits)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequirePositionCommands(artifact);
        ValidateJointLimits(limits);
        var targets = ReadCommands(artifact);
        ValidateJointTargets(targets, limits);
        return CalculateJointTiming(targets, limits);
    }

    /// <summary>Every recorded joint value lies inside the reported range. No clipping is performed.</summary>
    public static void ValidateJointRanges(IReadOnlyList<IReadOnlyList<double>> rows, IReadOnlyList<EpisodeReplayJointLimit> limits)
    {
        for (int row = 0; row < rows.Count; row++)
            for (int i = 0; i < 6; i++)
            {
                double angle = rows[row][i];
                var limit = limits[i];
                if (!double.IsFinite(angle) || angle < limit.MinimumDegrees || angle > limit.MaximumDegrees)
                    throw new InvalidOperationException($"Sample {row}, J{i + 1}: {angle} degrees is outside the reported {limit.MinimumDegrees}–{limit.MaximumDegrees} degree range. No clipping was performed.");
            }
    }

    private static void RequirePositionCommands(EpisodeReplayArtifact artifact)
    {
        if (!artifact.HasPositionCommands || artifact.CommandTimestampConvention != EpisodeReplayReader.CommandTimestampConvention ||
            artifact.Robot.CommandConvention != EpisodeReplayReader.CommandConvention || artifact.Robot.PositionActuatorProvenance is null)
            throw new InvalidOperationException("Version 1 contains measured observations only and is review-only. Physical planning requires version 2 verified position-actuator commands.");
    }

    private static void ValidateJointLimits(IReadOnlyList<EpisodeReplayJointLimit> limits)
    {
        if (limits is not { Count: 6 })
            throw new InvalidOperationException("Exactly six ordered reported joint limits are required.");
        for (int i = 0; i < limits.Count; i++)
        {
            var limit = limits[i];
            if (limit.JointNumber != i + 1 || !double.IsFinite(limit.MinimumDegrees) || !double.IsFinite(limit.MaximumDegrees) ||
                limit.MinimumDegrees >= limit.MaximumDegrees || !double.IsFinite(limit.MaximumDegreesPerSecond) || limit.MaximumDegreesPerSecond <= 0)
                throw new InvalidOperationException($"J{i + 1} needs a valid ordered reported range and speed.");
            if (!limit.AccelerationFresh || !double.IsFinite(limit.MaximumDegreesPerSecondSquared) || limit.MaximumDegreesPerSecondSquared <= 0)
                throw new InvalidOperationException($"J{i + 1} needs a fresh positive reported acceleration limit; no fallback acceleration is assumed.");
        }
    }

    private static EpisodeReplayTarget[] ReadCommands(EpisodeReplayArtifact artifact)
    {
        return artifact.Samples.Select((sample, index) =>
        {
            if (sample.CommandSource != EpisodeReplayReader.CommandSource || sample.CommandedJointDegrees is not { Count: 6 } ||
                sample.CommandedGripperOpeningMm is not double opening)
                throw new InvalidOperationException($"Sample {index} has no verified position-command targets; observations cannot substitute for actions.");
            return new EpisodeReplayTarget(sample.TimeSeconds, Array.AsReadOnly(sample.CommandedJointDegrees.ToArray()), opening,
                sample.SyncIndex, EpisodeReplayReader.CommandSource);
        }).ToArray();
    }

    private static void ValidateJointTargets(IReadOnlyList<EpisodeReplayTarget> samples, IReadOnlyList<EpisodeReplayJointLimit> limits)
    {
        if (samples is null || samples.Count is < 2 or > EpisodeReplayReader.MaximumSamples)
            throw new InvalidOperationException("Replay requires a bounded trajectory with at least two samples.");
        for (int row = 0; row < samples.Count; row++)
        {
            var sample = samples[row];
            if (sample.Source != EpisodeReplayReader.CommandSource || sample.JointDegrees is not { Count: 6 } ||
                !double.IsFinite(sample.TimeSeconds) || sample.TimeSeconds < 0 ||
                (row == 0 ? sample.TimeSeconds != 0 : sample.TimeSeconds <= samples[row - 1].TimeSeconds))
                throw new InvalidOperationException($"Sample {row} is not an ordered position-command trajectory.");
        }
        ValidateJointRanges(samples.Select(sample => sample.JointDegrees).ToArray(), limits);
    }

    private static EpisodeReplayJointTiming CalculateJointTiming(IReadOnlyList<EpisodeReplayTarget> samples, IReadOnlyList<EpisodeReplayJointLimit> limits)
    {
        double speedScale = 1, accelerationScale = 1;
        for (int row = 1; row < samples.Count; row++)
        {
            double seconds = samples[row].TimeSeconds - samples[row - 1].TimeSeconds;
            for (int i = 0; i < 6; i++)
            {
                double velocity = (samples[row].JointDegrees[i] - samples[row - 1].JointDegrees[i]) / seconds;
                speedScale = Math.Max(speedScale, Math.Abs(velocity) / limits[i].MaximumDegreesPerSecond);
                if (row < 2)
                    continue;
                double previousSeconds = samples[row - 1].TimeSeconds - samples[row - 2].TimeSeconds;
                double previousVelocity = (samples[row - 1].JointDegrees[i] - samples[row - 2].JointDegrees[i]) / previousSeconds;
                double acceleration = Math.Abs(velocity - previousVelocity) / (seconds / 2 + previousSeconds / 2);
                accelerationScale = Math.Max(accelerationScale, Math.Sqrt(acceleration / limits[i].MaximumDegreesPerSecondSquared));
            }
        }
        if (!double.IsFinite(speedScale) || !double.IsFinite(accelerationScale))
            throw new InvalidOperationException("Trajectory reference speed/acceleration overflows; timing cannot be planned.");
        return new(speedScale, accelerationScale);
    }
}
