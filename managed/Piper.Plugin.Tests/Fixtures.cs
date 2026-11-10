using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

/// <summary>Controller snapshot builders shared by the plugin test fixtures.
/// Each returns a fresh tree, so a test may mutate what it gets.</summary>
internal static class Fixtures
{
    /// <summary>An engine context over fake transports. The SDK keeps this
    /// constructor non-public, so this is the one place tests reach it.</summary>
    public static PluginContext Context(PluginStart start, Func<object, CancellationToken, Task> send,
        Func<string, JsonObject, CancellationToken, Task<JsonElement>> request) =>
        (PluginContext)Activator.CreateInstance(typeof(PluginContext), BindingFlags.Instance | BindingFlags.NonPublic, null, [start, send, request], null)!;

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

    /// <summary>A playing, ready engine runtime pose for the fixture robot "robot-a":
    /// six hinge joints, two gripper slides and their position actuators.</summary>
    public static JsonObject SimulationPose(double time = 1, long sequence = 1, long elapsedMs = 0)
    {
        var joints = new JsonArray(); var actuators = new JsonArray();
        double[] qpos = [0, .2, -.3, .1, .2, -.1, -.0370221912391186, .0370221912391186];
        for (int i = 1; i <= 8; i++)
        {
            double min = i switch { 2 => 0, 7 => -.0470221912391186, 8 => .0020221912391186, _ => -Math.PI };
            double max = i switch { 3 => 0, 7 => -.0020221912391186, 8 => .0470221912391186, _ => Math.PI };
            joints.Add(new JsonObject { ["name"] = "joint" + i, ["type"] = i <= 6 ? "hinge" : "slide", ["unit"] = i <= 6 ? "rad" : "m",
                ["position"] = qpos[i - 1], ["velocity"] = 0d, ["limited"] = true, ["min"] = min, ["max"] = max });
            if (i > 7) continue;
            actuators.Add(new JsonObject { ["name"] = i == 7 ? "gripper" : "joint" + i, ["control"] = qpos[i - 1], ["positionControl"] = true,
                ["controlUnit"] = i <= 6 ? "rad" : "m", ["targetJoint"] = "joint" + i, ["transmission"] = "joint", ["limited"] = true, ["min"] = min, ["max"] = max,
                ["gear"] = new JsonArray(1d, 0d, 0d, 0d, 0d, 0d), ["gain"] = new JsonArray(10d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d),
                ["bias"] = new JsonArray(0d, -10d, -1d, 0d, 0d, 0d, 0d, 0d, 0d, 0d) });
        }
        return new JsonObject { ["mode"] = "play", ["ready"] = true, ["sourceSceneId"] = "source-a", ["sceneId"] = "run-a", ["runId"] = "run-a",
            ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a", ["sequence"] = sequence,
            ["capturedMonotonicNs"] = (10_000_000_000L + elapsedMs * 1_000_000L).ToString(),
            ["simulationTimeNs"] = ((long)Math.Round(time * 1e9)).ToString(), ["capturedAtUnixMicroseconds"] = (1_000_000_000L + elapsedMs * 1000).ToString(),
            ["clockDomain"] = "host-utc", ["monotonicClockDomain"] = "native-steady-clock", ["mujocoTimeSeconds"] = time, ["timeScale"] = .2,
            ["joints"] = joints, ["actuators"] = actuators };
    }
}
