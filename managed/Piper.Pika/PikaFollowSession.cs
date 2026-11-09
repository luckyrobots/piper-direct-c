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

// Pika-G follow session phase (agent control thread only):
//   Idle | Holding | HoldFailed ──Start──▶ Following
//   Following ──Update / paced Tick write──▶ Following
//   Following ──Tick input expiry──▶ Hold(measured)
//   any ──Hold: measured position written──▶ Holding
//   any ──Hold: driver fault, no fresh measured angle, or write failure──▶ HoldFailed
//   any ──Cancel, Tick feedback loss or write failure──▶ Idle
// SessionId and OpeningMm are set only while Following.
public enum PikaFollowPhase { Idle, Following, Holding, HoldFailed }

/// <summary>A single replaceable opening target. This bounds command traffic, not physical jaw speed.
/// All methods are called by the serial agent's one control thread.</summary>
public sealed class PikaFollowSession
{
    public const double InputDeadlineSeconds = .250;
    public const double MinimumCommandPeriodSeconds = .030;
    public double ConfiguredCommandPeriodSeconds { get; }
    private readonly Action<double> writeAngle;
    public PikaFollowSession(Action<double> writeAngle, double commandPeriodSeconds = MinimumCommandPeriodSeconds)
    {
        ArgumentNullException.ThrowIfNull(writeAngle);
        if (!double.IsFinite(commandPeriodSeconds) || commandPeriodSeconds is < MinimumCommandPeriodSeconds or > InputDeadlineSeconds)
            throw new ArgumentOutOfRangeException(nameof(commandPeriodSeconds), "Command period must be 30–250 ms; it is not a physical speed limit.");
        this.writeAngle = writeAngle; ConfiguredCommandPeriodSeconds = commandPeriodSeconds;
    }
    public PikaFollowPhase Phase { get; private set; }
    public string? SessionId { get; private set; }
    public long Sequence { get; private set; }
    public double? OpeningMm { get; private set; }
    public bool Active => Phase == PikaFollowPhase.Following;
    public bool Holding => Phase == PikaFollowPhase.Holding;
    public string Message { get; private set; } = "No following session.";
    private double lastInput, lastWrite = double.NegativeInfinity, lastTime = double.NegativeInfinity;
    private long sessionOrdinal;
    private double maximumInputInterval;
    private readonly Queue<PikaFollowEvent> events = new();
    private readonly Queue<PikaFollowObservation> feedback = new();
    public PikaFollowRetirement? LastRetirement { get; private set; }
    public PikaFollowAudit Audit => new(sessionOrdinal, Active, InputDeadlineSeconds,
        sessionOrdinal > 0 ? maximumInputInterval : null, LastRetirement,
        Array.AsReadOnly(events.ToArray()), Array.AsReadOnly(feedback.ToArray()));

    public void ObserveFeedback(double now, double angle, double speed, bool enabled, string motorStatus)
    {
        // Diagnostic receive evidence only. It never filters or changes what
        // Tick accepts, and is not a firmware timestamp or device sequence.
        if (!double.IsFinite(now) || !double.IsFinite(angle) || !double.IsFinite(speed)) return;
        feedback.Enqueue(new(now, angle, speed, enabled, motorStatus.Length <= 16 && motorStatus.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? motorStatus : "invalid"));
        while (feedback.Count > 4) feedback.Dequeue();
    }

    private void Event(string kind, double now, string reason = "")
    {
        events.Enqueue(new(sessionOrdinal, kind, now, Sequence, lastInput, Math.Max(0, now - lastInput), reason));
        while (events.Count > 16) events.Dequeue();
    }

    // The single exit from Following: every other phase change goes through here
    // to Idle, and Hold then moves on to Holding or HoldFailed.
    private void Retire(string cause, string reason, double now, double? angle = null, bool? enabled = null)
    {
        // Cleanup/Stop may call Hold repeatedly. Only an actually active stream
        // can create its terminal record, so its first cause survives them.
        Event(cause, now, reason);
        if (Active)
            LastRetirement = new(sessionOrdinal, cause, reason, now, Sequence, lastInput, Math.Max(0, now - lastInput), OpeningMm, angle, enabled,
                Array.AsReadOnly(events.ToArray()), Array.AsReadOnly(feedback.ToArray()));
        Phase = PikaFollowPhase.Idle; SessionId = null; OpeningMm = null; Message = reason;
    }

    public string Start(double now)
    {
        CheckTime(now);
        if (Active) throw new InvalidOperationException("Hold the active Pika session before starting another.");
        Phase = PikaFollowPhase.Following;
        SessionId = Guid.NewGuid().ToString("D");
        sessionOrdinal++;
        Sequence = 0; OpeningMm = null;
        lastInput = now; lastWrite = double.NegativeInfinity;
        maximumInputInterval = 0;
        Event("start", now);
        Message = "Following session started; no position command sent.";
        return SessionId;
    }

    public void Update(string sessionId, long sequence, double openingMm, double now)
    {
        CheckTime(now);
        if (!Active || sessionId != SessionId) { Event("rejected-session", now); throw new InvalidOperationException("No matching active Pika following session."); }
        if (now - lastInput >= InputDeadlineSeconds) { Event("rejected-expired-input", now); throw new InvalidOperationException("Pika following input expired; hold and start a new session."); }
        if (sequence <= 0 || sequence <= Sequence) throw new InvalidOperationException("Discarded an old Pika following target.");
        if (!PikaProtocol.ValidOpening(openingMm))
            throw new ArgumentOutOfRangeException(nameof(openingMm), "Opening target must be within 0–90 mm.");
        // There is no target queue. A faster producer replaces this one value.
        maximumInputInterval = Math.Max(maximumInputInterval, now - lastInput);
        OpeningMm = openingMm; Sequence = sequence; lastInput = now;
        Event("input", now);
        Message = "Latest Pika opening target accepted.";
    }

    /// <summary>Requires fresh enabled feedback; a missed lease holds once at that measurement.
    /// A write failure retires the session before it can accept another target.</summary>
    public void Tick(double now, double? measuredAngle, bool enabled, PikaDriverStatus? driverStatus = null)
    {
        CheckTime(now);
        if (!Active) return;
        if (driverStatus?.HasFault == true || !enabled || measuredAngle is null || !PikaProtocol.ValidAngle(measuredAngle.Value))
        {
            string reason = driverStatus?.HasFault == true ? driverStatus.Text + "; following ended. Hold was not verified."
                : !enabled ? "Pika reported disabled motor feedback; following ended. Hold was not verified."
                : measuredAngle is null ? "Pika feedback expired after 250 ms; following ended. Hold was not verified."
                : "Pika reported an out-of-range jaw angle; following ended. Hold was not verified.";
            Retire(driverStatus?.HasFault == true ? "driver-fault" : "feedback-unavailable", reason, now, measuredAngle, enabled);
            throw new InvalidOperationException(Message);
        }
        if (now - lastInput >= InputDeadlineSeconds)
        {
            Hold(measuredAngle, "Pika input expired after 250 ms; holding the measured opening. Start a new session to resume.", now, "input-expired", enabled);
            return;
        }
        if (OpeningMm is not { } opening || now - lastWrite < ConfiguredCommandPeriodSeconds) return;
        try { writeAngle(PikaProtocol.Angle(opening)); lastWrite = now; }
        catch { Retire("write-failed", "Pika position write failed; following ended without automatic resume.", now, measuredAngle, enabled); throw; }
    }

    /// <summary>Cancels pending targets BEFORE attempting one measured-position write. It does not
    /// disable the motor. Command acceptance is not independent proof of holding torque.</summary>
    public void Hold(double? measuredAngle, string reason = "Holding the measured Pika opening; motor enable is unchanged.",
        double? now = null, string cause = "operator-hold", bool? enabled = null, PikaDriverStatus? driverStatus = null)
    {
        if (now.HasValue) CheckTime(now.Value);
        double at = now ?? (double.IsFinite(lastTime) ? lastTime : 0);
        bool faulted = driverStatus?.HasFault == true;
        Retire(faulted ? "driver-fault" : cause, faulted ? driverStatus!.Text + "; measured holding was not verified." : reason, at, measuredAngle, enabled);
        Phase = PikaFollowPhase.HoldFailed;
        Message = "Pika following cancelled; hold has not been written.";
        if (faulted) throw new InvalidOperationException(driverStatus!.Text + "; no hold target was sent, and pending targets were discarded.");
        if (measuredAngle is null || !PikaProtocol.ValidAngle(measuredAngle.Value))
            throw new InvalidOperationException("Fresh enabled Pika feedback is required to hold; pending targets were cancelled.");
        try { writeAngle(measuredAngle.Value); }
        catch { Event("hold-write-failed", at); throw; }
        Phase = PikaFollowPhase.Holding; Message = reason;
    }

    public void Cancel(string reason, double? now = null)
    {
        if (now.HasValue) CheckTime(now.Value);
        Retire("cancel", reason, now ?? (double.IsFinite(lastTime) ? lastTime : 0));
    }

    private void CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < 0 || now < lastTime)
            throw new ArgumentOutOfRangeException(nameof(now), "A monotonic clock is required.");
        lastTime = now;
    }
}
