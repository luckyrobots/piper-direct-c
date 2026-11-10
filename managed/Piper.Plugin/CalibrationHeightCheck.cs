using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // Jaw-height check (Cal-D); heightFrozen is the pinned pose.
    //   Inactive -> Live | Pinned | Saved   Check jaw height (a restored draft pose resumes)
    //   Live -> Pinned                      Measure this pose, or a first tape entry
    //   Pinned|Saved -> Pinned              Measure this pose again
    //   Pinned -> Saved                     Save check
    //   Pinned|Saved -> Live                Check another pose
    //   any -> Inactive                     Stop height check, or a profile change
    // Live has no pinned pose; Pinned and Saved have one. Inactive may keep the
    // saved draft pose so the next Check jaw height resumes it.
    internal enum HeightCheckPhase { Inactive, Live, Pinned, Saved }
    internal HeightCheckPhase heightPhase;
    // Kept under its old field name so the B3 workspace-page lines stay untouched.
    private bool heightCheckActive => heightPhase != HeightCheckPhase.Inactive;
    private void SetHeightPhase(HeightCheckPhase next)
    {
        Debug.Assert(next switch
        {
            HeightCheckPhase.Live => heightFrozen is null,
            HeightCheckPhase.Pinned => heightFrozen is not null && !FrozenPoseInChecks,
            HeightCheckPhase.Saved => heightFrozen is not null && FrozenPoseInChecks,
            _ => true
        }, "Jaw-height phase disagrees with its pinned pose.");
        heightPhase = next;
    }
    private bool heightGuidePublished, heightStorageBlocked;
    private string heightProfile = "", heightMeasuredText = "", heightStatus = "Move to a few different heights and compare them with a tape measure.";
    internal string heightIssue = "", heightGuideIssue = "", heightActionError = "";
    private sealed record LiveHeightPreview(HeightObservation Observation, long ReadAtTicks, double SourceAgeMilliseconds);
    private LiveHeightPreview? heightLive;
    internal HeightObservation? heightFrozen;
    private long heightGuideAt, heightEpoch;
    private HeightCheck[] heightChecks = [];
    private Task? heightMaintenance;
    // For DisposeAsync (B12): completes when no height-line request is in flight. Never faults.
    private Task HeightMaintenanceIdle => heightMaintenance ?? Task.CompletedTask;
    private string HeightCheckPath => Path.Combine(RobotCalibrationDirectory, "height-checks.json");
    private bool HeightFrozenSaved => heightPhase == HeightCheckPhase.Saved || heightPhase == HeightCheckPhase.Inactive && FrozenPoseInChecks;
    private bool FrozenPoseInChecks => heightFrozen is { } frozen && heightChecks.Any(c => c.Observation.CapturedAt == frozen.CapturedAt && c.Observation.Capture.PoseFingerprint == frozen.Capture.PoseFingerprint);

    private void LoadHeightChecks()
    {
        if (heightProfile == ActiveProfile.Id) return;
        heightProfile = ActiveProfile.Id; heightChecks = []; heightLive = null; heightFrozen = null; heightMeasuredText = ""; heightStorageBlocked = false; SetHeightPhase(HeightCheckPhase.Inactive);
        heightIssue = heightGuideIssue = heightActionError = "";
        heightStatus = "Move to a few different heights and compare them with a tape measure.";
        try
        {
            if (!File.Exists(HeightCheckPath)) return;
            if (new FileInfo(HeightCheckPath).Length > 2 * 1024 * 1024) throw new InvalidOperationException("Saved height checks exceed the supported size.");
            var saved = JsonSerializer.Deserialize<HeightCheckDocument>(File.ReadAllText(HeightCheckPath), PluginProtocol.Json)
                ?? throw new InvalidOperationException("Saved height checks are incomplete.");
            HeightValidation.ValidateDocument(saved, ActiveProfile.Id);
            heightChecks = saved.Checks;
            heightFrozen = saved.Draft?.Observation; heightMeasuredText = saved.Draft?.MeasuredText ?? "";
            heightStatus = heightFrozen is null ? "Saved height checks restored. Click Check jaw height for live readings."
                : "A previous measurement draft was retained. Click Check another pose to take a new measurement in this session.";
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
        { heightStorageBlocked = true; heightStatus = "Saved height checks could not be read; the file is retained and will not be overwritten. " + error.Message; }
    }

    private async Task SaveHeightChecks(CancellationToken cancellation)
    {
        if (heightStorageBlocked) throw new InvalidOperationException("The saved height-check file needs review before new measurements can overwrite it. The original file is retained.");
        var document = new HeightCheckDocument(1, ActiveProfile.Id, heightChecks, new(heightFrozen, heightMeasuredText));
        HeightValidation.ValidateDocument(document, ActiveProfile.Id);
        Directory.CreateDirectory(RobotCalibrationDirectory);
        await File.WriteAllTextAsync(HeightCheckPath + ".tmp", JsonSerializer.Serialize(document, PluginProtocol.Json), cancellation);
        File.Move(HeightCheckPath + ".tmp", HeightCheckPath, true);
    }

    internal CalibrationIdentity HeightContext()
    {
        if (!calibrationOpen) throw new InvalidOperationException("Open Calibration to check the jaw height.");
        if (calibrationIdentity is not { } saved || calibrationApplied is null || calibrationDesk.ToolPoint != "calibration_jaw_tips")
            throw new InvalidOperationException("Apply a desk alignment with the confirmed jaw-tip midpoint first.");
        var current = CurrentCalibrationIdentity();
        if (saved.ProfileId != current.ProfileId || saved.DeviceId != current.DeviceId || saved.RobotId != current.RobotId ||
            saved.ModelAssetId != current.ModelAssetId || saved.ModelRevision != current.ModelRevision)
            throw new InvalidOperationException("Select the calibrated robot and its matching scene model first.");
        _ = Device(current.DeviceId, current.Generation);
        return current;
    }

    private bool HeightPreviewFresh
    {
        get
        {
            if (!heightCheckActive || !calibrationOpen || heightLive is not { } live ||
                !HeightValidation.FreshAge(live.SourceAgeMilliseconds, Stopwatch.GetElapsedTime(live.ReadAtTicks).TotalMilliseconds) || !TelemetryIsFresh()) return false;
            try { return HeightContext() == live.Observation.Identity; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private void ObserveHeightFeedback(TelemetryReceipt receipt, JsonObject device, JsonObject acknowledgement)
    {
        if (!heightCheckActive || !calibrationOpen) return;
        try
        {
            var identity = HeightContext();
            long now = Stopwatch.GetTimestamp();
            if (!receipt.HasFreshDevice(device, now) || receipt.BackendToken != token)
                throw new InvalidOperationException("Waiting for fresh measured joint feedback.");
            var pose = acknowledgement["pose"] as JsonObject ?? throw new InvalidOperationException("Waiting for the measured scene pose.");
            double evidenceAge = receipt.DeviceEvidenceAgeMilliseconds(device, now);
            var capture = HeightValidation.ReadPreview(identity, calibrationDesk, calibrationApplied!, device, pose, evidenceAge);
            heightLive = new(new(identity, calibrationDesk, capture, DateTimeOffset.UtcNow, Path.GetFileName(calibrationPath)), now, evidenceAge);
            heightIssue = "";
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { heightLive = null; heightIssue = error.Message; }
    }

    private async Task FreezeHeightPose(CancellationToken cancellation)
    {
        if (!heightCheckActive) throw new InvalidOperationException("Click Check jaw height first.");
        RequireCalibrationIdle();
        var identity = HeightContext();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // A capture slower than the freshness window is rejected anyway.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(TelemetryReceipt.MaximumAgeMilliseconds));
        var capture = await ReadCalibrationPose(identity, calibrationApplied, handGuiding: false, deadline.Token);
        if (!heightCheckActive || identity != HeightContext()) throw new InvalidOperationException("The robot changed while measuring. Check another pose.");
        heightFrozen = new(identity, calibrationDesk, capture, DateTimeOffset.UtcNow, Path.GetFileName(calibrationPath));
        SetHeightPhase(HeightCheckPhase.Pinned);
        heightStatus = "Pose pinned. Keep it still and measure vertically from the desk to the midpoint where the closed jaw tips meet.";
        await SaveHeightChecks(cancellation);
    }

    internal async Task HeightCheckEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        EnsureIndependentCalibration();
        heightActionError = "";
        try
        {
            LoadWizard(); LoadHeightChecks();
            if (action == "toggle")
            {
                if (heightCheckActive) await StopHeightCheck(cancellation);
                else
                {
                    _ = HeightContext(); RequireCalibrationIdle(); heightLive = null;
                    SetHeightPhase(heightFrozen is null ? HeightCheckPhase.Live : FrozenPoseInChecks ? HeightCheckPhase.Saved : HeightCheckPhase.Pinned); heightIssue = heightGuideIssue = "";
                    Interlocked.Increment(ref heightEpoch);
                    if (!heightStorageBlocked) heightStatus = "Single-click the physical teach button for solid green, then move the arm by hand. Close the jaws and hold still when ready to measure.";
                }
                await Render(cancellation); return;
            }
            if (!heightCheckActive) throw new InvalidOperationException("Click Check jaw height first.");
            if (action == "pin") { heightMeasuredText = ""; await FreezeHeightPose(cancellation); }
            else if (action == "another")
            {
                heightFrozen = null; heightMeasuredText = ""; SetHeightPhase(HeightCheckPhase.Live);
                heightStatus = "Live again. Move the arm by hand, then measure another pose.";
                await SaveHeightChecks(cancellation);
            }
            else if (action == "measured")
            {
                string text = value.GetString() ?? "";
                if (text.Length > 32) throw new InvalidOperationException("Enter the height in millimetres.");
                // A first entry pins a stationary physical pose. Later telemetry can
                // never silently move the prediction beneath the entered number.
                if (text.Length > 0 && heightFrozen is null) await FreezeHeightPose(cancellation);
                heightMeasuredText = text;
                await SaveHeightChecks(cancellation);
            }
            else if (action == "save")
            {
                if (heightFrozen is not { } frozen) throw new InvalidOperationException("Hold still and Measure this pose first.");
                if (heightPhase == HeightCheckPhase.Saved) throw new InvalidOperationException("This check is already saved. Check another pose for a new measurement.");
                double measured = HeightValidation.ParseMeasurement(heightMeasuredText);
                if (!HeightValidation.SameHeldPose(frozen, HeightContext(), Device()))
                    throw new InvalidOperationException("The arm moved after this pose was pinned. Return to that pose, or click Check another pose before measuring again.");
                var confirmation = await ReadCalibrationPose(frozen.Identity, frozen.Capture.Placement, handGuiding: false, cancellation);
                if (confirmation.ToolWorld.Distance(frozen.Capture.ToolWorld) > .002)
                    throw new InvalidOperationException("The jaw moved after this pose was pinned. Check another pose and remeasure it.");
                if (heightChecks.Length >= HeightValidation.MaximumChecks) throw new InvalidOperationException("This robot already has 256 height checks. The existing file is retained.");
                var check = new HeightCheck(frozen, measured, DateTimeOffset.UtcNow);
                var previous = heightChecks;
                heightChecks = [.. previous, check];
                try { await SaveHeightChecks(cancellation); }
                catch { heightChecks = previous; throw; }
                SetHeightPhase(HeightCheckPhase.Saved);
                double errorMm = check.ErrorMm;
                double absoluteErrorMm = Math.Abs(errorMm);
                double precisionMm = check.Observation.Desk.PrecisionMm;
                string direction = errorMm >= 0 ? "higher" : "lower";
                string verdict = absoluteErrorMm <= precisionMm ? "Within" : "Outside";
                heightStatus = $"Tape {check.MeasuredMm:0.0} mm · simulation {check.Observation.PredictedMm:0.0} mm · real jaw {absoluteErrorMm:0.0} mm {direction}. " +
                    $"{verdict} ±{precisionMm:0.#} mm. Saved independently; the alignment was not changed.";
            }
            else return;
            if (action is "pin" or "another")
                await Patch(new() { ["height.measured"] = new() { ["text"] = heightMeasuredText } }, cancellation);
            await Update(cancellation);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            heightActionError = error is OperationCanceledException ? "The scene reading took too long. Keep the arm still and try again." : error.Message;
            await Update(cancellation);
        }
    }

    private Widget HeightCheckControls()
    {
        LoadHeightChecks();
        return Widget.Column("height.section",
            Widget.Row("height.header", Widget.Label("height.title", "Check jaw height") with { Appearance = "heading", Width = 200 },
                Widget.Button("height.toggle", heightCheckActive ? "Stop height check" : "Check jaw height") with { Width = 160 },
                new Widget("button", "height.info") { Text = "(i)", Width = 28, Hint = "Independent validation only. Height is measured vertically above the saved tabletop, from the exact calibration_jaw_tips model point. It does not fit the base or change physical joint zeros, targets or calibration." }),
            Widget.Label("height.status", heightStatus) with { Height = 65 },
            Widget.Column("height.body",
                Widget.Label("height.guide", "For free movement, single-click the arm's physical teach button until solid green. Close the jaws. Move by hand, then hold still and measure vertically from the desk to where the jaw tips meet.") with { Height = 64 },
                Widget.Row("height.live.row", Widget.Label("height.live.label", "Live jaw height") with { Width = 155 },
                    Widget.Label("height.live.value", "—") with { Width = 145, Height = 32, Appearance = "numeric" },
                    Widget.Button("height.pin", "Measure this pose") with { Width = 160 }),
                Widget.Label("height.frozen", "") with { Height = 30 },
                Widget.Row("height.measure.row", Widget.Label("height.measure.label", "Your tape reading (mm)") with { Width = 155 },
                    new Widget("input", "height.measured") { Text = heightMeasuredText, Width = 110, CommitOnly = false, Hint = "Vertical distance in millimetres. Your first entry pins the current stationary pose, so the prediction cannot change underneath your measurement." },
                    Widget.Button("height.save", "Save check") with { Width = 110 },
                    Widget.Button("height.another", "Check another pose") with { Width = 160 }),
                Widget.Label("height.history", "") with { Height = 125, Appearance = "muted" }) with { Visible = heightCheckActive, Spacing = 5 }) with { Spacing = 8, Padding = 10 };
    }

    private void AddHeightCheckPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        if (!calibrationOpen) return;
        bool fresh = HeightPreviewFresh;
        var live = fresh ? heightLive?.Observation : null;
        bool stationary = device?["status"]?["stationary_s"]?.GetValue<double>() is >= .4;
        patches["height.live.value"] = new() { ["text"] = live is null ? "—" : $"{live.PredictedMm:0.0} mm" };
        patches["height.pin"] = new() { ["enabled"] = heightCheckActive && fresh && stationary && !heightStorageBlocked };
        // Do not overwrite an in-progress native text edit with an older event's
        // prefix while the stationary scene read is completing.
        patches["height.measured"] = new() { ["enabled"] = heightCheckActive && !heightStorageBlocked && !HeightFrozenSaved && (heightFrozen is not null || fresh && stationary) };
        patches["height.another"] = new() { ["enabled"] = heightCheckActive && heightFrozen is not null && !heightStorageBlocked };
        string pin = heightFrozen is { } frozen ? $"Pinned pose: {frozen.PredictedMm:0.0} mm above the desk. Enter its tape reading below." : "Hold still, then Measure this pose—or enter your reading to pin it.";
        bool canSave = false;
        try
        {
            if (heightFrozen is { } saved)
            {
                bool held = device is not null && HeightValidation.SameHeldPose(saved, HeightContext(), device);
                if (!held) pin = $"Pinned: {saved.PredictedMm:0.0} mm. The arm moved or feedback changed; check another pose before measuring again.";
                _ = HeightValidation.ParseMeasurement(heightMeasuredText);
                canSave = held && !HeightFrozenSaved;
            }
        }
        catch (InvalidOperationException) { }
        if (HeightFrozenSaved) pin = "This height check is saved. Click Check another pose when ready.";
        patches["height.frozen"] = new() { ["text"] = pin };
        patches["height.save"] = new() { ["enabled"] = heightCheckActive && canSave && !heightStorageBlocked };
        string status = !heightCheckActive ? heightStatus : !fresh ? (heightIssue.Length > 0 ? heightIssue : "Waiting for fresh measured scene feedback. The line and height pause when feedback is unavailable.")
            : heightGuideIssue.Length > 0 ? heightGuideIssue : heightStatus;
        if (live is { } preview)
        {
            bool outside = Math.Abs(preview.Capture.ToolWorld.X) > preview.Desk.Width / 2 || Math.Abs(preview.Capture.ToolWorld.Z) > preview.Desk.Depth / 2;
            if (preview.PredictedMm < 0) status = "The model's jaw tip is below the tabletop plane. Check the pose and calibration before measuring.";
            else if (outside) status = "Above desk plane; beyond desk edge. Move the jaw above the tabletop for a tape-measure check.";
        }
        if (heightActionError.Length > 0) status = heightActionError;
        if (heightStorageBlocked) status = "Saved height checks could not be read. The original file is retained and will not be overwritten.";
        patches["height.status"] = new() { ["text"] = status };
        var checks = heightChecks;
        string history = checks.Length == 0 ? "No extra height checks saved yet. These checks do not change your four-point alignment." :
            $"{checks.Length} saved independent height checks\n" + string.Join("\n", checks.TakeLast(3).Reverse().Select(c =>
                $"{c.RecordedAt.ToLocalTime():HH:mm} · sim {c.Observation.PredictedMm:0.0} / tape {c.MeasuredMm:0.0} mm · error {c.ErrorMm:+0.0;-0.0;0.0} mm"));
        patches["height.history"] = new() { ["text"] = history };
    }

    private void QueueHeightMaintenance()
    {
        if (heightMaintenance is { IsCompleted: false }) return;
        heightMaintenance = MaintainHeightGuide(shutdown.Token);
    }

    private async Task MaintainHeightGuide(CancellationToken cancellation)
    {
        try
        {
            if (!HeightPreviewFresh || heightLive?.Observation is not { } live)
            {
                if (heightGuidePublished) await ClearHeightGuide(cancellation);
                return;
            }
            if (Stopwatch.GetElapsedTime(heightGuideAt).TotalMilliseconds < 300) return;
            long epoch = heightEpoch;
            heightGuideAt = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(400));
            var identity = live.Identity;
            var response = await engine.RequestAsync("robot.highlightToolSite", new JsonObject {
                ["sceneId"] = identity.SceneId, ["entityId"] = identity.RobotId, ["expectedModelAssetId"] = identity.ModelAssetId,
                ["expectedModelRevision"] = identity.ModelRevision, ["expectedRootPlacement"] = live.Capture.Placement.Json(),
                ["siteName"] = live.Desk.ToolPoint, ["heightSurfaceY"] = live.Desk.SurfaceY, ["durationSeconds"] = 1,
                ["markerId"] = "jaw-height-check", ["visible"] = true }, deadline.Token);
            if (response.GetProperty("highlighted").GetBoolean() != true || response.GetProperty("markerId").GetString() != "jaw-height-check" ||
                !response.TryGetProperty("heightSurfaceY", out var y) || Math.Abs(y.GetDouble() - live.Desk.SurfaceY) > 1e-9)
                throw new InvalidOperationException("Update Lucky Engine to show the vertical jaw-height line.");
            heightGuidePublished = true;
            heightGuideIssue = "";
            if (epoch != heightEpoch || !HeightPreviewFresh) await ClearHeightGuide(cancellation);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            heightGuideIssue = "Height line unavailable: " + error.Message;
            if (heightGuidePublished) try { await ClearHeightGuide(cancellation); } catch (Exception) { /* The one-second lease expires. */ }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task ClearHeightGuide(CancellationToken cancellation)
    {
        heightGuidePublished = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(400));
        await engine.RequestAsync("robot.highlightToolSite", new JsonObject { ["markerId"] = "jaw-height-check", ["visible"] = false }, deadline.Token);
    }

    private async Task StopHeightCheck(CancellationToken cancellation)
    {
        SetHeightPhase(HeightCheckPhase.Inactive); heightLive = null; Interlocked.Increment(ref heightEpoch);
        try { await ClearHeightGuide(cancellation); }
        catch (Exception error) when (!cancellation.IsCancellationRequested) { Trace.TraceWarning("Height line clear: {0}", error.Message); }
    }
}
