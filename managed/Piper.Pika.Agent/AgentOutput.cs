using System.Diagnostics;
using System.Text.Json;

namespace LuckyRobots.Piper.Pika.Agent;

/// <summary>The agent's only path to stdout: a bounded queue drained by its own
/// writer thread, so a Host that stops reading can block that thread and
/// nothing else. Nothing here ever waits for the sink.
///   Open ──a required frame finds no room, a frame waits longer than
///          StallSeconds, or the sink fails──▶ HostLost (terminal)
/// State frames are periodic and replaceable: when the queue is full the oldest
/// queued state frame is dropped. Required frames (ready, ACK, error, evidence)
/// are never dropped while Open; once one cannot be kept the Host is lost and
/// the watchdog disables, because a Host that cannot hear an ACK cannot
/// supervise the gripper either.</summary>
internal sealed class AgentOutput
{
    public const int Capacity = 64;
    /// <summary>A Host that has not taken one frame for as long as a heartbeat may be late is lost.</summary>
    public const double StallSeconds = PikaProtocol.HeartbeatTimeoutSeconds;

    private readonly record struct Frame(string Line, bool Required, long QueuedAt);
    private readonly object gate = new();
    private readonly LinkedList<Frame> queue = new();
    private readonly Action<string> sink;
    private long writingSince = -1, droppedStates, maximumWriteTicks;
    private bool lost;

    /// <param name="sink">Writes and flushes one line. It may block forever or throw.</param>
    public AgentOutput(Action<string> sink)
    {
        this.sink = sink;
        new Thread(WriteLoop) { IsBackground = true, Name = "pika-agent-stdout" }.Start();
    }

    public bool HostLost
    {
        get
        {
            lock (gate)
            {
                long oldest = writingSince >= 0 ? writingSince : queue.First?.Value.QueuedAt ?? -1;
                if (!lost && oldest >= 0 && Stopwatch.GetElapsedTime(oldest).TotalSeconds > StallSeconds) lost = true;
                return lost;
            }
        }
    }
    public long DroppedStates { get { lock (gate) return droppedStates; } }
    public int Queued { get { lock (gate) return queue.Count; } }
    public double MaximumWriteSeconds { get { lock (gate) return Stopwatch.GetElapsedTime(0, maximumWriteTicks).TotalSeconds; } }

    /// <summary>Queues a periodic frame; an older one may be dropped for it.</summary>
    public void State(object frame) => Add(JsonSerializer.Serialize(frame), required: false);
    /// <summary>Queues a frame that must reach the Host, or declares the Host lost.</summary>
    public void Required(object frame) => Add(JsonSerializer.Serialize(frame), required: true);

    private void Add(string line, bool required)
    {
        lock (gate)
        {
            if (lost) return;
            if (queue.Count >= Capacity)
            {
                var state = queue.First;
                while (state is not null && state.Value.Required) state = state.Next;
                if (state is not null) { queue.Remove(state); ++droppedStates; }
                else if (required) { lost = true; return; }
                else { ++droppedStates; return; }
            }
            queue.AddLast(new Frame(line, required, Stopwatch.GetTimestamp()));
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>Waits a bounded time for queued frames to reach the sink (a final ACK or error before exit).</summary>
    public bool Drain(TimeSpan limit)
    {
        long started = Stopwatch.GetTimestamp();
        lock (gate)
        {
            while (!lost && (queue.Count > 0 || writingSince >= 0))
            {
                TimeSpan left = limit - Stopwatch.GetElapsedTime(started);
                if (left <= TimeSpan.Zero || !Monitor.Wait(gate, left)) break;
            }
            return !lost && queue.Count == 0 && writingSince < 0;
        }
    }

    private void WriteLoop()
    {
        while (true)
        {
            string line;
            lock (gate)
            {
                while (queue.Count == 0 && !lost) Monitor.Wait(gate);
                if (lost) return;
                line = queue.First!.Value.Line; queue.RemoveFirst();
                writingSince = Stopwatch.GetTimestamp();
            }
            bool failed = false;
            try { sink(line); }
            catch (Exception) { failed = true; } // A closed or broken pipe: nobody is reading.
            lock (gate)
            {
                maximumWriteTicks = Math.Max(maximumWriteTicks, Stopwatch.GetTimestamp() - writingSince);
                writingSince = -1; lost |= failed;
                Monitor.PulseAll(gate);
            }
        }
    }
}
