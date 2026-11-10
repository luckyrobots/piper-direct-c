using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private bool PikaMonitorRetryNeeded() => TelemetryIsFresh() && !switchingProfile && CurrentRecovery is null &&
        PikaMonitorConnectionPolicy.ShouldRetry(state?["pika"] as JsonObject, PikaMatchesProfile(), owner, link is not null, telemetry.Pika.Role,
            state?["motion"]?["active"]?.GetValue<bool>() == true, teleoperating);

    internal void RetryLinkedPikaMonitor()
    {
        if (telemetry.Pika.Stopped || telemetry.Pika.Connecting || !PikaMonitorRetryNeeded()) return;
        // Connecting can wait for WSL/agent startup. Keep Watch and heartbeats
        // draining; never queue behind an Enable that itself awaits fresh state.
        telemetry.Pika.TryStart(shutdown.Token, async cancellation =>
        {
            bool entered = false;
            try
            {
                if (!await commands.WaitAsync(0, cancellation)) return;
                entered = true;
                if (!telemetry.Pika.Stopped && PikaMonitorRetryNeeded()) await MonitorLinkedPika(cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) { telemetry.Pika.Report("Pika monitor retry unavailable: " + error.Message); }
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
        var role = telemetry.Pika.Observe();
        var pika = state?["pika"] as JsonObject;
        bool matches = PikaMatchesProfile();
        if (!PikaMonitorConnectionPolicy.MayConnect(pika, matches, owner))
        {
            if (pika?["owner"]?.GetValue<string>() != owner) telemetry.Pika.TryAdvance(role, PikaSessionRole.None);
            telemetry.Pika.Report(!matches ? "Pika assignment is unconfigured or differs from this robot."
                : pika?["owner"]?.GetValue<string>() == owner ? telemetry.Pika.Owned ? "Pika control session active."
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
                if (role.Role == PikaSessionRole.Monitoring) telemetry.Pika.TryAdvance(role, PikaSessionRole.None);
                telemetry.Pika.Report("Pika monitor scope changed while connecting; no control permission was granted.");
                return;
            }
            if (role.Role == PikaSessionRole.None) telemetry.Pika.TryAdvance(role, PikaSessionRole.Monitoring);
            telemetry.Pika.Report(telemetry.Pika.Owned ? "Pika control session active." : "Pika monitoring only · motor power unchanged.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (role.Role == PikaSessionRole.Monitoring) telemetry.Pika.TryAdvance(role, PikaSessionRole.None);
            telemetry.Pika.Report("Pika monitor unavailable: " + error.Message);
        }
    }
}
