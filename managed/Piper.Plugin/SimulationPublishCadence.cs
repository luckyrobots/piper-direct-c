namespace LuckyRobots.Piper.Plugin;

/// <summary>Paces fresh publications on a monotonic deadline grid. Request
/// acknowledgement time consumes the period instead of being added to it;
/// expired deadlines are skipped and are never replayed as catch-up writes.</summary>
public sealed class SimulationPublishCadence
{
    private readonly long frequency, periodTicks;
    private long due, lastCompletion;
    public long SkippedDeadlines { get; private set; }

    public SimulationPublishCadence(long firstPublicationTicks, long timestampFrequency, TimeSpan period)
    {
        double ticks = period.TotalSeconds * timestampFrequency;
        if (firstPublicationTicks < 0 || timestampFrequency <= 0 || !double.IsFinite(ticks) || ticks < 1 || ticks > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(period), "Publication pacing needs a positive representable monotonic period.");
        frequency = timestampFrequency;
        periodTicks = checked((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        due = lastCompletion = firstPublicationTicks;
    }

    public TimeSpan DelayAfterPublication(long completedTicks)
    {
        if (completedTicks < lastCompletion)
            throw new InvalidOperationException("The physical publication clock moved backwards.");
        lastCompletion = completedTicks;
        due = checked(due + periodTicks);
        if (due < completedTicks)
        {
            long missed = checked(1 + (completedTicks - due - 1) / periodTicks);
            due = checked(due + checked(missed * periodTicks));
            SkippedDeadlines = checked(SkippedDeadlines + missed);
        }
        return TimeSpan.FromSeconds((due - completedTicks) / (double)frequency);
    }

    internal Task WaitAfterPublication(TimeProvider clock, CancellationToken cancellation) =>
        Task.Delay(DelayAfterPublication(clock.GetTimestamp()), clock, cancellation);
}
