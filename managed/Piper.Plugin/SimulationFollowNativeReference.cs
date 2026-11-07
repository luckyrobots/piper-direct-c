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
        var snapshot = receipt.State;
        var motion = snapshot["motion"];
        var device = ReadDevice(receipt, expectedBackend, expectedDevice, expectedGeneration, nowTicks);
        if (expectedOwner.Length == 0 || expectedRequest <= 0 || maximumPublishedSequence < 0 ||
            motion?["active"]?.GetValue<bool>() != true || motion["following"]?.GetValue<bool>() != true ||
            motion["trajectory_follow"]?.GetValue<bool>() != true || motion["owner"]?.GetValue<string>() != expectedOwner ||
            motion["request_id"]?.GetValue<long>() != expectedRequest ||
            motion["follow_sequence"] is not JsonValue sequenceValue || !sequenceValue.TryGetValue<long>(out long sequence) ||
            sequence < 0 || sequence > maximumPublishedSequence || device["joints"] is not JsonArray { Count: 6 } joints)
            throw new InvalidOperationException("The native trajectory reference is stale or belongs to a different following request; no queued target was sent.");
        var positions = new double[6];
        for (int joint = 0; joint < positions.Length; joint++)
        {
            if (joints[joint]?["command_deg"] is not JsonValue command || !command.TryGetValue<double>(out positions[joint]) || !double.IsFinite(positions[joint]))
                throw new InvalidOperationException("The native trajectory reference needs six finite commanded joint angles; no queued target was sent.");
        }
        return new(expectedDevice, expectedGeneration, expectedRequest, sequence, Array.AsReadOnly(positions));
    }

    public static bool AdmissionReady(TelemetryReceipt receipt, string expectedBackend, string expectedDevice,
        long expectedGeneration, string expectedOwner, long expectedRequest, long maximumPublishedSequence,
        long acknowledgementTicks, long acknowledgementSequence, long nowTicks, long minimumNativeSequence = 0)
    {
        _ = ReadDevice(receipt, expectedBackend, expectedDevice, expectedGeneration, nowTicks);
        if (expectedRequest <= 0 || acknowledgementTicks <= 0 || acknowledgementSequence < 0 ||
            receipt.State["motion"]?["request_id"] is not JsonValue value || !value.TryGetValue<long>(out long observedRequest) || observedRequest < 0)
            throw new InvalidOperationException("Native following admission has no valid request acknowledgement.");
        // Native request IDs increase at begin(), so an older ID is cached
        // pre-admission state, even if its transport delivery was delayed.
        // A different backend/device/generation already failed ReadDevice.
        if (observedRequest < expectedRequest) return false;
        var reference = Read(receipt, expectedBackend, expectedDevice, expectedGeneration, expectedOwner, expectedRequest, maximumPublishedSequence, nowTicks);
        if (minimumNativeSequence < 0 || reference.Sequence < minimumNativeSequence)
            throw new InvalidOperationException("The acknowledged native reference sequence moved backwards.");
        double capturedTicks = receipt.ReceivedTicks - receipt.SourceAgeMilliseconds * Stopwatch.Frequency / 1000;
        return receipt.Sequence > acknowledgementSequence && capturedTicks > acknowledgementTicks;
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
