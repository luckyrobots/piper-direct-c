using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed record CalibrationPreferences(CalibrationDesk Desk, CalibrationMeasurement Measurement);

public sealed partial class PipersPlugin
{
    internal bool calibrationPlacementUnverified, calibrationArchiveConfirmation;
    internal string calibrationProfile = "";
    internal string calibrationPath = "";
    internal CalibrationDesk calibrationDesk = new();
    private CalibrationMeasurement calibrationMeasurement = new();
    internal CalibrationIdentity? calibrationIdentity;
    internal CalibrationPlacement? calibrationOriginal, calibrationApplied;
    internal CalibrationFit? calibrationFit;
    internal readonly List<CalibrationSample> calibrationSamples = [];
    private string RobotCalibrationDirectory => Path.Combine(engine.Start.SettingsDirectory, "calibration", ActiveProfile.Id);
    private static string CalibrationNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // Desk calibration session (Cal-A), derived from the session fields:
    //   None -> Capturing           start pins identity and original placement
    //   Capturing -> Fitted         fit
    //   Fitted -> Capturing         capture, correction or re-evaluation clears the fit
    //   Fitted|Applied -> WritePending   apply or rollback sent; reply not verified yet
    //   WritePending -> Applied     verified apply, or inspect finds the proposal
    //   WritePending -> Fitted      verified rollback, a failed pre-send check, or inspect finds the original
    //   any -> None                 new/archive (WritePending and Applied ask first) or a profile change
    // Illegal (see CalibrationSessionFault): placement or samples without an
    // identity, or an applied placement without the fit that proposed it. Such a
    // session is never saved; New discards it and says so.
    private enum CalibrationPhase { None, Capturing, Fitted, WritePending, Applied }

    private CalibrationPhase CalibrationSessionPhase =>
        calibrationIdentity is null ? CalibrationPhase.None
        : calibrationPlacementUnverified ? CalibrationPhase.WritePending
        : calibrationApplied is not null ? CalibrationPhase.Applied
        : calibrationFit is not null ? CalibrationPhase.Fitted
        : CalibrationPhase.Capturing;

    internal string? CalibrationSessionFault =>
        calibrationIdentity is null && (calibrationOriginal is not null || calibrationApplied is not null || calibrationFit is not null ||
            calibrationPlacementUnverified || calibrationSamples.Count > 0) ? "Calibration session data exists without its identity."
        : calibrationApplied is not null && calibrationFit is null ? "An applied placement lost the fit that proposed it."
        : null;

    // The one exit to CalibrationPhase.None.
    private void ClearCalibrationSession()
    {
        calibrationIdentity = null; calibrationOriginal = calibrationApplied = null; calibrationFit = null; calibrationReevaluation = null;
        calibrationSamples.Clear(); calibrationPlacementUnverified = calibrationArchiveConfirmation = false;
    }

    private void LoadCalibrationPreferences()
    {
        if (calibrationProfile == ActiveProfile.Id) return;
        calibrationProfile = ActiveProfile.Id;
        ClearCalibrationSession();
        calibrationDesk = preferences.BenchReference?.Apply(new()) ?? new(); calibrationMeasurement = new();
        string path = Path.Combine(RobotCalibrationDirectory, "settings.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<CalibrationPreferences>(File.ReadAllText(path), PluginProtocol.Json) is { } saved)
            {
                if (saved.Desk is null || saved.Measurement is null) throw new InvalidOperationException("Saved desk/point settings are incomplete.");
                saved.Desk.Validate(); _ = saved.Measurement.World(saved.Desk); calibrationDesk = saved.Desk; calibrationMeasurement = saved.Measurement;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        { Trace.TraceWarning("Calibration preferences: {0}", error.Message); }
        calibrationPath = Directory.Exists(RobotCalibrationDirectory) ? Directory.EnumerateFiles(RobotCalibrationDirectory, "*-calibration.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "" : "";
        try
        {
            // Only a report with a sent or applied scene write resumes here; an
            // unapplied report resumes only through its wizard draft.
            if (calibrationPath.Length > 0 && new FileInfo(calibrationPath).Length <= 1024 * 1024 &&
                JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(calibrationPath), PluginProtocol.Json) is { RequiresInspection: true } report)
                RestoreCalibrationReport(report, calibrationPath);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { Trace.TraceWarning("Saved calibration could not be restored: {0}", error.Message); }
    }

    private void RestoreCalibrationReport(CalibrationReport report, string path)
    {
        report.Validate(ActiveProfile.Id);
        calibrationIdentity = report.Identity; calibrationOriginal = report.OriginalPlacement; calibrationApplied = report.AppliedPlacement; calibrationFit = report.Fit;
        calibrationReevaluation = report.ModelReevaluation;
        calibrationSamples.Clear(); calibrationSamples.AddRange(report.Samples); calibrationDesk = report.Desk; calibrationPath = path;
        calibrationMeasurement = report.Samples.LastOrDefault()?.Measurement ?? calibrationMeasurement;
        calibrationPlacementUnverified = report.RequiresInspection;
    }

    private CalibrationIdentity CurrentCalibrationIdentity()
    {
        var current = link ?? throw new InvalidOperationException("Link the scene robot first; linking reads measurements without moving hardware.");
        return new(ActiveProfile.Id, current.DeviceId, current.Generation, CalibrationCapture.Fingerprint(token), current.SceneId, current.EntityId,
            calibrationIdentity?.ModelAssetId ?? "", calibrationIdentity?.ModelRevision ?? "");
    }

    private async Task<JsonObject> ReadRobotPose(string sceneId, string entityId, CancellationToken cancellation)
    {
        var reply = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = sceneId, ["entityId"] = entityId }, cancellation);
        return JsonNode.Parse(reply.GetRawText())!.AsObject();
    }

    // Reads the linked robot's pose and requires that the link did not change
    // during the read and that the reply is a metric pose for its scene entity.
    private async Task<(RobotLink Link, JsonObject Pose)> ReadMetricPose(string unlinked, string unavailable, CancellationToken cancellation)
    {
        var current = link ?? throw new InvalidOperationException(unlinked);
        var pose = await ReadRobotPose(current.SceneId, current.EntityId, cancellation);
        if (!ReferenceEquals(link, current) || !CalibrationPose.IsMetric(pose, current.SceneId, current.EntityId))
            throw new InvalidOperationException(unavailable);
        return (current, pose);
    }

    private async Task<CalibrationCapture> ReadCalibrationPose(CalibrationIdentity identity, CalibrationPlacement? expectedPlacement, bool handGuiding, CancellationToken cancellation)
    {
        var before = (JsonObject)Device().DeepClone();
        if (CurrentCalibrationIdentity() != identity) throw new InvalidOperationException("Calibration connection changed. Start a new calibration.");
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pose = await ReadRobotPose(identity.SceneId, identity.RobotId, cancellation);
        var after = Device();
        if (before["joints"]!.AsArray().Zip(after["joints"]!.AsArray()).Any(p => Math.Abs(p.First!["actual_deg"]!.GetValue<double>() - p.Second!["actual_deg"]!.GetValue<double>()) > .05))
            throw new InvalidOperationException("The arm moved during scene measurement. Hold the same point still and capture again.");
        var captured = CalibrationCapture.Read(identity, CurrentCalibrationIdentity(), calibrationDesk, expectedPlacement, after, pose, start,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), handGuiding);
        EnsureIndependentCalibration(pose);
        return captured;
    }

    private void RequireCalibrationIdle()
    {
        if (state?["motion"]?["active"]?.GetValue<bool>() == true || followRequest != 0)
            throw new InvalidOperationException("Finish or stop the current movement before calibrating. This wizard never sends a stop or motion command for you.");
    }
    private async Task MakeCalibrationReadOnly(CancellationToken cancellation)
    {
        RequireCalibrationIdle();
        approved = linkCommandsEnabled = false;
        teachingSession.WithdrawApproval();
        PauseSceneFollowing();
        if (link is { } current) await engine.RequestAsync("robot.teleoperate", new JsonObject { ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId, ["enabled"] = false }, cancellation);
    }

    private async Task CalibrationEvent(string action, CancellationToken cancellation)
    {
        EnsureIndependentCalibration();
        LoadCalibrationPreferences();
        if (!calibrationOpen) throw new InvalidOperationException("Open Calibrate robot to review its measurements and scene-only actions first.");
        if (action == "open")
        {
            if (!File.Exists(calibrationPath)) throw new InvalidOperationException("Start and save a calibration report first.");
            Process.Start(new ProcessStartInfo(calibrationPath) { UseShellExecute = true }); return;
        }
        if (action == "archive.cancel") { calibrationArchiveConfirmation = false; await Render(cancellation); return; }
        if (action is "new" or "archive")
        {
            if (CalibrationSessionFault is { } fault)
            {
                ClearCalibrationSession();
                await Render(cancellation);
                throw new InvalidOperationException(fault + " The inconsistent session was discarded without saving; earlier saved reports are unchanged.");
            }
            if (action == "new" && CalibrationSessionPhase is CalibrationPhase.WritePending or CalibrationPhase.Applied)
            { calibrationArchiveConfirmation = true; await Render(cancellation); return; }
            if (action == "archive" && !calibrationArchiveConfirmation) throw new InvalidOperationException("Review the saved-placement archive warning first.");
            await SaveCalibration(cancellation);
            ClearCalibrationSession();
            await Render(cancellation); return;
        }
        RequireCalibrationIdle();
        if (action == "start")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("Use New / edit settings to preserve the old report and start a different calibration.");
            var identity = CurrentCalibrationIdentity();
            var capture = await ReadCalibrationPose(identity, null, true, cancellation);
            await MakeCalibrationReadOnly(cancellation);
            calibrationIdentity = identity with { ModelAssetId = capture.ModelAssetId, ModelRevision = capture.ModelRevision }; calibrationOriginal = capture.Placement; calibrationApplied = null; calibrationFit = null; calibrationSamples.Clear(); calibrationPlacementUnverified = false;
            Directory.CreateDirectory(RobotCalibrationDirectory);
            calibrationPath = Path.Combine(RobotCalibrationDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + "-calibration.json");
            await SaveCalibration(cancellation); await Render(cancellation); return;
        }
        var session = calibrationIdentity ?? throw new InvalidOperationException("Start a calibration while stationary in physical hand-guiding mode first.");
        if (action == "inspect")
        {
            var pose = await ReadRobotPose(session.SceneId, session.RobotId, cancellation);
            if (ActiveProfile.Id != session.ProfileId || preferences.RobotId != session.RobotId || pose["sceneId"]?.GetValue<string>() != session.SceneId || pose["entityId"]?.GetValue<string>() != session.RobotId ||
                pose["modelAssetId"]?.GetValue<string>() != session.ModelAssetId || pose["modelRevision"]?.GetValue<string>() != session.ModelRevision)
                throw new InvalidOperationException("The selected robot changed while checking placement; no scene write was sent.");
            var current = CalibrationPlacement.Read(pose["rootPlacement"]);
            if (calibrationOriginal!.Same(current)) calibrationApplied = null;
            else if (calibrationFit?.ProposedPlacement is { } proposal && proposal.Same(current)) calibrationApplied = proposal;
            else throw new InvalidOperationException("The root has a third, unexpected placement. Inspect/undo the scene edit; this wizard will not overwrite an unrelated change.");
            calibrationPlacementUnverified = false;
            await SaveCalibration(cancellation); await Render(cancellation); return;
        }
        if (session != CurrentCalibrationIdentity()) throw new InvalidOperationException("The robot/profile/backend/scene changed. Old fit writes remain blocked; inspect/archive the saved report and start a new calibration.");
        if (calibrationPlacementUnverified) throw new InvalidOperationException("Check scene placement first. A previous scene write has no verified reply; it will not be repeated automatically.");
        if (action is "capture" or "validate")
        {
            if (calibrationApplied is not null) throw new InvalidOperationException("This fit was already applied. Roll back or start a new calibration before capturing more points.");
            if (calibrationSamples.Count >= 24) throw new InvalidOperationException("Use at most 24 points per calibration. Start a new check for more points.");
            var measured = calibrationMeasurement.World(calibrationDesk);
            if (calibrationSamples.Any(s => s.MeasuredWorld.Distance(measured) < CalibrationSolver.ReferenceDuplicateDistanceMetres))
                throw new InvalidOperationException("That desk location was already sampled (within 1 mm). Choose a distinct physical mark.");
            var capture = await ReadCalibrationPose(session, calibrationOriginal, true, cancellation);
            bool heldOut = action == "validate";
            if (heldOut && calibrationSamples.Count(s => !s.HeldOut) < 4) throw new InvalidOperationException("Capture at least four training points before the independent validation point.");
            calibrationSamples.Add(new((heldOut ? "Validation " : "Training ") + (calibrationSamples.Count + 1), measured, capture.ToolWorld, capture.JointDegrees,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), heldOut, calibrationMeasurement, capture.PoseFingerprint));
            calibrationFit = null;
            await SaveCalibration(cancellation); await Render(cancellation); return;
        }
        if (action == "fit")
        {
            calibrationFit = CalibrationSolver.Fit(calibrationDesk, calibrationOriginal!, calibrationSamples);
            await SaveCalibration(cancellation); await Render(cancellation); return;
        }
        if (action is "apply" or "rollback")
        {
            bool rollback = action == "rollback";
            var target = rollback ? calibrationOriginal! : calibrationFit is { CanApply: true } fit ? fit.ProposedPlacement
                : throw new InvalidOperationException("Apply requires a passing fit and an independent validation point. Review the residuals first.");
            var expected = rollback ? calibrationApplied ?? throw new InvalidOperationException("There is no applied placement from this session to roll back.") : calibrationOriginal!;
            await ReadCalibrationPose(session, expected, !rollback, cancellation);
            await MakeCalibrationReadOnly(cancellation);
            CalibrationCapture.ValidateLive(session, CurrentCalibrationIdentity(), Device(session.DeviceId, session.Generation), !rollback);
            calibrationPlacementUnverified = true;
            await SaveCalibration(cancellation);
            try { CalibrationCapture.ValidateLive(session, CurrentCalibrationIdentity(), Device(session.DeviceId, session.Generation), !rollback); }
            catch
            {
                calibrationPlacementUnverified = false;
                await SaveCalibration(cancellation); throw;
            }
            CalibrationPlacement returnedPlacement;
            try
            {
                var response = await engine.RequestAsync("robot.placement", new JsonObject { ["sceneId"] = session.SceneId, ["entityId"] = session.RobotId,
                    ["expectedRootPlacement"] = expected.Json(), ["expectedModelAssetId"] = session.ModelAssetId, ["expectedModelRevision"] = session.ModelRevision,
                    ["position"] = target.Position.Json(), ["yawRadians"] = target.YawRadians }, cancellation);
                var result = JsonNode.Parse(response.GetRawText())!.AsObject();
                var returnedPose = result["pose"];
                returnedPlacement = CalibrationPlacement.Read(returnedPose?["rootPlacement"]);
                if (result["applied"]?.GetValue<bool>() != true || returnedPose?["sceneId"]?.GetValue<string>() != session.SceneId ||
                    returnedPose?["entityId"]?.GetValue<string>() != session.RobotId || returnedPose?["modelAssetId"]?.GetValue<string>() != session.ModelAssetId ||
                    returnedPose?["modelRevision"]?.GetValue<string>() != session.ModelRevision ||
                    session != CurrentCalibrationIdentity() || !returnedPlacement.Same(target))
                    throw new InvalidOperationException("The scene did not verify the requested placement. Use Check scene placement; no physical robot command was sent.");
            }
            catch { await Render(cancellation); throw; }
            calibrationApplied = rollback ? null : returnedPlacement;
            calibrationPlacementUnverified = false;
            await SaveCalibration(cancellation); await Render(cancellation);
        }
    }

    internal async Task SaveCalibration(CancellationToken cancellation)
    {
        // An inconsistent session is never persisted as a calibration report.
        if (CalibrationSessionFault is { } fault)
            throw new InvalidOperationException(fault + " Nothing was saved. Use New calibration to discard this session.");
        Directory.CreateDirectory(RobotCalibrationDirectory);
        await File.WriteAllTextAsync(Path.Combine(RobotCalibrationDirectory, "settings.json"), JsonSerializer.Serialize(new CalibrationPreferences(calibrationDesk, calibrationMeasurement), PluginProtocol.Json), cancellation);
        if (calibrationIdentity is null || calibrationOriginal is null || calibrationPath.Length == 0) return;
        var report = new CalibrationReport(1, calibrationIdentity, calibrationDesk, calibrationOriginal, calibrationSamples.ToArray(), calibrationFit, calibrationApplied, calibrationPlacementUnverified, DateTimeOffset.UtcNow,
            ModelReevaluation: calibrationReevaluation);
        string temporary = calibrationPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, PluginProtocol.Json), cancellation);
        File.Move(temporary, calibrationPath, true);
    }
}
