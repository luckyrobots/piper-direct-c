using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

/// <summary>The workspace's structure: the sidebar is the only navigation, a robot's page has tabs,
/// a bench page belongs to no single robot, and a follower group is chosen on one page.</summary>
internal static class WorkspaceStructureTests
{
    private static IEnumerable<Widget> All(Widget root) => new[] { root }.Concat((root.Children ?? []).SelectMany(All));

    public static void RunNavigation()
    {
        var start = WorkspaceNavigation.Initial;
        JsonElement Index(int value) => JsonSerializer.SerializeToElement(value);
        // Each robot tab lands on a page that reports the same tab back.
        string[] expected = ["controls", "robot-cameras", "recording", "settings.robot"];
        for (int tab = 0; tab < WorkspaceTabs.RobotTabs.Length; tab++)
        {
            string action = WorkspaceTabs.Action("pages", Index(tab));
            var page = start.Navigate(action);
            Check(action == expected[tab] && WorkspaceTabs.PageIndex(page) == tab && !WorkspaceTabs.BenchPage(page),
                "Robot tab " + WorkspaceTabs.RobotTabs[tab] + " did not open a page of the selected robot under the same tab");
        }
        Check(WorkspaceTabs.Action("settings.robot", Index(0)) == "settings.robot", "An ordinary workspace action was rewritten as a tab choice");
        bool rejected = false;
        try { WorkspaceTabs.Action("pages", Index(WorkspaceTabs.RobotTabs.Length)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A tab index outside the strip was accepted");

        foreach (string action in new[] { "overview", "follow", "camera.bench", "settings.diagnostics", "settings.cameras.bench" })
            Check(WorkspaceTabs.BenchPage(start.Navigate(action)), action + " is a bench page but was shown as a robot's page");
        foreach (string action in new[] { "controls", "recording", "replay", "settings.robot", "settings.bench", "camera.depth", "settings.cameras.gripper" })
            Check(!WorkspaceTabs.BenchPage(start.Navigate(action)), action + " belongs to the selected robot but was shown as a bench page");
        // The bench camera's setup must not be reopened as a robot's own camera.
        var robotCameras = start.Navigate("settings.cameras.bench").Navigate("robot-cameras");
        Check(robotCameras is { Page: WorkspacePage.Settings, Category: SettingsCategory.Cameras } && robotCameras.SettingsCamera != "bench",
            "A robot's Cameras tab opened the shared bench camera");
        Check(start.Navigate("settings.bench").CalibrationOpen && WorkspaceTabs.PageIndex(start.Navigate("settings.bench")) == 3,
            "Mounting calibration no longer sits under the robot's Setup tab");
    }

    public static async Task RunSidebarAndFollowPage()
    {
        var plugin = new PipersPlugin();
        var first = new RobotProfile("one", "Piper 1");
        var second = new RobotProfile("two", "Piper 3") with { FollowSimulationSelected = true };
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences { Robots = [first, second], ActiveRobotId = "one" });
        // Preferences are saved to a throwaway folder; nothing is sent to an editor or a controller.
        string folder = Path.Combine(Path.GetTempPath(), "piper-workspace-structure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        plugin.engine = Fixtures.Context(new PluginStart("fixture", folder, folder), (_, _) => Task.CompletedTask,
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })));
        try { await Exercise(plugin); }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task Exercise(PipersPlugin plugin)
    {

        var sidebar = All(plugin.RobotSidebar()).ToArray();
        Check(new[] { "profiles.select.one", "profiles.select.two", "profiles.add", "workspace.overview", "workspace.follow",
                "workspace.camera.bench@nav", "workspace.settings.diagnostics@nav", "group.follow.cancel" }.All(id => sidebar.Count(w => w.Id == id) == 1),
            "The sidebar lost a robot, a bench page, the arm server or Stop following");
        Check(sidebar.Single(w => w.Id == "profiles.select.one") is { Type: "nav-item", Value: 1 } &&
            sidebar.Single(w => w.Id == "profiles.select.two") is { Type: "nav-item", Value: 0 } &&
            sidebar.Single(w => w.Id == "group.follow.cancel").Visible == false,
            "The sidebar did not mark the open robot, or offered Stop following with no group running");

        // Each robot has its own STOP beside its name. It names its robot, so it reaches a robot that
        // is not open, on any page, and survives a view being republished.
        Check(sidebar.Count(w => w.Id == "stop.one" && w.Text == "Stop") == 1 && sidebar.Count(w => w.Id == "stop.two" && w.Text == "Stop") == 1,
            "A robot in the sidebar has no STOP of its own");
        // Neither fixture robot has an adapter, so there is nothing to stop: the button is shown but off.
        Check(sidebar.Where(w => w.Id is "stop.one" or "stop.two").All(w => w.Enabled == false && w.Hint!.Contains("not connected", StringComparison.Ordinal)),
            "A robot that is not connected still offered a live Stop");
        foreach (long epoch in new long[] { 0, 7 })
            Check(WorkspaceEventScope.Wrap(epoch, "one", "stop.two") == "stop.two", "A sidebar STOP was tied to the open robot or to one published view");
        Check(plugin.CaptureStopProfile("stop.two", plugin.preferences.Robots!.Single(p => p.Id == "one"))?.Id == "two" &&
            plugin.CaptureStopProfile("stop.one", plugin.preferences.Robots!.Single(p => p.Id == "one"))?.Id == "one" &&
            plugin.CaptureStopProfile("stop.gone", plugin.preferences.Robots!.Single(p => p.Id == "one")) is null,
            "A sidebar STOP did not stop exactly the robot it names");

        // Disable asks first only where it would let a resting, powered arm fall.
        var armed = new RobotProfile("p", "P", "can-p");
        JsonObject Bench(bool power, bool moving) => new() { ["any_motion_active"] = moving, ["devices"] = new JsonArray(new JsonObject {
            ["id"] = "can-p", ["joints"] = new JsonArray(new JsonObject { ["enabled"] = power }, new JsonObject { ["enabled"] = false }) }) };
        Check(PipersPlugin.DisableDropsIdleArm(armed, Bench(true, false), false), "Disable did not ask before dropping an idle powered arm");
        Check(!PipersPlugin.DisableDropsIdleArm(armed, Bench(false, false), false), "Disable asked although every motor was already off");
        Check(!PipersPlugin.DisableDropsIdleArm(armed, Bench(true, true), false) && !PipersPlugin.DisableDropsIdleArm(armed, Bench(true, false), true),
            "Disable asked a question while a movement or a follow run was in progress");
        Check(!PipersPlugin.DisableDropsIdleArm(armed, null, false) && !PipersPlugin.DisableDropsIdleArm(new RobotProfile("q", "Q"), Bench(true, false), false),
            "Disable was held back without fresh state or without an adapter");
        // Feedback of an unexpected shape must not throw on the Disable click: that would lose the click.
        var odd = Bench(true, false); odd["devices"]![0]!["joints"]![0]!["enabled"] = 1;
        Check(!PipersPlugin.DisableDropsIdleArm(armed, odd, false) && !PipersPlugin.DisableDropsIdleArm(armed, new JsonObject { ["devices"] = "none" }, false),
            "Unreadable feedback held Disable back instead of letting it through");

        // Coming back to the open robot from a bench page is navigation only: it keeps control.
        plugin.workspace.Commit(WorkspaceNavigation.Initial.Navigate("overview"));
        plugin.approved = true;
        await plugin.HandleEventCoreAsync(new("fixture", WorkspaceEventScope.Wrap(plugin.workspace.Epoch, "one", "profiles.select.one"), "click",
            JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        Check(plugin.approved && plugin.workspace.Navigation.Page == WorkspacePage.Controls && plugin.preferences.ActiveRobotId == "one",
            "Returning to the open robot from Overview released its control or did not open its Controls");
        plugin.approved = false;

        var page = All(plugin.FollowPage()).ToArray();
        Check(page.Single(w => w.Id == "group.follow.include.one").Value == 0 && page.Single(w => w.Id == "group.follow.include.two").Value == 1 &&
            page.Single(w => w.Id == "group.follow.ready.one") is { Text: "Not included", Appearance: "muted" } &&
            page.Count(w => w.Id == "group.follow.toggle") == 1 && page.All(w => w.Id != "group.follow.cancel"),
            "The Follow simulation page did not list every robot with its own Include tick");

        // Ticking a row includes that robot, whichever robot is open.
        await plugin.GroupFollowWorkspaceEvent("include.one", JsonSerializer.SerializeToElement(true), CancellationToken.None);
        Check(plugin.preferences.Robots!.Single(p => p.Id == "one").FollowSimulationSelected && plugin.preferences.ActiveRobotId == "one",
            "Including a robot from the Follow simulation page did not save, or changed the open robot");
        await plugin.GroupFollowWorkspaceEvent("include.two", JsonSerializer.SerializeToElement(false), CancellationToken.None);
        Check(!plugin.preferences.Robots!.Single(p => p.Id == "two").FollowSimulationSelected, "A robot that is not open could not be taken out of the group");
        bool rejected = false;
        try { await plugin.GroupFollowWorkspaceEvent("include.gone", JsonSerializer.SerializeToElement(true), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A tick for a robot that no longer exists was accepted");

        var overview = All(plugin.OverviewPage()).ToArray();
        Check(new[] { "one", "two" }.All(id => overview.Count(w => w.Id == "overview.state." + id) == 1 && overview.Count(w => w.Id == "overview.next." + id) == 1),
            "The overview did not show one row per robot");
    }

    // The Disable question: asked for an idle powered arm, answered only on the robot it was asked
    // about, and sent to the arm only after "Arm supported — disable".
    public static async Task RunDisableQuestion()
    {
        const string backend = "fake-backend-session";
        var plugin = new PipersPlugin();
        var rpc = new EmergencyStopTests.FakeCalls();
        plugin.client = new LuckyRobots.Piper.Grpc.PiperControl.PiperControlClient(rpc);
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
            [new("one", "Piper 1", "can-one"), new("two", "Piper 2", "can-two")], ActiveRobotId: "one"));
        plugin.telemetry.AdoptBackend(backend);
        var snapshot = Fixtures.Snapshot(backend, Fixtures.Arm("can-one", generation: 1));
        snapshot["any_motion_active"] = false;
        long sequence = 0;
        // Feedback is fresh for half a second; each step that needs it publishes a new receipt.
        void Feedback()
        {
            plugin.telemetry.PublishState(snapshot);
            plugin.telemetry.Publish(new(snapshot, backend, ++sequence, System.Diagnostics.Stopwatch.GetTimestamp(), 0));
        }
        Feedback();
        string folder = Path.Combine(Path.GetTempPath(), "piper-disable-question-" + Guid.NewGuid().ToString("N"));
        // What the editor was last told about the question's panel, from full views and patches alike.
        bool? shown = null;
        void Watch(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Watch(child); return; }
            if (node is not JsonObject item) return;
            if (item["id"] is JsonValue id && id.TryGetValue(out string? name) && name.EndsWith(".disable.confirm", StringComparison.Ordinal))
                shown = item["visible"]?.GetValue<bool>() ?? true;
            foreach (var (key, value) in item)
            {
                if (key.EndsWith(".disable.confirm", StringComparison.Ordinal) && value?["visible"] is JsonValue visible) shown = visible.GetValue<bool>();
                else Watch(value);
            }
        }
        plugin.engine = Fixtures.Context(new PluginStart("fixture", folder, folder),
            (sent, _) => { Watch(JsonSerializer.SerializeToNode(sent, PluginProtocol.Json)); return Task.CompletedTask; },
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })));
        int queued = 0;
        var dispatch = new UiEventDispatch((input, cancellation) => { Interlocked.Increment(ref queued); return plugin.HandleEventCoreAsync(input, cancellation); },
            () => plugin.ControlInputEpoch, _ => { });
        plugin.shell.Open(dispatch);
        try
        {
            await plugin.Render(CancellationToken.None);
            Task Click(string wire) => plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
            string Answer(string answer) => WorkspaceEventScope.Wrap(plugin.workspace.Epoch, plugin.preferences.ActiveRobotId, "disable.confirm." + answer);
            bool Shown() => shown == true;
            Check(shown == false, "The Disable question was not part of the published page, or was open before anyone asked");

            await Click("robot.one.disable");
            Check(plugin.DisableQuestionOpen && Shown() && rpc.Routes.IsEmpty && Volatile.Read(ref queued) == 0,
                "Disable on an idle powered arm did not ask first, or reached the arm before the answer");
            await Click(Answer("cancel"));
            Check(!plugin.DisableQuestionOpen && !Shown() && rpc.Routes.IsEmpty && Volatile.Read(ref queued) == 0, "Cancel did not withdraw the Disable question, or sent something");
            await Click(Answer("accept"));
            Check(rpc.Routes.IsEmpty && Volatile.Read(ref queued) == 0, "An answer with no question open disabled the arm");

            // Asked about robot one; robot two is then opened. The answer given there belongs to nobody.
            await Click("robot.one.disable");
            var asked = plugin.preferences;
            plugin.preferences = RobotProfileRouting.Activate(asked, "two");
            await plugin.Render(CancellationToken.None);
            plugin.approved = true; plugin.message = "";
            Check(!plugin.DisableQuestionOpen && !Shown(), "A Disable question asked about one robot was shown on another robot's page");
            await Click(Answer("accept"));
            Check(rpc.Routes.IsEmpty && Volatile.Read(ref queued) == 0 && plugin.approved && !plugin.message.StartsWith("Disable requested", StringComparison.Ordinal),
                "An answer on another robot's page announced a Disable, or withdrew that robot's control: " + plugin.message);
            plugin.approved = false;
            plugin.preferences = RobotProfileRouting.Activate(plugin.preferences, "one");
            await plugin.Render(CancellationToken.None);
            Check(!plugin.DisableQuestionOpen && !Shown(), "A Disable question came back when its robot was opened again");

            // Asked and accepted on the same robot: exactly one disable command for that arm.
            Feedback();
            await Click("robot.one.disable");
            Check(plugin.DisableQuestionOpen && rpc.Routes.IsEmpty, "Disable did not ask again after an abandoned question");
            Feedback();
            await Click(Answer("accept"));
            await Eventually(() => rpc.Routes.Contains("/api/command"), "Arm supported — disable did not send the disable command");
            var body = JsonNode.Parse(rpc.RequestBodies.Single())!;
            Check(!plugin.DisableQuestionOpen && body["action"]?.GetValue<string>() == "disable" && body["device_id"]?.GetValue<string>() == "can-one",
                "The confirmed Disable was not sent for the robot that was asked about: " + body.ToJsonString());

            // The question names its robot, and STOP on that robot withdraws it: a stopped arm is not asked about.
            Check(plugin.DisableQuestionText.StartsWith("Piper 1: ", StringComparison.Ordinal), "The Disable question did not name its robot: " + plugin.DisableQuestionText);
            Feedback();
            await Click("robot.one.disable");
            Check(plugin.DisableQuestionOpen && Shown(), "Disable did not ask before the STOP check");
            await Click("robot.one.stop");
            Check(!plugin.DisableQuestionOpen, "STOP left the Disable question open for the stopped robot");
        }
        finally
        {
            await dispatch.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    public static void RunProfileDisplay()
    {
        var plugin = new PipersPlugin();
        var profiles = new[] { "unset", "absent", "stopped", "guided", "holding", "partly", "limp" }
            .Select(id => new RobotProfile(id, id, id == "unset" ? "" : "can-" + id)).ToArray();
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences { Robots = profiles, ActiveRobotId = "unset" });
        JsonObject Arm(string id, bool enabled, Action<JsonObject>? change = null)
        { var arm = Fixtures.Arm("can-" + id, enabled: enabled); change?.Invoke(arm); return arm; }
        var state = Fixtures.Snapshot("backend", Arm("holding", true));
        var devices = state["devices"]!.AsArray();
        devices.Add(Arm("stopped", true, arm => arm["recovery_required"] = true));
        devices.Add(Arm("guided", true, arm => arm["status"]!["control"] = 2));
        devices.Add(Arm("partly", true, arm => arm["joints"]![2]!["enabled"] = false));
        devices.Add(Arm("limp", false));
        devices.Add(Arm("absent", true, arm => arm["present"] = false));
        plugin.telemetry.PublishState(state);
        (string, string) Shown(string id) => plugin.ProfileDisplay(plugin.preferences.Robots!.Single(p => p.Id == id));
        // The arm server has said nothing recently: what was last heard is not shown as the robot's state.
        Check(profiles.Skip(1).All(p => Shown(p.Id) == ("No feedback", "muted")) && Shown("unset") == ("Not set up", "muted"),
            "A robot kept its last known state on screen with no arm server answering");
        Check(All(plugin.OverviewPage()).Single(w => w.Id == "overview.next.holding").Text!.Contains("Start the arm server", StringComparison.Ordinal) &&
            All(plugin.OverviewPage()).Single(w => w.Id == "overview.next.unset").Text!.Contains("Setup", StringComparison.Ordinal),
            "The overview did not point a silent robot at the arm server, or stopped pointing an unassigned one at Setup");
        plugin.telemetry.AdoptBackend("backend");
        plugin.telemetry.Publish(new(state, "backend", 1, System.Diagnostics.Stopwatch.GetTimestamp(), 0));
        Check(Shown("unset") == ("Not set up", "muted") && Shown("absent") == ("Offline", "muted"), "A robot that is not there was not shown grey, or not named as such");
        Check(Shown("stopped") == ("Stopped", "danger"), "A robot that needs recovery was not shown red as Stopped");
        Check(Shown("guided") == ("Hand guiding", "warning"), "An arm on its own teach button was not shown as Hand guiding");
        // Green is kept for a robot with nothing left to do; a powered arm this session has not connected to is amber.
        Check(Shown("holding") == ("Motors on", "warning") && Shown("limp") == ("Disabled", "warning") && Shown("partly") == ("Disabled", "warning"),
            "A powered or unpowered robot that is not open was given the wrong word or colour");
    }
}
