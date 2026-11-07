namespace LuckyRobots.Piper.Plugin;

/// <summary>Canonical Pika zero-slide geometry is open; signed travel closes the gap, including models with a smaller allowed opening.</summary>
public sealed class EpisodeReplayGripperMapping
{
    private const double PositionEpsilonMetres = 1e-9;
    private readonly int firstSign;
    private readonly double minimumTravelMetres;
    private readonly double travelMetres;
    public double GeometricZeroOpeningMm => travelMetres * 2000;
    public double MaximumOpeningMm => (travelMetres - minimumTravelMetres) * 2000;
    public double MaximumAsymmetryMm { get; }

    public EpisodeReplayGripperMapping(double firstMinimumMetres, double firstMaximumMetres, double secondMinimumMetres, double secondMaximumMetres,
        double maximumAsymmetryMm = 1)
    {
        (int first, double firstOpen, double firstTravel) = Range(firstMinimumMetres, firstMaximumMetres);
        (int second, double secondOpen, double secondTravel) = Range(secondMinimumMetres, secondMaximumMetres);
        if (first == second || Math.Abs(firstTravel - secondTravel) > PositionEpsilonMetres || Math.Abs(firstOpen - secondOpen) > PositionEpsilonMetres ||
            !double.IsFinite(maximumAsymmetryMm) || maximumAsymmetryMm is < 0 or > 1)
            throw new InvalidOperationException("Pika opening requires opposite equal slide ranges in metres.");
        firstSign = first;
        minimumTravelMetres = Math.Max(firstOpen, secondOpen);
        travelMetres = Math.Min(firstTravel, secondTravel);
        MaximumAsymmetryMm = maximumAsymmetryMm;
    }

    public double OpeningMm(double firstPositionMetres, double secondPositionMetres)
    {
        double firstTravel = firstPositionMetres * firstSign;
        double secondTravel = secondPositionMetres * -firstSign;
        if (!double.IsFinite(firstTravel) || !double.IsFinite(secondTravel) || firstTravel < minimumTravelMetres - PositionEpsilonMetres ||
            secondTravel < minimumTravelMetres - PositionEpsilonMetres || firstTravel > travelMetres + PositionEpsilonMetres || secondTravel > travelMetres + PositionEpsilonMetres ||
            Math.Abs(firstTravel - secondTravel) > MaximumAsymmetryMm / 1000 + PositionEpsilonMetres)
            throw new InvalidOperationException("Finger states are asymmetric or outside their verified opposed-slide ranges; raw slide angles cannot be replayed as an opening.");
        // Remove at most 0.000002 mm of endpoint floating-point error, not physical range overshoot.
        return Math.Clamp(GeometricZeroOpeningMm - 1000 * (firstTravel + secondTravel), 0, MaximumOpeningMm);
    }

    private static (int Sign, double MinimumTravel, double MaximumTravel) Range(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            throw new InvalidOperationException("Pika slide limits must be finite metres.");
        if (maximum <= 0 && minimum is >= -.05 and < 0 && minimum < maximum)
            return (-1, -maximum, -minimum);
        if (minimum >= 0 && maximum is > 0 and <= .05 && minimum < maximum)
            return (1, minimum, maximum);
        throw new InvalidOperationException("Pika slides need matching signed intervals and at most 50 mm of closing travel from geometric zero.");
    }
}
