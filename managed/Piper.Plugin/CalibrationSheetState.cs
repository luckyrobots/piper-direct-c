using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private static string SheetPointName(int row) => row == 4 ? "Check" : "P" + (row + 1);
    private int SheetSelectedRow => int.TryParse(Wizard.Answers.GetValueOrDefault("sheetselected"), out int row) && row is >= 0 and <= 4 ? row : Math.Min(WizardTrainingCount, 4);

    private bool SheetEdgeConfirmed(string key)
    {
        try
        {
            if (!Wizard.Answers.TryGetValue("edgeroot", out string? root)) return false;
            string proof = WizardReferenceHighlightScope.DeskFingerprint(WizardReferenceIdentity(),
                CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000),
                CalibrationWizardFlow.SceneSurface(Wizard), CalibrationPlacement.Read(JsonNode.Parse(root)));
            return Wizard.Answers.GetValueOrDefault(key) == proof;
        }
        catch (InvalidOperationException) { return false; }
    }

    private string SheetSetupBlocker()
    {
        if (link is null) return "Link this robot in Robot controls first. Linking displays its measured pose without moving hardware.";
        try
        {
            _ = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000);
            _ = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        }
        catch (InvalidOperationException) { return "Enter the desk width and depth in millimetres."; }
        if (!SheetEdgeConfirmed("leftedgeconfirmed")) return "Show LEFT, compare it with your physical desk edge, then Confirm LEFT. Reconfirm after a connection or model change.";
        if (!SheetEdgeConfirmed("frame")) return "Show BACK, compare it with your physical desk edge, then Confirm BACK.";
        if (!Wizard.OperatorSaysTcpMatches || !Wizard.Answers.ContainsKey("physicalpoint")) return "Show jaw tips. If the marker is between the simulated jaw tips, click Confirm jaw tips.";
        if (!Wizard.Answers.ContainsKey("sceneconfirmed")) return "Confirm that the simulated robot base is on the desk (Base is on desk).";
        try { _ = CalibrationWizardFlow.Number(Wizard, "precision", 1, 10); }
        catch (InvalidOperationException) { return "Measurement accuracy cannot be 0. Enter ±1–10 mm, or click Use ±5 mm."; }
        if (!SheetContactCompatible) return "This saved report uses raised or grid points. Start New calibration for tabletop contacts.";
        if (calibrationPlacementUnverified) return "Click Check saved placement before recording more points.";
        if (calibrationApplied is not null) return "Alignment already applied. Start New calibration to record a new set.";
        if (calibrationIdentity is not null && calibrationIdentity != CurrentCalibrationIdentity()) return "Robot connection changed after a capture. Keep the saved report and start New calibration.";
        return "";
    }

    private string SheetMeasurementBlocker(int row)
    {
        if (SheetSample(row) is not null && sheetEditingRow != row) return "";
        if (SheetPoint(row) is not { } point) return "Enter BACK and LEFT distances for this cross, within the desk edges (mm).";
        // Reject duplicates before the operator spends time touching them.
        for (int previous = 0; previous < row; previous++)
            if (SheetPoint(previous) is { } other && point.PlanarDistance(other) < (row == 4 ? .05 : CalibrationSolver.ReferenceDuplicateDistanceMetres))
                return row == 4 ? "Use a check cross at least 50 mm from every reference point." : $"This overlaps {SheetPointName(previous)} (within 1 mm). Use a distinct physical cross.";
        if (row < 4 && WizardCross() is { } cross)
        {
            double left = CalibrationWizardFlow.Number(Wizard, $"p{row}left", 0, 10000), back = CalibrationWizardFlow.Number(Wizard, $"p{row}back", 0, 10000);
            if (Math.Sqrt(Math.Pow(left - cross.LeftMm, 2) + Math.Pow(back - cross.BackMm, 2)) < 50)
                return "Keep the centre cross and the 50 mm around it for the final independent check. Choose another reference cross.";
        }
        return "";
    }

    private string SheetCaptureBlocker(int row, JsonObject? device)
    {
        if (SheetSample(row) is not null) return "Already captured.";
        string error = SheetMeasurementBlocker(row);
        if (error.Length > 0) return error;
        error = SheetSetupBlocker();
        if (error.Length > 0) return error;
        if (row != Math.Min(WizardTrainingCount, 4)) return "Capture " + SheetPointName(Math.Min(WizardTrainingCount, 4)) + " first.";
        if (device is null || device["fresh"]?.GetValue<bool>() != true) return "Waiting for fresh robot feedback. Keep the robot connected.";
        if (!TeachingState.Inspect(device).HandGuiding) return "Single-click the physical teach button for solid green, then move the arm by hand to this cross.";
        if (device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4) return "Touch the cross and hold the arm still for 0.4 seconds.";
        if (device["status"]?["error"]?.GetValue<int>() != 0 || device["status"]?["arm_status"]?.GetValue<int>() is not (0 or 11) ||
            device["joints"] is not JsonArray { Count: 6 } joints || joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j?["fault"]?.GetValue<int>() != 0))
            return "Robot feedback reports a controller/joint problem. Review Robot controls before capturing.";
        if (state?["motion"]?["active"]?.GetValue<bool>() == true || followRequest != 0) return "Finish the current movement before capturing.";
        return "";
    }
}
