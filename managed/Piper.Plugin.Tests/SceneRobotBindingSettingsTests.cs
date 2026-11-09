using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class SceneRobotBindingSettingsTests
{
    public static void Run()
    {
        var first = new RobotProfile("one", "Piper 1", "can-one", RobotId: "compound");
        var second = new RobotProfile("two", "Piper 2", "can-two", RobotId: "compound", RobotJointPrefix: "piper3_");
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [first, second], ActiveRobotId: "one"));
        var scene = new JsonObject { ["robots"] = new JsonArray(new JsonObject { ["entityId"] = "compound",
            ["name"] = "Piper 1 + Piper 3 + tube - shared relay physics", ["joints"] = CompoundRobotBindingTests.World()["joints"]!.DeepClone() }) };
        var options = SceneRobotBindings.Options(scene["robots"]!.AsArray().OfType<JsonObject>());
        Check(SceneRobotBindings.AvailableFor(options, prefs.Robots!, first).Select(o => o.JointPrefix).SequenceEqual([""]) &&
            SceneRobotBindings.AvailableFor(options, prefs.Robots!, second).Select(o => o.JointPrefix).SequenceEqual(["piper3_"]),
            "Each real robot's picker offered the simulated arm already assigned to its neighbour");
        RobotProfileRouting.ValidateSceneAssignment(prefs, first.Id, "compound", "");
        RobotProfileRouting.ValidateSceneAssignment(prefs, second.Id, "compound", "piper3_");
        RobotProfileRouting.ValidateSceneAssignment(prefs, second.Id, "different-model", "");
        RobotProfileRouting.ValidateSceneAssignment(prefs, second.Id, "", "");
        Reject(() => RobotProfileRouting.ValidateSceneAssignment(prefs, second.Id, "compound", ""), "Exact duplicate simulation arm assignment was accepted");
        Reject(() => RobotProfileRouting.ValidateSceneAssignment(prefs, first.Id, "compound", "piper3_"), "A compound-model namespace could be assigned twice");
        Reject(() => RobotProfileRouting.ValidateSceneAssignment(prefs, second.Id, "", "piper3_"), "Unassignment left a dangling arm namespace");
        var roundTrip = RobotProfileRouting.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(prefs))!);
        Check(roundTrip.Robots!.Select(p => (p.Id, p.RobotId, p.RobotJointPrefix)).SequenceEqual(prefs.Robots!.Select(p => (p.Id, p.RobotId, p.RobotJointPrefix))),
            "Friendly scene labels replaced or changed the saved exact entity/namespace binding");

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string name, object value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        Widget Render() => (Widget)typeof(PipersPlugin).GetMethod("GlobalRobotsSettings", flags)!.Invoke(plugin, null)!;
        IEnumerable<Widget> Walk(Widget widget)
        {
            yield return widget;
            foreach (var child in widget.Children ?? []) foreach (var descendant in Walk(child)) yield return descendant;
        }
        Set("preferences", prefs); Set("scene", scene);
        var widgets = Walk(Render()).ToArray();
        Check(widgets.Where(w => w.Type is "input" or "dropdown").All(w => w.Width is null or <= 250) &&
            !widgets.Any(w => w.Id == "global.robot.headings" || w.Id.EndsWith(".connections", StringComparison.Ordinal)) &&
            new[] { first, second }.All(p => new[] { "name", "can", "pika", "scene" }.All(field =>
                widgets.Any(w => w.Id == "global.robot." + p.Id + "." + field + ".field" && w.Type == "column"))),
            "Global robot settings still required a wide multi-device grid instead of stacked fields");
        foreach (var (profile, expected) in new[] { (first, "Sim arm 1"), (second, "Sim arm 2") })
        {
            var dropdown = widgets.Single(w => w.Id == "global.robot." + profile.Id + ".scene");
            var summary = widgets.Single(w => w.Id == "global.robot." + profile.Id + ".scene.assignment");
            Check(dropdown.Options!.SequenceEqual(["Unassigned", expected]) && Convert.ToInt32(dropdown.Value) == 1 &&
                summary.Text == profile.Name + " → " + expected && summary.Hint!.Contains("Scene entity: compound", StringComparison.Ordinal),
                "Global settings did not show a distinct saved real-to-sim arm assignment for " + profile.Name);
        }
        Check(widgets.Where(w => w.Id.EndsWith(".scene", StringComparison.Ordinal)).All(w => w.Options!.All(o => !o.Contains("shared relay", StringComparison.Ordinal) && !o.Contains("piper3_", StringComparison.Ordinal))),
            "Physics container names or joint namespaces leaked back into the arm dropdown");
        Set("preferences", prefs with { Robots = [first, second with { Name = "Opposite robot" }] });
        var patches = new Dictionary<string, JsonObject>();
        typeof(PipersPlugin).GetMethod("AddProfilePatches", flags)!.Invoke(plugin, [patches]);
        Check(patches["global.robot.two.scene.assignment"]["text"]?.GetValue<string>() == "Opposite robot → Sim arm 2",
            "Renaming a robot left its assignment summary stale or required rebuilding the settings page");

        // A second assignment can change after this robot's dropdown rendered.
        // Recheck the exact selected identity before changing any preference or
        // touching a controller; a cached integer is not an assignment lease.
        Set("preferences", prefs with { Robots = [first with { RobotId = "" }, second with { RobotId = "", RobotJointPrefix = "" }] });
        _ = Render();
        Set("preferences", prefs with { Robots = [first, second with { RobotId = "", RobotJointPrefix = "" }] });
        Reject(() => ((Task)typeof(PipersPlugin).GetMethod("GlobalSettingsEvent", flags)!.Invoke(plugin,
            ["robot.two.scene", JsonSerializer.SerializeToElement(1), CancellationToken.None])!).GetAwaiter().GetResult(),
            "A stale dropdown selected the arm newly assigned to a different robot");
        var unchanged = (Preferences)typeof(PipersPlugin).GetField("preferences", flags)!.GetValue(plugin)!;
        Check(unchanged.Robots!.Single(p => p.Id == second.Id) is { RobotId: "", RobotJointPrefix: "" },
            "Rejected duplicate selection partially changed the real robot's binding");

    }
}
