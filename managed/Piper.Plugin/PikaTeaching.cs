using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record PikaTeachingIdentity(string ProfileId, string CanDeviceId, long Generation,
    string PikaDevice, string Distribution, string BackendSessionSha256)
{
    public static PikaTeachingIdentity Create(string profile, string can, long generation, string pika, string distribution, string backendToken) =>
        new(profile, can, generation, pika, distribution, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(backendToken))).ToLowerInvariant());
}

public sealed record PikaTeachingObservation(PikaTeachingIdentity Identity, long Sequence, long ReceivedTicks,
    long HostReceiptUnixMicroseconds, double[] JointDegrees, double RawPikaAngleRad, double? DisplayOpeningMm,
    double ControllerSourceAgeMs, double[] JointSourceAgesMs, double PikaSourceAgeMs, double PikaFeedbackTimeSeconds, bool PikaMotorEnabled = false);
public sealed record PikaTeachingFrame(double TimeSeconds, long Sequence, long HostReceiptUnixMicroseconds,
    double[] JointDegrees, double RawPikaAngleRad, double? DisplayOpeningMm, double ControllerSourceAgeMs,
    double[] JointSourceAgesMs, double PikaSourceAgeMs, double PikaFeedbackTimeSeconds, bool PikaMotorEnabled);
public sealed record PikaTeachingGap(double TimeSeconds, string Reason);
public sealed record PikaTeachingMotorEvent(double TimeSeconds, string Kind, string MotorRequestId);
public enum PikaTeachingMotorDisposition { Capture, Gap, End }
public sealed record PikaTeachingReport(string Schema, int Version, string SessionId, PikaTeachingIdentity Identity,
    DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string FinishReason, string DisabledMotorRequestId,
    double? ClosedDisplayReferenceAngleRad, string SampleConvention, bool PhysicalReplayAvailable,
    int GapCount, PikaTeachingFrame[] Frames, PikaTeachingGap[] Gaps, PikaTeachingMotorEvent[] MotorEvents);

/// <summary>Bounded, passive observations paired by their host receipt, not a
/// synchronized actuator-command trajectory or a physical replay program.</summary>
public sealed class PikaTeachingRecorder
{
    public const double MaximumDurationSeconds = 180;
    public const int MaximumFrames = 9000;
    public const double MaximumEvidenceAgeMs = 500;
    private readonly List<PikaTeachingFrame> frames = [];
    private readonly List<PikaTeachingGap> gaps = [];
    private readonly List<PikaTeachingMotorEvent> events = [];
    private readonly PikaTeachingIdentity identity;
    private readonly long startedTicks;
    private readonly DateTimeOffset startedUtc;
    private readonly string disabledRequest;
    private readonly double? displayReference;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private long sequence;
    private double lastTime = -1;
    private string? finishReason;
    public bool Complete => finishReason is not null;
    public int Frames => frames.Count;
    public int GapCount { get; private set; }
    public PikaTeachingIdentity Identity => identity;
    public string DisabledMotorRequestId => disabledRequest;
    public double? ClosedDisplayReferenceAngleRad => displayReference;
    public string CurrentMotorRequestId { get; private set; }
    public bool ExpectedMotorEnabled { get; private set; }
    private bool motorModeVerified;

    public PikaTeachingRecorder(PikaTeachingIdentity identity, string disabledMotorRequestId, double? closedDisplayReference,
        long startTicks, DateTimeOffset startUtc)
    {
        if (identity.Generation <= 0 || string.IsNullOrWhiteSpace(identity.ProfileId) || string.IsNullOrWhiteSpace(identity.CanDeviceId) ||
            string.IsNullOrWhiteSpace(identity.PikaDevice) || identity.BackendSessionSha256.Length != 64 ||
            !identity.BackendSessionSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(disabledMotorRequestId) || startTicks < 0 ||
            closedDisplayReference is double reference && (!double.IsFinite(reference) || Math.Abs(reference) > .15))
            throw new ArgumentException("A verified robot/Pika/backend identity, disabled request and display reference are required.");
        this.identity = identity; disabledRequest = disabledMotorRequestId; displayReference = closedDisplayReference;
        CurrentMotorRequestId = disabledMotorRequestId;
        startedTicks = startTicks; startedUtc = startUtc;
    }

    private double Elapsed(long ticks) => Stopwatch.GetElapsedTime(startedTicks, ticks).TotalSeconds;
    public void Finish(string reason) { finishReason ??= reason; }
    public void RequestMotorMode(string motorRequestId, bool enabled, long ticks)
    {
        if (Complete) return;
        if (string.IsNullOrWhiteSpace(motorRequestId) || motorRequestId.Length > 128) throw new ArgumentException("Expected local motor request ID is required");
        CurrentMotorRequestId = motorRequestId; ExpectedMotorEnabled = enabled; motorModeVerified = false;
        if (events.Count >= 512) { Finish("Motor-event limit reached"); return; }
        events.Add(new(Math.Max(0, Elapsed(ticks)), enabled ? "measured-hold-requested" : "free-jaws-requested", motorRequestId));
    }
    public void Gap(long ticks, string reason)
    {
        if (Complete) return;
        double time = Elapsed(ticks);
        if (time >= MaximumDurationSeconds) { Finish("Three-minute observation limit reached"); return; }
        ++GapCount;
        if (gaps.Count < 512 && (gaps.Count == 0 || gaps[^1].Reason != reason || time - gaps[^1].TimeSeconds >= 1))
            gaps.Add(new(Math.Max(0, time), reason));
    }

    public bool Observe(PikaTeachingObservation observation)
    {
        if (Complete) return false;
        if (observation.Identity != identity) { Finish("Robot, Pika or backend identity changed"); return false; }
        if (observation.PikaMotorEnabled != ExpectedMotorEnabled)
        { Gap(observation.ReceivedTicks, "Requested motor mode is not yet verified"); return false; }
        double time = Elapsed(observation.ReceivedTicks);
        if (time >= MaximumDurationSeconds || frames.Count >= MaximumFrames)
        { Finish("Three-minute/frame observation limit reached"); return false; }
        if (observation.Sequence <= sequence || time < 0 || lastTime >= time)
        { Gap(observation.ReceivedTicks, "Duplicate, out-of-order or non-monotonic receipt omitted"); return false; }
        if (observation.HostReceiptUnixMicroseconds <= 0 || observation.JointDegrees.Length != 6 || observation.JointSourceAgesMs.Length != 6 ||
            observation.JointDegrees.Any(v => !double.IsFinite(v) || Math.Abs(v) > 360) ||
            !double.IsFinite(observation.RawPikaAngleRad) || Math.Abs(observation.RawPikaAngleRad) > Math.PI ||
            observation.DisplayOpeningMm is double width && (!double.IsFinite(width) || width is < 0 or > 90) ||
            !double.IsFinite(observation.PikaFeedbackTimeSeconds) || observation.PikaFeedbackTimeSeconds < 0 ||
            new[] { observation.ControllerSourceAgeMs, observation.PikaSourceAgeMs }.Concat(observation.JointSourceAgesMs)
                .Any(v => !double.IsFinite(v) || v < 0 || v > MaximumEvidenceAgeMs))
        { Gap(observation.ReceivedTicks, "Missing, invalid or stale source evidence omitted"); return false; }
        if (lastTime >= 0 && time - lastTime > .25) Gap(observation.ReceivedTicks, "Host observation gap exceeds 250 ms");
        sequence = observation.Sequence; lastTime = time;
        if (!motorModeVerified)
        {
            if (events.Count >= 512) { Finish("Motor-event limit reached"); return false; }
            events.Add(new(time, ExpectedMotorEnabled ? "measured-hold-confirmed" : "free-jaws-confirmed", CurrentMotorRequestId));
            motorModeVerified = true;
        }
        frames.Add(new(time, observation.Sequence, observation.HostReceiptUnixMicroseconds,
            (double[])observation.JointDegrees.Clone(), observation.RawPikaAngleRad, observation.DisplayOpeningMm,
            observation.ControllerSourceAgeMs, (double[])observation.JointSourceAgesMs.Clone(), observation.PikaSourceAgeMs,
            observation.PikaFeedbackTimeSeconds, observation.PikaMotorEnabled));
        return true;
    }

    public PikaTeachingReport Snapshot(DateTimeOffset now) => new("lucky.piper.pika-teaching-observations", 1, sessionId, identity,
        startedUtc, now, finishReason ?? "Recording in progress", disabledRequest, displayReference,
        "Measured six-joint/Pika observations paired at host receipt; per-source ages retained; repeated Pika feedback times are not new Pika samples; no hardware clock synchronization or commanded actions inferred.",
        false, GapCount, frames.Select(f => f with { JointDegrees = (double[])f.JointDegrees.Clone(), JointSourceAgesMs = (double[])f.JointSourceAgesMs.Clone() }).ToArray(), gaps.ToArray(), events.ToArray());

    public static bool DisabledAfterRequest(JsonObject? pika, string owner, string request) =>
        MotorModeAfterRequest(pika, owner, request, false);

    public static PikaTeachingMotorDisposition AssessMotor(JsonObject? pika, string owner, string request, bool enabled, bool localTransitionPending)
    {
        if (pika?["owner"]?.GetValue<string>() != owner || pika["pending"]?.GetValue<bool>() == true || pika["following"]?.GetValue<bool>() == true)
            return PikaTeachingMotorDisposition.End;
        // The bounded local API call may be acknowledged after the telemetry
        // reader has already received its motor request. Never capture that
        // ambiguous interval, and never permanently whitelist an unknown ID.
        if (localTransitionPending) return PikaTeachingMotorDisposition.Gap;
        if (pika["last_motor_request"]?.GetValue<string>() != request) return PikaTeachingMotorDisposition.End;
        return MotorModeAfterRequest(pika, owner, request, enabled) ? PikaTeachingMotorDisposition.Capture : PikaTeachingMotorDisposition.Gap;
    }

    public static bool MotorModeAfterRequest(JsonObject? pika, string owner, string request, bool enabled) =>
        request.Length > 0 && pika?["owner"]?.GetValue<string>() == owner && pika["connected"]?.GetValue<bool>() == true &&
        pika["fresh"]?.GetValue<bool>() == true && pika["enabled"]?.GetValue<bool>() == enabled &&
        pika["armed"]?.GetValue<bool>() == enabled && pika["pending"]?.GetValue<bool>() == false && pika["following"]?.GetValue<bool>() != true &&
        (enabled || pika["power_uncertain"]?.GetValue<bool>() != true) && pika["last_motor_request"]?.GetValue<string>() == request &&
        Number(pika["request_time_s"], out double requested) && requested >= 0 &&
        Number(pika["feedback_time_s"], out double feedback) && feedback > requested &&
        Number(pika["age_s"], out double age) && age >= 0 && age * 1000 <= MaximumEvidenceAgeMs;

    public static PikaTeachingObservation Read(TelemetryReceipt receipt, JsonObject device, JsonObject pika,
        PikaTeachingIdentity identity, long hostReceiptUnixMicroseconds, double? displayReference)
    {
        double Age(JsonNode? source) => Number(source?["age_s"], out double age) && age >= 0
            ? receipt.SourceAgeMilliseconds + age * 1000 : double.PositiveInfinity;
        if (!receipt.Connected || device["fresh"]?.GetValue<bool>() != true || pika["connected"]?.GetValue<bool>() != true ||
            pika["fresh"]?.GetValue<bool>() != true || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || !Number(j["actual_deg"], out _)) ||
            !Number(pika["telemetry"]?["motor"]?["Position"], out double raw) || !Number(pika["feedback_time_s"], out double pikaTime))
            throw new InvalidOperationException("Fresh six-joint and Pika observations are required");
        double? width = GripperLink.TryMeasuredOpening(pika, out double opening, displayReference) && opening <= 90 ? opening : null;
        return new(identity, receipt.Sequence, receipt.ReceivedTicks, hostReceiptUnixMicroseconds,
            joints.Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray(), raw, width,
            Age(device["status"]), joints.Select(Age).ToArray(), Age(pika), pikaTime, pika["enabled"]?.GetValue<bool>() == true);
    }

    private static bool Number(JsonNode? node, out double value)
    {
        value = double.NaN;
        return node is JsonValue item && item.TryGetValue(out value) && double.IsFinite(value);
    }
}
