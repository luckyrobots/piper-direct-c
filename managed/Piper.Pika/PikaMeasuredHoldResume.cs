using System.Diagnostics;
using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Pika;

// Pika-F measured-hold resume outcome:
//   (none) | Verified | Failed | Cancelled ──Start──▶ Pending
//   Pending ──fresh enabled same-opening feedback──▶ Verified
//   Pending ──lost feedback, timeout, movement or write failure──▶ Failed (one disable written)
//   Pending ──Cancel──▶ Cancelled
// Active ⇔ Pending and Succeeded ⇔ Verified; neither is stored separately.
[JsonConverter(typeof(JsonStringEnumConverter<PikaMeasuredHoldOutcome>))]
public enum PikaMeasuredHoldOutcome
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled
}

public sealed record PikaMeasuredHoldResumeAudit(string RequestId, double RequestedAtSeconds,
    double RequestFeedbackTimeSeconds, double RequestedRawAngleRad, double ExpectedRawAngleRad, double ExpectedFeedbackTimeSeconds,
    double? VerifiedAtSeconds, double? VerifiedFeedbackTimeSeconds, double? VerifiedRawAngleRad, double? VerifiedDriftMm, PikaMeasuredHoldOutcome Outcome);

/// <summary>Explicit, bounded rearming at the measured raw motor angle. Protocol
/// ordering is tested separately from physical firmware qualification.</summary>
public sealed class PikaMeasuredHoldResume(Action<PikaCommand, double> write)
{
    public const double DeadlineSeconds = 1.5;
    public const double MaximumFeedbackAgeSeconds = .75;
    public PikaMeasuredHoldOutcome? Outcome => Audit?.Outcome;
    public bool Active => Outcome == PikaMeasuredHoldOutcome.Pending;
    public bool Succeeded => Outcome == PikaMeasuredHoldOutcome.Verified;
    public string Message { get; private set; } = "No measured-hold resume requested.";
    public PikaMeasuredHoldResumeAudit? Audit { get; private set; }
    private double target, requestedAt;

    public void Start(double now, double feedbackAt, double measuredAngle, double speed, bool enabled,
        bool pending, bool following, double expectedAngle, double expectedFeedbackAt, string requestId = "")
    {
        if (Active || !double.IsFinite(now) || now < 0 || !double.IsFinite(feedbackAt) || feedbackAt < 0 || feedbackAt > now || now - feedbackAt >= MaximumFeedbackAgeSeconds ||
            !double.IsFinite(expectedFeedbackAt) || expectedFeedbackAt < 0 || feedbackAt < expectedFeedbackAt ||
            !ValidMeasuredAngle(measuredAngle) || !ValidMeasuredAngle(expectedAngle) ||
            Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(expectedAngle)) > .25 ||
            !double.IsFinite(speed) || Math.Abs(speed) > PikaProtocol.StationarySpeedRadiansPerSecond || enabled || pending || following)
            throw new InvalidOperationException("Fresh disabled stationary Pika feedback at the same measured opening is required; no command was sent.");
        target = measuredAngle; requestedAt = now;
        Audit = new(requestId, now, feedbackAt, measuredAngle, expectedAngle, expectedFeedbackAt, null, null, null, null, PikaMeasuredHoldOutcome.Pending);
        try
        {
            write(PikaCommand.Position, target); // Retire a stale servo destination before power.
            write(PikaCommand.Enable, 0);
            write(PikaCommand.Position, target);
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
        if (Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(target)) > 1 || Math.Abs(speed) > PikaProtocol.StationarySpeedRadiansPerSecond)
        { Fail("Opening moved during measured-hold resume; disable requested once."); return; }
        if (!enabled) return;
        Audit = Audit! with { VerifiedAtSeconds = now, VerifiedFeedbackTimeSeconds = feedbackAt, VerifiedRawAngleRad = measuredAngle,
            VerifiedDriftMm = Math.Abs(PikaProtocol.Width(measuredAngle) - PikaProtocol.Width(target)), Outcome = PikaMeasuredHoldOutcome.Verified };
        Message = "Holding the measured jaw opening; fresh enabled feedback confirmed.";
    }

    public void Cancel()
    {
        if (Active) Audit = Audit! with { Outcome = PikaMeasuredHoldOutcome.Cancelled };
        Message = "Measured-hold resume cancelled.";
    }

    // Only a pending attempt can fail, so exactly one disable is written per attempt.
    private void Fail(string reason)
    {
        Debug.Assert(Active, "Only a pending measured-hold resume can fail.");
        Audit = Audit! with { Outcome = PikaMeasuredHoldOutcome.Failed };
        Message = reason;
        write(PikaCommand.Disable, 0);
    }

    // Small closed-reference offsets are accepted only for this unchanged raw
    // measured hold, never as a user-supplied opening destination.
    public static bool ValidMeasuredAngle(double angle) => double.IsFinite(angle) && angle >= -.15 &&
        angle <= PikaProtocol.Angle(PikaProtocol.MaximumOpeningMm) && PikaProtocol.Width(angle) >= -PikaProtocol.ArrivalToleranceMm &&
        PikaProtocol.Width(angle) <= PikaProtocol.MaximumOpeningMm;
}
