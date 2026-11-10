using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // Only the captured profile's scalar assignments and motion settings are used by Follow.
    private RobotProfile[] SelectedFollowProfiles() => RobotProfileRouting.Capture(preferences).Robots!
        .Where(p => p.FollowSimulationSelected).ToArray();

    internal FollowPreparationReport GroupPreparation(RobotProfile[]? selected = null, TelemetryReceipt? receipt = null, JsonObject? discovered = null)
    {
        var report = FollowPreparationReport.Inspect(selected ?? SelectedFollowProfiles(), receipt ?? telemetry.Latest, token, owner, controlApprovals,
            discovered ?? scene, follow.Clock.GetTimestamp());
        return GroupStartConflict() is { } conflict ? report with { Issues = [.. report.Issues, new("", "", "control-busy", conflict)] } : report;
    }

    internal Widget GroupFollowControls()
    {
        var preparation = GroupPreparation();
        string status = GroupStatusLabel(preparation);
        return Widget.Column("group.follow",
        Widget.Button("group.follow.toggle", "Follow simulation") with {
            Height = 42, Appearance = "primary", Visible = !GroupFollowActive,
            Enabled = !GroupFollowActive && preparation.RobotCount > 0,
            Hint = preparation.Ready ? "Arm the checked robots without entering Play. Then press the editor's Play button once for the whole group." : preparation.Summary },
        Widget.Button("group.follow.cancel", "Stop following") with {
            Height = 42, Visible = GroupFollowActive, Hint = "Stop sending targets, request a hold for the arms and grippers used, and pause the simulation." },
        Widget.Label("group.follow.status", status) with {
            Height = GroupStatusHeight(status), Hint = GroupFollowActive ? follow.Status.Detail : status }) with { Spacing = 8 };
    }

    private string GroupStatusLabel(FollowPreparationReport preparation) => GroupFollowActive ? follow.Status.Headline
        : follow.Outcome is { Summary.Length: > 0 } outcome ? outcome.Summary : preparation.Summary;

    private int GroupStatusHeight(string status) => GroupFollowActive ? 72
        : Math.Clamp((status.Count(c => c == '\n') + 2) * 38, 72, 420);

    private async Task GroupFollowWorkspaceEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "selected")
        {
            if (GroupFollowActive) throw new InvalidOperationException("Stop following before changing the robot group.");
            bool selected = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;
            string id = ActiveProfile.Id;
            preferences = RobotProfileRouting.Capture(preferences);
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == id ? p with { FollowSimulationSelected = selected } : p).ToArray() };
            follow.ClearOutcome();
            await Save(cancellation);
            // Patching avoids resetting a camera or an in-flight profile click.
            await Update(cancellation);
            return;
        }
        await GroupFollowEvent(action, cancellation);
        await Update(cancellation);
    }

    private void AddGroupFollowPatches(Dictionary<string, JsonObject> patches)
    {
        bool active = GroupFollowActive;
        var preparation = GroupPreparation();
        string status = GroupStatusLabel(preparation);
        patches["group.follow.toggle"] = new() { ["visible"] = !active,
            ["enabled"] = !active && preparation.RobotCount > 0, ["hint"] = active ? follow.Status.Detail : preparation.Summary };
        patches["group.follow.cancel"] = new() { ["visible"] = active, ["enabled"] = true };
        patches["group.follow.status"] = new() { ["text"] = status, ["height"] = GroupStatusHeight(status),
            ["hint"] = active ? follow.Status.Detail : status };
        patches["group.follow.selected"] = new() { ["value"] = ActiveProfile.FollowSimulationSelected ? 1 : 0,
            ["enabled"] = !active, ["hint"] = active ? "Stop following before changing the group." : "Include this robot when you press Follow simulation in the left sidebar." };
        if (!active) return;
        foreach (string id in patches.Keys.Where(id => GroupBlocksControlEvent(id) && id is not ("enable" or "teaching.help") &&
            (!id.StartsWith("joint.", StringComparison.Ordinal) || !id.EndsWith(".value", StringComparison.Ordinal) && !id.EndsWith(".current", StringComparison.Ordinal))))
        {
            patches[id]["enabled"] = false;
            patches[id]["hint"] = "Stop following in the left sidebar before changing robot controls.";
        }
        // Power release and movement-stop remain actionable cancellation paths.
        patches["motion.hold"] = new() { ["enabled"] = true, ["hint"] = "Stop following and hold all selected arms and grippers." };
    }
}
