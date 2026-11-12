using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private string SheetId(string action) => WizardId("sheet." + action);
    private bool SheetContactCompatible => calibrationSamples.All(s => s.Measurement.Mode == "edge measurements" && s.Measurement.HeightMm == 0);
    private CalibrationSample? SheetSample(int row) => row == 4
        ? calibrationSamples.FirstOrDefault(s => s.HeldOut)
        : calibrationSamples.Where(s => !s.HeldOut).ElementAtOrDefault(row);

    internal void LoadSheetRows()
    {
        // The saved report is authoritative after a correction commits. A crash
        // before the draft write must not restore old ruler values in markers.
        var answers = new List<(string Key, string Value)>();
        for (int row = 0; row < 5; row++)
            if (row != sheetEditingRow && SheetSample(row) is { } captured)
            {
                answers.Add(($"p{row}left", CalibrationNumber(captured.Measurement.LeftMm)));
                answers.Add(($"p{row}back", CalibrationNumber(captured.Measurement.BackMm)));
            }
        calibration.Wizard.Answer([.. answers]);
        var draft = Wizard;
        if (draft.Answers.ContainsKey("sheetready")) return;
        answers.Clear();
        // Preserve the current unrecorded XY entry, but never migrate a hover
        // height into this explicitly tabletop-contact workflow.
        int current = Math.Min(WizardTrainingCount, 4);
        foreach (string axis in new[] { "left", "back" })
            if (draft.Answers.TryGetValue(axis, out string? text)) answers.Add(($"p{current}{axis}", text));
        answers.Add(("sheetready", "tabletop-contact"));
        if (!draft.Answers.ContainsKey("width")) answers.Add(("width", CalibrationNumber(calibrationDesk.Width * 1000)));
        if (!draft.Answers.ContainsKey("depth")) answers.Add(("depth", CalibrationNumber(calibrationDesk.Depth * 1000)));
        if (!draft.Answers.ContainsKey("precision")) answers.Add(("precision", "5"));
        calibration.Wizard.Answer([.. answers]);
    }

    private Widget SheetInput(string key, string hint, bool enabled = true, string? text = null) =>
        new("input", SheetId("field." + key)) { Text = text ?? Wizard.Answers.GetValueOrDefault(key, ""), Width = 80, Hint = hint, Enabled = enabled, CommitOnly = false };
    private Widget SheetButton(string action, string text, string? hint = null) =>
        Widget.Button(SheetId(action), text) with { Hint = hint, Padding = 7 };

    private Widget CalibrationSheetControls()
    {
        if (CalibrationRootIsShared())
            return Widget.Column("calibration.branch.mount", Widget.Label("calibration.branch.title", ActiveProfile.Name + " · bench placement") with { Appearance = "heading" },
                Widget.Label("calibration.branch.explanation", "This arm shares a simulation model. Its mounting position is saved in the scene. Desk alignment editing is unavailable here because it would move every arm in the model. Joint linking and camera previews are available.") with { Height = 92 },
                Widget.Button("workspace.controls@branch", "Back to robot controls"));
        LoadWizard(); LoadSheetRows();
        var body = new List<Widget>
        {
            Widget.Row("sheet.header", Widget.Label("sheet.title", "Calibrate " + ActiveProfile.Name) with { Appearance = "heading" },
                Widget.Button("workspace.controls@sheet", "Robot controls")),
            HeightCheckControls(),
            Widget.Label("sheet.instructions", "Enter the distances for your four physical crosses. Select a point, hand-guide the jaw-tip midpoint onto that cross, then capture it. Selecting a point only moves the viewport.") with { Height = 58 },
            Widget.Label("sheet.next", "Checking calibration setup...") with { Height = 58, Appearance = "heading" },
            Widget.Label("sheet.save", sheetSaveStatus) with { Height = 22, Appearance = "muted" },
            Widget.Row("sheet.desk", Widget.Label("sheet.desk.label", "Desk width / depth (mm)") with { Width = 205 },
                SheetInput("width", "Full desk width in mm", calibrationIdentity is null), SheetInput("depth", "Full desk depth in mm", calibrationIdentity is null)),
            Widget.Row("sheet.accuracy", Widget.Label("sheet.accuracy.label", "Measurement accuracy ± mm") with { Width = 205 },
                SheetInput("precision", "How accurately can you measure and touch a mark? Enter 1–10 mm; 5 mm is a reasonable starting estimate.", calibrationIdentity is null),
                SheetButton("precision.default", "Use ±5 mm", "Sets your stated measurement uncertainty to 5 mm. It does not change a measured coordinate.")),
            Widget.Row("sheet.references", SheetButton("left.show", "Show LEFT"), SheetButton("left.confirm", "Confirm LEFT"),
                SheetButton("back.show", "Show BACK"), SheetButton("back.confirm", "Confirm BACK")),
            Widget.Row("sheet.tool", SheetButton("tool.show", "Show jaw tips"), SheetButton("tool.confirm", "Confirm jaw tips", wizardModelPoint + " " + wizardModelPointDetails),
                SheetButton("surface", "Base is on desk")),
            Widget.Label("sheet.setup", "") with { Height = 32 },
            Widget.Label("sheet.action.feedback", wizardMessage) with { Height = 48 },
            Widget.Row("sheet.columns", Widget.Label("sheet.point.column", "Mark") with { Width = 65 },
                Widget.Label("sheet.back.column", "BACK mm") with { Width = 90 }, Widget.Label("sheet.left.column", "LEFT mm") with { Width = 90 },
                SheetButton("marks.show", "Show all crosses") with { Width = 160 })
        };
        for (int row = 0; row < 5; row++)
        {
            var sample = SheetSample(row);
            string title = row == 4 ? "Check" : "P" + (row + 1);
            body.Add(Widget.Row("sheet.row." + row,
                Widget.Label("sheet.name." + row, title) with { Width = 65, Height = 32 },
                SheetInput($"p{row}back", "Distance from the confirmed BACK edge in mm. cm × 10.", sample is null || sheetEditingRow == row, sample is null || sheetEditingRow == row ? null : CalibrationNumber(sample.Measurement.BackMm)),
                SheetInput($"p{row}left", "Distance from the confirmed LEFT edge in mm. cm × 10.", sample is null || sheetEditingRow == row, sample is null || sheetEditingRow == row ? null : CalibrationNumber(sample.Measurement.LeftMm)),
                SheetButton("move." + row, "Select " + title) with { Width = 120, Enabled = false },
                SheetButton("capture." + row, "Capture " + title) with { Width = 115, Enabled = false }));
            body.Add(Widget.Label("sheet.row.status." + row, "Checking this point...") with { Height = 42 });
        }
        body.Add(Widget.Label("sheet.check.help", "Check: measure a fifth cross that you did not use above (your centre cross is suitable). All five contacts touch the tabletop: height = 0 mm.") with { Height = 44 });
        body.Add(Widget.Row("sheet.fit.actions", SheetButton("fit", "Calculate alignment") with { Enabled = false },
            SheetButton("apply", "Apply to simulation") with { Enabled = false }, SheetButton("rollback", "Undo alignment") with { Enabled = false }));
        body.Add(Widget.Label("sheet.results", WizardReviewSummary()) with { Height = 90 });
        body.Add(SheetButton("reevaluate", "Re-evaluate saved poses", "After correcting the same model or reopening the scene, recompute its named point from the saved physical angles. Requires the same robot entity, physical connection and original placement. Keeps the original report; never moves or recaptures the arm.") with { Enabled = false, Visible = false });
        body.Add(Widget.Row("sheet.report.actions", SheetButton("open", "Saved report"), SheetButton("inspect", "Check saved placement"), SheetButton("new", "New calibration")));
        if (calibrationArchiveConfirmation)
            body.Add(Widget.Row("sheet.archive", SheetButton("archive", "Keep old report & start new"), SheetButton("archive.cancel", "Cancel")));
        var camera = Widget.Column("sheet.camera", Widget.Label("sheet.camera.title", "Camera view"),
            new Widget("dropdown", WizardId("camera")) { Options = ["Shared bench", "Pika gripper", "D405 camera"], Value = Array.IndexOf(new[] { "bench", "gripper", "depth" }, Wizard.HelpCamera) },
            new Widget("image", "wizard.camera.image") { ImageId = CameraImageId(Wizard.HelpCamera), Height = 160, Visible = CameraFrameReady(Wizard.HelpCamera) },
            Widget.Label("sheet.camera.help", "Solid-green hand guiding: gently touch the selected cross with the midpoint between both jaw tips. Hold still, then Capture. The plugin records measurements; it never moves the real arm to a calibration mark.") with { Height = 160 }) with { Width = 235, Padding = 10, Spacing = 10 };
        return Widget.Row("sheet.page", Widget.Column("sheet.body", body.ToArray()) with { Padding = 12, Spacing = 7 }, camera);
    }

    private async Task CalibrationSheetEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        LoadSheetRows();
        if (action.StartsWith("field.", StringComparison.Ordinal))
        {
            await EditSheetField(action[6..], value, cancellation);
            return;
        }
        if (action == "precision.default")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("Accuracy is fixed after the first capture.");
            calibration.Wizard.Answer("precision", "5");
        }
        else if (action == "tool.show") await ShowSheetJawTips(cancellation);
        else if (action == "tool.confirm")
        {
            if (ToolMarker is not { } marker || !WizardHighlightCurrent(marker)) throw new InvalidOperationException("Show the jaw-tip marker first, then compare it with the real midpoint.");
            calibration.Wizard.ChangeSetup(setup => setup with { JawTipsConfirmed = true });
            calibration.Wizard.Report("Jaw tips confirmed. Next, confirm that the robot base rests on the tabletop.");
        }
        else if (action == "surface") await ConfirmSheetSurface(cancellation);
        else if (action is "left.show" or "back.show" or "left.confirm" or "back.confirm")
        {
            await ReadSheetReference(cancellation);
            var step = action.StartsWith("left", StringComparison.Ordinal) ? CalibrationWizardStep.DeskFrame : CalibrationWizardStep.DeskBack;
            calibration.Wizard.Edit(draft => draft.Step == step ? draft : draft with { Step = step });
            await ShowWizardReference(cancellation, frame: action.EndsWith("show", StringComparison.Ordinal));
            if (action.EndsWith("confirm", StringComparison.Ordinal)) ConfirmWizardReference();
        }
        else if (action == "marks.show") await ShowSheetMarks(cancellation);
        else if (SheetRowAction(action) is var (verb, row))
        {
            switch (verb, captured: SheetSample(row) is not null)
            {
                case ("move", captured: true): await EditSheetMeasurement(row, cancellation); await Update(cancellation); return;
                case ("capture", captured: true): await SaveSheetMeasurementCorrection(row, cancellation); await Update(cancellation); return;
                case ("move", captured: false): await SelectSheetRow(row, cancellation); break;
                default: await CaptureSheetRow(row, cancellation); break;
            }
        }
        else if (action == "reevaluate") await ReevaluateCalibrationModel(cancellation);
        else if (action is "fit" or "apply" or "rollback" or "inspect" or "open" or "new" or "archive" or "archive.cancel")
        {
            if (action is "fit" or "apply" && !SheetContactCompatible) throw new InvalidOperationException("Start a new tabletop calibration before fitting or applying these mixed reference types. The old report is retained.");
            if (action is "fit" or "apply" && (SavedModelChanged || calibrationReevaluation is not null && SheetSetupBlocker().Length > 0))
                throw new InvalidOperationException("Re-evaluate the corrected model and reconfirm its LEFT, BACK, jaw tips and desk references before calculating or applying alignment.");
            await CalibrationEvent(action, cancellation);
            if (action == "fit") calibration.Wizard.Report(calibrationFit?.CanApply == true ? "Alignment calculated. Review the result before Apply to simulation." : calibrationFit?.Reason ?? "Alignment calculation did not return a result.");
            if (action is "new" or "archive" && calibrationIdentity is null) calibration.Wizard.Edit(draft => CalibrationWizardFlow.New(ActiveProfile.Id, draft.Epoch + 1));
        }
        else return;
        await SaveWizard(cancellation);
        if (action.StartsWith("capture.", StringComparison.Ordinal) || action is "new" or "archive" or "archive.cancel") await Render(cancellation);
        await Update(cancellation);
    }

    // "move.N" or "capture.N" for sheet row N; null for any other action.
    private static (string Verb, int Row)? SheetRowAction(string action)
    {
        string[] parts = action.Split('.');
        if (parts.Length < 2 || parts[0] is not ("move" or "capture")) return null;
        if (!int.TryParse(parts[1], out int row) || row is < 0 or > 4) throw new InvalidOperationException("Unknown point.");
        return (parts[0], row);
    }

    private async Task EditSheetField(string key, JsonElement value, CancellationToken cancellation)
    {
        bool desk = key is "width" or "depth" or "precision";
        bool point = Enumerable.Range(0, 5).Any(i => key == $"p{i}left" || key == $"p{i}back");
        if (!desk && !point) throw new InvalidOperationException("Unknown measurement field.");
        if (desk && calibrationIdentity is not null || point && SheetSample(key[1] - '0') is not null && sheetEditingRow != key[1] - '0')
            throw new InvalidOperationException("Recorded measurements are fixed. Start a new calibration to change them; the old report is kept.");
        string text = value.GetString() ?? "";
        if (text.Length > 32) throw new InvalidOperationException("Enter a measurement in millimetres.");
        calibration.Wizard.Answer(key, text);
        if (desk && key != "precision") { calibration.Wizard.ChangeSetup(setup => setup.ClearEdges()); calibration.Markers.UnacknowledgeEdge(); }
        calibration.Markers.InvalidateSheet();
        await SaveWizard(cancellation);
    }

    private async Task ShowSheetJawTips(CancellationToken cancellation)
    {
        // Leave the legacy edge step before showing a tool marker. Otherwise
        // background edge renewal immediately replaces it and clears its proof.
        calibration.Wizard.Edit(draft => draft.Step == CalibrationWizardStep.ModelPoint ? draft : draft with { Step = CalibrationWizardStep.ModelPoint });
        if (calibrationIdentity is not null) { await ShowWizardHighlight(cancellation); return; }
        if (!SheetContactCompatible) throw new InvalidOperationException("Start a new tabletop calibration; the existing report contains raised or grid points and is retained.");
        await ShowWizardModelPoint(cancellation);
        if (wizardDiscoveredSites.Count(s => s == "calibration_jaw_tips") == 1 && Wizard.Answers.GetValueOrDefault("tool") != "calibration_jaw_tips")
        { calibration.Wizard.Answer("tool", "calibration_jaw_tips"); await ShowWizardModelPoint(cancellation); }
    }

    private async Task ConfirmSheetSurface(CancellationToken cancellation)
    {
        if (calibrationIdentity is not null && calibrationReevaluation is null) throw new InvalidOperationException("The tabletop reference is fixed for recorded points.");
        var root = await ReadWizardMetricRoot(cancellation);
        if (calibrationIdentity is not null && (calibrationOriginal is null || !calibrationOriginal.Same(root) || Math.Abs(root.Position.Y - calibrationDesk.SurfaceY) > 1e-6))
            throw new InvalidOperationException("Reconfirming a corrected model cannot change the saved original placement or tabletop height.");
        calibration.Wizard.Answer("sceney", CalibrationNumber(root.Position.Y * 1000));
        calibration.Desk.UseDesk(calibrationDesk with { SurfaceY = root.Position.Y });
        bool rootMoved = Wizard.Setup.EdgeRoot.Length > 0 && !CalibrationPlacement.Read(JsonNode.Parse(Wizard.Setup.EdgeRoot)).Same(root);
        calibration.Wizard.ChangeSetup(setup => (rootMoved ? setup.ClearEdges() : setup) with { SurfaceConfirmed = true });
    }

    private async Task SelectSheetRow(int row, CancellationToken cancellation)
    {
        await ShowSheetMarks(cancellation, frameRow: row);
        calibration.Wizard.Report($"{(row == 4 ? "CHECK" : "P" + (row + 1))} is highlighted in the viewport. In solid-green hand guiding, gently touch this physical cross with the jaw-tip midpoint, hold still, then Capture its row. The preview does not move the real arm.");
    }

    private async Task CaptureSheetRow(int row, CancellationToken cancellation)
    {
        string blocked = SheetCaptureBlocker(row, Device());
        if (blocked.Length > 0) throw new InvalidOperationException(blocked);
        if (row < 4 && row != WizardTrainingCount) throw new InvalidOperationException("Capture reference rows in order: Point " + (WizardTrainingCount + 1) + " is next. You can enter all measurements now.");
        if (row == 4 && WizardTrainingCount < 4) throw new InvalidOperationException("Capture the four reference points before the final check.");
        var entered = Wizard.Answers;
        calibration.Wizard.Answer(("left", entered.GetValueOrDefault($"p{row}left", "")), ("back", entered.GetValueOrDefault($"p{row}back", "")),
            ("height", "0"), ("method", "edge measurements"), ("role", row == 4 ? "validation" : "training"));
        calibration.Wizard.Edit(draft => draft with { Step = CalibrationWizardStep.Capture });
        await CaptureWizardPoint(cancellation);
        calibration.Wizard.Answer("sheetselected", Math.Min(WizardTrainingCount, 4).ToString());
        calibration.Wizard.Report($"Saved {(row == 4 ? "the final check" : "Point " + (row + 1))}. " + (row < 4 ? "Move by hand to the next mark and capture its row." : "Click Calculate alignment to check the result."));
    }

    private void AddCalibrationSheetPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        // Compute the setup stage and its blocker once: each costs a desk
        // fingerprint (JSON plus SHA-256), and every row below needs it.
        var stage = SheetStage();
        string setupBlocker = SheetSetupBlocker(stage);
        bool left = stage > SheetSetupStage.ConfirmLeft, back = stage > SheetSetupStage.ConfirmBack;
        bool tool = stage > SheetSetupStage.ConfirmJawTips, surface = stage > SheetSetupStage.ConfirmSurface;
        void Step(string action, string text, bool enabled) => patches[SheetId(action)] = new() { ["text"] = text, ["enabled"] = enabled };
        Step("left.confirm", left ? "LEFT confirmed" : "Confirm LEFT", !left);
        Step("back.show", "Show BACK", left);
        Step("back.confirm", back ? "BACK confirmed" : "Confirm BACK", left && !back);
        Step("tool.show", "Show jaw tips", back);
        Step("tool.confirm", tool ? "Jaw tips confirmed" : "Confirm jaw tips", back && !tool && ToolMarker is { } marker && WizardHighlightCurrent(marker));
        Step("surface", surface ? "Desk confirmed" : "Base is on desk", tool && !surface);
        Step("precision.default", "Use ±5 mm", calibrationIdentity is null);
        patches[SheetId("field.precision")] = new() { ["text"] = Wizard.Answers.GetValueOrDefault("precision", ""), ["enabled"] = calibrationIdentity is null };
        patches["sheet.title"] = new() { ["text"] = $"Calibrate {ActiveProfile.Name} · {WizardTrainingCount}/4 captured + {WizardValidationCount}/1 check" };
        patches["sheet.save"] = new() { ["text"] = sheetSaveStatus };
        patches["sheet.action.feedback"] = new() { ["text"] = wizardMessage };
        patches["sheet.setup"] = new() { ["text"] = $"LEFT {(left ? "confirmed" : "pending")} · BACK {(back ? "confirmed" : "pending")} · Jaw tips {(tool ? "confirmed" : "pending")} · Desk {(surface ? "confirmed" : "pending")}" };
        int selected = SheetSelectedRow;
        string next = setupBlocker.Length > 0 ? setupBlocker : SheetCaptureBlocker(selected, device, setupBlocker);
        patches["sheet.next"] = new() { ["text"] = SheetAppliedAlignmentMatchesModel ? "Saved alignment loaded. Use Check jaw height above for new measurements. The original four-point captures are kept; editing that alignment requires its saved-session checks."
            : SavedModelChanged ? "The simulation model or scene session changed. Re-evaluate saved poses below before calculating or applying alignment."
            : WizardValidationCount > 0 ? setupBlocker.Length > 0 ? setupBlocker : "All five points captured. Calculate alignment to check the result."
            : $"{SheetPointName(selected)}: " + (next.Length > 0 ? next : "Touch this physical cross with the jaw-tip midpoint. Hold still and click Capture.") };
        for (int row = 0; row < 5; row++)
        {
            bool captured = SheetSample(row) is not null;
            string reason = SheetCaptureBlocker(row, device, setupBlocker);
            string measurementError = SheetMeasurementBlocker(row);
            bool editing = sheetEditingRow == row;
            string editStatus = measurementError.Length > 0 ? measurementError : "Correct the entered distances, then Save entry. The captured arm pose stays unchanged.";
            string status = editing ? editStatus : captured ? "Captured. This measured pose is saved."
                : measurementError.Length > 0 ? measurementError
                : row != selected ? "Select this point to locate its cross in the viewport."
                : reason.Length > 0 ? reason : "Ready: gently touch this cross, hold still, then Capture.";
            patches["sheet.row.status." + row] = new() { ["text"] = status };
            patches[SheetId("capture." + row)] = new() { ["text"] = editing ? "Save entry" : captured ? "Captured" : "Capture " + SheetPointName(row), ["enabled"] = editing ? measurementError.Length == 0 && SheetCanCorrect : !captured && row == selected && reason.Length == 0, ["hint"] = editing ? editStatus : reason };
            if (captured)
                foreach (string axis in new[] { "left", "back" })
                    patches[SheetId($"field.p{row}{axis}")] = new() { ["enabled"] = editing, ["text"] = editing ? Wizard.Answers.GetValueOrDefault($"p{row}{axis}", "") : CalibrationNumber(axis == "left" ? SheetSample(row)!.Measurement.LeftMm : SheetSample(row)!.Measurement.BackMm) };
            Step("move." + row, editing ? "Cancel edit" : captured ? "Edit entry" : row == selected ? "Selected " + SheetPointName(row) : "Select " + SheetPointName(row), captured ? SheetCanCorrect : SheetPoint(row) is not null && link is not null);
        }
        patches[SheetId("reevaluate")] = new() { ["visible"] = CanCheckUpdatedModel, ["enabled"] = CanCheckUpdatedModel && sheetEditingRow < 0 && CanReevaluateWithLiveScene };
        patches[SheetId("fit")] = new() { ["enabled"] = !SavedModelChanged && setupBlocker.Length == 0 && sheetEditingRow < 0 && SheetContactCompatible && WizardTrainingCount >= 4 && WizardValidationCount >= 1 && !calibrationPlacementUnverified };
        patches[SheetId("apply")] = new() { ["enabled"] = sheetEditingRow < 0 && setupBlocker.Length == 0 && calibrationFit?.CanApply == true && calibrationApplied is null && !calibrationPlacementUnverified };
        patches[SheetId("rollback")] = new() { ["enabled"] = calibrationApplied is not null && !calibrationPlacementUnverified };
        patches["sheet.results"] = new() { ["text"] = WizardReviewSummary() };
    }
}
