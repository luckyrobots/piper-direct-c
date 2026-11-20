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

    /// <summary>Bench → Follow simulation: every arm on one page, so a group of three is chosen and
    /// checked without opening each robot in turn.</summary>
    internal Widget FollowPage()
    {
        var preparation = GroupPreparation();
        string status = GroupStatusLabel(preparation);
        var rows = preferences.Robots!.Select(p => Widget.Row("group.follow.row." + p.Id,
            new Widget("checkbox", "group.follow.include." + p.Id) { Text = p.Name, Value = p.FollowSimulationSelected ? 1 : 0, Width = 150,
                Enabled = !GroupFollowActive, Hint = "Include this robot in the group. Ticking this does not start Play or move hardware." },
            Widget.Label("group.follow.arm." + p.Id, FollowArm(p)) with { Width = 150, Height = 28, Appearance = "muted" },
            Widget.Label("group.follow.ready." + p.Id, FollowReadiness(p, preparation).Text) with { Height = 28, Appearance = FollowReadiness(p, preparation).Tone }) with { Spacing = 8 }).ToArray();
        return Widget.Column("group.follow",
            Widget.Label("group.follow.steps", FollowSteps(preparation)) with { Height = 28,
                Hint = "The order for one run. A filled dot is done; the arrow is what to do next." },
            Widget.Column("group.follow.robots", [Widget.Row("group.follow.columns",
                Widget.Label("group.follow.columns.include", "INCLUDE") with { Width = 150, Appearance = "muted" },
                Widget.Label("group.follow.columns.arm", "SIMULATION ARM") with { Width = 150, Appearance = "muted" },
                Widget.Label("group.follow.columns.ready", "READY?") with { Appearance = "muted" }) with { Spacing = 8 }, .. rows]) with { Padding = 12, Spacing = 6, Appearance = "panel" },
            Widget.Row("group.follow.actions", Widget.Button("group.follow.toggle", "Follow simulation") with {
                    Height = 42, Width = 200, Appearance = "primary", Visible = !GroupFollowActive,
                    Enabled = !GroupFollowActive && preparation.RobotCount > 0,
                    Hint = preparation.Ready ? "Arm the included robots without entering Play. Then press the editor's Play button once for the whole group." : preparation.Summary },
                Widget.Label("group.follow.then", GroupFollowActive ? "Running. Stop following is in the left sidebar." : "Then press Play in the editor.") with { Appearance = "muted" }) with { Spacing = 12 },
            Widget.Label("group.follow.status", status) with {
                Height = GroupStatusHeight(status), Hint = GroupFollowActive ? follow.Status.Detail : status }) with { Spacing = 12 };
    }

    private string FollowArm(RobotProfile profile)
    {
        var arm = SceneRobotBindings.Options(scene?["robots"]?.AsArray().OfType<JsonObject>() ?? []).SingleOrDefault(option => option.Matches(profile));
        return arm?.Name ?? (profile.RobotId.Length > 0 ? "Not in this scene" : "No arm assigned");
    }

    private static (string Text, string Tone) FollowReadiness(RobotProfile profile, FollowPreparationReport preparation) =>
        !profile.FollowSimulationSelected ? ("Not included", "muted")
        : preparation.Issues.FirstOrDefault(issue => issue.ProfileId == profile.Id) is { } issue ? (issue.Action, "warning")
        // A bench-wide problem (no feedback, an old server) means no robot has been checked yet.
        : preparation.Issues.Count > 0 ? ("Not checked yet", "muted") : ("Ready", "success");

    /// <summary>Bench → Overview: every arm's state and next step at a glance.</summary>
    internal Widget OverviewPage()
    {
        var rows = preferences.Robots!.Select(p => Widget.Row("overview.row." + p.Id,
            Widget.Label("overview.name." + p.Id, p.Name) with { Width = 130, Height = 28 },
            StatusBadge("overview.dot." + p.Id, ProfileDisplay(p).Tone, Widget.Label("overview.state." + p.Id, ProfileDisplay(p).Word) with { Width = 156, Height = 28 }) with { Width = 172 },
            Widget.Label("overview.next." + p.Id, OverviewNext(OverviewStatus(p))) with { Height = 28, Appearance = "muted" }) with { Spacing = 8 }).ToArray();
        return Widget.Column("overview",
            Widget.Column("overview.robots", [Widget.Row("overview.columns",
                Widget.Label("overview.columns.robot", "ROBOT") with { Width = 130, Appearance = "muted" },
                Widget.Label("overview.columns.state", "STATE") with { Width = 172, Appearance = "muted" },
                Widget.Label("overview.columns.next", "NEXT") with { Appearance = "muted" }) with { Spacing = 8 }, .. rows]) with { Padding = 12, Spacing = 6, Appearance = "panel" },
            Widget.Label("overview.open.help", "Click a robot in the left sidebar to open it.") with { Appearance = "muted", Height = 24 },
            Section("overview.server", "ARM SERVER"),
            Widget.Label("backend.status", shell.Backend.Standing.Status) with { Height = 42 },
            Section("overview.follow", "FOLLOWING"),
            Widget.Label("overview.following", GroupFollowActive ? follow.Status.Headline : "Not armed") with { Height = 28 },
            Section("overview.camera", "BENCH CAMERA"),
            Camera("bench", "Shared"),
            Widget.Button("workspace.camera.bench@compare", "Open bench camera") with { Width = 190, Icon = "camera", Hint = "The real and simulated bench views side by side." }) with { Spacing = 10 };
    }

    private static string OverviewNext(string status) => status switch
    {
        "unassigned" => "Open it and assign its arm connection in Setup.",
        "no feedback" => "Start the arm server on the Arm server page.",
        "offline" => "Check its power and USB-CAN cable, and that the arm server is running.",
        "Needs recovery" => "Open it and follow the recovery steps.",
        "In follower group" => "Following the simulation.",
        _ => "Open it to enable or move it."
    };

    private string GroupStatusLabel(FollowPreparationReport preparation) => GroupFollowActive ? follow.Status.Headline
        : follow.Outcome is { Summary.Length: > 0 } outcome ? "Last run: " + outcome.Summary : preparation.Summary;

    private int GroupStatusHeight(string status) => GroupFollowActive ? 72
        : Math.Clamp((status.Count(c => c == '\n') + 2) * 38, 72, 420);

    /// <summary>The four steps of one following run, for the whole group. Display only:
    /// admission is still decided by <see cref="GroupPreparation"/> and the native follower.</summary>
    internal string FollowSteps(FollowPreparationReport preparation)
    {
        bool included = preparation.RobotCount > 0, armed = GroupFollowActive;
        bool playing = armed && !follow.Status.Headline.StartsWith("Armed", StringComparison.Ordinal);
        (bool Done, string Text)[] steps = [(included, "1 Include robots"), (armed || preparation.Ready, "2 Enable them"),
            (armed, "3 Follow simulation"), (playing, "4 Play in the editor")];
        int next = Array.FindIndex(steps, step => !step.Done);
        return string.Join("     ", steps.Select((step, index) => (step.Done ? "● " : index == next ? "→ " : "○ ") + step.Text));
    }

    internal async Task GroupFollowWorkspaceEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        // "selected" is the robot on screen; "include.<id>" is a row of the Follow simulation page.
        if (action == "selected" || action.StartsWith("include.", StringComparison.Ordinal))
        {
            if (GroupFollowActive) throw new InvalidOperationException("Stop following before changing the robot group.");
            bool selected = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;
            string id = action == "selected" ? ActiveProfile.Id : action["include.".Length..];
            if (preferences.Robots!.All(p => p.Id != id)) throw new InvalidOperationException("That robot is no longer in this workspace.");
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

    private void AddGroupFollowPatches(Dictionary<string, JsonObject> patches, JsonObject? device = null)
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
            ["enabled"] = !active, ["hint"] = active ? "Stop following before changing the group." : "Include this robot when you press Follow simulation." };
        patches["group.follow.steps"] = new() { ["text"] = FollowSteps(preparation) };
        patches["group.follow.then"] = new() { ["text"] = active ? "Running. Stop following is in the left sidebar." : "Then press Play in the editor." };
        patches["overview.following"] = new() { ["text"] = active ? follow.Status.Headline : "Not armed" };
        foreach (var profile in preferences.Robots!)
        {
            patches["group.follow.include." + profile.Id] = new() { ["value"] = profile.FollowSimulationSelected ? 1 : 0, ["enabled"] = !active };
            patches["group.follow.arm." + profile.Id] = new() { ["text"] = FollowArm(profile) };
            var readiness = FollowReadiness(profile, preparation);
            patches["group.follow.ready." + profile.Id] = new() { ["text"] = readiness.Text, ["appearance"] = readiness.Tone };
        }
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
