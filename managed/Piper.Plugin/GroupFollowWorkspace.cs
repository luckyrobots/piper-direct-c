using System.Text.Json;
using System.Text.Json.Nodes;
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

    private Widget GroupFollowControls() => Widget.Column("group.follow",
        Widget.Button("group.follow.toggle", "Follow simulation") with {
            Height = 42, Appearance = "primary", Visible = !GroupFollowActive,
            Enabled = !GroupFollowActive && SelectedFollowProfiles().Length > 0,
            Hint = "Arm the checked robots without entering Play. Then press the editor's Play button once for the whole group." },
        Widget.Button("group.follow.cancel", "Stop following") with {
            Height = 42, Visible = GroupFollowActive, Hint = "Hold every selected arm and gripper at its measured pose and pause the simulation." },
        Widget.Label("group.follow.status", GroupStatusLabel()) with {
            Height = 72, Hint = GroupFollowDetail }) with { Spacing = 8 };

    private string GroupStatusLabel() => GroupFollowActive || GroupFollowStatus != "Simulation only" ? GroupFollowStatus : FollowSelectionLabel();

    private string FollowSelectionLabel()
    {
        int count = SelectedFollowProfiles().Length;
        return count == 0 ? "Check the robots you want to follow." : $"{count} robot{(count == 1 ? "" : "s")} selected · press Follow, then Play.";
    }

    private async Task GroupFollowWorkspaceEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "selected")
        {
            if (GroupFollowActive) throw new InvalidOperationException("Stop following before changing the robot group.");
            bool selected = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;
            string id = ActiveProfile.Id;
            preferences = RobotProfileRouting.Capture(preferences);
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == id ? p with { FollowSimulationSelected = selected } : p).ToArray() };
            await Save(cancellation);
            // Patching avoids resetting a camera or an in-flight profile click.
            await Update(cancellation);
            return;
        }
        if (action is not ("toggle" or "cancel")) throw new InvalidOperationException("Unknown follower group action.");
        if (Volatile.Read(ref simulationSession) is not null) throw new InvalidOperationException("Stop the previous single-robot follower before arming the group.");
        await GroupFollowEvent(action, cancellation);
        await Update(cancellation);
    }

    private void AddGroupFollowPatches(Dictionary<string, JsonObject> patches)
    {
        bool active = GroupFollowActive;
        patches["group.follow.toggle"] = new() { ["visible"] = !active,
            ["enabled"] = !active && SelectedFollowProfiles().Length > 0, ["hint"] = GroupFollowDetail };
        patches["group.follow.cancel"] = new() { ["visible"] = active, ["enabled"] = true };
        patches["group.follow.status"] = new() { ["text"] = GroupStatusLabel(), ["hint"] = GroupFollowDetail };
        patches["group.follow.selected"] = new() { ["value"] = ActiveProfile.FollowSimulationSelected ? 1 : 0,
            ["enabled"] = !active, ["hint"] = active ? "Stop following before changing the group." : "Include this robot when you press Follow simulation in the left sidebar." };
        if (!active || !GroupFollowIncludes(ActiveProfile.Id)) return;
        patches["enable"] = new() { ["text"] = "Enabled · in follower group", ["enabled"] = false, ["visible"] = workspacePage == "controls" };
        patches["workspace.connection"] = new() { ["text"] = GroupFollowStatus, ["hint"] = GroupFollowDetail };
        foreach (int i in Enumerable.Range(1, 6)) foreach (string action in new[] { "minus", "plus", "target", "zero" })
            patches[$"joint.{i}.{action}"] = new() { ["enabled"] = false, ["hint"] = "The follower group owns this robot. Stop following to use manual controls." };
        foreach (string id in new[] { "pika.minus", "pika.plus", "pika.target", "pika.zero", "zero.all", "teleoperate.toggle", "motion.hold", "speed" })
            patches[id] = new() { ["enabled"] = false, ["hint"] = "Stop the follower group before sending manual targets or changing its speed." };
    }
}
