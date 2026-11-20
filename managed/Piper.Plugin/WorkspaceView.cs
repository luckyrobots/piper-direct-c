using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private static Widget Info(string id, string hint) => Widget.Button(id, "") with { Width = 30, Icon = "info", Appearance = "quiet", Hint = hint };
    /// <summary>The one way into a robot's mounting calibration, identical wherever it appears.</summary>
    private static Widget CalibrationLink(string from) => Widget.Button("workspace.calibration@" + from, "Mounting calibration") with
        { Width = 210, Icon = "crosshair", Hint = "Opens this robot's guided mounting calibration. It never moves the arm." };

    /// <summary>A small capital heading, like the section titles of the editor's own panels.</summary>
    /// <summary>A question the operator must answer before something happens: the sentence on its own
    /// line and the answers beneath it. Side by side, a long sentence pushes the buttons past the
    /// edge of a narrow panel, where the one that matters cannot be seen.</summary>
    internal static Widget Confirmation(string id, Widget text, params Widget[] answers) =>
        Widget.Column(id, text, Widget.Row(id + ".answers", answers)) with { Padding = 12, Spacing = 8, Appearance = "panel" };

    /// <summary>A coloured status dot joined to its text, so the pair reads as one thing.
    /// Patch the dot's appearance by <paramref name="dotId"/> and the text by its own id.</summary>
    internal static Widget StatusBadge(string dotId, string tone, Widget text) => Widget.Row(dotId + "@badge",
        Widget.Label(dotId, "●") with { Width = 10, Height = text.Height, Appearance = tone }, text) with { Spacing = 3 };

    private static Widget Section(string id, string title) => Widget.Label("section." + id, title) with { Appearance = "muted", Height = 22 };

    private Widget PreferenceControl(string id, string title, double value, double minimum, double maximum, double step, string help) =>
        // One line per setting, like the joint rows above it: every part but the slider has a fixed
        // width, so a narrow panel shortens the slider instead of squeezing a title to one letter a line.
        Widget.Row(id + ".control", Widget.Label(id + ".label", title) with { Width = 74, Height = 28 },
            Info("workspace.info." + id, help),
            Widget.Slider(id, value, minimum, maximum, step) with { Hint = help },
            Widget.Label(id + ".value", id == "speed" ? $"{value:F0}%" : $"{value:F2}°") with { Width = 56, Height = 28, Appearance = "numeric" },
            Widget.Label(id + ".limits", id == "speed" ? "Waiting for reported speed limits" : $"{minimum:0.##}° — {maximum:0.##}°") with { Width = 200, Height = 28, Appearance = "muted", Hint = help }) with { Spacing = 8 };

    private static Widget Field(string id, string title, Widget input) => Widget.Column(id + ".field", Widget.Label(id + ".caption", title) with { Appearance = "muted" }, input) with { Spacing = 6 };

    private Widget RecordingPage() => Widget.Column("workspace.recording.body",
        Widget.Label("workspace.recording.title", "Before the first recording") with { Appearance = "heading" },
        Widget.Label("workspace.recording.subtitle", "Verify the measurements and streams that will be used in the dataset.") with { Appearance = "muted" },
        Widget.Label("workspace.recording.joints", "Joint & gripper feedback · waiting for live readings") with { Height = 34 },
        Widget.Row("workspace.recording.base", Widget.Label("workspace.recording.base.status", "Tool & base · physical calibration required") with { Height = 36 }, CalibrationLink("recording")),
        Widget.Row("workspace.recording.cameras", Widget.Label("workspace.recording.cameras.status", "Camera lenses & mounts · not verified") with { Height = 36 }, Info("workspace.recording.camera-info", "A base alignment does not establish the camera-to-tool/world transforms or image correspondence.")),
        Widget.Row("workspace.recording.timing", Widget.Label("workspace.recording.timing.status", "Camera-to-joint timing · not measured") with { Height = 36 }, Info("workspace.recording.time-info", "Preview pipe age excludes exposure delay. Keep exposure/receipt clocks, delay and dropped-sample evidence in each recording.")),
        Widget.Button("workspace.recording.unavailable", "Recording setup pending") with { Enabled = false },
        Widget.Button("workspace.replay", "Review a simulation episode for physical replay") with { Width = 350 },
        Widget.Label("workspace.recording.note", "Use the engine's recorder after the required calibration and timing checks. This page does not start a recording or claim synchronized data.") with { Height = 48, Appearance = "muted" }) with { Spacing = 14 };

    private void AddWorkspacePatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        string fullStatus = calibrationOpen ? "Calibration reads the measured pose. Use the physical teach button only when the guide asks."
            : device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? device["range_recovery"]!["reason"]?.GetValue<string>() ?? message : message;
        patches["status"] = new() { ["text"] = device is null ? "Waiting for fresh controller feedback" : device["recovery_required"]?.GetValue<bool>() == true ? Navigation.Page == WorkspacePage.Controls ? "Stopped · follow the recovery steps above" : "Stopped · open Controls to recover" : fullStatus.Length > 110 ? fullStatus[..107] + "…" : fullStatus, ["hint"] = fullStatus };
        var power = RobotPowerState.Inspect(device);
        var teachingMode = TeachingState.Inspect(device);
        patches["workspace.power"] = new() { ["text"] = power.Label, ["hint"] = power.AllEnabled
            ? "All six motors report enabled. " + (teachingMode.BlocksTargets ? teachingMode.Label + ". " + teachingMode.NextStep : approved ? "This session can use the controls." : "Use controls to acknowledge the workspace for this session.")
            : "Motor power comes from fresh feedback, independently of this session's control permission." };
        patches["link.status"] = new() { ["text"] = link is null ? "Not linked" : teleoperating ? "Leader target + measured ghost" : "Measured pose linked", ["hint"] = link is null ? "Link the selected scene and physical robot" : $"Joint error {linkErrorDegrees?.ToString("F4") ?? "—"}° · {link.GripperStatus} · {sceneTrackingStatus}. Physical, camera and time calibration require separate verification." };
        patches["speed.value"] = new() { ["text"] = $"{preferences.Speed:F0}%" };
        patches["step.value"] = new() { ["text"] = $"{preferences.Step:F1}°" };
        patches["tolerance.value"] = new() { ["text"] = $"{preferences.Tolerance:F2}°" };
        bool limits = device?["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["limits_fresh"]?.GetValue<bool>() == true &&
            j["max_speed_rad_s"] is JsonValue speed && speed.TryGetValue<double>(out double maximum) && double.IsFinite(maximum) && maximum > 0);
        double ceiling = limits ? device!["joints"]!.AsArray().Min(j => j!["max_speed_rad_s"]!.GetValue<double>()) * 180 / Math.PI * preferences.Speed / 100 : 0;
        patches["speed.limits"] = new() { ["text"] = limits ? $"{ceiling:F2}°/s ceiling" : "Waiting for reported limits" };
        patches["workspace.recording.joints"] = new() { ["text"] = device is null ? "Joint feedback · unavailable" : PikaMatchesProfile() && state?["pika"]?["fresh"]?.GetValue<bool>() != true ? "Joint feedback fresh · gripper feedback unavailable" : "Measured feedback available · physical agreement still to verify" };
        foreach (string id in new[] { "bench", "depth", "gripper" })
        {
            bool ready = CameraFrameReady(id);
            string? cameraStatus = calibration.Capture.Status(id);
            // A raw driver message tells an operator nothing to do; keep it in the tooltip.
            string shown = cameraStatus is null ? "Connecting camera…" : cameraStatus.StartsWith("Camera unavailable", StringComparison.Ordinal)
                ? "Camera not responding. Check its USB cable; retrying." : cameraStatus;
            patches["camera." + id + ".empty"] = new() { ["visible"] = !ready, ["text"] = shown, ["hint"] = cameraStatus ?? shown };
            patches["camera." + id + ".title"] = new() { ["hint"] = calibration.Capture.Status(id) ?? "Waiting for a fresh frame" };
        }
        if (calibrationOpen && calibrationWizard is { } wizard && wizardProfile == ActiveProfile.Id)
        {
            AddCalibrationSheetPatches(patches, device);
            patches["wizard.camera.image"] = new() { ["visible"] = CameraFrameReady(wizard.HelpCamera) };
        }
        AddReplayPatches(patches, device);
    }
}
