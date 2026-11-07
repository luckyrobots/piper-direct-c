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
        sheetEditingRow = -1;
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
                    calibrationReevaluation = report.ModelReevaluation;
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
        ReconcileReevaluatedWizard();
    }

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
    private (double LeftMm, double RightMm, double BackMm)? WizardCross()
    {
        var reference = ActiveProfile.Alignment;
        if (reference is null || !reference.Name.Contains("cross", StringComparison.OrdinalIgnoreCase) ||
            !Wizard.Answers.TryGetValue("width", out var _) || !Wizard.Answers.TryGetValue("depth", out var _)) return null;
        double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        double left = width / 2 + reference.X * 1000, back = depth / 2 + reference.Z * 1000;
        return left >= 0 && left <= width && back >= 0 && back <= depth ? (left, width - left, back) : null;
    }

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
        return $"Bottom joint (J1) rotation axis: {(original.Position.X + calibrationDesk.Width / 2) * 1000:F0} → {(p.X + calibrationDesk.Width / 2) * 1000:F0} mm from LEFT; " +
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
        sheetSaveStatus = "Saved locally · measurements restore next session.";
        if (calibrationIdentity is null && BenchReference.FromAnswers(Wizard.Answers, calibrationDesk.SurfaceY) is { } shared && preferences.BenchReference != shared)
        { preferences = preferences with { BenchReference = shared }; await Save(cancellation); }
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
        EnsureIndependentCalibration();
        try { await WizardEventCore(action, value, cancellation); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            wizardMessage = error.Message;
            // Sheet validation errors must not destroy the active input view.
            if (!action.Contains(".sheet.", StringComparison.Ordinal)) await Render(cancellation);
            await Update(cancellation);
            throw;
        }
    }
    private async Task WizardEventCore(string action, JsonElement value, CancellationToken cancellation)
    {
        LoadWizard();
        string[] parts = action.Split('.', 2);
        if (!calibrationOpen || parts.Length != 2 || !long.TryParse(parts[0], out long epoch) || !CalibrationWizardFlow.Accepts(Wizard, ActiveProfile.Id, epoch)) return;
        action = parts[1];
        if (action.StartsWith("sheet.", StringComparison.Ordinal)) { await CalibrationSheetEvent(action[6..], value, cancellation); return; }
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
            if (key is "sceney" or "width" or "depth" or "tool") { Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("edgeroot"); wizardReferenceAcknowledgedAt = 0; }
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
            Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("edgeroot"); wizardReferenceAcknowledgedAt = 0;
        }
        else if (action == "floor.zero" && Wizard.Step == CalibrationWizardStep.SceneFloorOrigin)
            Wizard.Answers["floory"] = "0";
        else if (action == "next")
        {
            // Re-acknowledge the exact pointer at confirmation time. The host
            // rejects a changed model/subscription even before first capture.
            if (WizardReferenceStep) await ShowWizardReference(cancellation, frame: false);
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
        bool samePoint = Wizard.ModelAssetId == asset && Wizard.ModelRevision == revision &&
            (wizardHighlightScope is null || wizardHighlightScope.SiteName == name);
        calibrationWizard = Wizard with { ModelAssetId = asset, ModelRevision = revision, OperatorSaysTcpMatches = samePoint && Wizard.OperatorSaysTcpMatches };
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
        if (!Wizard.Answers.TryGetValue("edgeroot", out string? savedEdgeRoot))
            throw new InvalidOperationException("Confirm the LEFT and BACK pointers before capturing. Start new questions if this saved draft used the older desk setup.");
        var edgeRoot = CalibrationPlacement.Read(JsonNode.Parse(savedEdgeRoot));
        long referenceReadStart = Stopwatch.GetTimestamp(), questionEpoch = Wizard.Epoch;
        var referenceIdentity = WizardReferenceIdentity();
        using (var referenceDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            referenceDeadline.CancelAfter(TimeSpan.FromMilliseconds(750));
            var currentRoot = await ReadWizardMetricRoot(referenceDeadline.Token);
            _ = Device(referenceIdentity.DeviceId, referenceIdentity.Generation);
            if (!calibrationOpen || questionEpoch != Wizard.Epoch || referenceIdentity != WizardReferenceIdentity() || Stopwatch.GetElapsedTime(referenceReadStart).TotalSeconds > .5 || !edgeRoot.Same(currentRoot))
                throw new InvalidOperationException("The simulated robot placement or connection changed after the desk edges were confirmed. Start new questions; the saved report is retained.");
        }
        string deskProof = WizardReferenceHighlightScope.DeskFingerprint(WizardReferenceIdentity(), CalibrationWizardFlow.Number(Wizard, "width", 100, 10000),
            CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000), CalibrationWizardFlow.SceneSurface(Wizard), edgeRoot);
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
        AddCalibrationSheetPatches(patches, device);
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
