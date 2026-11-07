using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The controller's bounded transmitted reference, distinct from the
/// queued path destination and from measured encoder feedback.</summary>
public sealed record SimulationFollowNativeReference(string DeviceId, long Generation, long RequestId, long Sequence,
    IReadOnlyList<double> JointDegrees)
{
    public static SimulationFollowNativeReference Read(TelemetryReceipt receipt, string expectedBackend, string expectedDevice,
        long expectedGeneration, string expectedOwner, long expectedRequest, long maximumPublishedSequence, long nowTicks)
    {
        var device = ReadDevice(receipt, expectedBackend, expectedDevice, expectedGeneration, nowTicks);
        return FollowNativeMotionEvidence.Capture(receipt.State["motion"], device["joints"] as JsonArray)
            .Read(expectedDevice, expectedGeneration, expectedOwner, expectedRequest, maximumPublishedSequence);
    }

    public static bool AdmissionReady(TelemetryReceipt receipt, string expectedBackend, string expectedDevice,
        long expectedGeneration, string expectedOwner, long expectedRequest, long maximumPublishedSequence,
        long acknowledgementTicks, long acknowledgementSequence, long nowTicks, long minimumNativeSequence = 0)
    {
        var device = ReadDevice(receipt, expectedBackend, expectedDevice, expectedGeneration, nowTicks);
        return FollowNativeMotionEvidence.Capture(receipt.State["motion"], device["joints"] as JsonArray)
            .AdmissionReady(receipt, expectedDevice, expectedGeneration, expectedOwner, expectedRequest, maximumPublishedSequence,
                acknowledgementTicks, acknowledgementSequence, minimumNativeSequence);
    }

    private static JsonObject ReadDevice(TelemetryReceipt receipt, string expectedBackend, string expectedDevice,
        long expectedGeneration, long nowTicks)
    {
        var snapshot = receipt.State;
        var devices = snapshot["devices"] as JsonArray;
        var matching = devices?.OfType<JsonObject>().Where(device => device["id"]?.GetValue<string>() == expectedDevice).ToArray();
        if (expectedBackend.Length == 0 || expectedDevice.Length == 0 || receipt.BackendToken != expectedBackend || snapshot["token"]?.GetValue<string>() != expectedBackend ||
            !receipt.IsFresh(nowTicks) || receipt.AgeMilliseconds(nowTicks) > SimulationFollowPolicy.MaximumSourceAgeMilliseconds ||
            snapshot["selected"]?.GetValue<string>() != expectedDevice || matching is not { Length: 1 } ||
            matching[0]["generation"]?.GetValue<long>() != expectedGeneration || expectedGeneration <= 0 ||
            !receipt.HasFreshDevice(matching[0], nowTicks))
            throw new InvalidOperationException("The native trajectory reference is stale or belongs to a different backend or robot; no queued target was sent.");
        return matching[0];
    }
}

internal sealed record FollowNativeMotionEvidence(bool Active, bool Following, bool Trajectory, string? Owner,
    long? RequestId, long? Sequence, IReadOnlyList<double>? CommandDegrees)
{
    public static FollowNativeMotionEvidence Capture(JsonNode? motion, JsonArray? joints)
    {
        static long? Number(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<long>(out long result) ? result : null;

        IReadOnlyList<double>? commands = null;
        if (joints is { Count: 6 })
        {
            var positions = new double[6];
            bool valid = true;
            for (int joint = 0; joint < positions.Length; joint++)
            {
                if (joints[joint]?["command_deg"] is JsonValue value && value.TryGetValue<double>(out positions[joint]) &&
                    double.IsFinite(positions[joint])) continue;
                valid = false;
                break;
            }
            if (valid) commands = Array.AsReadOnly(positions);
        }
        return new(motion?["active"]?.GetValue<bool>() == true, motion?["following"]?.GetValue<bool>() == true,
            motion?["trajectory_follow"]?.GetValue<bool>() == true, motion?["owner"]?.GetValue<string>(),
            Number(motion?["request_id"]), Number(motion?["follow_sequence"]), commands);
    }

    public SimulationFollowNativeReference Read(string device, long generation, string expectedOwner, long expectedRequest, long maximumPublishedSequence)
    {
        if (expectedOwner.Length == 0 || expectedRequest <= 0 || maximumPublishedSequence < 0 || !Active || !Following ||
            !Trajectory || Owner != expectedOwner || RequestId != expectedRequest || Sequence is not long sequence ||
            sequence < 0 || sequence > maximumPublishedSequence)
            throw new InvalidOperationException("The native trajectory reference is stale or belongs to a different following request; no queued target was sent.");
        if (CommandDegrees is null)
            throw new InvalidOperationException("The native trajectory reference needs six finite commanded joint angles; no queued target was sent.");
        return new(device, generation, expectedRequest, sequence, CommandDegrees);
    }

    public bool AdmissionReady(TelemetryReceipt receipt, string device, long generation, string expectedOwner, long expectedRequest,
        long maximumPublishedSequence, long acknowledgementTicks, long acknowledgementSequence, long minimumNativeSequence = 0)
    {
        if (expectedRequest <= 0 || acknowledgementTicks <= 0 || acknowledgementSequence < 0 || RequestId is not long observed || observed < 0)
            throw new InvalidOperationException("Native following admission has no valid request acknowledgement.");
        // An older request ID is pre-admission evidence, even if its delivery was delayed.
        if (observed < expectedRequest) return false;
        var reference = Read(device, generation, expectedOwner, expectedRequest, maximumPublishedSequence);
        if (minimumNativeSequence < 0 || reference.Sequence < minimumNativeSequence)
            throw new InvalidOperationException("The acknowledged native reference sequence moved backwards.");
        double capturedTicks = receipt.ReceivedTicks - receipt.SourceAgeMilliseconds * Stopwatch.Frequency / 1000;
        return receipt.Sequence > acknowledgementSequence && capturedTicks > acknowledgementTicks;
    }
}
