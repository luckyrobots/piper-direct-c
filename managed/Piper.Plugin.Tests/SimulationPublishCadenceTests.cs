using LuckyRobots.Piper.Plugin;

internal static class SimulationPublishCadenceTests
{
    public static void Run(Action<bool, string> check)
    {
        const long frequency = 1_000_000;
        static long Tick(double milliseconds) => (long)Math.Round(milliseconds * 1000);
        static bool Milliseconds(TimeSpan actual, double expected) => Math.Abs(actual.TotalMilliseconds - expected) < .0001;
        var cadence = new SimulationPublishCadence(Tick(1000), frequency, TimeSpan.FromMilliseconds(30));
        check(Milliseconds(cadence.DelayAfterPublication(Tick(1005)), 25), "An ACK added a full extra period instead of consuming the first deadline");
        check(Milliseconds(cadence.DelayAfterPublication(Tick(1037)), 23), "Varying ACK duration drifted the publication deadline grid");
        check(Milliseconds(cadence.DelayAfterPublication(Tick(1061)), 29), "A short ACK did not retain the same monotonic cadence");
        check(cadence.SkippedDeadlines == 0, "Ordinary request durations were mistaken for skipped writes");

        var overrun = new SimulationPublishCadence(0, frequency, TimeSpan.FromMilliseconds(30));
        check(Milliseconds(overrun.DelayAfterPublication(Tick(95)), 25) && overrun.SkippedDeadlines == 3,
            "A long request replayed expired deadlines or waited an extra period after acknowledgement");
        check(Milliseconds(overrun.DelayAfterPublication(Tick(124)), 26) && overrun.SkippedDeadlines == 3,
            "An overrun created catch-up publications after the next fresh write");
        check(Milliseconds(overrun.DelayAfterPublication(Tick(180)), 0) && overrun.SkippedDeadlines == 3,
            "A publication completing exactly on a later deadline was unnecessarily delayed");
        check(Milliseconds(overrun.DelayAfterPublication(Tick(180)), 30),
            "A deadline-boundary completion allowed a burst of backlogged writes");
        var exactOverrun = new SimulationPublishCadence(0, frequency, TimeSpan.FromMilliseconds(30));
        check(Milliseconds(exactOverrun.DelayAfterPublication(Tick(90)), 0) && exactOverrun.SkippedDeadlines == 2,
            "An exact multi-period overrun queued old writes or discarded the current deadline");
        check(Milliseconds(exactOverrun.DelayAfterPublication(Tick(91)), 29),
            "An exact multi-period overrun replayed more than one fresh publication");

        var sustained = new SimulationPublishCadence(0, frequency, TimeSpan.FromMilliseconds(30));
        long now = 0;
        for (int frame = 0; frame < 100; frame++)
        {
            long acknowledgement = now + Tick(frame % 2 == 0 ? 2 : 12);
            TimeSpan delay = sustained.DelayAfterPublication(acknowledgement);
            now = acknowledgement + (long)Math.Round(delay.TotalSeconds * frequency);
            check(now == Tick((frame + 1) * 30), "Acknowledgement overhead accumulated into slower publication timing");
        }
        check(sustained.SkippedDeadlines == 0, "A healthy sustained publisher skipped fresh deadlines");
        bool backwardRejected = false;
        try { sustained.DelayAfterPublication(0); } catch (InvalidOperationException) { backwardRejected = true; }
        check(backwardRejected, "Clock reversal was accepted by physical publication pacing");
        foreach (var invalid in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1) })
        {
            bool rejected = false;
            try { _ = new SimulationPublishCadence(0, frequency, invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "A nonpositive physical publication period was accepted");
        }
    }
}
