using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class BackendRestartRequest
{
    public static ApiRequest Create(JsonObject? state, string owner, bool following, bool grippersEmptyConfirmed)
    {
        if (!grippersEmptyConfirmed) throw new InvalidOperationException("Empty the grippers and confirm before restarting; Pika holding torque will be released.");
        if (following || BackendActivity.AnyMotion(state) || BackendActivity.AnyQuery(state))
            throw new InvalidOperationException("Stop following and let the arm finish holding before restarting the backend.");
        if (state?["backend_restart"]?["supported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(state?["backend_restart"]?["message"]?.GetValue<string>() ??
                "This backend needs the restart-capable launcher update. No process was stopped.");
        int pid = state["backend_process_id"]?.GetValue<int>() ?? 0;
        string instance = state["backend_instance_id"]?.GetValue<string>() ?? "";
        if (pid <= 0 || !Guid.TryParse(instance, out _) || !Guid.TryParse(owner, out _))
            throw new InvalidOperationException("Wait for a fresh identified backend session before restarting.");
        if (state["backend_restart"]?["mode"]?.GetValue<string>() == "plugin" &&
            state["backend_restart"]?["plugin_owner"]?.GetValue<string>() != owner)
            throw new InvalidOperationException("This backend belongs to another plugin session. Restart it from that session; no process was stopped.");
        return PiperApi.Requests.Restart(owner, pid, instance, releaseGrippers: true);
    }
}

public sealed partial class PipersPlugin
{
    private async Task RestartBackend(CancellationToken cancellation)
    {
        var request = BackendRestartRequest.Create(state, owner, SimulationFollowingActive || GroupFollowActive, shell.Backend.Standing.Restart == BackendRestartPhase.Confirming);
        bool relaunchOwned = state?["backend_restart"]?["mode"]?.GetValue<string>() == "plugin";
        var owned = shell.Backend.Standing.Owned;
        if (relaunchOwned && (owned is null || owned.Process.HasExited ||
            !BackendGuards.IsOwned(state!, owned.Process.Id, owned.Token)))
            throw new InvalidOperationException("This panel does not own the backend process. Restart from the plugin session that started it; no shutdown was requested.");
        string previousInstance = request.Body["backend_instance_id"]!.GetValue<string>();
        shell.Backend.RestartRequested();
        try { await RequestBackendRestart(request, relaunchOwned, owned?.Process, previousInstance, cancellation); }
        finally { shell.Backend.RestartEnded(); }
    }

    private async Task RequestBackendRestart(ApiRequest request, bool relaunchOwned, Process? process, string previousInstance, CancellationToken cancellation)
    {
        shell.Backend.Report("Requesting an idle backend restart…");
        await Update(cancellation);
        await Call(request, cancellation);
        // Once accepted, finish the exact monitoring-only restart even if a UI
        // gesture is cancelled. Plugin shutdown still cancels any owned relaunch.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        DisarmTelemetryTargets("Backend restart accepted; previous targets discarded", clearLink: true, forgetApproval: ActiveProfile.Id);
        telemetry.Pika.Release();
        shell.Backend.Report("Restart accepted · reconnecting automatically. Previous movement will not resume.");
        await Update(deadline.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (relaunchOwned && shell.Backend.Standing.Restart == BackendRestartPhase.Requested && process!.HasExited)
                {
                    if (process.ExitCode != 75)
                        throw new InvalidOperationException($"Backend exited with {process.ExitCode}, not an acknowledged maintenance restart. It was not restarted automatically; inspect server logs.");
                    shell.Backend.Relaunching();
                    await StartBundledBackend(deadline.Token);
                }
                try
                {
                    var next = JsonNode.Parse(await http.GetStringAsync(ControllerEndpoint.StateUrl, deadline.Token));
                    if (next?["backend_instance_id"]?.GetValue<string>() == previousInstance &&
                        next["backend_restart"]?["pending"]?.GetValue<bool>() == false &&
                        next["backend_restart"]?["last_failure"]?.GetValue<string>() is { Length: > 0 } failure)
                        throw new InvalidOperationException(failure + " The original backend is still running; no restart occurred.");
                    if (next?["backend_instance_id"]?.GetValue<string>() is { Length: > 0 } current && current != previousInstance)
                    {
                        message = "Backend restarted · connections are recovering automatically. Use Enable robot when ready. Existing robot STOPs are preserved; use Recover robot to clear them.";
                        shell.Backend.Report(message);
                        await Render(deadline.Token);
                        return;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException ||
                    error is OperationCanceledException && !deadline.IsCancellationRequested) { }
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            string unanswered = "Restart was accepted, but the new backend did not answer within 25 seconds. Automatic reconnection is still running. No movement will resume; check the server status before trying Start bundled server.";
            shell.Backend.Report(unanswered);
            throw new TimeoutException(unanswered);
        }
    }
}
