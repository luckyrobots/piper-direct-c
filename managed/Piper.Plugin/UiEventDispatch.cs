using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-owned serial UI actor. Enqueue never awaits the previous action, so the
/// SDK reader can deliver STOP through its separate immediate handler.</summary>
public sealed class UiEventDispatch : IAsyncDisposable
{
    private sealed record Pending(UiEvent Event, long Epoch, bool PermissionBound, bool WakeOnly = false, bool Urgent = false);
    private readonly Channel<Pending> queue;
    private readonly CancellationTokenSource stopping;
    private readonly Func<UiEvent, CancellationToken, Task> handler;
    private readonly Func<long> currentEpoch;
    private readonly Func<UiEvent, bool> permissionBound;
    private readonly Action<Exception> reportError;
    private readonly Action<UiEvent, string>? discarded;
    private readonly object gate = new();
    private readonly Task worker;
    private CancellationTokenSource? active;
    private bool activePermissionBound, activeUrgent, closed, disposed;
    private long activeEpoch;
    private Task? stopTask;
    private Pending? urgent;
    private readonly TimeSpan actionTimeout;

    public UiEventDispatch(Func<UiEvent, CancellationToken, Task> handler, Func<long> currentEpoch,
        Func<UiEvent, bool> permissionBound, Action<Exception> reportError,
        CancellationToken cancellation = default, int capacity = 32,
        Action<UiEvent, string>? discarded = null, TimeSpan? actionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(currentEpoch);
        ArgumentNullException.ThrowIfNull(permissionBound);
        ArgumentNullException.ThrowIfNull(reportError);
        if (capacity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.handler = handler; this.currentEpoch = currentEpoch; this.permissionBound = permissionBound;
        this.reportError = reportError; this.discarded = discarded;
        this.actionTimeout = actionTimeout ?? TimeSpan.FromSeconds(60);
        if (this.actionTimeout <= TimeSpan.Zero || this.actionTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(actionTimeout));
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        worker = Task.Run(RunAsync);
    }

    /// <summary>Returns false when full/stopped. The caller can report that the gesture was not
    /// accepted. No producer waits or unbounded task is created for a queued event.</summary>
    public bool Enqueue(UiEvent input, long capturedEpoch)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            if (closed || stopping.IsCancellationRequested) return false;
        }
        bool guarded = permissionBound(input);
        // The SDK is free to retire its deserialization document when this call returns.
        UiEvent owned = input with { Value = input.Value.ValueKind == JsonValueKind.Undefined ? default : input.Value.Clone() };
        lock (gate)
        {
            if (closed || stopping.IsCancellationRequested) return false;
            return queue.Writer.TryWrite(new(owned, capturedEpoch, guarded));
        }
    }

    /// <summary>Retire a pending operation before a power-release request. The same
    /// worker still owns execution, so cancelled commands drain before Disable runs.</summary>
    public bool EnqueueUrgent(UiEvent input, long capturedEpoch)
    {
        ArgumentNullException.ThrowIfNull(input);
        UiEvent owned = input with { Value = input.Value.ValueKind == JsonValueKind.Undefined ? default : input.Value.Clone() };
        CancellationTokenSource? cancel;
        lock (gate)
        {
            if (closed || stopping.IsCancellationRequested || urgent is not null || activeUrgent) return false;
            urgent = new(owned, capturedEpoch, permissionBound(input), Urgent: true);
            cancel = active;
            // A full channel already wakes the worker. With an empty channel this
            // marker wakes it without executing a second copy of the urgent event.
            queue.Writer.TryWrite(new(owned, capturedEpoch, false, WakeOnly: true));
        }
        Cancel(cancel);
        return true;
    }

    private bool TryTake(out Pending? pending)
    {
        lock (gate)
        {
            if (urgent is not null) { pending = urgent; urgent = null; return true; }
        }
        return queue.Reader.TryRead(out pending);
    }

    /// <summary>Call after advancing the control-permission epoch, before immediate STOP.
    /// Queued controls are also checked at dequeue; preference/navigation events remain eligible.</summary>
    public void InvalidateControlEvents()
    {
        long epoch = currentEpoch();
        CancellationTokenSource? cancel;
        lock (gate) cancel = activePermissionBound && activeEpoch != epoch ? active : null;
        Cancel(cancel);
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null) return stopTask;
            closed = true;
            queue.Writer.TryComplete();
            // Cancellation callbacks must never run while gate is held. A handler
            // must honor this token; release of motor ownership awaits the worker.
            stopTask = Task.Run(async () => { Cancel(stopping); await worker.ConfigureAwait(false); });
            return stopTask;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stopping.Token).ConfigureAwait(false))
            {
                while (TryTake(out Pending? pending))
                {
                    if (pending!.WakeOnly) continue;
                    using var actionDeadline = new CancellationTokenSource(actionTimeout);
                    using var action = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, actionDeadline.Token);
                    bool reject, preempt;
                    lock (gate)
                    {
                        reject = closed || stopping.IsCancellationRequested;
                        preempt = !pending.Urgent && urgent is not null;
                        if (!reject)
                        {
                            active = action; activePermissionBound = pending.PermissionBound; activeEpoch = pending.Epoch;
                            activeUrgent = pending.Urgent;
                        }
                    }
                    if (reject) continue;
                    if (preempt) Cancel(action);
                    try
                    {
                        if (pending.PermissionBound && pending.Epoch != currentEpoch())
                        {
                            discarded?.Invoke(pending.Event, "Control permission changed; queued action discarded.");
                            continue;
                        }
                        action.Token.ThrowIfCancellationRequested();
                        await handler(pending.Event, action.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (action.IsCancellationRequested)
                    {
                        // Timeout is a user-visible failure; an urgent request or
                        // permission change deliberately retires the old action.
                        if (actionDeadline.IsCancellationRequested && !stopping.IsCancellationRequested && pending.Epoch == currentEpoch())
                            Report(new TimeoutException($"The action did not finish within {actionTimeout.TotalSeconds:0} seconds. No previous movement will resume; use Disable or retry the connection."));
                    }
                    catch (Exception error) { Report(error); }
                    finally
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(active, action)) { active = null; activePermissionBound = activeUrgent = false; }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            lock (gate) { closed = true; urgent = null; }
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) { }
        }
    }

    private void Cancel(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { } // The worker completed the same action concurrently.
        catch (Exception error) { Report(error); }
    }

    private void Report(Exception error)
    {
        try { reportError(error); }
        catch (Exception reportingError) { Trace.TraceError("UI action/error reporting failed: {0}; {1}", error, reportingError); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stopping.Dispose();
        }
    }
}
