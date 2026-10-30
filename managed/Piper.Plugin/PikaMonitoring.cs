using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class PikaMonitorPolicy
{
    public static bool MayConnect(JsonObject? pika, bool matchesProfile, string owner) => matchesProfile &&
        pika?["configured"]?.GetValue<bool>() == true &&
        (string.IsNullOrEmpty(pika["owner"]?.GetValue<string>()) || pika["owner"]?.GetValue<string>() == owner);
}

public sealed partial class PipersPlugin
{
    private bool pikaMonitoring;
    private string pikaMonitorStatus = "Pika telemetry starts read-only when the scene is linked.";

    private async Task MonitorLinkedPika(CancellationToken cancellation)
    {
        var pika = state?["pika"] as JsonObject;
        bool matches = PikaMatchesProfile();
        if (!PikaMonitorPolicy.MayConnect(pika, matches, owner))
        {
            if (pika?["owner"]?.GetValue<string>() != owner) pikaOwned = pikaMonitoring = false;
            pikaMonitorStatus = !matches ? "Pika assignment is unconfigured or differs from this robot."
                : "Pika is owned by another client; fresh shared telemetry is read without taking over.";
            return;
        }
        try
        {
            // Monitoring never configures, enables, disables or positions hardware.
            // An explicit Enable action remains the only upgrade to power ownership.
            await Call("/api/pika/connect", new JsonObject { ["owner"] = owner, ["take_over"] = false }, cancellation);
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
