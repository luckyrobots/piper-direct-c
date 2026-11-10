using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal string sheetSaveStatus => calibration.Wizard.Session.SaveStatus;

    // Reading a visual reference must not fabricate any physical confirmation.
    private async Task<CalibrationPlacement> ReadSheetReference(CancellationToken cancellation)
    {
        if (link is null) throw new InvalidOperationException("Link the scene robot first.");
        var identity = CurrentCalibrationIdentity();
        _ = Device(identity.DeviceId, identity.Generation);
        const string loading = "Wait for the matching metric scene model to load.";
        var (_, pose) = await ReadMetricPose("Link the scene robot first.", loading, cancellation);
        if (identity != CurrentCalibrationIdentity() ||
            pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } asset || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } revision)
            throw new InvalidOperationException(loading);
        _ = Device(identity.DeviceId, identity.Generation);
        var root = CalibrationPlacement.Read(pose["rootPlacement"]);
        bool changed = Wizard.ModelAssetId != asset || Wizard.ModelRevision != revision;
        if (changed && calibrationIdentity is not null) throw new InvalidOperationException("The recorded model changed. Re-evaluate saved poses with the corrected model before confirming its pointers; the original report is retained.");
        // A changed model voids the edge proofs and the jaw-tip comparison together.
        if (changed)
            calibration.Wizard.Edit(draft => draft with { ModelAssetId = asset, ModelRevision = revision, Setup = draft.Setup.ClearEdges() with { JawTipsConfirmed = false } });
        if (!Wizard.Setup.SurfaceConfirmed)
            calibration.Wizard.Answer("sceney", CalibrationNumber(root.Position.Y * 1000));
        calibration.Wizard.ChangeSetup(setup => setup with { ReferencePreviewed = true });
        return root;
    }

    internal CalibrationPoint? SheetPoint(int row)
    {
        try
        {
            double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
            double left = CalibrationWizardFlow.Number(Wizard, $"p{row}left", 0, width), back = CalibrationWizardFlow.Number(Wizard, $"p{row}back", 0, depth);
            return new(left / 1000 - width / 2000, CalibrationWizardFlow.SceneSurface(Wizard) / 1000, back / 1000 - depth / 2000);
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task ShowSheetMarks(CancellationToken cancellation, int frameRow = -1)
    {
        await calibration.Markers.Gate.WaitAsync(cancellation);
        try { await PublishSheetMarksWithinGate(cancellation, frameRow, frameAll: frameRow < 0); }
        finally { calibration.Markers.Gate.Release(); }
    }

    private async Task PublishSheetMarksWithinGate(CancellationToken cancellation, int frameRow = -1, bool frameAll = false)
    {
        var root = await ReadSheetReference(cancellation);
        var identity = WizardReferenceIdentity();
        long epoch = Wizard.Epoch;
        var points = Enumerable.Range(0, 5).Select(SheetPoint).ToArray();
        if (frameRow >= 0 && points[frameRow] is null) throw new InvalidOperationException("Enter this row's BACK and LEFT distances first, in millimetres within the desk edges.");
        if (frameRow >= 0)
        {
            calibration.Wizard.Answer("sheetselected", frameRow.ToString());
            calibration.Markers.InvalidateSheet();
        }
        int selected = SheetSelectedRow;
        var all = new JsonArray(points.Where(p => p.HasValue).Select(p => (JsonNode)p!.Value.Json()).ToArray());
        int first = Array.FindIndex(points, p => p is not null);
        for (int row = 0; row < 5; row++)
        {
            if (!calibrationOpen || epoch != Wizard.Epoch || identity != WizardReferenceIdentity()) return;
            _ = Device(identity.DeviceId, identity.Generation);
            if (points[row] is not { } point)
            {
                await engine.RequestAsync("robot.highlightReference", new JsonObject { ["visible"] = false, ["markerId"] = "desk-point-" + row }, cancellation);
                continue;
            }
            string label = row == 4 ? "CHECK" : "P" + (row + 1);
            if (row == selected) label += " - SELECTED";
            bool frame = row == frameRow || frameAll && row == first;
            var scope = new WizardReferenceHighlightScope(identity, epoch, Wizard.Step,
                CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000),
                CalibrationWizardFlow.SceneSurface(Wizard), point, label, root);
            var request = scope.Request(true, frame);
            request["markerId"] = "desk-point-" + row;
            if (frameAll && frame) request["framePoints"] = all.DeepClone();
            var response = await engine.RequestAsync("robot.highlightReference", request, cancellation);
            if (!scope.Acknowledged(response, frame) || !response.TryGetProperty("markerId", out var returnedId) || returnedId.GetString() != "desk-point-" + row)
                throw new InvalidOperationException("Update Lucky Engine: the editor did not acknowledge the numbered desk marker.");
            calibration.Markers.MarkSheetPublished();
        }
    }

    // Called under the existing nonblocking visual-maintenance gate. Editing
    // saves immediately; slow rendering never blocks input or robot heartbeats.
    private async Task MaintainSheetMarks(CancellationToken cancellation)
    {
        if (!calibrationOpen || calibrationWizard is null || !Wizard.Answers.ContainsKey("sheetready") || link is null) return;
        if (!calibration.Markers.SheetDirty && Stopwatch.GetElapsedTime(calibration.Markers.SheetAttemptedAt).TotalSeconds < 10) return;
        calibration.Markers.BeginSheetPublish(Stopwatch.GetTimestamp());
        try { await PublishSheetMarksWithinGate(cancellation, frameAll: !calibration.Markers.SheetPublished); }
        catch (Exception error) when (error is not OperationCanceledException) { Trace.TraceWarning("Desk marks unavailable: {0}", error.Message); }
    }
}
