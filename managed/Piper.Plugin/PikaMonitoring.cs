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

public sealed partial class PipersPlugin
{
    // One field, written only through SetPikaRole/TryEnterPikaRole/ReleasePikaRole.
    internal int pikaRole;
    private PikaSessionRole PikaRole => (PikaSessionRole)Volatile.Read(ref pikaRole);
    private bool pikaOwned => PikaRole == PikaSessionRole.Owned;
    internal void SetPikaRole(PikaSessionRole next) => Volatile.Write(ref pikaRole, (int)next);
    /// <summary>Moves only from <paramref name="expected"/>, so a revocation that
    /// raced an in-flight connect is not overwritten.</summary>
    private bool TryEnterPikaRole(PikaSessionRole expected, PikaSessionRole next) =>
        Interlocked.CompareExchange(ref pikaRole, (int)next, (int)expected) == (int)expected;
    private void ReleasePikaRole() => SetPikaRole(PikaSessionRole.None);

    private string pikaMonitorStatus = "Pika telemetry starts read-only when the scene is linked.";
    internal long pikaMonitorAttemptAt;
    internal Task? pikaMonitorWorker;
    private CancellationTokenSource? pikaMonitorCancellation;
    internal bool pikaMonitorStopping;

    private bool PikaMonitorRetryNeeded() => TelemetryIsFresh() && !switchingProfile && CurrentRecovery is null &&
        PikaMonitorConnectionPolicy.ShouldRetry(state?["pika"] as JsonObject, PikaMatchesProfile(), owner, link is not null, PikaRole,
            state?["motion"]?["active"]?.GetValue<bool>() == true, teleoperating);

    internal void RetryLinkedPikaMonitor()
    {
        if (pikaMonitorStopping || pikaMonitorWorker is { IsCompleted: false } || !PikaMonitorRetryNeeded() ||
            pikaMonitorAttemptAt != 0 && Stopwatch.GetElapsedTime(pikaMonitorAttemptAt).TotalSeconds < 3) return;
        pikaMonitorAttemptAt = Stopwatch.GetTimestamp();
        pikaMonitorCancellation?.Dispose();
        pikaMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var cancellation = pikaMonitorCancellation.Token;
        // Connecting can wait for WSL/agent startup. Keep Watch and heartbeats
        // draining; never queue behind an Enable that itself awaits fresh state.
        pikaMonitorWorker = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                if (!await commands.WaitAsync(0, cancellation)) return;
                entered = true;
                if (!pikaMonitorStopping && PikaMonitorRetryNeeded()) await MonitorLinkedPika(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { pikaMonitorStatus = "Pika monitor retry unavailable: " + error.Message; }
            finally { if (entered) commands.Release(); }
        }, CancellationToken.None);
    }

    private async Task CancelPikaMonitorAttempt()
    {
        if (pikaMonitorWorker is not { IsCompleted: false }) return;
        // Operator input, especially STOP, must not wait behind a WSL startup.
        // Defer the next retry so the event can acquire the command gate first.
        pikaMonitorAttemptAt = Stopwatch.GetTimestamp();
        if (pikaMonitorCancellation is not null) await pikaMonitorCancellation.CancelAsync();
        await pikaMonitorWorker;
    }

    private async Task MonitorLinkedPika(CancellationToken cancellation)
    {
        var expectedLink = link;
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        var pika = state?["pika"] as JsonObject;
        bool matches = PikaMatchesProfile();
        if (!PikaMonitorConnectionPolicy.MayConnect(pika, matches, owner))
        {
            if (pika?["owner"]?.GetValue<string>() != owner) ReleasePikaRole();
            pikaMonitorStatus = !matches ? "Pika assignment is unconfigured or differs from this robot."
                : pika?["owner"]?.GetValue<string>() == owner ? pikaOwned ? "Pika control session active."
                    : "This Pika session has a recorded power request; use its explicit control or cleanup flow."
                : "Pika is owned by another client; fresh shared telemetry is read without taking over.";
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
                TryEnterPikaRole(PikaSessionRole.Monitoring, PikaSessionRole.None);
                pikaMonitorStatus = "Pika monitor scope changed while connecting; no control permission was granted.";
                return;
            }
            TryEnterPikaRole(PikaSessionRole.None, PikaSessionRole.Monitoring);
            pikaMonitorStatus = pikaOwned ? "Pika control session active." : "Pika monitoring only · motor power unchanged.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            TryEnterPikaRole(PikaSessionRole.Monitoring, PikaSessionRole.None);
            pikaMonitorStatus = "Pika monitor unavailable: " + error.Message;
        }
    }
}
