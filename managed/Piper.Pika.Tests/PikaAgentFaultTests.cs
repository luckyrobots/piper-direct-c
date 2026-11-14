using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Pika.Agent;
using Microsoft.Win32.SafeHandles;

/// <summary>Fault injection against the production agent (stage 5). Three layers:
/// PikaAgent on a fake clock (exact watchdog bounds), PikaAgentRunner in real
/// time (a blocked stdout, a serial read that never returns, closed input and
/// a termination signal each still reach Disable), and on Linux the real agent
/// binary on a pty, launched by PikaProcessService's native branch.</summary>
internal static class PikaAgentFaultTests
{
    private static readonly byte[] DisablePacket = PikaProtocol.Packet(PikaCommand.Disable), EnablePacket = PikaProtocol.Packet(PikaCommand.Enable);

    private static byte[] Telemetry(bool enabled, double position = .5, double speed = 0) => Encoding.UTF8.GetBytes(
        "{\"motor\":{\"Position\":" + position.ToString("R", CultureInfo.InvariantCulture) + ",\"Speed\":" + speed.ToString("R", CultureInfo.InvariantCulture) +
        ",\"Current\":0},\"motorstatus\":{\"Status\":\"" + (enabled ? "0x40" : "0x00") +
        "\",\"Voltage\":24,\"DriverTemp\":40,\"MotorTemp\":33,\"BusCurrent\":0}}\r\n");

    /// <summary>A gripper that reports the power state of the last enable/disable packet it received.</summary>
    private sealed class Gripper : IPikaAgentSerial
    {
        private readonly ManualResetEventSlim released = new();
        private int disables, enables, positions, attempts;
        public volatile bool Enabled, Stall, FailWrites;
        public int Disables => Volatile.Read(ref disables);
        public int Enables => Volatile.Read(ref enables);
        public int Positions => Volatile.Read(ref positions);
        public int Attempts => Volatile.Read(ref attempts);

        public int Read(byte[] buffer)
        {
            // A stalled read never returns: only the reader thread may be lost to it.
            if (Stall) { released.Wait(); throw new TimeoutException(); }
            Thread.Sleep(10);
            byte[] packet = Telemetry(Enabled); packet.CopyTo(buffer, 0); return packet.Length;
        }

        public void Write(byte[] packet)
        {
            Interlocked.Increment(ref attempts);
            if (FailWrites) throw new IOException("fixture serial write failure");
            if (packet.AsSpan().SequenceEqual(DisablePacket)) { Enabled = false; Interlocked.Increment(ref disables); }
            else if (packet.AsSpan().SequenceEqual(EnablePacket)) { Enabled = true; Interlocked.Increment(ref enables); }
            else if (packet.Length == 7 && packet[0] == (byte)PikaCommand.Position) Interlocked.Increment(ref positions);
            else throw new InvalidOperationException("The agent wrote an unknown packet.");
        }

        public void Release() => released.Set();
    }

    private static string Line(string id, string action, params (string Name, object Value)[] fields)
    {
        var command = new Dictionary<string, object> { ["id"] = id, ["action"] = action };
        foreach (var field in fields) command[field.Name] = field.Value;
        return JsonSerializer.Serialize(command);
    }

    private sealed class Captured
    {
        private readonly List<string> lines = [];
        public readonly ManualResetEventSlim Open = new(true);
        public void Sink(string line) { Open.Wait(); lock (lines) lines.Add(line); }
        public string[] Lines { get { lock (lines) return lines.ToArray(); } }
        public IEnumerable<JsonElement> Of(string type) => Lines.Select(line => JsonDocument.Parse(line).RootElement)
            .Where(frame => frame.GetProperty("type").GetString() == type);
        public bool? Ack(string id) => Of("ack").Where(frame => frame.GetProperty("id").GetString() == id)
            .Select(frame => (bool?)frame.GetProperty("ok").GetBoolean()).FirstOrDefault();
        public JsonElement? LastState => Of("state").Select(frame => (JsonElement?)frame).LastOrDefault();
    }

    /// <summary>PikaAgent stepped on a fake clock in 10 ms ticks.</summary>
    private sealed class Rig
    {
        private long ticks; private int nextId;
        public readonly Gripper Gripper = new();
        public readonly Captured Captured = new();
        public readonly AgentOutput Output;
        public readonly PikaAgent Agent;
        public double Now => ticks / 100.0;
        /// <summary>Measured position and speed reported at a fake-clock time; null reports a still jaw at 0.5 rad.</summary>
        public Func<double, (double Position, double Speed)>? Motion;

        public Rig()
        {
            Output = new AgentOutput(Captured.Sink);
            Agent = new PikaAgent(Gripper, Output, () => Now, PikaFollowSession.MinimumCommandPeriodSeconds);
        }

        public string Send(string action, params (string Name, object Value)[] fields)
        {
            string id = "t" + ++nextId;
            Agent.Command(Line(id, action, fields));
            if (Captured.Open.IsSet) Output.Drain(TimeSpan.FromSeconds(2));
            return id;
        }

        public void Advance(double seconds, bool telemetry = true, bool heartbeat = true)
        {
            for (long end = ticks + (long)Math.Round(seconds * 100); ticks < end;)
            {
                ++ticks;
                if (telemetry)
                {
                    var (position, speed) = Motion?.Invoke(Now) ?? (.5, 0);
                    Agent.SerialBytes(Telemetry(Gripper.Enabled, position, speed), 0);
                }
                if (heartbeat && ticks % 50 == 0) Agent.Command(Line("hb" + ticks, "heartbeat"));
                Agent.Tick();
                if (ticks % 10 == 0 && Captured.Open.IsSet) Output.Drain(TimeSpan.FromSeconds(2));
            }
            if (Captured.Open.IsSet) Output.Drain(TimeSpan.FromSeconds(2));
        }

        public Rig Arm()
        {
            Advance(.05);
            Check(Captured.Ack(Send("enable", ("clear", true))) == true && Gripper.Enables == 1, "The fixture agent refused a valid enable");
            Advance(.05);
            Check(Agent.Mode == MotorMode.Armed && Gripper.Enabled && Gripper.Disables == 0, "The fixture agent did not arm");
            Send("heartbeat");
            return this;
        }
    }

    public static async Task Run()
    {
        VerifyOutputQueue();
        VerifyHostSilence();
        VerifyStalledTelemetryAndTargetDeadline();
        VerifyArrivalUnderSpeedNoise();
        VerifyHoldingGrace();
        VerifyMalformedLines();
        VerifySerialWriteFailure();
        VerifyUnreadOutputEndsTheAgent();
        VerifyRunnerBlockedStdout();
        VerifyRunnerStalledSerial();
        VerifyRunnerExitPaths();
        if (OperatingSystem.IsLinux()) await VerifyRealAgentBinary();
        Console.WriteLine("Pika agent fault tests passed: non-blocking output, Host silence, stalled serial, holding grace, malformed input, write failure, blocked stdout, closed input and termination" +
            (OperatingSystem.IsLinux() ? ", and the real agent binary on a pty (native launch, heartbeat loss, SIGTERM)." : "."));
    }

    // P1: the output queue never makes its caller wait, and says when the Host is gone.
    private static void VerifyOutputQueue()
    {
        var blocked = new Captured(); blocked.Open.Reset();
        var output = new AgentOutput(blocked.Sink);
        var started = Stopwatch.StartNew();
        output.Required(new { type = "ack", id = "first" });
        for (int i = 0; i < 500; i++) output.State(new { type = "state", i });
        output.Required(new { type = "ack", id = "second" });
        Check(started.Elapsed.TotalSeconds < 1 && output.Queued <= AgentOutput.Capacity && output.DroppedStates >= 500 - AgentOutput.Capacity - 1 && !output.HostLost,
            "A blocked stdout made the agent wait, grew its queue without bound, or was declared lost before the stall bound");
        blocked.Open.Set();
        Check(output.Drain(TimeSpan.FromSeconds(2)) && !output.HostLost, "A resumed Host did not receive the queued frames");
        var acks = blocked.Of("ack").Select(frame => frame.GetProperty("id").GetString()).ToArray();
        var states = blocked.Of("state").Select(frame => frame.GetProperty("i").GetInt32()).ToArray();
        Check(acks.SequenceEqual(["first", "second"]) && states.Length > 0 && states[^1] == 499 && states.SequenceEqual(states.Order()),
            "Dropping state frames lost an ACK, reordered output or dropped the newest state instead of the oldest");

        var full = new Captured(); full.Open.Reset();
        var overflow = new AgentOutput(full.Sink);
        for (int i = 0; i < AgentOutput.Capacity + 2; i++) overflow.Required(new { type = "ack", id = "a" + i });
        Check(overflow.HostLost, "ACKs that could not be kept were dropped silently instead of declaring the Host lost");
        full.Open.Set();

        var stalled = new Captured(); stalled.Open.Reset();
        var stall = new AgentOutput(stalled.Sink);
        stall.State(new { type = "state" });
        Check(!stall.HostLost, "One unwritten frame was treated as a lost Host immediately");
        Thread.Sleep(TimeSpan.FromSeconds(AgentOutput.StallSeconds + .3));
        Check(stall.HostLost, "A Host that took no output for longer than a heartbeat timeout was not declared lost");
        stalled.Open.Set();

        var broken = new AgentOutput(_ => throw new IOException("broken pipe"));
        broken.Required(new { type = "ready" });
        broken.Drain(TimeSpan.FromSeconds(2));
        Check(broken.HostLost, "A failed stdout write was not treated as a lost Host");
    }

    // Host silence: disable at the heartbeat timeout, keep reporting, then leave.
    private static void VerifyHostSilence()
    {
        var rig = new Rig().Arm();
        double lastHeartbeat = rig.Now;
        rig.Advance(PikaProtocol.HeartbeatTimeoutSeconds - .01, heartbeat: false);
        Check(rig.Gripper.Disables == 0 && rig.Agent.Mode == MotorMode.Armed, "The heartbeat watchdog disabled before its timeout");
        rig.Advance(.03, heartbeat: false);
        Check(rig.Gripper.Disables == 1 && rig.Agent.Mode == MotorMode.ReadOnly && !rig.Agent.Finished,
            "A silent Host did not reach Disable within the heartbeat timeout");
        rig.Advance(.2, heartbeat: false);
        var state = rig.Captured.LastState!.Value;
        Check(state.GetProperty("fresh").GetBoolean() && !state.GetProperty("enabled").GetBoolean() && !state.GetProperty("armed").GetBoolean() &&
            state.GetProperty("feedback_time_s").GetDouble() > state.GetProperty("request_time_s").GetDouble(),
            "The agent did not report the disabled feedback that lets the Host verify the watchdog disable");
        rig.Advance(lastHeartbeat + PikaProtocol.ClientGoneSeconds - rig.Now - .01, heartbeat: false);
        Check(!rig.Agent.Finished, "The agent left before the client-gone bound");
        rig.Advance(.03, heartbeat: false);
        Check(rig.Agent.Finished, "The agent stayed after its client was gone");
        rig.Agent.Shutdown(); rig.Agent.Shutdown();
        Check(rig.Gripper.Disables == 2, "Agent exit did not write exactly one more disable after power had been written");
    }

    private static void VerifyStalledTelemetryAndTargetDeadline()
    {
        var rig = new Rig().Arm();
        rig.Advance(PikaProtocol.FeedbackFreshSeconds - .02, telemetry: false);
        Check(rig.Gripper.Disables == 0, "The telemetry watchdog disabled while feedback was still fresh");
        rig.Advance(.04, telemetry: false);
        Check(rig.Gripper.Disables == 1 && rig.Agent.Mode == MotorMode.ReadOnly && !rig.Agent.Finished, "Stalled telemetry did not reach Disable within the freshness bound");
        rig.Advance(.2, telemetry: false);
        Check(!rig.Captured.LastState!.Value.GetProperty("fresh").GetBoolean(), "The agent reported stale telemetry as fresh, so the Host could not hold the result Uncertain");

        var target = new Rig().Arm();
        Check(target.Captured.Ack(target.Send("position", ("value", PikaProtocol.Angle(80)))) == true && target.Agent.Mode == MotorMode.Targeting,
            "The fixture agent refused a valid target");
        target.Advance(PikaProtocol.TargetDeadlineSeconds - .02);
        Check(target.Gripper.Disables == 0 && target.Agent.Mode == MotorMode.Targeting, "The movement deadline fired early");
        target.Advance(.04);
        Check(target.Gripper.Disables == 1 && target.Agent.Mode == MotorMode.ReadOnly, "A target that never arrived was not disabled at the movement deadline");
    }

    // Measured arrival under real feedback noise. On hardware at rest, the per-sample
    // speed estimate reached 0.12 rad/s (six times StationarySpeedRadiansPerSecond)
    // while the opening varied by only ~0.2 mm, and a reached target was disabled at
    // its movement deadline because the speed test never held for 150 ms.
    private static void VerifyArrivalUnderSpeedNoise()
    {
        double target = PikaProtocol.Angle(40);
        var noise = new Random(7);
        var noisy = new Rig().Arm();
        // ±0.002 rad of position jitter (~±0.1 mm here) and ±0.12 rad/s of speed jitter around the target.
        noisy.Motion = _ => (target + (noise.NextDouble() - .5) * .004, (noise.NextDouble() - .5) * .24);
        Check(noisy.Captured.Ack(noisy.Send("position", ("value", target))) == true && noisy.Agent.Mode == MotorMode.Targeting,
            "The fixture agent refused a valid target");
        noisy.Advance(.3);
        Check(noisy.Agent.Mode == MotorMode.Armed && noisy.Gripper.Disables == 0,
            "A reached target was not confirmed promptly under realistic speed-estimate noise");
        noisy.Advance(PikaProtocol.TargetDeadlineSeconds + 1);
        Check(noisy.Gripper.Disables == 0 && noisy.Agent.Mode == MotorMode.Armed, "A reached target was disabled at the movement deadline");

        // A jaw still sweeping through the tolerance band (30 → 50 mm at 0.2 rad/s) has not arrived at 40 mm.
        var moving = new Rig().Arm();
        double from = PikaProtocol.Angle(30), to = PikaProtocol.Angle(50), startedAt = moving.Now;
        moving.Motion = now => (Math.Min(to, from + .2 * (now - startedAt)), now - startedAt < (to - from) / .2 ? .2 : 0);
        Check(moving.Captured.Ack(moving.Send("position", ("value", target))) == true, "The fixture agent refused a valid target");
        moving.Advance((to - from) / .2 + .5);
        Check(moving.Agent.Mode == MotorMode.Targeting, "Arrival was confirmed while the jaw was still travelling through the tolerance band");

        var window = new PikaStillness();
        Check(!window.Observe(0, 40) && !window.Observe(.1, 40) && window.Observe(.15, 40.4), "Stillness did not need its full window or rejected rest jitter");
        Check(!window.Observe(.2, 41), "A 0.6 mm spread within the window counted as still");
        Check(!window.Observe(.1, 41), "Time running backwards did not restart the window");
    }

    // P9: holding keeps torque for a bounded grace only.
    private static void VerifyHoldingGrace()
    {
        Rig Holding()
        {
            var rig = new Rig().Arm();
            Check(rig.Captured.Ack(rig.Send("hold")) == true && rig.Gripper.Positions == 1 && rig.Agent.Mode == MotorMode.Armed, "The fixture agent did not hold");
            return rig;
        }
        var supervised = Holding();
        supervised.Advance(3 * PikaProtocol.HoldingGraceSeconds);
        Check(supervised.Gripper.Disables == 0 && supervised.Agent.Mode == MotorMode.Armed && !supervised.Agent.Finished,
            "A supervised hold with live heartbeats and telemetry was dropped");

        var abandoned = Holding();
        abandoned.Send("heartbeat");
        abandoned.Advance(PikaProtocol.HoldingGraceSeconds - .01, heartbeat: false);
        Check(abandoned.Gripper.Disables == 0 && !abandoned.Agent.Finished && PikaProtocol.HoldingGraceSeconds > PikaProtocol.ClientGoneSeconds,
            "Holding dropped the grasp before its grace period: a STOP hold must survive a brief client absence");
        abandoned.Advance(.03, heartbeat: false);
        Check(abandoned.Gripper.Disables == 1 && abandoned.Agent.Mode == MotorMode.ReadOnly && abandoned.Agent.Message.Contains("Holding ended", StringComparison.Ordinal),
            "Holding without a heartbeat stayed energized past the holding grace period");
        abandoned.Advance(PikaProtocol.ClientGoneSeconds - .5, heartbeat: false);
        var given = abandoned.Captured.LastState!.Value;
        Check(!abandoned.Agent.Finished && given.GetProperty("fresh").GetBoolean() && !given.GetProperty("enabled").GetBoolean() && !given.GetProperty("holding").GetBoolean(),
            "The agent left without reporting the disabled feedback that proves the hold was given up");
        abandoned.Advance(1, heartbeat: false);
        Check(abandoned.Agent.Finished, "The agent stayed after an unsupervised hold was given up");

        var blind = Holding();
        blind.Advance(PikaProtocol.HoldingGraceSeconds - .02, telemetry: false);
        Check(blind.Gripper.Disables == 0, "Holding without telemetry was dropped before its grace period");
        blind.Advance(.04, telemetry: false);
        Check(blind.Gripper.Disables == 1 && blind.Agent.Mode == MotorMode.ReadOnly && !blind.Agent.Finished,
            "Holding without telemetry stayed energized past the holding grace period");
    }

    private static void VerifyMalformedLines()
    {
        var rig = new Rig().Arm();
        string[] malformed = ["not json", "{}", "[1]", "null", "{\"id\":5,\"action\":\"enable\"}", "{\"id\":\"m1\",\"action\":\"bogus\"}",
            "{\"id\":\"m2\",\"action\":\"position\",\"value\":\"wide\"}", "{\"id\":\"m3\",\"action\":\"position\"}", "{\"id\":\"m4\",\"action\":\"enable\"}",
            "{\"id\":\"m5\",\"action\":\"follow-update\"}", "{\"id\":\"m6\""];
        int attempts = rig.Gripper.Attempts;
        foreach (string line in malformed) rig.Agent.Command(line);
        rig.Output.Drain(TimeSpan.FromSeconds(2));
        Check(rig.Captured.Of("ack").Count(frame => !frame.GetProperty("ok").GetBoolean()) == malformed.Length && !rig.Captured.Of("error").Any(),
            "A malformed Host line was not answered with exactly one refusal");
        Check(!rig.Agent.Finished && rig.Agent.Mode == MotorMode.Armed && rig.Gripper.Attempts == attempts, "A malformed Host line ended the agent or wrote to the gripper");
        // Malformed traffic is not a heartbeat.
        for (int i = 0; i < 160; i++) { rig.Advance(.01, heartbeat: false); rig.Agent.Command(malformed[i % malformed.Length]); }
        Check(rig.Gripper.Disables == 1 && rig.Agent.Mode == MotorMode.ReadOnly, "Malformed Host lines kept the heartbeat watchdog from disabling");
    }

    private static void VerifySerialWriteFailure()
    {
        var rig = new Rig().Arm();
        rig.Gripper.FailWrites = true;
        int attempts = rig.Gripper.Attempts;
        string id = rig.Send("disable");
        Check(rig.Captured.Ack(id) is null && rig.Captured.Of("error").Count() == 1 && rig.Agent.Finished,
            "A failed disable write was acknowledged, or reported as an ordinary refusal that proves nothing was written");
        rig.Agent.Shutdown();
        Check(rig.Gripper.Attempts == attempts + 2, "Agent exit did not attempt the disable again after a failed write");

        var first = new Rig();
        first.Advance(.05);
        first.Gripper.FailWrites = true;
        string enable = first.Send("enable", ("clear", true));
        first.Agent.Shutdown();
        Check(first.Captured.Ack(enable) is null && first.Agent.Finished && first.Gripper.Attempts == 2,
            "An enable whose write failed did not end the agent with a disable attempt: the gripper may have received it");
    }

    // P1: ACKs nobody reads end the agent through the same disable path.
    private static void VerifyUnreadOutputEndsTheAgent()
    {
        var rig = new Rig().Arm();
        rig.Captured.Open.Reset();
        var started = Stopwatch.StartNew();
        for (int i = 0; i < AgentOutput.Capacity + 2; i++) rig.Send("heartbeat");
        rig.Advance(.01, heartbeat: false);
        Check(started.Elapsed.TotalSeconds < 1 && rig.Agent.Finished && rig.Agent.Message.Contains("Host stopped reading", StringComparison.Ordinal),
            "A Host that reads no ACKs kept the agent running, or made it wait");
        rig.Agent.Shutdown();
        Check(rig.Gripper.Disables == 1, "An agent whose Host stopped reading did not disable");
        rig.Captured.Open.Set();
    }

    /// <summary>The production runner in real time: three threads, a queue for stdin.</summary>
    private sealed class Live
    {
        private readonly BlockingCollection<string?> input = new();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private volatile bool beating;
        private int nextId;
        public readonly Gripper Gripper = new();
        public readonly Captured Captured = new();
        public readonly ManualResetEvent Terminate = new(false);
        public readonly PikaAgent Agent;
        public readonly Task Running;

        public Live()
        {
            var output = new AgentOutput(Captured.Sink);
            double Now() => clock.Elapsed.TotalSeconds;
            Agent = new PikaAgent(Gripper, output, Now, PikaFollowSession.MinimumCommandPeriodSeconds);
            Running = Task.Run(() => PikaAgentRunner.Run(Agent, Gripper, () => input.Take(), Now, Terminate));
        }

        public string Send(string action, params (string Name, object Value)[] fields)
        {
            string id = "l" + Interlocked.Increment(ref nextId);
            input.Add(Line(id, action, fields));
            return id;
        }

        public void CloseInput() => input.Add(null);

        public static bool Within(double seconds, Func<bool> condition)
        {
            var waited = Stopwatch.StartNew();
            while (!condition()) { if (waited.Elapsed.TotalSeconds > seconds) return false; Thread.Sleep(5); }
            return true;
        }

        /// <summary>Arms the gripper and keeps a live client heartbeat flowing.</summary>
        public Live Arm()
        {
            Check(Within(3, () => Captured.LastState is { } state && state.GetProperty("fresh").GetBoolean()), "The runner reported no fresh telemetry");
            string id = Send("enable", ("clear", true));
            Check(Within(3, () => Captured.Ack(id) == true && Agent.Mode == MotorMode.Armed && Gripper.Enabled), "The runner did not arm the fixture gripper");
            beating = true;
            new Thread(() => { while (beating) { Send("heartbeat"); Thread.Sleep(100); } }) { IsBackground = true }.Start();
            return this;
        }

        public void StopHeartbeats() => beating = false;

        public bool Ended(double seconds) { bool ended = Running.Wait(TimeSpan.FromSeconds(seconds)); beating = false; Gripper.Release(); Captured.Open.Set(); return ended; }
    }

    // P1: the Host heartbeats but reads nothing. The old loop blocked in Console.WriteLine for ever.
    private static void VerifyRunnerBlockedStdout()
    {
        var live = new Live().Arm();
        live.Captured.Open.Reset();
        var blocked = Stopwatch.StartNew();
        bool disabled = Live.Within(AgentOutput.StallSeconds + 2, () => live.Gripper.Disables >= 1);
        Check(disabled && blocked.Elapsed.TotalSeconds < AgentOutput.StallSeconds + 1.5,
            "A blocked stdout kept the watchdog from disabling an armed gripper within its bound");
        Check(live.Ended(2) && live.Agent.Message.Contains("Host stopped reading", StringComparison.Ordinal), "The agent did not end after its Host stopped reading");
    }

    // P1: a serial read that never returns stops only the reader thread.
    private static void VerifyRunnerStalledSerial()
    {
        var live = new Live().Arm();
        live.Gripper.Stall = true;
        var stalled = Stopwatch.StartNew();
        bool disabled = Live.Within(PikaProtocol.FeedbackFreshSeconds + 2, () => live.Gripper.Disables >= 1);
        Check(disabled && stalled.Elapsed.TotalSeconds < PikaProtocol.FeedbackFreshSeconds + 1.5 && stalled.Elapsed.TotalSeconds > PikaProtocol.FeedbackFreshSeconds - .5,
            "A stalled serial read kept the watchdog from disabling within the freshness bound");
        int reported = live.Captured.Of("state").Count();
        Check(Live.Within(2, () => live.Captured.Of("state").Count() > reported + 3) && !live.Captured.LastState!.Value.GetProperty("fresh").GetBoolean() && !live.Agent.Finished,
            "The agent stopped reporting, or reported fresh feedback, while its serial read was stalled");
        string heartbeat = live.Send("heartbeat");
        Check(Live.Within(2, () => live.Captured.Ack(heartbeat) == true), "A stalled serial read blocked Host commands");
        live.CloseInput();
        Check(live.Ended(2), "The agent did not end on closed input while its serial read was stalled");
    }

    private static void VerifyRunnerExitPaths()
    {
        var closed = new Live().Arm();
        var started = Stopwatch.StartNew();
        closed.CloseInput();
        Check(Live.Within(1, () => closed.Gripper.Disables == 1) && started.Elapsed.TotalSeconds < .5 && closed.Ended(2),
            "Closed Host input did not disable an armed gripper at once");

        // P2: what SIGINT, SIGTERM and process exit set in AgentProgram.
        var terminated = new Live().Arm();
        started.Restart();
        terminated.Terminate.Set();
        Check(Live.Within(1, () => terminated.Gripper.Disables == 1) && started.Elapsed.TotalSeconds < .5 && terminated.Ended(2),
            "A termination request did not disable an armed gripper at once");

        // A gripper that was never powered receives no packet on the way out.
        var idle = new Live();
        Check(Live.Within(3, () => idle.Captured.LastState is not null), "The idle runner reported no state");
        idle.Terminate.Set();
        Check(idle.Ended(2) && idle.Gripper.Attempts == 0, "Ending a read-only agent wrote to the gripper");
    }

    // ── Linux: the real agent binary on a pty. ──

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int process, int signal);

    /// <summary>The gripper end of a pty: streams telemetry and obeys enable/disable packets.</summary>
    private sealed class PtyGripper : IDisposable
    {
        private readonly int master;
        private volatile bool enabled, stopped;
        private int disables;
        public volatile bool Feeding;
        public string Slave { get; }
        public bool Enabled => enabled;
        public int Disables => Volatile.Read(ref disables);

        public PtyGripper()
        {
            master = posix_openpt(2 /* O_RDWR */ | 0x100 /* O_NOCTTY */);
            if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0) throw new IOException("Could not open a pty for the Pika agent fixture.");
            Slave = Marshal.PtrToStringAnsi(ptsname(master)) ?? throw new IOException("The fixture pty has no name.");
            new Thread(Receive) { IsBackground = true }.Start();
            new Thread(Transmit) { IsBackground = true }.Start();
        }

        private FileStream Stream(FileAccess access) => new(new SafeFileHandle((IntPtr)master, false), access, 1, false);

        private void Receive()
        {
            try
            {
                using var stream = Stream(FileAccess.Read);
                byte[] window = new byte[7], one = new byte[1];
                while (!stopped && stream.Read(one, 0, 1) == 1)
                {
                    Array.Copy(window, 1, window, 0, 6); window[6] = one[0];
                    if (window.AsSpan().SequenceEqual(EnablePacket)) enabled = true;
                    else if (window.AsSpan().SequenceEqual(DisablePacket)) { enabled = false; Interlocked.Increment(ref disables); }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        }

        private void Transmit()
        {
            try
            {
                using var stream = Stream(FileAccess.Write);
                while (!stopped)
                {
                    if (Feeding) { stream.Write(Telemetry(enabled)); stream.Flush(); }
                    Thread.Sleep(10);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        }

        public void Dispose() { stopped = true; Feeding = false; _ = close(master); }
    }

    /// <summary>A sysfs and /dev tree naming one Pika CH340K whose tty is the pty.</summary>
    private sealed class AgentFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pika-agent-fixture-" + Guid.NewGuid().ToString("N"));
        public string Device { get; }
        public PtyGripper Gripper { get; } = new();

        public AgentFixture(int ttyNumber)
        {
            string sys = Path.Combine(Root, "sys"), dev = Path.Combine(Root, "dev"), name = "ttyUSB" + ttyNumber.ToString(CultureInfo.InvariantCulture);
            string usb = Path.Combine(sys, "devices/pci0000:00/0000:00:14.0/usb1/1-4"), tty = Path.Combine(usb, "1-4:1.0", name, "tty", name);
            Directory.CreateDirectory(tty); Directory.CreateDirectory(Path.Combine(sys, "class/tty")); Directory.CreateDirectory(dev);
            File.WriteAllText(Path.Combine(usb, "idVendor"), PikaProtocol.UsbVendorId + "\n");
            File.WriteAllText(Path.Combine(usb, "idProduct"), PikaProtocol.UsbProductId + "\n");
            Directory.CreateSymbolicLink(Path.Combine(tty, "device"), "../../../" + name);
            Directory.CreateSymbolicLink(Path.Combine(sys, "class/tty", name), tty);
            // The agent checks the USB identity of the tty it really opens: the pty behind the link.
            Directory.CreateSymbolicLink(Path.Combine(sys, "class/tty", Path.GetFileName(Gripper.Slave)), tty);
            File.CreateSymbolicLink(Path.Combine(dev, name), Gripper.Slave);
            var found = PikaSerialDevices.EnumerateLinux(sys, dev);
            Check(found.Count == 1 && found[0].Device == PikaSerialDevices.PortPrefix + "pci0000:00+0000:00:14.0+port-4", "The agent fixture tree has no USB port identity");
            Device = found[0].Device;
        }

        /// <summary>The launched agent: the one other process started with this fixture root.</summary>
        public int AgentProcessId()
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id == Environment.ProcessId) continue;
                try { if (File.ReadAllText(Path.Combine(directory, "environ")).Contains(Root, StringComparison.Ordinal)) return id; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw new InvalidOperationException("The launched Pika agent process was not found.");
        }

        public void Dispose() { Gripper.Dispose(); try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    private static async Task<bool> Eventually(double seconds, Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition()) { if (waited.Elapsed.TotalSeconds > seconds) return false; await Task.Delay(10); }
        return true;
    }

    // P8: Piper.Pika.Agent itself, launched as PikaProcessService launches it on
    // native Linux, with its real serial port, stdio, threads and signal handlers.
    private static async Task VerifyRealAgentBinary()
    {
        string agentPath = Path.Combine(AppContext.BaseDirectory, "Piper.Pika.Agent");
        Check(File.Exists(agentPath), "The Pika agent binary was not built next to the tests: " + agentPath);
        string? previous = Environment.GetEnvironmentVariable(AgentProgram.FixtureRootVariable);
        try
        {
            foreach (bool signal in new[] { false, true })
            {
                using var fixture = new AgentFixture(signal ? 98 : 97);
                Environment.SetEnvironmentVariable(AgentProgram.FixtureRootVariable, fixture.Root);
                string owner = Guid.NewGuid().ToString();
                var input = JsonSerializer.SerializeToElement(new { owner, clear = true });
                await using var service = new PikaProcessService(fixture.Device, agentPath);
                await service.ExecuteAsync("connect", input, default);
                fixture.Gripper.Feeding = true;
                Check(await Eventually(5, () => service.Snapshot()["fresh"]!.GetValue<bool>()), "The real agent reported no fresh telemetry from its serial port");
                await service.ExecuteAsync("enable", input, default);
                var enabledAt = Stopwatch.StartNew();
                Check(await Eventually(3, () => service.Snapshot()["enabled"]?.GetValue<bool>() == true) && fixture.Gripper.Enabled && fixture.Gripper.Disables == 0,
                    "The real agent did not enable the fixture gripper");
                if (!signal)
                {
                    // Host silence: no client heartbeat follows the enable.
                    Check(await Eventually(PikaProtocol.HeartbeatTimeoutSeconds + 2, () => fixture.Gripper.Disables >= 1) &&
                        enabledAt.Elapsed.TotalSeconds < PikaProtocol.HeartbeatTimeoutSeconds + 1.5,
                        "The real agent did not disable within its heartbeat bound when the Host went silent");
                    Check(await Eventually(PikaProtocol.ClientGoneSeconds + 4, () => service.Snapshot()["agent_phase"]!.GetValue<string>() == "exited"),
                        "The real agent did not exit after its client was gone");
                    var ended = service.Snapshot();
                    Check(!ended["power_uncertain"]!.GetValue<bool>() && !ended["power_requested"]!.GetValue<bool>(),
                        "The Host did not verify the watchdog disable from the agent's own post-disable feedback");
                }
                else
                {
                    await service.ExecuteAsync("heartbeat", input, default);
                    int agent = fixture.AgentProcessId();
                    var signalled = Stopwatch.StartNew();
                    Check(kill(agent, 15 /* SIGTERM */) == 0, "Could not signal the real agent");
                    Check(await Eventually(1, () => fixture.Gripper.Disables >= 1) && signalled.Elapsed.TotalSeconds < PikaProtocol.HeartbeatTimeoutSeconds - .5,
                        "SIGTERM did not make the real agent write a disable before exiting");
                    Check(await Eventually(4, () => service.Snapshot()["agent_phase"]!.GetValue<string>() == "exited"), "The real agent did not exit on SIGTERM");
                    var ended = service.Snapshot();
                    // The agent left without reporting disabled feedback, so the Host has no proof.
                    Check(ended["power_uncertain"]!.GetValue<bool>() && ended["power_requested"]!.GetValue<bool>() && !ended["connected"]!.GetValue<bool>(),
                        "An agent that was terminated without verified disabled feedback did not leave Uncertain power evidence");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable(AgentProgram.FixtureRootVariable, previous); }
    }
}
