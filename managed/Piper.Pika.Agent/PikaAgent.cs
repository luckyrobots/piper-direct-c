using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Pika.Agent;

/// <summary>The serial line to the gripper. Read may block; Write is bounded by the port's write timeout.</summary>
internal interface IPikaAgentSerial
{
    /// <summary>Returns the bytes read, or throws <see cref="TimeoutException"/> when none arrived in time.</summary>
    int Read(byte[] buffer);
    void Write(byte[] packet);
}

/// <summary>A motor packet could not be written. Never an ordinary rejection:
/// the agent cannot know what the gripper received, so it ends (disable is
/// attempted once more on the way out) and the Host keeps its power evidence.</summary>
internal sealed class PikaSerialWriteException(Exception inner) : IOException("Pika serial write failed: " + inner.Message, inner);

/// <summary>The Pika-E jaw authority: motor mode, follow session, measured-hold
/// resume and the heartbeat watchdog, with no thread, console or serial port of
/// its own. Three callers enter it, each on its own thread and all serialized
/// by one lock: <see cref="Command"/> (a Host line), <see cref="SerialBytes"/>
/// (gripper telemetry) and <see cref="Tick"/> (the watchdog timer). Tick needs
/// neither of the other two to return, so a blocked stdout, a stalled serial
/// read and a silent Host each still reach Disable. Output goes through the
/// non-blocking <see cref="AgentOutput"/>.
///   Running ──disconnect, input closed, signal, client gone, Host lost or a
///             fault──▶ Finished ──Shutdown (one disable if power was ever written)──▶ done</summary>
internal sealed class PikaAgent
{
    private readonly object gate = new();
    private readonly IPikaAgentSerial serial;
    private readonly AgentOutput output;
    private readonly Func<double> clock;

    private MotorMode mode = MotorMode.ReadOnly;
    // `enabled` is measured feedback. `wrotePower` is deliberately sticky: once any
    // power write was sent, disconnect and agent exit always write a disable.
    private bool enabled, wrotePower, finished, shutDown;
    private double feedbackAt = -100, heartbeatAt, holdingEndedAt = -100, reportedAt = -100, position, speed;
    private string lastMotorRequest = ""; private double motorRequestAt = -100;
    // The ordinary target; meaningful only while mode == Targeting.
    private double targetWidth, targetAngle, stepAt, lastTargetAt;
    // Measured arrival: openings inside the target tolerance that stay steady (see PikaStillness).
    private readonly PikaStillness arrival = new();
    private JsonElement? telemetry;
    private PikaDriverStatus? driverStatus;
    private string message = "Reading telemetry; no enable or target command sent.";
    private string stopReason = "Agent stopping.";
    private PikaPowerFeedback? powerFeedback;
    private readonly PikaDriverFaultRecorder wireEvidence = new();
    private readonly PikaFollowSession following;
    private readonly PikaMeasuredHoldResume measuredResume;

    private readonly PikaJsonStream parser = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] characters = new char[8192];
    private long serialReadCalls, serialReadTimeouts, serialBytes, validTelemetryObjects, invalidTelemetryObjects;
    private readonly Dictionary<string, long> telemetryRejectionPaths = new(StringComparer.Ordinal);
    private double previousValidAt = -1, lastValidInterval, maximumValidInterval, maximumReadSeconds, maximumTickSeconds, previousTickAt;

    public PikaAgent(IPikaAgentSerial serial, AgentOutput output, Func<double> clock, double commandPeriodSeconds)
    {
        this.serial = serial; this.output = output; this.clock = clock;
        following = new PikaFollowSession(angle => Write(PikaCommand.Position, angle), commandPeriodSeconds);
        measuredResume = new PikaMeasuredHoldResume(Write);
        heartbeatAt = previousTickAt = Now();
    }

    /// <summary>True once the agent decided to end; the caller then runs <see cref="Shutdown"/>.</summary>
    public bool Finished { get { lock (gate) return finished; } }
    public MotorMode Mode { get { lock (gate) return mode; } }
    public string Message { get { lock (gate) return message; } }

    private double Now() => clock();
    private bool Fresh() => telemetry is not null && Now() - feedbackAt < PikaProtocol.FeedbackFreshSeconds;
    private bool FollowFresh() => telemetry is not null && Now() - feedbackAt < PikaFollowSession.InputDeadlineSeconds;
    private bool HeartbeatExpired() => Now() - heartbeatAt > PikaProtocol.HeartbeatTimeoutSeconds;
    private bool IsArmed() => mode is MotorMode.Armed or MotorMode.Targeting;

    private void Write(PikaCommand command, double value = 0)
    {
        byte[] bytes = PikaProtocol.Packet(command, value);
        var attempt = wireEvidence.BeginWrite(Now(), command, value, bytes);
        try { serial.Write(bytes); PikaDriverFaultRecorder.FinishWrite(attempt, Now()); }
        catch (Exception error) { PikaDriverFaultRecorder.FinishWrite(attempt, Now(), error); throw new PikaSerialWriteException(error); }
    }

    // The single Pika-E transition function.
    private void SetMode(MotorMode next)
    {
        if (next == MotorMode.Targeting) arrival.Reset();
        mode = next;
        Debug.Assert(mode != MotorMode.Targeting || following.Phase is not (PikaFollowPhase.Following or PikaFollowPhase.Holding),
            "A Pika position target coexists with a follow session.");
        Debug.Assert((mode == MotorMode.Resuming) == measuredResume.Active, "Motor mode and measured-hold resume disagree.");
    }

    private void RequireHealthyDriver()
    {
        if (driverStatus?.HasFault == true) throw new InvalidOperationException(driverStatus.Text + "; resolve the reported fault before sending a target or enabling.");
    }

    private void Disable(string reason)
    {
        measuredResume.Cancel();
        following.Cancel(reason, Now());
        SetMode(MotorMode.ReadOnly);
        Write(PikaCommand.Disable); powerFeedback = new(false, Now()); message = reason + " Disable requested; verify motor status.";
    }

    private void RecordMotorRequest(string id) { lastMotorRequest = id; motorRequestAt = Now(); }

    private void Finish(string reason) { finished = true; stopReason = reason; }

    // A fault is terminal. The error frame is required output; the disable is written by Shutdown.
    private void Fail(Exception error)
    {
        if (finished) return;
        message = error.Message;
        output.Required(new { type = "error", connected = false, message = error.Message, follow_audit = following.Audit });
        Finish("Agent stopping.");
    }

    /// <summary>Ends the agent from outside: Host input closed, or a termination signal.</summary>
    public void Stop(string reason) { lock (gate) if (!finished) Finish(reason); }

    /// <summary>Ends the agent on a failure outside its own calls, such as a serial read error.</summary>
    public void Fault(Exception error) { lock (gate) Fail(error); }

    /// <summary>The one exit path, whatever ended the agent: if power was ever
    /// written, write disable. Idempotent; never throws.</summary>
    public void Shutdown()
    {
        lock (gate)
        {
            finished = true;
            if (shutDown) return;
            shutDown = true;
            if (IsArmed() || wrotePower) try { Disable(stopReason); } catch (Exception) { }
        }
    }

    /// <summary>Applies one Host line and queues its ACK.</summary>
    public void Command(string line)
    {
        lock (gate)
        {
            if (finished) return;
            string id = "";
            try
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement;
                id = command.GetProperty("id").GetString() ?? "";
                string action = command.GetProperty("action").GetString() ?? "";
                string? followSession = HandleCommand(action, id, command);
                output.Required(new { type = "ack", id, ok = true, message, follow_session = followSession,
                    follow_audit = action is "hold" or "disable" ? following.Audit : null });
            }
            // A rejection: nothing was written. A serial write failure is not one of these.
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
            { output.Required(new { type = "ack", id, ok = false, message = e.Message, follow_audit = following.Audit }); }
            catch (Exception e) { Fail(e); }
        }
    }

    // Applies one client command. Returns the new follow session ID for follow-start.
    private string? HandleCommand(string action, string id, JsonElement command)
    {
        switch (action)
        {
            case "heartbeat": heartbeatAt = Now(); return null;
            case "disable": Disable("Operator stop."); RecordMotorRequest(id); return null;
            case "enable":
                RequireHealthyDriver();
                if (mode == MotorMode.Resuming) throw new InvalidOperationException("Wait for measured-hold resume feedback or disable first.");
                if (!Fresh() || !command.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Fresh telemetry and finger-clear confirmation are required");
                following.Cancel("Enable requested; previous following targets cancelled.", Now());
                wrotePower = true; Write(PikaCommand.Enable);
                if (mode == MotorMode.ReadOnly) SetMode(MotorMode.Armed);
                heartbeatAt = Now(); message = "Enable requested; waiting for enabled feedback.";
                RecordMotorRequest(id);
                powerFeedback = new(true, motorRequestAt);
                return null;
            case "position":
            {
                RequireHealthyDriver();
                // Armed exactly: not read-only, not already targeting and not resuming.
                if (!Fresh() || mode != MotorMode.Armed || !enabled || HeartbeatExpired() || following.Active)
                    throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required");
                double angle = command.GetProperty("value").GetDouble(), width = PikaProtocol.Width(angle);
                if (!PikaProtocol.ValidAngle(angle) || !PikaProtocol.ValidOpening(width))
                    throw new InvalidOperationException($"Opening target must be within {PikaProtocol.MinimumOpeningMm:0}–{PikaProtocol.MaximumOpeningMm:0} mm");
                following.Cancel("Ordinary Pika position command.", Now());
                powerFeedback = null;
                Write(PikaCommand.Position, angle); targetAngle = angle; lastTargetAt = Now(); targetWidth = width; stepAt = Now();
                SetMode(MotorMode.Targeting);
                message = $"Target {width:F2} mm; waiting for measured arrival.";
                RecordMotorRequest(id);
                return null;
            }
            case "follow-start":
                RequireHealthyDriver();
                if (!FollowFresh() || mode != MotorMode.Armed || !enabled || HeartbeatExpired())
                    throw new InvalidOperationException("Fresh enabled feedback, live client and an idle gripper are required to follow.");
                powerFeedback = null;
                string session = following.Start(Now()); heartbeatAt = Now(); message = following.Message;
                return session;
            case "follow-update":
                RequireHealthyDriver();
                if (!FollowFresh() || !IsArmed() || !enabled)
                    throw new InvalidOperationException("Fresh enabled Pika feedback is required to follow.");
                following.Update(command.GetProperty("follow_session").GetString() ?? "",
                    command.GetProperty("sequence").GetInt64(), command.GetProperty("opening_mm").GetDouble(), Now());
                heartbeatAt = Now(); message = following.Message;
                return null;
            case "hold":
                if (mode == MotorMode.Resuming)
                {
                    Disable("Measured-hold resume cancelled before verification.");
                    RecordMotorRequest(id);
                    return null;
                }
                // Retire an ordinary target too, even when fresh feedback is missing.
                if (mode == MotorMode.Targeting) SetMode(MotorMode.Armed);
                powerFeedback = null;
                following.Hold(FollowFresh() && IsArmed() && enabled ? position : null, now: Now(), enabled: IsArmed() && enabled, driverStatus: driverStatus);
                heartbeatAt = Now(); message = following.Message;
                RecordMotorRequest(id);
                return null;
            case "resume-hold":
                RequireHealthyDriver();
                // ReadOnly exactly: not armed, not targeting and not already resuming.
                if (!Fresh() || Now() - feedbackAt >= PikaMeasuredHoldResume.MaximumFeedbackAgeSeconds || HeartbeatExpired() ||
                    mode != MotorMode.ReadOnly || enabled || following.Active ||
                    !command.TryGetProperty("clear", out var resumeClear) || resumeClear.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Fresh disabled feedback and a live explicit measured-hold request are required.");
                following.Cancel("Measured hold requested; prior destinations discarded.", Now()); powerFeedback = null;
                wrotePower = true; RecordMotorRequest(id);
                measuredResume.Start(motorRequestAt, feedbackAt, position, speed, enabled, pending: false, following.Active,
                    command.GetProperty("expected_angle_rad").GetDouble(), command.GetProperty("expected_feedback_time_s").GetDouble(), id);
                SetMode(MotorMode.Resuming);
                heartbeatAt = Now(); message = measuredResume.Message;
                return null;
            case "disconnect":
                // Disable before the ACK, so an acknowledged disconnect has already written it.
                if (IsArmed() || wrotePower) Disable("Session closed.");
                Finish("Session closed."); return null;
            default: throw new InvalidOperationException("Unsupported Pika action");
        }
    }

    /// <summary>One serial read that returned no bytes in time.</summary>
    public void SerialTimeout(double readSeconds)
    {
        lock (gate) { ++serialReadCalls; ++serialReadTimeouts; maximumReadSeconds = Math.Max(maximumReadSeconds, readSeconds); }
    }

    /// <summary>Bytes from the gripper: decodes telemetry and renews its age.</summary>
    public void SerialBytes(ReadOnlySpan<byte> bytes, double readSeconds)
    {
        lock (gate)
        {
            if (finished) return;
            try
            {
                ++serialReadCalls; serialBytes += bytes.Length; maximumReadSeconds = Math.Max(maximumReadSeconds, readSeconds);
                wireEvidence.Read(Now(), bytes);
                int length = decoder.GetChars(bytes, characters, flush: false);
                foreach (var data in parser.Push(new string(characters, 0, length))) ObserveTelemetry(data);
            }
            catch (Exception e) { Fail(e); }
        }
    }

    private void ObserveTelemetry(JsonElement data)
    {
        if (!PikaProtocol.TryReadTelemetry(data, out var sample, out string reason))
        {
            ++invalidTelemetryObjects;
            telemetryRejectionPaths[reason] = telemetryRejectionPaths.GetValueOrDefault(reason) + 1;
            return;
        }
        ++validTelemetryObjects;
        double validAt = Now();
        if (previousValidAt >= 0) { lastValidInterval = validAt - previousValidAt; maximumValidInterval = Math.Max(maximumValidInterval, lastValidInterval); }
        previousValidAt = validAt;
        telemetry = data; position = sample.Position; speed = sample.Speed; enabled = sample.Enabled; feedbackAt = validAt;
        driverStatus = sample.Driver;
        following.ObserveFeedback(feedbackAt, sample.Position, sample.Speed, sample.Enabled, sample.RawStatus);
        if (wireEvidence.Observe(feedbackAt, data, sample) is { } evidence)
            output.Required(new { type = "driver_fault_evidence", evidence });
        if (powerFeedback is { } acknowledgement && acknowledgement.IsConfirmed(enabled, feedbackAt))
        {
            message = enabled ? "Enabled; measured motor feedback confirmed." :
                message.Replace("Disable requested; verify motor status.", "Disabled feedback confirmed.", StringComparison.Ordinal);
            powerFeedback = null;
        }
        if (mode != MotorMode.Targeting) return;
        // Arrival is judged from the measured opening, not the per-sample speed estimate,
        // which is too noisy at rest to confirm stillness before the movement deadline.
        double opening = PikaProtocol.Width(position);
        if (Math.Abs(opening - targetWidth) > PikaProtocol.ArrivalToleranceMm) { arrival.Reset(); return; }
        if (!arrival.Observe(feedbackAt, opening)) return;
        message = $"Reached {opening:F2} mm, within {PikaProtocol.ArrivalToleranceMm:F1} mm of target.";
        SetMode(MotorMode.Armed);
    }

    /// <summary>The watchdog and control step. Called on its own timer; it
    /// waits for neither stdout nor a serial read.</summary>
    public void Tick()
    {
        lock (gate)
        {
            if (finished) return;
            try { Step(); }
            catch (Exception e) { Fail(e); }
        }
    }

    private void Step()
    {
        double tickAt = Now(); maximumTickSeconds = Math.Max(maximumTickSeconds, tickAt - previousTickAt); previousTickAt = tickAt;
        // A Host that does not read cannot supervise: same outcome as closed input.
        if (output.HostLost) { message = "Host stopped reading agent output."; Finish("Host stopped reading agent output."); return; }
        if (mode == MotorMode.Resuming)
        {
            measuredResume.Tick(Now(), feedbackAt, position, speed, enabled, Fresh());
            message = measuredResume.Message;
            if (!measuredResume.Active) SetMode(measuredResume.Succeeded ? MotorMode.Armed : MotorMode.ReadOnly);
        }
        if (following.Active)
        {
            try { following.Tick(Now(), FollowFresh() ? position : null, IsArmed() && enabled, driverStatus); message = following.Message; }
            catch (InvalidOperationException error) { message = error.Message; }
        }
        // Explicit/follow-deadline hold retains the enabled position servo, so a
        // stop request never drops a grasp. It is separate from disable, but it
        // is not unsupervised for ever: see PikaProtocol.HoldingGraceSeconds.
        if (IsArmed())
        {
            if (following.Holding)
            {
                if (Now() - heartbeatAt > PikaProtocol.HoldingGraceSeconds || Now() - feedbackAt > PikaProtocol.HoldingGraceSeconds)
                { Disable("Holding ended: no client heartbeat or telemetry within the holding grace period."); holdingEndedAt = Now(); }
            }
            else if (!Fresh() || HeartbeatExpired() || (mode == MotorMode.Targeting && Now() - stepAt > PikaProtocol.TargetDeadlineSeconds))
                Disable("Telemetry, client heartbeat or movement deadline expired.");
        }
        // The vendor example streams the current target every 30 ms. Keep the
        // same bounded target, only while feedback and the client remain live.
        if (mode == MotorMode.Targeting && enabled && Fresh() && !HeartbeatExpired() && Now() - lastTargetAt >= .03)
        { Write(PikaCommand.Position, targetAngle); lastTargetAt = Now(); }
        // After giving up a hold, stay long enough to report the disabled feedback that proves it.
        if (!following.Holding && Now() - Math.Max(heartbeatAt, holdingEndedAt) > PikaProtocol.ClientGoneSeconds) { message = "Client disconnected."; Finish("Client disconnected."); return; }
        if (Now() - reportedAt >= .1) { output.State(State()); reportedAt = Now(); }
    }

    private PikaAgentState State() => new()
    {
        Connected = true, Fresh = Fresh(), Enabled = enabled, Armed = IsArmed(), Pending = mode == MotorMode.Targeting,
        DriverStatus = driverStatus?.Bits, DriverFaultMask = driverStatus?.FaultMask,
        DriverFault = driverStatus?.HasFault, DriverStatusText = driverStatus?.Text,
        DriverFaultEvidence = wireEvidence.LastSummary,
        Following = following.Active, FollowSession = following.SessionId, FollowSequence = following.Sequence,
        FollowOpeningMm = following.OpeningMm, Holding = following.Holding,
        FollowAudit = following.Audit,
        ResumingHold = measuredResume.Active,
        LastMeasuredHoldResume = measuredResume.Audit,
        AgentDiagnostics = new {
            agent_time_s = Now(), serial_read_calls = serialReadCalls, serial_read_timeouts = serialReadTimeouts, serial_bytes = serialBytes,
            valid_telemetry_objects = validTelemetryObjects, invalid_telemetry_objects = invalidTelemetryObjects,
            telemetry_rejection_paths = telemetryRejectionPaths, parser = parser.Diagnostics,
            last_valid_decode_interval_s = lastValidInterval, maximum_valid_decode_interval_s = maximumValidInterval,
            maximum_read_and_parse_s = maximumReadSeconds, maximum_loop_interval_s = maximumTickSeconds,
            maximum_stdout_emit_s = output.MaximumWriteSeconds, stdout_queued = output.Queued, stdout_dropped_states = output.DroppedStates,
            clock_note = "Agent receive/decode intervals; not firmware sample timestamps or a hardware synchronization guarantee."
        },
        Message = message, Telemetry = telemetry, AgeSeconds = telemetry is null ? null : Now() - feedbackAt,
        LastMotorRequest = lastMotorRequest, RequestTimeSeconds = lastMotorRequest.Length == 0 ? null : motorRequestAt,
        FeedbackTimeSeconds = telemetry is null ? null : feedbackAt
    };
}
