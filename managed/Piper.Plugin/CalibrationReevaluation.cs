using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private CalibrationModelReevaluation? calibrationReevaluation;

    private string AcknowledgedModelRevision => calibrationIdentity is { } saved &&
        link is { } current && acknowledgedScenePose?["pose"] is JsonObject pose && pose["sceneId"]?.GetValue<string>() == current.SceneId &&
        pose["entityId"]?.GetValue<string>() == saved.RobotId && pose["modelAssetId"]?.GetValue<string>() == saved.ModelAssetId
            ? pose["modelRevision"]?.GetValue<string>() ?? "" : "";
    private bool SavedModelChanged => calibrationIdentity is { } saved && link is { } current &&
        (current.SceneId != saved.SceneId || AcknowledgedModelRevision is { Length: > 0 } revision && revision != saved.ModelRevision);
    private bool CanCheckUpdatedModel => calibrationSamples.Count > 0 && (SavedModelChanged || AcknowledgedModelRevision.Length == 0);

    private static bool SameReevaluationRobot(CalibrationIdentity saved, CalibrationIdentity live) => saved.ProfileId == live.ProfileId &&
        saved.DeviceId == live.DeviceId && saved.Generation == live.Generation && saved.BackendSessionFingerprint == live.BackendSessionFingerprint &&
        saved.RobotId == live.RobotId;

    private bool CanReevaluateWithLiveScene
    {
        get
        {
            try { return calibrationIdentity is { } saved && SameReevaluationRobot(saved, CurrentCalibrationIdentity()) &&
                calibrationApplied is null && !calibrationPlacementUnverified && SheetContactCompatible; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private void RequireReevaluationScope(CalibrationIdentity saved, CalibrationIdentity live, CalibrationPlacement root, CalibrationSample[] samples)
    {
        RequireCalibrationIdle();
        if (!calibrationOpen || sheetEditingRow >= 0 || calibrationIdentity != saved || calibrationApplied is not null ||
            calibrationPlacementUnverified || !SheetContactCompatible || link is null || !SameReevaluationRobot(saved, live) || CurrentCalibrationIdentity() != live ||
            calibrationOriginal is null || !calibrationOriginal.Same(root) || !calibrationSamples.SequenceEqual(samples) ||
            Wizard.Answers.GetValueOrDefault("tool") != calibrationDesk.ToolPoint)
            throw new InvalidOperationException("Keep the same robot, original placement and contact point, finish any edits, and undo/verify an applied alignment before re-evaluating saved poses.");
        _ = Device(saved.DeviceId, saved.Generation);
    }

    private async Task<JsonObject> ReadReevaluationModel(CalibrationIdentity saved, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var response = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = saved.SceneId, ["entityId"] = saved.RobotId }, deadline.Token);
        return JsonNode.Parse(response.GetRawText())!.AsObject();
    }

    private void InvalidateReevaluationSetup(string revision)
    {
        calibrationWizard = Wizard with { ModelAssetId = calibrationIdentity!.ModelAssetId, ModelRevision = revision,
            OperatorSaysTcpMatches = false, Step = CalibrationWizardStep.ModelPoint, History = [] };
        foreach (string key in new[] { "leftedgeconfirmed", "frame", "edgeroot", "physicalpoint", "sceneconfirmed", "sheetreferencepreview" })
            Wizard.Answers.Remove(key);
        Wizard.Answers["reevaluatedscene"] = calibrationIdentity.SceneId;
        wizardHighlightScope = null; wizardReferenceHighlightScope = null; wizardReferenceAcknowledgedAt = 0;
        sheetMarkersDirty = true; sheetMarkersPublished = false;
    }

    // A report may commit before its wizard draft. Restore the authoritative
    // model revision and discard old visual confirmations after that interruption.
    private void ReconcileReevaluatedWizard()
    {
        if (calibrationReevaluation is not null && calibrationIdentity is { } saved &&
            (Wizard.ModelAssetId != saved.ModelAssetId || Wizard.ModelRevision != saved.ModelRevision ||
                calibrationReevaluation.SceneId.Length > 0 && Wizard.Answers.GetValueOrDefault("reevaluatedscene") != saved.SceneId))
        {
            InvalidateReevaluationSetup(saved.ModelRevision);
            wizardMessage = "Saved poses were re-evaluated in the current scene and model. Reconfirm LEFT, BACK, jaw tips and the desk, then Calculate alignment.";
        }
    }

    private async Task ReevaluateCalibrationModel(CancellationToken cancellation)
    {
        var saved = calibrationIdentity ?? throw new InvalidOperationException("Capture and save desk points before re-evaluating a corrected model.");
        var root = calibrationOriginal ?? throw new InvalidOperationException("The saved calibration has no original base placement.");
        var originals = calibrationSamples.ToArray();
        if (originals.Length is < 1 or > 24) throw new InvalidOperationException("There are no valid saved observations to re-evaluate.");
        var live = CurrentCalibrationIdentity();
        RequireReevaluationScope(saved, live, root, originals);
        var model = await ReadReevaluationModel(live, cancellation);
        if (model["metricPlacementSupported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException((model["metricPlacementReason"]?.GetValue<string>() is { Length: > 0 } metricReason
                ? metricReason : "This scene model does not support metric placement.") + " The saved report is unchanged.");
        if (model["kinematicEvaluationSupported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException((model["kinematicEvaluationReason"]?.GetValue<string>() is { Length: > 0 } evaluationReason
                ? evaluationReason : "This engine/model does not support read-only kinematic evaluation.") + " The saved report is unchanged.");
        if (model["modelAssetId"]?.GetValue<string>() != saved.ModelAssetId || model["modelRevision"]?.GetValue<string>() is not { Length: > 0 } revision ||
            revision == saved.ModelRevision && live.SceneId == saved.SceneId)
            throw new InvalidOperationException("Load the same model in the reopened scene, or a corrected model revision, in an engine that supports read-only kinematic evaluation. The saved report is unchanged.");
        var updated = saved with { SceneId = live.SceneId, ModelRevision = revision };
        CalibrationModelEvaluation.ValidateModel(model, updated, root);
        var evaluated = new List<CalibrationSample>();
        var receipts = new JsonArray();
        foreach (var sample in originals)
        {
            RequireReevaluationScope(saved, live, root, originals);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await engine.RequestAsync("robot.evaluate", new JsonObject
            {
                ["sceneId"] = live.SceneId, ["entityId"] = saved.RobotId, ["expectedModelAssetId"] = updated.ModelAssetId,
                ["expectedModelRevision"] = updated.ModelRevision, ["expectedRootPlacement"] = root.Json(),
                ["toolSite"] = calibrationDesk.ToolPoint, ["joints"] = CalibrationModelEvaluation.JointInputs(sample)
            }, deadline.Token);
            var receipt = JsonNode.Parse(response.GetRawText())!.AsObject();
            var point = CalibrationModelEvaluation.Read(receipt, updated, root, calibrationDesk.ToolPoint, sample);
            evaluated.Add(sample with { SceneWorld = point });
            receipts.Add(receipt);
        }
        CalibrationModelEvaluation.ValidateModel(await ReadReevaluationModel(live, cancellation), updated, root);
        RequireReevaluationScope(saved, live, root, originals);
        if (!File.Exists(calibrationPath) || new FileInfo(calibrationPath).Length > 1024 * 1024)
            throw new InvalidOperationException("The original saved calibration report is unavailable or too large; no replacement was written.");
        string previousReport = await File.ReadAllTextAsync(calibrationPath, cancellation);
        var persisted = JsonSerializer.Deserialize<CalibrationReport>(previousReport, PluginProtocol.Json);
        if (persisted is null || persisted.Identity != saved || persisted.AppliedPlacement is not null || persisted.PlacementUnverified ||
            !root.Same(persisted.OriginalPlacement) || persisted.Desk != calibrationDesk || persisted.Samples.Length != originals.Length ||
            persisted.Samples.Where((sample, i) => sample.Name != originals[i].Name || sample.SceneWorld != originals[i].SceneWorld ||
                sample.MeasuredWorld != originals[i].MeasuredWorld || sample.Measurement != originals[i].Measurement || sample.HeldOut != originals[i].HeldOut ||
                sample.HostUnixMilliseconds != originals[i].HostUnixMilliseconds || sample.PoseFingerprint != originals[i].PoseFingerprint ||
                !sample.JointDegrees.SequenceEqual(originals[i].JointDegrees)).Any())
            throw new InvalidOperationException("The saved report changed outside this panel. Reload and inspect it before re-evaluating; no replacement was written.");
        string directory = Path.Combine(RobotCalibrationDirectory, "model-reevaluations");
        Directory.CreateDirectory(directory);
        string auditName = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + ".json";
        await File.WriteAllTextAsync(Path.Combine(directory, auditName), JsonSerializer.Serialize(new
        {
            reason = "Explicit re-evaluation of saved physical observations after a model correction or reopening the scene; no new hardware capture or motion",
            previousReport = JsonDocument.Parse(previousReport).RootElement.Clone(),
            previousWizard = Wizard, previousSceneId = saved.SceneId, sceneId = live.SceneId,
            previousModelRevision = saved.ModelRevision, modelRevision = revision, evaluations = receipts
        }, PluginProtocol.Json), cancellation);
        RequireReevaluationScope(saved, live, root, originals);
        var oldFit = calibrationFit; var oldEvaluation = calibrationReevaluation;
        calibrationIdentity = updated; calibrationSamples.Clear(); calibrationSamples.AddRange(evaluated); calibrationFit = null;
        calibrationReevaluation = new(saved.ModelRevision, revision, "model-reevaluations/" + auditName, DateTimeOffset.UtcNow,
            PreviousSceneId: saved.SceneId, SceneId: live.SceneId);
        try { await SaveCalibration(cancellation); }
        catch
        {
            calibrationIdentity = saved; calibrationSamples.Clear(); calibrationSamples.AddRange(originals);
            calibrationFit = oldFit; calibrationReevaluation = oldEvaluation; throw;
        }
        InvalidateReevaluationSetup(revision);
        wizardMessage = "Saved poses re-evaluated in the current scene and model; all physical angles, measurements and capture times are preserved. Reconfirm LEFT, BACK, jaw tips and the desk, then Calculate alignment. Nothing has moved or been applied.";
        await SaveWizard(cancellation);
    }
}
