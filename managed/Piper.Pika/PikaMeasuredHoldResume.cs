namespace LuckyRobots.Piper.Pika;

public sealed record PikaMeasuredHoldResumeAudit(string RequestId, double RequestedAtSeconds,
    double RequestFeedbackTimeSeconds, double RequestedRawAngleRad, double ExpectedRawAngleRad, double ExpectedFeedbackTimeSeconds,
    double? VerifiedAtSeconds, double? VerifiedFeedbackTimeSeconds, double? VerifiedRawAngleRad, double? VerifiedDriftMm, string Outcome);

/// <summary>Explicit, bounded rearming at the measured raw motor angle. Protocol
/// ordering is tested separately from physical firmware qualification.</summary>
public sealed class PikaMeasuredHoldResume(Action<int, double> write)
{
    public const double DeadlineSeconds = 1.5;
    public const double MaximumFeedbackAgeSeconds = .75;
    public bool Active { get; private set; }
    public bool Succeeded { get; private set; }
    public string Message { get; private set; } = "No measured-hold resume requested.";
    public PikaMeasuredHoldResumeAudit? Audit { get; private set; }
    private double target, requestedAt;
    private bool disabledAfterFailure;

    public void Start(double now, double feedbackAt, double measuredAngle, double speed, bool enabled,
        bool pending, bool following, double expectedAngle, double expectedFeedbackAt, string requestId = "")
    {
        if (Active || !double.IsFinite(now) || now < 0 || !double.IsFinite(feedbackAt) || feedbackAt < 0 || feedbackAt > now || now - feedbackAt >= MaximumFeedbackAgeSeconds ||
            !double.IsFinite(expectedFeedbackAt) || expectedFeedbackAt < 0 || feedbackAt < expectedFeedbackAt ||
            !ValidMeasuredAngle(measuredAngle) || !ValidMeasuredAngle(expectedAngle) ||
            Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(expectedAngle)) > .25 ||
            !double.IsFinite(speed) || Math.Abs(speed) > .02 || enabled || pending || following)
            throw new InvalidOperationException("Fresh disabled stationary Pika feedback at the same measured opening is required; no command was sent.");
        target = measuredAngle; requestedAt = now; disabledAfterFailure = false; Succeeded = false; Active = true;
        Audit = new(requestId, now, feedbackAt, measuredAngle, expectedAngle, expectedFeedbackAt, null, null, null, null, "pending");
        try
        {
            write(22, target); // Retire a stale servo destination before power.
            write(11, 0);
            write(22, target);
            Message = "Measured target seeded; waiting for fresh enabled same-opening feedback.";
        }
        catch { Fail("Measured-hold resume write failed; disable requested once."); throw; }
    }

    public void Tick(double now, double feedbackAt, double measuredAngle, double speed, bool enabled, bool fresh)
    {
        if (!Active) return;
        if (!double.IsFinite(now) || now < requestedAt || !double.IsFinite(feedbackAt) || feedbackAt < 0 || feedbackAt > now || !fresh ||
            !ValidMeasuredAngle(measuredAngle) || !double.IsFinite(speed) || now - feedbackAt >= MaximumFeedbackAgeSeconds)
        { Fail("Fresh feedback was lost during measured-hold resume; disable requested once."); return; }
        if (now - requestedAt >= DeadlineSeconds)
        { Fail("Enabled same-opening feedback timed out; disable requested once."); return; }
        if (feedbackAt <= requestedAt) return; // Buffered feedback is not an acknowledgement.
        if (Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(target)) > 1 || Math.Abs(speed) > .02)
        { Fail("Opening moved during measured-hold resume; disable requested once."); return; }
        if (!enabled) return;
        Active = false; Succeeded = true;
        Audit = Audit! with { VerifiedAtSeconds = now, VerifiedFeedbackTimeSeconds = feedbackAt, VerifiedRawAngleRad = measuredAngle,
            VerifiedDriftMm = Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(target)), Outcome = "verified" };
        Message = "Holding the measured jaw opening; fresh enabled feedback confirmed.";
    }

    public void Cancel()
    {
        if (Active && Audit is not null) Audit = Audit with { Outcome = "cancelled" };
        Active = Succeeded = false; Message = "Measured-hold resume cancelled.";
    }

    private void Fail(string reason)
    {
        Active = Succeeded = false; Message = reason;
        if (Audit is not null) Audit = Audit with { Outcome = "failed" };
        if (!disabledAfterFailure) { disabledAfterFailure = true; write(10, 0); }
    }

    // Small closed-reference offsets are accepted only for this unchanged raw
    // measured hold, never as a user-supplied opening destination.
    public static bool ValidMeasuredAngle(double angle) => double.IsFinite(angle) && angle >= -.15 &&
        angle <= PikaProtocol.Angle(90) && PikaProtocol.Width(angle) >= -PikaProtocol.ArrivalToleranceMm && PikaProtocol.Width(angle) <= 90;
}
