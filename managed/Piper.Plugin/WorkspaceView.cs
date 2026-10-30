using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private static Widget Info(string id, string hint) => Widget.Button(id, "(i)") with { Width = 28, Appearance = "quiet", Hint = hint };
    private Widget Nav(string page, string text) => Widget.Button("workspace." + page, text) with { Appearance = workspacePage == page ? "selected" : "quiet", Width = 118 };

    private Task Render(CancellationToken cancellation)
    {
        Interlocked.Increment(ref workspaceViewEpoch);
        Widget body = workspacePage switch
        {
            "calibration" => GuidedCalibrationControls(),
            "settings" => SettingsPage(),
            "recording" => RecordingPage(),
            "camera" => CameraDetailPage(),
            _ => ControlsPage()
        };
        var content = Widget.Column("controls",
            Widget.Row("header",
                Widget.Label("workspace.robot-name", ActiveProfile.Name) with { Appearance = "heading", Width = 190, Height = 32, Hint = ActiveProfile.Name },
                Widget.Label("workspace.connection", "Connecting…") with { Width = 155, Height = 28 },
                Widget.Button("enable", "Enable robot") with { Appearance = "primary", Width = 132, Hint = "Enable arm and assigned Pika controls after one workspace-clear confirmation." },
                Widget.Button("disable", "Disable") with { Width = 90, Hint = "Disables motor holding torque. Support the arm before using this action." },
                Widget.Button("stop", "STOP") with { Appearance = "danger", Width = 88 }),
            Widget.Row("workspace.tabs", Nav("controls", "Controls"), Nav("calibration", "Calibration"), Nav("recording", "Recording"), Nav("settings", "Settings")) with { Spacing = 6 },
            Widget.Row("safety", Widget.Label("safety.text", "Make sure the workspace is clear and safe to move."),
                Widget.Button("safety.accept", "Enable controls") with { Appearance = "primary" }, Widget.Button("safety.cancel", "Cancel")) with { Visible = false, Padding = 12, Appearance = "panel" },
            Widget.Column("workspace.incident", Widget.Label("incident", "") with { Height = 36 }, Widget.Label("recovery.next", "") with { Height = 46 },
                Widget.Row("recovery", Widget.Button("recovery.run", "Clear & support arm — recover") with { Appearance = "danger" })) with { Visible = false, Padding = 12, Appearance = "panel" },
            body,
            Widget.Label("status", "Connecting…") with { Height = 28, Appearance = "muted" }) with { Padding = 18, Spacing = 14 };
        return engine.PublishViewAsync(Panel, ScopeRobotWidget(Widget.Row("workspace", RobotSidebar(), content) with { Spacing = 0 }), cancellation);
    }

    private Widget ControlsPage()
    {
        var widgets = new List<Widget>
        {
            Widget.Row("scene.binding", Widget.Button("link", link is null ? "Link robot" : "Unlink robot") with { Width = 122, Appearance = "selected" },
                Widget.Label("link.status", "Measured scene tracking") with { Height = 28 },
                Info("workspace.link-info", "Linking displays measured joints in the scene. Teleoperate deliberately sends scene targets to hardware. It does not establish physical, camera or timestamp calibration.")),
            Widget.Row("teleoperate", Widget.Button("teleoperate.toggle", "Start Teleoperate") with { Width = 165 },
                Widget.Label("teleoperate.hint", "Viewport joint") with { Width = 105, Appearance = "muted" },
                Widget.Row("teleoperate.joints", Enumerable.Range(1, 6).Select(i => Widget.Button("teleoperate.joint." + i, "J" + i) with { Width = 38, Enabled = false }).ToArray()) with { Spacing = 4 }),
            Widget.Row("cameras", Camera("depth", "D405"), Camera("gripper", "Pika fisheye")) with { Spacing = 16 },
            Widget.Row("pika", Widget.Label("pika.name", "Pika") with { Width = 38 },
                Widget.Label("pika.value", "— mm") with { Width = 112, Appearance = "numeric", Hint = "Measured opening. Pika is enabled with the robot." },
                Widget.Button("pika.minus", "−") with { Width = 32, Hint = "Close by 1 mm" },
                Widget.Slider("pika.target", 0, 0, 90, .1) with { Enabled = false, Hint = "Absolute gripper opening in millimetres" },
                Widget.Button("pika.plus", "+") with { Width = 32, Hint = "Open by 1 mm" }, Widget.Button("pika.zero", "Zero") with { Width = 48 }),
            Widget.Row("preferences", PreferenceControl("speed", "Speed", preferences.Speed, 0, 100, 1,
                    "Percentage of reported motor speed limits. The °/s value is the target ceiling, not measured speed."),
                PreferenceControl("step", "Jog step", preferences.Step, .1, 5, .1,
                    "Degrees added by each −/+ press. Joint sliders select absolute angles."),
                PreferenceControl("tolerance", "Tolerance", preferences.Tolerance, .05, 1, .05,
                    "Arrival tolerance in degrees. It does not define physical calibration accuracy.")) with { Spacing = 18 },
            Widget.Row("joint.headings", Widget.Label("joint.name.heading", "Joint") with { Width = 30, Appearance = "muted" },
                Widget.Label("joint.measured.heading", "Measured °") with { Width = 94, Appearance = "muted" },
                Widget.Label("joint.current.heading", "Current A") with { Width = 74, Appearance = "muted" }, Widget.Label("joint.target.heading", "Absolute target · robot-reported limits") with { Appearance = "muted" })
        };
        for (int i = 1; i <= 6; i++)
            widgets.Add(Widget.Row("joint." + i,
                Widget.Label($"joint.{i}.name", "J" + i) with { Width = 30 },
                Widget.Label($"joint.{i}.value", "—") with { Width = 94, Height = 30, Appearance = "numeric" },
                Widget.Label($"joint.{i}.current", "—") with { Width = 74, Height = 30, Appearance = "numeric" },
                Widget.Button($"joint.{i}.minus", "−") with { Width = 32 },
                Widget.Slider($"joint.{i}.target", 0, -180, 180, .1) with { Enabled = false },
                Widget.Button($"joint.{i}.plus", "+") with { Width = 32 }, Widget.Button($"joint.{i}.zero", "Zero") with { Width = 48 }) with { Spacing = 8 });
        widgets.Add(Widget.Row("motion", Widget.Button("zero.all", "Move all to zero") with { Width = 160, Hint = "Moves the joints to their zero angles through the controller's validated motion path; does not write encoder zeros." },
            Widget.Label("motion.progress", "Idle") with { Width = 340, Height = 28 }, Widget.Button("motion.hold", "Stop movement") with { Width = 135, Enabled = false }));
        widgets.Add(TeachingControls());
        return Widget.Column("workspace.controls.body", widgets.ToArray()) with { Spacing = 12 };
    }

    private Widget PreferenceControl(string id, string title, double value, double minimum, double maximum, double step, string help) =>
        Widget.Column(id + ".control", Widget.Row(id + ".heading", Widget.Label(id + ".label", title),
            Info("workspace.info." + id, help), Widget.Label(id + ".value", id == "speed" ? $"{value:F0}%" : $"{value:F2}°") with { Width = 74, Appearance = "numeric" }),
            Widget.Slider(id, value, minimum, maximum, step) with { Hint = help },
            Widget.Label(id + ".limits", id == "speed" ? "Waiting for reported speed limits" : $"{minimum:0.##}° — {maximum:0.##}°") with { Height = 24, Appearance = "muted", Hint = help }) with { Spacing = 5 };

    private Widget SettingsPage()
    {
        var robots = scene?["robots"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        Widget section = settingsCategory switch
        {
            "cameras" => CameraSettingsPage(),
            "controller" => Widget.Column("workspace.settings.controller.section", Widget.Label("workspace.settings.controller.title", "Local controller") with { Appearance = "heading" }, BackendSetup()),
            "diagnostics" => Widget.Column("workspace.settings.diagnostics.section", Widget.Label("workspace.settings.diagnostics.title", "Diagnostics & saved reports") with { Appearance = "heading" },
                Widget.Label("link.status", "") with { Height = 58 }, Widget.Label("pika.monitor", pikaMonitorStatus) with { Height = 46 },
                Widget.Button("workspace.legacy-alignment", legacyAlignmentOpen ? "Hide previous visual check" : "Previous visual alignment reports") with { Appearance = "quiet" },
                AlignmentControls() with { Visible = legacyAlignmentOpen }, Widget.Button("workspace.calibration@diagnostics", "Open guided calibration")),
            _ => Widget.Column("workspace.settings.robot.section",
                Widget.Label("workspace.settings.robot.title", "Robot connection") with { Appearance = "heading" },
                Field("profiles.name", "Name", new Widget("input", "profiles.name") { Text = ActiveProfile.Name }),
                Field("profiles.can", "CAN adapter", new Widget("dropdown", "profiles.can") { Options = CanOptions(), Value = CanIndex(), Hint = "Choose this robot's adapter. Device identity is shown under Device information." }),
                Widget.Button("profiles.activate", "Activate assigned CAN") with { Width = 200 },
                Field("robots.select", "Scene robot", new Widget("dropdown", "robots.select") { Options = robots.Select(r => r["name"]!.GetValue<string>()).ToArray(), Value = Math.Max(0, Array.FindIndex(robots, r => r["entityId"]!.GetValue<string>() == preferences.RobotId)) }),
                Widget.Button("robots.refresh", "Refresh scene robots") with { Width = 200 },
                Widget.Row("profiles.edit", Widget.Button("profiles.add", "+ Add robot") with { Enabled = preferences.Robots!.Length < RobotProfileRouting.MaximumProfiles }, Widget.Button("profiles.remove", "Remove selected profile") with { Enabled = preferences.Robots.Length > 1 }),
                Widget.Button("workspace.connection-details", connectionDetailsOpen ? "Hide device information" : "Device information & gripper assignment") with { Appearance = "quiet" },
                Widget.Column("workspace.settings.device-details", Widget.Label("profiles.can.status", "") with { Height = 46 },
                    Field("profiles.pika", "Pika serial device", new Widget("input", "profiles.pika") { Text = ActiveProfile.PikaDevice, Hint = "Stable Linux /dev/serial/by-id path; blank means unassigned." }),
                    Field("profiles.wsl", "WSL distribution", new Widget("input", "profiles.wsl") { Text = ActiveProfile.WslDistribution }) with { Visible = OperatingSystem.IsWindows() },
                    Widget.Label("pika.monitor", pikaMonitorStatus) with { Height = 44 },
                    Widget.Label("profiles.mode", "Switching profiles finishes this session's movement and discards its control permission. Other robot assignments remain intact.") with { Height = 48, Appearance = "muted" }) with { Visible = connectionDetailsOpen })
        };
        Widget Category(string id, string name) => Widget.Button("workspace.settings." + id, name) with { Appearance = settingsCategory == id ? "selected" : "quiet" };
        return Widget.Column("workspace.settings.body", Widget.Label("workspace.settings.title", "Settings") with { Appearance = "heading" },
            Widget.Row("workspace.settings.layout", Widget.Column("workspace.settings.categories", Category("robot", "Robot"), Category("cameras", "Cameras"), Category("controller", "Controller"), Category("diagnostics", "Diagnostics")) with { Width = 150, Spacing = 8 },
                section with { Padding = 12, Spacing = 14 })) with { Spacing = 14 };
    }

    private static Widget Field(string id, string title, Widget input) => Widget.Column(id + ".field", Widget.Label(id + ".caption", title) with { Appearance = "muted" }, input) with { Spacing = 6 };

    private Widget RecordingPage() => Widget.Column("workspace.recording.body",
        Widget.Label("workspace.recording.title", "Before the first recording") with { Appearance = "heading" },
        Widget.Label("workspace.recording.subtitle", "Verify the measurements and streams that will be used in the dataset.") with { Appearance = "muted" },
        Widget.Label("workspace.recording.joints", "Joint & gripper feedback · waiting for live readings") with { Height = 34 },
        Widget.Row("workspace.recording.base", Widget.Label("workspace.recording.base.status", "Tool & base · physical calibration required") with { Height = 36 }, Widget.Button("workspace.calibration@recording", "Calibrate robot")),
        Widget.Row("workspace.recording.cameras", Widget.Label("workspace.recording.cameras.status", "Camera lenses & mounts · not verified") with { Height = 36 }, Info("workspace.recording.camera-info", "A base alignment does not establish the camera-to-tool/world transforms or image correspondence.")),
        Widget.Row("workspace.recording.timing", Widget.Label("workspace.recording.timing.status", "Camera-to-joint timing · not measured") with { Height = 36 }, Info("workspace.recording.time-info", "Preview pipe age excludes exposure delay. Keep exposure/receipt clocks, delay and dropped-sample evidence in each recording.")),
        Widget.Button("workspace.recording.unavailable", "Recording setup pending") with { Enabled = false },
        Widget.Label("workspace.recording.note", "Use the engine's recorder after the required calibration and timing checks. This page does not start a recording or claim synchronized data.") with { Height = 48, Appearance = "muted" }) with { Padding = 14, Spacing = 18 };

    private void AddWorkspacePatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        patches["workspace.connection"] = new() { ["text"] = CompactConnectionStatus(device), ["hint"] = message };
        string fullStatus = calibrationOpen ? "Calibration reads the measured pose. Use the physical teach button only when the guide asks."
            : device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? device["range_recovery"]!["reason"]?.GetValue<string>() ?? message : message;
        patches["status"] = new() { ["text"] = device is null ? "Waiting for fresh controller feedback" : device["recovery_required"]?.GetValue<bool>() == true ? "Stopped · review recovery above" : fullStatus.Length > 110 ? fullStatus[..107] + "…" : fullStatus, ["hint"] = fullStatus };
        patches["workspace.incident"] = new() { ["visible"] = device?["recovery_required"]?.GetValue<bool>() == true || device?["reset_pending"]?.GetValue<bool>() == true };
        patches["enable"] = new() { ["enabled"] = workspacePage == "controls" && device is not null && !switchingProfile && !TeachingState.Inspect(device).BlocksTargets };
        patches["disable"] = new() { ["enabled"] = device is not null && !TeachingState.Inspect(device).BlocksTargets, ["hint"] = TeachingState.Inspect(device).BlocksTargets ? "Finish the physical teaching return before changing power here. STOP remains available." : "Support the arm. This action removes holding torque." };
        patches["link.status"] = new() { ["text"] = link is null ? "Not linked" : teleoperating ? "Leader target + measured ghost" : "Measured pose linked", ["hint"] = link is null ? "Link the selected scene and physical robot" : $"Joint error {linkErrorDegrees?.ToString("F4") ?? "—"}° · {link.GripperStatus} · {sceneTrackingStatus}. Physical, camera and time calibration require separate verification." };
        patches["speed.value"] = new() { ["text"] = $"{preferences.Speed:F0}%" };
        patches["step.value"] = new() { ["text"] = $"{preferences.Step:F1}°" };
        patches["tolerance.value"] = new() { ["text"] = $"{preferences.Tolerance:F2}°" };
        bool limits = device?["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["limits_fresh"]?.GetValue<bool>() == true);
        double ceiling = limits ? device!["joints"]!.AsArray().Min(j => j!["max_speed_rad_s"]!.GetValue<double>()) * 180 / Math.PI * preferences.Speed / 100 : 0;
        patches["speed.limits"] = new() { ["text"] = limits ? $"{ceiling:F2}°/s ceiling" : "Waiting for reported limits" };
        patches["workspace.recording.joints"] = new() { ["text"] = device is null ? "Joint feedback · unavailable" : PikaMatchesProfile() && state?["pika"]?["fresh"]?.GetValue<bool>() != true ? "Joint feedback fresh · gripper feedback unavailable" : "Measured feedback available · physical agreement still to verify" };
        foreach (string id in new[] { "bench", "depth", "gripper" })
        {
            bool ready = CameraFrameReady(id);
            patches["camera." + id + ".empty"] = new() { ["visible"] = !ready, ["text"] = cameraStatus.GetValueOrDefault(id, "Connecting camera…") };
            patches["camera." + id + ".title"] = new() { ["hint"] = cameraStatus.GetValueOrDefault(id, "Waiting for a fresh frame") };
        }
        AddWizardPatches(patches, device);
    }
}
