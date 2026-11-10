namespace LuckyRobots.Piper.Pika.Agent;

/// <summary>Display acknowledgement only; it does not grant ownership or motor authority.</summary>
internal readonly record struct PikaPowerFeedback(bool ExpectedEnabled, double RequestedAt)
{
    public bool IsConfirmed(bool enabled, double feedbackAt) => enabled == ExpectedEnabled &&
        double.IsFinite(RequestedAt) && RequestedAt >= 0 && double.IsFinite(feedbackAt) && feedbackAt > RequestedAt;
}
