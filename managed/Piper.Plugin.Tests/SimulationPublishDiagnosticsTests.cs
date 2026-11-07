using System.Diagnostics;
using LuckyRobots.Piper.Plugin;

internal static class SimulationPublishDiagnosticsTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var metrics = new SimulationPublishDiagnostics();
        long start = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var first = metrics.Begin(1, start, 4);
        var frozen = metrics.Snapshot();
        await metrics.Observe(first, Task.CompletedTask, arm: true);
        try { await metrics.Observe(first, Task.FromException(new InvalidOperationException("secret authorization value")), arm: false); }
        catch (InvalidOperationException) { }
        metrics.Complete(first);
        var sample = metrics.Snapshot();
        check(sample.RecentPublications.Count == 1 && sample.RecentPublications[0].ArmAcknowledgementMilliseconds is > 0 &&
            sample.RecentPublications[0].PikaErrorType == nameof(InvalidOperationException) && sample.RecentPublications[0].CompletionMilliseconds is > 0,
            "Per-channel ACK latency/error evidence was lost on failed coordinated publication");
        check(frozen.RecentPublications[0].CompletionMilliseconds is null && frozen.RecentPublications[0].ArmAcknowledgementMilliseconds is null,
            "A retained timing snapshot was mutated by later ACK/cleanup completion");
        for (int sequence = 2; sequence <= 40; sequence++) metrics.Begin(sequence, start + sequence * Stopwatch.Frequency / 10, sequence);
        sample = metrics.Snapshot();
        check(sample.RecentPublications.Count == 16 && sample.RecentPublications[0].Sequence == 25 && sample.RecentPublications[^1].Sequence == 40 &&
            Math.Abs(sample.MaximumIntervalMilliseconds - 200) < .01 && sample.MaximumSemaphoreWaitMilliseconds == 40,
            "Publication history was unbounded or omitted host scheduling/semaphore intervals");
        check(!System.Text.Json.JsonSerializer.Serialize(sample).Contains("secret"), "Transport timings copied backend error text or authorization values");
    }
}
