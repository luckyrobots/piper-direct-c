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
                JsonSerializer.Deserialize<CalibrationWizardFile>(File.ReadAllText(WizardDraftPath), PluginProtocol.Json) is { } file)
            {
                var saved = file.ToDraft();
                CalibrationWizardFlow.ValidateDraft(saved, ActiveProfile.Id);
                calibrationWizard = CalibrationWizardFlow.TabletopDefault(saved);
                // Restore partial observations only from the draft's named report.
                // Every later capture/write still rechecks the live pinned scope.
                string reportPath = Path.Combine(RobotCalibrationDirectory, saved.ReportFile);
                if (calibrationIdentity is null && saved.ReportFile.Length > 0 && File.Exists(reportPath) &&
                    JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(reportPath), PluginProtocol.Json) is { } report)
                    RestoreCalibrationReport(report, reportPath);
                wizardMessage = "Your saved answers and captured points are available. Nothing moves or applies automatically. If the robot connection or model changed, start a new check.";
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id); wizardMessage = "Saved draft could not be resumed; the report is retained. " + error.Message; }
        if (calibrationPlacementUnverified)
            calibrationWizard = Wizard with { Step = CalibrationWizardStep.InspectSaved, Epoch = Wizard.Epoch + 1 };
        else if (calibrationSamples.LastOrDefault() is { } last && CalibrationWizardFlow.AlreadyRecorded(Wizard, last))
        { calibrationWizard = CalibrationWizardFlow.NextPoint(Wizard, WizardTrainingCount, WizardValidationCount); wizardMessage = "The last explicit Capture was already saved before interruption. Its observation is retained; no duplicate capture or motion was repeated."; }
        else if (calibrationIdentity is not null && (int)Wizard.Step < (int)CalibrationWizardStep.PointIntro)
            calibrationWizard = Wizard with { Step = CalibrationWizardStep.Review, Epoch = Wizard.Epoch + 1 };
        if (calibrationIdentity is null && Wizard.Setup.SurfaceConfirmed)
        {
            try { calibrationDesk = calibrationDesk with { SurfaceY = CalibrationWizardFlow.Number(Wizard, "sceney", 0, 3000) / 1000 }; }
            catch (InvalidOperationException) { UpdateSetup(setup => setup with { SurfaceConfirmed = false }); }
        }
        ReconcileReevaluatedWizard();
    }

    private (double LeftMm, double RightMm, double BackMm)? WizardCross()
    {
        var reference = ActiveProfile.Alignment;
        if (reference is null || !reference.Name.Contains("cross", StringComparison.OrdinalIgnoreCase) ||
            !Wizard.Answers.TryGetValue("width", out var _) || !Wizard.Answers.TryGetValue("depth", out var _)) return null;
        double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        double left = width / 2 + reference.X * 1000, back = depth / 2 + reference.Z * 1000;
        return left >= 0 && left <= width && back >= 0 && back <= depth ? (left, width - left, back) : null;
    }

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
        var file = CalibrationWizardFile.From(Wizard);
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(file, PluginProtocol.Json), cancellation);
        File.Move(temporary, WizardDraftPath, true);
        sheetSaveStatus = "Saved locally · measurements restore next session.";
        if (calibrationIdentity is null && BenchReference.FromAnswers(Wizard.Answers, calibrationDesk.SurfaceY) is { } shared && preferences.BenchReference != shared)
        { preferences = preferences with { BenchReference = shared }; await Save(cancellation); }
        if (calibrationIdentity is not null && calibrationPath.Length > 0)
        {
            var evidence = new { report = Path.GetFileName(calibrationPath), Wizard.ProfileId, file.Answers,
                physicalPoint = file.Answers.GetValueOrDefault("physicalpoint"), operatorSaysTcpMatches = file.OperatorSaysTcpMatches,
                modelCorrespondenceIndependentlyVerified = false, Wizard.ModelAssetId, Wizard.ModelRevision,
                sceneSurfaceYMetres = calibrationDesk.SurfaceY,
                cameraEvidence = "JPEG preview only; no metric depth was queried", timestampUtc = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(calibrationPath.Replace("-calibration.json", "-wizard-evidence.json"), JsonSerializer.Serialize(evidence, PluginProtocol.Json), cancellation);
        }
    }

    private async Task WizardEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        EnsureIndependentCalibration();
        try
        {
            LoadWizard();
            string[] parts = action.Split('.', 2);
            if (!calibrationOpen || parts.Length != 2 || !long.TryParse(parts[0], out long epoch) ||
                !CalibrationWizardFlow.Accepts(Wizard, ActiveProfile.Id, epoch))
                return;
            if (parts[1].StartsWith("sheet.", StringComparison.Ordinal))
                await CalibrationSheetEvent(parts[1][6..], value, cancellation);
            else if (parts[1] == "camera")
            {
                calibrationWizard = Wizard with { HelpCamera = new[] { "bench", "gripper", "depth" }[Math.Clamp(value.GetInt32(), 0, 2)] };
                await SaveWizard(cancellation);
                await Render(cancellation);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            wizardMessage = error.Message;
            // Sheet validation errors must not destroy the active input view.
            if (!action.Contains(".sheet.", StringComparison.Ordinal)) await Render(cancellation);
            await Update(cancellation);
            throw;
        }
    }

    private async Task ShowWizardModelPoint(CancellationToken cancellation)
    {
        string name = Wizard.Answers.GetValueOrDefault("tool", "").Trim();
        const string unavailable = "The current model does not expose one exact named metric site. Select/fix the correct model point before continuing.";
        var (_, pose) = await ReadMetricPose("Link this robot before inspecting a model point.", unavailable, cancellation);
        wizardDiscoveredSites = pose["toolSites"]?.AsArray().Select(s => s?["name"]?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [];
        var site = CalibrationPose.ReadToolSite(pose, name, unavailable, unavailable);
        if (pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } asset || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } revision)
            throw new InvalidOperationException(unavailable);
        bool samePoint = Wizard.ModelAssetId == asset && Wizard.ModelRevision == revision &&
            (ToolMarker is not { } shown || shown.SiteName == name);
        calibrationWizard = Wizard with { ModelAssetId = asset, ModelRevision = revision, Setup = Wizard.Setup with { JawTipsConfirmed = samePoint && Wizard.Setup.JawTipsConfirmed } };
        _ = CalibrationPlacement.Read(pose["rootPlacement"]);
        wizardModelPoint = "Look for the marker between the simulated jaw tips, then compare it with the real jaw-tip midpoint.";
        wizardModelPointDetails = $"Marker coordinates ({site.X * 1000:F1}, {site.Y * 1000:F1}, {site.Z * 1000:F1}) mm. Site '{name}' world coordinates {site.Json().ToJsonString()} m; compiled model {asset}, revision {revision}. The marker identifies a model point only. Continue records the operator's comparison, not independent physical calibration.";
        await ShowWizardHighlight(cancellation);
    }

    private async Task<CalibrationPlacement> ReadWizardMetricRoot(CancellationToken cancellation)
    {
        const string changed = "The model/reference changed. Read and compare the named model point again before confirming a scene surface.";
        var (_, pose) = await ReadMetricPose("Link this robot before using its scene root as a reference.", changed, cancellation);
        if (pose["modelAssetId"]?.GetValue<string>() != Wizard.ModelAssetId || pose["modelRevision"]?.GetValue<string>() != Wizard.ModelRevision)
            throw new InvalidOperationException(changed);
        return CalibrationPlacement.Read(pose["rootPlacement"]);
    }
    private async Task CaptureWizardPoint(CancellationToken cancellation)
    {
        var setup = Wizard.Setup;
        if (!setup.JawTipsConfirmed || setup.BackProof.Length == 0 || !setup.SurfaceConfirmed)
            throw new InvalidOperationException("Identify the fixed physical/model point and scene desk frame before capturing.");
        var tabletop = CalibrationWizardFlow.TabletopInputs(Wizard);
        if (setup.EdgeRoot.Length == 0)
            throw new InvalidOperationException("Confirm the LEFT and BACK pointers before capturing. Start new questions if this saved draft used the older desk setup.");
        var edgeRoot = CalibrationPlacement.Read(JsonNode.Parse(setup.EdgeRoot));
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
        if (Wizard.Setup.Stage(deskProof) is SheetSetupStage.ConfirmLeft or SheetSetupStage.ConfirmBack)
            throw new InvalidOperationException("The LEFT/BACK pointers have not been confirmed for this robot and these desk measurements. Start new questions and compare both desk edges before capturing more points; the saved report is retained.");
        if (Math.Abs(CalibrationWizardFlow.SceneSurface(Wizard) - calibrationDesk.SurfaceY * 1000) > .001)
            throw new InvalidOperationException("The saved scene reference changed. Start new questions before mixing measurements from different scene surfaces.");
        if (calibrationIdentity is null) calibrationDesk = tabletop.Desk;
        else if (calibrationDesk != tabletop.Desk) throw new InvalidOperationException("The captured session's fixed tabletop settings changed. Start new questions before mixing references.");
        calibrationMeasurement = tabletop.Measurement;
        if (!WizardValidation && WizardCross() is { } cross && calibrationMeasurement.World(calibrationDesk).PlanarDistance(calibrationDesk.World(cross.LeftMm, cross.BackMm, 0)) < .05)
            throw new InvalidOperationException("Keep the saved profile cross and its nearby5cm for independent validation. Choose a different training location.");
        if (calibrationIdentity is null) { await CalibrationEvent("start", cancellation); await SaveWizard(cancellation); }
        if (Math.Abs(calibrationOriginal!.Position.Y - calibrationDesk.SurfaceY) > .001)
            throw new InvalidOperationException("The current scene root is not on the confirmed scene tabletop Y. Edit/verify scene desk/root setup, then start over; no point or scene transform was applied.");
        if (calibrationIdentity!.ModelAssetId != Wizard.ModelAssetId || calibrationIdentity.ModelRevision != Wizard.ModelRevision)
            throw new InvalidOperationException("The compiled model changed since the point comparison. Start over and identify the point on the current model.");
        await CalibrationEvent(WizardValidation ? "validate" : "capture", cancellation);
        calibrationWizard = CalibrationWizardFlow.NextPoint(Wizard, WizardTrainingCount, WizardValidationCount);
    }
}
