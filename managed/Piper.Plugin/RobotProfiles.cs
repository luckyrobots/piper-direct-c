using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;
using Distribution = LuckyRobots.Piper.Pika.PikaDistribution;

namespace LuckyRobots.Piper.Plugin;

public sealed record AlignmentReference(string Name = "Desk cross / reference object", double X = 0, double DeskY = 0, double Z = 0,
    double ObjectHeightMm = 0, string ToolPoint = "grasp_site");

public sealed record RobotProfile(string Id, string Name, string CanDeviceId = "", double Speed = 10, double Step = 1, double Tolerance = .5,
    string RobotId = "", CameraPreferences? Gripper = null, CameraPreferences? Depth = null, string DepthSerial = "",
    Dictionary<string, SimulationCameraPreferences>? SimulationCameras = null, string PikaDevice = "", string? WslDistribution = null, int CameraSlot = -1,
    AlignmentReference? Alignment = null, bool? RememberMeasuredLink = null, string SimulationTransport = SimulationTrajectoryTransport.PositionVelocity, string RobotJointPrefix = "", bool FollowSimulationSelected = false)
{
    private readonly string? wslDistribution = Distribution.ForCurrentPlatform(WslDistribution);

    /// <summary>The persisted WSL setting, normalized on every construction, <c>with</c>
    /// and load by <see cref="Distribution.ForCurrentPlatform"/>: the named
    /// (or default) WSL distribution on Windows, <c>null</c> (native Linux) elsewhere.
    /// Record equality therefore compares normalized values.</summary>
    public string? WslDistribution { get => wslDistribution; init => wslDistribution = Distribution.ForCurrentPlatform(value); }

    /// <summary>The Pika agent distribution for this host, as the backend routes it.</summary>
    internal string? PikaDistribution => WslDistribution;
}

/// <summary>Persistent identity and settings projection, independent of the editor and hardware.</summary>
public static class RobotProfileRouting
{
    public const int MaximumProfiles = 8;
    public static Preferences Normalize(Preferences value)
    {
        RobotProfile[] profiles = value.Robots is { Length: > 0 } ? value.Robots :
            [new("piper-1", "Piper 1", Speed: value.Speed, Step: value.Step, Tolerance: value.Tolerance, RobotId: value.RobotId,
                Gripper: value.Gripper, Depth: value.Depth, DepthSerial: value.DepthSerial,
                SimulationCameras: RobotCameras(value.SimulationCameras), PikaDevice: value.Backend?.PikaDevice ?? "", WslDistribution: value.Backend?.WslDistribution,
                SimulationTransport: SimulationTrajectoryTransport.Name(value.SimulationTransport))];
        if (profiles.Length > MaximumProfiles || profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length ||
            profiles.Any(p => p.Id.Length is 0 or > 64 || p.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new InvalidOperationException("Robot profile IDs must be unique simple names, with at most eight profiles.");
        var slots = new HashSet<int>();
        profiles = profiles.Select(p =>
        {
            _ = SceneRobotBindings.ValidatePrefix(p.RobotJointPrefix);
            int slot = p.CameraSlot;
            if (slot < 0 || slot >= MaximumProfiles || !slots.Add(slot))
            { slot = Enumerable.Range(0, MaximumProfiles).First(i => !slots.Contains(i)); slots.Add(slot); }
            return p with { Name = CleanName(p.Name), Speed = Math.Clamp(p.Speed, 0, 100), Step = Math.Clamp(p.Step, .1, 5), Tolerance = Math.Clamp(p.Tolerance, .05, 1), CameraSlot = slot,
                SimulationTransport = SimulationTrajectoryTransport.Name(p.SimulationTransport) };
        }).ToArray();
        string active = profiles.Any(p => p.Id == value.ActiveRobotId) ? value.ActiveRobotId : profiles[0].Id;
        return Project(value with { Robots = profiles, ActiveRobotId = active }, profiles.Single(p => p.Id == active));
    }

    public static string CleanName(string name)
    {
        string result = name.Trim();
        if (result.Length is 0 or > 48 || result.Any(char.IsControl)) throw new InvalidOperationException("Use a robot name of 1–48 characters.");
        return result;
    }

    /// <summary>The sidebar's STOP for one named robot: "stop.&lt;profile id&gt;". It names its robot
    /// itself, so it is the same button whichever robot or page is open.</summary>
    public const string RobotStopPrefix = "stop.";

    public static bool SharedWidget(string id) => id.StartsWith("profiles.select.", StringComparison.Ordinal) ||
        id.StartsWith(RobotStopPrefix, StringComparison.Ordinal) ||
        id.StartsWith("group.follow.", StringComparison.Ordinal) && id != "group.follow.selected" ||
        id.StartsWith("global.", StringComparison.Ordinal) ||
        id is "profiles.add" or "profiles.heading" or "profiles.list" or "profiles.edit" or "profiles.mode" ||
        id.StartsWith("camera.bench", StringComparison.Ordinal) || id.StartsWith("backend.", StringComparison.Ordinal) ||
        id.StartsWith("engine.", StringComparison.Ordinal) || id is "workspace" or "sidebar";

    public static string ScopeEvent(string profileId, string id) => SharedWidget(id) ? id : "robot." + profileId + "." + id;
    public static string EventForProfile(string profileId, string id)
    {
        if (!id.StartsWith("robot.", StringComparison.Ordinal)) return SharedWidget(id) ? id : "";
        string prefix = "robot." + profileId + ".";
        return id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : "";
    }

    public static string[] CanInventoryIds(IEnumerable<string> previous, Preferences preferences, JsonObject snapshot) => previous
        .Concat(preferences.Robots!.Select(p => p.CanDeviceId)).Concat(snapshot["devices"]!.AsArray().OfType<JsonObject>().Select(d => d["id"]!.GetValue<string>()))
        .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Take(128).ToArray();

    public static string CanInventoryKey(JsonObject snapshot) => JsonSerializer.Serialize(snapshot["devices"]!.AsArray().OfType<JsonObject>()
        .Select(d => new { Id = d["id"]?.GetValue<string>(), Name = d["name"]?.GetValue<string>(), Present = d["present"]?.GetValue<bool>() == true }).OrderBy(d => d.Id, StringComparer.Ordinal));

    private static Dictionary<string, SimulationCameraPreferences> RobotCameras(Dictionary<string, SimulationCameraPreferences>? cameras) =>
        new((cameras ?? []).Where(p => p.Key != "bench"));

    public static RobotProfile Active(Preferences value) => value.Robots?.SingleOrDefault(p => p.Id == value.ActiveRobotId)
        ?? throw new InvalidOperationException("No active robot profile.");

    public static Preferences Capture(Preferences value)
    {
        if (value.Robots is not { Length: > 0 }) return value;
        var active = Active(value) with { Speed = value.Speed, Step = value.Step, Tolerance = value.Tolerance, RobotId = value.RobotId,
            Gripper = value.Gripper, Depth = value.Depth, DepthSerial = value.DepthSerial, SimulationCameras = RobotCameras(value.SimulationCameras), SimulationTransport = value.SimulationTransport };
        return value with { Robots = value.Robots.Select(p => p.Id == active.Id ? active : p).ToArray() };
    }

    public static Preferences Activate(Preferences value, string id)
    {
        value = Capture(value);
        var profile = value.Robots!.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Unknown robot profile.");
        return Project(value with { ActiveRobotId = id }, profile);
    }

    /// <summary>Stores a new or edited profile and activates it. The active profile's live values
    /// are captured first; capturing after the substitution (as <see cref="Activate"/> would) copies
    /// the stale projection over an edit to the active profile, e.g. a new simulation arm.</summary>
    public static Preferences Apply(Preferences value, RobotProfile profile, bool append)
    {
        value = Capture(value);
        if (!append && value.Robots!.All(p => p.Id != profile.Id)) throw new InvalidOperationException("Unknown robot profile.");
        RobotProfile[] robots = append ? [.. value.Robots!, profile] : value.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray();
        return Project(value with { Robots = robots, ActiveRobotId = profile.Id }, profile);
    }

    private static Preferences Project(Preferences value, RobotProfile profile)
    {
        var cameras = RobotCameras(profile.SimulationCameras);
        if (value.SimulationCameras?.TryGetValue("bench", out var bench) == true) cameras["bench"] = bench;
        return value with { Speed = profile.Speed, Step = profile.Step, Tolerance = profile.Tolerance, RobotId = profile.RobotId,
            Gripper = profile.Gripper, Depth = profile.Depth, DepthSerial = profile.DepthSerial, SimulationCameras = cameras, SimulationTransport = profile.SimulationTransport };
    }

    public static void ValidateCanAssignment(Preferences value, string id, string canId)
    {
        if (canId.Length > 256 || canId.Any(char.IsControl)) throw new InvalidOperationException("Invalid CAN adapter identity.");
        if (canId.Length > 0 && value.Robots!.Any(p => p.Id != id && p.CanDeviceId == canId))
            throw new InvalidOperationException("That CAN adapter is assigned to another robot. Remove its assignment first.");
    }

    public static void ValidateSceneAssignment(Preferences value, string id, string entityId, string jointPrefix)
    {
        SceneRobotBindings.ValidatePrefix(jointPrefix);
        if (entityId.Length > 256 || entityId.Any(char.IsControl) || entityId.Length == 0 && jointPrefix.Length > 0)
            throw new InvalidOperationException("Invalid simulation arm identity.");
        var assigned = value.Robots?.FirstOrDefault(p => p.Id != id && entityId.Length > 0 &&
            p.RobotId == entityId && p.RobotJointPrefix == jointPrefix);
        if (assigned is not null)
            throw new InvalidOperationException($"That simulation arm is already assigned to {assigned.Name}. Unassign it there first.");
    }

    public static JsonObject Device(Preferences value, JsonObject snapshot)
    {
        string id = Active(value).CanDeviceId;
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Assign this robot's CAN adapter in the left sidebar.");
        if (snapshot["selected"]?.GetValue<string>() != id)
            throw new InvalidOperationException("The controller selected a different CAN adapter. Activate this robot's assigned CAN bus before controlling it.");
        return snapshot["devices"]!.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == id)
            ?? throw new InvalidOperationException("This robot's assigned CAN adapter is disconnected.");
    }
}

public sealed partial class PipersPlugin
{
    private volatile bool switchingProfile;
    private bool legacyImportPending;
    private volatile string[] knownCanIds = [];
    private readonly object canIdsGate = new();
    private string canInventoryKey = "";
    private bool canInventoryRenderPending;
    private RobotProfile ActiveProfile => RobotProfileRouting.Active(preferences);
    private string CameraImageId(string id) => id == "bench" ? id : "piper-" + ActiveProfile.CameraSlot + "." + id;
    private bool CameraFrameReady(string id) => calibration.Capture.PublishedFrame(id) is { } frame &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMicroseconds / 1000 < 2000;
    private static Widget ScopeRobotWidget(Widget widget, long epoch, string profileId) => widget with {
        Id = WorkspaceEventScope.Wrap(epoch, profileId, widget.Id), Children = widget.Children?.Select(child => ScopeRobotWidget(child, epoch, profileId)).ToArray() };
    private Task Patch(Dictionary<string, JsonObject> patches, CancellationToken cancellation) =>
        CaptureWorkspacePatchScope() is { } scope
            ? PublishWorkspacePatch(scope, patches, cancellation)
            : Task.CompletedTask;

    /// <summary>The only navigation of the workspace, drawn with the editor's own Settings-sidebar
    /// list: group headings, one indented row per page, a hairline between groups.</summary>
    internal Widget RobotSidebar()
    {
        bool bench = WorkspaceTabs.BenchPage(Navigation);
        bool serverPage = Navigation.Page == WorkspacePage.Settings && Navigation.Category == SettingsCategory.Diagnostics;
        bool benchCamera = bench && (Navigation.Page == WorkspacePage.Camera || Navigation.Page == WorkspacePage.Settings && Navigation.Category == SettingsCategory.Cameras);
        // Every robot keeps its own Stop beside its name, on every page.
        var robots = preferences.Robots!.Select(p => (Widget)(Widget.Row("profiles.row." + p.Id,
            // No tooltip on a plain row: it would cover the robot below it.
            Widget.NavItem("profiles.select." + p.Id, ProfileLabel(p), "robot-arm", !bench && p.Id == ActiveProfile.Id) with
                { Appearance = ProfileDisplay(p).Tone, Hint = p.CanDeviceId.Length == 0 ? "Assign an adapter in this robot's Setup tab" : null },
            Widget.Button(RobotProfileRouting.RobotStopPrefix + p.Id, "Stop") with { Height = 28, Width = 52, Appearance = "danger-quiet",
                Enabled = StopAvailable(p), Hint = StopHint(p) },
            Widget.Label("profiles.row.pad." + p.Id, "") with { Width = 6 }) with { Spacing = 4 })).ToList();
        // Adding a robot opens it, which releases the open robot's control: not offered while that
        // robot is under control or a group is following, so a slip below the list cannot do it.
        robots.Add(Widget.NavItem("profiles.add", "Add robot", "plus", false) with {
            Enabled = preferences.Robots!.Length < RobotProfileRouting.MaximumProfiles && !approved && !GroupFollowActive,
            Hint = approved || GroupFollowActive ? "Disable the open robot and stop following before adding another." : null });
        var rows = new List<Widget> { Widget.NavGroup("profiles.heading", "Robots", "robot-arm", !bench) };
        rows.AddRange(robots);
        rows.AddRange([
            Widget.Separator("sidebar.bench.separator"),
            Widget.NavGroup("sidebar.bench.heading", "Bench", "scene", bench && !serverPage),
            Widget.NavItem("workspace.overview", "Overview", "eye", Navigation.Page == WorkspacePage.Overview),
            Widget.NavItem("workspace.follow", "Follow simulation", "play", Navigation.Page == WorkspacePage.Follow),
            // Always one click away, on every page, while a group is armed or running.
            Widget.Row("group.follow.cancel.row", Widget.Label("group.follow.cancel.pad", "") with { Width = 28 },
                Widget.Button("group.follow.cancel", "Stop following") with { Height = 30, Width = 150, Visible = GroupFollowActive,
                    Hint = "Stop sending targets, request a hold for the arms and grippers used, and pause the simulation." }),
            Widget.NavItem("workspace.camera.bench@nav", "Bench camera", "camera", benchCamera),
            Widget.Separator("sidebar.settings.separator"),
            Widget.NavGroup("sidebar.settings.heading", "Settings", "gear", serverPage),
            Widget.NavItem("workspace.settings.diagnostics@nav", ServerLabel, "server", serverPage) with { Appearance = ServerLive ? "success" : "muted" }]);
        // The Settings sidebar's own width and row gap; rows reach both edges so the lit row spans it.
        // The lighter panel grey of the editor's Settings sidebar, not a black strip.
        return Widget.Column("sidebar", rows.ToArray()) with { Width = 244, Padding = 4, Spacing = 2, Appearance = "panel" };
    }

    /// <summary>A robot with no adapter, or one the arm server reports as absent, has nothing to stop.
    /// A robot that was last seen present keeps its Stop even if feedback has since gone quiet.</summary>
    /// While a follower group runs, every Stop halts the whole group, so none is ever switched off then.
    private bool StopAvailable(RobotProfile profile) => GroupFollowActive || ProfileStatus(profile) is not ("unassigned" or "offline");
    private string StopHint(RobotProfile profile) => StopAvailable(profile)
        ? "Stop " + profile.Name + " now. Holding torque can be lost: support the arm."
        : profile.Name + " is not connected, so there is nothing to stop.";

    private string ProfileLabel(RobotProfile profile) => profile.Name + "\n● " + ProfileDisplay(profile).Word;

    /// <summary>One word and one colour for a robot wherever it is listed: what the arm itself is doing,
    /// not merely that its adapter answers. Green holds or follows, yellow needs the operator before it
    /// can move, red is stopped, grey is not there. Read from the arm server alone, so it is the same
    /// for the open robot and the others.</summary>
    internal (string Word, string Tone) ProfileDisplay(RobotProfile profile)
    {
        string status = ProfileStatus(profile);
        bool open = openRobotStatus is { } header && header.Profile == profile.Id && profile.Id == ActiveProfile.Id;
        // With the arm server silent, what was last heard about a robot is history, not its state. The open
        // robot is the exception: its header already says it is waiting, and its row repeats the header.
        if (NoFeedback(profile) && !(open && status == "connected")) return ("No feedback", "muted");
        if (status != "connected") return (status switch { "Needs recovery" => "Stopped", "In follower group" => "Following", _ => StatusText(status) }, StatusTone(status));
        // The open robot's row repeats its page header, word and colour, so the two can never disagree.
        if (open) return (openRobotStatus!.Value.Word, openRobotStatus.Value.Tone);
        var device = state?["devices"]?.AsArray().FirstOrDefault(d => d?["id"]?.GetValue<string>() == profile.CanDeviceId);
        // Control mode 2 is the arm's own teach button: the hand moves it, software does not.
        if (device?["status"]?["control"]?.GetValue<int>() == 2) return ("Hand guiding", "warning");
        bool powered = device?["joints"] is JsonArray joints && joints.Count > 0 && joints.All(joint => joint?["enabled"]?.GetValue<bool>() == true);
        // Motors on is not yet ready: this session has not connected to the robot, which is what its
        // page will ask for when opened. Green is kept for a robot with nothing left to do.
        return (powered ? "Motors on" : "Disabled", "warning");
    }

    /// <summary>An assigned robot while the arm server is not answering. Only for what is displayed:
    /// <see cref="StopAvailable"/> deliberately keeps Stop for a robot that was last seen present.</summary>
    private bool NoFeedback(RobotProfile profile) => profile.CanDeviceId.Length > 0 && !ServerLive;
    private string OverviewStatus(RobotProfile profile) => NoFeedback(profile) ? "no feedback" : ProfileStatus(profile);

    /// <summary>What the open robot's page header last showed; null until a header has been drawn.</summary>
    private (string Profile, string Word, string Tone)? openRobotStatus;

    /// <summary>What the operator reads for a robot's state, and the colour it is read in.</summary>
    internal static string StatusText(string status) => status switch { "connected" => "Connected", "offline" => "Offline", "unassigned" => "Not set up", _ => status };
    internal static string StatusTone(string status) => status switch { "connected" or "In follower group" => "success", "Needs recovery" => "danger", _ => "muted" };

    private string ProfileStatus(RobotProfile profile)
    {
        var device = state?["devices"]?.AsArray().FirstOrDefault(d => d?["id"]?.GetValue<string>() == profile.CanDeviceId);
        return string.IsNullOrEmpty(profile.CanDeviceId) ? "unassigned" : device?["present"]?.GetValue<bool>() != true ? "offline" :
            device["recovery_required"]?.GetValue<bool>() == true || device["status"]?["arm_status"]?.GetValue<int>() == 1 ? "Needs recovery" :
            GroupFollowIncludes(profile.Id) ? "In follower group" : "connected";
    }

    private async Task ObserveProfileRouting(JsonObject snapshot, CancellationToken cancellation)
    {
        lock (canIdsGate)
        {
            // Slots never reorder or disappear: a delayed dropdown index cannot bind
            // a different adapter after hotplug adds/removes another device.
            string[] next = RobotProfileRouting.CanInventoryIds(knownCanIds, preferences, snapshot);
            string key = RobotProfileRouting.CanInventoryKey(snapshot);
            if (!next.SequenceEqual(knownCanIds, StringComparer.Ordinal) || key != canInventoryKey) canInventoryRenderPending = true;
            knownCanIds = next; canInventoryKey = key;
        }
        if (legacyImportPending)
        {
            string selected = snapshot["selected"]?.GetValue<string>() ?? "";
            if (snapshot["devices"]!.AsArray().Any(d => d?["id"]?.GetValue<string>() == selected && d["present"]?.GetValue<bool>() == true))
            {
                legacyImportPending = false;
                var profile = ActiveProfile with { CanDeviceId = selected,
                    PikaDevice = snapshot["pika"]?["device"]?.GetValue<string>() ?? ActiveProfile.PikaDevice,
                    WslDistribution = snapshot["pika"]?["distribution"]?.GetValue<string>() ?? ActiveProfile.WslDistribution };
                preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray() };
                await Save(cancellation);
            }
        }
        if (!switchingProfile && !string.IsNullOrEmpty(ActiveProfile.CanDeviceId) && snapshot["selected"]?.GetValue<string>() != ActiveProfile.CanDeviceId)
        {
            approved = false; linkCommandsEnabled = false;
            if (teleoperating && await commands.WaitAsync(0, cancellation))
            {
                try { await PauseTeleoperation(cancellation); }
                finally { commands.Release(); }
            }
            message = "The controller changed CAN selection. This robot is read-only until its assigned CAN bus is activated.";
        }
        // Patch supported dropdown choices in place. Hotplug must not recreate
        // button IDs and discard a click or slider gesture already in flight.
        if (canInventoryRenderPending && await commands.WaitAsync(0, cancellation))
        {
            try
            {
                var adapterIds = knownCanIds;
                var patches = new Dictionary<string, JsonObject>();
                string[] settingsOptions = ["Unassigned", .. adapterIds.Select(CanFriendlyName)];
                foreach (var p in preferences.Robots!) patches["global.robot." + p.Id + ".can"] = new() {
                    ["options"] = JsonSerializer.SerializeToNode(settingsOptions),
                    ["value"] = Array.IndexOf(adapterIds, p.CanDeviceId) + 1 };
                await Patch(patches, cancellation); canInventoryRenderPending = false;
            }
            finally { commands.Release(); }
        }
    }

    internal void AddProfilePatches(Dictionary<string, JsonObject> patches)
    {
        foreach (var profile in preferences.Robots!)
        {
            var display = ProfileDisplay(profile);
            patches["profiles.select." + profile.Id] = new() { ["text"] = ProfileLabel(profile), ["enabled"] = true, ["appearance"] = display.Tone };
            patches[RobotProfileRouting.RobotStopPrefix + profile.Id] = new() { ["enabled"] = StopAvailable(profile), ["hint"] = StopHint(profile) };
            patches["overview.dot." + profile.Id] = new() { ["appearance"] = display.Tone };
            patches["overview.state." + profile.Id] = new() { ["text"] = display.Word };
            patches["overview.next." + profile.Id] = new() { ["text"] = OverviewNext(OverviewStatus(profile)) };
            var assignment = SceneArmAssignment(profile, settingsRobotOptions);
            patches[assignment.Id] = new() { ["text"] = assignment.Text };
        }
        patches["profiles.add"] = new() { ["enabled"] = preferences.Robots!.Length < RobotProfileRouting.MaximumProfiles && !approved && !GroupFollowActive };
        string can = ActiveProfile.CanDeviceId;
        patches["profiles.can.status"] = new() { ["text"] = string.IsNullOrEmpty(can) ? "No CAN adapter assigned · scene and robot cameras can still be configured."
            : snapshotSelected() == can ? "Active CAN · " + can : "Assigned CAN inactive or disconnected · " + can };
        string snapshotSelected() => state?["selected"]?.GetValue<string>() ?? "";
    }

    private async Task ProfileEvent(string action, CancellationToken cancellation)
    {
        // An explicit profile edit/selection supersedes the one-time legacy
        // import, even if controller feedback has not arrived yet.
        legacyImportPending = false;
        if (action == "add")
        {
            if (preferences.Robots!.Length >= RobotProfileRouting.MaximumProfiles) throw new InvalidOperationException("At most eight robots are supported in this workspace.");
            var profile = new RobotProfile(Guid.NewGuid().ToString("N"), "Piper " + (preferences.Robots.Length + 1),
                Gripper: new("", false), Depth: new("", false), CameraSlot: Enumerable.Range(0, RobotProfileRouting.MaximumProfiles).First(i => preferences.Robots.All(p => p.CameraSlot != i)));
            await SwitchProfile(profile, append: true, cancellation); return;
        }
        if (action.StartsWith("select.", StringComparison.Ordinal))
        {
            var profile = RobotProfileRouting.Capture(preferences).Robots!.Single(p => p.Id == action[7..]);
            await SwitchProfile(profile, false, cancellation); return;
        }
        throw new InvalidOperationException("Unknown robot profile action.");
    }

    private async Task ReleaseProfileControl(CancellationToken cancellation)
    {
        var snapshot = state;
        bool active = snapshot?["motion"]?["active"]?.GetValue<bool>() == true;
        if (active && snapshot?["motion"]?["owner"]?.GetValue<string>() != owner)
            throw new InvalidOperationException("Another client is moving the arm. Finish that movement before switching robot profiles.");
        if (teleoperating || followRequest != 0) await PauseTeleoperation(cancellation);
        snapshot = state;
        active = snapshot?["motion"]?["active"]?.GetValue<bool>() == true;
        if (active && snapshot?["motion"]?["owner"]?.GetValue<string>() != owner)
            throw new InvalidOperationException("Another client began moving the arm. Profile was not switched.");
        if (active && snapshot is not null)
        {
            var device = RobotProfileRouting.Device(preferences, snapshot);
            await Call(PiperApi.Requests.Command(device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>(), "hold", owner,
                standalone: true, requestId: snapshot["motion"]!["request_id"]!.GetValue<long>()), cancellation);
            long started = Stopwatch.GetTimestamp();
            while (state?["motion"]?["active"]?.GetValue<bool>() == true)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new InvalidOperationException("Waiting for the previous robot to hold. Profile was not switched.");
                await Task.Delay(25, cancellation);
            }
        }
        // Switching the visible robot does not release holding power on its
        // independent gripper session. All owned sessions keep heartbeating.
        if (link is not null) await engine.RequestAsync("robot.unsubscribe", cancellation: cancellation);
        CommandAuthority.Revoke();
        follow.Viewport.Stop();
        approved = linkCommandsEnabled = false; telemetry.Pika.Release(); link = null; linkErrorDegrees = null;
        CancelHandGuideRecovery();
    }

    private async Task SwitchProfile(RobotProfile profile, bool append, CancellationToken cancellation)
    {
        bool groupViewOnly = GroupFollowActive;
        if (groupViewOnly && (append || !preferences.Robots!.Any(p => p.Id == profile.Id && p.CanDeviceId == profile.CanDeviceId &&
            p.RobotId == profile.RobotId && p.RobotJointPrefix == profile.RobotJointPrefix && p.PikaDevice == profile.PikaDevice && p.WslDistribution == profile.WslDistribution)))
            throw new InvalidOperationException("Stop grouped following before changing a robot assignment.");
        if (LensWorkerBusy) throw new InvalidOperationException("Finish camera setup/calibration before switching robots.");
        switchingProfile = true;
        // A Disable question belongs to the robot it was asked about; it does not follow the operator
        // to another robot, nor wait to reappear when this one is opened again.
        pendingDisable = null;
        try
        {
            // Each step is traced: a switch that never returns leaves every later click unanswered, and the
            // last line in the journal then names the step that did not finish.
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "releasing control");
            if (!groupViewOnly) await ReleaseProfileControl(cancellation);
            else { CommandAuthority.Revoke(); link = null; approved = linkCommandsEnabled = false; telemetry.Pika.Release(); }
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "finishing gripper teaching");
            await FinishPikaTeaching("Robot profile or hardware assignment changed", cancellation);
            recovery.PikaTeaching.Store(ActiveProfile);
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "stopping cameras");
            foreach (string id in new[] { "gripper", "depth" }) await calibration.Capture.Stop(id);
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "cameras stopped");
            preferences = RobotProfileRouting.Apply(preferences, profile, append);
            recovery.PikaTeaching.Restore(ActiveProfile);
            calibration.Lens.ClearProfiles();
            foreach (string id in new[] { "gripper", "depth" }) calibration.Capture.Connecting(id, "Connecting…");
            calibration.Lens.Reset();
            await LoadCameraCalibration(cancellation);
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "reading the scene");
            try
            {
                scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
                Materialize(scene);
                await DiscoverSimulationCameras(cancellation);
            }
            catch (PluginRequestException error) { System.Diagnostics.Trace.TraceWarning("Profile scene discovery: {0}", error.Message); }
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "saving");
            await Save(cancellation);
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "selecting the adapter");
            string? selectionError = null;
            // With no arm server answering there is nobody to ask. Nothing selects the adapter later by
            // itself: once feedback returns, clicking this robot again does (see the "elsewhere" rule).
            if (profile.CanDeviceId.Length > 0 && !ServerLive) selectionError = "the arm server is not running";
            else if (profile.CanDeviceId.Length > 0 && state?["selected"]?.GetValue<string>() != profile.CanDeviceId)
            {
                // The robot opens whether or not its adapter could be selected: a switch abandoned here
                // would leave the old robot's page on screen over the new robot's settings.
                try { await Call(PiperApi.Requests.Select(profile.CanDeviceId, owner), cancellation); }
                catch (InvalidOperationException error) { selectionError = error.Message; }
                catch (global::Grpc.Core.RpcException) { selectionError = "the arm server is not running"; }
            }
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "starting cameras");
            foreach (string id in new[] { "gripper", "depth" }) await RestartCamera(id);
            System.Diagnostics.Trace.TraceInformation("Robot switch: {0}", "done");
            message = selectionError is null ? profile.Name + " selected. Controls require fresh feedback and Enable robot. Shared bench camera stays running."
                : profile.Name + " selected · CAN unavailable: " + selectionError;
        }
        finally { switchingProfile = false; }
    }

    private bool PikaMatchesProfile() => ProfilePikaRouting.Matches(ActiveProfile, state?["pika"] as JsonObject);

    private async Task ConfigureProfilePika(string expectedToken, CancellationToken cancellation)
    {
        if (ActiveProfile.PikaDevice.Length == 0 || PikaMatchesProfile()) return;
        if (state?["motion"]?["active"]?.GetValue<bool>() == true) throw new InvalidOperationException("Finish arm movement before changing the Pika assignment.");
        await Call(PiperApi.Requests.PikaConfigure(owner, device: ActiveProfile.PikaDevice, pikaDevice: ActiveProfile.PikaDevice,
            pikaDistribution: ActiveProfile.PikaDistribution, distribution: ActiveProfile.PikaDistribution), cancellation, expectedToken);
        long started = Stopwatch.GetTimestamp();
        while (!PikaMatchesProfile())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2) throw new InvalidOperationException("Waiting for the robot's Pika assignment to be confirmed.");
            await Task.Delay(25, cancellation);
        }
    }
}
