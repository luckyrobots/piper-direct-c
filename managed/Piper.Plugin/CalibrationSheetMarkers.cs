using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private bool sheetMarkersDirty = true;
    private long sheetMarkersAt;
    private bool sheetMarkersPublished;
    private string sheetMarkerStatus = "Enter distances to place numbered marks in the viewport.";
    private string sheetSaveStatus = "Measurements save automatically as you type.";

    // Reading a visual reference must not fabricate any physical confirmation.
    private async Task<CalibrationPlacement> ReadSheetReference(CancellationToken cancellation)
    {
        var current = link ?? throw new InvalidOperationException("Link the scene robot first.");
        var identity = CurrentCalibrationIdentity();
        _ = Device(identity.DeviceId, identity.Generation);
        var reply = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId }, cancellation);
        var pose = JsonNode.Parse(reply.GetRawText())!.AsObject();
        if (!ReferenceEquals(link, current) || identity != CurrentCalibrationIdentity() ||
            pose["sceneId"]?.GetValue<string>() != current.SceneId || pose["entityId"]?.GetValue<string>() != current.EntityId ||
            pose["metricPlacementSupported"]?.GetValue<bool>() != true || pose["metricFrame"]?.GetValue<string>() != "engine-world-y-up" ||
            pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } asset || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } revision)
            throw new InvalidOperationException("Wait for the matching metric scene model to load.");
        _ = Device(identity.DeviceId, identity.Generation);
        var root = CalibrationPlacement.Read(pose["rootPlacement"]);
        bool changed = Wizard.ModelAssetId != asset || Wizard.ModelRevision != revision;
        if (changed && calibrationIdentity is not null) throw new InvalidOperationException("The recorded model changed. Keep the report and start a new calibration.");
        if (changed)
        {
            calibrationWizard = Wizard with { ModelAssetId = asset, ModelRevision = revision, OperatorSaysTcpMatches = false };
            Wizard.Answers.Remove("leftedgeconfirmed"); Wizard.Answers.Remove("frame"); Wizard.Answers.Remove("edgeroot");
        }
        if (!Wizard.Answers.ContainsKey("sceneconfirmed"))
            Wizard.Answers["observedrooty"] = Wizard.Answers["sceney"] = CalibrationNumber(root.Position.Y * 1000);
        Wizard.Answers["sheetreferencepreview"] = "read-only visual reference; not operator confirmation";
        return root;
    }

    private CalibrationPoint? SheetPoint(int row)
    {
        try
        {
            double width = CalibrationWizardFlow.Number(Wizard, "width", 100, 10000), depth = CalibrationWizardFlow.Number(Wizard, "depth", 100, 10000);
            double left = CalibrationWizardFlow.Number(Wizard, $"p{row}left", 0, width), back = CalibrationWizardFlow.Number(Wizard, $"p{row}back", 0, depth);
            return new(left / 1000 - width / 2000, CalibrationWizardFlow.SceneSurface(Wizard) / 1000 + .003, back / 1000 - depth / 2000);
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task PublishSheetMarks(CancellationToken cancellation, int frameRow = -1, bool frameAll = false)
    {
        var root = await ReadSheetReference(cancellation);
        var identity = WizardReferenceIdentity();
        long epoch = Wizard.Epoch;
        var points = Enumerable.Range(0, 5).Select(SheetPoint).ToArray();
        if (frameRow >= 0 && points[frameRow] is null) throw new InvalidOperationException("Enter this row's BACK and LEFT distances first, in millimetres within the desk edges.");
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
            label += $" · BACK {Wizard.Answers[$"p{row}back"]} / LEFT {Wizard.Answers[$"p{row}left"]} mm";
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
            sheetMarkersPublished = true;
        }
        sheetMarkerStatus = first < 0 ? "Enter BACK / LEFT distances to show marks." : "Numbered marks shown in the viewport. These are visual guides, not movement commands.";
    }

    // Called under the existing nonblocking visual-maintenance gate. Editing
    // saves immediately; slow rendering never blocks input or robot heartbeats.
    private async Task MaintainSheetMarks(CancellationToken cancellation)
    {
        if (!calibrationOpen || calibrationWizard is null || !Wizard.Answers.ContainsKey("sheetready") || link is null) return;
        if (!sheetMarkersDirty && Stopwatch.GetElapsedTime(sheetMarkersAt).TotalSeconds < 10) return;
        sheetMarkersDirty = false; sheetMarkersAt = Stopwatch.GetTimestamp();
        try { await PublishSheetMarks(cancellation, frameAll: !sheetMarkersPublished); }
        catch (Exception error) when (error is not OperationCanceledException) { sheetMarkerStatus = "Desk marks unavailable: " + error.Message; }
    }
}
