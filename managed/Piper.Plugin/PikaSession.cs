using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

/// <summary>This plugin's relationship to the active profile's Pika session.
/// <code>
///   None --read-only connect--> Monitoring --explicit Enable--> Owned
///   None --explicit Enable--> Owned;  Owned --jaws freed for teaching--> Monitoring
///   Monitoring --teaching measured hold verified--> Owned
///   any --backend/telemetry loss, profile switch, restart, failed heartbeat--> None
/// </code></summary>
internal enum PikaSessionRole { None, Monitoring, Owned }

/// <summary>Plugin-F as one value: what the session is doing right now.
/// <code>
///   Idle --TryStart--> Connecting --attempt ends, role None--> Backoff --time--> Idle
///   Connecting --attempt connected read-only--> Monitoring
///   Idle | Backoff | Monitoring --explicit Enable verified--> Owned
///   Owned --jaws freed for teaching--> Monitoring;  Owned | Monitoring --Release--> Idle | Backoff
///   any --Stop--> Stopping (terminal: no further attempts)
/// </code></summary>
internal enum PikaSessionPhase { Idle, Backoff, Connecting, Monitoring, Owned, Stopping }

/// <summary>One read of the role: the role plus the revision that distinguishes it
/// from every later role write, including a release to the same role.</summary>
internal readonly record struct PikaRoleObservation(PikaSessionRole Role, long Revision);

/// <summary>One monitor outcome and the exact role observation it describes.</summary>
internal sealed record PikaMonitorOutcome(PikaRoleObservation Role, string Text);

/// <summary>One connect attempt and the only cancellation that can end it. A
/// retry never reuses an attempt, so cancelling a finished attempt cannot reach
/// the attempt that replaced it.</summary>
internal sealed class PikaMonitorAttempt
{
    // Never disposed: it holds no timer or wait handle, so Cancel stays safe after the worker ended.
    private readonly CancellationTokenSource cancel = new();
    private readonly TaskCompletionSource<Func<CancellationToken, Task>?> body = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Worker { get; }

    /// <summary>Creates the attempt without running it, so it can be published first.</summary>
    public PikaMonitorAttempt(CancellationToken lifetime) => Worker = RunAsync(lifetime);

    private async Task RunAsync(CancellationToken lifetime)
    {
        if (await body.Task.ConfigureAwait(false) is not { } run) return;
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancel.Token);
        await run(scope.Token).ConfigureAwait(false);
    }

    /// <summary>Runs the body on the thread pool; called once by the session that published this attempt.</summary>
    internal void Start(Func<CancellationToken, Task> run) => body.SetResult(run);
    /// <summary>Ends an attempt that lost the race to be published.</summary>
    internal void Abandon() => body.SetResult(null);

    public bool Running => !Worker.IsCompleted;

    /// <summary>Cancels this attempt (a no-op once it ended) and waits for it.</summary>
    public async Task CancelAsync()
    {
        await cancel.CancelAsync();
        await Worker;
    }
}

/// <summary>The whole of Plugin-F at one instant. The role and the monitor's
/// attempt are two coordinates of this one value, so no reader can combine a role
/// from one moment with an attempt or outcome from another. An attempt that
/// started from Monitoring keeps that role visible while it reconnects.</summary>
internal sealed record PikaSessionState(PikaSessionRole Role, long Revision, PikaMonitorAttempt? Attempt,
    long AttemptAt, bool Stopped, PikaMonitorOutcome? Outcome)
{
    public static readonly PikaSessionState Initial = new(PikaSessionRole.None, 0, null, 0, false, null);

    public PikaRoleObservation Observation => new(Role, Revision);
    /// <summary>An attempt is in flight. Its task completes once and never restarts.</summary>
    public bool Connecting => Attempt is { Running: true };
    public bool BackingOff(long nowTicks, TimeSpan backoff) => AttemptAt != 0 && Stopwatch.GetElapsedTime(AttemptAt, nowTicks) < backoff;

    public PikaSessionPhase Phase(long nowTicks, TimeSpan backoff) =>
        Stopped ? PikaSessionPhase.Stopping
        : Connecting ? PikaSessionPhase.Connecting
        : Role == PikaSessionRole.Owned ? PikaSessionPhase.Owned
        : Role == PikaSessionRole.Monitoring ? PikaSessionPhase.Monitoring
        : BackingOff(nowTicks, backoff) ? PikaSessionPhase.Backoff : PikaSessionPhase.Idle;

    /// <summary>Status text, derived rather than written: the monitor's last outcome
    /// while the role it was reported under still holds, otherwise the role.</summary>
    public string Status => Outcome is { } outcome && outcome.Role == Observation ? outcome.Text : Role switch
    {
        PikaSessionRole.Owned => "Pika control session active.",
        PikaSessionRole.Monitoring => "Pika monitoring only · motor power unchanged.",
        _ => "Pika telemetry starts read-only when the scene is linked."
    };
}

/// <summary>Plugin-F's single writer. The state is one immutable
/// <see cref="PikaSessionState"/> behind one reference; every transition is a
/// compare-and-swap of that reference, so no caller takes a lock or waits.
/// <code>
///   caller                                   thread                     transition
///   TryAdvance (Enable, teaching, restore,   UI actor, telemetry        role, only from the exact
///     the monitor's own attempt)             processing, attempt worker observation it read
///   Release (backend/telemetry loss,         telemetry reader, Pika     role to None, unconditional:
///     profile switch, failed heartbeat)      heartbeat, UI actor        a revocation is never lost
///   TryStart                                 telemetry processing       publish one attempt
///   CancelAsync, Stop, StopAsync             UI actor, shutdown         restart backoff, refuse attempts
///   Report                                   attempt worker             outcome under the current role
/// </code>
/// A role writer observes before its await and advances only from that
/// observation, so a stale reply fails instead of overwriting a newer role.
/// Attempt bookkeeping never changes the revision, so it cannot fail a role writer.</summary>
internal sealed class PikaSession(TimeSpan backoff)
{
    private PikaSessionState state = PikaSessionState.Initial;

    /// <summary>The published snapshot.</summary>
    public PikaSessionState Current => Volatile.Read(ref state);
    public PikaRoleObservation Observe() => Current.Observation;
    public PikaSessionRole Role => Current.Role;
    public bool Owned => Role == PikaSessionRole.Owned;
    public PikaSessionPhase Phase => Current.Phase(Stopwatch.GetTimestamp(), backoff);
    public string Status => Current.Status;
    public bool Stopped => Current.Stopped;
    public bool Connecting => Current.Connecting;
    /// <summary>The latest attempt, for callers that must drain or cancel exactly it.</summary>
    public PikaMonitorAttempt? Attempt => Current.Attempt;

    private bool Swap(PikaSessionState seen, PikaSessionState next) => ReferenceEquals(Interlocked.CompareExchange(ref state, next, seen), seen);

    private void Apply(Func<PikaSessionState, PikaSessionState> change)
    {
        PikaSessionState seen;
        do seen = Current; while (!Swap(seen, change(seen)));
    }

    /// <summary>Moves to <paramref name="next"/> only if no role write happened since <paramref name="observed"/>.</summary>
    public bool TryAdvance(PikaRoleObservation observed, PikaSessionRole next)
    {
        while (true)
        {
            var seen = Current;
            if (seen.Observation != observed) return false;
            if (Swap(seen, seen with { Role = next, Revision = seen.Revision + 1 })) return true;
        }
    }

    /// <summary>Revokes the role. It always wins and always invalidates earlier observations.</summary>
    public void Release() => Apply(seen => seen with { Role = PikaSessionRole.None, Revision = seen.Revision + 1 });

    /// <summary>Records one monitor outcome under the role current at the same instant.</summary>
    public void Report(string text) => Apply(seen => seen with { Outcome = new(seen.Observation, text) });

    /// <summary>Runs <paramref name="body"/> on the thread pool unless stopped,
    /// already connecting or still inside the backoff. The backoff is measured from the
    /// end of the previous attempt, however long that attempt took.</summary>
    public bool TryStart(CancellationToken lifetime, Func<CancellationToken, Task> body)
    {
        PikaMonitorAttempt? candidate = null;
        while (true)
        {
            var seen = Current;
            long now = Stopwatch.GetTimestamp();
            if (seen.Stopped || seen.Connecting || seen.BackingOff(now, backoff)) { candidate?.Abandon(); return false; }
            candidate ??= new(lifetime);
            if (!Swap(seen, seen with { Attempt = candidate, AttemptAt = now })) continue;
            var attempt = candidate;
            candidate.Start(async cancellation =>
            {
                try { await body(cancellation).ConfigureAwait(false); }
                finally { RestartBackoff(attempt); }
            });
            return true;
        }
    }

    /// <summary>Measures the backoff from now, while <paramref name="attempt"/> is still
    /// the latest. An attempt calls this before its worker completes, so no reader
    /// sees it ended with the pause it started under already spent.</summary>
    private void RestartBackoff(PikaMonitorAttempt attempt)
    {
        long now = Stopwatch.GetTimestamp();
        Apply(seen => ReferenceEquals(seen.Attempt, attempt) ? seen with { AttemptAt = now } : seen);
    }

    /// <summary>Cancels a running attempt and waits for it; the backoff restarts as it ends.</summary>
    public async Task CancelAsync()
    {
        if (Current.Attempt is { Running: true } running) await running.CancelAsync();
    }

    /// <summary>Refuses every later attempt; a running one continues until cancelled.</summary>
    public void Stop() => Apply(seen => seen with { Stopped = true });

    public async Task StopAsync()
    {
        Stop();
        await CancelAsync();
    }

    /// <summary>Lets the next <see cref="TryStart"/> run without waiting out the backoff.</summary>
    internal void ExpireBackoff() => Apply(seen => seen with { AttemptAt = 0 });
}
