using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationPublishTiming(long Sequence, long StartedMonotonicNs, double? IntervalMilliseconds,
    double SemaphoreWaitMilliseconds, double? ArmAcknowledgementMilliseconds, double? PikaAcknowledgementMilliseconds,
    double? CompletionMilliseconds, string? ArmErrorType, string? PikaErrorType,
    int Gen0Collections, int Gen1Collections, int Gen2Collections, long TotalAllocatedBytes);
public sealed record SimulationPublishAudit(double MaximumIntervalMilliseconds, double MaximumSemaphoreWaitMilliseconds,
    double MaximumArmAcknowledgementMilliseconds, double MaximumPikaAcknowledgementMilliseconds,
    IReadOnlyList<SimulationPublishTiming> RecentPublications, int ProcessId, string ClockDomain);

/// <summary>Bounded transport timings only; no target values, owners or tokens.
/// Metrics cannot renew a lease or change target scheduling.</summary>
public sealed class SimulationPublishDiagnostics
{
    private readonly object gate = new();
    private readonly Queue<Attempt> recent = new();
    private long previousStart;
    private double maxInterval, maxWait, maxArm, maxPika;
    public sealed class Attempt
    {
        internal readonly long Sequence, Started;
        internal readonly double? Interval;
        internal readonly double Wait;
        internal double? Arm, Pika, Completed;
        internal string? ArmError, PikaError;
        internal readonly int Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2);
        internal readonly long Allocated = GC.GetTotalAllocatedBytes(precise: false);
        internal Attempt(long sequence, long started, double? interval, double wait) { Sequence = sequence; Started = started; Interval = interval; Wait = wait; }
        internal SimulationPublishTiming Snapshot() => new(Sequence, (long)(Started * (1e9 / Stopwatch.Frequency)), Interval, Wait, Arm, Pika, Completed, ArmError, PikaError, Gen0, Gen1, Gen2, Allocated);
    }
    public Attempt Begin(long sequence, long started, double waitMilliseconds)
    {
        lock (gate)
        {
            double? interval = previousStart > 0 ? Stopwatch.GetElapsedTime(previousStart, started).TotalMilliseconds : null;
            if (interval.HasValue) maxInterval = Math.Max(maxInterval, interval.Value);
            maxWait = Math.Max(maxWait, waitMilliseconds); previousStart = started;
            var attempt = new Attempt(sequence, started, interval, waitMilliseconds); recent.Enqueue(attempt);
            while (recent.Count > 16) recent.Dequeue();
            return attempt;
        }
    }
    public async Task Observe(Attempt attempt, Task operation, bool arm)
    {
        string? error = null;
        try { await operation; }
        catch (Exception failure) { error = failure.GetType().Name; throw; }
        finally
        {
            double elapsed = Stopwatch.GetElapsedTime(attempt.Started).TotalMilliseconds;
            lock (gate)
            {
                if (arm) { attempt.Arm = elapsed; attempt.ArmError = error; maxArm = Math.Max(maxArm, elapsed); }
                else { attempt.Pika = elapsed; attempt.PikaError = error; maxPika = Math.Max(maxPika, elapsed); }
            }
        }
    }
    public void Complete(Attempt attempt) { lock (gate) attempt.Completed = Stopwatch.GetElapsedTime(attempt.Started).TotalMilliseconds; }
    public SimulationPublishAudit Snapshot() { lock (gate) return new(maxInterval, maxWait, maxArm, maxPika, Array.AsReadOnly(recent.Select(a => a.Snapshot()).ToArray()), Environment.ProcessId, "client-stopwatch"); }
}
