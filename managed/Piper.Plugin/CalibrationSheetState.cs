using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The next confirmation the calibration sheet needs before capture.</summary>
public enum SheetSetupStage { ConfirmLeft, ConfirmBack, ConfirmJawTips, ConfirmSurface, Ready }

/// <summary>
/// The operator's sheet confirmations. The stage advances in this order:
///   ConfirmLeft -> ConfirmBack -> ConfirmJawTips -> ConfirmSurface -> Ready
/// LEFT pins the simulated root (EdgeRoot) and a desk fingerprint (LeftProof);
/// BACK must repeat that fingerprint. An edge counts only while its proof
/// equals the fingerprint of the current robot, desk and pinned root, so a
/// changed desk or model sends the stage back to ConfirmLeft.
/// Invariants: BackProof is set only with LeftProof, and LeftProof only with EdgeRoot.
/// </summary>
public sealed record SheetSetup(string EdgeRoot = "", string LeftProof = "", string BackProof = "",
    bool JawTipsConfirmed = false, bool SurfaceConfirmed = false, bool ReferencePreviewed = false)
{
    /// <summary>Restores saved confirmations, dropping any that lost the confirmation before them.</summary>
    public static SheetSetup Restore(string edgeRoot, string leftProof, string backProof, bool jawTips, bool surface, bool previewed)
    {
        if (edgeRoot.Length > 512 || leftProof.Length > 512 || backProof.Length > 512)
            throw new InvalidOperationException("Saved wizard progress is invalid. Keep its report and start a new draft.");
        if (edgeRoot.Length == 0) leftProof = "";
        if (leftProof.Length == 0) backProof = "";
        return new(edgeRoot, leftProof, backProof, jawTips, surface, previewed);
    }

    public SheetSetup ConfirmLeft(string edgeRoot, string proof) => Checked(this with { EdgeRoot = edgeRoot, LeftProof = proof });
    public SheetSetup ConfirmBack(string proof) => LeftProof == proof && proof.Length > 0 ? Checked(this with { BackProof = proof })
        : throw new InvalidOperationException("The desk or robot changed since LEFT was confirmed. Go back and confirm the LEFT edge again.");
    public SheetSetup ClearEdges() => this with { EdgeRoot = "", LeftProof = "", BackProof = "" };

    public SheetSetupStage Stage(string? deskProof) =>
        deskProof is null || LeftProof != deskProof ? SheetSetupStage.ConfirmLeft
        : BackProof != deskProof ? SheetSetupStage.ConfirmBack
        : !JawTipsConfirmed ? SheetSetupStage.ConfirmJawTips
        : !SurfaceConfirmed ? SheetSetupStage.ConfirmSurface
        : SheetSetupStage.Ready;

    private static SheetSetup Checked(SheetSetup next)
    {
        if (next.BackProof.Length > 0 && next.LeftProof.Length == 0 || next.LeftProof.Length > 0 && next.EdgeRoot.Length == 0)
            throw new InvalidOperationException("A sheet edge confirmation lost the confirmation before it. Nothing was confirmed; confirm the LEFT edge again.");
        return next;
    }
}

public sealed partial class PipersPlugin
{
    private const double CheckSpacingMetres = .05;
    private static string SheetPointName(int row) => row == 4 ? "Check" : "P" + (row + 1);
    private int SheetSelectedRow => int.TryParse(Wizard.Answers.GetValueOrDefault("sheetselected"), out int row) && row is >= 0 and <= 4 ? row : Math.Min(WizardTrainingCount, 4);
    // Informational only: reopening a scene does not authorize editing its old
    // observations. Existing fit/apply/rollback identity guards stay unchanged.
    private bool SheetAppliedAlignmentMatchesModel => calibrationApplied is not null && calibrationIdentity is { } saved &&
        AcknowledgedModelRevision == saved.ModelRevision;

    private void UpdateSetup(Func<SheetSetup, SheetSetup> change) => calibrationWizard = Wizard with { Setup = change(Wizard.Setup) };

    // The fingerprint LEFT and BACK must match: the current robot, desk size,
    // tabletop and pinned root. Null when there is no pinned root or valid desk.
    private string? SheetDeskProof()
    {
        if (Wizard.Setup.EdgeRoot.Length == 0) return null;
        try
        {
            return WizardReferenceHighlightScope.DeskFingerprint(WizardReferenceIdentity(),
                CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000),
                CalibrationWizardFlow.SceneSurface(Wizard), CalibrationPlacement.Read(JsonNode.Parse(Wizard.Setup.EdgeRoot)));
        }
        catch (InvalidOperationException) { return null; }
    }

    private SheetSetupStage SheetStage() => Wizard.Setup.Stage(SheetDeskProof());

    private string SheetSetupBlocker() => SheetSetupBlocker(SheetStage());

    private string SheetSetupBlocker(SheetSetupStage stage)
    {
        if (link is null) return "Link this robot in Robot controls first. Linking displays its measured pose without moving hardware.";
        try
        {
            _ = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000);
            _ = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
        }
        catch (InvalidOperationException) { return "Enter the desk width and depth in millimetres."; }
        switch (stage)
        {
            case SheetSetupStage.ConfirmLeft: return "Show LEFT, compare it with your physical desk edge, then Confirm LEFT. Reconfirm after a connection or model change.";
            case SheetSetupStage.ConfirmBack: return "Show BACK, compare it with your physical desk edge, then Confirm BACK.";
            case SheetSetupStage.ConfirmJawTips: return "Show jaw tips. If the marker is between the simulated jaw tips, click Confirm jaw tips.";
            case SheetSetupStage.ConfirmSurface: return "Confirm that the simulated robot base is on the desk (Base is on desk).";
        }
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
        if (SheetSample(row) is { } sample)
            return sheetEditingRow == row ? ValidateSheetCorrection(row, sample).Blocker : "";
        if (SheetPoint(row) is not { } point) return "Enter BACK and LEFT distances for this cross, within the desk edges (mm).";
        if (row < 4 && SheetSample(4) is { } check && point.PlanarDistance(check.MeasuredWorld) < CheckSpacingMetres)
            return "Keep each reference point at least 50 mm from the captured check cross.";
        // Reject duplicates before the operator spends time touching them.
        for (int previous = 0; previous < row; previous++)
            if (SheetPoint(previous) is { } other && point.PlanarDistance(other) < (row == 4 ? CheckSpacingMetres : CalibrationSolver.ReferenceDuplicateDistanceMetres))
                return row == 4 ? "Use a check cross at least 50 mm from every reference point." : $"This overlaps {SheetPointName(previous)} (within 1 mm). Use a distinct physical cross.";
        if (row == 4) return "";
        double left = CalibrationWizardFlow.Number(Wizard, $"p{row}left", 0, 10000), back = CalibrationWizardFlow.Number(Wizard, $"p{row}back", 0, 10000);
        return SheetReservedCrossBlocker(left, back);
    }

    private string SheetReservedCrossBlocker(double left, double back) =>
        WizardCross() is { } cross && Math.Sqrt(Math.Pow(left - cross.LeftMm, 2) + Math.Pow(back - cross.BackMm, 2)) < CheckSpacingMetres * 1000
            ? "Keep the centre cross and the 50 mm around it for the final independent check. Choose another reference cross."
            : "";

    private string SheetCaptureBlocker(int row, JsonObject? device) => SheetCaptureBlocker(row, device, SheetSetupBlocker());

    // setupBlocker is SheetSetupBlocker(), computed once per patch pass.
    private string SheetCaptureBlocker(int row, JsonObject? device, string setupBlocker)
    {
        if (SheetSample(row) is not null) return "Already captured.";
        string error = SheetMeasurementBlocker(row);
        if (error.Length > 0) return error;
        if (setupBlocker.Length > 0) return setupBlocker;
        if (row != Math.Min(WizardTrainingCount, 4)) return "Capture " + SheetPointName(Math.Min(WizardTrainingCount, 4)) + " first.";
        if (device is null || device["fresh"]?.GetValue<bool>() != true) return "Waiting for fresh robot feedback. Keep the robot connected.";
        if (!TeachingState.Inspect(device).HandGuiding) return "Single-click the physical teach button for solid green, then move the arm by hand to this cross.";
        if (device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4) return "Touch the cross and hold the arm still for 0.4 seconds.";
        if (!DeviceHealth.IsHealthy(device) || device["status"]?["arm_status"]?.GetValue<int>() is not (0 or 11))
            return "Robot feedback reports a controller/joint problem. Review Robot controls before capturing.";
        if (state?["motion"]?["active"]?.GetValue<bool>() == true || followRequest != 0) return "Finish the current movement before capturing.";
        return "";
    }
}
