using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The feedback health every calibration and height-check read requires.</summary>
public static class DeviceHealth
{
    /// <summary>Fresh device, no controller error, and six fresh joints without a driver fault.</summary>
    public static bool IsHealthy(JsonObject? device) => device?["fresh"]?.GetValue<bool>() == true && device["status"]?["error"]?.GetValue<int>() == 0 &&
        device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true && j["fault"]?.GetValue<int>() == 0);
}

/// <summary>Checks shared by every read of the engine's robot.read pose reply.</summary>
public static class CalibrationPose
{
    /// <summary>The pose is for this scene entity, in engine-world Y-up metres.</summary>
    public static bool IsMetric(JsonObject pose, string sceneId, string entityId) =>
        pose["sceneId"]?.GetValue<string>() == sceneId && pose["entityId"]?.GetValue<string>() == entityId &&
        pose["metricPlacementSupported"]?.GetValue<bool>() == true && pose["metricFrame"]?.GetValue<string>() == "engine-world-y-up";

    /// <summary>Requires the scene's six arm joints to echo the measured angles within 0.01 degrees.</summary>
    public static void RequireJointsEcho(JsonObject pose, double[] measuredDegrees, string missing, string mismatch)
    {
        if (measuredDegrees.Length != 6 || measuredDegrees.Any(x => !double.IsFinite(x)) || pose["joints"] is not JsonArray sceneJoints)
            throw new InvalidOperationException(missing);
        for (int i = 0; i < 6; i++)
        {
            var matches = sceneJoints.Where(j => j?["name"]?.GetValue<string>() == "joint" + (i + 1)).ToArray();
            if (matches.Length != 1 || matches[0]?["unit"]?.GetValue<string>() != "rad" ||
                matches[0]?["position"]?.GetValue<double>() is not double angle || !double.IsFinite(angle) || Math.Abs(angle * 180 / Math.PI - measuredDegrees[i]) > .01)
                throw new InvalidOperationException(mismatch);
        }
    }

    /// <summary>Reads the one metric tool site with this exact name.</summary>
    public static CalibrationPoint ReadToolSite(JsonObject pose, string siteName, string ambiguous, string nonFinite)
    {
        var sites = pose["toolSites"]?.AsArray().Where(s => s?["name"]?.GetValue<string>() == siteName).ToArray() ?? [];
        if (sites.Length != 1 || sites[0]?["unit"]?.GetValue<string>() != "m" || sites[0]?["position"] is not JsonArray { Count: 3 } xyz)
            throw new InvalidOperationException(ambiguous);
        var tool = new CalibrationPoint(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz[2]!.GetValue<double>());
        if (!tool.Finite) throw new InvalidOperationException(nonFinite);
        return tool;
    }
}

public sealed record CalibrationIdentity(string ProfileId, string DeviceId, long Generation, string BackendSessionFingerprint, string SceneId, string RobotId, string ModelAssetId = "", string ModelRevision = "");
public sealed record CalibrationCapture(CalibrationPoint ToolWorld, CalibrationPlacement Placement, double[] JointDegrees, string PoseFingerprint, string ModelAssetId, string ModelRevision)
{
    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static void ValidateLive(CalibrationIdentity expected, CalibrationIdentity current, JsonObject device, bool requireHandGuiding)
    {
        if (expected != current || device["id"]?.GetValue<string>() != expected.DeviceId || device["generation"]?.GetValue<long>() != expected.Generation)
            throw new InvalidOperationException("Robot/profile/backend identity changed. Start a new calibration; old measurements cannot authorize a new connection.");
        bool handGuiding = TeachingState.Inspect(device).HandGuiding;
        int armStatus = device["status"]?["arm_status"]?.GetValue<int>() ?? -1;
        if (!DeviceHealth.IsHealthy(device) || armStatus != 0 && !(armStatus == 11 && handGuiding))
            throw new InvalidOperationException("Calibration requires healthy, fresh six-joint feedback and a healthy controller status.");
        if (device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4 || requireHandGuiding && !handGuiding)
            throw new InvalidOperationException("Use the physical teach button (solid green), guide the contact point by hand and hold still for 0.4 seconds. The wizard sends no motion command.");
    }

    public static CalibrationCapture Read(CalibrationIdentity expected, CalibrationIdentity current, CalibrationDesk desk,
        CalibrationPlacement? expectedPlacement, JsonObject device, JsonObject pose, long requestStartedMs, long nowMs, bool requireHandGuiding = true)
    {
        // ValidateLive covers the identity, device generation and feedback health.
        ValidateLive(expected, current, device, requireHandGuiding);
        if (pose["sceneId"]?.GetValue<string>() != expected.SceneId || pose["entityId"]?.GetValue<string>() != expected.RobotId)
            throw new InvalidOperationException("Robot/profile/backend/scene identity changed. Start a new calibration; old measurements cannot authorize a new connection.");
        if (nowMs < requestStartedMs || nowMs - requestStartedMs > TelemetryReceipt.MaximumAgeMilliseconds)
            throw new InvalidOperationException($"Capture needs healthy, fresh arm and scene feedback within {TelemetryReceipt.MaximumAgeMilliseconds:F0} ms. Hold still and try again.");
        if (!CalibrationPose.IsMetric(pose, expected.SceneId, expected.RobotId) ||
            pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } modelAssetId || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } modelRevision)
            throw new InvalidOperationException("This model has no supported engine-world metric placement/FK contract. " + (pose["metricPlacementReason"]?.GetValue<string>() ?? "Update the engine and use a flat, unparented, unit-scale robot model."));
        if (expected.ModelAssetId.Length > 0 && expected.ModelAssetId != modelAssetId || expected.ModelRevision.Length > 0 && expected.ModelRevision != modelRevision)
            throw new InvalidOperationException("The compiled robot model changed. Start a new calibration and verify its named physical contact point.");
        var placement = CalibrationPlacement.Read(pose["rootPlacement"]);
        if (expectedPlacement is not null && !expectedPlacement.Same(placement))
            throw new InvalidOperationException("The scene base placement changed after calibration started. Undo it or start a new calibration before adding/applying points.");
        double[] measured = device["joints"]!.AsArray().Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray();
        CalibrationPose.RequireJointsEcho(pose, measured, "Six finite measured and simulated joint readings are required.",
            "The simulated joints have not acknowledged the stationary measured pose. Wait, then Capture again.");
        var tool = CalibrationPose.ReadToolSite(pose, desk.ToolPoint,
            "The model must expose exactly one named metric tool site '" + desk.ToolPoint + "'. Verify the TCP/model instead of guessing a fingertip or camera origin.",
            "The named tool site has no finite world position.");
        return new(tool, placement, measured, Fingerprint(pose.ToJsonString()), modelAssetId, modelRevision);
    }
}

public sealed record CalibrationReport(int Schema, CalibrationIdentity Identity, CalibrationDesk Desk, CalibrationPlacement OriginalPlacement,
    CalibrationSample[] Samples, CalibrationFit? Fit, CalibrationPlacement? AppliedPlacement, bool PlacementUnverified, DateTimeOffset UpdatedAt,
    string Evidence = "Operator edge/grid measurements, measured joint feedback and model forward kinematics. Fits base world-Y yaw/XZ translation only; no encoder/joint/TCP/scale changes. Camera calibration and exposure synchronization are unverified.",
    string CoordinateFrame = "Engine world Y-up, metres. Desk centre X=0/Z=0; left=-X, back=-Z. Heights above measured desk surface.", string ClockDomain = "host-utc-receipt (not exposure time)",
    CalibrationModelReevaluation? ModelReevaluation = null)
{
    /// <summary>A scene write was sent or applied, so the placement must be inspected before more edits.</summary>
    [JsonIgnore] public bool RequiresInspection => PlacementUnverified || AppliedPlacement is not null;

    /// <summary>Validates a report read from disk. It restores rollback evidence, never permission to repeat an interrupted write.</summary>
    public void Validate(string profileId)
    {
        if (Schema != 1 || Identity is null || Desk is null || OriginalPlacement?.Rotation is not { Length: 4 } ||
            Identity.ProfileId != profileId || string.IsNullOrWhiteSpace(Identity.ModelAssetId) || string.IsNullOrWhiteSpace(Identity.ModelRevision) ||
            Samples is null || Samples.Length > 24 || Samples.Any(s => s is null || !s.MeasuredWorld.Finite || !s.SceneWorld.Finite || s.JointDegrees is not { Length: 6 } || s.Measurement is null) ||
            AppliedPlacement is { Rotation: not { Length: 4 } } || AppliedPlacement is not null && Fit is null || Fit is not null && Fit.ProposedPlacement?.Rotation is not { Length: 4 })
            throw new InvalidOperationException("Saved calibration identity/geometry is invalid; inspect the report before starting a new check.");
        Desk.Validate(); _ = CalibrationPlacement.Read(OriginalPlacement.Json());
        if (AppliedPlacement is not null) _ = CalibrationPlacement.Read(AppliedPlacement.Json());
        if (Fit is not null) _ = CalibrationPlacement.Read(Fit.ProposedPlacement.Json());
    }
}
