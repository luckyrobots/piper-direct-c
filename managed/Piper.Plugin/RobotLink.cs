using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Explicit joint-name mapping and unit conversion, independent of engine internals.</summary>
public sealed class RobotLink
{
    public string SceneId { get; }
    public string EntityId { get; }
    public string[] JointNames { get; }
    public long Generation { get; }
    public string DeviceId { get; }

    public RobotLink(string sceneId, string entityId, JsonArray modelJoints, JsonObject device)
    {
        SceneId = sceneId; EntityId = entityId;
        DeviceId = device["id"]!.GetValue<string>();
        Generation = device["generation"]!.GetValue<long>();
        JointNames = Enumerable.Range(1, 6).Select(i => "joint" + i).ToArray();
        foreach (string name in JointNames)
        {
            var matches = modelJoints.OfType<JsonObject>().Where(j => j["name"]?.GetValue<string>() == name).ToArray();
            if (matches.Length != 1 || matches[0]["unit"]?.GetValue<string>() != "rad")
                throw new InvalidOperationException($"Pipers requires one revolute {name} in the selected model. Map/calibrate a different model before linking.");
        }
    }

    public JsonObject Feedback(JsonObject device)
    {
        ValidateDevice(device, false);
        var measured = device["joints"]!.AsArray();
        if (measured.Count != 6) throw new InvalidOperationException("Expected six live joints.");
        var values = new JsonObject();
        for (int i = 0; i < 6; i++)
        {
            double degrees = measured[i]!["actual_deg"]?.GetValue<double>() ?? double.NaN;
            if (!double.IsFinite(degrees)) throw new InvalidOperationException("Joint feedback is unavailable.");
            values[JointNames[i]] = degrees * Math.PI / 180;
        }
        return new JsonObject { ["sceneId"] = SceneId, ["entityId"] = EntityId, ["joints"] = values };
    }

    public double[] Target(JsonObject edit, JsonObject device)
    {
        ValidateDevice(device, true);
        if (edit["sceneId"]?.GetValue<string>() != SceneId || edit["entityId"]?.GetValue<string>() != EntityId)
            throw new InvalidOperationException("Target belongs to another robot or scene.");
        var joints = edit["joints"]!.AsArray().OfType<JsonObject>().ToArray();
        var targets = JointNames.Select(name => joints.Single(j => j["name"]?.GetValue<string>() == name)["position"]!.GetValue<double>() * 180 / Math.PI).ToArray();
        if (edit["editedJoint"] is { } edited)
        {
            string name = edited.GetValue<string>();
            if (!JointNames.Contains(name)) throw new InvalidOperationException("The edited joint is not mapped to this arm.");
            for (int i = 0; i < targets.Length; i++)
                if (JointNames[i] != name) targets[i] = device["joints"]![i]!["actual_deg"]!.GetValue<double>();
        }
        for (int i = 0; i < 6; i++)
        {
            var limit = device["joints"]![i]!;
            double minimum = limit["min_deg"]!.GetValue<double>(), maximum = limit["max_deg"]!.GetValue<double>();
            double measured = limit["actual_deg"]!.GetValue<double>();
            // Preserve the dashboard's 0.1-degree feedback allowance for an
            // unchanged joint. Every emitted target still stays inside its limits.
            if (Math.Abs(targets[i] - measured) < .001 && measured >= minimum - .1 && measured <= maximum + .1)
                targets[i] = Math.Clamp(targets[i], minimum, maximum);
            if (limit["limits_fresh"]?.GetValue<bool>() != true || !double.IsFinite(targets[i]) ||
                targets[i] < minimum || targets[i] > maximum)
                throw new InvalidOperationException($"Scene target J{i + 1} is outside the robot's fresh reported limits.");
        }
        return targets;
    }

    public double FeedbackError(JsonObject requested, JsonObject reply)
    {
        var pose = reply["pose"]?["joints"]?.AsArray() ?? throw new InvalidOperationException("The editor cannot verify the linked pose; update the engine plugin host.");
        double maximum = 0;
        foreach (string name in JointNames)
        {
            double actual = pose.OfType<JsonObject>().Single(j => j["name"]?.GetValue<string>() == name)["position"]!.GetValue<double>();
            double error = Math.Abs(actual - requested["joints"]![name]!.GetValue<double>()) * 180 / Math.PI;
            if (!double.IsFinite(error) || error > 0.01) throw new InvalidOperationException($"Scene joint {name} did not accept measured feedback.");
            maximum = Math.Max(maximum, error);
        }
        return maximum;
    }

    private void ValidateDevice(JsonObject device, bool forMotion)
    {
        if (device["id"]?.GetValue<string>() != DeviceId || device["generation"]?.GetValue<long>() != Generation || device["fresh"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Robot connection changed or feedback is stale. Link again after reconnecting.");
        if (forMotion && device["recovery_required"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Robot needs recovery. Measured scene tracking continues; motion is blocked.");
    }
}
