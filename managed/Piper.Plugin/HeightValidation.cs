using System.Globalization;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>A separately logged height observation; never an input to base fitting.</summary>
public sealed record HeightObservation(CalibrationIdentity Identity, CalibrationDesk Desk, CalibrationCapture Capture,
    DateTimeOffset CapturedAt, string CalibrationReport)
{
    public double PredictedMm => (Capture.ToolWorld.Y - Desk.SurfaceY) * 1000;
}

public sealed record HeightCheck(HeightObservation Observation, double MeasuredMm, DateTimeOffset RecordedAt)
{
    public double ErrorMm => MeasuredMm - Observation.PredictedMm;
}

public sealed record HeightCheckDraft(HeightObservation? Observation, string MeasuredText);
public sealed record HeightCheckDocument(int Schema, string ProfileId, HeightCheck[] Checks, HeightCheckDraft? Draft);

public static class HeightValidation
{
    public const int MaximumChecks = 256;
    public static bool FreshAge(double sourceAgeMilliseconds, double elapsedMilliseconds) =>
        double.IsFinite(sourceAgeMilliseconds) && double.IsFinite(elapsedMilliseconds) && sourceAgeMilliseconds >= 0 &&
        elapsedMilliseconds >= 0 && sourceAgeMilliseconds + elapsedMilliseconds <= 500;

    public static double ParseMeasurement(string text)
    {
        if (text is null || text.Length > 32 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value is < 0 or > 2000)
            throw new InvalidOperationException("Enter the vertical tape measurement in millimetres, from 0 to 2000.");
        return value;
    }

    // Unlike a saved observation, the live preview may be read while the arm is
    // moving. Its scene joints must still echo the exact fresh feedback receipt.
    public static CalibrationCapture ReadPreview(CalibrationIdentity expected, CalibrationDesk desk,
        CalibrationPlacement expectedPlacement, JsonObject device, JsonObject pose, double ageMilliseconds)
    {
        desk.Validate();
        if (desk.ToolPoint != "calibration_jaw_tips")
            throw new InvalidOperationException("Confirm the model's jaw-tip midpoint before checking its height.");
        if (!FreshAge(ageMilliseconds, 0) ||
            device["id"]?.GetValue<string>() != expected.DeviceId || device["generation"]?.GetValue<long>() != expected.Generation ||
            !DeviceHealth.IsHealthy(device) || device["status"]?["arm_status"]?.GetValue<int>() is not (0 or 11))
            throw new InvalidOperationException("Waiting for fresh, healthy robot feedback.");
        if (!CalibrationPose.IsMetric(pose, expected.SceneId, expected.RobotId) ||
            pose["modelAssetId"]?.GetValue<string>() != expected.ModelAssetId || pose["modelRevision"]?.GetValue<string>() != expected.ModelRevision)
            throw new InvalidOperationException("Waiting for the matching calibrated scene model.");
        var root = CalibrationPlacement.Read(pose["rootPlacement"]);
        if (!root.Same(expectedPlacement)) throw new InvalidOperationException("The scene base moved after calibration. Check its saved placement first.");
        var angles = device["joints"]!.AsArray().Select(j => j?["actual_deg"]?.GetValue<double>() ?? double.NaN).ToArray();
        CalibrationPose.RequireJointsEcho(pose, angles, "Waiting for six measured and simulated joint angles.", "Waiting for the simulation to follow the measured joints.");
        var tool = CalibrationPose.ReadToolSite(pose, desk.ToolPoint, "The current model must expose exactly one metric jaw-tip midpoint.", "The model's jaw-tip position is invalid.");
        return new(tool, root, angles, CalibrationCapture.Fingerprint(pose.ToJsonString()), expected.ModelAssetId, expected.ModelRevision);
    }

    public static void ValidateObservation(HeightObservation observation, string profileId)
    {
        if (observation is null || observation.Identity is not { } id || id.ProfileId != profileId || string.IsNullOrWhiteSpace(id.DeviceId) ||
            string.IsNullOrWhiteSpace(id.SceneId) || string.IsNullOrWhiteSpace(id.RobotId) || string.IsNullOrWhiteSpace(id.ModelAssetId) ||
            string.IsNullOrWhiteSpace(id.ModelRevision) || observation.Desk is null || observation.Capture is not { } capture ||
            capture.JointDegrees is not { Length: 6 } || capture.JointDegrees.Any(a => !double.IsFinite(a)) || !capture.ToolWorld.Finite ||
            capture.ModelAssetId != id.ModelAssetId || capture.ModelRevision != id.ModelRevision || capture.PoseFingerprint is not { Length: 64 } ||
            !capture.PoseFingerprint.All(Uri.IsHexDigit) || capture.Placement?.Rotation is not { Length: 4 } || observation.CapturedAt == default || observation.CalibrationReport is null || observation.CalibrationReport.Length > 256)
            throw new InvalidOperationException("The saved height observation has invalid robot/model evidence.");
        observation.Desk.Validate();
        if (observation.Desk.ToolPoint != "calibration_jaw_tips") throw new InvalidOperationException("The saved height observation uses a different measuring point.");
        _ = CalibrationPlacement.Read(capture.Placement.Json());
    }

    public static void ValidateDocument(HeightCheckDocument document, string profileId)
    {
        if (document is null || document.Schema != 1 || document.ProfileId != profileId || document.Checks is null || document.Checks.Length > MaximumChecks)
            throw new InvalidOperationException("The saved height checks belong to a different robot or unsupported format.");
        foreach (var check in document.Checks)
        {
            if (check is null) throw new InvalidOperationException("The saved height check is incomplete.");
            ValidateObservation(check.Observation, profileId);
            _ = ParseMeasurement(check.MeasuredMm.ToString("R", CultureInfo.InvariantCulture));
            if (check.RecordedAt == default) throw new InvalidOperationException("The saved height check has no recording time.");
        }
        if (document.Draft is { } draft)
        {
            if (draft.MeasuredText is null || draft.MeasuredText.Length > 32) throw new InvalidOperationException("The saved tape-measure draft is too long.");
            if (draft.Observation is { } observation) ValidateObservation(observation, profileId);
        }
    }

    public static bool SameHeldPose(HeightObservation frozen, CalibrationIdentity identity, JsonObject device)
    {
        if (frozen.Identity != identity || !DeviceHealth.IsHealthy(device) ||
            device["id"]?.GetValue<string>() != identity.DeviceId || device["generation"]?.GetValue<long>() != identity.Generation ||
            device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4 || device["status"]?["arm_status"]?.GetValue<int>() is not (0 or 11)) return false;
        var joints = device["joints"]!.AsArray();
        for (int i = 0; i < 6; i++)
            if (joints[i]?["actual_deg"]?.GetValue<double>() is not double angle || !double.IsFinite(angle) || Math.Abs(angle - frozen.Capture.JointDegrees[i]) > .1)
                return false;
        return true;
    }
}
