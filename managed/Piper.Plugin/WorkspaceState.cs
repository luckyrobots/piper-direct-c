using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>UI events belong to a particular rendered page and robot; stop remains available across page changes.</summary>
public static class WorkspaceEventScope
{
    private static bool Persistent(string id) => id is "stop" or "motion.hold" or "teaching.reset.abort" || id.StartsWith("engine.", StringComparison.Ordinal);
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
    public static bool RequiresControls(string id) => id is "enable" or "safety.accept" or "teleoperate.toggle" or "zero.all" or "pika.target" or "pika.minus" or "pika.plus" or "pika.zero" or "engine.robot.target" or "engine.robot.follow" ||
        id.StartsWith("joint.", StringComparison.Ordinal) || id.StartsWith("teleoperate.joint.", StringComparison.Ordinal);
}

public sealed partial class PipersPlugin
{
    private string workspacePage = "controls", settingsCategory = "robot", cameraDetail = "bench", cameraReturnPage = "controls";
    private long workspaceViewEpoch;
    private Task? wizardHighlightMaintenance;
    private bool connectionDetailsOpen, cameraStreamDetailsOpen, cameraLensDetailsOpen, legacyAlignmentOpen;

    private async Task WorkspaceEvent(string action, CancellationToken cancellation)
    {
        action = action.Split('@', 2)[0];
        if (action == "link-info" || action.StartsWith("info.", StringComparison.Ordinal) || action is "recording.camera-info" or "recording.time-info") return;
        if (action == "connection-details") { connectionDetailsOpen = !connectionDetailsOpen; await Render(cancellation); return; }
        if (action == "camera-stream-details") { cameraStreamDetailsOpen = !cameraStreamDetailsOpen; await Render(cancellation); return; }
        if (action == "camera-lens-details") { cameraLensDetailsOpen = !cameraLensDetailsOpen; await Render(cancellation); return; }
        if (action == "legacy-alignment") { legacyAlignmentOpen = !legacyAlignmentOpen; await Render(cancellation); return; }
        string page = action;
        if (action.StartsWith("settings.", StringComparison.Ordinal))
        {
            string[] parts = action[9..].Split('.'); settingsCategory = parts[0];
            if (parts.Length > 1 && parts[0] == "cameras" && parts[1] is "bench" or "depth" or "gripper") settingsCamera = parts[1];
            page = "settings";
        }
        if (action.StartsWith("camera.", StringComparison.Ordinal))
        {
            string selected = action[7..];
            if (selected is not ("bench" or "depth" or "gripper")) throw new InvalidOperationException("Unknown camera.");
            cameraReturnPage = workspacePage == "camera" ? cameraReturnPage : workspacePage;
            cameraDetail = selected; page = "camera";
        }
        if (action == "camera-back") page = cameraReturnPage;
        if (page is not ("controls" or "calibration" or "settings" or "recording" or "camera")) throw new InvalidOperationException("Unknown workspace page.");
        if (settingsCategory is not ("robot" or "cameras" or "controller" or "diagnostics")) settingsCategory = "robot";
        if (page != workspacePage)
        {
            // Navigation is explicit. Finish this session's viewport gesture before
            // hiding its controls, then discard all permission from the old page.
            if (teleoperating || followRequest != 0) await PauseTeleoperation(cancellation);
            else if (lease != 0 && state?["motion"]?["active"]?.GetValue<bool>() == true && state["motion"]?["owner"]?.GetValue<string>() == owner)
                await Command("hold", new JsonObject { ["request_id"] = state["motion"]!["request_id"]!.DeepClone() }, cancellation);
            if (page == "calibration") await MakeCalibrationReadOnly(cancellation);
            if (page != "controls")
            {
                Interlocked.Increment(ref targetPermissionEpoch);
                approved = linkCommandsEnabled = approvalBeforeTeaching = false;
                PauseSceneFollowing();
            }
            bool nextCalibration = page == "calibration" || page == "camera" && cameraReturnPage == "calibration";
            if (calibrationOpen && !nextCalibration) await ClearWizardHighlight(cancellation);
            workspacePage = page;
            calibrationOpen = page == "calibration" || page == "camera" && cameraReturnPage == "calibration";
            alignmentOpen = false;
            teachingReturnConfirmation = false;
        }
        backendSetupOpen = workspacePage == "settings" && settingsCategory == "controller";
        await Render(cancellation);
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

    private string CompactConnectionStatus(JsonObject? device) => device is null ? "Waiting for feedback"
        : device["recovery_required"]?.GetValue<bool>() == true ? "Recovery required"
        : device["range_recovery"]?["required"]?.GetValue<bool>() == true ? "Range entry needed"
        : TeachingState.Inspect(device).HandGuiding ? "Hand guiding"
        : TeachingState.Inspect(device).Active ? "Onboard playback"
        : "Connected";
}
