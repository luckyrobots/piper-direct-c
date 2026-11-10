using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class HeightValidationTests
{
    public static void Run()
    {
        var identity = new CalibrationIdentity("robot-a", "can-a", 1, "backend-a", "scene-a", "entity-a", "asset-a", "model-a");
        var desk = new CalibrationDesk(1.4, .7, .75, 5, "calibration_jaw_tips");
        var placement = CalibrationPlacement.At(new(0, .75, 0), 0);
        var device = new JsonObject { ["id"] = "can-a", ["generation"] = 1L, ["fresh"] = true,
            ["status"] = new JsonObject { ["error"] = 0, ["arm_status"] = 0, ["stationary_s"] = 0.0 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject {
                ["actual_deg"] = i * 10.0, ["fresh"] = true, ["fault"] = 0 }).ToArray()) };
        var pose = new JsonObject { ["sceneId"] = identity.SceneId, ["entityId"] = identity.RobotId, ["modelAssetId"] = identity.ModelAssetId,
            ["modelRevision"] = identity.ModelRevision, ["metricPlacementSupported"] = true, ["metricFrame"] = "engine-world-y-up",
            ["rootPlacement"] = placement.Json(), ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject {
                ["name"] = "joint" + i, ["unit"] = "rad", ["position"] = i * 10 * Math.PI / 180 }).ToArray()),
            ["toolSites"] = new JsonArray(new JsonObject { ["name"] = desk.ToolPoint, ["unit"] = "m", ["position"] = new JsonArray(.1, 1.05, .15) }) };
        var capture = HeightValidation.ReadPreview(identity, desk, placement, device, pose, 250);
        Check(HeightValidation.FreshAge(400, 100), "A preview still inside the 500ms evidence window was hidden");
        Check(!HeightValidation.FreshAge(400, 101), "A delayed scene acknowledgement reset the source evidence age");
        Check(!HeightValidation.FreshAge(1, double.NaN), "An invalid live clock age was treated as fresh");
        var observed = new HeightObservation(identity, desk, capture, DateTimeOffset.UtcNow, "report.json");
        Check(Math.Abs(observed.PredictedMm - 300) < 1e-9, "Height preview used floor/root height instead of tabletop-relative site height");
        Check(!HeightValidation.SameHeldPose(observed, identity, device), "Moving live preview was saved as a stationary measurement");
        device["status"]!["stationary_s"] = 1.0;
        Check(HeightValidation.SameHeldPose(observed, identity, device), "Stationary fresh frozen pose was rejected");
        Check(!HeightValidation.SameHeldPose(observed, identity with { ProfileId = "robot-b" }, device), "Frozen pose leaked to another profile");
        Check(!HeightValidation.SameHeldPose(observed, identity with { BackendSessionFingerprint = "other" }, device), "Frozen pose survived backend identity replacement");
        Check(!HeightValidation.SameHeldPose(observed, identity with { SceneId = "other" }, device), "Frozen pose silently rebound to another scene session");
        device["joints"]![2]!["actual_deg"] = 31.0;
        Check(!HeightValidation.SameHeldPose(observed, identity, device), "Entered measurement silently followed a newly moved physical pose");
        device["joints"]![2]!["actual_deg"] = 30.0;
        foreach (double age in new[] { -1, 501, double.NaN, double.PositiveInfinity })
            Reject(() => HeightValidation.ReadPreview(identity, desk, placement, device, pose, age), "Stale/invalid source age produced a live height");
        foreach (string field in new[] { "sceneId", "entityId", "modelAssetId", "modelRevision" })
        {
            var bad = (JsonObject)pose.DeepClone(); bad[field] = "other";
            Reject(() => HeightValidation.ReadPreview(identity, desk, placement, device, bad, 1), "Height preview accepted changed " + field);
        }
        var wrongRoot = (JsonObject)pose.DeepClone(); wrongRoot["rootPlacement"] = CalibrationPlacement.At(new(.01, .75, 0), 0).Json();
        Reject(() => HeightValidation.ReadPreview(identity, desk, placement, device, wrongRoot, 1), "Height preview ignored a changed calibrated root");
        var wrongJoint = (JsonObject)pose.DeepClone(); wrongJoint["joints"]![0]!["position"] = .5;
        Reject(() => HeightValidation.ReadPreview(identity, desk, placement, device, wrongJoint, 1), "Height preview accepted scene joints unrelated to physical feedback");
        foreach (string condition in new[] { "name", "unit", "duplicate", "missing" })
        {
            var bad = (JsonObject)pose.DeepClone();
            if (condition is "name" or "unit") bad["toolSites"]![0]![condition] = "other";
            else if (condition == "duplicate") bad["toolSites"]!.AsArray().Add(bad["toolSites"]![0]!.DeepClone());
            else bad["toolSites"] = new JsonArray();
            Reject(() => HeightValidation.ReadPreview(identity, desk, placement, device, bad, 1), "Height preview accepted an unverified jaw-point " + condition);
        }
        foreach (string condition in new[] { "device", "generation", "fresh", "fault", "status" })
        {
            var bad = (JsonObject)device.DeepClone();
            if (condition == "device") bad["id"] = "other";
            else if (condition == "generation") bad["generation"] = 2;
            else if (condition == "fresh") bad["fresh"] = false;
            else if (condition == "fault") bad["joints"]![1]!["fault"] = 1;
            else bad["status"]!["arm_status"] = 4;
            Reject(() => HeightValidation.ReadPreview(identity, desk, placement, bad, pose, 1), "Height preview accepted bad physical " + condition);
        }
        foreach (string text in new[] { "NaN", "Infinity", "-1", "2001", "cm", "", new string('9', 33) })
            Reject(() => HeightValidation.ParseMeasurement(text), "Invalid tape measurement was accepted: " + text);
        Check(HeightValidation.ParseMeasurement("590.5") == 590.5, "Millimetre decimal tape reading changed");
        var result = new HeightCheck(observed, 304, DateTimeOffset.UtcNow);
        Check(Math.Abs(result.ErrorMm - 4) < 1e-9, "Height discrepancy direction was reversed");
        var draft = new HeightCheckDocument(1, identity.ProfileId, [result], new(observed, "3e"));
        var restored = JsonSerializer.Deserialize<HeightCheckDocument>(JsonSerializer.Serialize(draft, PluginProtocol.Json), PluginProtocol.Json)!;
        HeightValidation.ValidateDocument(restored, identity.ProfileId);
        Check(restored.Draft?.MeasuredText == "3e" && restored.Draft.Observation!.Capture.PoseFingerprint == observed.Capture.PoseFingerprint,
            "Draft persistence lost partially typed measurement or frozen pose evidence");
        Check(restored.Checks[0].Observation.Identity == identity && restored.Checks[0].Observation.Capture.JointDegrees.SequenceEqual(capture.JointDegrees),
            "Saved height check did not preserve profile/model/scene and joint evidence");
        Reject(() => HeightValidation.ValidateDocument(restored, "other"), "Height checks loaded into a different profile");
        Reject(() => HeightValidation.ValidateDocument(restored with { Checks = Enumerable.Repeat(result, 257).ToArray() }, identity.ProfileId), "Unbounded saved height history was accepted");
        Reject(() => HeightValidation.ValidateDocument(restored with { Draft = new(observed with { Capture = capture with { ToolWorld = new(0, double.NaN, 0) } }, "2") }, identity.ProfileId),
            "Nonfinite restored jaw pose was accepted");
        Reject(() => HeightValidation.ValidateDocument(restored with { Draft = new(observed with { Capture = capture with { Placement = placement with { Rotation = null! } } }, "2") }, identity.ProfileId),
            "Malformed restored placement rotation was accepted");
        Reject(() => HeightValidation.ValidateDocument(restored with { Draft = new(observed with { Capture = capture with { PoseFingerprint = null! } }, "2") }, identity.ProfileId),
            "Missing restored pose fingerprint was accepted");
        Reject(() => HeightValidation.ValidateDocument(restored with { Checks = [result with { Observation = null! }] }, identity.ProfileId),
            "Null restored height observation was accepted");
        Check(restored.Checks[0].Observation.Desk == desk && restored.Checks[0].Observation.CalibrationReport == "report.json",
            "Height history lost its tabletop/report provenance");
    }
}
