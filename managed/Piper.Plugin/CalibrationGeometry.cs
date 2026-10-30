using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public readonly record struct CalibrationPoint(double X, double Y, double Z)
{
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Distance(CalibrationPoint other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2) + Math.Pow(Z - other.Z, 2));
    public double PlanarDistance(CalibrationPoint other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Z - other.Z, 2));
    public JsonArray Json() => new(X, Y, Z);
}

public sealed record CalibrationPlacement(CalibrationPoint Position, double[] Rotation)
{
    public double YawRadians => Math.Atan2(2 * Rotation[1] * Rotation[3], 1 - 2 * Rotation[1] * Rotation[1]);
    public JsonObject Json() => new() { ["position"] = Position.Json(), ["rotation"] = new JsonArray(Rotation.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["unit"] = "m" };
    public static CalibrationPlacement Read(JsonNode? node)
    {
        if (node?["unit"]?.GetValue<string>() != "m" || node["position"] is not JsonArray { Count: 3 } position || node["rotation"] is not JsonArray { Count: 4 } rotation)
            throw new InvalidOperationException("The engine must report metric world rootPlacement and a quaternion; update the engine plugin host.");
        var value = new CalibrationPlacement(new(position[0]!.GetValue<double>(), position[1]!.GetValue<double>(), position[2]!.GetValue<double>()), rotation.Select(x => x!.GetValue<double>()).ToArray());
        if (!value.Position.Finite || value.Rotation.Any(x => !double.IsFinite(x)) || Math.Abs(value.Rotation.Sum(x => x * x) - 1) > 1e-5 ||
            Math.Abs(value.Rotation[0]) > 1e-5 || Math.Abs(value.Rotation[2]) > 1e-5)
            throw new InvalidOperationException("Base calibration requires a flat, unit-scale robot root: only world Y yaw and translation are supported.");
        return value;
    }
    public bool Same(CalibrationPlacement other) => Position.Distance(other.Position) <= 1e-6 &&
        Math.Abs(Math.Abs(Rotation.Zip(other.Rotation).Sum(pair => pair.First * pair.Second)) - 1) <= 1e-6;
    public static CalibrationPlacement At(CalibrationPoint point, double yaw) => new(point, [0, Math.Sin(yaw / 2), 0, Math.Cos(yaw / 2)]);
}

public sealed record CalibrationDesk(double Width = 1.4, double Depth = .7, double SurfaceY = .75, double PrecisionMm = 10, string ToolPoint = "grasp_site")
{
    public void Validate()
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Depth) || !double.IsFinite(SurfaceY) || !double.IsFinite(PrecisionMm) ||
            Width is < .1 or > 10 || Depth is < .1 or > 10 || SurfaceY is < 0 or > 3 || PrecisionMm is < 1 or > 10 ||
            string.IsNullOrWhiteSpace(ToolPoint) || ToolPoint.Length > 80 || ToolPoint.Any(char.IsControl))
            throw new InvalidOperationException("Enter desk dimensions in metres, surface height 0–3 m, measurement precision 1–10 mm and the exact named TCP.");
    }
    public CalibrationPoint World(double leftMm, double backMm, double heightMm)
    {
        Validate();
        if (!double.IsFinite(leftMm) || !double.IsFinite(backMm) || !double.IsFinite(heightMm) || leftMm < 0 || leftMm > Width * 1000 ||
            backMm < 0 || backMm > Depth * 1000 || heightMm is < 0 or > 1000)
            throw new InvalidOperationException("Measure from the desk's left and back edges in millimetres; the point must be on the desk footprint, 0–1000 mm above its surface.");
        // Engine Y-up desk frame, centred tabletop, back edge at negative Z.
        return new(-Width / 2 + leftMm / 1000, SurfaceY + heightMm / 1000, -Depth / 2 + backMm / 1000);
    }
}

public sealed record CalibrationMeasurement(string Mode = "edge measurements", double LeftMm = 700, double BackMm = 350, double HeightMm = 0,
    double SquareMm = 50, double OriginLeftMm = 0, double OriginBackMm = 0, int Column = 0, int Row = 0)
{
    public CalibrationPoint World(CalibrationDesk desk)
    {
        if (Mode == "grid intersections")
        {
            if (!double.IsFinite(SquareMm) || SquareMm is < 5 or > 500 || Column is < 0 or > 200 || Row is < 0 or > 200)
                throw new InvalidOperationException("Use a measured grid square size of 5–500 mm and non-negative numbered intersections.");
            return desk.World(OriginLeftMm + Column * SquareMm, OriginBackMm + Row * SquareMm, HeightMm);
        }
        if (Mode != "edge measurements") throw new InvalidOperationException("Choose edge measurements or grid intersections.");
        return desk.World(LeftMm, BackMm, HeightMm);
    }
}

public sealed record CalibrationSample(string Name, CalibrationPoint MeasuredWorld, CalibrationPoint SceneWorld, double[] JointDegrees,
    long HostUnixMilliseconds, bool HeldOut, CalibrationMeasurement Measurement, string PoseFingerprint);
public sealed record CalibrationResidual(string Name, bool HeldOut, double PlanarMm, double VerticalMm, double TotalMm);
public sealed record CalibrationFit(bool CanApply, string Reason, CalibrationPlacement ProposedPlacement, double YawDeltaRadians,
    double TrainingRmsMm, double TrainingMaxMm, double ValidationMaxMm, double VerticalRmsMm, CalibrationResidual[] Residuals);

/// <summary>Rigid world-Y yaw/XZ translation only. Never fits joint zero, scale, tilt or tool offset.</summary>
public static class CalibrationSolver
{
    public static CalibrationPoint Rotate(CalibrationPoint point, double yaw) =>
        new(Math.Cos(yaw) * point.X + Math.Sin(yaw) * point.Z, point.Y, -Math.Sin(yaw) * point.X + Math.Cos(yaw) * point.Z);

    public static CalibrationFit Fit(CalibrationDesk desk, CalibrationPlacement original, IReadOnlyList<CalibrationSample> samples)
    {
        desk.Validate();
        if (samples.Count > 24 || samples.Any(s => !s.MeasuredWorld.Finite || !s.SceneWorld.Finite))
            throw new InvalidOperationException("Calibration needs at most 24 finite measured point pairs.");
        var training = samples.Where(s => !s.HeldOut).ToArray();
        var heldOut = samples.Where(s => s.HeldOut).ToArray();
        CalibrationFit Reject(string reason) => new(false, reason, original, 0, 0, 0, 0, 0, []);
        if (training.Length < 4) return Reject("Capture at least four training points spread across the reachable desk, then a separate validation point.");
        if (training.Where((p, i) => training.Take(i).Any(q => p.MeasuredWorld.PlanarDistance(q.MeasuredWorld) < .02)).Any())
            return Reject("Training needs four distinct desk locations at least 2 cm apart; repeated readings at one point are not extra geometric constraints.");
        if (!Spread(training.Select(s => s.MeasuredWorld).ToArray()) || !Spread(training.Select(s => s.SceneWorld).ToArray()))
            return Reject("Training points are too close or nearly collinear. Spread them at least 10 cm in two desk directions; use a rectangle rather than one line.");
        double sx = training.Average(s => s.SceneWorld.X), sz = training.Average(s => s.SceneWorld.Z);
        double px = training.Average(s => s.MeasuredWorld.X), pz = training.Average(s => s.MeasuredWorld.Z);
        double a = training.Sum(s => (s.SceneWorld.X - sx) * (s.MeasuredWorld.X - px) + (s.SceneWorld.Z - sz) * (s.MeasuredWorld.Z - pz));
        double b = training.Sum(s => (s.SceneWorld.Z - sz) * (s.MeasuredWorld.X - px) - (s.SceneWorld.X - sx) * (s.MeasuredWorld.Z - pz));
        double yaw = Math.Atan2(b, a);
        var centre = Rotate(new(sx, 0, sz), yaw);
        double tx = px - centre.X, tz = pz - centre.Z;
        var rotatedBase = Rotate(original.Position, yaw);
        var proposed = CalibrationPlacement.At(new(rotatedBase.X + tx, desk.SurfaceY, rotatedBase.Z + tz), original.YawRadians + yaw);
        var residuals = samples.Select(s =>
        {
            var rotated = Rotate(s.SceneWorld, yaw);
            var prediction = new CalibrationPoint(rotated.X + tx, s.SceneWorld.Y + desk.SurfaceY - original.Position.Y, rotated.Z + tz);
            return new CalibrationResidual(s.Name, s.HeldOut, prediction.PlanarDistance(s.MeasuredWorld) * 1000,
                (prediction.Y - s.MeasuredWorld.Y) * 1000, prediction.Distance(s.MeasuredWorld) * 1000);
        }).ToArray();
        double rms = Math.Sqrt(residuals.Where(r => !r.HeldOut).Average(r => r.TotalMm * r.TotalMm));
        double max = residuals.Where(r => !r.HeldOut).Max(r => r.TotalMm);
        double validationMax = heldOut.Length == 0 ? 0 : residuals.Where(r => r.HeldOut).Max(r => r.TotalMm);
        double vertical = Math.Sqrt(residuals.Average(r => r.VerticalMm * r.VerticalMm));
        string reason = "Base yaw/XZ fit passes the entered measurement precision and independent validation. Camera calibration and dataset timing remain unverified.";
        if (heldOut.Length == 0) reason = "Fit preview only. Capture at least one new validation point that was not used in the fit.";
        else if (heldOut.Any(h => training.Min(t => h.MeasuredWorld.PlanarDistance(t.MeasuredWorld)) < .05))
            reason = "Validation must use a new desk point at least 5 cm from every training point; reusing a fitted point is not independent validation.";
        else if (vertical > desk.PrecisionMm || residuals.Any(r => Math.Abs(r.VerticalMm) > desk.PrecisionMm * 2))
            reason = "Height disagreement is too large. Check the named contact point, desk height, robot model and joint-zero mapping. The solver will not lift or tilt the base to hide this error.";
        else if (rms > desk.PrecisionMm || max > desk.PrecisionMm * 2)
            reason = "Training fit is inconsistent. Remeasure points; check TCP, model and joint offsets. A base shift cannot repair inconsistent joint geometry.";
        else if (validationMax > desk.PrecisionMm * 2)
            reason = "The independent validation point failed. Check TCP/model/joint mapping and collect better points; do not apply this placement.";
        else if (Math.Abs(proposed.Position.X) > desk.Width / 2 || Math.Abs(proposed.Position.Z) > desk.Depth / 2)
            reason = "The proposed base centre lies outside the desk. Verify desk orientation, edge measurements and tool identity before proceeding.";
        bool pass = heldOut.Length > 0 && reason.StartsWith("Base yaw/XZ fit passes", StringComparison.Ordinal);
        return new(pass, reason, proposed, yaw, rms, max, validationMax, vertical, residuals);
    }

    private static bool Spread(CalibrationPoint[] points)
    {
        double span = points.Max(p => points.Max(q => p.PlanarDistance(q)));
        double x = points.Average(p => p.X), z = points.Average(p => p.Z);
        double xx = points.Sum(p => Math.Pow(p.X - x, 2)), zz = points.Sum(p => Math.Pow(p.Z - z, 2));
        double xz = points.Sum(p => (p.X - x) * (p.Z - z));
        return span >= .1 && xx + zz > 0 && (xx * zz - xz * xz) / Math.Pow(xx + zz, 2) >= .01;
    }
}
