using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

// Pika-A agent process phase. Every transition is made under stateGate.
//   Idle      ──connect──────────────▶ Launching (owner reserved, then process started)
//   Launching ──"ready" line─────────▶ Ready
//   Launching | Ready ──stdout EOF───▶ Exited   (process retained as exit evidence)
//   Launching | Ready ──read failure or a run of malformed lines──▶ Faulted
//   Faulted   ──retire (own task)────▶ Closing  (EOF, bounded wait, then tree kill)
//   any       ──CloseAsync───────────▶ Closing ──▶ Idle
// Each phase is a slot record carrying exactly the data valid in it, so these
// hold by construction: a process exists only in Launching, Ready, Exited and
// Faulted, always with an owner; Ready, Exited and Faulted always have one.
// Only Ready is "connected".
internal enum PikaAgentPhase { Idle, Launching, Ready, Exited, Faulted, Closing }

// Pika-A power evidence for motor requests this service sent.
//   any ──motor request──▶ Requested ──matching ACK──▶ Acked
//   Requested ──matching rejection (the agent wrote nothing)──▶ the evidence held before the request
//   Acked ──fresh post-request disabled feedback──▶ VerifiedDisabled
//   VerifiedDisabled ──fresh feedback that is not verified disabled──▶ Acked
//   Uncertain ──fresh disabled feedback──▶ None
//   Exited + verified disabled idle final sample ──▶ None
//   close: verified disabled ──▶ None; Requested | Acked | VerifiedDisabled ──▶ Uncertain
// Each evidence is a record carrying its requesting owner and request ID where
// valid; the unresolved owner exists exactly for Requested, Acked and Uncertain.
// After a close, power ∈ {None, Uncertain}: a failed process never proves its
// motor was disabled.
internal enum PikaPowerEvidence { None, Requested, Acked, VerifiedDisabled, Uncertain }

/// <summary>Reusable C# owner of a Linux serial agent, local or through WSL.
/// The Windows process never opens a WCH COM port.</summary>
public sealed class PikaProcessService : IPikaSession
{
    // A run of unparseable stdout lines means the agent is not speaking this
    // protocol. Isolated bad lines are skipped: they only age the evidence.
    internal const int MaximumConsecutiveMalformedLines = 32;
    private const int MaximumLineCharacters = 32768;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    private string device;
    private readonly string agentPath;
    private readonly double commandPeriodSeconds;
    private readonly bool directLaunch;
    private string? distribution;
    public PikaProcessService(string device, string agentPath, string? distribution = null,
        double commandPeriodSeconds = PikaFollowSession.MinimumCommandPeriodSeconds)
    {
        _ = new PikaFollowSession(_ => { }, commandPeriodSeconds); // Validate without opening a transport.
        this.device = device; this.agentPath = agentPath; this.distribution = distribution; this.commandPeriodSeconds = commandPeriodSeconds;
    }

    // Test fixtures run the agent executable directly on every OS, bypassing WSL.
    internal PikaProcessService(string device, string fixtureAgent, bool directLaunch) : this(device, fixtureAgent) =>
        this.directLaunch = directLaunch;

    private readonly SemaphoreSlim operations = new(1);
    private readonly object stateGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    // ── Pika-A agent slots. ──

    // One launched agent. Pump is set once, under stateGate, right after the
    // slot holding this agent is published (the pump needs that slot to publish).
    private sealed class AgentProcess(Process process, Task<string> stderr)
    {
        public Process Process { get; } = process;
        public Task<string> Stderr { get; } = stderr;
        public Task? Pump { get; set; }
    }

    private abstract record AgentSlot(PikaAgentPhase Phase);
    private sealed record IdleSlot() : AgentSlot(PikaAgentPhase.Idle);
    private abstract record OwnedSlot(PikaAgentPhase Phase, string Owner) : AgentSlot(Phase);
    // Owner reserved; Agent is set once the process has started.
    private sealed record LaunchingSlot(string Owner, AgentProcess? Agent = null) : OwnedSlot(PikaAgentPhase.Launching, Owner);
    private abstract record LiveSlot(PikaAgentPhase Phase, string Owner, AgentProcess Agent) : OwnedSlot(Phase, Owner);
    private sealed record ReadySlot(string Owner, AgentProcess Agent) : LiveSlot(PikaAgentPhase.Ready, Owner, Agent);
    // The exited process is retained as exit evidence.
    private sealed record ExitedSlot(string Owner, AgentProcess Agent) : LiveSlot(PikaAgentPhase.Exited, Owner, Agent);
    private sealed record FaultedSlot(string Owner, AgentProcess Agent) : LiveSlot(PikaAgentPhase.Faulted, Owner, Agent);
    // The process is detached and being stopped; the owner stays until Idle.
    private sealed record ClosingSlot(string Owner) : OwnedSlot(PikaAgentPhase.Closing, Owner);

    // ── Pika-A power evidence records. ──

    private abstract record PowerState(PikaPowerEvidence Evidence);
    private sealed record NoPower() : PowerState(PikaPowerEvidence.None);
    // A motor request this service sent: its owner and agent request ID.
    private abstract record SentPower(PikaPowerEvidence Evidence, string Owner, string RequestId) : PowerState(Evidence);
    private sealed record RequestedPower(string Owner, string RequestId) : SentPower(PikaPowerEvidence.Requested, Owner, RequestId);
    private sealed record AckedPower(string Owner, string RequestId) : SentPower(PikaPowerEvidence.Acked, Owner, RequestId);
    private sealed record VerifiedDisabledPower(string Owner, string RequestId) : SentPower(PikaPowerEvidence.VerifiedDisabled, Owner, RequestId);
    // Closing a failed process does not prove that its motor was disabled. Keep
    // this identity until fresh disabled feedback is observed on the same port.
    private sealed record UncertainPower(string Owner) : PowerState(PikaPowerEvidence.Uncertain);

    private AgentSlot slot = new IdleSlot();
    private PowerState power = new NoPower();
    private JsonObject? state;
    private PikaAgentState? agentState;
    private PikaFollowAudit? followAudit;
    private readonly Queue<PikaDriverFaultEvidence> driverFaultEvidence = new();
    private string error = "Not connected";
    private long stateAt;
    private bool Configured => !string.IsNullOrWhiteSpace(device);

    // Derived views. Callers hold stateGate.
    private string? Owner => (slot as OwnedSlot)?.Owner;
    private (string Owner, AgentProcess Agent)? Attached => slot switch
    {
        LaunchingSlot { Agent: { } agent } launching => (launching.Owner, agent),
        LiveSlot live => (live.Owner, live.Agent),
        _ => null
    };
    private bool IsAttached(Process agent) => Attached is { } current && ReferenceEquals(current.Agent.Process, agent);
    private string? UnresolvedOwner => power switch
    {
        RequestedPower requested => requested.Owner,
        AckedPower acked => acked.Owner,
        UncertainPower uncertain => uncertain.Owner,
        _ => null
    };
    private bool PowerRequested => power is SentPower;

    public PikaSessionStatus Status
    {
        get { lock (stateGate) return new(slot is ReadySlot, Owner, ConfigurationBlocked()); }
    }

    internal PikaAgentPhase Phase { get { lock (stateGate) return slot.Phase; } }

    public async Task<PikaHoldResult> HoldAsync(CancellationToken cancellation)
    {
        const string noLiveSession = "Pika power is unverified and there is no live session to hold.";
        const string retiring = "Pika agent is retiring: closing its input makes it disable the gripper; its exit is not yet confirmed.";
        Process agent; string owner;
        lock (stateGate)
        {
            // A faulted or closing agent cannot take a hold: its retirement
            // (EOF, bounded wait, tree kill) owns the operation gate, and a hold
            // queued behind it would only time out. Report that retirement,
            // still unverified, instead of waiting for it.
            bool retiringAgent = slot is FaultedSlot or ClosingSlot;
            // Nothing attached and no unresolved power: nothing to hold. Pin
            // the attached process so a queued STOP cannot reach its replacement.
            if (Attached is not { } current)
                return UnresolvedOwner is null ? new(PikaHoldOutcome.Skipped)
                    : new(PikaHoldOutcome.Unverified, retiringAgent ? retiring : noLiveSession);
            if (retiringAgent) return new(PikaHoldOutcome.Unverified, retiring);
            (owner, agent) = (current.Owner, current.Agent.Process);
        }

        await operations.WaitAsync(cancellation);
        try
        {
            bool live;
            lock (stateGate) live = IsAttached(agent) && Owner == owner && slot is not FaultedSlot && !agent.HasExited;
            if (!live) return new(PikaHoldOutcome.Unverified, noLiveSession);
            // The hold is sent in the session owner's name, whoever asked for it.
            var reply = await ExecuteAgentAsync(agent, "hold", default, owner, cancellation);
            return new(PikaHoldOutcome.Acknowledged, reply["message"]?.GetValue<string>());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(PikaHoldOutcome.Unverified, error.Message);
        }
        finally { operations.Release(); }
    }

    public JsonObject Snapshot(bool includeRawEvidence = false)
    {
        lock (stateGate)
        {
            double age = EvidenceAgeSeconds();
            bool connected = slot is ReadySlot;
            var snapshot = state?.DeepClone().AsObject() ?? new JsonObject();
            snapshot.Remove("driver_fault_evidence_raw");
            if (driverFaultEvidence.LastOrDefault() is { } fault)
                snapshot["driver_fault_evidence"] = JsonSerializer.SerializeToNode(fault.Summary);
            if (includeRawEvidence) snapshot["driver_fault_evidence_raw"] = JsonSerializer.SerializeToNode(driverFaultEvidence.ToArray());
            if (followAudit is not null) snapshot["follow_audit"] = JsonSerializer.SerializeToNode(followAudit);
            WriteSessionFields(snapshot, device, distribution, commandPeriodSeconds);
            snapshot["owner"] = Owner;
            snapshot["power_requested"] = PowerRequested;
            snapshot["power_uncertain"] = UnresolvedOwner is not null;
            // A retained power reservation also exists during a healthy enabled
            // session. Maintenance must distinguish that from an unresolved
            // request, using private ACK identity and post-request feedback.
            snapshot["post_request_feedback_verified"] = HasPostRequestEvidence();
            snapshot["configuration_blocked"] = ConfigurationBlocked();
            snapshot["agent_phase"] = slot.Phase.ToString().ToLowerInvariant();
            snapshot["connected"] = connected;
            // A client may adopt only this privately verified dead, unpowered
            // observer. ExecuteAsync rechecks this under the mutation lock.
            snapshot["read_only_adoptable"] = ReadOnlyAdoptable();
            // The agent's age is relative to its last emission. Do not leave
            // that cached value looking current after its process has exited.
            snapshot["age_s"] = double.IsFinite(age) ? age : null;
            snapshot["fresh"] = connected && age < PikaProtocol.FeedbackFreshSeconds && agentState?.Fresh == true;
            snapshot["message"] = connected ? agentState?.Message ?? "Waiting for telemetry" : error;
            return snapshot;
        }
    }

    /// <summary>The service-owned snapshot fields, shared with the registry's
    /// placeholder for a device that has no session yet.</summary>
    internal static void WriteSessionFields(JsonObject snapshot, string device, string? distribution, double commandPeriodSeconds)
    {
        bool configured = !string.IsNullOrWhiteSpace(device);
        snapshot["configured"] = configured;
        snapshot["device"] = device;
        snapshot["distribution"] = distribution;
        snapshot["transport"] = configured ? PikaDistribution.Transport(distribution) : OperatingSystem.IsWindows() ? "disabled" : "browser";
        // Transport cadence is not a measured/verified physical opening rate.
        snapshot["follow_minimum_command_period_s"] = commandPeriodSeconds;
        snapshot["follow_maximum_command_hz"] = 1 / commandPeriodSeconds;
        snapshot["verified_maximum_opening_speed_mm_s"] = null;
        snapshot["physical_replay_available"] = false;
        snapshot["measured_hold_resume_supported"] = true;
    }

    public async Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
    {
        string requestedOwner = input.TryGetProperty("owner", out var value) ? value.GetString() ?? "" : "";
        if (!Guid.TryParse(requestedOwner, out _)) throw new ArgumentException("A dashboard Pika owner ID is required");
        await operations.WaitAsync(cancellation);
        try
        {
            RequireReadOnlyAdoptionAllowed(action, input);
            // A faulted agent is retired before anything else under this gate,
            // even if its own retirement task has not run yet. A dead observer
            // owns no motor state: release that stale serial reservation, but
            // retain every failed power/position attempt.
            string? fault; bool deadObserver;
            lock (stateGate)
            {
                fault = slot is FaultedSlot ? error : null;
                deadObserver = Attached?.Agent.Process is { HasExited: true } && power is NoPower;
            }
            if (fault is not null) await CloseAsync(fault);
            else if (deadObserver) await CloseAsync();
            if (action == "configure") return Configure(input, requestedOwner);
            if (!Configured) throw new InvalidOperationException("Pika Linux serial transport has not been configured");
            if (action == "connect") return await ConnectAsync(input, requestedOwner, cancellation);
            Process? currentAgent;
            lock (stateGate) currentAgent = Attached is { } current && current.Owner == requestedOwner ? current.Agent.Process : null;
            if (currentAgent is null) throw new InvalidOperationException("This dashboard does not own the Pika session");
            return await ExecuteAgentAsync(currentAgent, action, input, requestedOwner, cancellation);
        }
        finally { operations.Release(); }
    }

    private void RequireReadOnlyAdoptionAllowed(string action, JsonElement input)
    {
        if (!input.TryGetProperty("read_only_adopt", out var readOnlyAdopt)) return;
        if (readOnlyAdopt.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("read_only_adopt must be true or false.");
        if (readOnlyAdopt.ValueKind == JsonValueKind.False) return;
        bool allowed;
        lock (stateGate) allowed = action == "connect" && TakeOver(input) && ReadOnlyAdoptable();
        if (!allowed)
            throw new InvalidOperationException("Read-only adoption requires a confirmed exited, unpowered, idle Pika monitor. A connected, powered or unverified session was not changed.");
    }

    private static bool TakeOver(JsonElement input) => input.TryGetProperty("take_over", out var takeOver) && takeOver.ValueKind == JsonValueKind.True;

    // Caller holds the operation gate.
    private JsonObject Configure(JsonElement input, string requestedOwner)
    {
        lock (stateGate)
        {
            // A dead process may have left enabled hardware behind. Never
            // discard its owner/state as a side effect of changing routing.
            if (ConfigurationBlocked())
                throw new InvalidOperationException((Owner ?? UnresolvedOwner) != requestedOwner
                    ? "Another dashboard owns the Pika session. It must verify disabled feedback and disconnect before changing the serial device."
                    : "Verify disabled Pika feedback and disconnect this session before changing its serial device. Reconnect the same device if its agent exited.");
            string nextDevice = input.GetProperty("device").GetString() ?? "";
            string? nextDistribution = input.TryGetProperty("distribution", out var distro) ? distro.GetString() : distribution;
            ValidateConfiguration(nextDevice, nextDistribution);
            device = nextDevice; distribution = nextDistribution; state = null; agentState = null; stateAt = 0; error = "Configured; connect to read telemetry.";
            return new JsonObject { ["ok"] = true, ["message"] = "Pika serial assignment changed. No device was opened or motor command sent.", ["device"] = device, ["distribution"] = distribution };
        }
    }

    // Caller holds the operation gate.
    private async Task<JsonObject> ConnectAsync(JsonElement input, string requestedOwner, CancellationToken cancellation)
    {
        bool takeOver = TakeOver(input), exited;
        lock (stateGate)
        {
            if ((Owner ?? UnresolvedOwner) is { } previousOwner && previousOwner != requestedOwner && !takeOver)
                throw new InvalidOperationException("Pika is controlled by another dashboard tab");
            exited = Attached?.Agent.Process is { HasExited: true };
        }
        if (exited) await CloseAsync();
        bool replace;
        lock (stateGate)
        {
            if (Attached is { } current && current.Owner == requestedOwner) return new JsonObject { ["ok"] = true, ["message"] = "Session already connected" };
            replace = Attached is not null;
            // A foreign owner already required take_over above. Owners change
            // only under this gate; the pump changes just the phase.
            Debug.Assert(!replace || takeOver, "A foreign live Pika session reached replacement without take_over.");
        }
        // EOF disables the old session before another agent opens the port.
        // Ownership changes never replay a position target.
        if (replace) await CloseAsync();
        lock (stateGate) slot = new LaunchingSlot(requestedOwner);
        try
        {
            // Setup failures, including WSL path resolution before process
            // launch, must release a read-only reservation as well.
            var info = await AgentStartInfoAsync(cancellation);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var agent = BoundedProcess.Start(info, "Could not start the Pika serial agent.");
            var stderr = agent.StandardError.ReadToEndAsync();
            var launched = new AgentProcess(agent, stderr);
            lock (stateGate) slot = new LaunchingSlot(requestedOwner, launched);
            var pump = PumpAsync(agent, ready, stderr);
            lock (stateGate) launched.Pump = pump;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
        }
        catch { await CloseAsync(); throw; }
        return new JsonObject { ["ok"] = true, ["message"] = "Linux serial connected; reading only" };
    }

    private async Task<ProcessStartInfo> AgentStartInfoAsync(CancellationToken cancellation)
    {
        var info = new ProcessStartInfo { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (distribution is not null && !directLaunch)
        {
            info.FileName = "wsl.exe";
            info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(distribution);
            string linuxAgent = agentPath.StartsWith('/') ? agentPath : await ResolveWslPath(agentPath, distribution, cancellation);
            info.ArgumentList.Add("--exec"); info.ArgumentList.Add(linuxAgent);
        }
        else
        {
            if (!OperatingSystem.IsLinux() && !directLaunch) throw new InvalidOperationException("Native Windows WCH serial is disabled; configure the WSL transport");
            if (OperatingSystem.IsLinux() && File.Exists(agentPath)) File.SetUnixFileMode(agentPath, File.GetUnixFileMode(agentPath) | UnixFileMode.UserExecute);
            info.FileName = agentPath;
        }
        info.ArgumentList.Add("--device"); info.ArgumentList.Add(device);
        info.ArgumentList.Add("--command-period-ms"); info.ArgumentList.Add((commandPeriodSeconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return info;
    }

    // Caller holds the operation gate and has verified ownership of this agent.
    private async Task<JsonObject> ExecuteAgentAsync(Process agent, string action, JsonElement input, string requestedOwner,
        CancellationToken cancellation)
    {
        if (action is not ("heartbeat" or "enable" or "disable" or "position" or "disconnect" or "hold" or "follow-start" or "follow-update" or "resume-hold")) throw new ArgumentException("Unsupported Pika action");
        if (action == "disconnect" && agent.HasExited)
        {
            bool unresolved;
            lock (stateGate) unresolved = UnresolvedOwner is not null || PowerRequested && !KnownDisabled();
            if (unresolved)
                throw new InvalidOperationException("The Pika agent exited without verified disabled feedback. Reconnect the same device, disable it and verify feedback before changing its assignment.");
            await CloseAsync(); return new JsonObject { ["ok"] = true, ["message"] = "Disconnected" };
        }
        // Coordinated STOP uses this same hold action. Do not turn an
        // already verified free-jaw state into a failed servo command or a
        // new unresolved power reservation. Both ownership and the private
        // acknowledged-request evidence remain serialized with mutations.
        // A gripper this session never powered has no servo to hold: fresh
        // disabled feedback with no power evidence is the same verified result.
        string? disabledMessage = null;
        if (action == "hold")
            lock (stateGate)
                if (KnownDisabled())
                {
                    var current = Snapshot();
                    disabledMessage = PikaDisabledEvidence.Confirmed(current, requestedOwner) ? "Pika already verified disabled; no motor command sent." :
                        power is NoPower && PikaDisabledEvidence.NeverPowered(current, requestedOwner)
                            ? "Pika was never powered by this session and reports disabled; no motor command sent." : null;
                }
        if (disabledMessage is not null)
            return new JsonObject { ["ok"] = true, ["state"] = "disabled", ["message"] = disabledMessage };
        string id = Guid.NewGuid().ToString("N");
        var command = new JsonObject { ["id"] = id, ["action"] = action };
        if (action == "enable") command["clear"] = input.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True;
        if (action == "resume-hold")
        {
            bool disabled;
            lock (stateGate) disabled = KnownDisabled();
            if (!disabled || !input.TryGetProperty("clear", out var supported) || supported.ValueKind != JsonValueKind.True ||
                !input.TryGetProperty("expected_angle_rad", out var expected) || !expected.TryGetDouble(out double angle) || !double.IsFinite(angle) ||
                !input.TryGetProperty("expected_feedback_time_s", out var expectedTime) || !expectedTime.TryGetDouble(out double feedbackTime) || !double.IsFinite(feedbackTime))
                throw new InvalidOperationException("Confirm a stationary measured hold with verified disabled Pika feedback before resuming.");
            command["clear"] = true; command["expected_angle_rad"] = angle; command["expected_feedback_time_s"] = feedbackTime;
        }
        if (action == "position")
        {
            if (!input.TryGetProperty("value", out var target) || target.ValueKind != JsonValueKind.Number ||
                !target.TryGetDouble(out double position) || !double.IsFinite(position))
                throw new ArgumentException("Pika position requires a finite numeric value.");
            command["value"] = position;
        }
        if (action == "follow-update")
        {
            if (!input.TryGetProperty("follow_session", out var session) || session.ValueKind != JsonValueKind.String ||
                !input.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out long nextSequence) || nextSequence <= 0 ||
                !input.TryGetProperty("opening_mm", out var opening) || opening.ValueKind != JsonValueKind.Number || !opening.TryGetDouble(out double nextOpening) || !PikaProtocol.ValidOpening(nextOpening))
                throw new ArgumentException($"Pika follow-update needs its session, a positive integer sequence and an opening within {PikaProtocol.MinimumOpeningMm:0}–{PikaProtocol.MaximumOpeningMm:0} mm.");
            command["follow_session"] = session.GetString();
            command["sequence"] = nextSequence;
            command["opening_mm"] = nextOpening;
        }
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        bool motorRequest = action is "enable" or "position" or "disable" or "hold" or "follow-update" or "resume-hold";
        bool disconnected = false;
        PowerState? beforeRequest = null;
        try
        {
            if (motorRequest)
                lock (stateGate) { beforeRequest = power; power = new RequestedPower(requestedOwner, id); }
            await agent.StandardInput.WriteLineAsync(command.ToJsonString().AsMemory(), cancellation);
            await agent.StandardInput.FlushAsync(cancellation);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            // The pump validated ok (bool) and message (string or null) before completing this request.
            if (!response.GetProperty("ok").GetBoolean())
            {
                PikaFollowAudit? rejected;
                lock (stateGate)
                {
                    // A rejection is the agent's proof that it wrote nothing for
                    // this request (a failed serial write ends the agent instead),
                    // so the evidence held before the request still stands.
                    if (beforeRequest is not null && power is RequestedPower refused && refused.RequestId == id) power = beforeRequest;
                    RetainFollowAudit(response); rejected = followAudit;
                }
                string reason = AckMessage(response) ?? "Pika request rejected";
                if (rejected is not null) throw new PikaFollowRejectedException(reason, rejected);
                throw new InvalidOperationException(reason);
            }
            cancellation.ThrowIfCancellationRequested();
            if (motorRequest)
                lock (stateGate)
                {
                    if (power is RequestedPower requested && requested.RequestId == id) power = new AckedPower(requested.Owner, id);
                    if (KnownDisabled()) ResolveDisabled();
                    RetireVerifiedEndedPower();
                }
            disconnected = action == "disconnect";
            var result = new JsonObject { ["ok"] = true, ["message"] = AckMessage(response) };
            if (motorRequest) result["motor_request_id"] = id;
            if (response.TryGetProperty("follow_session", out var followSession) && followSession.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                result["follow_session"] = followSession.GetString();
            return result;
        }
        finally { pending.TryRemove(id, out _); if (disconnected) await CloseAsync(); }
    }

    private static string? AckMessage(JsonElement response) =>
        response.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null;

    // ── Pika-A transition functions. Callers hold stateGate. ──

    // Fresh feedback proves the motor disabled (KnownDisabled already checked).
    private void ResolveDisabled()
    {
        if (power is AckedPower acked) power = new VerifiedDisabledPower(acked.Owner, acked.RequestId);
        else if (power is UncertainPower) power = new NoPower();
    }

    private bool ConfigurationBlocked() => slot is OwnedSlot || UnresolvedOwner is not null;

    // Caller holds stateGate. Cached public booleans cannot grant takeover:
    // private process-exit and power-request evidence are authoritative.
    private bool ReadOnlyAdoptable() => slot is ExitedSlot && power is NoPower && pending.IsEmpty &&
        agentState is { Enabled: false, Armed: false, Pending: false, Following: false } && agentState.ResumingHold != true;

    // Caller holds stateGate.
    private bool KnownDisabled() => EvidenceAgeSeconds() < PikaProtocol.FeedbackFreshSeconds &&
        agentState is { Fresh: true, Enabled: false } && (!PowerRequested || HasPostRequestEvidence());

    // Receipt order is insufficient: an old disabled packet may have been
    // buffered before enable. Match the acknowledged mutation and compare
    // hardware-feedback/request times from the same agent monotonic clock.
    private bool HasPostRequestEvidence() => power is SentPower { Evidence: PikaPowerEvidence.Acked or PikaPowerEvidence.VerifiedDisabled } sent &&
        sent.RequestId.Length > 0 && agentState?.LastMotorRequest == sent.RequestId &&
        agentState.RequestTimeSeconds is { } requestTime && double.IsFinite(requestTime) && requestTime >= 0 &&
        agentState.FeedbackTimeSeconds is { } feedbackTime && double.IsFinite(feedbackTime) && feedbackTime > requestTime;

    private void RetireVerifiedEndedPower()
    {
        if (slot is ExitedSlot && KnownDisabled() && agentState is { Armed: false, Pending: false })
            power = new NoPower();
    }

    // Call only under stateGate. Missing/invalid source age is not fresh
    // evidence, even if a buffered state line was received very recently.
    private double EvidenceAgeSeconds() => stateAt != 0 && agentState?.AgeSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0
        ? seconds + Stopwatch.GetElapsedTime(stateAt).TotalSeconds : double.PositiveInfinity;

    public static void ValidateConfiguration(string device, string? distribution)
    {
        if (device.Length > 1024 || device.Any(char.IsControl) || (device.Length > 0 && !device.StartsWith("/dev/", StringComparison.Ordinal)))
            throw new ArgumentException("Choose this gripper's Linux USB device identity from Settings, or leave it unassigned.");
        PikaDistribution.Validate(distribution);
    }

    internal static async Task<string> ResolveWslPath(string path, string distribution, CancellationToken cancellation)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The bundled Pika agent is missing. Reinstall the Pipers package.", path);
        var conversion = await BoundedProcess.RunAsync("wsl.exe", ["--distribution", distribution, "--exec", "wslpath", "-a", Path.GetFullPath(path)],
            "Could not start WSL path conversion.", "WSL did not respond while locating the bundled Pika agent.", cancellation);
        string linux = conversion.Output.Trim();
        if (conversion.ExitCode != 0 || !linux.StartsWith('/') || linux.Contains('\n'))
            throw new IOException("WSL could not locate the bundled Pika agent. " + conversion.Error.Trim());
        // ZIP extraction on Windows does not retain Unix execute bits. Change
        // only this user's packaged apphost, without root or global installation.
        var chmod = await BoundedProcess.RunAsync("wsl.exe", ["--distribution", distribution, "--exec", "chmod", "u+x", linux],
            "Could not prepare the bundled Pika agent.", "WSL timed out while preparing the packaged Pika agent.", cancellation);
        if (chmod.ExitCode != 0)
            throw new IOException("WSL cannot execute the packaged Pika agent. Move the plugin to a user-writable WSL-accessible location. " + chmod.Error.Trim());
        return linux;
    }

    // Called only under stateGate. ACKs carry the terminal event immediately;
    // waiting for a100ms state emission can otherwise lose its cause to cleanup.
    private void RetainFollowAudit(JsonElement data)
    {
        try
        {
            if (!data.TryGetProperty("follow_audit", out var audit)) return;
            var parsed = audit.Deserialize<PikaFollowAudit>();
            if (parsed is null || parsed.RecentEvents is null || parsed.RecentFeedback is null || parsed.RecentEvents.Count > 16 || parsed.RecentFeedback.Count > 4 ||
                parsed.LastRetirement is { } retired && (retired.RecentEvents is null || retired.RecentFeedback is null || retired.RecentEvents.Count > 16 || retired.RecentFeedback.Count > 4)) return;
            followAudit = parsed;
        }
        catch (JsonException) { /* Malformed diagnostics cannot change control or stream ownership. */ }
    }

    private void RetainDriverFaultEvidence(JsonElement data)
    {
        try
        {
            if (!data.TryGetProperty("evidence", out var value)) return;
            var evidence = value.Deserialize<PikaDriverFaultEvidence>();
            if (evidence is null || evidence.Summary is null || evidence.RawTelemetryJson is null || evidence.RawTelemetryJson.Length > 4096 ||
                evidence.RecentFeedback is null || evidence.RecentFeedback.Count > 4 || evidence.RecentReads is null || evidence.RecentReads.Count > 16 ||
                evidence.RecentWrites is null || evidence.RecentWrites.Count > 16 ||
                evidence.RecentReads.Any(r => r is null || r.PrefixBase64 is null || r.SuffixBase64 is null || r.PrefixBase64.Length + r.SuffixBase64.Length > 344) ||
                evidence.RecentWrites.Any(w => w is null || w.PacketBase64 is null || w.PacketBase64.Length > 12)) return;
            if (driverFaultEvidence.Any(previous => previous.TransitionOrdinal == evidence.TransitionOrdinal)) return;
            driverFaultEvidence.Enqueue(evidence); while (driverFaultEvidence.Count > 4) driverFaultEvidence.Dequeue();
        }
        catch (JsonException) { /* Evidence is passive; a malformed report never changes control. */ }
    }

    // Reads the agent's stdout until EOF. After a detach or a fault the pump
    // keeps draining, so a blocked agent write can never stall its own
    // heartbeat/Disable watchdog while it shuts down.
    private async Task PumpAsync(Process agent, TaskCompletionSource sessionReady, Task<string> stderr)
    {
        bool draining = false;
        int malformed = 0;
        try
        {
            while (await agent.StandardOutput.ReadLineAsync() is { } line)
            {
                if (draining) continue;
                bool published;
                try
                {
                    if (line.Length > MaximumLineCharacters) throw new FormatException("Pika agent line exceeds its size limit.");
                    using var document = JsonDocument.Parse(line);
                    published = PublishAgentMessage(agent, sessionReady, document.RootElement);
                }
                catch (Exception e) when (e is JsonException or FormatException)
                {
                    // Skipping a line is safe: it renews no evidence age, completes no
                    // request and grants nothing. Only a sustained run is a fault.
                    if (++malformed < MaximumConsecutiveMalformedLines) continue;
                    Fault(agent, sessionReady, $"Pika agent sent {malformed} consecutive malformed lines; the agent was stopped. Last: {e.Message}");
                    draining = true; continue;
                }
                malformed = 0;
                draining = !published;
            }
            string details = await stderr;
            await agent.WaitForExitAsync();
            lock (stateGate)
            {
                if (Attached is not { } current || !ReferenceEquals(current.Agent.Process, agent) || slot is FaultedSlot) return;
                slot = new ExitedSlot(current.Owner, current.Agent);
                // The old client's heartbeat can expire during an editor
                // reload. Its agent disables first and then exits. A verified
                // disabled, idle final sample releases only that obsolete
                // power-request bookkeeping, so a new read-only monitor can
                // reopen the unchanged serial assignment. Never infer a
                // disable from EOF alone or from enabled/stale/pending data.
                RetireVerifiedEndedPower();
                if (!string.IsNullOrWhiteSpace(details)) error = details.Length > 2048 ? details[..2048] : details;
                if (string.IsNullOrEmpty(error)) error = "Pika agent disconnected; reconnect explicitly";
                FailAgentRequests(sessionReady, new IOException(error));
            }
        }
        catch (Exception e)
        {
            // The pipe itself failed; nothing more can be read from this agent.
            Fault(agent, sessionReady, e.Message);
        }
    }

    // B2: a faulted agent can no longer report. Fail its requests now, then
    // close it on its own task: EOF lets it disable a gripper it enabled, and
    // the process tree is killed if it does not exit in time. Power evidence
    // becomes Uncertain unless fresh disabled feedback was already verified.
    private void Fault(Process agent, TaskCompletionSource sessionReady, string reason)
    {
        lock (stateGate)
        {
            if (Attached is not { } current || !ReferenceEquals(current.Agent.Process, agent) || slot is FaultedSlot) return;
            slot = new FaultedSlot(current.Owner, current.Agent);
            error = reason;
            FailAgentRequests(sessionReady, new IOException(reason));
        }
        _ = RetireFaultedAgentAsync(agent, reason);
    }

    private async Task RetireFaultedAgentAsync(Process agent, string reason)
    {
        await operations.WaitAsync();
        try
        {
            bool current;
            lock (stateGate) current = IsAttached(agent);
            if (current) await CloseAsync(reason);
        }
        finally { operations.Release(); }
    }

    // Validates the message shape before taking the lock, so a malformed line
    // throws without changing any state. Returns false once this agent has been
    // detached: the Process instance is the generation.
    internal bool PublishAgentMessage(Process agent, TaskCompletionSource sessionReady, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            throw new FormatException("Pika agent message has no type.");
        string type = typeProperty.GetString()!;
        PikaAgentState? parsedState = type == "state" ? data.Deserialize<PikaAgentState>() : null;
        string? ackId = null;
        if (type == "ack")
        {
            if (!data.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !data.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                data.TryGetProperty("message", out var ackMessage) && ackMessage.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new FormatException("Malformed Pika agent acknowledgement.");
            ackId = id.GetString()!;
        }
        string? errorMessage = null;
        if (type == "error" && data.TryGetProperty("message", out var failure))
            errorMessage = failure.ValueKind switch
            {
                JsonValueKind.String => failure.GetString(),
                JsonValueKind.Null => null,
                _ => throw new FormatException("Malformed Pika agent error.")
            };
        lock (stateGate)
        {
            // Detach and publication share this lock, including ACKs and terminal request failures.
            if (Attached is not { } current || !ReferenceEquals(current.Agent.Process, agent)) return false;
            switch (type)
            {
                case "ready":
                    if (slot is LaunchingSlot) { slot = new ReadySlot(current.Owner, current.Agent); error = ""; }
                    sessionReady.TrySetResult();
                    break;
                case "state":
                    RetainFollowAudit(data);
                    state = JsonNode.Parse(data.GetRawText())!.AsObject(); agentState = parsedState; stateAt = Stopwatch.GetTimestamp();
                    if (agentState!.Fresh == true)
                    {
                        if (KnownDisabled()) ResolveDisabled();
                        else if (power is VerifiedDisabledPower verified) power = new AckedPower(verified.Owner, verified.RequestId);
                        // Observing externally enabled hardware creates no power ownership.
                    }
                    break;
                case "ack":
                    RetainFollowAudit(data);
                    if (pending.TryRemove(ackId!, out var completion)) completion.TrySetResult(data.Clone());
                    break;
                case "driver_fault_evidence":
                    RetainDriverFaultEvidence(data);
                    break;
                case "error":
                    RetainFollowAudit(data); error = errorMessage ?? "Pika agent failed";
                    break;
            }
            return true;
        }
    }

    private void FailAgentRequests(TaskCompletionSource sessionReady, Exception failure)
    {
        sessionReady.TrySetException(failure);
        foreach (var item in pending) item.Value.TrySetException(failure);
    }

    // Caller holds the operation gate. B16: EOF first so the agent can disable
    // what it enabled, a bounded wait, then the whole tree (wsl.exe included)
    // is killed; the pump and stderr reader are awaited before disposal.
    private async Task CloseAsync(string? finalError = null)
    {
        Process? agent; Task? pump; Task<string>? stderr;
        lock (stateGate)
        {
            if (KnownDisabled()) power = new NoPower();
            else if (power is SentPower sent) power = new UncertainPower(sent.Owner);
            var current = Attached;
            agent = current?.Agent.Process; pump = current?.Agent.Pump; stderr = current?.Agent.Stderr;
            slot = current is { } closing ? new ClosingSlot(closing.Owner) : new IdleSlot();
        }
        if (agent is not null)
        {
            try { agent.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
            if (!await ExitedWithin(agent, ExitWait))
            {
                BoundedProcess.KillTree(agent);
                await ExitedWithin(agent, ExitWait);
            }
            try { await Task.WhenAll(new[] { pump, stderr }.OfType<Task>()).WaitAsync(ExitWait); }
            catch (Exception) { /* The reader outcome is diagnostic; the generation is already detached. */ }
            agent.Dispose();
        }
        lock (stateGate)
        {
            Debug.Assert(power is NoPower or UncertainPower, "A closed Pika session retained live power evidence.");
            followAudit = null; driverFaultEvidence.Clear(); state = null; agentState = null; stateAt = 0; error = finalError ?? "Disconnected";
            slot = new IdleSlot();
        }
    }

    private static async Task<bool> ExitedWithin(Process agent, TimeSpan timeout)
    {
        try { await agent.WaitForExitAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return false; }
        catch (InvalidOperationException) { return true; } // Never started: nothing to wait for.
    }

    public async ValueTask DisposeAsync()
    {
        // The semaphore is not disposed: a fault retirement may still be queued on it.
        await operations.WaitAsync();
        try { await CloseAsync(); }
        finally { operations.Release(); }
    }

    // ── Test seams (InternalsVisibleTo Piper.Pika.Tests). They never open a transport. ──

    internal void AttachForTest(Process agent, string agentOwner, PikaAgentPhase agentPhase)
    {
        var attached = new AgentProcess(agent, Task.FromResult(""));
        AgentSlot next = agentPhase switch
        {
            PikaAgentPhase.Launching => new LaunchingSlot(agentOwner, attached),
            PikaAgentPhase.Ready => new ReadySlot(agentOwner, attached),
            PikaAgentPhase.Exited => new ExitedSlot(agentOwner, attached),
            PikaAgentPhase.Faulted => new FaultedSlot(agentOwner, attached),
            _ => throw new ArgumentOutOfRangeException(nameof(agentPhase), "Only a process-carrying phase can attach an agent.")
        };
        lock (stateGate) slot = next;
    }

    internal void SetEvidenceForTest(PikaPowerEvidence evidence, string powerOwner, string requestId, string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        PowerState next = evidence switch
        {
            PikaPowerEvidence.None => new NoPower(),
            PikaPowerEvidence.Requested => new RequestedPower(powerOwner, requestId),
            PikaPowerEvidence.Acked => new AckedPower(powerOwner, requestId),
            PikaPowerEvidence.VerifiedDisabled => new VerifiedDisabledPower(powerOwner, requestId),
            _ => new UncertainPower(powerOwner)
        };
        lock (stateGate)
        {
            power = next;
            state = JsonNode.Parse(stateJson)!.AsObject(); agentState = document.RootElement.Deserialize<PikaAgentState>(); stateAt = Stopwatch.GetTimestamp();
        }
    }

    internal void DetachForTest()
    {
        lock (stateGate) { slot = new IdleSlot(); power = new NoPower(); }
    }
}
