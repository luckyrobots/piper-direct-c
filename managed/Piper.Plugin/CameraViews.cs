using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private Widget Camera(string id, string title)
    {
        // Two cards share the page width, so the actions are icons with tooltips. A gear always leads to
        // Settings; the expand icon opens the enlarged real-and-simulation view in place.
        var heading = new List<Widget> { Widget.Label("camera." + id + ".title", title) with { Height = 28, Width = id == "bench" ? 110 : 136 } };
        if (id == "depth")
            heading.Add(Widget.Segmented("camera.depth.mode", ["Color", "Depth"], D405View.Mode(CameraSettings(id)) == "depth" ? 1 : 0) with { Hint = "Which D405 stream to show" });
        if (id != "bench") heading.Add(Widget.Button("workspace.camera." + id, "") with { Width = 34, Icon = "expand", Appearance = "quiet", Hint = "Compare: enlarge the real and simulated views side by side" });
        return Widget.Column("camera." + id, Widget.Row("camera." + id + ".header", heading.ToArray()) with { Spacing = 4 },
            Widget.Label("camera." + id + ".real.title", "REAL") with { Height = 20, Appearance = "muted" },
            new Widget("image", "camera." + id + ".image") { ImageId = CameraImageId(id), Visible = CameraFrameReady(id), Height = id == "bench" ? 130 : 230 },
            Widget.Label("camera." + id + ".empty", CameraSettings(id).Enabled ? "Connecting camera…" : "Camera paused · start its preview on the Bench camera page") with { Height = id == "bench" ? 44 : 56, Visible = !CameraFrameReady(id), Appearance = "muted" },
            Widget.Label("camera." + id + ".sim.title", "SIM") with { Height = 20, Appearance = "muted", Hint = "Same scene-camera identity in Edit and Play. Camera mount and exposure synchronization still need verification." },
            new Widget("scene-camera", "camera." + id + ".sim") { Height = id == "bench" ? 130 : 180,
                Visible = SimulationCameraRouting.IsAvailable(preferences.SimulationCameras?.GetValueOrDefault(id), simulationCameras) },
            Widget.Label("camera." + id + ".sim.status", "Camera alignment unverified") with { Height = 28, Appearance = "muted" }) with { Spacing = 6 };
    }

    /// <summary>One camera's real picture beside its simulated counterpart. Used in place on a
    /// robot's Cameras tab and on the Bench camera page, so comparing never means leaving the page.</summary>
    private Widget CameraCompare(string id, int height) => Widget.Column("camera." + id + ".compare",
        Widget.Row("camera." + id + ".comparison",
            Widget.Column("camera." + id + ".real", Widget.Label("camera." + id + ".real.title", "REAL · " + (D405View.MetricDepth(id, CameraSettings(id)) ? "distance, 7–50 cm" : "color image")) with { Appearance = "muted" },
                new Widget("image", "camera." + id + ".image") { ImageId = CameraImageId(id), Height = height, Visible = CameraFrameReady(id) },
                Widget.Label("camera." + id + ".empty", "Waiting for a fresh camera frame") with { Height = 44, Visible = !CameraFrameReady(id) },
                Widget.Label("camera." + id + ".frame-info", calibration.Capture.Status(id) ?? "Connecting…") with { Height = 28, Appearance = "muted", Visible = CameraFrameReady(id) }),
            Widget.Column("camera." + id + ".counterpart", Widget.Label("camera." + id + ".sim.title", "SIM · " + (D405View.MetricDepth(id, CameraSettings(id)) ? "ideal distance" : id == "gripper" ? "fisheye" : "color viewpoint")) with { Appearance = "muted" },
                new Widget("scene-camera", "camera." + id + ".sim") { Height = height },
                Widget.Label("camera." + id + ".sim.status", "Camera alignment unverified") with { Height = 28, Appearance = "muted" })) with { Spacing = 18 },
        Widget.Button("camera." + id + ".simoverlay", "Real / simulation overlay") with { Width = 225, Enabled = !D405View.MetricDepth(id, CameraSettings(id)), Hint = "Visual comparison only. It does not establish calibrated mounts or synchronized exposures." }) with { Spacing = 10 };

    /// <summary>A camera's own page content: the comparison, then that camera's settings directly under
    /// it. There is no separate settings page to step into or back from.</summary>
    private Widget CameraPanel(string id, int height, SimulationCameraChoices cameraChoices)
    {
        var parts = new List<Widget>();
        if (id == "depth") parts.Add(Widget.Row("camera.depth.header", Widget.Label("camera.depth.mode.label", "D405 stream") with { Width = 100, Appearance = "muted" },
            Widget.Segmented("camera.depth.mode", ["Color", "Depth"], D405View.Mode(CameraSettings(id)) == "depth" ? 1 : 0) with { Hint = "Which D405 stream to show" }) with { Spacing = 8 });
        parts.Add(CameraCompare(id, height));
        parts.Add(CameraSettingsPage(cameraChoices, id));
        return Widget.Column("camera." + id + ".panel", parts.ToArray()) with { Spacing = 14 };
    }

    /// <summary>Bench → Bench camera. Reached from the sidebar, so it needs no way back.</summary>
    private Widget CameraDetailPage(SimulationCameraChoices cameraChoices) => Widget.Column("workspace.camera.body", CameraPanel(cameraDetail, 420, cameraChoices));

    private Widget CameraSettingsPage(SimulationCameraChoices cameraChoices, string id)
    {
        var settings = CameraSettings(id);
        return Widget.Column("workspace.settings.cameras.section", Section("camera-settings", "CAMERA SETTINGS"),
            Field("camera." + id + ".device", "Camera device", cameraDeviceChoices.Length > 0
                ? new Widget("dropdown", "camera." + id + ".device") { Options = new[] { "Choose camera" }.Concat(cameraDeviceChoices.Select(choice => choice.Label)).ToArray(),
                    Value = CameraDeviceChoices.Index(cameraDeviceChoices, settings.Device), Hint = "Selections use stable device identities. Duplicate camera names are separated by their USB connection." }
                : new Widget("input", "camera." + id + ".device") { Text = settings.Device, Hint = cameraDeviceDiscoveryStatus }) with { Visible = id != "depth" },
            Field("camera.depth.serial", "D405 camera", depthDeviceChoices.Length > 0
                ? new Widget("dropdown", "camera.depth.serial") { Options = new[] { "Automatic · one D405 only" }.Concat(depthDeviceChoices.Select(choice => choice.Label)).ToArray(),
                    Value = CameraDeviceChoices.Index(depthDeviceChoices, preferences.DepthSerial), Hint = "Choose this robot's D405. Its factory serial is retained across sessions." }
                : new Widget("input", "camera.depth.serial") { Text = preferences.DepthSerial, Hint = cameraDeviceDiscoveryStatus }) with { Visible = id == "depth" },
            Field("camera." + id + ".view", "D405 view", new Widget("dropdown", "camera." + id + ".view") { Options = ["Color image", "Depth distance · 7–50 cm"], Value = D405View.Mode(settings) == "depth" ? 1 : 0 }) with { Visible = id == "depth" },
            Widget.Row("workspace.settings.camera-actions", Widget.Button("camera." + id + ".toggle", settings.Enabled ? "Pause preview" : "Start preview"), 
                Widget.Button("camera." + id + ".simrefresh", "Refresh cameras")),
            Widget.Label("workspace.settings.camera-remember", "Selection and preview state are remembered. Enabled cameras start when Pipers opens.") with { Height = 40, Appearance = "muted" },
            Widget.Button("workspace.camera-stream-details", workspace.CameraStreamDetailsOpen ? "Hide stream & device details" : "Stream & device details"),
            Widget.Column("workspace.settings.camera-stream",
                Widget.Label("camera." + id + ".info", calibration.Capture.Status(id) ?? "Waiting for frames…") with { Height = 40 },
                Field("camera." + id + ".simselect", "Simulation counterpart", new Widget("dropdown", "camera." + id + ".simselect") {
                    Options = ["Automatic counterpart", .. cameraChoices.Cameras.Select(camera => camera.Name)],
                    Value = preferences.SimulationCameras?.GetValueOrDefault(id) is not { } counterpart || counterpart.SceneId != cameraChoices.SceneId
                        ? 0 : Array.FindIndex(cameraChoices.Cameras, camera => camera.Id == counterpart.CameraId) + 1 }),
                Widget.Label("camera." + id + ".clock", "Host receipt timestamps are available. Exposure delay and camera-to-world alignment remain unverified.") with { Height = 42, Appearance = "muted" }) with { Visible = workspace.CameraStreamDetailsOpen, Spacing = 10 },
            Widget.Button("workspace.camera-lens-details", workspace.CameraLensDetailsOpen ? "Hide lens calibration" : "Lens calibration & profiles"),
            Widget.Column("camera." + id + ".calibration",
                Widget.Button("camera." + id + ".calibrationtools", "Check bundled camera runtime"),
                Widget.Label("camera." + id + ".calibration.help", "Print the 8 × 6 checkerboard at actual size, 20 mm squares. Move the flat board, keeping the robot stationary, and capture 20+ varied views.") with { Height = 48, Hint = "Include different tilts, distances and image edges. The camera lens solve does not calibrate its mount." },
                Widget.Row("camera." + id + ".calibration.actions", Widget.Button("camera." + id + ".calibrationboard", "Printable board"), Widget.Button("camera." + id + ".calibrationnew", "New session"), Widget.Button("camera." + id + ".calibrationcapture", "Capture board")),
                Widget.Button("camera." + id + ".calibrationsolve", "Solve & validate fisheye lens") with { Appearance = "primary" },
                Widget.Label("camera." + id + ".calibration.status", lens.Status) with { Height = 46 }) with { Visible = workspace.CameraLensDetailsOpen && id == "gripper", Spacing = 12 },
            Widget.Label("workspace.settings.factory-profile", "D405 uses its selected stream's factory intrinsics. Bench and wrist camera mounts still need independent verification.") with { Visible = workspace.CameraLensDetailsOpen && id != "gripper", Height = 48, Appearance = "muted" }) with { Spacing = 14 };
    }
}
