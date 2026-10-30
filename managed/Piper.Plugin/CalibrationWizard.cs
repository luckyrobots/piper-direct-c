using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private CalibrationWizardDraft? calibrationWizard;
    private string[] wizardDiscoveredSites = [];
    private string wizardProfile = "", wizardMessage = "", wizardModelPoint = "Show the point, then compare its marker with the real jaw tips.";
    private string wizardModelPointDetails = "Model coordinates and site identity are not a verified physical TCP calibration.";
    private CalibrationWizardDraft Wizard => calibrationWizard ?? throw new InvalidOperationException("Open the calibration wizard first.");
    private string WizardId(string action) => "wizard." + Wizard.Epoch + "." + action;
    private int WizardTrainingCount => calibrationSamples.Count(s => !s.HeldOut);
    private int WizardValidationCount => calibrationSamples.Count(s => s.HeldOut);
    private bool WizardValidation => Wizard.Answers.GetValueOrDefault("role") == "validation";
    private string WizardDraftPath => Path.Combine(RobotCalibrationDirectory, "wizard-draft.json");

    private void LoadWizard()
    {
        LoadCalibrationPreferences();
        if (wizardProfile == ActiveProfile.Id && calibrationWizard is not null) return;
        wizardProfile = ActiveProfile.Id; wizardMessage = ""; wizardDiscoveredSites = []; wizardModelPoint = "Show the point, then compare its marker with the real jaw tips."; wizardReferenceRetryRequired = false;
        calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id);
        try
        {
            if (File.Exists(WizardDraftPath) && new FileInfo(WizardDraftPath).Length <= 64 * 1024 &&
                JsonSerializer.Deserialize<CalibrationWizardDraft>(File.ReadAllText(WizardDraftPath), PluginProtocol.Json) is { } saved)
            {
                CalibrationWizardFlow.ValidateDraft(saved, ActiveProfile.Id);
                calibrationWizard = CalibrationWizardFlow.TabletopDefault(saved);
                // Restore partial observations only from the draft's named report.
                // Every later capture/write still rechecks the live pinned scope.
                string reportPath = Path.Combine(RobotCalibrationDirectory, saved.ReportFile);
                if (calibrationIdentity is null && saved.ReportFile.Length > 0 && File.Exists(reportPath) &&
                    JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(reportPath), PluginProtocol.Json) is { } report)
                {
                    _ = CalibrationSavedState.Recover(report, ActiveProfile.Id);
                    calibrationIdentity = report.Identity; calibrationOriginal = report.OriginalPlacement; calibrationApplied = report.AppliedPlacement;
                    calibrationSamples.Clear(); calibrationSamples.AddRange(report.Samples); calibrationFit = report.Fit; calibrationDesk = report.Desk; calibrationPath = reportPath;
                    calibrationPlacementUnverified = report.PlacementUnverified || report.AppliedPlacement is not null;
                }
                wizardMessage = "Your saved answers and captured points are available. Nothing moves or applies automatically. If the robot connection or model changed, start a new check.";
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id); wizardMessage = "Saved draft could not be resumed; the report is retained. " + error.Message; }
        if (calibrationPlacementUnverified)
            calibrationWizard = Wizard with { Step = CalibrationWizardStep.InspectSaved, History = [], Epoch = Wizard.Epoch + 1 };
        else if (calibrationSamples.LastOrDefault() is { } last && CalibrationWizardFlow.AlreadyRecorded(Wizard, last))
        { calibrationWizard = CalibrationWizardFlow.NextPoint(Wizard, WizardTrainingCount, WizardValidationCount); wizardMessage = "The last explicit Capture was already saved before interruption. Its observation is retained; no duplicate capture or motion was repeated."; }
        else if (calibrationIdentity is not null && (int)Wizard.Step < (int)CalibrationWizardStep.PointIntro)
            calibrationWizard = Wizard with { Step = CalibrationWizardStep.Review, History = [], Epoch = Wizard.Epoch + 1 };
        if (calibrationIdentity is null && Wizard.Answers.ContainsKey("sceneconfirmed"))
        {
            try { calibrationDesk = calibrationDesk with { SurfaceY = CalibrationWizardFlow.Number(Wizard, "sceney", 0, 3000) / 1000 }; }
            catch (InvalidOperationException) { Wizard.Answers.Remove("sceneconfirmed"); }
        }
    }

    private Widget WizardButton(string action, string text) => Widget.Button(WizardId(action), text) with
    { Appearance = action is "next" or "capture" or "apply" ? "primary" : action is "back" or "new" or "open" ? "quiet" : "default", Padding = 8 };
    private Widget GuidedCalibrationControls()
    {
        LoadWizard();
        var step = Wizard.Step;
        var children = new List<Widget>
        {
            Widget.Row("wizard.header", Widget.Label("wizard.title", "Calibrate " + ActiveProfile.Name) with { Appearance = "heading" }, Widget.Button("workspace.controls@wizard", "Back to robot controls") with { Width = 190, Appearance = "quiet" }),
            Widget.Label("wizard.progress", $"{WizardTrainingCount}/4 reference points + {WizardValidationCount}/1 check point · {WizardStepName()}") with { Height = 30, Appearance = "muted" },
            Widget.Row("wizard.prompt", Widget.Label("wizard.question", WizardPrompt()) with { Height = 60, Appearance = "heading" },
                WizardButton("help", "(i)") with { Width = 32, Padding = 4, Appearance = "quiet", Hint = WizardQuestion() }),
        };
        if (step is CalibrationWizardStep.HandGuide or CalibrationWizardStep.Capture)
            children.Add(Widget.Label("wizard.capture.essentials", step == CalibrationWizardStep.HandGuide
                ? "Press the arm button once: solid green means hand guiding. Support the arm as needed. Move the jaw-tip midpoint to your chosen spot and hold still for 0.4 seconds."
                : "Keep the light solid green and support the arm as needed. Hold still for 0.4 seconds, then Capture. This records the position; it does not move or power the arm.") with { Height = 64 });
        string? input = CalibrationWizardFlow.Input(step);
        if (step == CalibrationWizardStep.Prepare)
            children.Add(link is null ? Widget.Button("link", "Link robot") with { Appearance = "primary", Hint = "Shows measured real-joint positions in the simulation. This does not enable motors or send a movement command." } : Widget.Label("wizard.linked", "Linked. Moving the real arm by hand should move its simulated copy."));
        if (input is not null && step != CalibrationWizardStep.SceneSurfaceY)
        {
            if (step == CalibrationWizardStep.ModelPoint) children.Add(Widget.Label("wizard.input.label", "Model point name (advanced)") with { Appearance = "muted" });
            children.Add(new Widget("input", WizardId("answer")) { Text = Wizard.Answers.GetValueOrDefault(input, ""), Hint = input == "tool" ? "Exact case-sensitive model site name. Use Show selected point, or choose the discovered jaw-tip reference below." : input is "column" or "row" ? "Enter the whole-number line index, starting at 0." : "Enter your measurement in millimetres. For example, 1 cm = 10 mm. Blank means unknown." });
        }
        if (step is CalibrationWizardStep.DeskFrame or CalibrationWizardStep.DeskBack)
        {
            children.Add(Widget.Label("wizard.desk.orientation", "Stand at the front long edge of the real desk. LEFT is the short edge on your left. BACK is the long edge furthest from you. Compare the pointer in the simulation with the real edge you will measure from.") with { Height = 84 });
            children.Add(WizardButton("edge.show", step == CalibrationWizardStep.DeskFrame ? "Show LEFT edge" : "Show BACK edge"));
            children.Add(Widget.Label("wizard.edge.status", WizardReferenceShown() ? "Pointer shown. Confirm only if it marks the matching desk edge." : "Waiting for the simulation pointer. Confirmation stays unavailable until it is shown.") with { Height = 48 });
        }
        if (step == CalibrationWizardStep.ContactPoint)
        {
            children.Add(Widget.Label("wizard.contact.diagram", "left jaw tip o-----+-----o right jaw tip\n                  measured midpoint") with { Height = 42, Appearance = "numeric" });
            children.Add(WizardButton("contact", Wizard.Answers.ContainsKey("physicalpoint") ? "Jaw-tip midpoint selected" : "Use midpoint between jaw tips"));
        }
        if (step == CalibrationWizardStep.ModelPoint)
        {
            children.Add(WizardButton("model.show", "Show selected point") with { Hint = "Reads the exact selected model site and shows its temporary marker. This does not move hardware or verify physical correspondence." });
            if (wizardDiscoveredSites.Count(name => name == "calibration_jaw_tips") == 1)
                children.Add(WizardButton("model.jawtips", "Show midpoint between jaw tips") with { Hint = "Uses the discovered model's calibration_jaw_tips site. Compare the temporary marker with the physical midpoint; software does not verify correspondence." });
            children.Add(Widget.Label("wizard.model.point", wizardModelPoint) with { Height = 50, Hint = wizardModelPointDetails });
        }
        if (step == CalibrationWizardStep.TabletopHeight)
            children.Add(Widget.Label("wizard.height.scene", $"Confirmed scene surface Y: {calibrationDesk.SurfaceY * 1000:F0} mm. Scene floor reference: {Wizard.Answers.GetValueOrDefault("floory", "unknown")} mm. Physical ruler height remains separate; no scene object moves.") with { Height = 42 });
        if (step == CalibrationWizardStep.SceneSurfaceY)
            children.Add(WizardButton("scene.root", "Use this tabletop reference") with { Hint = "Confirm the simulated base is on the simulated tabletop. This reads its validated metric world-Y reference; it does not move any scene object or use a physical floor measurement." });
        if (step == CalibrationWizardStep.SceneFloorOrigin)
            children.Add(WizardButton("floor.zero", "My scene floor and physical floor origin both use Y=0"));
        if (step == CalibrationWizardStep.Method)
            children.Add(new Widget("dropdown", WizardId("method")) { Options = ["Measure from desk edges", "Aligned grid mat intersections"], Value = Wizard.Answers.GetValueOrDefault("method") == "grid intersections" ? 1 : 0 });
        if (step == CalibrationWizardStep.PointIntro)
        {
            if (WizardValidation && WizardCross() is { } cross)
                children.Add(WizardButton("cross", "Use saved desk cross") with { Hint = $"Verify this profile's cross: {cross.RightMm:F0} mm from the RIGHT edge and {cross.BackMm:F0} mm from the BACK edge. The following questions show its saved edge measurements for you to check." });
            children.Add(Widget.Label("wizard.point.role", WizardValidation ? "Choose a NEW spot at least 5 cm from every reference point. This checks the result; it is not used to calculate the alignment." :
                "Choose four different spots across the desk, not along one line. Spread them at least 10 cm apart overall in both directions. Save the desk cross for the final check.") with { Height = 70 });
        }
        if (step == CalibrationWizardStep.Capture)
        {
            children.Add(Widget.Label("wizard.measurement", WizardMeasurementSummary()) with { Height = 48 });
            children.Add(WizardButton("capture", WizardValidation ? "Capture check point" : "Capture reference point"));
        }
        if (step is CalibrationWizardStep.Review or CalibrationWizardStep.Complete)
        {
            children.Add(Widget.Label("wizard.review", WizardReviewSummary()) with { Height = 116, Hint = CalibrationPreview() });
            children.Add(Widget.Row("wizard.review.actions", WizardButton("fit", "Calculate alignment"), WizardButton("apply", "Apply to simulation"), WizardButton("rollback", "Undo alignment")));
            children.Add(Widget.Row("wizard.more.actions", WizardButton("add.training", "Add reference point"), WizardButton("add.validation", "Add check point"), WizardButton("open", "Open saved report")));
        }
        if (step == CalibrationWizardStep.InspectSaved)
            children.Add(Widget.Row("wizard.inspect.actions", WizardButton("inspect", "Check scene placement (read-only)"), WizardButton("open", "Open saved report")));
        if (calibrationArchiveConfirmation)
            children.Add(Widget.Column("wizard.archive", Widget.Label("wizard.archive.warning", "Keep the saved report and archive its pending/applied rollback context? This does not undo or repeat a scene write.") with { Height = 44 },
                Widget.Row("wizard.archive.actions", WizardButton("archive", "Keep report & start new questions"), WizardButton("archive.cancel", "Cancel"))));
        children.Add(Widget.Row("wizard.navigation", WizardButton("back", "Previous question"), WizardButton("next", WizardNextLabel()), WizardButton("new", "Start over / edit setup")));
        children.Add(Widget.Label("wizard.status", wizardMessage.Length > 0 ? wizardMessage : robotCalibrationStatus) with { Height = 58 });
        var help = new List<Widget> { Widget.Column("wizard.camera.header", Widget.Label("wizard.camera.title", "Camera view"),
            new Widget("dropdown", WizardId("camera")) { Options = ["Shared bench", "Pika gripper", "D405 camera (current view)"], Value = Array.IndexOf(new[] { "bench", "gripper", "depth" }, Wizard.HelpCamera) }) };
        help.Add(new Widget("image", "wizard.camera.image") { ImageId = CameraImageId(Wizard.HelpCamera), Height = 180, Visible = CameraFrameReady(Wizard.HelpCamera) });
        help.Add(Widget.Row("wizard.camera.caption", Widget.Label("wizard.camera.note", "Live preview") with { Appearance = "muted" },
            WizardButton("help", "(i)") with { Id = WizardId("camera.help"), Width = 32, Padding = 4, Appearance = "quiet",
                Hint = "Existing JPEG preview only. It does not measure distance or prove physical/model point correspondence. This page reuses the selected running camera; it does not open another capture." }));
        if (step is >= CalibrationWizardStep.DeskFrame and <= CalibrationWizardStep.GridRow or CalibrationWizardStep.DeskBack)
        {
            help.Add(Widget.Label("wizard.desk.title", "Desk seen from above") with { Appearance = "heading" });
            help.Add(Widget.Label("wizard.desk.diagram", "        BACK (far edge)\n     +------------------+\n     |        |         |\nLEFT |        | back    | RIGHT\n     |--left--P         |\n     |                  |\n     +------------------+\n       FRONT (you here)") with { Height = 174, Appearance = "numeric", Hint = "P is the spot you measure. Measure straight in from LEFT and BACK, parallel to the desk edges. Advanced: the scene desk is centred on X/Z=0; left is -X, back is -Z, width follows X, depth follows Z. A translated or rotated scene desk must be fixed separately." });
            help.Add(Widget.Label("wizard.desk.caption", "P = your chosen spot. Measure from LEFT and BACK. Height is above the tabletop, not above the floor.") with { Height = 62 });
        }
        return Widget.Row("wizard.page", Widget.Column("wizard.body", children.ToArray()) with { Padding = 16, Spacing = 12, Appearance = "panel" },
            Widget.Column("wizard.help", help.ToArray()) with { Width = 320, Padding = 12, Spacing = 8, Appearance = "panel" }) with { Spacing = 14 };
    }

    private string WizardQuestion() => Wizard.Step switch
    {
        CalibrationWizardStep.Prepare => "Choose the real robot and its matching simulated copy, then click Link robot here. Check that both show the same pose. This page reads positions only; it never enables, resets or moves the real arm.",
        CalibrationWizardStep.ContactPoint => "Which physical point will you measure at every location? Use the midpoint between BOTH jaw tips, not one tilted fingertip or the camera lens. Software has not verified its matching model site.",
        CalibrationWizardStep.ModelPoint => "What exact named model point represents the physical midpoint? Read its coordinates and inspect the temporary scene marker, then report correspondence. A grasp centre may be behind the jaw tips; a name or marker alone does not verify the physical TCP.",
        CalibrationWizardStep.DeskFrame => "Stand at the front long edge of your real desk, facing across it. The LEFT edge is the short edge on your left. The pointer marks the simulated LEFT edge. Confirm only if these are the corresponding edges. Advanced: this scene desk is centred at X/Z=0; its left edge is X=-width/2. A differently placed or rotated simulated desk must be corrected first.",
        CalibrationWizardStep.DeskBack => "Keep standing at the same front long edge. BACK means the far long edge, furthest from you. The pointer marks the simulated BACK edge. Confirm only if it matches the real edge you will measure from. Advanced: this scene's back edge is Z=-depth/2.",
        CalibrationWizardStep.SceneSurfaceY => "Check that the simulated base is on the simulated tabletop, then use that reference. The validated model root supplies the technical world-Y value; this reads a scene reference without moving anything. Physical floor/table height is irrelevant to tabletop-relative contact measurements.",
        CalibrationWizardStep.SceneFloorOrigin => "Optional legacy office-height audit only: enter a known scene-world floor reference if you want to preserve it. This value is not required or used for tabletop capture, fitting or Apply.",
        CalibrationWizardStep.DeskWidth => "What is the full tabletop width, from left edge to right edge, in millimetres? Enter your measured value.",
        CalibrationWizardStep.DeskDepth => "What is the full tabletop depth, from back edge to front edge, in millimetres? Enter your measured value.",
        CalibrationWizardStep.TabletopHeight => "Optional legacy office-height audit only: physical tabletop height remains unknown unless measured and entered. It is visual-office metadata, not a tabletop-calibration requirement or capture gate.",
        CalibrationWizardStep.Precision => "Enter how much your ruler or hand placement could be off, in millimetres, from 1 to 10. For example, enter 5 if you can place and measure a point only to about 5 mm. The result is rejected when the recorded points disagree too much; this number is not a guarantee of accuracy.",
        CalibrationWizardStep.Method => "How will you locate each point: measure its distance from the desk edges, or use numbered intersections on a grid mat? No object is required.",
        CalibrationWizardStep.GridSquare => "What is the grid square size in millimetres? Verify with a ruler. Secure the mat flat and parallel to the desk edges; rotated grids are not supported.",
        CalibrationWizardStep.GridOriginLeft => "How far is grid intersection (column 0, row 0) from the desk's LEFT edge, in millimetres? Columns must increase toward the right.",
        CalibrationWizardStep.GridOriginBack => "How far is grid intersection (column 0, row 0) from the desk's BACK edge, in millimetres? Rows must increase toward the front.",
        CalibrationWizardStep.PointIntro => WizardValidation ? "Choose a new check point that you have not captured before, at least 5 cm away from each reference point. It checks the alignment without helping calculate it. You may use your saved desk cross after verifying its measurements." : $"Choose reference point {WizardTrainingCount + 1} of four: a spot where you can position and measure the midpoint between the jaw tips. Use spots spread across both desk directions, not one line. Keep the desk cross for the final check.",
        CalibrationWizardStep.PointLeft => "At this point, what is the distance from the physical desk's LEFT edge to the defined contact point, in millimetres?",
        CalibrationWizardStep.PointBack => "At this point, what is the distance from the physical desk's BACK edge to the same defined contact point, in millimetres?",
        CalibrationWizardStep.PointHeight => "How high is that contact point above the tabletop, in millimetres? Enter 0 only if it is actually on the bare desk. Include grid-mat thickness; do not assume zero.",
        CalibrationWizardStep.GridColumn => "Which numbered grid intersection COLUMN is this point? Use a whole number from column 0 at the measured origin; not a square centre.",
        CalibrationWizardStep.GridRow => "Which numbered grid intersection ROW is this point? Use a whole number from row 0 at the measured origin; not a square centre.",
        CalibrationWizardStep.HandGuide => "Single-click the arm's physical button for solid green hand guiding. Bring the defined midpoint to your measured location by hand. Keep support as needed and hold still at least 0.4 seconds. The scene should follow measured joints.",
        CalibrationWizardStep.Capture => "Ready to record this one point? Check the entered measurements and both views. Capture is an explicit action, requires fresh healthy stationary hand guiding, and never moves the arm.",
        CalibrationWizardStep.Review => "Click Calculate alignment, then compare the errors with your measurement precision. All four reference points and the separate check point must pass. Apply changes only where the simulated robot sits and faces; it sends no movement command to the real robot. The detailed report keeps every measurement and error.",
        CalibrationWizardStep.Complete => "The simulated robot's placement was saved; the real arm was not commanded. A successful desk check does not verify camera alignment, the exact physical measuring point, or synchronized recording. Those need separate checks.",
        _ => "A saved scene write/applied placement needs read-only inspection. No Apply, rollback or motion resumes automatically after a restart."
    };
    private string WizardPrompt() => Wizard.Step switch
    {
        CalibrationWizardStep.Prepare => "Link this real robot with its simulated copy.",
        CalibrationWizardStep.ContactPoint => "Which physical point will you measure?",
        CalibrationWizardStep.ModelPoint => "Check the point between the simulated jaw tips.",
        CalibrationWizardStep.DeskFrame => "Is the pointer on the LEFT edge you will measure from?",
        CalibrationWizardStep.DeskBack => "Is the pointer on the BACK edge you will measure from?",
        CalibrationWizardStep.SceneSurfaceY => "Is the simulated robot base resting on the tabletop?",
        CalibrationWizardStep.SceneFloorOrigin => "Optional: enter your saved floor reference, or continue.",
        CalibrationWizardStep.DeskWidth => "Measure LEFT to RIGHT across the desk. Enter millimetres.",
        CalibrationWizardStep.DeskDepth => "Measure BACK to FRONT across the desk. Enter millimetres.",
        CalibrationWizardStep.TabletopHeight => "Optional: enter the desk's height above the floor, in mm.",
        CalibrationWizardStep.Precision => "How many millimetres could your measurements be off?",
        CalibrationWizardStep.Method => "Will you use edge measurements or a grid mat?",
        CalibrationWizardStep.GridSquare => "What is the verified grid-square size in millimetres?",
        CalibrationWizardStep.GridOriginLeft => "Measure LEFT edge to grid line 0. Enter millimetres.",
        CalibrationWizardStep.GridOriginBack => "Measure BACK edge to grid line 0. Enter millimetres.",
        CalibrationWizardStep.PointIntro => WizardValidation ? "Choose one new spot to check the alignment." : $"Choose spot {WizardTrainingCount + 1} of 4 on your desk.",
        CalibrationWizardStep.PointLeft => "Measure LEFT edge to this spot. Enter millimetres.",
        CalibrationWizardStep.PointBack => "Measure BACK edge to this spot. Enter millimetres.",
        CalibrationWizardStep.PointHeight => "How many millimetres is the jaw-tip midpoint above the tabletop?",
        CalibrationWizardStep.GridColumn => "Which numbered grid column contains this intersection?",
        CalibrationWizardStep.GridRow => "Which numbered grid row contains this intersection?",
        CalibrationWizardStep.HandGuide => "Move the jaw-tip midpoint to your measured spot by hand.",
        CalibrationWizardStep.Capture => "Check the measurements below, then capture this position.",
        CalibrationWizardStep.Review => "Check the errors before applying the simulated alignment.",
        CalibrationWizardStep.Complete => "Simulation alignment saved. The real arm was not moved.",
        _ => "Inspect the saved scene placement before continuing."
    };
    private string WizardNextLabel() => Wizard.Step switch
    {
        CalibrationWizardStep.ModelPoint => "Marker matches jaw tips",
        CalibrationWizardStep.DeskFrame => "This is my LEFT edge",
        CalibrationWizardStep.DeskBack => "This is my BACK edge",
        CalibrationWizardStep.SceneSurfaceY => "The base is on the tabletop",
        CalibrationWizardStep.HandGuide => "I am holding it still",
        _ => "Continue"
    };
    private (double LeftMm, double RightMm, double BackMm)? WizardCross()
    {
        var reference = ActiveProfile.Alignment;
        if (reference is null || !reference.Name.Contains("cross", StringComparison.OrdinalIgnoreCase) ||
            !Wizard.Answers.TryGetValue("width", out var _) || !Wizard.Answers.TryGetValue("depth", out var _)) return null;
        double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        double left = width / 2 + reference.X * 1000, back = depth / 2 + reference.Z * 1000;
        return left >= 0 && left <= width && back >= 0 && back <= depth ? (left, width - left, back) : null;
    }
    private string WizardMeasurementSummary() => $"{(WizardValidation ? "Check point" : "Reference point")} · " +
        (Wizard.Answers.GetValueOrDefault("method") == "grid intersections" ? $"grid ({Wizard.Answers.GetValueOrDefault("column", "?")}, {Wizard.Answers.GetValueOrDefault("row", "?")})" :
            $"left {Wizard.Answers.GetValueOrDefault("left", "?")} / back {Wizard.Answers.GetValueOrDefault("back", "?")} mm") +
        $" · jaw-tip midpoint above tabletop {Wizard.Answers.GetValueOrDefault("height", "?")} mm.";

    private string WizardStepName() => Wizard.Step switch
    {
        CalibrationWizardStep.Prepare => "Connect robot", CalibrationWizardStep.ContactPoint => "Choose measuring point", CalibrationWizardStep.ModelPoint => "Compare jaw tips",
        CalibrationWizardStep.DeskFrame => "Choose LEFT edge", CalibrationWizardStep.DeskBack => "Choose BACK edge", CalibrationWizardStep.SceneSurfaceY => "Check simulated tabletop",
        CalibrationWizardStep.SceneFloorOrigin or CalibrationWizardStep.TabletopHeight => "Optional office measurements", CalibrationWizardStep.DeskWidth or CalibrationWizardStep.DeskDepth => "Measure desk",
        CalibrationWizardStep.Precision => "Measurement accuracy", CalibrationWizardStep.Method => "Choose ruler or mat",
        CalibrationWizardStep.GridSquare or CalibrationWizardStep.GridOriginLeft or CalibrationWizardStep.GridOriginBack => "Set up grid mat",
        CalibrationWizardStep.PointIntro or CalibrationWizardStep.PointLeft or CalibrationWizardStep.PointBack or CalibrationWizardStep.PointHeight or CalibrationWizardStep.GridColumn or CalibrationWizardStep.GridRow => WizardValidation ? "Measure check point" : "Measure reference point",
        CalibrationWizardStep.HandGuide => "Move arm by hand", CalibrationWizardStep.Capture => "Record one position", CalibrationWizardStep.Review => "Review alignment",
        CalibrationWizardStep.Complete => "Alignment saved", _ => "Check saved alignment"
    };

    private string WizardReviewSummary()
    {
        if (calibrationFit is not { } fit || calibrationOriginal is not { } original) return "Click Calculate alignment to compare your four reference points with the extra check point. Nothing has been applied yet.";
        var p = fit.ProposedPlacement.Position;
        return $"Simulated base centre: {(original.Position.X + calibrationDesk.Width / 2) * 1000:F0} → {(p.X + calibrationDesk.Width / 2) * 1000:F0} mm from LEFT; " +
            $"{(original.Position.Z + calibrationDesk.Depth / 2) * 1000:F0} → {(p.Z + calibrationDesk.Depth / 2) * 1000:F0} mm from BACK.\n" +
            $"Direction: {original.YawRadians * 180 / Math.PI:F1}° → {fit.ProposedPlacement.YawRadians * 180 / Math.PI:F1}°. Reference point mismatch {fit.TrainingRmsMm:F1} mm; worst {fit.TrainingMaxMm:F1} mm.\n" +
            $"Check point error {fit.ValidationMaxMm:F1} mm; height mismatch {fit.VerticalRmsMm:F1} mm. Your stated precision: ±{calibrationDesk.PrecisionMm:F0} mm. " +
            (fit.CanApply ? "Checks passed. Apply changes the simulation only." : "Checks did not pass. Review the measurements and jaw-tip marker before applying.");
    }

    private async Task SaveWizard(CancellationToken cancellation)
    {
        Directory.CreateDirectory(RobotCalibrationDirectory);
        calibrationWizard = Wizard with { ReportFile = calibrationIdentity is null ? "" : Path.GetFileName(calibrationPath) };
        string temporary = WizardDraftPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(Wizard, PluginProtocol.Json), cancellation);
        File.Move(temporary, WizardDraftPath, true);
        if (calibrationIdentity is not null && calibrationPath.Length > 0)
        {
            var evidence = new { report = Path.GetFileName(calibrationPath), Wizard.ProfileId, Wizard.Answers,
                physicalPoint = Wizard.Answers.GetValueOrDefault("physicalpoint"), operatorSaysTcpMatches = Wizard.OperatorSaysTcpMatches,
                modelCorrespondenceIndependentlyVerified = false, Wizard.ModelAssetId, Wizard.ModelRevision,
                sceneSurfaceYMetres = calibrationDesk.SurfaceY, optionalOfficeAudit = CalibrationWizardFlow.OptionalOfficeAudit(Wizard),
                cameraEvidence = "JPEG preview only; no metric depth was queried", timestampUtc = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(calibrationPath.Replace("-calibration.json", "-wizard-evidence.json"), JsonSerializer.Serialize(evidence, PluginProtocol.Json), cancellation);
        }
    }

    private async Task WizardEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        try { await WizardEventCore(action, value, cancellation); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            wizardMessage = error.Message;
            await Render(cancellation); await Update(cancellation);
            throw;
        }
    }
    private async Task WizardEventCore(string action, JsonElement value, CancellationToken cancellation)
    {
        LoadWizard();
        string[] parts = action.Split('.', 2);
        if (!calibrationOpen || parts.Length != 2 || !long.TryParse(parts[0], out long epoch) || !CalibrationWizardFlow.Accepts(Wizard, ActiveProfile.Id, epoch)) return;
        action = parts[1];
        if (action is "help" or "camera.help") return; // Tooltip only; never advances or edits the draft.
        if (action == "answer")
        {
            string key = CalibrationWizardFlow.Input(Wizard.Step) ?? throw new InvalidOperationException("This question has no editable answer.");
            var answers = new Dictionary<string, string>(Wizard.Answers, StringComparer.Ordinal) { [key] = value.GetString() ?? "" };
            if (answers[key].Length > 512) throw new InvalidOperationException("That answer is too long.");
            calibrationWizard = Wizard with { Answers = answers, OperatorSaysTcpMatches = key == "tool" ? false : Wizard.OperatorSaysTcpMatches,
                ModelAssetId = key == "tool" ? "" : Wizard.ModelAssetId, ModelRevision = key == "tool" ? "" : Wizard.ModelRevision };
            if (key == "tool") { wizardModelPoint = "Point name changed. Read/inspect the named model point again; correspondence is not verified."; Wizard.Answers.Remove("observedrooty"); Wizard.Answers.Remove("sceneconfirmed"); }
            if (key == "sceney") Wizard.Answers.Remove("sceneconfirmed");
            if (key is "sceney" or "width" or "depth" or "tool") { Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("frame"); wizardReferenceAcknowledgedAt = 0; }
            if (key == "floory") Wizard.Answers.Remove("floorconfirmed");
            await SaveWizard(cancellation); return;
        }
        if (action == "camera") { calibrationWizard = Wizard with { HelpCamera = new[] { "bench", "gripper", "depth" }[Math.Clamp(value.GetInt32(), 0, 2)] }; await SaveWizard(cancellation); await Render(cancellation); return; }
        wizardMessage = "";
        if (action == "back") calibrationWizard = CalibrationWizardFlow.Back(Wizard);
        else if (action == "contact" && Wizard.Step == CalibrationWizardStep.ContactPoint)
            Wizard.Answers["physicalpoint"] = "midpoint between both jaw tips (operator selected; model correspondence unverified)";
        else if (action == "method" && Wizard.Step == CalibrationWizardStep.Method)
            Wizard.Answers["method"] = value.GetInt32() == 1 ? "grid intersections" : "edge measurements";
        else if (action == "model.show" && Wizard.Step == CalibrationWizardStep.ModelPoint)
            await ShowWizardModelPoint(cancellation);
        else if (action == "edge.show" && WizardReferenceStep)
            await ShowWizardReference(cancellation);
        else if (action == "model.jawtips" && Wizard.Step == CalibrationWizardStep.ModelPoint && wizardDiscoveredSites.Count(name => name == "calibration_jaw_tips") == 1)
        {
            Wizard.Answers["tool"] = "calibration_jaw_tips";
            calibrationWizard = Wizard with { OperatorSaysTcpMatches = false, ModelAssetId = "", ModelRevision = "" };
            await ShowWizardModelPoint(cancellation);
        }
        else if (action == "scene.root" && Wizard.Step == CalibrationWizardStep.SceneSurfaceY)
        {
            var root = await ReadWizardMetricRoot(cancellation);
            Wizard.Answers["observedrooty"] = Wizard.Answers["sceney"] = CalibrationNumber(root.Position.Y * 1000);
            Wizard.Answers["baseontabletop"] = "operator states the metric base rests on the scene tabletop";
        }
        else if (action == "floor.zero" && Wizard.Step == CalibrationWizardStep.SceneFloorOrigin)
            Wizard.Answers["floory"] = "0";
        else if (action == "next")
        {
            // Re-acknowledge the exact pointer at confirmation time. The host
            // rejects a changed model/subscription even before first capture.
            if (WizardReferenceStep) await ShowWizardReference(cancellation);
            ValidateWizardQuestion(); calibrationWizard = CalibrationWizardFlow.Advance(Wizard);
        }
        else if (action == "cross" && Wizard.Step == CalibrationWizardStep.PointIntro && WizardValidation && WizardCross() is { } cross)
        { Wizard.Answers["method"] = "edge measurements"; Wizard.Answers["left"] = CalibrationNumber(cross.LeftMm); Wizard.Answers["back"] = CalibrationNumber(cross.BackMm); Wizard.Answers["cross"] = "operator chose saved profile cross; verify measured coordinates"; }
        else if (action == "capture" && Wizard.Step == CalibrationWizardStep.Capture)
            await CaptureWizardPoint(cancellation);
        else if (action is "fit" or "apply" or "rollback" && Wizard.Step is CalibrationWizardStep.Review or CalibrationWizardStep.Complete)
        {
            await CalibrationEvent(action, value, cancellation);
            if (action == "apply") calibrationWizard = Wizard with { Step = CalibrationWizardStep.Complete, Epoch = Wizard.Epoch + 1, History = [] };
        }
        else if (action == "inspect" && Wizard.Step == CalibrationWizardStep.InspectSaved)
        { await CalibrationEvent("inspect", value, cancellation); calibrationWizard = Wizard with { Step = CalibrationWizardStep.Review, Epoch = Wizard.Epoch + 1, History = [] }; }
        else if (action == "open") await CalibrationEvent("open", value, cancellation);
        else if (action is "new" or "archive" or "archive.cancel")
        {
            await CalibrationEvent(action, value, cancellation);
            if (calibrationIdentity is null) calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id, Wizard.Epoch + 1);
        }
        else if (action is "add.training" or "add.validation" && Wizard.Step is CalibrationWizardStep.Review or CalibrationWizardStep.Complete)
        {
            if (calibrationApplied is not null || calibrationPlacementUnverified) throw new InvalidOperationException("Roll back or archive the applied placement before collecting another point.");
            calibrationWizard = CalibrationWizardFlow.NextPoint(Wizard, 0, 0);
            Wizard.Answers["role"] = action == "add.training" ? "training" : "validation";
        }
        else return;
        await SaveWizard(cancellation);
        if (WizardReferenceStep && action is ("next" or "back")) await ShowWizardReference(cancellation);
        await Render(cancellation); await Update(cancellation);
    }

    private void ValidateWizardQuestion()
    {
        switch (Wizard.Step)
        {
            case CalibrationWizardStep.Prepare:
                _ = Device(); var current = CurrentCalibrationIdentity();
                if (calibrationIdentity is not null && calibrationIdentity != current) throw new InvalidOperationException("The saved observation connection changed. Archive the old report and start new questions; old samples will not be reused."); break;
            case CalibrationWizardStep.ContactPoint:
                if (!Wizard.Answers.ContainsKey("physicalpoint")) throw new InvalidOperationException("Select the physical midpoint you will measure before continuing."); break;
            case CalibrationWizardStep.ModelPoint:
                if (Wizard.ModelAssetId.Length == 0 || Wizard.ModelRevision.Length == 0) throw new InvalidOperationException("Inspect the exact named model point first. Software cannot infer that it is the physical midpoint.");
                calibrationWizard = Wizard with { OperatorSaysTcpMatches = true }; break;
            case CalibrationWizardStep.DeskFrame:
            case CalibrationWizardStep.DeskBack: ConfirmWizardReference(); break;
            case CalibrationWizardStep.SceneSurfaceY:
                if (calibrationIdentity is not null) throw new InvalidOperationException("Start over before changing a captured session's scene reference.");
                double sceneY = CalibrationWizardFlow.SceneSurface(Wizard, Wizard.Answers.ContainsKey("observedrooty") ? CalibrationWizardFlow.Number(Wizard, "observedrooty", 0, 3000) : null);
                calibrationDesk = calibrationDesk with { SurfaceY = sceneY / 1000 };
                Wizard.Answers["sceneconfirmed"] = "operator explicitly confirmed scene surface reference; no scene transform changed"; break;
            case CalibrationWizardStep.SceneFloorOrigin:
                if (Wizard.Answers.TryGetValue("floory", out var optionalFloor) && !string.IsNullOrWhiteSpace(optionalFloor))
                { _ = CalibrationWizardFlow.Number(Wizard, "floory", -10000, 10000); Wizard.Answers["floorconfirmed"] = "operator supplied optional office floor reference; not used for tabletop fit"; } break;
            case CalibrationWizardStep.DeskWidth: _ = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000); break;
            case CalibrationWizardStep.DeskDepth: _ = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000); break;
            case CalibrationWizardStep.TabletopHeight:
                if (Wizard.Answers.TryGetValue("tableheight", out var optionalHeight) && !string.IsNullOrWhiteSpace(optionalHeight))
                    _ = CalibrationWizardFlow.Number(Wizard, "tableheight", 1, 3000); break;
            case CalibrationWizardStep.Precision:
                _ = CalibrationWizardFlow.Number(Wizard, "precision", 1, 10); break;
            case CalibrationWizardStep.GridSquare: _ = CalibrationWizardFlow.Number(Wizard, "square", 5, 500); break;
            case CalibrationWizardStep.GridOriginLeft: _ = CalibrationWizardFlow.Number(Wizard, "originleft", 0, CalibrationWizardFlow.Number(Wizard, "width", 100, 10000)); break;
            case CalibrationWizardStep.GridOriginBack: _ = CalibrationWizardFlow.Number(Wizard, "originback", 0, CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000)); break;
            case CalibrationWizardStep.PointLeft: _ = CalibrationWizardFlow.Number(Wizard, "left", 0, CalibrationWizardFlow.Number(Wizard, "width", 100, 10000)); break;
            case CalibrationWizardStep.PointBack: _ = CalibrationWizardFlow.Number(Wizard, "back", 0, CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000)); break;
            case CalibrationWizardStep.PointHeight: _ = CalibrationWizardFlow.Number(Wizard, "height", 0, 1000); break;
            case CalibrationWizardStep.GridColumn: _ = CalibrationWizardFlow.Number(Wizard, "column", 0, 200, true); break;
            case CalibrationWizardStep.GridRow: _ = CalibrationWizardFlow.Number(Wizard, "row", 0, 200, true); break;
            case CalibrationWizardStep.HandGuide:
                var device = Device(); if (!TeachingState.Inspect(device).HandGuiding) throw new InvalidOperationException("Press the physical teach button for solid green hand guiding; no software mode change will be sent."); break;
        }
    }
    private async Task ShowWizardModelPoint(CancellationToken cancellation)
    {
        var current = link ?? throw new InvalidOperationException("Link this robot before inspecting a model point.");
        string name = Wizard.Answers.GetValueOrDefault("tool", "").Trim();
        var reply = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId }, cancellation);
        var pose = JsonNode.Parse(reply.GetRawText())!.AsObject();
        wizardDiscoveredSites = pose["toolSites"]?.AsArray().Select(s => s?["name"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [];
        var sites = pose["toolSites"]?.AsArray().Where(s => s?["name"]?.GetValue<string>() == name).ToArray() ?? [];
        if (!ReferenceEquals(link, current) || pose["sceneId"]?.GetValue<string>() != current.SceneId || pose["entityId"]?.GetValue<string>() != current.EntityId ||
            pose["metricPlacementSupported"]?.GetValue<bool>() != true || pose["metricFrame"]?.GetValue<string>() != "engine-world-y-up" ||
            sites.Length != 1 || sites[0]?["unit"]?.GetValue<string>() != "m" || sites[0]?["position"] is not JsonArray { Count: 3 } xyz ||
            xyz.Any(v => v is null || !double.IsFinite(v.GetValue<double>())) || pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } asset || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } revision)
            throw new InvalidOperationException("The current model does not expose one exact named metric site. Select/fix the correct model point before continuing.");
        calibrationWizard = Wizard with { ModelAssetId = asset, ModelRevision = revision, OperatorSaysTcpMatches = false };
        Wizard.Answers["observedrooty"] = CalibrationNumber(CalibrationPlacement.Read(pose["rootPlacement"]).Position.Y * 1000);
        var coordinates = sites[0]!["position"]!.AsArray().Select(v => v!.GetValue<double>() * 1000).ToArray();
        wizardModelPoint = "Look for the marker between the simulated jaw tips, then compare it with the real jaw-tip midpoint.";
        wizardModelPointDetails = $"Marker coordinates ({coordinates[0]:F1}, {coordinates[1]:F1}, {coordinates[2]:F1}) mm. Site '{name}' world coordinates {sites[0]!["position"]!.ToJsonString()} m; compiled model {asset}, revision {revision}. The marker identifies a model point only. Continue records the operator's comparison, not independent physical calibration.";
        await ShowWizardHighlight(cancellation);
    }

    private async Task<CalibrationPlacement> ReadWizardMetricRoot(CancellationToken cancellation)
    {
        var current = link ?? throw new InvalidOperationException("Link this robot before using its scene root as a reference.");
        var reply = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId }, cancellation);
        var pose = JsonNode.Parse(reply.GetRawText())!.AsObject();
        if (!ReferenceEquals(link, current) || pose["sceneId"]?.GetValue<string>() != current.SceneId || pose["entityId"]?.GetValue<string>() != current.EntityId ||
            pose["metricPlacementSupported"]?.GetValue<bool>() != true || pose["metricFrame"]?.GetValue<string>() != "engine-world-y-up" ||
            pose["modelAssetId"]?.GetValue<string>() != Wizard.ModelAssetId || pose["modelRevision"]?.GetValue<string>() != Wizard.ModelRevision)
            throw new InvalidOperationException("The model/reference changed. Read and compare the named model point again before confirming a scene surface.");
        return CalibrationPlacement.Read(pose["rootPlacement"]);
    }
    private async Task CaptureWizardPoint(CancellationToken cancellation)
    {
        if (!Wizard.OperatorSaysTcpMatches || !Wizard.Answers.ContainsKey("physicalpoint") || !Wizard.Answers.ContainsKey("frame") ||
            !Wizard.Answers.ContainsKey("sceneconfirmed"))
            throw new InvalidOperationException("Identify the fixed physical/model point and scene desk frame before capturing.");
        var tabletop = CalibrationWizardFlow.TabletopInputs(Wizard);
        string deskProof = WizardReferenceHighlightScope.DeskFingerprint(WizardReferenceIdentity(), CalibrationWizardFlow.Number(Wizard, "width", 100, 10000),
            CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000), CalibrationWizardFlow.SceneSurface(Wizard));
        if (Wizard.Answers.GetValueOrDefault("leftedgeconfirmed") != deskProof || Wizard.Answers.GetValueOrDefault("frame") != deskProof)
            throw new InvalidOperationException("The LEFT/BACK pointers have not been confirmed for this robot and these desk measurements. Start new questions and compare both desk edges before capturing more points; the saved report is retained.");
        if (Math.Abs(CalibrationWizardFlow.SceneSurface(Wizard) - calibrationDesk.SurfaceY * 1000) > .001)
            throw new InvalidOperationException("The saved scene reference changed. Start new questions before mixing measurements from different scene surfaces.");
        foreach (string key in new[] { "tool", "width", "depth", "precision", "left", "back", "height", "square", "originleft", "originback", "column", "row" })
            calibrationInputs[key] = Wizard.Answers.GetValueOrDefault(key, key == "tool" ? "grasp_site" : "0");
        calibrationInputs["surface"] = CalibrationNumber(calibrationDesk.SurfaceY * 1000);
        calibrationMode = Wizard.Answers.GetValueOrDefault("method", "edge measurements");
        ReadCalibrationInputs(calibrationIdentity is null);
        if (calibrationDesk != tabletop.Desk) throw new InvalidOperationException("The captured session's fixed tabletop settings changed. Start new questions before mixing references.");
        _ = calibrationMeasurement.World(calibrationDesk);
        if (!WizardValidation && WizardCross() is { } cross && calibrationMeasurement.World(calibrationDesk).PlanarDistance(calibrationDesk.World(cross.LeftMm, cross.BackMm, 0)) < .05)
            throw new InvalidOperationException("Keep the saved profile cross and its nearby5cm for independent validation. Choose a different training location.");
        calibrationTcpConfirmed = true;
        if (calibrationIdentity is null) { await CalibrationEvent("start", default, cancellation); await SaveWizard(cancellation); }
        if (Math.Abs(calibrationOriginal!.Position.Y - calibrationDesk.SurfaceY) > .001)
            throw new InvalidOperationException("The current scene root is not on the confirmed scene tabletop Y. Edit/verify scene desk/root setup, then start over; no point or scene transform was applied.");
        if (calibrationIdentity!.ModelAssetId != Wizard.ModelAssetId || calibrationIdentity.ModelRevision != Wizard.ModelRevision)
            throw new InvalidOperationException("The compiled model changed since the point comparison. Start over and identify the point on the current model.");
        await CalibrationEvent(WizardValidation ? "validate" : "capture", default, cancellation);
        calibrationWizard = CalibrationWizardFlow.NextPoint(Wizard, WizardTrainingCount, WizardValidationCount);
    }

    private void AddWizardPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        if (!calibrationOpen || calibrationWizard is null || wizardProfile != ActiveProfile.Id) return;
        patches["wizard.status"] = new() { ["text"] = wizardMessage.Length > 0 ? wizardMessage : robotCalibrationStatus };
        bool captureReady = device is not null && TeachingState.Inspect(device).HandGuiding && device["status"]?["stationary_s"]?.GetValue<double>() is >= .4;
        bool sameScope = calibrationIdentity is null || link is not null && calibrationIdentity == CurrentCalibrationIdentity();
        patches[WizardId("capture")] = new() { ["enabled"] = Wizard.Step == CalibrationWizardStep.Capture && captureReady && sameScope && !calibrationPlacementUnverified };
        patches[WizardId("next")] = new() { ["enabled"] = Wizard.Step is not (CalibrationWizardStep.Capture or CalibrationWizardStep.Review or CalibrationWizardStep.Complete or CalibrationWizardStep.InspectSaved) };
        if (WizardReferenceStep)
        {
            bool shown = WizardReferenceShown();
            patches[WizardId("next")] = new() { ["enabled"] = shown };
            patches["wizard.edge.status"] = new() { ["text"] = shown ? "Pointer shown. Confirm only if it marks the matching desk edge." : "Waiting for the simulation pointer. Use Show edge to retry; confirmation stays unavailable." };
        }
        patches[WizardId("back")] = new() { ["enabled"] = Wizard.History.Length > 0 };
        patches[WizardId("fit")] = new() { ["enabled"] = WizardTrainingCount >= 4 && !calibrationPlacementUnverified };
        patches[WizardId("apply")] = new() { ["enabled"] = captureReady && sameScope && calibrationFit?.CanApply == true && calibrationApplied is null && !calibrationPlacementUnverified };
        patches[WizardId("rollback")] = new() { ["enabled"] = sameScope && calibrationApplied is not null && !calibrationPlacementUnverified };
        patches["wizard.camera.image"] = new() { ["visible"] = CameraFrameReady(Wizard.HelpCamera) };
    }
}
