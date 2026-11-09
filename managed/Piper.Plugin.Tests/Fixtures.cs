using System.Text.Json.Nodes;

/// <summary>Controller snapshot builders shared by the plugin test fixtures.
/// Each returns a fresh tree, so a test may mutate what it gets.</summary>
internal static class Fixtures
{
    /// <summary>One present, fresh, idle arm named "Fixture": controller mode,
    /// no teach or error state, six healthy joints at zero with ±180° limits.
    /// <paramref name="joint"/> adjusts each joint (1-based index) in place.</summary>
    public static JsonObject Arm(string id = "can-a", long generation = 7, bool enabled = true, double ageSeconds = .001,
        Action<int, JsonObject>? joint = null) => new()
    {
        ["id"] = id, ["name"] = "Fixture", ["generation"] = generation, ["present"] = true, ["up"] = true, ["fresh"] = true,
        ["firmware_supported"] = true, ["recovery_required"] = false, ["reset_pending"] = false, ["blocker"] = "",
        ["status"] = new JsonObject { ["control"] = 1, ["teach_state"] = 0, ["arm_status"] = 0, ["error"] = 0, ["age_s"] = ageSeconds, ["stationary_s"] = 1.0 },
        ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(index =>
        {
            var value = new JsonObject
            {
                ["index"] = index, ["fresh"] = true, ["enabled"] = enabled, ["fault"] = 0, ["actual_deg"] = 0.0, ["age_s"] = ageSeconds,
                ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
            };
            joint?.Invoke(index, value);
            return (JsonNode)value;
        }).ToArray())
    };

    /// <summary>A configured, connected and fresh Pika session record.</summary>
    public static JsonObject Pika(string device, string? distribution, string owner = "", bool enabled = false, double position = 0) => new()
    {
        ["configured"] = true, ["device"] = device, ["distribution"] = distribution, ["owner"] = owner,
        ["fresh"] = true, ["connected"] = true, ["armed"] = enabled, ["enabled"] = enabled, ["pending"] = false, ["age_s"] = .001,
        ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = position, ["Speed"] = 0.0 } }
    };

    /// <summary>A controller state with <paramref name="arm"/> selected.</summary>
    public static JsonObject Snapshot(string token, JsonObject arm, JsonObject? pika = null, JsonObject? motion = null) => new()
    {
        ["token"] = token, ["selected"] = arm["id"]!.GetValue<string>(),
        ["motion"] = motion ?? new JsonObject { ["active"] = false, ["request_id"] = 0L },
        ["devices"] = new JsonArray(arm),
        ["pika"] = pika ?? new JsonObject { ["configured"] = false, ["fresh"] = false, ["connected"] = false }
    };
}
