using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Measured opening for the Piper/Pika paired slide-joint model, in metres.</summary>
public sealed class GripperLink
{
    public const double ClosedNoiseToleranceMm = .25;
    private static readonly double MinimumFeedbackAngle = -PikaProtocol.Angle(PikaProtocol.ArrivalToleranceMm);
    private readonly int firstSign;
    private readonly double firstClosed;
    private readonly double maximumMm;
    private readonly string? mappingError;
    private readonly string firstJoint, secondJoint;
    public string Status { get; private set; } = "Scene gripper: waiting for fresh matching Pika feedback.";
    public double? ErrorMm { get; private set; }

    public GripperLink(JsonArray modelJoints, string jointPrefix = "")
    {
        jointPrefix = SceneRobotBindings.ValidatePrefix(jointPrefix);
        firstJoint = jointPrefix + "joint7"; secondJoint = jointPrefix + "joint8";
        if (!Finger(modelJoints, firstJoint, out int first, out double closed, out double open) ||
            !Finger(modelJoints, secondJoint, out int second, out double otherClosed, out double otherOpen) ||
            first == second || Math.Abs(closed + otherClosed) > 1e-6 || Math.Abs(open + otherOpen) > 1e-6)
            mappingError = "Scene gripper not mapped: expected mirrored joint7/joint8 slide ranges in metres.";
        else
        {
            firstSign = first;
            firstClosed = closed;
            maximumMm = 2000 * Math.Abs(open - closed);
        }
        if (mappingError is not null) Status = mappingError;
    }

    public void AddFeedback(JsonObject joints, JsonObject? pika, bool matchesProfile, double? closedDisplayReference = null)
    {
        ErrorMm = null;
        if (mappingError is not null) { Status = mappingError; return; }
        if (!matchesProfile)
        {
            Status = "Scene gripper unchanged: no Pika matching this robot profile.";
            return;
        }
        if (!TryMeasuredOpening(pika, out double width, closedDisplayReference))
        {
            Status = ClosedDisplayReferenceNeeded(pika) && closedDisplayReference is null
                ? "Scene gripper unchanged: closed display reference needed. With the physical jaws closed, use Settings → Robot → Gripper display reference."
                : "Scene gripper unchanged: waiting for fresh valid Pika motor position.";
            return;
        }
        if (width > maximumMm)
        {
            Status = $"Scene gripper unchanged: measured opening {width:F1} mm is outside the model's 0–{maximumMm:F1} mm range. Check the tool model/calibration.";
            return;
        }
        // Calibrated models can have a nonzero open endpoint. The closed
        // endpoint defines contact; equal opposite slides open by half the gap.
        double firstPosition = firstClosed - firstSign * width / 2000;
        joints[firstJoint] = firstPosition;
        joints[secondJoint] = -firstPosition;
        Status = $"Scene gripper linked · measured {width:F1} mm / model {maximumMm:F1} mm" +
            (closedDisplayReference.HasValue ? $" · display reference {closedDisplayReference.Value:F5} rad (full travel unverified)" : "");
    }

    // Bound endpoint encoder noise in feedback presentation only. Raw telemetry,
    // motor targets and individual model ranges retain their original bounds.
    public static bool TryMeasuredOpening(JsonObject? pika, out double width, double? closedDisplayReference = null)
    {
        width = double.NaN;
        if (!TryRawAngle(pika, out double angle)) return false;
        if (closedDisplayReference is double reference)
        {
            if (!double.IsFinite(reference) || Math.Abs(reference) > .15 ||
                Math.Abs(PikaProtocol.Width(reference)) > PikaClosedDisplayReference.MaximumNominalOffsetMm) return false;
            angle -= reference;
        }
        if (!PikaProtocol.ValidAngle(angle) && (angle >= 0 || angle < MinimumFeedbackAngle))
            return false;
        width = PikaProtocol.Width(angle);
        // Noise at either physical endpoint is not extra jaw travel. The same
        // small allowance applies at 0 and 90 mm; larger/model mismatches fail.
        if (width >= -ClosedNoiseToleranceMm && width < 0) width = 0;
        if (width > 90 && width <= 90 + ClosedNoiseToleranceMm) width = 90;
        return double.IsFinite(width) && width >= 0 && width <= 90;
    }

    public static bool ClosedDisplayReferenceNeeded(JsonObject? pika) => TryRawAngle(pika, out double angle) &&
        angle < 0 && Math.Abs(angle) <= .15 && PikaProtocol.Width(angle) < -ClosedNoiseToleranceMm &&
        PikaProtocol.Width(angle) >= -PikaClosedDisplayReference.MaximumNominalOffsetMm;

    private static bool TryRawAngle(JsonObject? pika, out double angle)
    {
        angle = double.NaN;
        return pika?["connected"]?.GetValue<bool>() != false &&
            pika?["fresh"] is JsonValue fresh && fresh.TryGetValue<bool>(out bool current) && current &&
            pika["telemetry"]?["motor"]?["Position"] is JsonValue motor && motor.TryGetValue<double>(out angle) && double.IsFinite(angle);
    }

    public void Verify(JsonObject requested, JsonArray pose)
    {
        ErrorMm = null;
        if (requested[firstJoint] is null || requested[secondJoint] is null) return;
        double maximum = 0;
        foreach (string name in new[] { firstJoint, secondJoint })
        {
            var matches = pose.OfType<JsonObject>().Where(j => j["name"]?.GetValue<string>() == name).ToArray();
            if (matches.Length != 1 || matches[0]["position"] is not JsonValue value || !value.TryGetValue<double>(out double actual))
                throw new InvalidOperationException($"The editor did not acknowledge measured {name} feedback.");
            double error = Math.Abs(actual - requested[name]!.GetValue<double>()) * 1000;
            if (!double.IsFinite(error) || error > .01)
                throw new InvalidOperationException($"Scene gripper {name} did not accept measured feedback.");
            maximum = Math.Max(maximum, error);
        }
        ErrorMm = maximum;
    }

    private static bool Finger(JsonArray joints, string name, out int sign, out double closed, out double open)
    {
        sign = 0; closed = open = 0;
        var matches = joints.OfType<JsonObject>().Where(j => j["name"]?.GetValue<string>() == name).ToArray();
        if (matches.Length != 1 || matches[0]["unit"]?.GetValue<string>() != "m" || matches[0]["limited"]?.GetValue<bool>() != true ||
            matches[0]["min"] is not JsonValue low || !low.TryGetValue<double>(out double minimum) ||
            matches[0]["max"] is not JsonValue high || !high.TryGetValue<double>(out double maximum) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum)) return false;
        if (minimum >= maximum) return false;
        if (maximum <= 1e-9 && minimum < 0) { sign = -1; closed = minimum; open = maximum; }
        else if (minimum >= -1e-9 && maximum > 0) { sign = 1; closed = maximum; open = minimum; }
        return sign != 0 && Math.Abs(closed) <= .05 + 1e-9 && Math.Abs(open - closed) > 0;
    }
}
