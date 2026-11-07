using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>Reusable C# owner of a Linux serial agent, local or through WSL.
/// The Windows process never opens a WCH COM port.</summary>
public sealed class PikaProcessService : IPikaSession
{
    private string device;
    private readonly string agentPath;
    private readonly double commandPeriodSeconds;
    private string? distribution;
    public PikaProcessService(string device, string agentPath, string? distribution = null,
        double commandPeriodSeconds = PikaFollowSession.MinimumCommandPeriodSeconds)
    {
        _ = new PikaFollowSession(_ => { }, commandPeriodSeconds); // Validate without opening a transport.
        this.device = device; this.agentPath = agentPath; this.distribution = distribution; this.commandPeriodSeconds = commandPeriodSeconds;
    }
    private readonly SemaphoreSlim operations = new(1);
    private readonly object stateGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private string? owner;
    // Closing a failed process does not prove that its motor was disabled. Keep
    // this identity until fresh disabled feedback is observed on the same port.
    private string? unresolvedOwner;
    private bool sessionPowerRequested;
    private string motorRequestId = "";
    private bool motorRequestAcknowledged, processConfirmedExited;
    private JsonObject? state;
    private PikaFollowAudit? followAudit;
    private readonly Queue<PikaDriverFaultEvidence> driverFaultEvidence = new();
    private bool connected;
    private string error = "Not connected";
    private long stateAt;
    private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Configured => !string.IsNullOrWhiteSpace(device);

    public JsonObject Snapshot(bool includeRawEvidence = false)
    {
        lock (stateGate)
        {
            double age = EvidenceAgeSeconds();
            var snapshot = state?.DeepClone().AsObject() ?? new JsonObject();
            snapshot.Remove("driver_fault_evidence_raw");
            if (driverFaultEvidence.LastOrDefault() is { } fault)
                snapshot["driver_fault_evidence"] = JsonSerializer.SerializeToNode(fault.Summary);
            if (includeRawEvidence) snapshot["driver_fault_evidence_raw"] = JsonSerializer.SerializeToNode(driverFaultEvidence.ToArray());
            if (followAudit is not null) snapshot["follow_audit"] = JsonSerializer.SerializeToNode(followAudit);
            snapshot["configured"] = Configured;
            snapshot["device"] = device;
            snapshot["distribution"] = distribution;
            snapshot["owner"] = owner;
            snapshot["power_requested"] = sessionPowerRequested;
            snapshot["power_uncertain"] = unresolvedOwner is not null;
            // A retained power reservation also exists during a healthy enabled
            // session. Maintenance must distinguish that from an unresolved
            // request, using private ACK identity and post-request feedback.
            snapshot["post_request_feedback_verified"] = HasPostRequestEvidence();
            snapshot["configuration_blocked"] = process is not null || owner is not null || unresolvedOwner is not null;
            snapshot["transport"] = Configured ? distribution is null ? "linux-serial" : "wsl-linux-serial" : OperatingSystem.IsWindows() ? "disabled" : "browser";
            snapshot["connected"] = connected;
            // A client may adopt only this privately verified dead, unpowered
            // observer. ExecuteAsync rechecks this under the mutation lock.
            snapshot["read_only_adoptable"] = ReadOnlyAdoptable();
            // Transport cadence is not a measured/verified physical opening rate.
            snapshot["follow_minimum_command_period_s"] = commandPeriodSeconds;
            snapshot["follow_maximum_command_hz"] = 1 / commandPeriodSeconds;
            snapshot["verified_maximum_opening_speed_mm_s"] = null;
            snapshot["physical_replay_available"] = false;
            snapshot["measured_hold_resume_supported"] = true;
            // The agent's age is relative to its last emission. Do not leave
            // that cached value looking current after its process has exited.
            snapshot["age_s"] = double.IsFinite(age) ? age : null;
            snapshot["fresh"] = connected && age < 2 && snapshot["fresh"]?.GetValue<bool>() == true;
            snapshot["message"] = connected ? snapshot["message"]?.GetValue<string>() ?? "Waiting for telemetry" : error;
            return snapshot;
        }
    }

    public async Task<JsonObject> ExecuteAsync(string action, JsonElement input, CancellationToken cancellation)
    {
        string requestedOwner = input.TryGetProperty("owner", out var value) ? value.GetString() ?? "" : "";
        if (!Guid.TryParse(requestedOwner, out _)) throw new ArgumentException("A dashboard Pika owner ID is required");
        await operations.WaitAsync(cancellation);
        try
        {
            if (input.TryGetProperty("read_only_adopt", out var readOnlyAdopt))
            {
                if (readOnlyAdopt.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("read_only_adopt must be true or false.");
                if (readOnlyAdopt.GetBoolean())
                {
                    lock (stateGate)
                        if (action != "connect" || !input.TryGetProperty("take_over", out var takeover) || takeover.ValueKind != JsonValueKind.True || !ReadOnlyAdoptable())
                            throw new InvalidOperationException("Read-only adoption requires a confirmed exited, unpowered, idle Pika monitor. A connected, powered or unverified session was not changed.");
                }
            }
            // A dead observer owns no motor state. Release that stale serial
            // reservation, but retain every failed power/position attempt.
            if (process is { HasExited: true } && !sessionPowerRequested && unresolvedOwner is null)
                await CloseAsync();
            if (action == "configure")
            {
                // A dead process may have left enabled hardware behind. Never
                // discard its owner/state as a side effect of changing routing.
                if (process is not null || owner is not null || unresolvedOwner is not null)
                    throw new InvalidOperationException((owner ?? unresolvedOwner) != requestedOwner
                        ? "Another dashboard owns the Pika session. It must verify disabled feedback and disconnect before changing the serial device."
                        : "Verify disabled Pika feedback and disconnect this session before changing its serial device. Reconnect the same device if its agent exited.");
                string nextDevice = input.GetProperty("device").GetString() ?? "";
                string? nextDistribution = input.TryGetProperty("distribution", out var distro) ? distro.GetString() : distribution;
                ValidateConfiguration(nextDevice, nextDistribution);
                lock (stateGate) { device = nextDevice; distribution = nextDistribution; state = null; stateAt = 0; error = "Configured; connect to read telemetry."; }
                return new JsonObject { ["ok"] = true, ["message"] = "Pika serial assignment changed. No device was opened or motor command sent.", ["device"] = device, ["distribution"] = distribution };
            }
            if (!Configured) throw new InvalidOperationException("Pika Linux serial transport has not been configured");
            if (action == "connect")
            {
                bool takeOver = input.TryGetProperty("take_over", out var takeover) && takeover.ValueKind == JsonValueKind.True;
                if ((owner ?? unresolvedOwner) is { } previousOwner && previousOwner != requestedOwner && !takeOver)
                    throw new InvalidOperationException("Pika is controlled by another dashboard tab");
                if (process is not null && process.HasExited) await CloseAsync();
                if (process is not null && owner == requestedOwner) return new JsonObject { ["ok"] = true, ["message"] = "Session already connected" };
                if (process is not null)
                {
                    if (!takeOver)
                        throw new InvalidOperationException("Pika is controlled by another dashboard tab");
                    // EOF disables the old session before another agent opens the port.
                    // Ownership changes never replay a position target.
                    await CloseAsync();
                }
                owner = requestedOwner;
                try
                {
                    // Setup failures, including WSL path resolution before process
                    // launch, must release a read-only reservation as well.
                    var info = new ProcessStartInfo { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    if (distribution is not null)
                    {
                        info.FileName = "wsl.exe";
                        info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(distribution);
                        string linuxAgent = agentPath.StartsWith('/') ? agentPath : await ResolveWslPath(agentPath, distribution, cancellation);
                        info.ArgumentList.Add("--exec"); info.ArgumentList.Add(linuxAgent);
                    }
                    else
                    {
                        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Native Windows WCH serial is disabled; configure the WSL transport");
                        if (File.Exists(agentPath)) File.SetUnixFileMode(agentPath, File.GetUnixFileMode(agentPath) | UnixFileMode.UserExecute);
                        info.FileName = agentPath;
                    }
                    info.ArgumentList.Add("--device"); info.ArgumentList.Add(device);
                    info.ArgumentList.Add("--command-period-ms"); info.ArgumentList.Add((commandPeriodSeconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (stateGate) processConfirmedExited = false;
                    process = Process.Start(info) ?? throw new IOException("Could not start the Pika serial agent");
                    _ = PumpAsync(process, ready);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
                }
                catch { await CloseAsync(); throw; }
                return new JsonObject { ["ok"] = true, ["message"] = "Linux serial connected; reading only" };
            }
            if (owner != requestedOwner || process is null) throw new InvalidOperationException("This dashboard does not own the Pika session");
            if (action is not ("heartbeat" or "enable" or "disable" or "position" or "disconnect" or "hold" or "follow-start" or "follow-update" or "resume-hold")) throw new ArgumentException("Unsupported Pika action");
            if (action == "disconnect" && process.HasExited)
            {
                if (unresolvedOwner is not null || sessionPowerRequested && !KnownDisabled())
                    throw new InvalidOperationException("The Pika agent exited without verified disabled feedback. Reconnect the same device, disable it and verify feedback before changing its assignment.");
                await CloseAsync(); return new JsonObject { ["ok"] = true, ["message"] = "Disconnected" };
            }
            // Coordinated STOP uses this same hold action. Do not turn an
            // already verified free-jaw state into a failed servo command or a
            // new unresolved power reservation. Both ownership and the private
            // acknowledged-request evidence remain serialized with mutations.
            if (action == "hold" && KnownDisabled() && PikaDisabledEvidence.Confirmed(Snapshot(), requestedOwner))
                return new JsonObject { ["ok"] = true, ["state"] = "disabled",
                    ["message"] = "Pika already verified disabled; no motor command sent." };
            string id = Guid.NewGuid().ToString("N");
            var command = new JsonObject { ["id"] = id, ["action"] = action };
            if (action == "enable") command["clear"] = input.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True;
            if (action == "resume-hold")
            {
                if (!KnownDisabled() || !input.TryGetProperty("clear", out var supported) || supported.ValueKind != JsonValueKind.True ||
                    !input.TryGetProperty("expected_angle_rad", out var expected) || !expected.TryGetDouble(out double angle) || !double.IsFinite(angle) ||
                    !input.TryGetProperty("expected_feedback_time_s", out var expectedTime) || !expectedTime.TryGetDouble(out double feedbackTime) || !double.IsFinite(feedbackTime))
                    throw new InvalidOperationException("Confirm a stationary measured hold with verified disabled Pika feedback before resuming.");
                command["clear"] = true; command["expected_angle_rad"] = angle; command["expected_feedback_time_s"] = feedbackTime;
            }
            if (action == "position") command["value"] = input.GetProperty("value").GetDouble();
            if (action == "follow-update")
            {
                if (!input.TryGetProperty("follow_session", out var session) || session.ValueKind != JsonValueKind.String ||
                    !input.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out long nextSequence) || nextSequence <= 0 ||
                    !input.TryGetProperty("opening_mm", out var opening) || opening.ValueKind != JsonValueKind.Number || !opening.TryGetDouble(out double nextOpening) || !double.IsFinite(nextOpening) || nextOpening is < 0 or > 90)
                    throw new ArgumentException("Pika follow-update needs its session, a positive integer sequence and an opening within 0–90 mm.");
                command["follow_session"] = session.GetString();
                command["sequence"] = nextSequence;
                command["opening_mm"] = nextOpening;
            }
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            bool disconnected = false;
            try
            {
                if (action is "enable" or "position" or "disable" or "hold" or "follow-update" or "resume-hold")
                    lock (stateGate) { sessionPowerRequested = true; unresolvedOwner = requestedOwner; motorRequestId = id; motorRequestAcknowledged = false; }
                await process.StandardInput.WriteLineAsync(command.ToJsonString().AsMemory(), cancellation);
                await process.StandardInput.FlushAsync(cancellation);
                var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
                bool ok = response.GetProperty("ok").GetBoolean();
                if (!ok)
                {
                    PikaFollowAudit? rejected;
                    lock (stateGate) { RetainFollowAudit(response); rejected = followAudit; }
                    string reason = response.GetProperty("message").GetString() ?? "Pika request rejected";
                    if (rejected is not null) throw new PikaFollowRejectedException(reason, rejected);
                    throw new InvalidOperationException(reason);
                }
                cancellation.ThrowIfCancellationRequested();
                if (action is "enable" or "position" or "disable" or "hold" or "follow-update" or "resume-hold")
                    lock (stateGate)
                    {
                        motorRequestAcknowledged = motorRequestId == id;
                        if (KnownDisabled()) unresolvedOwner = null;
                        RetireVerifiedEndedPower();
                    }
                disconnected = action == "disconnect";
                var result = new JsonObject { ["ok"] = true, ["message"] = response.GetProperty("message").GetString() };
                if (action is "enable" or "position" or "disable" or "hold" or "follow-update" or "resume-hold") result["motor_request_id"] = id;
                if (response.TryGetProperty("follow_session", out var followSession)) result["follow_session"] = followSession.GetString();
                return result;
            }
            finally { pending.TryRemove(id, out _); if (disconnected) await CloseAsync(); }
        }
        finally { operations.Release(); }
    }

    // Caller holds stateGate. Cached public booleans cannot grant takeover:
    // private process-exit and power-request evidence are authoritative.
    private bool ReadOnlyAdoptable() => !connected && process is not null && processConfirmedExited &&
        owner is not null && !sessionPowerRequested && unresolvedOwner is null && pending.IsEmpty &&
        FalseState("enabled") && FalseState("armed") && FalseState("pending") && FalseState("following") &&
        state?["resuming_hold"]?.GetValue<bool>() != true;

    private bool FalseState(string key) => state?[key] is JsonValue value && value.TryGetValue<bool>(out bool active) && !active;

    private bool KnownDisabled()
    {
        lock (stateGate) return EvidenceAgeSeconds() < 2 &&
            state?["fresh"]?.GetValue<bool>() == true && state["enabled"]?.GetValue<bool>() == false &&
            (!sessionPowerRequested || HasPostRequestEvidence());
    }

    // Receipt order is insufficient: an old disabled packet may have been
    // buffered before enable. Match the acknowledged mutation and compare
    // hardware-feedback/request times from the same agent monotonic clock.
    private bool HasPostRequestEvidence() => motorRequestAcknowledged && motorRequestId.Length > 0 &&
        state?["last_motor_request"]?.GetValue<string>() == motorRequestId &&
        state["request_time_s"] is JsonValue request && request.TryGetValue<double>(out double requestTime) && double.IsFinite(requestTime) && requestTime >= 0 &&
        state["feedback_time_s"] is JsonValue feedback && feedback.TryGetValue<double>(out double feedbackTime) && double.IsFinite(feedbackTime) && feedbackTime > requestTime;

    private void RetireVerifiedEndedPower()
    {
        if (processConfirmedExited && KnownDisabled() && state?["armed"]?.GetValue<bool>() == false && state["pending"]?.GetValue<bool>() == false)
        { sessionPowerRequested = false; unresolvedOwner = null; }
    }

    // Call only under stateGate. Missing/invalid source age is not fresh
    // evidence, even if a buffered state line was received very recently.
    private double EvidenceAgeSeconds() => stateAt != 0 && state?["age_s"] is JsonValue source &&
        source.TryGetValue<double>(out double seconds) && double.IsFinite(seconds) && seconds >= 0
        ? seconds + Stopwatch.GetElapsedTime(stateAt).TotalSeconds : double.PositiveInfinity;

    public static void ValidateConfiguration(string device, string? distribution)
    {
        if (device.Length > 1024 || device.Any(char.IsControl) || (device.Length > 0 && !device.StartsWith("/dev/", StringComparison.Ordinal)))
            throw new ArgumentException("Choose this gripper's Linux USB device identity from Settings, or leave it unassigned.");
        if (distribution is not null && (distribution.Length is 0 or > 128 || distribution.Any(char.IsControl)))
            throw new ArgumentException("Invalid WSL distribution name.");
        if (OperatingSystem.IsWindows() && distribution is null) throw new InvalidOperationException("Native Windows WCH serial is disabled. Choose a WSL distribution.");
    }

    internal static async Task<string> ResolveWslPath(string path, string distribution, CancellationToken cancellation)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The bundled Pika agent is missing. Reinstall the Pipers package.", path);
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--distribution", distribution!, "--exec", "wslpath", "-a", Path.GetFullPath(path) }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var conversion = Process.Start(start) ?? throw new IOException("Could not start WSL path conversion.");
        conversion.StandardInput.Close();
        using var stop = deadline.Token.Register(() => { try { if (!conversion.HasExited) conversion.Kill(true); } catch (InvalidOperationException) { } });
        var output = conversion.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = conversion.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await conversion.WaitForExitAsync(deadline.Token);
            string linux = (await output).Trim();
            string error = await errors;
            if (conversion.ExitCode != 0 || !linux.StartsWith('/') || linux.Contains('\n'))
                throw new IOException("WSL could not locate the bundled Pika agent. " + error.Trim());
            await MakeWslExecutable(linux, distribution, cancellation);
            return linux;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("WSL did not respond while locating the bundled Pika agent."); }
        finally
        {
            if (!conversion.HasExited) conversion.Kill(true);
            await conversion.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
    }

    private static async Task MakeWslExecutable(string linuxPath, string distribution, CancellationToken cancellation)
    {
        // ZIP extraction on Windows does not retain Unix execute bits. Change
        // only this user's packaged apphost, without root or global installation.
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--distribution", distribution!, "--exec", "chmod", "u+x", linuxPath }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var chmod = Process.Start(start) ?? throw new IOException("Could not prepare the bundled Pika agent.");
        chmod.StandardInput.Close();
        using var stop = deadline.Token.Register(() => { try { if (!chmod.HasExited) chmod.Kill(true); } catch (InvalidOperationException) { } });
        var output = chmod.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = chmod.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await chmod.WaitForExitAsync(deadline.Token);
            await output;
            string error = await errors;
            if (chmod.ExitCode != 0) throw new IOException("WSL cannot execute the packaged Pika agent. Move the plugin to a user-writable WSL-accessible location. " + error.Trim());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("WSL timed out while preparing the packaged Pika agent."); }
        finally
        {
            if (!chmod.HasExited) chmod.Kill(true);
            await chmod.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
        }
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

    private async Task PumpAsync(Process agent, TaskCompletionSource sessionReady)
    {
        var stderr = agent.StandardError.ReadToEndAsync();
        try
        {
            while (await agent.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!ReferenceEquals(process, agent)) return;
                if (line.Length > 32768) continue;
                using var document = JsonDocument.Parse(line);
                var data = document.RootElement;
                switch (data.GetProperty("type").GetString())
                {
                    case "ready":
                        lock (stateGate) { connected = true; error = ""; }
                        sessionReady.TrySetResult(); break;
                    case "state":
                        lock (stateGate)
                        {
                            RetainFollowAudit(data);
                            state = JsonNode.Parse(line)!.AsObject(); stateAt = Stopwatch.GetTimestamp();
                            if (state["fresh"]?.GetValue<bool>() == true)
                            {
                                if (KnownDisabled()) unresolvedOwner = null;
                                else if (sessionPowerRequested) unresolvedOwner = owner;
                                // Observing externally enabled hardware is not an
                                // enable request and creates no power ownership.
                            }
                        }
                        break;
                    case "ack":
                        lock (stateGate) RetainFollowAudit(data);
                        if (pending.TryRemove(data.GetProperty("id").GetString() ?? "", out var completion)) completion.TrySetResult(data.Clone());
                        break;
                    case "driver_fault_evidence":
                        lock (stateGate) RetainDriverFaultEvidence(data);
                        break;
                    case "error":
                        lock (stateGate) { RetainFollowAudit(data); error = data.GetProperty("message").GetString() ?? "Pika agent failed"; }
                        break;
                }
            }
            string details = await stderr;
            await agent.WaitForExitAsync();
            lock (stateGate)
            {
                if (!ReferenceEquals(process, agent)) return;
                connected = false;
                processConfirmedExited = true;
                // The old client's heartbeat can expire during an editor
                // reload. Its agent disables first and then exits. A verified
                // disabled, idle final sample releases only that obsolete
                // power-request bookkeeping, so a new read-only monitor can
                // reopen the unchanged serial assignment. Never infer a
                // disable from EOF alone or from enabled/stale/pending data.
                RetireVerifiedEndedPower();
                if (!string.IsNullOrWhiteSpace(details)) error = details.Length > 2048 ? details[..2048] : details;
                if (string.IsNullOrEmpty(error)) error = "Pika agent disconnected; reconnect explicitly";
            }
            sessionReady.TrySetException(new IOException(error));
            foreach (var item in pending) item.Value.TrySetException(new IOException(error));
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(process, agent)) return;
            lock (stateGate) { connected = false; error = e.Message; }
            sessionReady.TrySetException(e);
            foreach (var item in pending) item.Value.TrySetException(e);
        }
    }

    private async Task CloseAsync()
    {
        lock (stateGate)
        {
            if (KnownDisabled()) unresolvedOwner = null;
            else if (sessionPowerRequested) unresolvedOwner ??= owner;
        }
        if (process is { } agent)
        {
            process = null;
            // EOF makes the agent disable a gripper it enabled, then close.
            try { agent.StandardInput.Close(); await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { if (!agent.HasExited) agent.Kill(); }
            agent.Dispose();
        }
        owner = null;
        lock (stateGate) { sessionPowerRequested = false; motorRequestId = ""; motorRequestAcknowledged = processConfirmedExited = false; followAudit = null; driverFaultEvidence.Clear();
            connected = false; state = null; stateAt = 0; error = "Disconnected"; }
    }
    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync();
        try { await CloseAsync(); }
        finally { operations.Release(); operations.Dispose(); }
    }
}
