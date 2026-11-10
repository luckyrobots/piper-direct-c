using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>This plugin's relationship to the active profile's Pika session.
/// <code>
///   None --read-only connect--> Monitoring --explicit Enable--> Owned
///   None --explicit Enable--> Owned;  Owned --jaws freed for teaching--> Monitoring
///   Monitoring --teaching measured hold verified--> Owned
///   any --backend/telemetry loss, profile switch, restart, failed heartbeat--> None
/// </code></summary>
internal enum PikaSessionRole { None, Monitoring, Owned }

/// <summary>One read of the role field: the role plus the transition count that
/// distinguishes it from every later write, including a release to the same role.</summary>
internal readonly record struct PikaRoleObservation(long Value)
{
    public PikaSessionRole Role => (PikaSessionRole)(Value & 3);
    public long Next(PikaSessionRole role) => (((Value >> 2) + 1) << 2) | (long)role;
}

public sealed partial class PipersPlugin
{
    // One field: the role in the low two bits, a transition count above them.
    // Writers observe it before their await and advance only from that exact
    // observation, so a stale writer fails instead of clobbering a newer state.
    // Only ReleasePikaRole is unconditional: a revocation is never lost.
    internal long pikaRole;
    internal PikaRoleObservation ObservePikaRole() => new(Volatile.Read(ref pikaRole));
    internal PikaSessionRole PikaRole => ObservePikaRole().Role;
    private bool pikaOwned => PikaRole == PikaSessionRole.Owned;
    internal bool TryAdvancePikaRole(PikaRoleObservation observed, PikaSessionRole next) =>
        Interlocked.CompareExchange(ref pikaRole, observed.Next(next), observed.Value) == observed.Value;
    internal void ReleasePikaRole()
    {
        PikaRoleObservation current;
        do current = ObservePikaRole(); while (!TryAdvancePikaRole(current, PikaSessionRole.None));
    }

    // One field, written only through its own TryStart/CancelAsync/Stop/StopAsync/Report.
    internal readonly PikaMonitorRetry pikaMonitor = new(TimeSpan.FromSeconds(3));

    /// <summary>Plugin-F status text, derived rather than written: the monitor's last
    /// outcome while the role it was reported under still holds, otherwise the role.</summary>
    internal string PikaMonitorStatus
    {
        get
        {
            var role = ObservePikaRole();
            if (pikaMonitor.Outcome is { } outcome && outcome.Role == role) return outcome.Text;
            return role.Role switch
            {
                PikaSessionRole.Owned => "Pika control session active.",
                PikaSessionRole.Monitoring => "Pika monitoring only · motor power unchanged.",
                _ => "Pika telemetry starts read-only when the scene is linked."
            };
        }
    }

    /// <summary>Records one monitor outcome with the role observed after its transition.</summary>
    private void ReportPikaMonitor(string text) => pikaMonitor.Report(new(ObservePikaRole(), text));

    private bool PikaMonitorRetryNeeded() => TelemetryIsFresh() && !switchingProfile && CurrentRecovery is null &&
        PikaMonitorConnectionPolicy.ShouldRetry(state?["pika"] as JsonObject, PikaMatchesProfile(), owner, link is not null, PikaRole,
            state?["motion"]?["active"]?.GetValue<bool>() == true, teleoperating);

    internal void RetryLinkedPikaMonitor()
    {
        if (pikaMonitor.Stopped || pikaMonitor.Running || !PikaMonitorRetryNeeded()) return;
        // Connecting can wait for WSL/agent startup. Keep Watch and heartbeats
        // draining; never queue behind an Enable that itself awaits fresh state.
        pikaMonitor.TryStart(shutdown.Token, async cancellation =>
        {
            bool entered = false;
            try
            {
                if (!await commands.WaitAsync(0, cancellation)) return;
                entered = true;
                if (!pikaMonitor.Stopped && PikaMonitorRetryNeeded()) await MonitorLinkedPika(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { ReportPikaMonitor("Pika monitor retry unavailable: " + error.Message); }
            finally { if (entered) commands.Release(); }
        });
    }

    private async Task MonitorLinkedPika(CancellationToken cancellation)
    {
        var expectedLink = link;
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        // The role is observed with the snapshot that decides it; any newer
        // write (an Enable's Owned or a revocation) makes this monitor's own
        // transitions below fail rather than overwrite it.
        var role = ObservePikaRole();
        var pika = state?["pika"] as JsonObject;
        bool matches = PikaMatchesProfile();
        if (!PikaMonitorConnectionPolicy.MayConnect(pika, matches, owner))
        {
            if (pika?["owner"]?.GetValue<string>() != owner) TryAdvancePikaRole(role, PikaSessionRole.None);
            ReportPikaMonitor(!matches ? "Pika assignment is unconfigured or differs from this robot."
                : pika?["owner"]?.GetValue<string>() == owner ? pikaOwned ? "Pika control session active."
                    : "This Pika session has a recorded power request; use its explicit control or cleanup flow."
                : "Pika is owned by another client; fresh shared telemetry is read without taking over.");
            return;
        }
        try
        {
            if (expectedLink is null) return;
            _ = Device(expectedLink.DeviceId, expectedLink.Generation, expectedToken);
            // Monitoring never configures, enables, disables or positions hardware.
            // An explicit Enable action remains the only upgrade to power ownership.
            await Call("/api/pika/connect", new JsonObject { ["owner"] = owner, ["take_over"] = false }, cancellation, expectedToken);
            _ = Device(expectedLink.DeviceId, expectedLink.Generation, expectedToken);
            if (!ReferenceEquals(link, expectedLink) || token != expectedToken || ActiveProfile.Id != expectedProfile || !PikaMatchesProfile())
            {
                if (role.Role == PikaSessionRole.Monitoring) TryAdvancePikaRole(role, PikaSessionRole.None);
                ReportPikaMonitor("Pika monitor scope changed while connecting; no control permission was granted.");
                return;
            }
            if (role.Role == PikaSessionRole.None) TryAdvancePikaRole(role, PikaSessionRole.Monitoring);
            ReportPikaMonitor(pikaOwned ? "Pika control session active." : "Pika monitoring only · motor power unchanged.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (role.Role == PikaSessionRole.Monitoring) TryAdvancePikaRole(role, PikaSessionRole.None);
            ReportPikaMonitor("Pika monitor unavailable: " + error.Message);
        }
    }
}

/// <summary>One monitor outcome and the exact role observation it describes.</summary>
internal sealed record PikaMonitorOutcome(PikaRoleObservation Role, string Text);

/// <summary>The read-only Pika monitor's connect attempts, one at a time.
/// <code>
///   Idle --TryStart (backoff elapsed)--> Running --attempt ends--> Idle
///   Running --CancelAsync--> Idle (backoff restarts)
///   any --Stop--> Stopped (terminal: no further attempts)
/// </code></summary>
internal sealed class PikaMonitorRetry(TimeSpan backoff)
{
    private Task? worker;
    private CancellationTokenSource? cancellation;
    private long attemptAt;
    private volatile bool stopped;
    private PikaMonitorOutcome? outcome;

    public bool Stopped => stopped;
    /// <summary>The latest attempt's outcome, the only source of monitor status text.</summary>
    public PikaMonitorOutcome? Outcome => Volatile.Read(ref outcome);
    public void Report(PikaMonitorOutcome latest) => Volatile.Write(ref outcome, latest);
    public bool Running => worker is { IsCompleted: false };
    /// <summary>The latest attempt, for callers that must drain it.</summary>
    public Task? Worker => worker;

    /// <summary>Runs <paramref name="attempt"/> on the thread pool unless stopped,
    /// already running or still inside the backoff after the previous attempt.</summary>
    public bool TryStart(CancellationToken lifetime, Func<CancellationToken, Task> attempt)
    {
        if (stopped || Running || attemptAt != 0 && Stopwatch.GetElapsedTime(attemptAt) < backoff) return false;
        attemptAt = Stopwatch.GetTimestamp();
        cancellation?.Dispose();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = cancellation.Token;
        worker = Task.Run(() => attempt(token), CancellationToken.None);
        return true;
    }

    /// <summary>Cancels a running attempt and waits for it; the backoff restarts now.</summary>
    public async Task CancelAsync()
    {
        if (worker is not { IsCompleted: false } running) return;
        attemptAt = Stopwatch.GetTimestamp();
        if (cancellation is not null) await cancellation.CancelAsync();
        await running;
    }

    /// <summary>Refuses every later attempt; a running one continues until cancelled.</summary>
    public void Stop() => stopped = true;

    public async Task StopAsync()
    {
        Stop();
        try { await CancelAsync(); }
        finally { cancellation?.Dispose(); }
    }

    /// <summary>Lets the next <see cref="TryStart"/> run without waiting out the backoff.</summary>
    internal void ExpireBackoff() => attemptAt = 0;
}
