namespace LuckyRobots.Piper.Plugin;

/// <summary>The STOP follow-up part of the Shell component: the detached work
/// that runs after a STOP has been sent. At most one pass runs and one waits.
/// A STOP that arrives while a pass is waiting is covered by that pass, so
/// repeated STOP clicks cannot pile up work.
/// <code>
///   field   written by                                       readers see
///   tail    a STOP handler (Queue), under gate               the task that ends when every
///                                                            queued pass has ended
///   group   a STOP handler (Queue) sets it; the waiting      (not read outside)
///   queued  pass clears both when it starts, under gate
/// </code>
/// <see cref="Queue"/> is called only after the STOP request has been answered
/// or has timed out. It holds <c>gate</c> for a few assignments and never
/// while a pass runs, so one STOP cannot delay the next.
///
/// Not owned here: what a pass does (the caller's delegate, which must not
/// fault).</summary>
internal sealed class StopFollowUpQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;
    private bool group, queued;

    /// <summary>Completes when every queued pass has ended.</summary>
    public Task Idle { get { lock (gate) return tail; } }

    /// <summary>Asks for one more pass. <paramref name="pass"/> is told whether
    /// any STOP it covers was sent while a follower group was active.</summary>
    public void Queue(bool groupActive, Func<bool, Task> pass)
    {
        lock (gate)
        {
            group |= groupActive;
            if (queued) return;
            queued = true;
            Task previous = tail;
            tail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                bool covered;
                lock (gate) { covered = group; group = queued = false; }
                await pass(covered).ConfigureAwait(false);
            });
        }
    }
}
