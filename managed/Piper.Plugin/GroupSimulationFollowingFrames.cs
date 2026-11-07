using System.Globalization;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One physics cursor contains all participants. A partial or reordered
/// track set is rejected before any participant receives those frames.</summary>
public static class GroupSimulationFrames
{
    public static IReadOnlyDictionary<string, IReadOnlyList<OrderedSimulationFrame>> Read(JsonObject response, IReadOnlyList<string> trackIds)
    {
        if (trackIds.Count is < 1 or > 8 || trackIds.Any(string.IsNullOrWhiteSpace) || trackIds.Distinct(StringComparer.Ordinal).Count() != trackIds.Count ||
            response["frames"] is not JsonArray frames || frames.Count > 128)
            throw new InvalidOperationException("A grouped tape requires one complete, distinct selected track set.");
        var result = trackIds.ToDictionary(id => id, _ => new List<OrderedSimulationFrame>(), StringComparer.Ordinal);
        foreach (var raw in frames)
        {
            if (raw is not JsonObject frame || frame["tracks"] is not JsonArray tracks || tracks.Count != trackIds.Count || tracks.Any(t => t is not JsonObject))
                throw new InvalidOperationException("An atomic source frame omitted a selected robot track.");
            var indexed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var track in tracks.OfType<JsonObject>())
                if (track["id"]?.GetValue<string>() is not { Length: > 0 } id || !result.ContainsKey(id) || !indexed.TryAdd(id, track))
                    throw new InvalidOperationException("An atomic source frame has an unknown or duplicate robot track.");
            foreach (string id in trackIds)
            {
                var projected = (JsonObject)frame.DeepClone(); projected.Remove("tracks");
                foreach (string key in new[] { "commandJoints", "measuredJoints", "commandOpeningMm", "measuredOpeningMm" })
                    projected[key] = indexed[id][key]?.DeepClone();
                result[id].Add(OrderedSimulationFrame.Read(projected));
            }
        }
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<OrderedSimulationFrame>)p.Value.AsReadOnly(), StringComparer.Ordinal);
    }
}

/// <summary>All lanes start a semantic phase together. A faster lane keeps
/// validating and holding its endpoint until every lane reaches that cursor.</summary>
public sealed class GroupSimulationPhaseBarrier
{
    public ulong CompletedCursor { get; private set; }
    private ulong endCursor;
    public bool MayStart(OrderedSimulationExecution execution) => endCursor == 0 || execution.CompletedCursor < endCursor;

    public void Observe(IReadOnlyList<OrderedSimulationExecution> lanes)
    {
        if (lanes.Count == 0) throw new InvalidOperationException("A follower group has no lanes.");
        if (endCursor != 0 && lanes.All(l => l.CompletedCursor == endCursor))
        { CompletedCursor = endCursor; endCursor = 0; }
        if (endCursor != 0) return;
        var phases = lanes.Select(l => l.Path.Current).ToArray();
        if (phases.All(p => p is null)) return;
        if (phases.Any(p => p is null) || phases.Any(p => p!.End.Cursor != phases[0]!.End.Cursor || p.End.Phase != phases[0]!.End.Phase))
            throw new InvalidOperationException("Selected robot tracks disagree on their shared semantic phase boundary.");
        endCursor = phases[0]!.End.Cursor;
    }
}

public static class GroupSimulationLaneTelemetry
{
    public static TelemetryReceipt Project(TelemetryReceipt receipt, RobotProfile profile, long generation)
    {
        var matches = receipt.State["devices"]?.AsArray().OfType<JsonObject>().Where(d => d["id"]?.GetValue<string>() == profile.CanDeviceId).ToArray();
        if (matches is not { Length: 1 } || matches[0]["generation"]?.GetValue<long>() != generation)
            throw new InvalidOperationException(profile.Name + ": the assigned CAN connection changed.");
        var device = matches[0];
        if (device["motion"] is not JsonObject motion || motion["device_id"]?.GetValue<string>() != profile.CanDeviceId ||
            motion["generation"]?.GetValue<long>() != generation)
            throw new InvalidOperationException(profile.Name + ": per-device motion evidence is unavailable; update the backend.");
        var pika = ProfilePikaRouting.Find(receipt.State, profile)
            ?? throw new InvalidOperationException(profile.Name + ": its assigned Pika is unavailable.");
        var state = new JsonObject { ["token"] = receipt.BackendToken, ["selected"] = profile.CanDeviceId,
            ["devices"] = new JsonArray(device.DeepClone()), ["motion"] = motion.DeepClone(), ["pika"] = pika.DeepClone(),
            ["backend_process_id"] = receipt.State["backend_process_id"]?.DeepClone() };
        return receipt with { State = state };
    }
}
