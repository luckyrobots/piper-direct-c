using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private string settingsCamera = "bench";
    private static string CameraTitle(string id) => id switch { "bench" => "Bench camera · shared", "depth" => "D405", _ => "Pika fisheye" };

    private Widget Camera(string id, string title)
    {
        var heading = new List<Widget> { Widget.Label("camera." + id + ".title", title) with { Height = 28 } };
        if (id == "depth")
        {
            heading.Add(Widget.Button("camera.depth.selectcolor", "Color") with { Width = 52, Appearance = D405View.Mode(CameraSettings(id)) == "color" ? "selected" : "quiet" });
            heading.Add(Widget.Button("camera.depth.selectdepth", "Depth") with { Width = 52, Appearance = D405View.Mode(CameraSettings(id)) == "depth" ? "selected" : "quiet" });
        }
        heading.Add(Widget.Button("workspace.camera." + id, "↗") with { Width = 32, Appearance = "quiet", Hint = "Enlarge real and simulation camera views" });
        heading.Add(Widget.Button("workspace.settings.cameras." + id, "⚙") with { Width = 32, Appearance = "quiet", Hint = "Camera settings" });
        return Widget.Column("camera." + id, Widget.Row("camera." + id + ".header", heading.ToArray()) with { Spacing = 4 },
            new Widget("image", "camera." + id + ".image") { ImageId = CameraImageId(id), Visible = CameraFrameReady(id), Height = id == "bench" ? 130 : 230 },
            Widget.Label("camera." + id + ".empty", CameraSettings(id).Enabled ? "Connecting camera…" : "Camera paused · use ⚙ to start") with { Height = id == "bench" ? 80 : 120, Visible = !CameraFrameReady(id), Appearance = "muted" }) with { Spacing = 6 };
    }

    private Widget CameraDetailPage() => Widget.Column("workspace.camera.body",
        Widget.Row("workspace.camera.heading", Widget.Button("workspace.camera-back", "‹ Back") with { Width = 100 },
            Widget.Label("workspace.camera.title", CameraTitle(cameraDetail)) with { Appearance = "heading" },
            Widget.Button("workspace.settings.cameras." + cameraDetail, "⚙ Camera settings") with { Width = 165 }),
        Widget.Row("camera." + cameraDetail + ".comparison",
            Widget.Column("camera." + cameraDetail + ".real", Widget.Label("camera." + cameraDetail + ".real.title", "REAL · " + (D405View.MetricDepth(cameraDetail, CameraSettings(cameraDetail)) ? "distance, 7–50 cm" : "color image")) with { Appearance = "muted" },
                new Widget("image", "camera." + cameraDetail + ".image") { ImageId = CameraImageId(cameraDetail), Height = 420, Visible = CameraFrameReady(cameraDetail) },
                Widget.Label("camera." + cameraDetail + ".empty", "Waiting for a fresh camera frame") with { Height = 60, Visible = !CameraFrameReady(cameraDetail) },
                Widget.Label("camera." + cameraDetail + ".info", cameraStatus.GetValueOrDefault(cameraDetail, "Connecting…")) with { Height = 40, Appearance = "muted" }),
            Widget.Column("camera." + cameraDetail + ".counterpart", Widget.Label("camera." + cameraDetail + ".sim.title", "SIM · " + (D405View.MetricDepth(cameraDetail, CameraSettings(cameraDetail)) ? "ideal distance" : cameraDetail == "gripper" ? "fisheye" : "color viewpoint")) with { Appearance = "muted" },
                new Widget("scene-camera", "camera." + cameraDetail + ".sim") { Height = 420 },
                Widget.Label("camera." + cameraDetail + ".sim.status", "Camera alignment unverified") with { Height = 50, Appearance = "muted" })) with { Spacing = 18 },
        Widget.Button("camera." + cameraDetail + ".simoverlay", "Real / simulation overlay") with { Width = 225, Enabled = !D405View.MetricDepth(cameraDetail, CameraSettings(cameraDetail)), Hint = "Visual comparison only. It does not establish calibrated mounts or synchronized exposures." }) with { Spacing = 14 };

    private Widget CameraSettingsPage()
    {
        string id = settingsCamera;
        var settings = CameraSettings(id);
        Widget Select(string camera, string name) => Widget.Button("workspace.settings.cameras." + camera + "@tab", name) with { Appearance = id == camera ? "selected" : "quiet" };
        return Widget.Column("workspace.settings.cameras.section", Widget.Label("workspace.settings.cameras.title", "Cameras") with { Appearance = "heading" },
            Widget.Row("workspace.settings.camera-tabs", Select("bench", "Bench · shared"), Select("depth", "D405"), Select("gripper", "Pika")),
            Widget.Label("workspace.settings.camera-scope", id == "bench" ? "One bench camera is shared by all robot profiles." : "These camera settings belong to " + ActiveProfile.Name + ".") with { Appearance = "muted", Height = 28 },
            Field("camera." + id + ".device", "Camera device", new Widget("input", "camera." + id + ".device") { Text = settings.Device, Visible = id != "depth" }) with { Visible = id != "depth" },
            Field("camera." + id + ".view", "D405 view", new Widget("dropdown", "camera." + id + ".view") { Options = ["Color image", "Depth distance · 7–50 cm"], Value = D405View.Mode(settings) == "depth" ? 1 : 0 }) with { Visible = id == "depth" },
            Widget.Row("workspace.settings.camera-actions", Widget.Button("camera." + id + ".toggle", settings.Enabled ? "Pause preview" : "Start preview"), Widget.Button("workspace.camera." + id + "@settings", "Enlarge & compare")),
            Widget.Label("workspace.settings.camera-remember", "Selection and preview state are remembered. Enabled cameras start when Pipers opens.") with { Height = 40, Appearance = "muted" },
            Widget.Button("workspace.camera-stream-details", cameraStreamDetailsOpen ? "Hide stream & device details" : "Stream & device details") with { Appearance = "quiet" },
            Widget.Column("workspace.settings.camera-stream", Field("camera." + id + ".serial", "D405 serial", new Widget("input", "camera." + id + ".serial") { Text = preferences.DepthSerial, Hint = "Blank only when exactly one D405 is connected" }) with { Visible = id == "depth" },
                Widget.Label("camera." + id + ".info", cameraStatus.GetValueOrDefault(id, "Waiting for frames…")) with { Height = 40 },
                Widget.Button("camera." + id + ".simrefresh", "Find simulation cameras"), Field("camera." + id + ".simselect", "Simulation counterpart", new Widget("dropdown", "camera." + id + ".simselect") { Options = SimulationCameraNames(), Value = SimulationCameraIndex(id) }),
                Widget.Label("camera." + id + ".clock", "Host receipt timestamps are available. Exposure delay and camera-to-world alignment remain unverified.") with { Height = 42, Appearance = "muted" }) with { Visible = cameraStreamDetailsOpen, Spacing = 10 },
            Widget.Button("workspace.camera-lens-details", cameraLensDetailsOpen ? "Hide lens calibration" : "Lens calibration & profiles") with { Appearance = "quiet" },
            Widget.Column("camera." + id + ".calibration",
                Widget.Button("camera." + id + ".calibrationtools", "Check bundled camera runtime"),
                Widget.Label("camera." + id + ".calibration.help", "Print the 8 × 6 checkerboard at actual size, 20 mm squares. Move the flat board, keeping the robot stationary, and capture 20+ varied views.") with { Height = 48, Hint = "Include different tilts, distances and image edges. The camera lens solve does not calibrate its mount." },
                Widget.Row("camera." + id + ".calibration.actions", Widget.Button("camera." + id + ".calibrationboard", "Printable board"), Widget.Button("camera." + id + ".calibrationnew", "New session"), Widget.Button("camera." + id + ".calibrationcapture", "Capture board")),
                Widget.Button("camera." + id + ".calibrationsolve", "Solve & validate fisheye lens") with { Appearance = "primary" },
                Widget.Label("camera." + id + ".calibration.status", calibrationStatus) with { Height = 46 }) with { Visible = cameraLensDetailsOpen && id == "gripper", Spacing = 12 },
            Widget.Label("workspace.settings.factory-profile", "D405 uses its selected stream's factory intrinsics. Bench and wrist camera mounts still need independent verification.") with { Visible = cameraLensDetailsOpen && id != "gripper", Height = 48, Appearance = "muted" }) with { Spacing = 14 };
    }
}
