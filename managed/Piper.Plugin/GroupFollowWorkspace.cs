using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public static class BackendActivity
{
    public static bool AnyMotion(JsonNode? state) => state?["any_motion_active"]?.GetValue<bool>() == true ||
        state?["motion"]?["active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["active"]?.GetValue<bool>() == true) ||
        state?["devices"] is JsonArray devices && devices.Any(device => device?["motion"]?["active"]?.GetValue<bool>() == true);
    public static bool AnyQuery(JsonNode? state) => state?["any_query_active"]?.GetValue<bool>() == true ||
        state?["query_active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["query_active"]?.GetValue<bool>() == true);
}

public sealed partial class PipersPlugin
{
    // These are value records. Clone nested camera maps as well so a running
    // follower's assignment cannot be changed by an unrelated settings edit.
    private RobotProfile[] SelectedFollowProfiles() => RobotProfileRouting.Capture(preferences).Robots!
        .Where(p => p.FollowSimulationSelected)
        .Select(p => p with { SimulationCameras = p.SimulationCameras is null ? null : new(p.SimulationCameras) }).ToArray();

    private string groupFollowFailure = "";

    private FollowPreparationReport GroupPreparation(RobotProfile[]? selected = null, TelemetryReceipt? receipt = null, JsonObject? discovered = null) =>
        FollowPreparationReport.Inspect(selected ?? SelectedFollowProfiles(), receipt ?? telemetry.Latest, token, owner,
            controlApprovals.ToDictionary(p => p.Key, p => new FollowControlApproval(p.Value.Backend, p.Value.Device,
                p.Value.Generation, p.Value.SceneRobot, p.Value.JointPrefix, p.Value.PikaDevice, p.Value.Distribution)),
            discovered ?? scene, Stopwatch.GetTimestamp());

    private Widget GroupFollowControls()
    {
        var preparation = GroupPreparation();
        return Widget.Column("group.follow",
        Widget.Button("group.follow.toggle", "Follow simulation") with {
            Height = 42, Appearance = "primary", Visible = !GroupFollowActive,
            Enabled = !GroupFollowActive && preparation.RobotCount > 0,
            Hint = preparation.Ready ? "Arm the checked robots without entering Play. Then press the editor's Play button once for the whole group." : preparation.Summary },
        Widget.Button("group.follow.cancel", "Stop following") with {
            Height = 42, Visible = GroupFollowActive, Hint = "Hold every selected arm and gripper at its measured pose and pause the simulation." },
        Widget.Label("group.follow.status", GroupStatusLabel(preparation)) with {
            Height = GroupStatusHeight(preparation), Hint = GroupFollowActive ? GroupFollowDetail : preparation.Summary }) with { Spacing = 8 };
    }

    private string GroupStatusLabel(FollowPreparationReport preparation) => GroupFollowActive ? GroupFollowStatus
        : groupFollowFailure.Length > 0 ? groupFollowFailure : preparation.Summary;

    private int GroupStatusHeight(FollowPreparationReport preparation) => GroupFollowActive ? 72
        : Math.Clamp((groupFollowFailure.Length > 0 ? groupFollowFailure.Count(c => c == '\n') + 2 : preparation.Issues.Count + 1) * 38, 72, 420);

    private async Task GroupFollowWorkspaceEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "selected")
        {
            if (GroupFollowActive) throw new InvalidOperationException("Stop following before changing the robot group.");
            bool selected = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;
            string id = ActiveProfile.Id;
            preferences = RobotProfileRouting.Capture(preferences);
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == id ? p with { FollowSimulationSelected = selected } : p).ToArray() };
            groupFollowFailure = "";
            await Save(cancellation);
            // Patching avoids resetting a camera or an in-flight profile click.
            await Update(cancellation);
            return;
        }
        if (action is not ("toggle" or "cancel")) throw new InvalidOperationException("Unknown follower group action.");
        await GroupFollowEvent(action, cancellation);
        await Update(cancellation);
    }

    private void AddGroupFollowPatches(Dictionary<string, JsonObject> patches)
    {
        bool active = GroupFollowActive;
        var preparation = GroupPreparation();
        patches["group.follow.toggle"] = new() { ["visible"] = !active,
            ["enabled"] = !active && preparation.RobotCount > 0, ["hint"] = active ? GroupFollowDetail : preparation.Summary };
        patches["group.follow.cancel"] = new() { ["visible"] = active, ["enabled"] = true };
        patches["group.follow.status"] = new() { ["text"] = GroupStatusLabel(preparation), ["height"] = GroupStatusHeight(preparation),
            ["hint"] = active ? GroupFollowDetail : preparation.Summary };
        patches["group.follow.selected"] = new() { ["value"] = ActiveProfile.FollowSimulationSelected ? 1 : 0,
            ["enabled"] = !active, ["hint"] = active ? "Stop following before changing the group." : "Include this robot when you press Follow simulation in the left sidebar." };
        if (!active) return;
        foreach (string id in patches.Keys.Where(id => id is "enable" or "link" or "zero.all" or "teleoperate.toggle" or "speed" or
            "pika.target" or "pika.minus" or "pika.plus" or "pika.zero" or "pika.reference.set" or "pika.reference.clear" or
            "teaching.return" or "teaching.return.confirm" or "teaching.reset.begin" or "teaching.reset.confirm" or
            "teaching.pika.free" or "teaching.pika.confirm" or "teaching.pika.hold" ||
            id.StartsWith("teleoperate.joint.", StringComparison.Ordinal) || id.StartsWith("joint.", StringComparison.Ordinal) &&
                (id.EndsWith(".target", StringComparison.Ordinal) || id.EndsWith(".minus", StringComparison.Ordinal) ||
                 id.EndsWith(".plus", StringComparison.Ordinal) || id.EndsWith(".zero", StringComparison.Ordinal))))
        {
            patches[id]["enabled"] = false;
            patches[id]["hint"] = "Stop following in the left sidebar before changing robot controls.";
        }
        // Power release and movement-stop remain actionable cancellation paths.
        patches["motion.hold"] = new() { ["enabled"] = true, ["hint"] = "Stop following and hold all selected arms and grippers." };
        if (!GroupFollowIncludes(ActiveProfile.Id)) return;
        patches["enable"] = new() { ["text"] = "Enabled · in follower group", ["enabled"] = false, ["visible"] = workspacePage == "controls" };
        patches["workspace.connection"] = new() { ["text"] = GroupFollowStatus, ["hint"] = GroupFollowDetail };
        foreach (int i in Enumerable.Range(1, 6)) foreach (string action in new[] { "minus", "plus", "target", "zero" })
            patches[$"joint.{i}.{action}"] = new() { ["enabled"] = false, ["hint"] = "The follower group owns this robot. Stop following to use manual controls." };
        foreach (string id in new[] { "pika.minus", "pika.plus", "pika.target", "pika.zero", "zero.all", "teleoperate.toggle", "speed" })
            patches[id] = new() { ["enabled"] = false, ["hint"] = "Stop the follower group before sending manual targets or changing its speed." };
    }
}
