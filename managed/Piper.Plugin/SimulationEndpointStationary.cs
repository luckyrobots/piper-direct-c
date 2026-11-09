using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Ordinary position arrival uses the native position/encoder window.
/// The stricter velocity timer remains reserved for power/reset/teach handoffs.</summary>
public static class SimulationEndpointStationary
{
    internal static bool PoseDwellSatisfied(JsonObject status)
    {
        // Do not OR the timers: an explicitly reset pose window means real
        // movement, even if the legacy velocity timer still reports quiet.
        JsonNode? seconds = status.ContainsKey("pose_stationary_s") ? status["pose_stationary_s"] : status["stationary_s"];
        return seconds is JsonValue value && value.TryGetValue<double>(out double dwell) && double.IsFinite(dwell) && dwell >= .4;
    }
}
