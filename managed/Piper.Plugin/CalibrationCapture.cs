using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

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
        if (device["fresh"]?.GetValue<bool>() != true || device["status"]?["error"]?.GetValue<int>() != 0 ||
            armStatus != 0 && !(armStatus == 11 && handGuiding) || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0))
            throw new InvalidOperationException("Calibration requires healthy, fresh six-joint feedback and a healthy controller status.");
        if (device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4 || requireHandGuiding && !handGuiding)
            throw new InvalidOperationException("Use the physical teach button (solid green), guide the contact point by hand and hold still for 0.4 seconds. The wizard sends no motion command.");
    }

    public static CalibrationCapture Read(CalibrationIdentity expected, CalibrationIdentity current, CalibrationDesk desk,
        CalibrationPlacement? expectedPlacement, JsonObject device, JsonObject pose, long requestStartedMs, long nowMs, bool requireHandGuiding = true)
    {
        ValidateLive(expected, current, device, requireHandGuiding);
        if (expected != current || device["id"]?.GetValue<string>() != expected.DeviceId || device["generation"]?.GetValue<long>() != expected.Generation ||
            pose["sceneId"]?.GetValue<string>() != expected.SceneId || pose["entityId"]?.GetValue<string>() != expected.RobotId)
            throw new InvalidOperationException("Robot/profile/backend/scene identity changed. Start a new calibration; old measurements cannot authorize a new connection.");
        if (nowMs < requestStartedMs || nowMs - requestStartedMs > 500 || device["fresh"]?.GetValue<bool>() != true ||
            device["status"]?["error"]?.GetValue<int>() != 0 || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0))
            throw new InvalidOperationException("Capture needs healthy, fresh arm and scene feedback within 500 ms. Hold still and try again.");
        if (pose["metricPlacementSupported"]?.GetValue<bool>() != true || pose["metricFrame"]?.GetValue<string>() != "engine-world-y-up" ||
            pose["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } modelAssetId || pose["modelRevision"]?.GetValue<string>() is not { Length: > 0 } modelRevision)
            throw new InvalidOperationException("This model has no supported engine-world metric placement/FK contract. " + (pose["metricPlacementReason"]?.GetValue<string>() ?? "Update the engine and use a flat, unparented, unit-scale robot model."));
        if (expected.ModelAssetId.Length > 0 && expected.ModelAssetId != modelAssetId || expected.ModelRevision.Length > 0 && expected.ModelRevision != modelRevision)
            throw new InvalidOperationException("The compiled robot model changed. Start a new calibration and verify its named physical contact point.");
        var placement = CalibrationPlacement.Read(pose["rootPlacement"]);
        if (expectedPlacement is not null && !expectedPlacement.Same(placement))
            throw new InvalidOperationException("The scene base placement changed after calibration started. Undo it or start a new calibration before adding/applying points.");
        double[] measured = joints.Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray();
        if (measured.Any(x => !double.IsFinite(x)) || pose["joints"] is not JsonArray sceneJoints)
            throw new InvalidOperationException("Six finite measured and simulated joint readings are required.");
        for (int i = 0; i < 6; i++)
        {
            var matches = sceneJoints.Where(j => j?["name"]?.GetValue<string>() == "joint" + (i + 1)).ToArray();
            if (matches.Length != 1 || matches[0]?["unit"]?.GetValue<string>() != "rad" ||
                matches[0]?["position"]?.GetValue<double>() is not double angle || !double.IsFinite(angle) || Math.Abs(angle * 180 / Math.PI - measured[i]) > .01)
                throw new InvalidOperationException("The simulated joints have not acknowledged the stationary measured pose. Wait, then Capture again.");
        }
        var sites = pose["toolSites"]?.AsArray().Where(s => s?["name"]?.GetValue<string>() == desk.ToolPoint).ToArray() ?? [];
        if (sites.Length != 1 || sites[0]?["unit"]?.GetValue<string>() != "m" || sites[0]?["position"] is not JsonArray { Count: 3 } xyz)
            throw new InvalidOperationException("The model must expose exactly one named metric tool site '" + desk.ToolPoint + "'. Verify the TCP/model instead of guessing a fingertip or camera origin.");
        var tool = new CalibrationPoint(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz[2]!.GetValue<double>());
        if (!tool.Finite) throw new InvalidOperationException("The named tool site has no finite world position.");
        return new(tool, placement, measured, Fingerprint(pose.ToJsonString()), modelAssetId, modelRevision);
    }
}

public sealed record CalibrationReport(int Schema, CalibrationIdentity Identity, CalibrationDesk Desk, CalibrationPlacement OriginalPlacement,
    CalibrationSample[] Samples, CalibrationFit? Fit, CalibrationPlacement? AppliedPlacement, bool PlacementUnverified, DateTimeOffset UpdatedAt,
    string Evidence = "Operator edge/grid measurements, measured joint feedback and model forward kinematics. Fits base world-Y yaw/XZ translation only; no encoder/joint/TCP/scale changes. Camera calibration and exposure synchronization are unverified.",
    string CoordinateFrame = "Engine world Y-up, metres. Desk centre X=0/Z=0; left=-X, back=-Z. Heights above measured desk surface.", string ClockDomain = "host-utc-receipt (not exposure time)");

/// <summary>Restores rollback evidence, never permission to repeat an interrupted write.</summary>
public sealed record CalibrationSavedState(CalibrationReport Report, bool RequiresInspection)
{
    public static CalibrationSavedState? Recover(CalibrationReport report, string profileId)
    {
        if (report.Schema != 1 || report.Identity is null || report.Desk is null || report.OriginalPlacement?.Rotation is not { Length: 4 } ||
            report.Identity.ProfileId != profileId || string.IsNullOrWhiteSpace(report.Identity.ModelAssetId) || string.IsNullOrWhiteSpace(report.Identity.ModelRevision) ||
            report.Samples is null || report.Samples.Length > 24 || report.Samples.Any(s => s is null || !s.MeasuredWorld.Finite || !s.SceneWorld.Finite || s.JointDegrees is not { Length: 6 } || s.Measurement is null) ||
            report.AppliedPlacement is { Rotation: not { Length: 4 } } || report.Fit is not null && report.Fit.ProposedPlacement?.Rotation is not { Length: 4 })
            throw new InvalidOperationException("Saved calibration identity/geometry is invalid; inspect the report before starting a new check.");
        report.Desk.Validate(); _ = CalibrationPlacement.Read(report.OriginalPlacement.Json());
        if (report.AppliedPlacement is not null) _ = CalibrationPlacement.Read(report.AppliedPlacement.Json());
        if (report.Fit is not null) _ = CalibrationPlacement.Read(report.Fit.ProposedPlacement.Json());
        return report.PlacementUnverified || report.AppliedPlacement is not null ? new(report, true) : null;
    }
}
