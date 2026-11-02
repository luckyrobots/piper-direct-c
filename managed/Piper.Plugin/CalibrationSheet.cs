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
        new("input", SheetId("field." + key)) { Text = text ?? Wizard.Answers.GetValueOrDefault(key, ""), Width = 80, Hint = hint, Enabled = enabled };
    private Widget SheetButton(string action, string text, string? hint = null) =>
        Widget.Button(SheetId(action), text) with { Hint = hint, Padding = 7 };

    private Widget CalibrationSheetControls()
    {
        LoadWizard(); LoadSheetRows();
        var body = new List<Widget>
        {
            Widget.Row("sheet.header", Widget.Label("sheet.title", "Calibrate " + ActiveProfile.Name) with { Appearance = "heading" },
                Widget.Button("workspace.controls@sheet", "Robot controls")),
            Widget.Label("sheet.instructions", "Mark four reachable crosses on the desk. Enter ALL measurements below. Then single-click the arm button for solid green hand guiding. For each row, gently touch its cross with the midpoint between BOTH jaw tips, hold still, and Capture.") with { Height = 75 },
            Widget.Label("sheet.contact", "Touch the tabletop — height is 0 mm. No hovering, automatic robot movement, or Next/Back steps.") with { Height = 36 },
            Widget.Row("sheet.desk", Widget.Label("sheet.desk.label", "Desk mm · width / depth / precision ±") with { Width = 255 },
                SheetInput("width", "Full desk width in mm", calibrationIdentity is null), SheetInput("depth", "Full desk depth in mm", calibrationIdentity is null),
                SheetInput("precision", "Measurement uncertainty in mm (1–10)", calibrationIdentity is null)),
            Widget.Row("sheet.references", SheetButton("left.show", "Show LEFT"), SheetButton("left.confirm", "Confirm LEFT"),
                SheetButton("back.show", "Show BACK"), SheetButton("back.confirm", "Confirm BACK")),
            Widget.Row("sheet.tool", SheetButton("tool.show", "Show jaw-tip marker"), SheetButton("tool.confirm", "Marker matches jaw tips"),
                SheetButton("surface", "Base rests on tabletop")),
            Widget.Label("sheet.tool.info", wizardModelPoint) with { Height = 34, Hint = wizardModelPointDetails },
            Widget.Label("sheet.setup", $"Jaw tips: {(Wizard.OperatorSaysTcpMatches ? "confirmed" : "show and confirm marker")} · LEFT: {(Wizard.Answers.ContainsKey("leftedgeconfirmed") ? "confirmed" : "confirm marker")} · BACK: {(Wizard.Answers.ContainsKey("frame") ? "confirmed" : "confirm marker")}. Fixed edges do not change with your seating position.") with { Height = 45 },
            Widget.Row("sheet.columns", Widget.Label("sheet.point.column", "Desk mark") with { Width = 85 },
                Widget.Label("sheet.back.column", "BACK mm") with { Width = 95 }, Widget.Label("sheet.left.column", "LEFT mm") with { Width = 95 },
                Widget.Label("sheet.action.column", "Hand-guide the arm, then capture"))
        };
        for (int row = 0; row < 5; row++)
        {
            var sample = SheetSample(row);
            string title = row == 4 ? "Final check" : "Point " + (row + 1);
            body.Add(Widget.Row("sheet.row." + row,
                Widget.Label("sheet.name." + row, title) with { Width = 85 },
                SheetInput($"p{row}back", "Distance from fixed BACK edge, in mm. cm × 10.", sample is null, sample is null ? null : CalibrationNumber(sample.Measurement.BackMm)),
                SheetInput($"p{row}left", "Distance from fixed LEFT edge, in mm. cm × 10.", sample is null, sample is null ? null : CalibrationNumber(sample.Measurement.LeftMm)),
                SheetButton("move." + row, sample is null ? "Move arm to this mark" : "Recorded ✓",
                    "Instructions only: this button never moves the robot.") with { Width = 180, Enabled = sample is null },
                SheetButton("capture." + row, "Capture") with { Width = 88, Enabled = sample is null }));
        }
        body.Add(Widget.Label("sheet.check.help", "Final check: use your existing centre cross. It tests the alignment and is NOT used to calculate it. All distances are mm; e.g. 20 / 69.7 cm → BACK 200 / LEFT 697.") with { Height = 47 });
        if (!SheetContactCompatible) body.Add(Widget.Label("sheet.legacy", "This saved report contains raised or grid points. Keep the report and start New calibration for tabletop-touch points; old measurements are not converted.") with { Height = 55 });
        body.Add(Widget.Row("sheet.fit.actions", SheetButton("fit", "Calculate alignment"), SheetButton("apply", "Apply to simulation"), SheetButton("rollback", "Undo alignment")));
        body.Add(Widget.Label("sheet.results", WizardReviewSummary()) with { Height = 105 });
        body.Add(Widget.Row("sheet.report.actions", SheetButton("open", "Saved report"), SheetButton("inspect", "Check saved placement"), SheetButton("new", "New calibration")));
        if (calibrationArchiveConfirmation)
            body.Add(Widget.Row("sheet.archive", SheetButton("archive", "Keep old report & start new"), SheetButton("archive.cancel", "Cancel")));
        body.Add(Widget.Label("sheet.status", wizardMessage.Length > 0 ? wizardMessage : robotCalibrationStatus) with { Height = 65 });
        var camera = Widget.Column("sheet.camera", Widget.Label("sheet.camera.title", "Camera view"),
            new Widget("dropdown", WizardId("camera")) { Options = ["Shared bench", "Pika gripper", "D405 camera"], Value = Array.IndexOf(new[] { "bench", "gripper", "depth" }, Wizard.HelpCamera) },
            new Widget("image", "wizard.camera.image") { ImageId = CameraImageId(Wizard.HelpCamera), Height = 160, Visible = CameraFrameReady(Wizard.HelpCamera) },
            Widget.Label("sheet.camera.help", "Touch each physical mark gently; do not press into the desk. Use the same jaw-tip midpoint every time. Spread the four marks across both desk directions, away from the final check.") with { Height = 115 }) with { Width = 235, Padding = 10, Spacing = 10 };
        return Widget.Row("sheet.page", Widget.Column("sheet.body", body.ToArray()) with { Padding = 12, Spacing = 9 }, camera);
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
            await SaveWizard(cancellation); return;
        }
        if (action == "tool.show")
        {
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
        }
        else if (action == "surface")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("The tabletop reference is fixed for recorded points.");
            var root = await ReadWizardMetricRoot(cancellation);
            Wizard.Answers["observedrooty"] = Wizard.Answers["sceney"] = CalibrationNumber(root.Position.Y * 1000);
            Wizard.Answers["sceneconfirmed"] = "operator confirmed simulated base rests on tabletop";
            calibrationDesk = calibrationDesk with { SurfaceY = root.Position.Y };
            Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("edgeroot");
        }
        else if (action is "left.show" or "back.show" or "left.confirm" or "back.confirm")
        {
            var step = action.StartsWith("left", StringComparison.Ordinal) ? CalibrationWizardStep.DeskFrame : CalibrationWizardStep.DeskBack;
            if (Wizard.Step != step) calibrationWizard = Wizard with { Step = step, Epoch = Wizard.Epoch + 1 };
            await ShowWizardReference(cancellation, frame: action.EndsWith("show", StringComparison.Ordinal));
            if (action.EndsWith("confirm", StringComparison.Ordinal)) ConfirmWizardReference();
        }
        else if (action.StartsWith("move.", StringComparison.Ordinal) || action.StartsWith("capture.", StringComparison.Ordinal))
        {
            if (!int.TryParse(action.Split('.')[1], out int row) || row is < 0 or > 4) throw new InvalidOperationException("Unknown point.");
            if (SheetSample(row) is not null) throw new InvalidOperationException("This point is already recorded.");
            if (action.StartsWith("move.", StringComparison.Ordinal))
                wizardMessage = $"{(row == 4 ? "Final check" : "Point " + (row + 1))}: single-click the physical arm button for solid green. By hand, gently touch THIS cross with the midpoint between both jaw tips. Hold still, then click Capture on this row. This button sends no movement command.";
            else
            {
                if (row < 4 && row != WizardTrainingCount) throw new InvalidOperationException("Capture reference rows in order: Point " + (WizardTrainingCount + 1) + " is next. You can enter all measurements now.");
                if (row == 4 && WizardTrainingCount < 4) throw new InvalidOperationException("Capture the four reference points before the final check.");
                Wizard.Answers["left"] = Wizard.Answers.GetValueOrDefault($"p{row}left", "");
                Wizard.Answers["back"] = Wizard.Answers.GetValueOrDefault($"p{row}back", "");
                Wizard.Answers["height"] = "0"; Wizard.Answers["method"] = "edge measurements"; Wizard.Answers["role"] = row == 4 ? "validation" : "training";
                calibrationWizard = Wizard with { Step = CalibrationWizardStep.Capture };
                await CaptureWizardPoint(cancellation);
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
        await SaveWizard(cancellation); await Render(cancellation); await Update(cancellation);
    }

    private void AddCalibrationSheetPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        patches["sheet.status"] = new() { ["text"] = wizardMessage.Length > 0 ? wizardMessage : robotCalibrationStatus };
        bool ready = device is not null && TeachingState.Inspect(device).HandGuiding && device["status"]?["stationary_s"]?.GetValue<double>() is >= .4 &&
            SheetContactCompatible && !calibrationPlacementUnverified && calibrationApplied is null && (calibrationIdentity is null || link is not null && calibrationIdentity == CurrentCalibrationIdentity());
        for (int row = 0; row < 5; row++)
            patches[SheetId("capture." + row)] = new() { ["enabled"] = ready && SheetSample(row) is null && (row == 4 ? WizardTrainingCount >= 4 : row == WizardTrainingCount) };
        patches[SheetId("fit")] = new() { ["enabled"] = SheetContactCompatible && WizardTrainingCount >= 4 && WizardValidationCount >= 1 && !calibrationPlacementUnverified };
        patches[SheetId("apply")] = new() { ["enabled"] = ready && calibrationFit?.CanApply == true };
        patches[SheetId("rollback")] = new() { ["enabled"] = calibrationApplied is not null && !calibrationPlacementUnverified };
    }
}
