namespace LuckyRobots.Piper.Plugin;

/// <summary>Builds reviewed position waypoints only; contains no robot commands, homing or recovery actions.</summary>
public static class EpisodeReplayPlanner
{
    public static EpisodeReplayPlan Build(EpisodeReplayArtifact artifact, EpisodeReplayHardware hardware, EpisodeReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequirePositionCommands(artifact);
        options ??= new();
        ValidateOptions(options);
        ValidateHardware(hardware, artifact.Robot);
        var targets = ReadCommands(artifact);
        ValidateTargets(targets, hardware);
        var jointTiming = CalculateJointTiming(targets, hardware.JointLimits);
        double scale = RequiredTimeScale(targets, hardware, jointTiming);
        if (scale > 1 && !options.AllowTimeScaling)
            throw new InvalidOperationException($"Command reference speed/acceleration or gripper rate exceeds a verified physical limit; explicit uniform time scaling of at least {scale:F6}× is required. No positions were clipped.");
        if (scale > options.MaximumTimeScale || artifact.Samples[^1].TimeSeconds * scale > 24 * 3600)
            throw new InvalidOperationException("Required replay time scaling exceeds the permitted factor or 24-hour duration bound.");

        var samples = targets.Select(sample => sample with
        {
            TimeSeconds = sample.TimeSeconds * scale,
            JointDegrees = Array.AsReadOnly(sample.JointDegrees.ToArray())
        }).ToArray();
        var measuredStart = artifact.Samples[0];
        var differences = ComparePose(measuredStart.JointDegrees, measuredStart.GripperOpeningMm, hardware.JointDegrees, hardware.GripperOpeningMm,
            options.StartJointToleranceDegrees, options.StartGripperToleranceMm);
        var commandDifferences = ComparePose(samples[0].JointDegrees, samples[0].GripperOpeningMm, measuredStart.JointDegrees, measuredStart.GripperOpeningMm,
            options.InitialCommandJointToleranceDegrees, options.InitialCommandGripperToleranceMm);
        string[] notes =
        [
            scale > 1 ? $"Timing explicitly stretched by {scale:F6}×; positions and gripper openings are unchanged." : "Original sample timing retained.",
            differences.Count == 0 ? "Current measured pose matches the first sample." : "Start is blocked: align to the displayed first pose separately, then validate fresh feedback again. No approach or zero-position move was generated.",
            commandDifferences.Count == 0 ? "The first command is close to the measured source start within explicit tolerances." : "Start is blocked: the first command differs from the measured source pose beyond explicit tolerances. No initial jump was inserted.",
            $"Joint reference speed requires {jointTiming.SpeedTimeScale:F6}× timing; sampled reference acceleration requires {jointTiming.AccelerationTimeScale:F6}× timing.",
            "Zero-order-held targets are unchanged. Checks use interval velocities and differences between adjacent interval velocities at their midpoint times; they do not certify instantaneous ZOH transitions, endpoint transients, contact forces or unrecorded internal control updates.",
            "Actuator semantics rely on pinned authored position tags and exported runtime mapping checks; runtime actuator-law type is not independently introspectable.",
            "Initial physical tube placement must match the exported LEFT/BACK/yaw and object dimensions before replay."
        ];
        return new(artifact, hardware.DeviceId, hardware.Generation, Array.AsReadOnly(samples), scale, differences, commandDifferences, options, Array.AsReadOnly(notes));
    }

    /// <summary>Rechecks identity, limits and the measured start immediately before a future executor starts.</summary>
    public static IReadOnlyList<EpisodeReplayStartDifference> CheckStart(EpisodeReplayPlan plan, EpisodeReplayHardware hardware)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Artifact.HasPositionCommands || plan.InitialCommandDifferences.Count != 0)
            throw new InvalidOperationException("The source's initial command is not eligible for replay; rebuild a version2 plan with explicit valid initial-command tolerances.");
        ValidateHardware(hardware, plan.Artifact.Robot);
        if (hardware.DeviceId != plan.DeviceId || hardware.Generation != plan.Generation)
            throw new InvalidOperationException("The physical adapter identity/generation changed; rebuild the replay plan.");
        ValidateTargets(plan.Samples, hardware);
        if (RequiredTimeScale(plan.Samples, hardware, CalculateJointTiming(plan.Samples, hardware.JointLimits)) > 1 + 1e-9)
            throw new InvalidOperationException("Reported physical speed or acceleration limits changed; rebuild the replay plan.");
        var measured = plan.Artifact.Samples[0];
        return ComparePose(measured.JointDegrees, measured.GripperOpeningMm, hardware.JointDegrees, hardware.GripperOpeningMm,
            plan.Options.StartJointToleranceDegrees, plan.Options.StartGripperToleranceMm);
    }

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

    private static void RequirePositionCommands(EpisodeReplayArtifact artifact)
    {
        if (!artifact.HasPositionCommands || artifact.CommandTimestampConvention != EpisodeReplayReader.CommandTimestampConvention ||
            artifact.Robot.CommandConvention != EpisodeReplayReader.CommandConvention || artifact.Robot.PositionActuatorProvenance is null)
            throw new InvalidOperationException("Version 1 contains measured observations only and is review-only. Physical planning requires version 2 verified position-actuator commands.");
    }

    private static void ValidateOptions(EpisodeReplayOptions options)
    {
        if (!double.IsFinite(options.MaximumTimeScale) || options.MaximumTimeScale is < 1 or > 10_000 ||
            !double.IsFinite(options.StartJointToleranceDegrees) || options.StartJointToleranceDegrees is < 0 or > 5 ||
            !double.IsFinite(options.StartGripperToleranceMm) || options.StartGripperToleranceMm is < 0 or > 5 ||
            !double.IsFinite(options.InitialCommandJointToleranceDegrees) || options.InitialCommandJointToleranceDegrees is < 0 or > 5 ||
            !double.IsFinite(options.InitialCommandGripperToleranceMm) || options.InitialCommandGripperToleranceMm is < 0 or > 5)
            throw new InvalidOperationException("Replay options require finite explicit time-scale and bounded start tolerances (0–5 degrees/mm).");
    }

    private static void ValidateHardware(EpisodeReplayHardware hardware, EpisodeReplayRobot robot)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (string.IsNullOrWhiteSpace(hardware.DeviceId) || hardware.Generation < 0 ||
            hardware.RobotModel != "agilex.piper" || robot.Model != hardware.RobotModel ||
            hardware.JointConvention != EpisodeReplayReader.JointConvention || robot.JointConvention != hardware.JointConvention ||
            hardware.GripperConvention != EpisodeReplayReader.GripperConvention || robot.GripperConvention != hardware.GripperConvention ||
            robot.GripperMapping != EpisodeReplayReader.GripperMapping || robot.JointNames is not { Count: 6 } ||
            !robot.JointNames.SequenceEqual(Enumerable.Range(1, 6).Select(i => "joint" + i)))
            throw new InvalidOperationException("Replay requires the explicit physical Piper J1–J6 degree convention and Pika millimetre opening mapping.");
        if (!hardware.FeedbackFresh || !hardware.LimitsFresh || !hardware.GripperFresh || !hardware.GripperLimitsVerified)
            throw new InvalidOperationException("Fresh joint/gripper measurements, reported joint limits and verified gripper limits are required; no fallback limits are assumed.");
        ValidateJointLimits(hardware.JointLimits);
        if (hardware.JointDegrees is not { Count: 6 })
            throw new InvalidOperationException("Exactly six physical joint readings and ordered reported limits are required.");
        for (int i = 0; i < 6; i++)
        {
            var limit = hardware.JointLimits[i];
            if (!double.IsFinite(hardware.JointDegrees[i]) || hardware.JointDegrees[i] < limit.MinimumDegrees || hardware.JointDegrees[i] > limit.MaximumDegrees)
                throw new InvalidOperationException($"J{i + 1} needs an in-range current measurement. Replay cannot perform range recovery.");
        }
        if (!double.IsFinite(hardware.MaximumGripperOpeningMm) || hardware.MaximumGripperOpeningMm is <= 0 or > 90 ||
            !double.IsFinite(hardware.MaximumGripperSpeedMmPerSecond) || hardware.MaximumGripperSpeedMmPerSecond <= 0 ||
            !double.IsFinite(hardware.GripperOpeningMm) || hardware.GripperOpeningMm < 0 || hardware.GripperOpeningMm > hardware.MaximumGripperOpeningMm)
            throw new InvalidOperationException("Pika replay needs a verified 0–90 mm opening range, positive opening-speed limit, and valid measured opening.");
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

    private static void ValidateTargets(IReadOnlyList<EpisodeReplayTarget> samples, EpisodeReplayHardware hardware)
    {
        ValidateJointTargets(samples, hardware.JointLimits);
        for (int row = 0; row < samples.Count; row++)
        {
            double opening = samples[row].GripperOpeningMm;
            if (!double.IsFinite(opening) || opening < 0 || opening > hardware.MaximumGripperOpeningMm)
                throw new InvalidOperationException($"Sample {row}: Pika opening {opening} mm exceeds its verified range. No clipping was performed.");
        }
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
            for (int i = 0; i < 6; i++)
            {
                double angle = sample.JointDegrees[i];
                var limit = limits[i];
                if (!double.IsFinite(angle) || angle < limit.MinimumDegrees || angle > limit.MaximumDegrees)
                    throw new InvalidOperationException($"Sample {row}, J{i + 1}: {angle} degrees is outside the reported {limit.MinimumDegrees}–{limit.MaximumDegrees} degree range. No clipping was performed.");
            }
        }
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

    private static double RequiredTimeScale(IReadOnlyList<EpisodeReplayTarget> samples, EpisodeReplayHardware hardware, EpisodeReplayJointTiming joints)
    {
        double scale = joints.RequiredTimeScale;
        for (int row = 1; row < samples.Count; row++)
        {
            double seconds = samples[row].TimeSeconds - samples[row - 1].TimeSeconds;
            scale = Math.Max(scale, Math.Abs(samples[row].GripperOpeningMm - samples[row - 1].GripperOpeningMm) / seconds / hardware.MaximumGripperSpeedMmPerSecond);
        }
        if (!double.IsFinite(scale))
            throw new InvalidOperationException("Trajectory rates overflow; timing cannot be planned.");
        return scale;
    }

    private static IReadOnlyList<EpisodeReplayStartDifference> ComparePose(IReadOnlyList<double> required, double requiredOpening, IReadOnlyList<double> actual,
        double actualOpening, double jointTolerance, double openingTolerance)
    {
        var differences = new List<EpisodeReplayStartDifference>();
        for (int i = 0; i < 6; i++)
        {
            if (Math.Abs(actual[i] - required[i]) > jointTolerance)
                differences.Add(new("J" + (i + 1), actual[i], required[i], jointTolerance, "deg"));
        }
        if (Math.Abs(actualOpening - requiredOpening) > openingTolerance)
            differences.Add(new("Pika", actualOpening, requiredOpening, openingTolerance, "mm"));
        return differences.AsReadOnly();
    }
}
