using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class BackendRestartRequest
{
    public static JsonObject Create(JsonObject? state, string owner, bool following, bool grippersEmptyConfirmed)
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
        return new() { ["owner"] = owner, ["backend_process_id"] = pid,
            ["backend_instance_id"] = instance, ["release_grippers"] = true };
    }
}

public sealed partial class PipersPlugin
{
    private bool backendRestartConfirmation;

    private async Task RestartBackend(CancellationToken cancellation)
    {
        var request = BackendRestartRequest.Create(state, owner, SimulationFollowingActive || GroupFollowActive, backendRestartConfirmation);
        bool relaunchOwned = state?["backend_restart"]?["mode"]?.GetValue<string>() == "plugin";
        var process = ownedBackend;
        if (relaunchOwned && (process is null || process.HasExited ||
            !BackendGuards.IsOwned(state!, process.Id, ownedBackendToken)))
            throw new InvalidOperationException("This panel does not own the backend process. Restart from the plugin session that started it; no shutdown was requested.");
        string previousInstance = request["backend_instance_id"]!.GetValue<string>();
        backendRestartConfirmation = false;
        backendStatus = "Requesting an idle backend restart…";
        await Update(cancellation);
        await Call("/api/maintenance/restart", request, cancellation);
        // Once accepted, finish the exact monitoring-only restart even if a UI
        // gesture is cancelled. Plugin shutdown still cancels any owned relaunch.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        DisarmTelemetryTargets("Backend restart accepted; previous targets discarded", clearLink: true, forgetApproval: ActiveProfile.Id);
        ReleasePikaRole();
        backendStatus = "Restart accepted · reconnecting automatically. Previous movement will not resume.";
        await Update(deadline.Token);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            bool relaunched = false;
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (relaunchOwned && !relaunched && process!.HasExited)
                {
                    if (process.ExitCode != 75)
                        throw new InvalidOperationException($"Backend exited with {process.ExitCode}, not an acknowledged maintenance restart. It was not restarted automatically; inspect server logs.");
                    await StartBundledBackend(deadline.Token);
                    relaunched = true;
                }
                try
                {
                    var next = JsonNode.Parse(await http.GetStringAsync("http://127.0.0.1:8765/api/state", deadline.Token));
                    if (next?["backend_instance_id"]?.GetValue<string>() == previousInstance &&
                        next["backend_restart"]?["pending"]?.GetValue<bool>() == false &&
                        next["backend_restart"]?["last_failure"]?.GetValue<string>() is { Length: > 0 } failure)
                        throw new InvalidOperationException(failure + " The original backend is still running; no restart occurred.");
                    if (next?["backend_instance_id"]?.GetValue<string>() is { Length: > 0 } current && current != previousInstance)
                    {
                        backendStatus = "Backend restarted · connections are recovering automatically. Use Enable robot when ready. Existing robot STOPs are preserved; use Recover robot to clear them.";
                        message = backendStatus;
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
            backendStatus = "Restart was accepted, but the new backend did not answer within 25 seconds. Automatic reconnection is still running. No movement will resume; check the server status before trying Start bundled server.";
            throw new TimeoutException(backendStatus);
        }
    }
}
