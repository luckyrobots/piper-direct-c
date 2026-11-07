using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Ordinary position arrival uses the native position/encoder window.
/// The stricter velocity timer remains reserved for power/reset/teach handoffs.</summary>
public static class SimulationEndpointStationary
{
    public static bool IsStationary(TelemetryReceipt receipt, string backend, string deviceId, long generation, long nowTicks)
    {
        if (receipt.BackendToken != backend || receipt.State["selected"]?.GetValue<string>() != deviceId ||
            receipt.State["devices"] is not JsonArray devices) return false;
        var matches = devices.OfType<JsonObject>().Where(d => d["id"]?.GetValue<string>() == deviceId).ToArray();
        if (matches.Length != 1) return false;
        var device = matches[0];
        if (device["generation"]?.GetValue<long>() != generation || device["present"]?.GetValue<bool>() != true ||
            device["up"]?.GetValue<bool>() != true || !receipt.HasFreshDevice(device, nowTicks) ||
            device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true ||
            device["status"]?["arm_status"]?.GetValue<int>() != 0 || device["status"]?["error"]?.GetValue<int>() != 0 ||
            device["status"]?["control"]?.GetValue<int>() != 1 ||
            device["joints"]!.AsArray().Any(j => j?["enabled"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0)) return false;

        var status = device["status"]!.AsObject();
        // Do not OR the timers: an explicitly reset pose window means real
        // movement, even if the legacy velocity timer still reports quiet.
        JsonNode? seconds = status.ContainsKey("pose_stationary_s") ? status["pose_stationary_s"] : status["stationary_s"];
        return seconds is JsonValue value && value.TryGetValue<double>(out double dwell) && double.IsFinite(dwell) && dwell >= .4;
    }
}
