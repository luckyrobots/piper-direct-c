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
    private static bool Persistent(string id) => id is "stop" or "disable" or "recovery.guide.cancel" or "motion.hold" or "simulation.stop" or "group.follow.cancel" || id.StartsWith("engine.", StringComparison.Ordinal);
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

public sealed partial class PipersPlugin
{
    private string workspacePage = "controls", settingsCategory = "robot", cameraDetail = "bench", cameraReturnPage = "controls";
    private long workspaceViewEpoch;
    private Task? wizardHighlightMaintenance;
    private bool connectionDetailsOpen, cameraStreamDetailsOpen, cameraLensDetailsOpen;

    private async Task WorkspaceEvent(string action, CancellationToken cancellation)
    {
        action = action.Split('@', 2)[0];
        if (action == "link-info" || action.StartsWith("info.", StringComparison.Ordinal) || action is "recording.camera-info" or "recording.time-info") return;
        if (action == "connection-details") { connectionDetailsOpen = !connectionDetailsOpen; await Render(cancellation); return; }
        if (action == "camera-stream-details") { cameraStreamDetailsOpen = !cameraStreamDetailsOpen; await Render(cancellation); return; }
        if (action == "camera-lens-details") { cameraLensDetailsOpen = !cameraLensDetailsOpen; await Render(cancellation); return; }
        string page = action, category = settingsCategory, cameraSettings = settingsCamera;
        string detail = cameraDetail, returnPage = cameraReturnPage;
        if (action.StartsWith("settings.", StringComparison.Ordinal))
        {
            string[] parts = action[9..].Split('.');
            category = parts[0];
            if (parts.Length > 1 && category == "cameras" && parts[1] is "bench" or "depth" or "gripper") cameraSettings = parts[1];
            page = "settings";
        }
        if (action.StartsWith("camera.", StringComparison.Ordinal))
        {
            string selected = action[7..];
            if (selected is not ("bench" or "depth" or "gripper")) throw new InvalidOperationException("Unknown camera.");
            if (workspacePage != "camera") returnPage = workspacePage;
            detail = selected;
            page = "camera";
        }
        if (action == "camera-back") page = returnPage;
        if (page is not ("controls" or "calibration" or "settings" or "recording" or "replay" or "camera")) throw new InvalidOperationException("Unknown workspace page.");
        if (page == "calibration") { page = "settings"; category = "bench"; }
        if (category is not ("robot" or "cameras" or "bench" or "controller" or "diagnostics")) category = "robot";
        bool bench = page == "settings" && category == "bench";
        if (bench && GroupFollowActive)
            throw new InvalidOperationException("Stop following before opening bench calibration. No follower targets were changed.");

        // End a viewport gesture when hiding its controls. Browsing settings
        // does not revoke a healthy robot session or interrupt an episode.
        if (page != workspacePage && (teleoperating || followRequest != 0)) await PauseTeleoperation(cancellation);
        bool nextCalibration = bench || page == "camera" && returnPage == "settings" && category == "bench";
        if (bench && !calibrationOpen) await MakeCalibrationReadOnly(cancellation);
        if (calibrationOpen && !nextCalibration)
        {
            await StopHeightCheck(cancellation);
            await ClearWizardHighlight(cancellation);
        }
        if (passiveMeasuredTrackingStarted && page == "settings" && category == "robot" && (pikaInventoryAt == 0 || Stopwatch.GetElapsedTime(pikaInventoryAt).TotalSeconds > 5))
            await RefreshPikaInventory(cancellation);
        await PublishWorkspaceView(cancellation, () =>
        {
            workspacePage = page;
            settingsCategory = category;
            settingsCamera = cameraSettings;
            cameraDetail = detail;
            cameraReturnPage = returnPage;
            calibrationOpen = nextCalibration;
            backendSetupOpen = page == "settings" && category is "controller" or "diagnostics";
        });
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
