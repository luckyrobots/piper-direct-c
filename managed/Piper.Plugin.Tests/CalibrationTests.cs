using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

public static class CalibrationTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string why)
        {
            try { action(); } catch (InvalidOperationException) { check(true, why); return; }
            check(false, why);
        }
        var desk = new CalibrationDesk();
        var original = CalibrationPlacement.At(new(.1, .75, -.2), .15);
        double yaw = .32, tx = -.02, tz = .03;
        CalibrationPoint Transform(CalibrationPoint p)
        {
            var rotated = CalibrationSolver.Rotate(p, yaw); return new(rotated.X + tx, rotated.Y, rotated.Z + tz);
        }
        CalibrationSample Sample(CalibrationPoint p, int i, bool heldOut = false) => new("Point " + i, Transform(p), p, [0, 0, 0, 0, 0, 0], 1000 + i, heldOut, new(), "fingerprint-" + i);
        var samples = new[] { Sample(new(-.14, .75, -.12), 1), Sample(new(.14, .75, -.12), 2), Sample(new(.14, .75, .12), 3), Sample(new(-.14, .75, .12), 4), Sample(new(0, .75, 0), 5, true) };
        var fit = CalibrationSolver.Fit(desk, original, samples);
        var targetRoot = Transform(original.Position);
        check(fit.CanApply, "Exact rigid desk fit did not pass independent validation");
        check(Math.Abs(fit.YawDeltaRadians - yaw) < 1e-10 && fit.ProposedPlacement.Position.Distance(targetRoot) < 1e-10, "Yaw/translation solver produced the wrong world-Y placement");
        check(fit.ProposedPlacement.Position.Y == desk.SurfaceY && Math.Abs(fit.ProposedPlacement.YawRadians - original.YawRadians - yaw) < 1e-10, "Fit changed desk height or discarded existing root yaw");
        check(fit.TrainingRmsMm < 1e-8 && fit.ValidationMaxMm < 1e-8 && fit.VerticalRmsMm < 1e-8, "Exact data acquired spurious residuals");
        var noisy = samples.Select((s, i) => s with { MeasuredWorld = new(s.MeasuredWorld.X + (i % 2 == 0 ? .001 : -.001), s.MeasuredWorld.Y + .001, s.MeasuredWorld.Z + (i % 2 == 0 ? -.001 : .001)) }).ToArray();
        check(CalibrationSolver.Fit(desk, original, noisy).CanApply, "Sub-millimetre-scale measurement noise was rejected at 10mm operator precision");
        check(!CalibrationSolver.Fit(desk, original, samples.Take(3).ToArray()).CanApply, "Three training points were treated as a complete check");
        var noHeldOut = CalibrationSolver.Fit(desk, original, samples.Take(4).ToArray());
        check(!noHeldOut.CanApply && noHeldOut.ProposedPlacement.Same(fit.ProposedPlacement), "A missing validation point changed the fitted placement or enabled Apply");
        var badValidation = samples.ToArray(); badValidation[4] = badValidation[4] with { MeasuredWorld = new(badValidation[4].MeasuredWorld.X + .04, .75, badValidation[4].MeasuredWorld.Z) };
        var invalid = CalibrationSolver.Fit(desk, original, badValidation);
        check(!invalid.CanApply && invalid.Reason.Contains("validation", StringComparison.OrdinalIgnoreCase), "A 4cm held-out failure enabled Apply");
        check(invalid.ProposedPlacement.Same(fit.ProposedPlacement), "Validation data leaked into the training fit");
        var reused = samples.ToArray(); reused[4] = reused[4] with { MeasuredWorld = samples[0].MeasuredWorld, SceneWorld = samples[0].SceneWorld };
        check(!CalibrationSolver.Fit(desk, original, reused).CanApply, "A training point was accepted as independent validation");
        var duplicate = samples.ToArray(); duplicate[3] = duplicate[0];
        check(!CalibrationSolver.Fit(desk, original, duplicate).CanApply, "Duplicate training points inflated geometric evidence");
        var line = Enumerable.Range(0, 5).Select(i => Sample(new(i * .07, .75, 0), i, i == 4)).ToArray();
        check(!CalibrationSolver.Fit(desk, original, line).CanApply, "Collinear desk points yielded an actionable yaw fit");
        var tiny = samples.Select(s => s with { SceneWorld = new(s.SceneWorld.X / 10, .75, s.SceneWorld.Z / 10), MeasuredWorld = Transform(new(s.SceneWorld.X / 10, .75, s.SceneWorld.Z / 10)) }).ToArray();
        check(!CalibrationSolver.Fit(desk, original, tiny).CanApply, "Closely clustered points passed the 10cm geometry guard");
        var outlier = samples.ToArray(); outlier[0] = outlier[0] with { MeasuredWorld = new(outlier[0].MeasuredWorld.X + .1, .75, outlier[0].MeasuredWorld.Z) };
        check(!CalibrationSolver.Fit(desk, original, outlier).CanApply, "A large inconsistent training outlier was silently compensated");
        var wrongTcp = samples.Select(s => s with { SceneWorld = new(s.SceneWorld.X, s.SceneWorld.Y + .03, s.SceneWorld.Z) }).ToArray();
        var vertical = CalibrationSolver.Fit(desk, original, wrongTcp);
        check(!vertical.CanApply && vertical.Reason.Contains("Height") && vertical.ProposedPlacement.Position.Y == .75, "A TCP height mismatch was hidden by floating the base");
        var outside = samples.Select(s => s with { MeasuredWorld = new(s.MeasuredWorld.X + 2, .75, s.MeasuredWorld.Z) }).ToArray();
        check(!CalibrationSolver.Fit(desk, original, outside).CanApply, "A base centre outside the desk enabled Apply");
        Reject(() => CalibrationSolver.Fit(desk with { PrecisionMm = 25 }, original, samples), "A loose precision setting bypassed the conservative gate");
        check(desk.World(0, 0, 0) == new CalibrationPoint(-.7, .75, -.35), "Desk left/back origin conversion is incorrect");
        check(desk.World(1400, 700, 100) == new CalibrationPoint(.7, .85, .35), "Desk edge/height millimetre conversion is incorrect");
        var grid = new CalibrationMeasurement("grid intersections", HeightMm: 0, SquareMm: 50, OriginLeftMm: 100, OriginBackMm: 200, Column: 2, Row: 3);
        check(grid.World(desk).Distance(new(-.5, .75, 0)) < 1e-12, "Grid origin and numbered intersection conversion is incorrect");
        Reject(() => desk.World(-1, 0, 0), "Negative physical edge measurement was accepted");
        Reject(() => (grid with { Column = -1 }).World(desk), "A negative grid intersection was accepted");
        Reject(() => desk.World(0, double.NaN, 0), "A non-finite reference measurement was accepted");
        var roundTrip = CalibrationPlacement.Read(original.Json());
        check(original.Same(roundTrip), "Metric root placement did not survive the public JSON contract");
        check(original.Same(original with { Rotation = original.Rotation.Select(x => -x).ToArray() }), "Equivalent quaternion signs were treated as different roots");
        check(CalibrationPlacement.At(new(0, .75, 0), Math.PI + .2).YawRadians is >= -Math.PI and <= Math.PI, "Absolute scene yaw was not wrapped to the engine placement contract");
        Reject(() => CalibrationPlacement.Read(new JsonObject { ["position"] = new JsonArray(0.0, .75, 0.0), ["rotation"] = new JsonArray(.1, 0.0, 0.0, Math.Sqrt(.99)), ["unit"] = "m" }), "A tilted base entered the yaw-only fit");

        var identity = new CalibrationIdentity("piper-1", "usb:one", 7, "session-hash", "scene", "robot");
        var device = new JsonObject { ["id"] = "usb:one", ["generation"] = 7L, ["fresh"] = true,
            ["status"] = new JsonObject { ["error"] = 0, ["arm_status"] = 0, ["stationary_s"] = .5, ["control"] = 2, ["teach_state"] = 1 },
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject { ["actual_deg"] = (double)i, ["fresh"] = true, ["fault"] = 0 }).ToArray()) };
        var pose = new JsonObject { ["sceneId"] = "scene", ["entityId"] = "robot", ["rootPlacement"] = original.Json(), ["modelAssetId"] = "model-a", ["modelRevision"] = "revision-a", ["metricPlacementSupported"] = true, ["metricFrame"] = "engine-world-y-up",
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + (i + 1), ["position"] = i * Math.PI / 180, ["unit"] = "rad" }).ToArray()),
            ["toolSites"] = new JsonArray(new JsonObject { ["name"] = "grasp_site", ["position"] = new JsonArray(.1, .75, .2), ["rotation"] = new JsonArray(0.0, 0.0, 0.0, 1.0), ["unit"] = "m" }) };
        var captured = CalibrationCapture.Read(identity, identity, desk, original, device, pose, 1000, 1200);
        check(captured.ToolWorld == new CalibrationPoint(.1, .75, .2) && captured.JointDegrees.SequenceEqual(Enumerable.Range(0, 6).Select(i => (double)i)), "Capture did not preserve measured joints and named TCP world pose");
        check(captured.PoseFingerprint.Length == 64, "A calibration sample has no inspectable pose fingerprint");
        check(captured.ModelAssetId == "model-a", "Capture did not bind the compiled robot model asset");
        foreach (var changed in new[] { identity with { ProfileId = "other" }, identity with { DeviceId = "usb:two" }, identity with { Generation = 8 }, identity with { BackendSessionFingerprint = "new-session" }, identity with { SceneId = "new-scene" }, identity with { RobotId = "another-robot" } })
            Reject(() => CalibrationCapture.Read(identity, changed, desk, original, device, pose, 1000, 1200), "A changed calibration identity reused old measurements");
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, pose, 1000, 1501), "An IPC measurement older than500ms was captured");
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, pose, 1000, 999), "A reversed capture clock was accepted");
        JsonObject copy = (JsonObject)device.DeepClone(); copy["fresh"] = false;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200), "Stale arm feedback entered a geometric fit");
        copy = (JsonObject)device.DeepClone(); copy["joints"]![2]!["fault"] = 1;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200), "A faulted driver entered a calibration capture");
        copy = (JsonObject)device.DeepClone(); copy["status"]!["stationary_s"] = .39;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200), "A moving arm was captured");
        copy = (JsonObject)device.DeepClone(); copy["status"]!["teach_state"] = 0; copy["status"]!["control"] = 1;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200), "An ordinary enabled robot was treated as hand guiding");
        foreach (int faultStatus in new[] { 1, 5 })
        {
            copy = (JsonObject)device.DeepClone(); copy["status"]!["arm_status"] = faultStatus;
            Reject(() => CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200), "A fault/STOP controller status with retained teach state entered calibration");
        }
        copy = (JsonObject)device.DeepClone(); copy["status"]!["arm_status"] = 11;
        check(CalibrationCapture.Read(identity, identity, desk, original, copy, pose, 1000, 1200).ToolWorld.Finite, "Benign hand-guiding status11 was rejected");
        JsonObject badPose = (JsonObject)pose.DeepClone(); badPose["rootPlacement"]!["position"]![0] = .11;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, badPose, 1000, 1200), "A scene base edit was mixed into old calibration samples");
        badPose = (JsonObject)pose.DeepClone(); badPose["joints"]![0]!["position"] = .01;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, badPose, 1000, 1200), "An unacknowledged simulated pose entered the point fit");
        badPose = (JsonObject)pose.DeepClone(); badPose["toolSites"]![0]!["name"] = "camera_origin";
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, badPose, 1000, 1200), "An arbitrary camera origin replaced the chosen fixed TCP");
        badPose = (JsonObject)pose.DeepClone(); badPose["toolSites"]!.AsArray().Add(badPose["toolSites"]![0]!.DeepClone());
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, badPose, 1000, 1200), "Ambiguous duplicate named tool sites were accepted");
        badPose = (JsonObject)pose.DeepClone(); badPose["metricPlacementSupported"] = false;
        Reject(() => CalibrationCapture.Read(identity, identity, desk, original, device, badPose, 1000, 1200), "Unsupported metric model placement was treated as usable FK");
        var modelBound = identity with { ModelAssetId = "model-a", ModelRevision = "revision-a" };
        badPose = (JsonObject)pose.DeepClone(); badPose["modelAssetId"] = "model-b";
        Reject(() => CalibrationCapture.Read(modelBound, modelBound, desk, original, device, badPose, 1000, 1200), "Replacing the compiled model reused old calibration geometry");
        badPose = (JsonObject)pose.DeepClone(); badPose["modelRevision"] = "revision-b";
        Reject(() => CalibrationCapture.Read(modelBound, modelBound, desk, original, device, badPose, 1000, 1200), "An in-place compiled source/dependency revision reused old calibration geometry");
        foreach (string targetAction in new[] { "enable", "safety.accept", "zero.all", "pika.target", "joint.6.plus", "joint.3.target", "teleoperate.toggle", "engine.robot.target", "engine.robot.follow" })
        {
            check(CalibrationCommandGuard.Blocks(targetAction, true), "A queued/hotkey target escaped the calibration routing guard: " + targetAction);
            check(!CalibrationCommandGuard.Blocks(targetAction, false), "Calibration routing blocked ordinary controls after leaving the wizard");
        }
        check(!CalibrationCommandGuard.Blocks("stop", true) && !CalibrationCommandGuard.Blocks("disable", true) && !CalibrationCommandGuard.Blocks("calibration.capture", true), "Calibration routing blocked STOP/disable/passive capture");
        var report = new CalibrationReport(1, modelBound, desk, original, samples, fit, null, true, DateTimeOffset.UtcNow);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<CalibrationReport>(System.Text.Json.JsonSerializer.Serialize(report))!;
        var saved = CalibrationSavedState.Recover(decoded, "piper-1");
        check(saved is { RequiresInspection: true } && saved.Report.OriginalPlacement.Same(original) && saved.Report.Fit!.ProposedPlacement.Same(fit.ProposedPlacement), "Restart discarded pending scene-write original/proposed rollback evidence");
        saved = CalibrationSavedState.Recover(report with { PlacementUnverified = false, AppliedPlacement = fit.ProposedPlacement }, "piper-1");
        check(saved is { RequiresInspection: true } && saved.Report.AppliedPlacement!.Same(fit.ProposedPlacement), "Reload treated a previous applied root as currently verified or lost rollback placement");
        check(CalibrationSavedState.Recover(report with { PlacementUnverified = false }, "piper-1") is null, "An unapplied completed report silently resumed an old fit");
        Reject(() => CalibrationSavedState.Recover(report, "another-profile"), "Saved rollback state crossed robot profiles");
        Reject(() => CalibrationCapture.ValidateLive(identity, identity with { Generation = 8 }, device, true), "Post-save scope guard allowed a new CAN generation");
    }
}
