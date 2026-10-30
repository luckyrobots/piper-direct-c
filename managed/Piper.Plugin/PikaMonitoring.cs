using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class PikaMonitorPolicy
{
    public static bool MayConnect(JsonObject? pika, bool matchesProfile, string owner) => matchesProfile &&
        pika?["configured"]?.GetValue<bool>() == true &&
        pika["power_requested"]?.GetValue<bool>() != true && pika["power_uncertain"]?.GetValue<bool>() != true &&
        (string.IsNullOrEmpty(pika["owner"]?.GetValue<string>()) || pika["owner"]?.GetValue<string>() == owner ||
            pika["connected"]?.GetValue<bool>() == false && pika["fresh"]?.GetValue<bool>() == false &&
            pika["power_requested"]?.GetValue<bool>() == false && pika["power_uncertain"]?.GetValue<bool>() == false);

    public static bool ShouldRetry(JsonObject? pika, bool matchesProfile, string owner, bool linked, bool ownsPower, bool monitoring,
        bool armMoving = false, bool teleoperating = false) => linked && !ownsPower && !armMoving && !teleoperating &&
        MayConnect(pika, matchesProfile, owner) && (!monitoring || pika?["connected"]?.GetValue<bool>() != true);
}

public sealed partial class PipersPlugin
{
    private bool pikaMonitoring;
    private string pikaMonitorStatus = "Pika telemetry starts read-only when the scene is linked.";
    private long pikaMonitorAttemptAt;
    private Task? pikaMonitorWorker;
    private CancellationTokenSource? pikaMonitorCancellation;
    private bool pikaMonitorStopping;

    private bool PikaMonitorRetryNeeded() => TelemetryIsFresh() && !switchingProfile && !teachingResetBusy &&
        PikaMonitorPolicy.ShouldRetry(state?["pika"] as JsonObject, PikaMatchesProfile(), owner, link is not null, pikaOwned, pikaMonitoring,
            state?["motion"]?["active"]?.GetValue<bool>() == true, teleoperating);

    private void RetryLinkedPikaMonitor()
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

    private async Task StopPikaMonitorRetries()
    {
        pikaMonitorStopping = true;
        await CancelPikaMonitorAttempt();
        pikaMonitorCancellation?.Dispose();
    }

    private async Task MonitorLinkedPika(CancellationToken cancellation)
    {
        var expectedLink = link;
        string expectedToken = token, expectedProfile = ActiveProfile.Id;
        var pika = state?["pika"] as JsonObject;
        bool matches = PikaMatchesProfile();
        if (!PikaMonitorPolicy.MayConnect(pika, matches, owner))
        {
            if (pika?["owner"]?.GetValue<string>() != owner) pikaOwned = pikaMonitoring = false;
            pikaMonitorStatus = !matches ? "Pika assignment is unconfigured or differs from this robot."
                : pika?["owner"]?.GetValue<string>() == owner ? pikaOwned ? "Pika control session active."
                    : "This Pika session has a recorded power request; use its explicit control or cleanup flow."
                : "Pika is owned by another client; fresh shared telemetry is read without taking over.";
            return;
        }
        try
        {
            // Monitoring never configures, enables, disables or positions hardware.
            // An explicit Enable action remains the only upgrade to power ownership.
            await Call("/api/pika/connect", new JsonObject { ["owner"] = owner, ["take_over"] = false }, cancellation, expectedToken);
            if (!ReferenceEquals(link, expectedLink) || token != expectedToken || ActiveProfile.Id != expectedProfile || !PikaMatchesProfile())
            {
                pikaMonitoring = false;
                pikaMonitorStatus = "Pika monitor scope changed while connecting; no control permission was granted.";
                return;
            }
            pikaMonitoring = true;
            pikaMonitorStatus = pikaOwned ? "Pika control session active." : "Pika monitoring only · motor power unchanged.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            pikaMonitoring = false;
            pikaMonitorStatus = "Pika monitor unavailable: " + error.Message;
        }
    }
}
