using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>Strict evidence for a no-write stop of an already disabled gripper.
/// The service also checks its private acknowledged request under its operation lock.</summary>
public static class PikaDisabledEvidence
{
    public static bool Confirmed(JsonObject? state, string owner) => Guid.TryParse(owner, out _) &&
        Text(state?["owner"]) == owner && Text(state?["device"]).StartsWith("/dev/", StringComparison.Ordinal) &&
        Is(state?["configured"], true) && Is(state?["connected"], true) && Is(state?["fresh"], true) &&
        Is(state?["enabled"], false) && Is(state?["armed"], false) && Is(state?["pending"], false) &&
        Is(state?["following"], false) && Is(state?["resuming_hold"], false) && Is(state?["power_uncertain"], false) &&
        Text(state?["last_motor_request"]).Length > 0 && Number(state?["age_s"], out double age) && age is >= 0 and <= .5 &&
        Number(state?["request_time_s"], out double requested) && requested >= 0 &&
        Number(state?["feedback_time_s"], out double feedback) && feedback > requested;

    /// <summary>A connected gripper that reports fresh disabled feedback and
    /// that no session request ever powered: there is no servo to hold.</summary>
    public static bool NeverPowered(JsonObject? state, string owner) => Guid.TryParse(owner, out _) &&
        Text(state?["owner"]) == owner && Text(state?["device"]).StartsWith("/dev/", StringComparison.Ordinal) &&
        Is(state?["configured"], true) && Is(state?["connected"], true) && Is(state?["fresh"], true) &&
        Is(state?["enabled"], false) && Is(state?["armed"], false) && Is(state?["pending"], false) &&
        Is(state?["following"], false) && Is(state?["resuming_hold"], false) &&
        Is(state?["power_requested"], false) && Is(state?["power_uncertain"], false) &&
        Text(state?["last_motor_request"]).Length == 0 && Number(state?["age_s"], out double age) && age is >= 0 and <= .5;

    private static bool Is(JsonNode? node, bool expected) => node is JsonValue value && value.TryGetValue<bool>(out bool actual) && actual == expected;
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text ?? "" : "";
    private static bool Number(JsonNode? node, out double result)
    {
        result = double.NaN;
        return node is JsonValue value && value.TryGetValue(out result) && double.IsFinite(result);
    }
}
