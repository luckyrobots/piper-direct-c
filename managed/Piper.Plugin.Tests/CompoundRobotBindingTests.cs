using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static class CompoundRobotBindingTests
{
    public static void Run()
    {
        var world = World();
        var first = SimulationFollowSample.Read(world);
        var second = SimulationFollowSample.Read(world, "piper3_");
        Check(first.CommandDegrees.SequenceEqual(SimulationFollowSample.Read(Fixtures.SimulationPose()).CommandDegrees),
            "Compound runtime selected another robot by array order instead of canonical names");
        Check(Math.Abs(second.CommandDegrees[0] - .5 * 180 / Math.PI) < 1e-9 && Math.Abs(second.MeasuredDegrees[0] - .6 * 180 / Math.PI) < 1e-9 &&
            second.CommandOpeningMm == 0 && second.MeasuredOpeningMm == 0 && first.CommandOpeningMm > 19.9,
            "Namespaced runtime did not independently select its six joints and two Pika fingers");
        Check(first.Identity == second.Identity && !first.BindsTo(second), "Two arm subsets of one model shared a mapping fingerprint");
        var reordered = World();
        foreach (string key in new[] { "joints", "actuators" }) reordered[key] = new JsonArray(reordered[key]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        Check(first.BindsTo(SimulationFollowSample.Read(reordered)) && second.BindsTo(SimulationFollowSample.Read(reordered, "piper3_")),
            "World ordering changed the pinned exact-name arm mapping");

        var options = SceneRobotBindings.Options([new JsonObject { ["entityId"] = "compound", ["name"] = "Relay world", ["joints"] = world["joints"]!.DeepClone() },
            new JsonObject { ["entityId"] = "static-reference", ["name"] = "Piper 2 reference" }]);
        Check(options.Length == 2 && options.All(o => o.EntityId == "compound") && options.Select(o => o.JointPrefix).ToHashSet().SetEquals(["", "piper3_"]),
            "Scene binding picker lost a namespace or exposed an empty static reference as a controllable arm");
        Check(options.Select(o => o.Name).SequenceEqual(["Sim arm 1", "Sim arm 2"]) && options.All(o => o.ModelName == "Relay world") &&
            options[1].Details.Contains("piper3_", StringComparison.Ordinal),
            "Scene choices exposed a combined physics container instead of individual arms with technical identity in details");
        var reversedOptions = SceneRobotBindings.Options([new JsonObject { ["entityId"] = "compound", ["name"] = "Relay world",
            ["joints"] = reordered["joints"]!.DeepClone() }]);
        Check(options.SequenceEqual(reversedOptions), "Joint enumeration order relabeled the individual simulated arms");
        var device = Device();
        foreach (string prefix in new[] { "", "piper3_" })
        {
            var link = new RobotLink("source-a", "robot-a", world["joints"]!.AsArray(), device, prefix);
            var pika = new JsonObject { ["connected"] = true, ["fresh"] = true,
                ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = PikaProtocol.Angle(20) } } };
            var feedback = link.Feedback(device, pika, true);
            var values = feedback["joints"]!.AsObject();
            Check(values.Count == 8 && values.All(j => j.Key.StartsWith(prefix + "joint", StringComparison.Ordinal)) &&
                (prefix.Length == 0 || values["joint1"] is null && values["joint7"] is null), "Measured feedback crossed robot namespaces");
            var reply = new JsonObject { ["pose"] = new JsonObject { ["joints"] = new JsonArray(values.Select(p => (JsonNode)new JsonObject {
                ["name"] = p.Key, ["position"] = p.Value!.DeepClone() }).ToArray()) } };
            Check(link.FeedbackError(feedback, reply) == 0 && link.GripperErrorMm == 0, "Namespaced arm/finger acknowledgement failed");
            var capture = link.OrderedCaptureMapping(world);
            Check(capture["jointNames"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(link.JointNames) &&
                capture["actuatorNames"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(link.JointNames) &&
                capture["gripperMapping"]!["firstJoint"]!.GetValue<string>() == prefix + "joint7" &&
                capture["gripperMapping"]!["secondJoint"]!.GetValue<string>() == prefix + "joint8" &&
                capture["gripperMapping"]!["actuator"]!.GetValue<string>() == prefix + "gripper", "Ordered source requested another robot's actuator track");
            var edit = new JsonObject { ["sceneId"] = "source-a", ["entityId"] = "robot-a", ["joints"] = world["joints"]!.DeepClone(), ["editedJoint"] = prefix + "joint1" };
            var target = link.Target(edit, device);
            Check(Math.Abs(target[0] - (prefix.Length == 0 ? 0 : .6 * 180 / Math.PI)) < 1e-9 && target.Skip(1).All(v => v == 0),
                "Selected viewport edit did not preserve the other physical joints");
            edit["editedJoint"] = prefix.Length == 0 ? "piper3_joint1" : "joint1";
            Reject(() => link.Target(edit, device), "A viewport edit for the other arm crossed its namespace");
        }
        foreach (var invalid in new (string Reason, Action<JsonObject> Change)[] {
            ("missing selected joint", p => p["joints"]!.AsArray().Remove(p["joints"]!.AsArray().First(n => n!["name"]!.GetValue<string>() == "joint1"))),
            ("extra actuator on selected joint", p => p["actuators"]!.AsArray().First(n => n!["name"]!.GetValue<string>() == "piper3_joint1")!["targetJoint"] = "joint1"),
            ("duplicate unselected name", p => p["joints"]!.AsArray().Add(p["joints"]!.AsArray()[0]!.DeepClone())),
            ("oversized world", p => { while (p["joints"]!.AsArray().Count <= 256) p["joints"]!.AsArray().Add(p["joints"]!.AsArray()[0]!.DeepClone()); }) })
        {
            var bad = World(); invalid.Change(bad); Reject(() => SimulationFollowSample.Read(bad), invalid.Reason);
        }
        Reject(() => SimulationFollowSample.Read(world, "unknown_"), "Unknown namespace silently selected an available robot");
        Reject(() => SimulationFollowSample.Read(world, "../"), "Invalid namespace was accepted");

    }

    internal static JsonObject World()
    {
        var result = Fixtures.SimulationPose(); var other = Fixtures.SimulationPose();
        foreach (string key in new[] { "joints", "actuators" })
        {
            foreach (var item in other[key]!.AsArray().OfType<JsonObject>())
            {
                string name = item["name"]!.GetValue<string>(); item["name"] = "piper3_" + name;
                if (key == "actuators") item["targetJoint"] = "piper3_" + item["targetJoint"]!.GetValue<string>();
                if (name == "joint1") item[key == "joints" ? "position" : "control"] = key == "joints" ? .6 : .5;
                if (name == "joint7" || name == "gripper") item[key == "joints" ? "position" : "control"] = item["min"]!.DeepClone();
                if (name == "joint8") item["position"] = item["max"]!.DeepClone();
            }
            result[key] = new JsonArray(other[key]!.AsArray().Concat(result[key]!.AsArray()).Select(n => n!.DeepClone()).ToArray());
        }
        return result;
    }

    private static JsonObject Device() => new() { ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true,
        ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject { ["actual_deg"] = 0d,
            ["limits_fresh"] = true, ["min_deg"] = -180d, ["max_deg"] = 180d }).ToArray()) };
}
