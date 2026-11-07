namespace LuckyRobots.Piper.Pika;

public sealed record PikaFollowEvent(long SessionOrdinal, string Kind, double AgentTimeSeconds,
    long LastInputSequence, double LastInputTimeSeconds, double InputAgeSeconds, string Reason);
public sealed record PikaFollowObservation(double AgentTimeSeconds, double PositionRadians,
    double SpeedRadiansPerSecond, bool Enabled, string MotorStatus);
public sealed record PikaFollowRetirement(long SessionOrdinal, string Cause, string Reason, double AgentTimeSeconds,
    long LastInputSequence, double LastInputTimeSeconds, double InputAgeSeconds, double? CommandOpeningMm,
    double? MeasuredAngleRadians, bool? Enabled, IReadOnlyList<PikaFollowEvent> RecentEvents,
    IReadOnlyList<PikaFollowObservation> RecentFeedback);
public sealed record PikaFollowAudit(long SessionOrdinal, bool Active, double InputDeadlineSeconds,
    double? MaximumInputIntervalSeconds, PikaFollowRetirement? LastRetirement,
    IReadOnlyList<PikaFollowEvent> RecentEvents, IReadOnlyList<PikaFollowObservation> RecentFeedback);

public sealed class PikaFollowRejectedException(string message, PikaFollowAudit audit) : InvalidOperationException(
    !audit.Active && audit.LastRetirement is { } last && last.SessionOrdinal == audit.SessionOrdinal && !message.Contains("Previous stream ended:", StringComparison.Ordinal)
        ? message + " Previous stream ended: " + last.Reason : message)
{
    public PikaFollowAudit Audit { get; } = audit;
}
