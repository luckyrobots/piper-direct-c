namespace LuckyRobots.Piper.Pika;

/// <summary>Decides that the jaw has stopped from its measured opening over a short window.
/// <para>The gripper's per-sample speed estimate is too noisy for this: with the jaw at rest
/// it jitters up to about 0.12 rad/s, six times
/// <see cref="PikaProtocol.StationarySpeedRadiansPerSecond"/>, and exceeds that bound in
/// roughly a third of samples at the ~100 Hz feedback rate. A single-sample speed test
/// therefore confirmed stillness only when the noise happened to stay quiet long enough,
/// and an arrived target could reach its movement deadline and be disabled.
/// The measured opening is steady instead: at rest it varied by about 0.2 mm within any
/// 150 ms window.</para>
/// <para>Still means the observations span at least <see cref="WindowSeconds"/> and all
/// openings in that span lie within <see cref="MaximumRangeMm"/>, i.e. the jaw averaged
/// under about 3 mm/s. <see cref="Reset"/> restarts the window.</para></summary>
public sealed class PikaStillness
{
    /// <summary>How long the opening must stay steady; the arrival dwell the agent always used.</summary>
    public const double WindowSeconds = .15;

    /// <summary>Largest opening spread within the window; about 2.4× the measured rest jitter.</summary>
    public const double MaximumRangeMm = .5;

    // Oldest first. Exactly one sample at or before the window start is kept, so a
    // window that is full reaches back at least WindowSeconds.
    private readonly List<(double At, double OpeningMm)> samples = [];

    public void Reset() => samples.Clear();

    /// <summary>Adds one measured opening and reports whether the jaw is still.</summary>
    public bool Observe(double at, double openingMm)
    {
        if (!double.IsFinite(at) || !double.IsFinite(openingMm) || samples.Count > 0 && at < samples[^1].At)
        {
            Reset();
            return false;
        }
        samples.Add((at, openingMm));
        while (samples.Count >= 2 && samples[1].At <= at - WindowSeconds) samples.RemoveAt(0);
        if (samples[0].At > at - WindowSeconds) return false;
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (var (_, opening) in samples) { low = Math.Min(low, opening); high = Math.Max(high, opening); }
        return high - low <= MaximumRangeMm;
    }
}
