using LuckyRobots.Piper.Plugin;

/// <summary>Puts a Pika session in a starting role through its own transitions.</summary>
internal static class PikaSessionFixture
{
    public static void Enter(this PikaSession session, PikaSessionRole role)
    {
        session.Release();
        if (role != PikaSessionRole.None && !session.TryAdvance(session.Observe(), role))
            throw new InvalidOperationException("The fixture could not enter the Pika role " + role);
    }
}
