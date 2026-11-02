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

    private void LoadSheetRows()
    {
        if (Wizard.Answers.ContainsKey("sheetready")) return;
        // Preserve the current unrecorded XY entry, but never migrate a hover
        // height into this explicitly tabletop-contact workflow.
        int current = Math.Min(WizardTrainingCount, 4);
        foreach (string axis in new[] { "left", "back" })
            if (Wizard.Answers.TryGetValue(axis, out string? text)) Wizard.Answers[$"p{current}{axis}"] = text;
        Wizard.Answers["sheetready"] = "tabletop-contact";
        if (!Wizard.Answers.ContainsKey("width")) Wizard.Answers["width"] = CalibrationNumber(calibrationDesk.Width * 1000);
        if (!Wizard.Answers.ContainsKey("depth")) Wizard.Answers["depth"] = CalibrationNumber(calibrationDesk.Depth * 1000);
        if (!Wizard.Answers.ContainsKey("precision")) Wizard.Answers["precision"] = "5";
    }

    private Widget SheetInput(string key, string hint, bool enabled = true, string? text = null) =>
        new("input", SheetId("field." + key)) { Text = text ?? Wizard.Answers.GetValueOrDefault(key, ""), Width = 80, Hint = hint, Enabled = enabled, CommitOnly = false };
    private Widget SheetButton(string action, string text, string? hint = null) =>
        Widget.Button(SheetId(action), text) with { Hint = hint, Padding = 7 };

    private Widget CalibrationSheetControls()
    {
        LoadWizard(); LoadSheetRows();
        var body = new List<Widget>
        {
            Widget.Row("sheet.header", Widget.Label("sheet.title", "Calibrate " + ActiveProfile.Name) with { Appearance = "heading" },
                Widget.Button("workspace.controls@sheet", "Robot controls")),
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
                SheetInput($"p{row}back", "Distance from the confirmed BACK edge in mm. cm × 10.", sample is null, sample is null ? null : CalibrationNumber(sample.Measurement.BackMm)),
                SheetInput($"p{row}left", "Distance from the confirmed LEFT edge in mm. cm × 10.", sample is null, sample is null ? null : CalibrationNumber(sample.Measurement.LeftMm)),
                SheetButton("move." + row, "Select " + title) with { Width = 120, Enabled = false },
                SheetButton("capture." + row, "Capture " + title) with { Width = 115, Enabled = false }));
            body.Add(Widget.Label("sheet.row.status." + row, "Checking this point...") with { Height = 42 });
        }
        body.Add(Widget.Label("sheet.check.help", "Check: measure a fifth cross that you did not use above (your centre cross is suitable). All five contacts touch the tabletop: height = 0 mm.") with { Height = 44 });
        body.Add(Widget.Row("sheet.fit.actions", SheetButton("fit", "Calculate alignment") with { Enabled = false },
            SheetButton("apply", "Apply to simulation") with { Enabled = false }, SheetButton("rollback", "Undo alignment") with { Enabled = false }));
        body.Add(Widget.Label("sheet.results", WizardReviewSummary()) with { Height = 90 });
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
            string key = action[6..];
            bool desk = key is "width" or "depth" or "precision";
            bool point = Enumerable.Range(0, 5).Any(i => key == $"p{i}left" || key == $"p{i}back");
            if (!desk && !point) throw new InvalidOperationException("Unknown measurement field.");
            if (desk && calibrationIdentity is not null || point && SheetSample(key[1] - '0') is not null)
                throw new InvalidOperationException("Recorded measurements are fixed. Start a new calibration to change them; the old report is kept.");
            string text = value.GetString() ?? "";
            if (text.Length > 32) throw new InvalidOperationException("Enter a measurement in millimetres.");
            Wizard.Answers[key] = text;
            if (desk && key != "precision") { Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("edgeroot"); wizardReferenceAcknowledgedAt = 0; }
            sheetMarkersDirty = true;
            await SaveWizard(cancellation); return;
        }
        if (action == "precision.default")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("Accuracy is fixed after the first capture.");
            Wizard.Answers["precision"] = "5";
        }
        else if (action == "tool.show")
        {
            // Leave the legacy edge step before showing a tool marker. Otherwise
            // background edge renewal immediately replaces it and clears its proof.
            if (Wizard.Step != CalibrationWizardStep.ModelPoint)
                calibrationWizard = Wizard with { Step = CalibrationWizardStep.ModelPoint };
            if (calibrationIdentity is not null) await ShowWizardHighlight(cancellation);
            else
            {
                if (!SheetContactCompatible) throw new InvalidOperationException("Start a new tabletop calibration; the existing report contains raised or grid points and is retained.");
                await ShowWizardModelPoint(cancellation);
                if (wizardDiscoveredSites.Count(s => s == "calibration_jaw_tips") == 1 && Wizard.Answers.GetValueOrDefault("tool") != "calibration_jaw_tips")
                { Wizard.Answers["tool"] = "calibration_jaw_tips"; await ShowWizardModelPoint(cancellation); }
            }
        }
        else if (action == "tool.confirm")
        {
            if (wizardHighlightScope is not { } marker || !WizardHighlightCurrent(marker)) throw new InvalidOperationException("Show the jaw-tip marker first, then compare it with the real midpoint.");
            calibrationWizard = Wizard with { OperatorSaysTcpMatches = true };
            Wizard.Answers["physicalpoint"] = "operator confirmed midpoint between both jaw tips";
            wizardMessage = "Jaw tips confirmed. Next, confirm that the robot base rests on the tabletop.";
        }
        else if (action == "surface")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("The tabletop reference is fixed for recorded points.");
            var root = await ReadWizardMetricRoot(cancellation);
            Wizard.Answers["observedrooty"] = Wizard.Answers["sceney"] = CalibrationNumber(root.Position.Y * 1000);
            Wizard.Answers["sceneconfirmed"] = "operator confirmed simulated base rests on tabletop";
            calibrationDesk = calibrationDesk with { SurfaceY = root.Position.Y };
            if (Wizard.Answers.TryGetValue("edgeroot", out string? pinned) && !CalibrationPlacement.Read(JsonNode.Parse(pinned)).Same(root))
            { Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("edgeroot"); }
        }
        else if (action is "left.show" or "back.show" or "left.confirm" or "back.confirm")
        {
            await ReadSheetReference(cancellation);
            var step = action.StartsWith("left", StringComparison.Ordinal) ? CalibrationWizardStep.DeskFrame : CalibrationWizardStep.DeskBack;
            if (Wizard.Step != step) calibrationWizard = Wizard with { Step = step };
            await ShowWizardReference(cancellation, frame: action.EndsWith("show", StringComparison.Ordinal));
            if (action.EndsWith("confirm", StringComparison.Ordinal)) ConfirmWizardReference();
        }
        else if (action == "marks.show") await PublishSheetMarks(cancellation, frameAll: true);
        else if (action.StartsWith("move.", StringComparison.Ordinal) || action.StartsWith("capture.", StringComparison.Ordinal))
        {
            if (!int.TryParse(action.Split('.')[1], out int row) || row is < 0 or > 4) throw new InvalidOperationException("Unknown point.");
            if (SheetSample(row) is not null) throw new InvalidOperationException("This point is already recorded.");
            if (action.StartsWith("move.", StringComparison.Ordinal))
            {
                Wizard.Answers["sheetselected"] = row.ToString();
                await PublishSheetMarks(cancellation, frameRow: row);
                wizardMessage = $"{(row == 4 ? "CHECK" : "P" + (row + 1))} is highlighted in the viewport. In solid-green hand guiding, gently touch this physical cross with the jaw-tip midpoint, hold still, then Capture its row. The preview does not move the real arm.";
            }
            else
            {
                string blocked = SheetCaptureBlocker(row, Device());
                if (blocked.Length > 0) throw new InvalidOperationException(blocked);
                if (row < 4 && row != WizardTrainingCount) throw new InvalidOperationException("Capture reference rows in order: Point " + (WizardTrainingCount + 1) + " is next. You can enter all measurements now.");
                if (row == 4 && WizardTrainingCount < 4) throw new InvalidOperationException("Capture the four reference points before the final check.");
                Wizard.Answers["left"] = Wizard.Answers.GetValueOrDefault($"p{row}left", "");
                Wizard.Answers["back"] = Wizard.Answers.GetValueOrDefault($"p{row}back", "");
                Wizard.Answers["height"] = "0"; Wizard.Answers["method"] = "edge measurements"; Wizard.Answers["role"] = row == 4 ? "validation" : "training";
                calibrationWizard = Wizard with { Step = CalibrationWizardStep.Capture };
                await CaptureWizardPoint(cancellation);
                Wizard.Answers["sheetselected"] = Math.Min(WizardTrainingCount, 4).ToString();
                wizardMessage = $"Saved {(row == 4 ? "the final check" : "Point " + (row + 1))}. " + (row < 4 ? "Move by hand to the next mark and capture its row." : "Click Calculate alignment to check the result.");
            }
        }
        else if (action is "fit" or "apply" or "rollback" or "inspect" or "open" or "new" or "archive" or "archive.cancel")
        {
            if (action is "fit" or "apply" && !SheetContactCompatible) throw new InvalidOperationException("Start a new tabletop calibration before fitting or applying these mixed reference types. The old report is retained.");
            await CalibrationEvent(action, value, cancellation);
            if (action is "new" or "archive" && calibrationIdentity is null) calibrationWizard = CalibrationWizardFlow.New(ActiveProfile.Id, Wizard.Epoch + 1);
        }
        else return;
        await SaveWizard(cancellation);
        if (action.StartsWith("capture.", StringComparison.Ordinal) || action is "new" or "archive" or "archive.cancel") await Render(cancellation);
        await Update(cancellation);
    }

    private void AddCalibrationSheetPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        bool left = SheetEdgeConfirmed("leftedgeconfirmed"), back = left && SheetEdgeConfirmed("frame");
        bool tool = back && Wizard.OperatorSaysTcpMatches && Wizard.Answers.ContainsKey("physicalpoint");
        bool surface = tool && Wizard.Answers.ContainsKey("sceneconfirmed");
        void Step(string action, string text, bool enabled) => patches[SheetId(action)] = new() { ["text"] = text, ["enabled"] = enabled };
        Step("left.confirm", left ? "LEFT confirmed" : "Confirm LEFT", !left);
        Step("back.show", "Show BACK", left);
        Step("back.confirm", back ? "BACK confirmed" : "Confirm BACK", left && !back);
        Step("tool.show", "Show jaw tips", back);
        Step("tool.confirm", tool ? "Jaw tips confirmed" : "Confirm jaw tips", back && !tool && wizardHighlightScope is { } marker && WizardHighlightCurrent(marker));
        Step("surface", surface ? "Desk confirmed" : "Base is on desk", tool && !surface);
        Step("precision.default", "Use ±5 mm", calibrationIdentity is null);
        patches[SheetId("field.precision")] = new() { ["text"] = Wizard.Answers.GetValueOrDefault("precision", ""), ["enabled"] = calibrationIdentity is null };
        patches["sheet.title"] = new() { ["text"] = $"Calibrate {ActiveProfile.Name} · {WizardTrainingCount}/4 captured + {WizardValidationCount}/1 check" };
        patches["sheet.save"] = new() { ["text"] = sheetSaveStatus };
        patches["sheet.action.feedback"] = new() { ["text"] = wizardMessage };
        patches["sheet.setup"] = new() { ["text"] = $"LEFT {(left ? "confirmed" : "pending")} · BACK {(back ? "confirmed" : "pending")} · Jaw tips {(tool ? "confirmed" : "pending")} · Desk {(surface ? "confirmed" : "pending")}" };
        int selected = SheetSelectedRow;
        string next = SheetSetupBlocker();
        if (next.Length == 0) next = SheetCaptureBlocker(selected, device);
        patches["sheet.next"] = new() { ["text"] = WizardValidationCount > 0 ? "All five points captured. Calculate alignment to check the result."
            : $"{SheetPointName(selected)}: " + (next.Length > 0 ? next : "Touch this physical cross with the jaw-tip midpoint. Hold still and click Capture.") };
        for (int row = 0; row < 5; row++)
        {
            bool captured = SheetSample(row) is not null;
            string reason = SheetCaptureBlocker(row, device);
            string measurementError = SheetMeasurementBlocker(row);
            string status = captured ? "Captured. This measured pose is saved."
                : measurementError.Length > 0 ? measurementError
                : row != selected ? "Select this point to locate its cross in the viewport."
                : reason.Length > 0 ? reason : "Ready: gently touch this cross, hold still, then Capture.";
            patches["sheet.row.status." + row] = new() { ["text"] = status };
            patches[SheetId("capture." + row)] = new() { ["text"] = captured ? "Captured" : "Capture " + SheetPointName(row), ["enabled"] = !captured && row == selected && reason.Length == 0, ["hint"] = reason };
            Step("move." + row, captured ? "Saved" : row == selected ? "Selected " + SheetPointName(row) : "Select " + SheetPointName(row), !captured && SheetPoint(row) is not null && link is not null);
        }
        patches[SheetId("fit")] = new() { ["enabled"] = SheetContactCompatible && WizardTrainingCount >= 4 && WizardValidationCount >= 1 && !calibrationPlacementUnverified };
        patches[SheetId("apply")] = new() { ["enabled"] = SheetSetupBlocker().Length == 0 && calibrationFit?.CanApply == true && calibrationApplied is null && !calibrationPlacementUnverified };
        patches[SheetId("rollback")] = new() { ["enabled"] = calibrationApplied is not null && !calibrationPlacementUnverified };
        patches["sheet.results"] = new() { ["text"] = WizardReviewSummary() };
    }
}
