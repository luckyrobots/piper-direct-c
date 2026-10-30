using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private CalibrationWizardDraft? calibrationWizard;
    private string[] wizardDiscoveredSites = [];
    private string wizardProfile = "", wizardMessage = "", wizardModelPoint = "Not inspected. Software has not verified the physical/model point correspondence.";
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
        wizardProfile = ActiveProfile.Id; wizardMessage = ""; wizardDiscoveredSites = []; wizardModelPoint = "Not inspected. Software has not verified the physical/model point correspondence.";
        calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id);
        try
        {
            if (File.Exists(WizardDraftPath) && new FileInfo(WizardDraftPath).Length <= 64 * 1024 &&
                JsonSerializer.Deserialize<CalibrationWizardDraft>(File.ReadAllText(WizardDraftPath), PluginProtocol.Json) is { } saved)
            {
                CalibrationWizardFlow.ValidateDraft(saved, ActiveProfile.Id);
                calibrationWizard = saved with { Epoch = saved.Epoch + 1 };
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
                wizardMessage = "Saved questions/observations recovered. Measurements are drafts until you explicitly capture. A changed robot/backend/model cannot reuse old samples.";
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
            Widget.Label("wizard.progress", $"{WizardTrainingCount}/4 training · {WizardValidationCount}/1 new validation · {step}") with { Height = 30, Appearance = "muted" },
            Widget.Row("wizard.prompt", Widget.Label("wizard.question", WizardPrompt()) with { Height = 60, Appearance = "heading" },
                WizardButton("help", "(i)") with { Width = 32, Padding = 4, Appearance = "quiet", Hint = WizardQuestion() }),
        };
        if (step is CalibrationWizardStep.HandGuide or CalibrationWizardStep.Capture)
            children.Add(Widget.Label("wizard.capture.essentials", step == CalibrationWizardStep.HandGuide
                ? "Solid green = hand guiding. The controller manages holding torque; support the arm as needed and hold the measured point still for at least 0.4 seconds."
                : "Keep support as needed and hold still in hand guiding for at least 0.4 seconds. Capture only reads this point; it never moves, enables or resets the arm.") with { Height = 50 });
        string? input = CalibrationWizardFlow.Input(step);
        if (step == CalibrationWizardStep.Prepare)
            children.Add(link is null ? Widget.Button("link", "Link robot · measured pose only") with { Appearance = "primary" } : Widget.Label("wizard.linked", "Selected real robot and scene are linked; Continue is read-only."));
        if (input is not null) children.Add(new Widget("input", WizardId("answer")) { Text = Wizard.Answers.GetValueOrDefault(input, ""), Hint = input == "tool" ? "Exact case-sensitive model site name" : "Measured millimetres; decimal point. Blank means unknown." });
        if (step == CalibrationWizardStep.ContactPoint)
        {
            children.Add(Widget.Label("wizard.contact.diagram", "left jaw tip o-----+-----o right jaw tip\n                  measured midpoint") with { Height = 42, Appearance = "numeric" });
            children.Add(WizardButton("contact", Wizard.Answers.ContainsKey("physicalpoint") ? "Both jaw tips midpoint selected" : "Use midpoint between BOTH jaw tips"));
        }
        if (step == CalibrationWizardStep.ModelPoint)
        {
            children.Add(WizardButton("model.show", "Read coordinates + show temporary model marker"));
            if (wizardDiscoveredSites.Count(name => name == "calibration_jaw_tips") == 1)
                children.Add(WizardButton("model.jawtips", "Use model jaw-tip reference · inspect and confirm"));
            children.Add(Widget.Label("wizard.model.point", wizardModelPoint) with { Height = 58 });
        }
        if (step == CalibrationWizardStep.TabletopHeight)
            children.Add(Widget.Label("wizard.height.scene", $"Confirmed scene surface Y: {calibrationDesk.SurfaceY * 1000:F0} mm. Scene floor reference: {Wizard.Answers.GetValueOrDefault("floory", "unknown")} mm. Physical ruler height remains separate; no scene object moves.") with { Height = 42 });
        if (step == CalibrationWizardStep.SceneSurfaceY)
            children.Add(WizardButton("scene.root", "Base sits on scene tabletop: use current metric root Y"));
        if (step == CalibrationWizardStep.SceneFloorOrigin)
            children.Add(WizardButton("floor.zero", "My scene floor and physical floor origin both use Y=0"));
        if (step == CalibrationWizardStep.Method)
            children.Add(new Widget("dropdown", WizardId("method")) { Options = ["Measure from desk edges", "Aligned grid mat intersections"], Value = Wizard.Answers.GetValueOrDefault("method") == "grid intersections" ? 1 : 0 });
        if (step == CalibrationWizardStep.PointIntro)
        {
            if (WizardValidation && WizardCross() is { } cross)
                children.Add(WizardButton("cross", $"Use saved cross with edge measurements · right {cross.RightMm:F0} / back {cross.BackMm:F0} mm (verify)"));
            children.Add(Widget.Label("wizard.point.role", WizardValidation ? "This NEW point is held out of the fit. Keep it at least 5 cm from every training point." :
                "Choose a distinct training point. Spread four points in both desk directions with at least 10 cm overall span; keep the saved cross for validation.") with { Height = 48 });
        }
        if (step == CalibrationWizardStep.Capture)
        {
            children.Add(Widget.Label("wizard.measurement", WizardMeasurementSummary()) with { Height = 48 });
            children.Add(WizardButton("capture", WizardValidation ? "Capture this NEW validation point" : "Capture this training point"));
        }
        if (step is CalibrationWizardStep.Review or CalibrationWizardStep.Complete)
        {
            children.Add(Widget.Label("wizard.review", CalibrationPreview()) with { Height = 116 });
            children.Add(Widget.Row("wizard.review.actions", WizardButton("fit", "Fit / review residuals"), WizardButton("apply", "Apply to SCENE only"), WizardButton("rollback", "Roll back scene placement")));
            children.Add(Widget.Row("wizard.more.actions", WizardButton("add.training", "Add training point"), WizardButton("add.validation", "Add validation point"), WizardButton("open", "Open saved report")));
        }
        if (step == CalibrationWizardStep.InspectSaved)
            children.Add(Widget.Row("wizard.inspect.actions", WizardButton("inspect", "Check scene placement (read-only)"), WizardButton("open", "Open saved report")));
        if (calibrationArchiveConfirmation)
            children.Add(Widget.Column("wizard.archive", Widget.Label("wizard.archive.warning", "Keep the saved report and archive its pending/applied rollback context? This does not undo or repeat a scene write.") with { Height = 44 },
                Widget.Row("wizard.archive.actions", WizardButton("archive", "Keep report & start new questions"), WizardButton("archive.cancel", "Cancel"))));
        children.Add(Widget.Row("wizard.navigation", WizardButton("back", "Previous question"), WizardButton("next", WizardNextLabel()), WizardButton("new", "Start over / edit setup")));
        children.Add(Widget.Label("wizard.status", wizardMessage.Length > 0 ? wizardMessage : robotCalibrationStatus) with { Height = 58 });
        var help = new List<Widget> { Widget.Column("wizard.camera.header", Widget.Label("wizard.camera.title", "Camera help · " + Wizard.HelpCamera),
            new Widget("dropdown", WizardId("camera")) { Options = ["Shared bench", "Pika gripper", "D405 camera (current view)"], Value = Array.IndexOf(new[] { "bench", "gripper", "depth" }, Wizard.HelpCamera) }) };
        help.Add(new Widget("image", "wizard.camera.image") { ImageId = CameraImageId(Wizard.HelpCamera), Height = 180, Visible = CameraFrameReady(Wizard.HelpCamera) });
        help.Add(Widget.Row("wizard.camera.caption", Widget.Label("wizard.camera.note", "Live preview") with { Appearance = "muted" },
            WizardButton("help", "(i)") with { Id = WizardId("camera.help"), Width = 32, Padding = 4, Appearance = "quiet",
                Hint = "Existing JPEG preview only. It does not measure distance or prove physical/model point correspondence. This page reuses the selected running camera; it does not open another capture." }));
        if (step is >= CalibrationWizardStep.DeskFrame and <= CalibrationWizardStep.GridRow)
            help.Add(Widget.Label("wizard.desk.diagram", "             BACK (-Z)\n        +----------------+\n LEFT   |       | back   | RIGHT\n (-X)   | left-- P       | (+X)\n        +----------------+\n             FRONT (+Z)\n    point height: above tabletop") with { Height = 148, Appearance = "numeric" });
        return Widget.Row("wizard.page", Widget.Column("wizard.body", children.ToArray()) with { Padding = 16, Spacing = 12, Appearance = "panel" },
            Widget.Column("wizard.help", help.ToArray()) with { Width = 320, Padding = 12, Spacing = 8, Appearance = "panel" }) with { Spacing = 14 };
    }

    private string WizardQuestion() => Wizard.Step switch
    {
        CalibrationWizardStep.Prepare => "Prepare this robot: confirm its assigned CAN adapter and scene model, then Link robot in Controls. The wizard remains read-only; no Enable, reset or target is sent.",
        CalibrationWizardStep.ContactPoint => "Which physical point will you measure at every location? Use the midpoint between BOTH jaw tips, not one tilted fingertip or the camera lens. Software has not verified its matching model site.",
        CalibrationWizardStep.ModelPoint => "What exact named model point represents the physical midpoint? Read its coordinates and inspect the temporary scene marker, then report correspondence. A grasp centre may be behind the jaw tips; a name or marker alone does not verify the physical TCP.",
        CalibrationWizardStep.DeskFrame => "Confirm the scene desk frame: centre X=0/Z=0, width along X, back edge at −Z. This wizard does not infer a desk transform from a camera. Fix a different/rotated scene desk before continuing.",
        CalibrationWizardStep.SceneSurfaceY => "What is the SCENE tabletop's world Y, in millimetres? Enter it explicitly. Only if the metric robot base actually rests on that scene tabletop may you use its current root Y below. This reference does not move the scene or come from a ruler.",
        CalibrationWizardStep.SceneFloorOrigin => "What scene-world Y corresponds to your physical floor reference, in millimetres? Explicitly choose zero if origins match, or enter the known world-floor offset. Ruler height will be compared to scene tabletop Y minus this reference.",
        CalibrationWizardStep.DeskWidth => "What is the full tabletop width, from left edge to right edge, in millimetres? Enter your measured value.",
        CalibrationWizardStep.DeskDepth => "What is the full tabletop depth, from back edge to front edge, in millimetres? Enter your measured value.",
        CalibrationWizardStep.TabletopHeight => "How high is the physical tabletop above the floor, in millimetres? UNKNOWN until you measure it. The value must agree with the established scene surface; a mismatch needs separate scene setup.",
        CalibrationWizardStep.Precision => "How precisely can you measure these points, in millimetres (1–10)? Enter an honest uncertainty; this controls the fit's rejection thresholds, not a promise of physical accuracy.",
        CalibrationWizardStep.Method => "How will you locate each point: measure its distance from the desk edges, or use numbered intersections on a grid mat? No object is required.",
        CalibrationWizardStep.GridSquare => "What is the grid square size in millimetres? Verify with a ruler. Secure the mat flat and parallel to the desk edges; rotated grids are not supported.",
        CalibrationWizardStep.GridOriginLeft => "How far is grid intersection (column 0, row 0) from the desk's LEFT edge, in millimetres? Columns must increase toward the right.",
        CalibrationWizardStep.GridOriginBack => "How far is grid intersection (column 0, row 0) from the desk's BACK edge, in millimetres? Rows must increase toward the front.",
        CalibrationWizardStep.PointIntro => WizardValidation ? "Choose a NEW validation point, not used in training. Your saved profile cross may be used after checking its measurements." : $"Choose training point {WizardTrainingCount + 1} of at least four. Do not use the reserved validation cross yet.",
        CalibrationWizardStep.PointLeft => "At this point, what is the distance from the physical desk's LEFT edge to the defined contact point, in millimetres?",
        CalibrationWizardStep.PointBack => "At this point, what is the distance from the physical desk's BACK edge to the same defined contact point, in millimetres?",
        CalibrationWizardStep.PointHeight => "How high is that contact point above the tabletop, in millimetres? Enter 0 only if it is actually on the bare desk. Include grid-mat thickness; do not assume zero.",
        CalibrationWizardStep.GridColumn => "Which numbered grid intersection COLUMN is this point? Use a whole number from column 0 at the measured origin; not a square centre.",
        CalibrationWizardStep.GridRow => "Which numbered grid intersection ROW is this point? Use a whole number from row 0 at the measured origin; not a square centre.",
        CalibrationWizardStep.HandGuide => "Single-click the arm's physical button for solid green hand guiding. Bring the defined midpoint to your measured location by hand. Keep support as needed and hold still at least 0.4 seconds. The scene should follow measured joints.",
        CalibrationWizardStep.Capture => "Ready to record this one point? Check the entered measurements and both views. Capture is an explicit action, requires fresh healthy stationary hand guiding, and never moves the arm.",
        CalibrationWizardStep.Review => "Review every residual and the proposed scene base correction. Four training points and a separate passing validation point are required. Only an explicit Apply changes scene placement; no physical target is sent.",
        CalibrationWizardStep.Complete => "Scene placement was applied. The real arm was not commanded. Check another point if needed; camera calibration, TCP accuracy and synchronized recording remain separate requirements.",
        _ => "A saved scene write/applied placement needs read-only inspection. No Apply, rollback or motion resumes automatically after a restart."
    };
    private string WizardPrompt() => Wizard.Step switch
    {
        CalibrationWizardStep.Prepare => "Link the selected real robot and scene model.",
        CalibrationWizardStep.ContactPoint => "Which physical point will you measure?",
        CalibrationWizardStep.ModelPoint => "Which model site matches that physical point?",
        CalibrationWizardStep.DeskFrame => "Confirm the scene desk's left/back coordinate frame.",
        CalibrationWizardStep.SceneSurfaceY => "What is the scene tabletop's world Y in millimetres?",
        CalibrationWizardStep.SceneFloorOrigin => "What scene-world Y represents the physical floor?",
        CalibrationWizardStep.DeskWidth => "What is the measured tabletop width in millimetres?",
        CalibrationWizardStep.DeskDepth => "What is the measured tabletop depth in millimetres?",
        CalibrationWizardStep.TabletopHeight => "How high is the physical tabletop above the floor?",
        CalibrationWizardStep.Precision => "What is your measurement precision in millimetres?",
        CalibrationWizardStep.Method => "Will you use edge measurements or a grid mat?",
        CalibrationWizardStep.GridSquare => "What is the verified grid-square size in millimetres?",
        CalibrationWizardStep.GridOriginLeft => "How far is grid origin (0,0) from the left edge?",
        CalibrationWizardStep.GridOriginBack => "How far is grid origin (0,0) from the back edge?",
        CalibrationWizardStep.PointIntro => WizardValidation ? "Choose a new point for independent validation." : $"Choose distinct training point {WizardTrainingCount + 1}.",
        CalibrationWizardStep.PointLeft => "How far is this point from the desk's left edge?",
        CalibrationWizardStep.PointBack => "How far is this point from the desk's back edge?",
        CalibrationWizardStep.PointHeight => "How high is this point above the bare tabletop?",
        CalibrationWizardStep.GridColumn => "Which numbered grid column contains this intersection?",
        CalibrationWizardStep.GridRow => "Which numbered grid row contains this intersection?",
        CalibrationWizardStep.HandGuide => "Guide the measured point by hand and hold it still.",
        CalibrationWizardStep.Capture => "Capture this measured point when you are ready.",
        CalibrationWizardStep.Review => "Review the residuals before changing scene placement.",
        CalibrationWizardStep.Complete => "The scene placement is saved; the real arm was not moved.",
        _ => "Inspect the saved scene placement before continuing."
    };
    private string WizardNextLabel() => Wizard.Step switch
    {
        CalibrationWizardStep.ModelPoint => "I compared the physical/model points",
        CalibrationWizardStep.DeskFrame => "This scene desk uses that frame",
        CalibrationWizardStep.SceneSurfaceY => "I confirm this scene tabletop reference",
        CalibrationWizardStep.HandGuide => "I am holding the measured point",
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
    private string WizardMeasurementSummary() => $"{(WizardValidation ? "Held-out validation" : "Training")} · " +
        (Wizard.Answers.GetValueOrDefault("method") == "grid intersections" ? $"grid ({Wizard.Answers.GetValueOrDefault("column", "?")}, {Wizard.Answers.GetValueOrDefault("row", "?")})" :
            $"left {Wizard.Answers.GetValueOrDefault("left", "?")} / back {Wizard.Answers.GetValueOrDefault("back", "?")} mm") +
        $" · above desk {Wizard.Answers.GetValueOrDefault("height", "?")} mm · named site {Wizard.Answers.GetValueOrDefault("tool", "?")}.";

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
                sceneSurfaceYMetres = calibrationDesk.SurfaceY, cameraEvidence = "JPEG preview only; no metric depth was queried", timestampUtc = DateTimeOffset.UtcNow };
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
        else if (action == "next") { ValidateWizardQuestion(); calibrationWizard = CalibrationWizardFlow.Advance(Wizard); }
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
        await SaveWizard(cancellation); await Render(cancellation); await Update(cancellation);
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
            case CalibrationWizardStep.DeskFrame: Wizard.Answers["frame"] = "operator confirmed centre X/Z0, width+X, back-Z"; break;
            case CalibrationWizardStep.SceneSurfaceY:
                if (calibrationIdentity is not null) throw new InvalidOperationException("Start over before changing a captured session's scene reference.");
                double sceneY = CalibrationWizardFlow.SceneSurface(Wizard, Wizard.Answers.ContainsKey("observedrooty") ? CalibrationWizardFlow.Number(Wizard, "observedrooty", 0, 3000) : null);
                calibrationDesk = calibrationDesk with { SurfaceY = sceneY / 1000 };
                Wizard.Answers["sceneconfirmed"] = "operator explicitly confirmed scene surface reference; no scene transform changed"; break;
            case CalibrationWizardStep.SceneFloorOrigin:
                _ = CalibrationWizardFlow.Number(Wizard, "floory", -10000, 10000);
                Wizard.Answers["floorconfirmed"] = "operator supplied scene-world Y for physical floor reference"; break;
            case CalibrationWizardStep.DeskWidth: _ = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000); break;
            case CalibrationWizardStep.DeskDepth: _ = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000); break;
            case CalibrationWizardStep.TabletopHeight:
                CalibrationWizardFlow.ValidateTabletopHeight(Wizard, 10); break;
            case CalibrationWizardStep.Precision:
                double precision = CalibrationWizardFlow.Number(Wizard, "precision", 1, 10);
                CalibrationWizardFlow.ValidateTabletopHeight(Wizard, precision); break;
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
        wizardModelPoint = $"Model site '{name}': {sites[0]!["position"]!.ToJsonString()} m. The marker identifies only the model point; physical correspondence remains unverified.";
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
            !Wizard.Answers.ContainsKey("sceneconfirmed") || !Wizard.Answers.ContainsKey("floorconfirmed"))
            throw new InvalidOperationException("Identify the fixed physical/model point and scene desk frame before capturing.");
        double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        double precision = CalibrationWizardFlow.Number(Wizard, "precision", 1, 10);
        CalibrationWizardFlow.ValidateTabletopHeight(Wizard, precision);
        if (Math.Abs(CalibrationWizardFlow.SceneSurface(Wizard) - calibrationDesk.SurfaceY * 1000) > .001)
            throw new InvalidOperationException("The saved scene reference changed. Start new questions before mixing measurements from different scene surfaces.");
        _ = CalibrationWizardFlow.Number(Wizard, "height", 0, 1000);
        bool grid = Wizard.Answers.GetValueOrDefault("method") == "grid intersections";
        if (grid)
        {
            _ = CalibrationWizardFlow.Number(Wizard, "square", 5, 500);
            _ = CalibrationWizardFlow.Number(Wizard, "originleft", 0, width); _ = CalibrationWizardFlow.Number(Wizard, "originback", 0, depth);
            _ = CalibrationWizardFlow.Number(Wizard, "column", 0, 200, true); _ = CalibrationWizardFlow.Number(Wizard, "row", 0, 200, true);
        }
        else { _ = CalibrationWizardFlow.Number(Wizard, "left", 0, width); _ = CalibrationWizardFlow.Number(Wizard, "back", 0, depth); }
        foreach (string key in new[] { "tool", "width", "depth", "precision", "left", "back", "height", "square", "originleft", "originback", "column", "row" })
            calibrationInputs[key] = Wizard.Answers.GetValueOrDefault(key, key == "tool" ? "grasp_site" : "0");
        calibrationInputs["surface"] = CalibrationNumber(calibrationDesk.SurfaceY * 1000);
        calibrationMode = Wizard.Answers.GetValueOrDefault("method", "edge measurements");
        ReadCalibrationInputs(calibrationIdentity is null);
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
        patches[WizardId("back")] = new() { ["enabled"] = Wizard.History.Length > 0 };
        patches[WizardId("fit")] = new() { ["enabled"] = WizardTrainingCount >= 4 && !calibrationPlacementUnverified };
        patches[WizardId("apply")] = new() { ["enabled"] = captureReady && sameScope && calibrationFit?.CanApply == true && calibrationApplied is null && !calibrationPlacementUnverified };
        patches[WizardId("rollback")] = new() { ["enabled"] = sameScope && calibrationApplied is not null && !calibrationPlacementUnverified };
        patches["wizard.camera.image"] = new() { ["visible"] = CameraFrameReady(Wizard.HelpCamera) };
    }
}
