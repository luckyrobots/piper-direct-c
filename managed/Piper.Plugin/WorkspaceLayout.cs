using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal Task Render(CancellationToken cancellation) => PublishWorkspaceView(cancellation);

    private Widget BuildWorkspaceView(bool showClearanceConfirmation, SimulationCameraChoices cameraChoices)
    {
        bool bench = WorkspaceTabs.BenchPage(Navigation);
        Widget body = Navigation.Page switch {
            WorkspacePage.Overview => OverviewPage(), WorkspacePage.Follow => FollowPage(),
            WorkspacePage.Settings => SettingsPage(cameraChoices),
            WorkspacePage.Recording => RecordingPage(), WorkspacePage.Replay => ReplayPage(), WorkspacePage.Camera => CameraDetailPage(cameraChoices), _ => ControlsPage() };
        // A robot page is a tabbed panel: the robot's own header and its tab strip sit on the dark
        // band, and the open tab runs into the lighter page below it. Nothing may come between the
        // strip and the page, or the open tab stops reading as part of what it shows.
        var top = Widget.Column("workspace.top", Widget.Column("workspace.top.header",
            Widget.Column("header", Widget.Row("header.title", // A fixed width: in a narrow panel the name otherwise wraps inside itself ("Pipe / r 3") before the status does.
                Widget.Label("workspace.robot-name", ActiveProfile.Name) with { Appearance = "heading", Height = 30, Width = Math.Clamp(ActiveProfile.Name.Length * 11 + 6, 60, 260) },
                StatusBadge("workspace.connection.dot", "muted", Widget.Label("workspace.connection", "Connecting…") with { Height = 30 })),
                Widget.Label("workspace.next", "") with { Visible = false, Hint = "The one thing to do next for this robot." },
                Widget.Row("header.actions", Widget.Button("enable", "Enable robot") with { Appearance = "primary" },
                    // Stop is not repeated here: every robot has its own in the sidebar, on every page.
                    Widget.Button("disable", "Disable") with { Width = 76, Hint = "Support the arm before releasing holding torque." }) with { Visible = Navigation.Page == WorkspacePage.Controls }) with { Visible = !bench },
            Widget.Label("workspace.primary-action", "") with { Height = 48, Visible = false },
            Widget.Label("workspace.action-error", "") with { Height = 64, Visible = false, Appearance = "danger" },
            Widget.Label("workspace.page-title", BenchPageTitle()) with { Appearance = "heading", Visible = bench }) with { Padding = 18, Spacing = 12 },
            // Tabs belong to the selected robot. A bench page is not about one robot and shows its own title.
            Widget.Tabs("workspace.pages", WorkspaceTabs.RobotTabs, WorkspaceTabs.PageIndex(Navigation)) with { Icons = ["", "", "", "gear"], Visible = !bench })
            with { Spacing = 0, Appearance = bench ? null : "sidebar" };
        var page = Widget.Column("workspace.page",
            Confirmation("safety", Widget.Label("safety.text", "Make sure it is safe to move the robot."),
                Widget.Button("safety.accept", "Enable robot") with { Appearance = "primary" }, Widget.Button("safety.cancel", "Cancel")) with { Visible = showClearanceConfirmation },
            Confirmation("disable.confirm", Widget.Label("disable.confirm.text", DisableQuestionText),
                Widget.Button("disable.confirm.accept", "Arm supported — disable") with { Appearance = "danger" },
                Widget.Button("disable.confirm.cancel", "Cancel")) with { Visible = DisableQuestionOpen },
            ResetCompletionConfirmation(),
            Widget.Column("workspace.incident", Widget.Label("incident.heading", "RECOVERY") with { Appearance = "muted" },
                Widget.Label("incident", ""),
                Widget.Label("recovery.next", ""),
                Widget.Label("recovery.requirements", "") with { Appearance = "muted" },
                Widget.Label("recovery.feedback", "") with { Visible = false },
                Widget.Button("recovery.card.run", "Open recovery") with { Enabled = false, Hint = "The current recovery step and unmet conditions appear above." },
                Widget.Button("recovery.home", "Arm resting, path clear — recover to zero") with { Enabled = false },
                Widget.Button("recovery.guide.cancel", "Cancel recovery guide") with { Visible = false, Appearance = "quiet" }) with { Visible = false, Padding = 12, Spacing = 8, Appearance = "panel" },
            body, Widget.Label("status", "Connecting…") with { Height = 28, Appearance = "muted", Visible = !bench }) with { Padding = 18, Spacing = 12 };
        var content = Widget.Column("controls", top, page) with { Spacing = 0 };
        return Widget.Row("workspace", RobotSidebar(), TidyLayout(content, inColumn: false)) with { Spacing = 0 };
    }

    /// <summary>One layout rule for every page, applied after the page is built:
    /// a button placed straight into a column would stretch across the whole pane whatever width it
    /// asks for, so it gets a row of its own and keeps its own width; and a wrapping text label in a column takes the
    /// height of its text, not a fixed block that leaves a gap when the text is short.</summary>
    internal static Widget TidyLayout(Widget widget, bool inColumn)
    {
        var tidy = widget.Children is null ? widget
            : widget with { Children = widget.Children.Select(child => TidyLayout(child, widget.Type == "column")).ToArray() };
        if (!inColumn) return tidy;
        if (widget.Type == "button")
            // The row stays visible: a later patch shows or hides the button itself, never this wrapper.
            return Widget.Row(widget.Id + "@fit", tidy);
        if (widget.Type is "text" or "markdown" && widget.Height > 32 && widget.Appearance != "numeric")
            return tidy with { Height = null };
        return tidy;
    }

    internal Widget ControlsPage()
    {
        var widgets = new List<Widget> {
            Widget.Column("teaching", Widget.Label("teaching.label", recovery.ObservedTeaching.Label) with { Height = 28 },
                Widget.Label("teaching.next", recovery.ObservedTeaching.NextStep) with { Height = 48 }, PikaTeachingControls()) with { Visible = recovery.ObservedTeaching.BlocksTargets || PikaTeachingControlsVisible },
            Section("joints", "GRIPPER & JOINTS"),
            Widget.Row("pika", Widget.Label("pika.name", "Pika") with { Width = 28 },
                Widget.Label("pika.value", "— mm") with { Width = 64, Appearance = "numeric", Hint = "Measured jaw opening. Enabled together with this robot." },
                Widget.Button("pika.minus", "−") with { Width = 28 }, Widget.Slider("pika.target", PikaProtocol.MinimumOpeningMm, PikaProtocol.MinimumOpeningMm, PikaProtocol.MaximumOpeningMm, .1) with { Enabled = false, Hint = $"Absolute opening, {PikaProtocol.MinimumOpeningMm}–{PikaProtocol.MaximumOpeningMm} mm" },
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
        widgets.Add(Widget.Column("preferences", PreferenceControl("speed", "Speed", preferences.Speed, 0, 100, 1, "Percentage of this robot's reported speed limit. The °/s readout is its target ceiling."),
            PreferenceControl("step", "Jog step", preferences.Step, .1, 5, .1, "Degrees per minus/plus press. The joint slider sets an absolute angle."),
            PreferenceControl("tolerance", "Tolerance", preferences.Tolerance, .05, 1, .05, "Arrival tolerance in degrees; saved automatically.")) with { Spacing = 4 });
        widgets.Add(Widget.Row("teleoperate", Widget.Button("teleoperate.toggle", "Drag joints") with { Width = 135 },
            Widget.Row("teleoperate.joints", Enumerable.Range(1, 6).Select(i => Widget.Button("teleoperate.joint." + i, "J" + i) with { Width = 38, Enabled = false }).ToArray()) with { Spacing = 4 },
            Info("workspace.link-info", "The solid robot is the simulated leader. The translucent ghost shows measured real joint and gripper feedback.")));
        widgets.Add(new Widget("checkbox", "global.ghosts") { Text = "Show real robot ghosts", Value = preferences.ShowMeasuredGhosts ? 1 : 0,
            Hint = "Measured hardware pose, visible in Edit and Play. Ghosts are excluded from camera recordings." });
        return Widget.Column("workspace.controls.body", widgets.ToArray()) with { Spacing = 10 };
    }

    private string BenchPageTitle() => Navigation.Page switch
    {
        WorkspacePage.Overview => "Bench overview", WorkspacePage.Follow => "Follow simulation",
        WorkspacePage.Camera => "Bench camera",
        _ => Navigation.Category == SettingsCategory.Cameras ? "Bench camera setup" : "Arm server"
    };

    private Widget SettingsPage(SimulationCameraChoices cameraChoices)
    {
        bool benchCamera = Navigation.Category == SettingsCategory.Cameras && settingsCamera == "bench";
        Widget section = Navigation.Category switch {
            // A robot's Cameras tab: choose one of its two wrist cameras, see it beside its simulated
            // counterpart, and change its settings right under the picture.
            SettingsCategory.Cameras => benchCamera ? Widget.Column("global.cameras", CameraPanel("bench", 420, cameraChoices))
                : Widget.Column("global.cameras",
                    Widget.Segmented("workspace.robot-camera", WorkspaceTabs.RobotCameras, settingsCamera == "depth" ? 1 : 0),
                    CameraPanel(settingsCamera == "depth" ? "depth" : "gripper", 320, cameraChoices)),
            SettingsCategory.Bench => Widget.Column("global.bench",
                Widget.Row("global.bench.heading", Widget.Button("workspace.settings.robot@calibration", "Setup") with { Width = 90, Icon = "back", Hint = "Back to this robot's Setup" },
                    Widget.Label("global.bench.title", "Mounting calibration · " + ActiveProfile.Name) with { Appearance = "heading" }),
                Widget.Label("global.bench.shared", "Shared desk reference · each arm keeps its own mounting alignment") with { Height = 32, Appearance = "muted" },
                CalibrationSheetControls()),
            SettingsCategory.Diagnostics => Widget.Column("global.diagnostics",
                Widget.Label("global.server.help", "One arm server runs the whole bench: every arm and every gripper.") with { Appearance = "muted", Height = 28 },
                BackendSetup()),
            // A robot's Setup tab: everything that belongs to this one arm.
            _ => Widget.Column("global.setup", Section("connections", "CONNECTIONS"), GlobalRobotsSettings(),
                Section("calibration", "MOUNTING CALIBRATION"),
                Widget.Row("global.setup.calibration", CalibrationLink("setup"),
                    Widget.Label("global.setup.calibration.help", "Where this arm stands on the desk. Guided; it never moves the arm.") with { Appearance = "muted" }) with { Spacing = 10 },
                Section("diagnostics", "DIAGNOSTICS"),
                Widget.Label("workspace.power", "Power unavailable"), Widget.Label("link.status", "") with { Height = 50 },
                Widget.Label("pika.monitor", telemetry.Pika.Status) with { Height = 42 },
                Widget.Button("workspace.connection-details", workspace.ConnectionDetailsOpen ? "Hide advanced controller actions" : "Advanced controller actions"),
                Widget.Column("global.advanced", Widget.Button("recovery.run", "Verify controller state"),
                    Widget.Label("profiles.can.status", "") with { Height = 42 }, PikaReferenceControls()) with { Visible = workspace.ConnectionDetailsOpen },
                // Add robot is at the end of the robot list; this is its counterpart, for this robot.
                Section("remove", "REMOVE ROBOT"),
                Widget.Row("global.setup.remove", Widget.Button("global.robot." + ActiveProfile.Id + ".remove", "Remove " + ActiveProfile.Name) with
                    { Width = 190, Appearance = "danger-quiet", Enabled = preferences.Robots!.Length > 1, Hint = "The last robot cannot be removed." },
                    Widget.Label("global.setup.remove.help", "Deletes this robot's profile and settings here. The arm itself is not touched.") with { Appearance = "muted" }) with { Spacing = 10 })
        };
        return Widget.Column("workspace.settings.body", section with { Spacing = 12 }) with { Spacing = 16 };
    }
}
