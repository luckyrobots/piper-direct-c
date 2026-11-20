using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record RobotPowerState(int? EnabledCount)
{
    public bool AllEnabled => EnabledCount == 6;
    public string Label => EnabledCount switch { null => "Power unavailable", 6 => "Enabled", 0 => "Disabled", var count => $"{count}/6 enabled" };

    public static RobotPowerState Inspect(JsonObject? device)
    {
        if (device?["fresh"]?.GetValue<bool>() != true || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["enabled"] is not JsonValue value || !value.TryGetValue<bool>(out _)))
            return new((int?)null);
        return new(joints.Count(j => j!["enabled"]!.GetValue<bool>()));
    }
}

/// <summary>UI events belong to a particular rendered page and robot; stop remains available across page changes.</summary>
public static class WorkspaceEventScope
{
    private static bool Persistent(string id) => id.StartsWith(RobotProfileRouting.RobotStopPrefix, StringComparison.Ordinal) || id is "stop" or "disable" or "recovery.guide.cancel" or "motion.hold" or "simulation.stop" or "group.follow.cancel" || id.StartsWith("engine.", StringComparison.Ordinal);
    public static string Wrap(long epoch, string profileId, string id) => Persistent(id)
        ? RobotProfileRouting.ScopeEvent(profileId, id)
        : "view." + epoch.ToString(CultureInfo.InvariantCulture) + "." + RobotProfileRouting.ScopeEvent(profileId, id);

    public static string Unwrap(long epoch, string profileId, string wireId)
    {
        string id;
        if (wireId.StartsWith("view.", StringComparison.Ordinal))
        {
            string prefix = "view." + epoch.ToString(CultureInfo.InvariantCulture) + ".";
            if (!wireId.StartsWith(prefix, StringComparison.Ordinal)) return "";
            id = RobotProfileRouting.EventForProfile(profileId, wireId[prefix.Length..]);
            return id.StartsWith("view.", StringComparison.Ordinal) || Persistent(id) ? "" : id;
        }
        id = RobotProfileRouting.EventForProfile(profileId, wireId);
        return Persistent(id) ? id : "";
    }
}

public static class WorkspaceCommandPolicy
{
    public static bool RequiresControls(string id) => id is "enable" or "safety.accept" or "teleoperate.toggle" or "zero.all" or "recovery.home" or "pika.target" or "pika.minus" or "pika.plus" or "pika.zero" or "engine.robot.target" or "engine.robot.follow" ||
        id.StartsWith("joint.", StringComparison.Ordinal) || id.StartsWith("teleoperate.joint.", StringComparison.Ordinal) || id is "simulation.toggle" or "simulation.match";
}

/// <summary>Plugin-1 workspace navigation, one immutable value per published view.
/// States: Controls · Recording · Replay · Camera(detail, return page) ·
/// Settings(category, settings camera). Calibration is not a page: it is open
/// exactly while bench settings, or a camera enlarged from them, are visible.
/// <see cref="Navigate"/> is the only transition; <see cref="WorkspaceComponent"/>
/// holds the committed value, adopted inside the view publication gate. It is a
/// reference type so a reader on another thread sees one whole page.</summary>
internal sealed record WorkspaceNavigation(WorkspacePage Page, SettingsCategory Category,
    string SettingsCamera, string CameraDetail, WorkspacePage CameraReturn)
{
    public static readonly WorkspaceNavigation Initial = new(WorkspacePage.Controls, SettingsCategory.Robot, "bench", "bench", WorkspacePage.Controls);
    public static readonly WorkspaceNavigation Calibration = Initial with { Page = WorkspacePage.Settings, Category = SettingsCategory.Bench };

    public bool CalibrationOpen => Category == SettingsCategory.Bench &&
        (Page == WorkspacePage.Settings || Page == WorkspacePage.Camera && CameraReturn == WorkspacePage.Settings);

    public static WorkspaceNavigation At(WorkspacePage page, SettingsCategory category = SettingsCategory.Robot) =>
        Initial with { Page = page, Category = category };

    private static bool IsCamera(string id) => id is "bench" or "depth" or "gripper";

    public WorkspaceNavigation Navigate(string action)
    {
        if (CameraReturn == WorkspacePage.Camera)
        {
            // A camera detail page cannot return to itself: Back would trap the
            // operator there. Repair the return page to Controls and carry on.
            Trace.TraceError("Workspace navigation held a camera page as its own return page; returning to Controls instead.");
            return (this with { CameraReturn = WorkspacePage.Controls }).Navigate(action);
        }
        if (action.StartsWith("settings.", StringComparison.Ordinal))
        {
            string[] parts = action[9..].Split('.');
            var category = parts[0] switch
            {
                "cameras" => SettingsCategory.Cameras, "bench" => SettingsCategory.Bench,
                "diagnostics" => SettingsCategory.Diagnostics, _ => SettingsCategory.Robot
            };
            string camera = parts.Length > 1 && category == SettingsCategory.Cameras && IsCamera(parts[1]) ? parts[1] : SettingsCamera;
            // The bench camera has one page, with its settings under its picture.
            if (category == SettingsCategory.Cameras && parts.Length > 1 && camera == "bench")
                return this with { Page = WorkspacePage.Camera, CameraDetail = "bench", CameraReturn = Page == WorkspacePage.Camera ? CameraReturn : Page };
            return this with { Page = WorkspacePage.Settings, Category = category, SettingsCamera = camera };
        }
        if (action.StartsWith("camera.", StringComparison.Ordinal))
        {
            string selected = action[7..];
            if (!IsCamera(selected)) throw new InvalidOperationException("Unknown camera.");
            return this with { Page = WorkspacePage.Camera, CameraDetail = selected, CameraReturn = Page == WorkspacePage.Camera ? CameraReturn : Page };
        }
        return action switch
        {
            "camera-back" => this with { Page = CameraReturn },
            "controls" => this with { Page = WorkspacePage.Controls },
            "recording" => this with { Page = WorkspacePage.Recording },
            "replay" => this with { Page = WorkspacePage.Replay },
            "calibration" => this with { Page = WorkspacePage.Settings, Category = SettingsCategory.Bench },
            "settings" => this with { Page = WorkspacePage.Settings },
            "overview" => this with { Page = WorkspacePage.Overview },
            "follow" => this with { Page = WorkspacePage.Follow },
            // A robot's own cameras: never the shared bench camera, which has its own page.
            "robot-cameras" => this with { Page = WorkspacePage.Settings, Category = SettingsCategory.Cameras, SettingsCamera = SettingsCamera == "bench" ? "gripper" : SettingsCamera },
            _ => throw new InvalidOperationException("Unknown workspace page.")
        };
    }
}

internal enum WorkspacePage { Controls, Recording, Replay, Camera, Settings, Overview, Follow }

/// <summary>How the workspace is laid out. The left sidebar is the only navigation: a robot, a bench
/// page or a settings page. A robot's page has tabs for what belongs to that one arm; a bench page
/// belongs to no single robot and has none.</summary>
internal static class WorkspaceTabs
{
    // Setup comes last: the tabs you work in first, configuration at the end.
    public static readonly string[] RobotTabs = ["Controls", "Cameras", "Recording", "Setup"];
    private static readonly string[] RobotTabActions = ["controls", "robot-cameras", "recording", "settings.robot"];
    /// <summary>The camera switch on a robot's Cameras tab, in the order of its actions below.</summary>
    public static readonly string[] RobotCameras = ["Gripper fisheye", "D405 wrist camera"];
    private static readonly string[] RobotCameraActions = ["settings.cameras.gripper", "settings.cameras.depth"];

    /// <summary>A page about the whole bench: the overview, following, the shared camera and the arm server.</summary>
    public static bool BenchPage(WorkspaceNavigation navigation) => navigation.Page switch
    {
        WorkspacePage.Overview or WorkspacePage.Follow => true,
        WorkspacePage.Camera => navigation.CameraDetail == "bench",
        WorkspacePage.Settings => navigation.Category == SettingsCategory.Diagnostics ||
            navigation.Category == SettingsCategory.Cameras && navigation.SettingsCamera == "bench",
        _ => false
    };

    /// <summary>The robot tab a page sits under: a replay belongs to Recording, calibration to Setup,
    /// an enlarged wrist camera to Cameras.</summary>
    public static int PageIndex(WorkspaceNavigation navigation) => navigation.Page switch
    {
        WorkspacePage.Recording or WorkspacePage.Replay => 2,
        WorkspacePage.Camera => 1,
        WorkspacePage.Settings => navigation.Category == SettingsCategory.Cameras ? 1 : 3,
        _ => 0
    };

    /// <summary>Turns a tab's change event into its navigation action; any other workspace action passes through.</summary>
    public static string Action(string action, System.Text.Json.JsonElement value)
    {
        string[]? actions = action switch { "pages" => RobotTabActions, "robot-camera" => RobotCameraActions, _ => null };
        if (actions is null) return action;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Number || !value.TryGetInt32(out int index) || index < 0 || index >= actions.Length)
            throw new InvalidOperationException("Unknown workspace tab.");
        return actions[index];
    }
}
internal enum SettingsCategory { Robot, Cameras, Bench, Diagnostics }

public sealed partial class PipersPlugin
{
    /// <summary>The Workspace component; this shell reads it and asks it for transitions.</summary>
    internal readonly WorkspaceComponent workspace = new();
    private WorkspaceNavigation Navigation => workspace.Navigation;
    private bool calibrationOpen => Navigation.CalibrationOpen;
    private string settingsCamera => Navigation.SettingsCamera;
    private string cameraDetail => Navigation.CameraDetail;
    private Task? wizardHighlightMaintenance;

    internal async Task WorkspaceEvent(string action, CancellationToken cancellation)
    {
        action = action.Split('@', 2)[0];
        if (action == "link-info" || action.StartsWith("info.", StringComparison.Ordinal) || action is "recording.camera-info" or "recording.time-info") return;
        if (action == "connection-details") { workspace.ToggleConnectionDetails(); await Render(cancellation); return; }
        if (action == "camera-stream-details") { workspace.ToggleCameraStreamDetails(); await Render(cancellation); return; }
        if (action == "camera-lens-details") { workspace.ToggleCameraLensDetails(); await Render(cancellation); return; }
        var next = Navigation.Navigate(action);
        bool bench = next.Page == WorkspacePage.Settings && next.Category == SettingsCategory.Bench;
        if (bench && GroupFollowActive)
            throw new InvalidOperationException("Stop following before opening bench calibration. No follower targets were changed.");

        // End a viewport gesture when hiding its controls. Browsing settings
        // does not revoke a healthy robot session or interrupt an episode.
        if (next.Page != Navigation.Page && (teleoperating || followRequest != 0)) await PauseTeleoperation(cancellation);
        if (bench && !calibrationOpen) await MakeCalibrationReadOnly(cancellation);
        if (calibrationOpen && !next.CalibrationOpen)
        {
            await StopHeightCheck(cancellation);
            await ClearWizardHighlight(cancellation);
        }
        if (passiveMeasuredTrackingStarted && next is { Page: WorkspacePage.Settings, Category: SettingsCategory.Robot } &&
            (pikaInventoryAt == 0 || Stopwatch.GetElapsedTime(pikaInventoryAt).TotalSeconds > 5))
            await RefreshPikaInventory(cancellation);
        await PublishWorkspaceView(cancellation, next);
    }

    private void QueueWizardHighlight()
    {
        if (wizardHighlightMaintenance is { IsCompleted: false }) return;
        wizardHighlightMaintenance = MaintainWizardHighlight(shutdown.Token);
    }

    private static double FiniteControlNumber(JsonElement value, string label)
    {
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number)) throw new InvalidOperationException(label + " must be a finite number.");
        return number;
    }
}
