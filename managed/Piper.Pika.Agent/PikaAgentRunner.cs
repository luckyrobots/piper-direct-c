namespace LuckyRobots.Piper.Pika.Agent;

/// <summary>Gives <see cref="PikaAgent"/> its three independent threads: the
/// serial reader, the Host input reader and, on the calling thread, the
/// watchdog timer. A reader blocked for ever stops only itself. Every way out
/// of <see cref="Run"/> passes through <see cref="PikaAgent.Shutdown"/>.</summary>
internal static class PikaAgentRunner
{
    public const int TickMilliseconds = 10;
    /// <summary>Longest Host line accepted; a longer one is ignored unparsed.</summary>
    public const int MaximumLineCharacters = 4096;

    /// <param name="readLine">Blocks for the next Host line; null when the Host closed its end.</param>
    /// <param name="terminate">Set by a termination signal or process exit.</param>
    public static void Run(PikaAgent agent, IPikaAgentSerial serial, Func<string?> readLine, Func<double> clock, WaitHandle terminate)
    {
        new Thread(() => ReadSerial(agent, serial, clock)) { IsBackground = true, Name = "pika-agent-serial" }.Start();
        new Thread(() => ReadInput(agent, readLine)) { IsBackground = true, Name = "pika-agent-stdin" }.Start();
        try
        {
            while (!agent.Finished)
            {
                agent.Tick();
                if (terminate.WaitOne(TickMilliseconds)) agent.Stop("Agent terminated.");
            }
        }
        finally { agent.Shutdown(); }
    }

    private static void ReadSerial(PikaAgent agent, IPikaAgentSerial serial, Func<double> clock)
    {
        byte[] buffer = new byte[4096];
        while (!agent.Finished)
        {
            double started = clock();
            try
            {
                int count = serial.Read(buffer);
                if (count > 0) agent.SerialBytes(buffer.AsSpan(0, count), clock() - started);
                else { agent.SerialTimeout(clock() - started); Thread.Sleep(1); }
            }
            catch (TimeoutException) { agent.SerialTimeout(clock() - started); }
            catch (Exception error) { agent.Fault(error); return; }
        }
    }

    private static void ReadInput(PikaAgent agent, Func<string?> readLine)
    {
        try
        {
            // One line at a time: the Host pipe is the only queue, so transport memory stays bounded.
            while (readLine() is { } line) if (line.Length <= MaximumLineCharacters) agent.Command(line);
        }
        catch (Exception) { }
        // Without the Host nobody can stop the gripper: end, and disable on the way out.
        agent.Stop("Host closed the agent input.");
    }
}
