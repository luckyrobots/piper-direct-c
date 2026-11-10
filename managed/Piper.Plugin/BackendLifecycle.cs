using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The local controller's fixed loopback endpoints (Piper.Host defaults).</summary>
internal static class ControllerEndpoint
{
    public const string Loopback = "127.0.0.1";
    public const int HttpPort = 8765, GrpcPort = 8766;
    public const string GrpcAddress = "http://" + Loopback + ":8766";
    private const string Http = "http://" + Loopback + ":8765";
    public const string StateUrl = Http + "/api/state", StopUrl = Http + "/api/stop", ShutdownUrl = Http + "/api/shutdown";
}

public static class BackendGuards
{
    private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(500);

    public static bool IsOwned(JsonNode snapshot, int processId, string? sessionToken) =>
        snapshot["backend_process_id"]?.GetValue<int>() == processId &&
        !string.IsNullOrEmpty(snapshot["token"]?.GetValue<string>()) &&
        (sessionToken is null || snapshot["token"]?.GetValue<string>() == sessionToken);

    public static async Task<bool> PortOccupied(int port, CancellationToken cancellation)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(PortProbeTimeout);
        try { await client.ConnectAsync("127.0.0.1", port, deadline.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }
}

public static class BackendActivity
{
    public static bool AnyMotion(JsonNode? state) => state?["any_motion_active"]?.GetValue<bool>() == true ||
        state?["motion"]?["active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["active"]?.GetValue<bool>() == true) ||
        state?["devices"] is JsonArray devices && devices.Any(device => device?["motion"]?["active"]?.GetValue<bool>() == true);
    public static bool AnyQuery(JsonNode? state) => state?["any_query_active"]?.GetValue<bool>() == true ||
        state?["query_active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["query_active"]?.GetValue<bool>() == true);
}

/// <summary>Plugin-4 restart of the controller backend: Idle → Confirming (the
/// operator opened the gripper-release confirmation) → Requested (the request
/// was sent) → Relaunching (this session's owned process exited for maintenance
/// and is being started again) → Idle. Any failure returns to Idle.</summary>
internal enum BackendRestartPhase { Idle, Confirming, Requested, Relaunching }

public sealed record BackendSettings(string WslDistribution = PikaDistribution.WindowsDefault, string PikaDevice = "/dev/serial/by-id/usb-" + PikaProtocol.UsbVendorId + "_USB_Serial-if00-port0");

public sealed partial class PipersPlugin
{
    /// <summary>Plugin-4 ownership: External (null) → Starting (Token null) → Ready (Token
    /// set) → External once stopped or found exited. Read across threads; replaced whole.</summary>
    private sealed record OwnedBackendProcess(Process Process, string? Token);
    private OwnedBackendProcess? ownedBackend;
    private OwnedBackendProcess? OwnedBackend => Volatile.Read(ref ownedBackend);
    private string backendStatus = $"Use the existing controller, or start the bundled server when ports {ControllerEndpoint.HttpPort}/{ControllerEndpoint.GrpcPort} are free.";
    private string setupStatus = "Windows Pika requires WSL 2 + a Linux distribution and usbipd-win. Share only the Pika USB device with WSL. Driver/USB setup is explicit; see plugin instructions.";
    private BackendSettings BackendOptions => preferences.Backend ?? new();

    private Widget BackendSetup() => Widget.Column("backend.setup",
        Widget.Row("backend.actions", Widget.Button("backend.restart", "Restart backend") with { Hint = "Reopens connections. Releases Pika holding torque; does not clear a robot STOP or replay movement." },
            Widget.Button("backend.start", "Start bundled server"), Widget.Button("backend.check", "Check Pika setup")),
        Widget.Column("backend.restart.confirmation", Widget.Label("backend.restart.explanation", "Restarting releases all Pika grippers. Empty the jaws first. Arm power stays unchanged; robot STOPs still need recovery.") with { Height = 64 },
            Widget.Row("backend.restart.confirmation.actions", Widget.Button("backend.restart.confirm", "Grippers empty — restart") with { Appearance = "primary" },
                Widget.Button("backend.restart.cancel", "Cancel"))) with { Visible = backendRestart == BackendRestartPhase.Confirming, Padding = 12, Appearance = "panel" },
        Widget.Label("backend.status", backendStatus) with { Height = 42 },
        Widget.Label("backend.prerequisites", setupStatus) with { Height = 66 },
        Widget.Column("backend.options", Widget.Label("backend.wsl.label", "WSL distro") with { Visible = OperatingSystem.IsWindows() },
            new Widget("input", "backend.wsl") { Text = BackendOptions.WslDistribution, Hint = "Existing WSL 2 distribution; used only by a bundled server started here", Visible = OperatingSystem.IsWindows() },
            Widget.Label("backend.device.label", "Pika Linux device"),
            new Widget("input", "backend.device") { Text = BackendOptions.PikaDevice, Hint = "Linux serial path. Leave blank for an arm-only server." }),
        Widget.Button("backend.instructions", "Open setup instructions"));

    private async Task BackendEvent(string action, string value, CancellationToken cancellation)
    {
        if (action == "restart")
        {
            if (backendRestart is not (BackendRestartPhase.Idle or BackendRestartPhase.Confirming))
                throw new InvalidOperationException("A backend restart is already in progress.");
            backendRestart = BackendRestartPhase.Confirming; await Render(cancellation);
        }
        else if (action == "restart.cancel")
        {
            if (backendRestart == BackendRestartPhase.Confirming) backendRestart = BackendRestartPhase.Idle;
            await Render(cancellation);
        }
        else if (action == "restart.confirm") await RestartBackend(cancellation);
        else if (action == "start") await StartBundledBackend(cancellation);
        else if (action == "check") await CheckPikaSetup(cancellation);
        else if (action is "wsl" or "device")
        {
            if (value.Length > 1024 || value.Contains('\0') || value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("Invalid backend setting.");
            preferences = preferences with { Backend = action == "wsl" ? BackendOptions with { WslDistribution = value.Trim() } : BackendOptions with { PikaDevice = value.Trim() } };
            await Save(cancellation);
            backendStatus = "Saved for the next bundled server start. A running external controller is unchanged.";
        }
        else if (action == "instructions")
            Process.Start(new ProcessStartInfo(Path.Combine(engine.Start.PackageDirectory, "docs", "lucky-engine-plugin.md")) { UseShellExecute = true });
    }

    private async Task StartBundledBackend(CancellationToken cancellation)
    {
        if (OwnedBackend is { Process.HasExited: false }) { backendStatus = "The bundled server is already running; waiting for feedback."; return; }
        string global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuckyRobots", "Piper");
        Directory.CreateDirectory(global);
        FileStream startupLock;
        try { startupLock = new FileStream(Path.Combine(global, "server-start.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { backendStatus = "Another Pipers instance is starting the controller. Waiting for its endpoint."; return; }
        using (startupLock)
        {
            if (await BackendGuards.PortOccupied(ControllerEndpoint.HttpPort, cancellation) || await BackendGuards.PortOccupied(ControllerEndpoint.GrpcPort, cancellation))
            {
                backendStatus = $"A local service is already listening on {ControllerEndpoint.HttpPort}/{ControllerEndpoint.GrpcPort}. Waiting for a compatible controller endpoint; no second server was started.";
                return;
            }
            string directory = Path.Combine(engine.Start.PackageDirectory, "server");
            string executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "Piper.Host.exe" : "Piper.Host");
            if (!File.Exists(executable)) throw new FileNotFoundException("The bundled controller is missing. Reinstall Pipers.", executable);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "--port", ControllerEndpoint.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--grpc-port", ControllerEndpoint.GrpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--web-root", Path.Combine(engine.Start.PackageDirectory, "web"),
                "--state-dir", Path.Combine(global, "controller"), "--session-owner", owner, "--pika-device", BackendOptions.PikaDevice }) start.ArgumentList.Add(argument);
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("--pika-wsl"); start.ArgumentList.Add(BackendOptions.WslDistribution); }
            Interlocked.Exchange(ref ownedBackend, null)?.Process.Dispose();
            var process = Process.Start(start) ?? throw new IOException("Could not start the bundled controller.");
            Volatile.Write(ref ownedBackend, new(process, null));
            process.StandardInput.Close();
            _ = DrainBackendOutput(process.StandardOutput, Path.Combine(global, "server-output.log"));
            _ = DrainBackendOutput(process.StandardError, Path.Combine(global, "server-errors.log"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (int attempt = 0; attempt < 40; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) { backendStatus = $"Bundled server exited ({process.ExitCode}). See {global}. Check USB/controller prerequisites."; return; }
                try
                {
                    var snapshot = JsonNode.Parse(await http.GetStringAsync(ControllerEndpoint.StateUrl, cancellation));
                    if (snapshot is not null && BackendGuards.IsOwned(snapshot, process.Id, null))
                    {
                        Volatile.Write(ref ownedBackend, new(process, snapshot["token"]!.GetValue<string>()));
                        backendStatus = "Bundled server started in monitoring mode. It belongs to this plugin session; closing Pipers stops this server. Startup sends no motor-power or movement commands.";
                        return;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
                await Task.Delay(200, cancellation);
            }
            backendStatus = "Bundled server is starting slowly; state streaming will reconnect automatically. Check Pika setup and server logs if it remains unavailable.";
        }
    }

    private static async Task DrainBackendOutput(StreamReader reader, string path)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                // Keep a bounded diagnostic file rather than retaining process output in memory.
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                await File.AppendAllTextAsync(path, DateTimeOffset.UtcNow.ToString("O") + " " + line + Environment.NewLine);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Trace.TraceWarning("Backend log: " + error.Message); }
    }

    private async Task CheckPikaSetup(CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(BackendOptions.PikaDevice)) { setupStatus = "Pika is disabled for bundled-server starts. Arm and cameras remain available."; return; }
        if (OperatingSystem.IsLinux())
        {
            setupStatus = File.Exists(BackendOptions.PikaDevice) ? "Pika serial device exists. Ensure this user has read/write permissions; the bundled agent opens it only when requested."
                : "Pika serial device is absent. Connect the device and select its stable /dev/serial/by-id path; configure udev/group permissions as described in setup instructions.";
            return;
        }
        if (!OperatingSystem.IsWindows()) { setupStatus = "The Pika transport is supported on Windows + WSL and Linux."; return; }
        string usbipd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        if (!File.Exists(usbipd)) { setupStatus = "Install usbipd-win and WSL 2 with a Linux distribution, then follow the Pika-only USB sharing steps in setup instructions. No driver settings were changed."; return; }
        var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--distribution", BackendOptions.WslDistribution, "--exec", "test", "-w", BackendOptions.PikaDevice }) start.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var check = Process.Start(start) ?? throw new IOException("Could not start WSL. Install WSL 2 and the selected Linux distribution.");
        check.StandardInput.Close();
        var output = check.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = check.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await check.WaitForExitAsync(deadline.Token);
            await output; await error;
            setupStatus = check.ExitCode == 0 ? "WSL can access the Pika serial device. The Linux agent is bundled; no /opt installation is required. No motor commands were sent."
                : "WSL cannot write the selected Pika serial device. Check distro name, Pika-only usbipd attachment and Linux serial permissions. See setup instructions; no driver settings were changed.";
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { setupStatus = "WSL setup check timed out. Start the selected Linux distribution and check its Pika USB attachment before retrying."; }
        finally
        {
            if (!check.HasExited) check.Kill(true);
            await check.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }

    private async Task StopOwnedBackend()
    {
        if (OwnedBackend is not { } owned) return;
        var process = owned.Process;
        try
        {
            if (process.HasExited) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var snapshot = JsonNode.Parse(await http.GetStringAsync(ControllerEndpoint.StateUrl, deadline.Token))!;
            if (!BackendGuards.IsOwned(snapshot, process.Id, owned.Token)) return;
            string currentToken = snapshot["token"]!.GetValue<string>();
            if (BackendActivity.AnyMotion(snapshot))
            {
                // An explicitly session-owned server cannot outlive the engine's
                // process group. Stop its exact active request before closing USB.
                var motions = snapshot["motions"] is JsonArray lanes ? lanes : new JsonArray(snapshot["motion"]?.DeepClone());
                if (motions.Any(lane => lane?["active"]?.GetValue<bool>() == true && lane["owner"]?.GetValue<string>() != owner))
                { Trace.TraceWarning("Bundled server has another client's active lease; no stop or shutdown was sent."); return; }
                var stop = new JsonObject { ["owner"] = owner };
                using var stopRequest = new HttpRequestMessage(HttpMethod.Post, ControllerEndpoint.StopUrl) { Content = new StringContent(stop.ToJsonString(), Encoding.UTF8, "application/json") };
                stopRequest.Headers.Add("X-Piper-Token", currentToken);
                using var stopped = await http.SendAsync(stopRequest, deadline.Token);
                stopped.EnsureSuccessStatusCode();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, ControllerEndpoint.ShutdownUrl) { Content = new StringContent(new JsonObject { ["owner"] = owner }.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Piper-Token", currentToken);
            using var response = await http.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        { Trace.TraceWarning("Bundled server shutdown: " + error.Message); }
        finally { process.Dispose(); Interlocked.CompareExchange(ref ownedBackend, null, owned); }
    }
}
