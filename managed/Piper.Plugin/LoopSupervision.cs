using Grpc.Core;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The background-loop part of the Shell component: keeps each loop
/// running until shutdown and counts the times one ended on its own.
/// <code>
///   field         written by                                  readers see
///   worker        plugin start (Started), once                the task that ends when every loop
///                                                             has ended, or null before start
///   faults        the supervised loops (Supervise), by an     a count that only grows
///                 interlocked increment
///   RestartDelay  tests only, before a loop is supervised     the pause before a loop restarts
/// </code>
/// Not owned here: the loops themselves, and what a fault means for the robot
/// (the caller's <c>fault</c> callback withdraws motion permission and tells
/// the operator).</summary>
internal sealed class LoopSupervision
{
    private Task? worker;
    private int faults;

    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How many times a supervised loop ended outside shutdown.</summary>
    public int Faults => Volatile.Read(ref faults);
    public Task? Worker => Volatile.Read(ref worker);

    /// <summary>Records the plugin's running loops. They are started once.</summary>
    public void Started(Task loops)
    {
        ArgumentNullException.ThrowIfNull(loops);
        if (Interlocked.CompareExchange(ref worker, loops, null) is not null)
            throw new InvalidOperationException("The background loops are already running.");
    }

    /// <summary>Runs one loop until <paramref name="cancellation"/>. A loop that
    /// faults, or returns before then, is counted, reported through
    /// <paramref name="fault"/> (name, cause) and started again after
    /// <see cref="RestartDelay"/>.</summary>
    public async Task Supervise(string name, Func<CancellationToken, Task> loop, Action<string, string> fault, CancellationToken cancellation)
    {
        while (true)
        {
            string cause;
            try
            {
                await loop(cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) return;
                cause = "ended unexpectedly";
            }
            catch (Exception error) when (cancellation.IsCancellationRequested &&
                error is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled }) { return; }
            catch (Exception error) { cause = "failed: " + error.Message; }
            Interlocked.Increment(ref faults);
            fault(name, cause);
            try { await Task.Delay(RestartDelay, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
