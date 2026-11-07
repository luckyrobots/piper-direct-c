using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed record CalibrationPreferences(CalibrationDesk Desk, CalibrationMeasurement Measurement);

public static class CalibrationCommandGuard
{
    public static bool Blocks(string action, bool calibrationOpen) => calibrationOpen &&
        (action is "enable" or "safety.accept" or "teleoperate.toggle" or "zero.all" or "recovery.home" or "recovery.home.incident" or "pika.target" or "engine.robot.target" or "engine.robot.follow" ||
            action.StartsWith("joint.", StringComparison.Ordinal));
}

public sealed partial class PipersPlugin
{
    private bool calibrationOpen, calibrationTcpConfirmed, calibrationPlacementUnverified, calibrationArchiveConfirmation;
    private string calibrationProfile = "";
    private string calibrationMode = "edge measurements", calibrationPath = "";
    private CalibrationDesk calibrationDesk = new();
    private CalibrationMeasurement calibrationMeasurement = new();
    private CalibrationIdentity? calibrationIdentity;
    private CalibrationPlacement? calibrationOriginal, calibrationApplied;
    private CalibrationFit? calibrationFit;
    private readonly List<CalibrationSample> calibrationSamples = [];
    private readonly Dictionary<string, string> calibrationInputs = [];
    private bool FocusedRobotView => alignmentOpen || calibrationOpen;
    private string RobotCalibrationDirectory => Path.Combine(engine.Start.SettingsDirectory, "calibration", ActiveProfile.Id);
    private static string CalibrationNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void LoadCalibrationPreferences()
    {
        if (calibrationProfile == ActiveProfile.Id) return;
        calibrationProfile = ActiveProfile.Id;
        calibrationIdentity = null; calibrationOriginal = calibrationApplied = null; calibrationFit = null; calibrationReevaluation = null; calibrationSamples.Clear(); calibrationTcpConfirmed = calibrationPlacementUnverified = calibrationArchiveConfirmation = false;
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
            if (calibrationPath.Length > 0 && new FileInfo(calibrationPath).Length <= 1024 * 1024 &&
                JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(calibrationPath), PluginProtocol.Json) is { } report &&
                CalibrationSavedState.Recover(report, ActiveProfile.Id) is { } saved)
            {
                calibrationIdentity = report.Identity; calibrationOriginal = report.OriginalPlacement; calibrationApplied = report.AppliedPlacement; calibrationFit = report.Fit;
                calibrationReevaluation = report.ModelReevaluation;
                calibrationSamples.AddRange(report.Samples); calibrationDesk = report.Desk; calibrationMeasurement = report.Samples.LastOrDefault()?.Measurement ?? calibrationMeasurement;
                calibrationPlacementUnverified = saved.RequiresInspection;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { Trace.TraceWarning("Saved calibration could not be restored: {0}", error.Message); }
        calibrationMode = calibrationMeasurement.Mode;
        calibrationInputs.Clear();
        foreach (var pair in new Dictionary<string, double> { ["width"] = calibrationDesk.Width * 1000, ["depth"] = calibrationDesk.Depth * 1000,
            ["surface"] = calibrationDesk.SurfaceY * 1000, ["precision"] = calibrationDesk.PrecisionMm, ["left"] = calibrationMeasurement.LeftMm,
            ["back"] = calibrationMeasurement.BackMm, ["height"] = calibrationMeasurement.HeightMm, ["square"] = calibrationMeasurement.SquareMm,
            ["originleft"] = calibrationMeasurement.OriginLeftMm, ["originback"] = calibrationMeasurement.OriginBackMm,
            ["column"] = calibrationMeasurement.Column, ["row"] = calibrationMeasurement.Row }) calibrationInputs[pair.Key] = CalibrationNumber(pair.Value);
        calibrationInputs["tool"] = calibrationDesk.ToolPoint;
    }

    private double CalibrationValue(string key)
    {
        if (!double.TryParse(calibrationInputs[key], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
            throw new InvalidOperationException("Enter a finite number for " + key + ". Use millimetres and a decimal point.");
        return value;
    }
    private void ReadCalibrationInputs(bool readDesk)
    {
        if (readDesk) calibrationDesk = new(CalibrationValue("width") / 1000, CalibrationValue("depth") / 1000, CalibrationValue("surface") / 1000,
            CalibrationValue("precision"), calibrationInputs["tool"].Trim());
        calibrationDesk.Validate();
        double column = CalibrationValue("column"), row = CalibrationValue("row");
        if (column != Math.Truncate(column) || row != Math.Truncate(row)) throw new InvalidOperationException("Grid intersection row/column must be whole numbers.");
        calibrationMeasurement = new(calibrationMode, CalibrationValue("left"), CalibrationValue("back"), CalibrationValue("height"), CalibrationValue("square"),
            CalibrationValue("originleft"), CalibrationValue("originback"), checked((int)column), checked((int)row));
        _ = calibrationMeasurement.World(calibrationDesk);
    }
    private CalibrationIdentity CurrentCalibrationIdentity()
    {
        var current = link ?? throw new InvalidOperationException("Link the scene robot first; linking reads measurements without moving hardware.");
        return new(ActiveProfile.Id, current.DeviceId, current.Generation, CalibrationCapture.Fingerprint(token), current.SceneId, current.EntityId,
            calibrationIdentity?.ModelAssetId ?? "", calibrationIdentity?.ModelRevision ?? "");
    }

    private async Task<CalibrationCapture> ReadCalibrationPose(CalibrationIdentity identity, CalibrationPlacement? expectedPlacement, bool handGuiding, CancellationToken cancellation)
    {
        var before = (JsonObject)Device().DeepClone();
        if (CurrentCalibrationIdentity() != identity) throw new InvalidOperationException("Calibration connection changed. Start a new calibration.");
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var reply = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = identity.SceneId, ["entityId"] = identity.RobotId }, cancellation);
        var pose = JsonNode.Parse(reply.GetRawText())!.AsObject();
        var after = Device();
        if (before["joints"]!.AsArray().Zip(after["joints"]!.AsArray()).Any(p => Math.Abs(p.First!["actual_deg"]!.GetValue<double>() - p.Second!["actual_deg"]!.GetValue<double>()) > .05))
            throw new InvalidOperationException("The arm moved during scene measurement. Hold the same point still and capture again.");
        return CalibrationCapture.Read(identity, CurrentCalibrationIdentity(), calibrationDesk, expectedPlacement, after, pose, start,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), handGuiding);
    }

    private void RequireCalibrationIdle()
    {
        if (state?["motion"]?["active"]?.GetValue<bool>() == true || followRequest != 0)
            throw new InvalidOperationException("Finish or stop the current movement before calibrating. This wizard never sends a stop or motion command for you.");
    }
    private async Task MakeCalibrationReadOnly(CancellationToken cancellation)
    {
        RequireCalibrationIdle();
        approved = linkCommandsEnabled = approvalBeforeTeaching = false;
        PauseSceneFollowing();
        if (link is { } current) await engine.RequestAsync("robot.teleoperate", new JsonObject { ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId, ["enabled"] = false }, cancellation);
    }

    private async Task CalibrationEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        EnsureIndependentCalibration();
        LoadCalibrationPreferences();
        if (!calibrationOpen && action != "toggle") throw new InvalidOperationException("Open calibration before editing its draft or invoking an action.");
        if (calibrationInputs.ContainsKey(action))
        {
            if (calibrationIdentity is not null && action is "width" or "depth" or "surface" or "precision" or "tool")
                throw new InvalidOperationException("Start a new calibration before changing its desk, precision or fixed tool point.");
            calibrationInputs[action] = value.GetString() ?? "";
            if (action is "width" or "depth" or "surface" or "precision" or "tool") calibrationTcpConfirmed = false;
            return;
        }
        if (action == "toggle")
        {
            if (!calibrationOpen) { await MakeCalibrationReadOnly(cancellation); alignmentOpen = false; }
            calibrationOpen = !calibrationOpen; await Render(cancellation); return;
        }
        if (!calibrationOpen) throw new InvalidOperationException("Open Calibrate robot to review its measurements and scene-only actions first.");
        if (action == "mode")
        {
            calibrationMode = value.GetInt32() == 0 ? "edge measurements" : "grid intersections";
            await Render(cancellation); return;
        }
        if (action == "tcp")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("The TCP is fixed for the active check. Start a new calibration to redefine it.");
            calibrationTcpConfirmed = !calibrationTcpConfirmed; await Render(cancellation); return;
        }
        if (action == "open")
        {
            if (!File.Exists(calibrationPath)) throw new InvalidOperationException("Start and save a calibration report first.");
            Process.Start(new ProcessStartInfo(calibrationPath) { UseShellExecute = true }); return;
        }
        if (action == "archive.cancel") { calibrationArchiveConfirmation = false; await Render(cancellation); return; }
        if (action is "new" or "archive")
        {
            if (action == "new" && (calibrationPlacementUnverified || calibrationApplied is not null))
            { calibrationArchiveConfirmation = true; await Render(cancellation); return; }
            if (action == "archive" && !calibrationArchiveConfirmation) throw new InvalidOperationException("Review the saved-placement archive warning first.");
            await SaveCalibration(cancellation);
            calibrationIdentity = null; calibrationOriginal = calibrationApplied = null; calibrationFit = null; calibrationReevaluation = null; calibrationSamples.Clear(); calibrationTcpConfirmed = calibrationPlacementUnverified = calibrationArchiveConfirmation = false;
            await Render(cancellation); return;
        }
        RequireCalibrationIdle();
        if (action == "start")
        {
            if (calibrationIdentity is not null) throw new InvalidOperationException("Use New / edit settings to preserve the old report and start a different calibration.");
            if (!calibrationTcpConfirmed) throw new InvalidOperationException("Confirm the scene desk frame (centre X/Z=0, back edge −Z) and that the named model TCP is your exact physical contact/measurement point.");
            // Start also replaces an old session, so parse all draft settings now.
            ReadCalibrationInputs(true);
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
            var response = await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = session.SceneId, ["entityId"] = session.RobotId }, cancellation);
            var pose = JsonNode.Parse(response.GetRawText())!.AsObject();
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
            ReadCalibrationInputs(false);
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

    private async Task SaveCalibration(CancellationToken cancellation)
    {
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
