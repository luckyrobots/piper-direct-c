using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private Task Render(CancellationToken cancellation) => PublishWorkspaceView(cancellation);

    private Widget BuildWorkspaceView()
    {
        Widget body = workspacePage switch {
            "settings" => SettingsPage(), "calibration" => CalibrationSheetControls(),
            "recording" => RecordingPage(), "replay" => ReplayPage(), "camera" => CameraDetailPage(), _ => ControlsPage() };
        var content = Widget.Column("controls",
            Widget.Column("header", Widget.Row("header.title", Widget.Label("workspace.robot-name", ActiveProfile.Name) with { Appearance = "heading", Height = 30 },
                Widget.Label("workspace.connection", "Connecting…") with { Height = 30 }),
                Widget.Row("header.actions", Widget.Button("enable", "Enable robot") with { Appearance = "primary" },
                    Widget.Button("disable", "Disable") with { Width = 68, Appearance = "quiet", Hint = "Support the arm before releasing holding torque." },
                    Widget.Button("stop", "STOP") with { Appearance = "danger", Width = 72 })),
            Widget.Label("workspace.primary-action", "") with { Height = 48, Visible = false },
            Widget.Label("workspace.action-error", "") with { Height = 64, Visible = false, Appearance = "danger" },
            Widget.Row("workspace.tabs", Nav("controls", "Controls"), Nav("recording", "Recording")) with { Spacing = 10 },
                new Widget("checkbox", "global.ghosts") { Text = "Show real robot ghosts", Value = preferences.ShowMeasuredGhosts ? 1 : 0,
                    Hint = "Measured hardware pose, visible in Edit and Play. Ghosts are excluded from camera recordings." },
            Widget.Row("safety", Widget.Label("safety.text", "Make sure it is safe to move the robot."),
                Widget.Button("safety.accept", "Enable robot") with { Appearance = "primary" }, Widget.Button("safety.cancel", "Cancel")) with { Visible = false, Padding = 12, Appearance = "panel" },
            Widget.Column("workspace.incident", Widget.Label("incident", "") with { Height = 56 },
                Widget.Label("recovery.next", "") with { Height = 88 },
                Widget.Label("recovery.requirements", "") with { Height = 96 },
                Widget.Label("recovery.feedback", "") with { Height = 56, Visible = false },
                Widget.Button("recovery.card.run", "Open recovery") with { Enabled = false, Hint = "The current recovery step and unmet conditions appear above." },
                Widget.Button("recovery.guide.cancel", "Cancel recovery guide") with { Visible = false, Appearance = "quiet" }) with { Visible = false, Padding = 12, Appearance = "panel" },
            body, Widget.Label("status", "Connecting…") with { Height = 28, Appearance = "muted" }) with { Padding = 18, Spacing = 12 };
        return Widget.Row("workspace", RobotSidebar(), content) with { Spacing = 0 };
    }

    private Widget ControlsPage()
    {
        var widgets = new List<Widget> {
            new Widget("checkbox", "group.follow.selected") { Text = "Follow simulation with this robot", Value = ActiveProfile.FollowSimulationSelected ? 1 : 0,
                Enabled = !GroupFollowActive, Hint = "Select this robot, then use Follow simulation in the left sidebar. Checking this does not start Play or move hardware." },
            Widget.Column("teaching", Widget.Label("teaching.label", teaching.Label) with { Height = 28 },
                Widget.Label("teaching.next", teaching.NextStep) with { Height = 48 }, PikaTeachingControls()) with { Visible = teaching.BlocksTargets || PikaTeachingControlsVisible },
            Widget.Row("cameras", Camera("gripper", "Gripper fisheye"), Camera("depth", "Depth")) with { Spacing = 14 },
            Widget.Row("pika", Widget.Label("pika.name", "Pika") with { Width = 28 },
                Widget.Label("pika.value", "— mm") with { Width = 64, Appearance = "numeric", Hint = "Measured jaw opening. Enabled together with this robot." },
                Widget.Button("pika.minus", "−") with { Width = 28 }, Widget.Slider("pika.target", 0, 0, 90, .1) with { Enabled = false, Hint = "Absolute opening, 0–90 mm" },
                Widget.Button("pika.plus", "+") with { Width = 28 }, Widget.Button("pika.zero", "0") with { Width = 36 }),
            Widget.Row("joint.headings", Widget.Label("joint.name.heading", "") with { Width = 28, Appearance = "muted" },
                Widget.Label("joint.measured.heading", "Angle °") with { Width = 64, Appearance = "muted" },
                Widget.Label("joint.current.heading", "Amps") with { Width = 48, Appearance = "muted" },
                Widget.Label("joint.target.heading", "Target · reported range") with { Appearance = "muted" })
        };
        for (int i = 1; i <= 6; i++) widgets.Add(Widget.Row("joint." + i,
            Widget.Label($"joint.{i}.name", "J" + i) with { Width = 28 },
            Widget.Label($"joint.{i}.value", "—") with { Width = 64, Height = 30, Appearance = "numeric" },
            Widget.Label($"joint.{i}.current", "—") with { Width = 48, Height = 30, Appearance = "numeric" },
            Widget.Button($"joint.{i}.minus", "−") with { Width = 28 },
            Widget.Slider($"joint.{i}.target", 0, -180, 180, .1) with { Enabled = false },
            Widget.Button($"joint.{i}.plus", "+") with { Width = 28 }, Widget.Button($"joint.{i}.zero", "0") with { Width = 36 }) with { Spacing = 4 });
        widgets.Add(Widget.Row("motion", Widget.Button("zero.all", "Move all to zero") with { Width = 160 },
            Widget.Label("motion.progress", "Idle") with { Height = 28 }, Widget.Button("motion.hold", "Stop") with { Width = 65, Enabled = false }));
        widgets.Add(Widget.Row("preferences", PreferenceControl("speed", "Speed", preferences.Speed, 0, 100, 1, "Percentage of this robot's reported speed limit. The °/s readout is its target ceiling."),
            PreferenceControl("step", "Jog step", preferences.Step, .1, 5, .1, "Degrees per minus/plus press. The joint slider sets an absolute angle."),
            PreferenceControl("tolerance", "Tolerance", preferences.Tolerance, .05, 1, .05, "Arrival tolerance in degrees; saved automatically.")) with { Spacing = 16 });
        widgets.Add(Widget.Row("teleoperate", Widget.Button("teleoperate.toggle", "Drag joints") with { Width = 135 },
            Widget.Row("teleoperate.joints", Enumerable.Range(1, 6).Select(i => Widget.Button("teleoperate.joint." + i, "J" + i) with { Width = 38, Enabled = false }).ToArray()) with { Spacing = 4 },
            Info("workspace.link-info", "The solid robot is the simulated leader. The translucent ghost shows measured real joint and gripper feedback.")));
        return Widget.Column("workspace.controls.body", widgets.ToArray()) with { Spacing = 10 };
    }

    private Widget SettingsPage()
    {
        Widget section = settingsCategory switch {
            "cameras" => Widget.Column("global.cameras", SettingsRobotPicker("cameras"), CameraSettingsPage()),
            "bench" => Widget.Column("global.bench", Widget.Label("global.bench.shared", "Shared desk reference · each arm keeps its own mounting alignment") with { Height = 32, Appearance = "muted" },
                SettingsRobotPicker("bench"), CalibrationSheetControls()),
            "controller" => BackendSetup(),
            "diagnostics" => Widget.Column("global.diagnostics", SettingsRobotPicker("diagnostics"),
                Widget.Label("workspace.power", "Power unavailable") with { Appearance = "numeric" }, Widget.Label("link.status", "") with { Height = 50 },
                Widget.Label("pika.monitor", pikaMonitorStatus) with { Height = 42 }, BackendSetup(),
                Widget.Button("workspace.connection-details", connectionDetailsOpen ? "Hide advanced controller actions" : "Advanced controller actions") with { Appearance = "quiet" },
                Widget.Column("global.advanced", Widget.Button("recovery.run", "Verify controller state"),
                    Widget.Button("recovery.home", "Resting & clear — recover to zero"),
                    Widget.Label("profiles.can.status", "") with { Height = 42 }, PikaReferenceControls()) with { Visible = connectionDetailsOpen }),
            _ => GlobalRobotsSettings()
        };
        Widget Category(string id, string text) => Widget.Button("workspace.settings." + id, text) with { Appearance = settingsCategory == id ? "selected" : "quiet" };
        return Widget.Column("workspace.settings.body", Widget.Row("global.settings.heading", Widget.Label("workspace.settings.title", "Bench settings") with { Appearance = "heading" },
            Widget.Button("workspace.controls@settings", "Back to controls")),
            Widget.Row("workspace.settings.categories", Category("robot", "Robots & devices"), Category("cameras", "Cameras")) with { Spacing = 8 },
            Widget.Row("workspace.settings.categories.more", Category("bench", "Bench & calibration"), Category("diagnostics", "Diagnostics")) with { Spacing = 8 },
            section with { Spacing = 12 }) with { Spacing = 16 };
    }
}
