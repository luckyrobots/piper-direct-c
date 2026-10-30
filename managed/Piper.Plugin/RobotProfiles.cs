using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed record RobotProfile(string Id, string Name, string CanDeviceId = "", double Speed = 10, double Step = 1, double Tolerance = .5,
    string RobotId = "", CameraPreferences? Gripper = null, CameraPreferences? Depth = null, string DepthSerial = "",
    Dictionary<string, SimulationCameraPreferences>? SimulationCameras = null, string PikaDevice = "", string WslDistribution = "Ubuntu", int CameraSlot = -1,
    AlignmentReference? Alignment = null);

/// <summary>Persistent identity and settings projection, independent of the editor and hardware.</summary>
public static class RobotProfileRouting
{
    public const int MaximumProfiles = 8;
    public static Preferences Normalize(Preferences value)
    {
        RobotProfile[] profiles = value.Robots is { Length: > 0 } ? value.Robots :
            [new("piper-1", "Piper 1", Speed: value.Speed, Step: value.Step, Tolerance: value.Tolerance, RobotId: value.RobotId,
                Gripper: value.Gripper, Depth: value.Depth, DepthSerial: value.DepthSerial,
                SimulationCameras: RobotCameras(value.SimulationCameras), PikaDevice: value.Backend?.PikaDevice ?? "", WslDistribution: value.Backend?.WslDistribution ?? "Ubuntu")];
        if (profiles.Length > MaximumProfiles || profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Length ||
            profiles.Any(p => p.Id.Length is 0 or > 64 || p.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new InvalidOperationException("Robot profile IDs must be unique simple names, with at most eight profiles.");
        var slots = new HashSet<int>();
        profiles = profiles.Select(p =>
        {
            int slot = p.CameraSlot;
            if (slot < 0 || slot >= MaximumProfiles || !slots.Add(slot))
            { slot = Enumerable.Range(0, MaximumProfiles).First(i => !slots.Contains(i)); slots.Add(slot); }
            return p with { Name = CleanName(p.Name), Speed = Math.Clamp(p.Speed, 0, 100), Step = Math.Clamp(p.Step, .1, 5), Tolerance = Math.Clamp(p.Tolerance, .05, 1), CameraSlot = slot };
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

    public static bool SharedWidget(string id) => id.StartsWith("profiles.select.", StringComparison.Ordinal) ||
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

    public static Preferences RefreshCameraBindings(Preferences value, string sceneId, IEnumerable<string> cameraIds)
    {
        var available = cameraIds.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, SimulationCameraPreferences>? Refresh(Dictionary<string, SimulationCameraPreferences>? bindings) => bindings?.ToDictionary(pair => pair.Key,
            pair => available.Contains(pair.Value.CameraId) ? pair.Value with { SceneId = sceneId } : pair.Value);
        return value with { SimulationCameras = Refresh(value.SimulationCameras),
            Robots = value.Robots?.Select(p => p with { SimulationCameras = Refresh(p.SimulationCameras) }).ToArray() };
    }

    private static Dictionary<string, SimulationCameraPreferences> RobotCameras(Dictionary<string, SimulationCameraPreferences>? cameras) =>
        new((cameras ?? []).Where(p => p.Key != "bench"));

    public static RobotProfile Active(Preferences value) => value.Robots?.SingleOrDefault(p => p.Id == value.ActiveRobotId)
        ?? throw new InvalidOperationException("No active robot profile.");

    public static Preferences Capture(Preferences value)
    {
        if (value.Robots is not { Length: > 0 }) return value;
        var active = Active(value) with { Speed = value.Speed, Step = value.Step, Tolerance = value.Tolerance, RobotId = value.RobotId,
            Gripper = value.Gripper, Depth = value.Depth, DepthSerial = value.DepthSerial, SimulationCameras = RobotCameras(value.SimulationCameras) };
        return value with { Robots = value.Robots.Select(p => p.Id == active.Id ? active : p).ToArray() };
    }

    public static Preferences Activate(Preferences value, string id)
    {
        value = Capture(value);
        var profile = value.Robots!.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Unknown robot profile.");
        return Project(value with { ActiveRobotId = id }, profile);
    }

    private static Preferences Project(Preferences value, RobotProfile profile)
    {
        var cameras = RobotCameras(profile.SimulationCameras);
        if (value.SimulationCameras?.TryGetValue("bench", out var bench) == true) cameras["bench"] = bench;
        return value with { Speed = profile.Speed, Step = profile.Step, Tolerance = profile.Tolerance, RobotId = profile.RobotId,
            Gripper = profile.Gripper, Depth = profile.Depth, DepthSerial = profile.DepthSerial, SimulationCameras = cameras };
    }

    public static void ValidateCanAssignment(Preferences value, string id, string canId)
    {
        if (canId.Length > 256 || canId.Any(char.IsControl)) throw new InvalidOperationException("Invalid CAN adapter identity.");
        if (canId.Length > 0 && value.Robots!.Any(p => p.Id != id && p.CanDeviceId == canId))
            throw new InvalidOperationException("That CAN adapter is assigned to another robot. Remove its assignment first.");
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
    private bool CameraFrameReady(string id) => cameras.TryGetValue(id, out var camera) && camera.PublishedFrame is { } frame &&
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMicroseconds / 1000 < 2000;
    private string ScopeWidgetId(string id) => RobotProfileRouting.ScopeEvent(ActiveProfile.Id, id);
    private Widget ScopeRobotWidget(Widget widget) => widget with { Id = ScopeWidgetId(widget.Id), Children = widget.Children?.Select(ScopeRobotWidget).ToArray() };
    private Task Patch(Dictionary<string, JsonObject> patches, CancellationToken cancellation) => engine.PatchAsync(Panel,
        patches.ToDictionary(p => ScopeWidgetId(p.Key), p => p.Value), cancellation);
    private string UnscopeEvent(string id) => RobotProfileRouting.EventForProfile(ActiveProfile.Id, id);

    private Widget RobotSidebar()
    {
        var rows = preferences.Robots!.Select(p => Widget.Button("profiles.select." + p.Id, ProfileLabel(p))).ToArray();
        return Widget.Column("sidebar", Widget.Label("profiles.heading", "Pipers"), Widget.Column("profiles.list", rows),
            Widget.Row("profiles.edit", Widget.Button("profiles.add", "+ Robot") with { Enabled = rows.Length < RobotProfileRouting.MaximumProfiles },
                Widget.Button("profiles.remove", "Remove") with { Enabled = rows.Length > 1 }),
            new Widget("input", "profiles.name") { Text = ActiveProfile.Name, Hint = "Robot name" },
            Widget.Label("profiles.can.label", "CAN adapter"), new Widget("dropdown", "profiles.can") { Options = CanOptions(), Value = CanIndex() },
            Widget.Button("profiles.activate", "Activate assigned CAN"), Widget.Label("profiles.can.status", "Assign a CAN adapter") with { Height = 52 },
            Widget.Label("profiles.pika.label", "Pika serial device"),
            new Widget("input", "profiles.pika") { Text = ActiveProfile.PikaDevice, Hint = "This robot's /dev/serial/by-id/... gripper; blank = unassigned" },
            new Widget("input", "profiles.wsl") { Text = ActiveProfile.WslDistribution, Visible = OperatingSystem.IsWindows(), Hint = "WSL distribution for this robot's Pika" },
            Widget.Label("profiles.mode", "One selected robot controls at a time. Switching holds this session's movement; other arms keep their measured state.") with { Height = 66 },
            Camera("bench", "Shared bench · Lumina")) with { Width = 280 };
    }

    private string ProfileLabel(RobotProfile profile)
    {
        string status = string.IsNullOrEmpty(profile.CanDeviceId) ? "unassigned" : state?["devices"]?.AsArray().Any(d => d?["id"]?.GetValue<string>() == profile.CanDeviceId && d["present"]?.GetValue<bool>() == true) == true ? "connected" : "offline";
        return (profile.Id == preferences.ActiveRobotId ? "● " : "") + profile.Name + " · " + status;
    }

    private int CanIndex() => Array.IndexOf(knownCanIds, ActiveProfile.CanDeviceId) + 1;
    private string[] CanOptions() => new[] { "Unassigned" }.Concat(knownCanIds.Select(id =>
    {
        var device = state?["devices"]?.AsArray().FirstOrDefault(d => d?["id"]?.GetValue<string>() == id);
        return (device?["name"]?.GetValue<string>() ?? "Adapter") + (device?["present"]?.GetValue<bool>() == true ? "" : " (offline)") + " · " + id;
    })).ToArray();

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
            approved = false; linkCommandsEnabled = false; recoveryInProgress = false;
            if (teleoperating && await commands.WaitAsync(0, cancellation))
            {
                try { await PauseTeleoperation(cancellation); }
                finally { commands.Release(); }
            }
            message = "The controller changed CAN selection. This robot is read-only until its assigned CAN bus is activated.";
        }
        // API 1 dropdown choices are constructor-only. Rebuild the view once
        // after inventory/hotplug changes, never on ordinary telemetry updates.
        // Keep the pending change while an input/command owns the UI semaphore.
        if (canInventoryRenderPending && await commands.WaitAsync(0, cancellation))
        {
            try { await Render(cancellation); canInventoryRenderPending = false; }
            finally { commands.Release(); }
        }
    }

    private void AddProfilePatches(Dictionary<string, JsonObject> patches)
    {
        foreach (var profile in preferences.Robots!) patches["profiles.select." + profile.Id] = new() { ["text"] = ProfileLabel(profile) };
        string can = ActiveProfile.CanDeviceId;
        patches["profiles.can.status"] = new() { ["text"] = string.IsNullOrEmpty(can) ? "No CAN adapter assigned · scene and robot cameras can still be configured."
            : snapshotSelected() == can ? "Active CAN · " + can : "Assigned CAN inactive or disconnected · " + can };
        string snapshotSelected() => state?["selected"]?.GetValue<string>() ?? "";
        patches["enable"] = new() { ["enabled"] = !switchingProfile && can.Length > 0 && snapshotSelected() == can };
    }

    private async Task ProfileEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        // An explicit profile edit/selection supersedes the one-time legacy
        // import, even if controller feedback has not arrived yet.
        legacyImportPending = false;
        if (action == "name")
        {
            var profile = ActiveProfile with { Name = RobotProfileRouting.CleanName(value.GetString() ?? "") };
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray() };
            await Save(cancellation); return;
        }
        if (action is "pika" or "wsl")
        {
            var profile = action == "pika" ? ActiveProfile with { PikaDevice = value.GetString()?.Trim() ?? "" } : ActiveProfile with { WslDistribution = value.GetString()?.Trim() ?? "" };
            PikaProcessService.ValidateConfiguration(profile.PikaDevice, OperatingSystem.IsWindows() ? profile.WslDistribution : null);
            if (profile.PikaDevice.Length > 0 && preferences.Robots!.Any(p => p.Id != profile.Id && p.PikaDevice == profile.PikaDevice && p.WslDistribution == profile.WslDistribution))
                throw new InvalidOperationException("That Pika serial device is assigned to another robot.");
            await ReleaseProfileControl(cancellation);
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray() };
            await Save(cancellation); message = "Pika assignment saved. Enable this robot to connect its selected gripper."; return;
        }
        if (action == "add")
        {
            if (preferences.Robots!.Length >= RobotProfileRouting.MaximumProfiles) throw new InvalidOperationException("At most eight robots are supported in this workspace.");
            var profile = new RobotProfile(Guid.NewGuid().ToString("N"), "Piper " + (preferences.Robots.Length + 1),
                Gripper: new("", false), Depth: new("", false), CameraSlot: Enumerable.Range(0, RobotProfileRouting.MaximumProfiles).First(i => preferences.Robots.All(p => p.CameraSlot != i)));
            await SwitchProfile(profile, append: true, cancellation); return;
        }
        if (action == "remove")
        {
            if (preferences.Robots!.Length <= 1) throw new InvalidOperationException("Keep at least one robot profile.");
            string removed = ActiveProfile.Id;
            await SwitchProfile(preferences.Robots.First(p => p.Id != removed), false, cancellation);
            preferences = preferences with { Robots = preferences.Robots!.Where(p => p.Id != removed).ToArray() };
            await Save(cancellation); return;
        }
        if (action.StartsWith("select.", StringComparison.Ordinal))
        {
            var profile = RobotProfileRouting.Capture(preferences).Robots!.Single(p => p.Id == action[7..]);
            await SwitchProfile(profile, false, cancellation); return;
        }
        if (action == "activate") { await SwitchProfile(ActiveProfile, false, cancellation); return; }
        if (action == "can")
        {
            int index = value.GetInt32() - 1;
            var choices = knownCanIds;
            if (index < -1 || index >= choices.Length) throw new InvalidOperationException("CAN selection is no longer available.");
            string canId = index < 0 ? "" : choices[index];
            RobotProfileRouting.ValidateCanAssignment(preferences, ActiveProfile.Id, canId);
            await SwitchProfile(ActiveProfile with { CanDeviceId = canId }, false, cancellation);
        }
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
            await Call("/api/command", new JsonObject { ["action"] = "hold", ["owner"] = owner, ["standalone"] = true,
                ["device_id"] = device["id"]!.DeepClone(), ["generation"] = device["generation"]!.DeepClone(), ["request_id"] = snapshot["motion"]!["request_id"]!.DeepClone() }, cancellation);
            long started = Stopwatch.GetTimestamp();
            while (state?["motion"]?["active"]?.GetValue<bool>() == true)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new InvalidOperationException("Waiting for the previous robot to hold. Profile was not switched.");
                await Task.Delay(25, cancellation);
            }
        }
        if (pikaOwned || pikaMonitoring || state?["pika"]?["owner"]?.GetValue<string>() == owner)
            await ReleaseOwnPika(cancellation);
        if (link is not null) await engine.RequestAsync("robot.unsubscribe", cancellation: cancellation);
        teleoperating = false; followInput.End(); followRequest = lease = lastCommand = 0;
        approved = pikaOwned = pikaMonitoring = linkCommandsEnabled = recoveryInProgress = false; link = null; linkErrorDegrees = null;
    }

    private async Task ReleaseOwnPika(CancellationToken cancellation)
    {
        if (state?["pika"]?["owner"]?.GetValue<string>() != owner)
        {
            pikaOwned = pikaMonitoring = false;
            return; // Never disable or disconnect a foreign client's agent.
        }
        if (!PikaMatchesProfile()) throw new InvalidOperationException("Pika assignment changed outside this robot profile. Reconnect its original assignment before switching.");
        if (pikaOwned || state?["pika"]?["power_requested"]?.GetValue<bool>() == true)
        {
            await Call("/api/pika/disable", new JsonObject { ["owner"] = owner }, cancellation);
            long started = Stopwatch.GetTimestamp();
            while (state?["pika"]?["fresh"]?.GetValue<bool>() != true || state["pika"]?["enabled"]?.GetValue<bool>() != false)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new InvalidOperationException("Pika disable is not verified. Keep this robot selected and restore its gripper connection before switching.");
                await Task.Delay(25, cancellation);
            }
        }
        // A read-only monitor never armed its agent. EOF/disconnect therefore
        // releases serial ownership without turning off externally enabled Pika.
        await Call("/api/pika/disconnect", new JsonObject { ["owner"] = owner }, cancellation);
        pikaOwned = pikaMonitoring = false;
        pikaMonitorStatus = "Pika telemetry starts read-only when the scene is linked.";
    }

    private async Task SwitchProfile(RobotProfile profile, bool append, CancellationToken cancellation)
    {
        if (calibrationWorker is { IsCompleted: false }) throw new InvalidOperationException("Finish camera setup/calibration before switching robots.");
        await FinishAlignment("Robot profile changed", cancellation);
        switchingProfile = true;
        try
        {
            await ReleaseProfileControl(cancellation);
            preferences = RobotProfileRouting.Capture(preferences);
            foreach (string id in new[] { "gripper", "depth" }) if (cameras.Remove(id, out var camera)) await camera.DisposeAsync();
            var profiles = append ? preferences.Robots!.Append(profile).ToArray() : preferences.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray();
            preferences = RobotProfileRouting.Activate(preferences with { Robots = profiles }, profile.Id);
            LoadAlignmentSummary();
            cameraCalibration.Clear(); cameraDimensions.TryRemove("gripper", out _); cameraDimensions.TryRemove("depth", out _);
            cameraStatus["gripper"] = cameraStatus["depth"] = "Connecting…";
            calibrationSession = null; calibrationLastSequence = 0; calibrationStatus = "No lens calibration. Camera mount remains unverified.";
            await LoadCameraCalibration(cancellation);
            try
            {
                scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
                Materialize(scene);
                await DiscoverSimulationCameras(cancellation);
            }
            catch (PluginRequestException error) { System.Diagnostics.Trace.TraceWarning("Profile scene discovery: {0}", error.Message); }
            await Save(cancellation);
            string? selectionError = null;
            if (profile.CanDeviceId.Length > 0 && state?["selected"]?.GetValue<string>() != profile.CanDeviceId)
            {
                try { await Call("/api/select", new JsonObject { ["device_id"] = profile.CanDeviceId, ["owner"] = owner }, cancellation); }
                catch (InvalidOperationException error) { selectionError = error.Message; }
            }
            foreach (string id in new[] { "gripper", "depth" }) await RestartCamera(id);
            message = selectionError is null ? profile.Name + " selected. Controls require fresh feedback and Enable robot. Shared bench camera stays running."
                : profile.Name + " selected · CAN unavailable: " + selectionError;
        }
        finally { switchingProfile = false; }
    }

    private bool PikaMatchesProfile() => ActiveProfile.PikaDevice.Length > 0 && state?["pika"]?["device"]?.GetValue<string>() == ActiveProfile.PikaDevice &&
        (!OperatingSystem.IsWindows() || state?["pika"]?["distribution"]?.GetValue<string>() == ActiveProfile.WslDistribution);

    private async Task ConfigureProfilePika(CancellationToken cancellation)
    {
        if (ActiveProfile.PikaDevice.Length == 0 || PikaMatchesProfile()) return;
        if (state?["motion"]?["active"]?.GetValue<bool>() == true) throw new InvalidOperationException("Finish arm movement before changing the Pika assignment.");
        await Call("/api/pika/configure", new JsonObject { ["owner"] = owner, ["device"] = ActiveProfile.PikaDevice,
            ["distribution"] = OperatingSystem.IsWindows() ? ActiveProfile.WslDistribution : null }, cancellation);
        long started = Stopwatch.GetTimestamp();
        while (!PikaMatchesProfile())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 2) throw new InvalidOperationException("Waiting for the robot's Pika assignment to be confirmed.");
            await Task.Delay(25, cancellation);
        }
    }
}
