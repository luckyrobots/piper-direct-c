using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Measured opening for the Piper/Pika paired slide-joint model, in metres.</summary>
public sealed class GripperLink
{
    public const double ClosedNoiseToleranceMm = .1;
    private static readonly double MinimumFeedbackAngle = -PikaProtocol.Angle(PikaProtocol.ArrivalToleranceMm);
    private readonly int firstSign;
    private readonly double maximumMm;
    private readonly string? mappingError;
    public string Status { get; private set; } = "Scene gripper: waiting for fresh matching Pika feedback.";
    public double? ErrorMm { get; private set; }

    public GripperLink(JsonArray modelJoints)
    {
        if (!Finger(modelJoints, "joint7", out int first, out double firstTravel) ||
            !Finger(modelJoints, "joint8", out int second, out double secondTravel) ||
            first == second || Math.Abs(firstTravel - secondTravel) > 1e-6)
            mappingError = "Scene gripper not mapped: expected opposite, equal-range joint7/joint8 slides in metres.";
        else
        {
            firstSign = first;
            maximumMm = 2000 * Math.Min(firstTravel, secondTravel);
        }
        if (mappingError is not null) Status = mappingError;
    }

    public void AddFeedback(JsonObject joints, JsonObject? pika, bool matchesProfile)
    {
        ErrorMm = null;
        if (mappingError is not null) { Status = mappingError; return; }
        if (!matchesProfile)
        {
            Status = "Scene gripper unchanged: no Pika matching this robot profile.";
            return;
        }
        if (pika?["fresh"] is not JsonValue fresh || !fresh.TryGetValue<bool>(out bool current) || !current ||
            pika["telemetry"]?["motor"]?["Position"] is not JsonValue motor || !motor.TryGetValue<double>(out double angle) ||
            !double.IsFinite(angle) || (!PikaProtocol.ValidAngle(angle) && (angle >= 0 || angle < MinimumFeedbackAngle)))
        {
            Status = "Scene gripper unchanged: waiting for fresh Pika motor position.";
            return;
        }
        double width = PikaProtocol.Width(angle);
        // Closed encoder noise is not a negative opening. Only this bounded
        // near-zero feedback allowance is saturated; model mismatch is rejected.
        if (width >= -ClosedNoiseToleranceMm && width < 0) width = 0;
        if (!double.IsFinite(width) || width < 0 || width > maximumMm)
        {
            Status = $"Scene gripper unchanged: measured opening {width:F1} mm is outside the model's 0–{maximumMm:F1} mm range. Check the tool model/calibration.";
            return;
        }
        // The canonical Pika slides are open at q=0. Their signed range
        // endpoints bring the two inner jaw faces together. Opening therefore
        // decreases slide travel; using width directly inverts the real jaws.
        double closingTravelMetres = (maximumMm - width) / 2000;
        joints["joint7"] = firstSign * closingTravelMetres;
        joints["joint8"] = -firstSign * closingTravelMetres;
        Status = $"Scene gripper linked · measured {width:F1} mm / model {maximumMm:F1} mm";
    }

    public void Verify(JsonObject requested, JsonArray pose)
    {
        ErrorMm = null;
        if (requested["joint7"] is null || requested["joint8"] is null) return;
        double maximum = 0;
        foreach (string name in new[] { "joint7", "joint8" })
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

    private static bool Finger(JsonArray joints, string name, out int sign, out double travel)
    {
        sign = 0; travel = 0;
        var matches = joints.OfType<JsonObject>().Where(j => j["name"]?.GetValue<string>() == name).ToArray();
        if (matches.Length != 1 || matches[0]["unit"]?.GetValue<string>() != "m" || matches[0]["limited"]?.GetValue<bool>() != true ||
            matches[0]["min"] is not JsonValue low || !low.TryGetValue<double>(out double minimum) ||
            matches[0]["max"] is not JsonValue high || !high.TryGetValue<double>(out double maximum) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum)) return false;
        if (Math.Abs(maximum) < 1e-9 && minimum < 0) { sign = -1; travel = -minimum; }
        else if (Math.Abs(minimum) < 1e-9 && maximum > 0) { sign = 1; travel = maximum; }
        return travel > 0 && travel <= .05 + 1e-9;
    }
}
