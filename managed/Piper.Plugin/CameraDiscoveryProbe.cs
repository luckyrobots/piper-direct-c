using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

/// <summary>
/// A background read that runs at most once per interval, never twice at once,
/// and leaves its result for the caller to apply on its own thread.
/// States: Idle -> Reading -> Idle, with at most one pending result.
/// A read that returns null (failed or superseded) leaves nothing pending.
/// </summary>
internal sealed class ThrottledProbe<T>(TimeSpan interval) where T : class
{
    private long attemptedAt;
    private Task? reading;
    private T? pending;

    public Task? Reading => reading;
    public bool HasPending => Volatile.Read(ref pending) is not null;

    /// <summary>Starts a read unless one is running or the interval has not elapsed (unless forced).</summary>
    public void Queue(Func<Task<T?>> read, bool force = false)
    {
        if (reading is { IsCompleted: false } || !force && attemptedAt != 0 && Stopwatch.GetElapsedTime(attemptedAt) < interval) return;
        attemptedAt = Stopwatch.GetTimestamp();
        reading = Read(read);
    }

    private async Task Read(Func<Task<T?>> read)
    {
        if (await read() is { } result) Interlocked.Exchange(ref pending, result);
    }

    /// <summary>Takes the pending result, if any; a later read replaces an untaken one.</summary>
    public T? Take() => Interlocked.Exchange(ref pending, null);

    /// <summary>Waits for an in-flight read and drops its result.</summary>
    public async Task Retire()
    {
        if (reading is { } task) { try { await task; } catch (OperationCanceledException) { } }
        Take();
    }
}
