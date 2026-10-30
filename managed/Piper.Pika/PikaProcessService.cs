using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Pika;

/// <summary>Reusable C# owner of a Linux serial agent, local or through WSL.
/// The Windows process never opens a WCH COM port.</summary>
public sealed class PikaProcessService : IAsyncDisposable
{
    private string device;
    private readonly string agentPath;
    private string? distribution;
    public PikaProcessService(string device, string agentPath, string? distribution = null)
    { this.device = device; this.agentPath = agentPath; this.distribution = distribution; }
    private readonly SemaphoreSlim operations = new(1);
    private readonly object stateGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private string? owner;
    // Closing a failed process does not prove that its motor was disabled. Keep
    // this identity until fresh disabled feedback is observed on the same port.
    private string? unresolvedOwner;
    private bool sessionPowerRequested;
    private JsonObject? state;
    private bool connected;
    private string error = "Not connected";
    private long stateAt;
    private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Configured => !string.IsNullOrWhiteSpace(device);

    public JsonObject Snapshot()
    {
        lock (stateGate)
        {
            double age = stateAt == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(stateAt).TotalSeconds;
            var snapshot = state?.DeepClone().AsObject() ?? new JsonObject();
            snapshot["configured"] = Configured;
            snapshot["device"] = device;
            snapshot["distribution"] = distribution;
            snapshot["owner"] = owner;
            snapshot["power_requested"] = sessionPowerRequested;
            snapshot["power_uncertain"] = unresolvedOwner is not null;
            snapshot["configuration_blocked"] = process is not null || owner is not null || unresolvedOwner is not null;
            snapshot["transport"] = Configured ? distribution is null ? "linux-serial" : "wsl-linux-serial" : OperatingSystem.IsWindows() ? "disabled" : "browser";
            snapshot["connected"] = connected;
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
                        string linuxAgent = agentPath.StartsWith('/') ? agentPath : await ResolveWslPath(agentPath, cancellation);
                        info.ArgumentList.Add("--exec"); info.ArgumentList.Add(linuxAgent);
                    }
                    else
                    {
                        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Native Windows WCH serial is disabled; configure the WSL transport");
                        if (File.Exists(agentPath)) File.SetUnixFileMode(agentPath, File.GetUnixFileMode(agentPath) | UnixFileMode.UserExecute);
                        info.FileName = agentPath;
                    }
                    info.ArgumentList.Add("--device"); info.ArgumentList.Add(device);
                    ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    process = Process.Start(info) ?? throw new IOException("Could not start the Pika serial agent");
                    _ = PumpAsync(process, ready);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation);
                }
                catch { await CloseAsync(); throw; }
                return new JsonObject { ["ok"] = true, ["message"] = "Linux serial connected; reading only" };
            }
            if (owner != requestedOwner || process is null) throw new InvalidOperationException("This dashboard does not own the Pika session");
            if (action is not ("heartbeat" or "enable" or "disable" or "position" or "disconnect")) throw new ArgumentException("Unsupported Pika action");
            if (action == "disconnect" && process.HasExited)
            {
                if (unresolvedOwner is not null || sessionPowerRequested && !KnownDisabled())
                    throw new InvalidOperationException("The Pika agent exited without verified disabled feedback. Reconnect the same device, disable it and verify feedback before changing its assignment.");
                await CloseAsync(); return new JsonObject { ["ok"] = true, ["message"] = "Disconnected" };
            }
            string id = Guid.NewGuid().ToString("N");
            var command = new JsonObject { ["id"] = id, ["action"] = action };
            if (action == "enable") command["clear"] = input.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True;
            if (action == "position") command["value"] = input.GetProperty("value").GetDouble();
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            bool disconnected = false;
            try
            {
                if (action is "enable" or "position" or "disable")
                    lock (stateGate) { sessionPowerRequested = true; unresolvedOwner = requestedOwner; }
                await process.StandardInput.WriteLineAsync(command.ToJsonString().AsMemory(), cancellation);
                await process.StandardInput.FlushAsync(cancellation);
                var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellation);
                bool ok = response.GetProperty("ok").GetBoolean();
                if (!ok) throw new InvalidOperationException(response.GetProperty("message").GetString());
                disconnected = action == "disconnect";
                return new JsonObject { ["ok"] = true, ["message"] = response.GetProperty("message").GetString() };
            }
            finally { pending.TryRemove(id, out _); if (disconnected) await CloseAsync(); }
        }
        finally { operations.Release(); }
    }

    private bool KnownDisabled()
    {
        lock (stateGate) return stateAt != 0 && Stopwatch.GetElapsedTime(stateAt).TotalSeconds < 2 &&
            state?["fresh"]?.GetValue<bool>() == true && state["enabled"]?.GetValue<bool>() == false;
    }

    public static void ValidateConfiguration(string device, string? distribution)
    {
        if (device.Length > 1024 || device.Any(char.IsControl) || (device.Length > 0 && !device.StartsWith("/dev/", StringComparison.Ordinal)))
            throw new ArgumentException("Choose a Linux /dev/serial/by-id device path, or leave it blank.");
        if (distribution is not null && (distribution.Length is 0 or > 128 || distribution.Any(char.IsControl)))
            throw new ArgumentException("Invalid WSL distribution name.");
        if (OperatingSystem.IsWindows() && distribution is null) throw new InvalidOperationException("Native Windows WCH serial is disabled. Choose a WSL distribution.");
    }

    private async Task<string> ResolveWslPath(string path, CancellationToken cancellation)
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
            await MakeWslExecutable(linux, cancellation);
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

    private async Task MakeWslExecutable(string linuxPath, CancellationToken cancellation)
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
                            state = JsonNode.Parse(line)!.AsObject(); stateAt = Stopwatch.GetTimestamp();
                            if (state["fresh"]?.GetValue<bool>() == true)
                            {
                                if (state["enabled"]?.GetValue<bool>() == false) unresolvedOwner = null;
                                else if (sessionPowerRequested) unresolvedOwner = owner;
                                // Observing externally enabled hardware is not an
                                // enable request and creates no power ownership.
                            }
                        }
                        break;
                    case "ack":
                        if (pending.TryRemove(data.GetProperty("id").GetString() ?? "", out var completion)) completion.TrySetResult(data.Clone());
                        break;
                    case "error":
                        lock (stateGate) error = data.GetProperty("message").GetString() ?? "Pika agent failed";
                        break;
                }
            }
            string details = await stderr;
            lock (stateGate)
            {
                if (!ReferenceEquals(process, agent)) return;
                connected = false;
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
        lock (stateGate) { sessionPowerRequested = false; connected = false; state = null; stateAt = 0; error = "Disconnected"; }
    }
    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync();
        try { await CloseAsync(); }
        finally { operations.Release(); operations.Dispose(); }
    }
}
