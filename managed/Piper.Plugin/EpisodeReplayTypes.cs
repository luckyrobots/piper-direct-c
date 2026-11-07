namespace LuckyRobots.Piper.Plugin;

public sealed record EpisodeReplayPosition(double X, double Y, double Z);
public sealed record EpisodeReplayDesk(double WidthMm, double DepthMm, double SurfaceYMetres, string Frame);
public sealed record EpisodeReplayTubeStart(double LeftMm, double BackMm, double YawDeg, EpisodeReplayPosition WorldPositionMetres,
    IReadOnlyList<double> WorldQuaternionXyzw, string Source);
public sealed record EpisodeReplayTubeDimensions(double DiameterMm, double HeightMm, double MassKg, string MassSource);
public sealed record EpisodeReplayRobot(string Model, string JointConvention, IReadOnlyList<string> JointNames,
    string GripperConvention, string GripperMapping, double GripperMaximumOpeningMm, string ActionSource,
    double? GripperReferenceGapMm = null, string? CommandConvention = null, EpisodeReplayActuatorProvenance? PositionActuatorProvenance = null);
public sealed record EpisodeReplayActuatorProvenance(string SourceSha256, string Validation, string RuntimeChecks, string RuntimeTuning);
public sealed record EpisodeReplayNativeRecording(bool Requested, string SessionLabel, int? EpisodeOrdinal,
    string SessionLabelSource, int TaskIndex, string TaskSessionId, string NativeOrdinalSource);
public sealed record EpisodeReplayGoal(double LeftMm, double BackMm, double PlacementToleranceMm);
/// <summary>Native qpos is retained as audit evidence only; the planner never interprets it as hardware targets.</summary>
public sealed record EpisodeReplayInitialState(IReadOnlyList<double> MujocoQposForAudit, string QposConvention, string WorldEntityId, ulong SyncIndex);
public sealed record EpisodeReplayAudit(DateTimeOffset CreatedUtc, int Seed, int EpisodeOrdinal, EpisodeReplayGoal Goal,
    EpisodeReplayInitialState InitialSimulationState, string Outcome, string OutcomeSource);
public sealed record EpisodeReplaySample(double TimeSeconds, IReadOnlyList<double> JointDegrees, double GripperOpeningMm,
    ulong SyncIndex, string Source, IReadOnlyList<double>? CommandedJointDegrees = null, double? CommandedGripperOpeningMm = null, string? CommandSource = null);
public sealed record EpisodeReplayArtifact(string ArtifactId, string SessionId, EpisodeReplayRobot Robot, EpisodeReplayDesk Desk,
    EpisodeReplayTubeStart TubeStart, EpisodeReplayTubeDimensions TubeDimensions, EpisodeReplayNativeRecording NativeRecording,
    IReadOnlyList<EpisodeReplaySample> Samples, EpisodeReplayAudit Audit, int Version = 1, string? CommandTimestampConvention = null,
    double? NominalCaptureHz = null)
{
    public bool HasPositionCommands => Version == 2;
}

/// <summary>Reported physical limits, ordered J1 through J6; no fallback limits are assumed.</summary>
public sealed record EpisodeReplayJointLimit(int JointNumber, double MinimumDegrees, double MaximumDegrees, double MaximumDegreesPerSecond,
    double MaximumDegreesPerSecondSquared, bool AccelerationFresh);
public sealed record EpisodeReplayJointTiming(double SpeedTimeScale, double AccelerationTimeScale)
{
    public double RequiredTimeScale => Math.Max(SpeedTimeScale, AccelerationTimeScale);
}
public sealed record EpisodeReplayHardware(string DeviceId, long Generation, string RobotModel, string JointConvention,
    string GripperConvention, bool FeedbackFresh, bool LimitsFresh, bool GripperFresh, bool GripperLimitsVerified,
    IReadOnlyList<EpisodeReplayJointLimit> JointLimits, IReadOnlyList<double> JointDegrees,
    double GripperOpeningMm, double MaximumGripperOpeningMm, double MaximumGripperSpeedMmPerSecond);
public sealed record EpisodeReplayOptions(bool AllowTimeScaling = false, double MaximumTimeScale = 100,
    double StartJointToleranceDegrees = .5, double StartGripperToleranceMm = 1,
    double InitialCommandJointToleranceDegrees = .5, double InitialCommandGripperToleranceMm = 1);
public sealed record EpisodeReplayStartDifference(string Axis, double Actual, double Required, double Tolerance, string Unit)
{
    public double Difference => Actual - Required;
}
public sealed record EpisodeReplayPlan(EpisodeReplayArtifact Artifact, string DeviceId, long Generation,
    IReadOnlyList<EpisodeReplayTarget> Samples, double TimeScale, IReadOnlyList<EpisodeReplayStartDifference> StartDifferences,
    IReadOnlyList<EpisodeReplayStartDifference> InitialCommandDifferences, EpisodeReplayOptions Options, IReadOnlyList<string> Notes)
{
    /// <summary>Position-plan checks only. An executor must independently verify live controller mode, faults and ownership.</summary>
    public bool ReadyToStart => Artifact.HasPositionCommands && StartDifferences.Count == 0 && InitialCommandDifferences.Count == 0;
    public bool TimingChanged => TimeScale != 1;
    public double DurationSeconds => Samples[^1].TimeSeconds;
}
public sealed record EpisodeReplayTarget(double TimeSeconds, IReadOnlyList<double> JointDegrees, double GripperOpeningMm,
    ulong SyncIndex, string Source);
