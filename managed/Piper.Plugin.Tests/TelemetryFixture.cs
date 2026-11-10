using LuckyRobots.Piper.Plugin;

/// <summary>Brings Telemetry component state to a starting point through its own transitions.</summary>
internal static class TelemetryFixture
{
    public static void Enter(this PikaSession session, PikaSessionRole role)
    {
        session.Release();
        if (role != PikaSessionRole.None && !session.TryAdvance(session.Observe(), role))
            throw new InvalidOperationException("The fixture could not enter the Pika role " + role);
    }

    /// <summary>Records a capability as a finished probe whose backoff has already elapsed.</summary>
    public static void Record(this GhostCapability capability, GhostCapabilities recorded)
    {
        capability.ExpireBackoff();
        if (!capability.TryBeginProbe(System.Diagnostics.Stopwatch.GetTimestamp())) throw new InvalidOperationException("The fixture could not claim the ghost capability probe");
        capability.EndProbe(recorded);
        capability.ExpireBackoff();
    }
}
