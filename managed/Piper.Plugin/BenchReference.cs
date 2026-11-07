using System.Globalization;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Shared tabletop geometry; tool references and arm placement remain per robot.</summary>
public sealed record BenchReference(double Width, double Depth, double SurfaceY)
{
    public CalibrationDesk Apply(CalibrationDesk desk) => desk with { Width = Width, Depth = Depth, SurfaceY = SurfaceY };
    public static BenchReference? FromAnswers(IReadOnlyDictionary<string, string> answers, double surfaceY)
    {
        bool Read(string key, out double value) => double.TryParse(answers.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        if (!Read("width", out double width) || !Read("depth", out double depth) || width is < 100 or > 10000 || depth is < 100 or > 10000) return null;
        if (Read("sceney", out double measuredY) && measuredY is >= 0 and <= 3000) surfaceY = measuredY / 1000;
        return double.IsFinite(surfaceY) && surfaceY is >= 0 and <= 3 ? new(width / 1000, depth / 1000, surfaceY) : null;
    }
}

public sealed partial class PipersPlugin
{
    private void EnsureIndependentCalibration()
    {
        if (ActiveProfile.RobotJointPrefix.Length > 0)
            throw new InvalidOperationException("Desk alignment editing for this arm is unavailable because it would move both simulated arms. Its mounting position remains saved in the scene.");
    }
}
