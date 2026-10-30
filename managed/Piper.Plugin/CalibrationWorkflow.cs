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
        (action is "enable" or "safety.accept" or "teleoperate.toggle" or "zero.all" or "pika.target" or "engine.robot.target" or "engine.robot.follow" ||
            action.StartsWith("joint.", StringComparison.Ordinal));
}

public sealed partial class PipersPlugin
{
    private bool calibrationOpen, calibrationTcpConfirmed, calibrationPlacementUnverified, calibrationArchiveConfirmation;
    private string calibrationProfile = "", robotCalibrationStatus = "Measure desk points to align the scene base. Joint linking alone does not establish spatial calibration.";
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
        calibrationIdentity = null; calibrationOriginal = calibrationApplied = null; calibrationFit = null; calibrationSamples.Clear(); calibrationTcpConfirmed = calibrationPlacementUnverified = calibrationArchiveConfirmation = false;
        calibrationDesk = new(); calibrationMeasurement = new();
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
        string restored = "";
        try
        {
            if (calibrationPath.Length > 0 && new FileInfo(calibrationPath).Length <= 1024 * 1024 &&
                JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(calibrationPath), PluginProtocol.Json) is { } report &&
                CalibrationSavedState.Recover(report, ActiveProfile.Id) is { } saved)
            {
                calibrationIdentity = report.Identity; calibrationOriginal = report.OriginalPlacement; calibrationApplied = report.AppliedPlacement; calibrationFit = report.Fit;
                calibrationSamples.AddRange(report.Samples); calibrationDesk = report.Desk; calibrationMeasurement = report.Samples.LastOrDefault()?.Measurement ?? calibrationMeasurement;
                calibrationPlacementUnverified = saved.RequiresInspection;
                restored = "Saved pending/applied scene placement restored, CURRENT STATE UNVERIFIED. Use Check scene placement (read-only); no write, rollback or replay resumes automatically. Changed connections keep old fit writes blocked. New / edit settings explicitly archives this rollback context.";
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { restored = "Saved calibration could not be restored. Inspect Open report before starting another check. " + error.Message; }
        calibrationMode = calibrationMeasurement.Mode;
        calibrationInputs.Clear();
        foreach (var pair in new Dictionary<string, double> { ["width"] = calibrationDesk.Width * 1000, ["depth"] = calibrationDesk.Depth * 1000,
            ["surface"] = calibrationDesk.SurfaceY * 1000, ["precision"] = calibrationDesk.PrecisionMm, ["left"] = calibrationMeasurement.LeftMm,
            ["back"] = calibrationMeasurement.BackMm, ["height"] = calibrationMeasurement.HeightMm, ["square"] = calibrationMeasurement.SquareMm,
            ["originleft"] = calibrationMeasurement.OriginLeftMm, ["originback"] = calibrationMeasurement.OriginBackMm,
            ["column"] = calibrationMeasurement.Column, ["row"] = calibrationMeasurement.Row }) calibrationInputs[pair.Key] = CalibrationNumber(pair.Value);
        calibrationInputs["tool"] = calibrationDesk.ToolPoint;
        robotCalibrationStatus = restored.Length > 0 ? restored : calibrationPath.Length == 0 ? "No metric base calibration for this robot. Start with four distinct desk points and one new validation point."
            : "A saved base-calibration report is available. Start a new check to verify the current hardware, scene and contact point.";
    }

    private Widget CalibrationInput(string id, string label, int labelWidth = 90) => Widget.Row("calibration." + id + ".row",
        Widget.Label("calibration." + id + ".label", label) with { Width = labelWidth },
        new Widget("input", "calibration." + id) { Text = calibrationInputs.GetValueOrDefault(id, ""), Hint = id == "tool" ? "Exact named model tool site; fixed throughout this check"
            : id == "height" ? "Measured contact-point height above the desk, including grid-mat thickness; millimetres"
            : id is "square" or "originleft" or "originback" or "column" or "row" ? "Secure the mat flat and aligned with desk edges (rotated grids are not supported). Verify square size with a ruler. Include mat thickness in Above desk mm. Row/column are whole intersection numbers."
            : "Millimetres" });

    private Widget CalibrationControls()
    {
        LoadCalibrationPreferences();
        if (!calibrationOpen) return Widget.Button("calibration.toggle", "Calibrate robot · desk measurements");
        bool grid = calibrationMode == "grid intersections";
        return Widget.Column("calibration",
            Widget.Row("calibration.header", Widget.Label("calibration.title", "Calibrate robot · base placement"), Widget.Button("calibration.toggle", "Back to robot controls") with { Width = 190 }),
            Widget.Label("calibration.instructions", "Use the physical teach button (solid green). Touch/hold the SAME named contact point at four measured desk locations, then one NEW validation location. Measure left-edge distance, back-edge distance and height above the desk; no object is required. Spread points in both desk directions. This wizard never moves or enables the real arm.") with { Height = 66 },
            Widget.Label("calibration.frame", "Required SCENE desk frame: centre X=0, Z=0; width along X; back edge is −Z. Surface Y is its world height. This wizard does not measure or infer the scene desk transform.") with { Height = 38 },
            Widget.Row("calibration.desk", CalibrationInput("width", "Desk width mm"), CalibrationInput("depth", "Depth mm", 70), CalibrationInput("surface", "Surface Y mm")),
            Widget.Row("calibration.reference", CalibrationInput("tool", "Named TCP", 85), CalibrationInput("precision", "Precision mm", 90),
                Widget.Button("calibration.tcp", calibrationTcpConfirmed ? "Desk frame + TCP confirmed" : "Confirm desk frame + TCP")),
            Widget.Label("calibration.tcp.help", "For grasp_site, verify on the model that it is the same midpoint between the jaw tips you measure physically. If it is not, select/fix the correct named site first. A fingertip or camera origin is not interchangeable with that point.") with { Height = 44 },
            Widget.Row("calibration.mode.row", new Widget("dropdown", "calibration.mode") { Options = ["Edge measurements", "Grid mat intersections"], Value = grid ? 1 : 0 },
                CalibrationInput("height", "Above desk mm", 100)),
            Widget.Row("calibration.edges", CalibrationInput("left", "From LEFT mm", 100), CalibrationInput("back", "From BACK mm", 100)) with { Visible = !grid },
            Widget.Row("calibration.grid.origin", CalibrationInput("originleft", "Grid origin left mm", 130), CalibrationInput("originback", "Origin back mm", 110), CalibrationInput("square", "Square mm", 80)) with { Visible = grid },
            Widget.Row("calibration.grid.point", CalibrationInput("column", "Intersection column", 130), CalibrationInput("row", "Intersection row", 110)) with { Visible = grid },
            Widget.Row("calibration.capture.actions", Widget.Button("calibration.new", "New / edit settings"), Widget.Button("calibration.start", "Start calibration"), Widget.Button("calibration.capture", "Capture training point"), Widget.Button("calibration.validate", "Capture NEW validation point")),
            Widget.Column("calibration.archive.confirmation",
                Widget.Label("calibration.archive.warning", "A saved/pending scene placement remains in the report. Starting a new check archives its rollback context; it does not undo or repeat a scene write. Inspect/save the report first.") with { Height = 44 },
                Widget.Row("calibration.archive.actions", Widget.Button("calibration.archive", "Keep report & start new check"), Widget.Button("calibration.archive.cancel", "Cancel"))) with { Visible = calibrationArchiveConfirmation },
            Widget.Label("calibration.status", robotCalibrationStatus) with { Height = 52 },
            Widget.Label("calibration.preview", CalibrationPreview()) with { Height = calibrationFit is null ? 28 : 110 },
            Widget.Row("calibration.apply.actions", Widget.Button("calibration.fit", "Fit / preview"), Widget.Button("calibration.apply", "Apply to SCENE only"),
                Widget.Button("calibration.rollback", "Roll back scene placement"), Widget.Button("calibration.inspect", "Check scene placement") with { Visible = calibrationPlacementUnverified }, Widget.Button("calibration.open", "Open report")),
            Widget.Label("calibration.limits", "Fits base yaw and desk-plane translation only. No joint zeros, tool offsets, scale or real robot targets change. Camera calibration and exposure timing remain unverified.") with { Height = 38 });
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
        LoadCalibrationPreferences();
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
            calibrationIdentity = null; calibrationOriginal = calibrationApplied = null; calibrationFit = null; calibrationSamples.Clear(); calibrationTcpConfirmed = calibrationPlacementUnverified = calibrationArchiveConfirmation = false;
            robotCalibrationStatus = "Previous report preserved. Edit desk/TCP settings if needed, then Start calibration while hand guiding.";
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
            robotCalibrationStatus = "Session started. Capture four DISTINCT training points, spread in both desk directions; then capture a NEW validation point, at least 5 cm from every training point.";
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
            robotCalibrationStatus = calibrationApplied is null ? "Scene root is at the original placement; no pending write remains." : "Proposed scene placement verified. Roll back remains available; no physical robot command was sent.";
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
            if (calibrationSamples.Any(s => s.MeasuredWorld.Distance(measured) < .02))
                throw new InvalidOperationException("That desk location was already sampled. Choose a different point at least 2 cm away.");
            var capture = await ReadCalibrationPose(session, calibrationOriginal, true, cancellation);
            bool heldOut = action == "validate";
            if (heldOut && calibrationSamples.Count(s => !s.HeldOut) < 4) throw new InvalidOperationException("Capture at least four training points before the independent validation point.");
            calibrationSamples.Add(new((heldOut ? "Validation " : "Training ") + (calibrationSamples.Count + 1), measured, capture.ToolWorld, capture.JointDegrees,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), heldOut, calibrationMeasurement, capture.PoseFingerprint));
            calibrationFit = null;
            robotCalibrationStatus = $"Saved {calibrationSamples.Count(s => !s.HeldOut)} training and {calibrationSamples.Count(s => s.HeldOut)} independent validation points. " +
                (heldOut ? "Fit / preview checks agreement before Apply is available." : "Move to the next measured point by hand, hold still, then Capture.");
            await SaveCalibration(cancellation); await Render(cancellation); return;
        }
        if (action == "fit")
        {
            calibrationFit = CalibrationSolver.Fit(calibrationDesk, calibrationOriginal!, calibrationSamples);
            robotCalibrationStatus = calibrationFit.Reason;
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
            robotCalibrationStatus = "Scene-only placement pending verification. If interrupted, use Check scene placement before any further write.";
            await SaveCalibration(cancellation);
            try { CalibrationCapture.ValidateLive(session, CurrentCalibrationIdentity(), Device(session.DeviceId, session.Generation), !rollback); }
            catch
            {
                calibrationPlacementUnverified = false;
                robotCalibrationStatus = "Calibration identity/pose changed before sending placement. No scene write was sent; restore the context or archive this report and start again.";
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
            robotCalibrationStatus = rollback ? "Scene base restored to its original placement. Hardware and measured joints were unchanged."
                : "Scene base placement applied and verified. Physical robot was not commanded. Recheck a new desk point; camera calibration and exposure timing remain separate.";
            await SaveCalibration(cancellation); await Render(cancellation);
        }
    }

    private string CalibrationPreview()
    {
        if (calibrationFit is not { } fit || calibrationOriginal is not { } original) return "No fit yet · scene base unchanged.";
        var p = fit.ProposedPlacement.Position; var old = original.Position;
        return $"Scene base ({old.X:F3}, {old.Y:F3}, {old.Z:F3}) → ({p.X:F3}, {p.Y:F3}, {p.Z:F3}) m; yaw {original.YawRadians * 180 / Math.PI:F2}° → {fit.ProposedPlacement.YawRadians * 180 / Math.PI:F2}°.\n" +
            $"Base-centre distances: left {(old.X + calibrationDesk.Width / 2) * 1000:F0} → {(p.X + calibrationDesk.Width / 2) * 1000:F0} mm; back {(old.Z + calibrationDesk.Depth / 2) * 1000:F0} → {(p.Z + calibrationDesk.Depth / 2) * 1000:F0} mm. Recheck outer mounting clearances separately.\n" +
            $"Training RMS {fit.TrainingRmsMm:F1} mm / max {fit.TrainingMaxMm:F1} mm; validation max {fit.ValidationMaxMm:F1} mm; height RMS {fit.VerticalRmsMm:F1} mm. Precision entered ±{calibrationDesk.PrecisionMm:F0} mm.\n" +
            string.Join(" · ", fit.Residuals.TakeLast(4).Select(r => $"{r.Name}: {r.TotalMm:F1} mm"));
    }
    private async Task SaveCalibration(CancellationToken cancellation)
    {
        Directory.CreateDirectory(RobotCalibrationDirectory);
        await File.WriteAllTextAsync(Path.Combine(RobotCalibrationDirectory, "settings.json"), JsonSerializer.Serialize(new CalibrationPreferences(calibrationDesk, calibrationMeasurement), PluginProtocol.Json), cancellation);
        if (calibrationIdentity is null || calibrationOriginal is null || calibrationPath.Length == 0) return;
        var report = new CalibrationReport(1, calibrationIdentity, calibrationDesk, calibrationOriginal, calibrationSamples.ToArray(), calibrationFit, calibrationApplied, calibrationPlacementUnverified, DateTimeOffset.UtcNow);
        string temporary = calibrationPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, PluginProtocol.Json), cancellation);
        File.Move(temporary, calibrationPath, true);
    }
    private void AddCalibrationPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        if (!calibrationOpen) return;
        patches["enable"] = new() { ["enabled"] = false };
        patches["calibration.status"] = new() { ["text"] = robotCalibrationStatus };
        bool handGuiding = device is not null && TeachingState.Inspect(device).HandGuiding && device["status"]?["stationary_s"]?.GetValue<double>() is >= .4;
        bool same = calibrationIdentity is not null && link is not null && calibrationIdentity == CurrentCalibrationIdentity();
        patches["calibration.start"] = new() { ["enabled"] = calibrationIdentity is null && link is not null && handGuiding && calibrationTcpConfirmed };
        foreach (string action in new[] { "capture", "validate" }) patches["calibration." + action] = new() { ["enabled"] = same && handGuiding && calibrationApplied is null };
        patches["calibration.fit"] = new() { ["enabled"] = same && calibrationSamples.Count(s => !s.HeldOut) >= 4 };
        patches["calibration.apply"] = new() { ["enabled"] = same && handGuiding && !calibrationPlacementUnverified && calibrationApplied is null && calibrationFit?.CanApply == true };
        patches["calibration.rollback"] = new() { ["enabled"] = same && !calibrationPlacementUnverified && calibrationApplied is not null };
        patches["calibration.open"] = new() { ["enabled"] = File.Exists(calibrationPath) };
        foreach (string field in new[] { "width", "depth", "surface", "precision", "tool", "tcp" })
            patches["calibration." + field] = new() { ["enabled"] = calibrationIdentity is null };
    }
}
