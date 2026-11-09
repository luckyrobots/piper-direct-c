using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Observations copy the values used by a follower. Reusing a receipt
/// never grants a new freshness window or replaces a current scope check.</summary>
internal sealed class FollowLaneTelemetry(RobotProfile profile, long generation, double? closedReference,
    Func<TelemetryReceipt?> readLatest, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly ConditionalWeakTable<TelemetryReceipt, FollowLaneObservation> observations = new();

    public FollowLaneObservation Read(string backend, TelemetryReceipt? source = null)
    {
        var receipt = source ?? readLatest() ?? throw new InvalidOperationException("Controller feedback is unavailable.");
        // Production uses TimeProvider.System. Fixtures use the same timestamp
        // scale as the telemetry receipt's monotonic clock.
        if (receipt.BackendToken != backend || !receipt.IsFresh(clock.GetTimestamp()))
            throw new InvalidOperationException("Controller feedback is stale or belongs to another session.");
        lock (gate) return observations.GetValue(receipt, current => new(current, profile, generation, closedReference, clock.TimestampFrequency));
    }
}

internal sealed class FollowLaneObservation
{
    // Only receipt timing metadata is used after construction.
    private readonly TelemetryReceipt receipt;
    private readonly RobotProfile profile;
    private readonly long generation, timestampFrequency;
    private readonly bool armSamplesFresh, armHealthy, armReady, armSettled;
    private readonly bool pikaSamplesFresh, pikaReady;
    private readonly double armSampleAge, pikaSampleAge;
    private readonly string? pikaOwner;
    private readonly double? pikaSpeed, pikaFeedbackTime;
    private readonly SimulationFollowHardware? hardware;

    public FollowNativeMotionEvidence Motion { get; }
    public long Sequence => receipt.Sequence;
    public bool PikaIdle { get; }
    public double PikaSpeed => pikaSpeed ?? throw new InvalidOperationException("Pika did not provide a finite telemetry.motor.Speed.");
    public double PikaFeedbackTime => pikaFeedbackTime ?? throw new InvalidOperationException("Pika did not provide a finite feedback_time_s.");

    public FollowLaneObservation(TelemetryReceipt receipt, RobotProfile profile, long generation, double? closedReference,
        long timestampFrequency)
    {
        this.receipt = receipt;
        this.timestampFrequency = timestampFrequency;
        this.profile = profile;
        this.generation = generation;
        var (device, motion, pika) = GroupSimulationLaneTelemetry.Resolve(receipt, profile, generation);
        var joints = device["joints"] as JsonArray;
        var status = device["status"] as JsonObject;
        Motion = FollowNativeMotionEvidence.Capture(motion, joints);
        armSamplesFresh = device["fresh"]?.GetValue<bool>() == true && joints is { Count: 6 } &&
            joints.All(j => j?["fresh"]?.GetValue<bool>() == true);
        armSampleAge = joints is { Count: 6 } ? Math.Max(TelemetryReceipt.SampleAgeMilliseconds(status),
            joints.Max(TelemetryReceipt.SampleAgeMilliseconds)) : double.PositiveInfinity;
        armHealthy = device["present"]?.GetValue<bool>() == true && device["up"]?.GetValue<bool>() == true &&
            device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true &&
            status?["control"]?.GetValue<int>() == 1 && status["arm_status"]?.GetValue<int>() == 0 && status["error"]?.GetValue<int>() == 0 &&
            joints is { Count: 6 } && joints.All(j => j?["enabled"]?.GetValue<bool>() == true && j["fault"]?.GetValue<int>() == 0);
        armReady = armHealthy && device["firmware_supported"]?.GetValue<bool>() == true && !TeachingState.Inspect(device).BlocksTargets &&
            joints!.All(j => j?["limits_fresh"]?.GetValue<bool>() == true && j["acceleration_fresh"]?.GetValue<bool>() == true);
        armSettled = status is not null && SimulationEndpointStationary.PoseDwellSatisfied(status);

        pikaSamplesFresh = pika["connected"]?.GetValue<bool>() == true && pika["fresh"]?.GetValue<bool>() == true;
        pikaSampleAge = TelemetryReceipt.SampleAgeMilliseconds(pika);
        pikaOwner = pika["owner"]?.GetValue<string>();
        pikaReady = GripperLink.TryMeasuredOpening(pika, out double width, closedReference) &&
            pika["enabled"]?.GetValue<bool>() == true && pika["armed"]?.GetValue<bool>() == true && pika["driver_fault"]?.GetValue<bool>() != true;
        PikaIdle = pika["pending"]?.GetValue<bool>() == false && pika["following"]?.GetValue<bool>() != true &&
            pika["resuming_hold"]?.GetValue<bool>() != true;
        pikaSpeed = ReadNumber(pika["telemetry"]?["motor"]?["Speed"]) is double speed && double.IsFinite(speed) ? speed : null;
        pikaFeedbackTime = ReadNumber(pika["feedback_time_s"]) is double time && double.IsFinite(time) ? time : null;

        if (!armReady || !pikaReady) return;
        var positions = new double[6];
        var limits = new EpisodeReplayJointLimit[6];
        for (int i = 0; i < positions.Length; i++)
        {
            var joint = joints![i]!;
            if (ReadNumber(joint["actual_deg"]) is not double position || ReadNumber(joint["min_deg"]) is not double minimum ||
                ReadNumber(joint["max_deg"]) is not double maximum || ReadNumber(joint["max_speed_rad_s"]) is not double velocity ||
                ReadNumber(joint["max_acceleration_rad_s2"]) is not double acceleration) return;
            positions[i] = position;
            limits[i] = new(i + 1, minimum, maximum, velocity * 180 / Math.PI, acceleration * 180 / Math.PI, true);
        }
        hardware = new(profile.CanDeviceId, generation, true, true, true, Array.AsReadOnly(positions), Array.AsReadOnly(limits), width);
    }

    private static double? ReadNumber(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out double number) ? number : null;

    private bool HasFreshArm(long now) => receipt.IsFresh(now) && armSamplesFresh && receipt.AgeMilliseconds(now) + armSampleAge <= TelemetryReceipt.MaximumAgeMilliseconds;

    public bool ArmStationary(long now) => armHealthy && armSettled && HasFreshArm(now);

    public SimulationFollowHardware Hardware(string owner, long now, long? nativeRequest = null, long publishedSequence = 0)
    {
        if (!HasFreshArm(now) || !armReady)
            throw new InvalidOperationException(profile.Name + ": fresh healthy CAN control, six enabled motors and reported limits are required.");
        if (Motion.Active && Motion.Owner != owner)
            throw new InvalidOperationException(profile.Name + ": another controller owns its active motion.");
        if (!receipt.IsFresh(now) || !pikaSamplesFresh || receipt.AgeMilliseconds(now) + pikaSampleAge > 250 ||
            pikaOwner != owner || !pikaReady)
            throw new InvalidOperationException(profile.Name + ": its enabled Pika needs fresh owned, fault-free feedback (250 ms limit).");
        var result = hardware ?? throw new InvalidOperationException(profile.Name + ": the controller did not provide measured joints and numeric limits.");
        if (nativeRequest is not long request) return result;
        RequireNativeScope(now);
        return result with { NativeReference = Motion.Read(profile.CanDeviceId, generation, owner, request, publishedSequence) };
    }

    public bool AdmissionReady(string owner, long request, long publishedSequence, long acknowledgementTicks, long acknowledgementSequence, long now)
    {
        RequireNativeScope(now);
        return Motion.AdmissionReady(receipt, profile.CanDeviceId, generation, owner, request, publishedSequence, acknowledgementTicks, acknowledgementSequence, timestampFrequency);
    }

    private void RequireNativeScope(long now)
    {
        if (receipt.BackendToken.Length == 0 || profile.CanDeviceId.Length == 0 || generation <= 0 || !HasFreshArm(now) ||
            receipt.AgeMilliseconds(now) > SimulationFollowPolicy.MaximumSourceAgeMilliseconds)
            throw new InvalidOperationException("The native trajectory reference is stale or belongs to a different backend or robot; no queued target was sent.");
    }
}
