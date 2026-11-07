using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Host;

internal static class BackendRestartIntegrationTests
{
    internal static async Task Run()
    {
        await RunTransport(false);
        await RunTransport(true);
        await RunTransport(true, true);
    }

    private static async Task RunTransport(bool grpc, bool pluginOwned = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "piper-host-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string owner = Guid.NewGuid().ToString(), supervisor = Guid.NewGuid().ToString();
        using var firstPort = new TcpListener(IPAddress.Loopback, 0);
        using var secondPort = new TcpListener(IPAddress.Loopback, 0);
        firstPort.Start(); secondPort.Start();
        int httpPort = ((IPEndPoint)firstPort.LocalEndpoint).Port;
        int grpcPort = ((IPEndPoint)secondPort.LocalEndpoint).Port;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { typeof(PiperRpcContext).Assembly.Location, "--simulate", "--port", httpPort.ToString(),
            "--grpc-port", grpcPort.ToString(), "--state-dir", directory, "--session-owner", owner })
            start.ArgumentList.Add(argument);
        if (!pluginOwned)
            foreach (string argument in new[] { "--restart-supervisor-id", supervisor, "--restart-supervisor-pid", Environment.ProcessId.ToString() })
                start.ArgumentList.Add(argument);
        firstPort.Stop(); secondPort.Stop();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start hardware-disabled maintenance fixture");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"), Timeout = TimeSpan.FromSeconds(2) };
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
        var client = new PiperControl.PiperControlClient(channel);
        JsonObject? state = null;
        AsyncServerStreamingCall<StateFrame>? observer = null;
        try
        {
            long started = Stopwatch.GetTimestamp();
            bool queried = false;
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                if (process.HasExited) throw new InvalidOperationException("Simulated maintenance host exited during startup");
                try
                {
                    state = JsonNode.Parse(await http.GetStringAsync("/api/state"))!.AsObject();
                    if (!queried && state["simulated"]?.GetValue<bool>() == true && state["devices"] is JsonArray { Count: 1 } devices)
                    {
                        // The native simulator begins with no queried firmware,
                        // just like a fresh transport. Read its fake configuration
                        // through the real API; never weaken production admission.
                        using var select = new HttpRequestMessage(HttpMethod.Post, "/api/select") {
                            Content = new StringContent(new JsonObject { ["owner"] = owner,
                                ["device_id"] = devices[0]!["id"]!.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json") };
                        select.Headers.Add("X-Piper-Token", state["token"]!.GetValue<string>());
                        using var selected = await http.SendAsync(select); selected.EnsureSuccessStatusCode();
                        using var query = new HttpRequestMessage(HttpMethod.Post, "/api/query") {
                            Content = new StringContent(new JsonObject { ["owner"] = owner,
                                ["device_id"] = devices[0]!["id"]!.DeepClone(), ["generation"] = devices[0]!["generation"]!.DeepClone()
                            }.ToJsonString(), Encoding.UTF8, "application/json") };
                        query.Headers.Add("X-Piper-Token", state["token"]!.GetValue<string>());
                        using var queriedReply = await http.SendAsync(query); queriedReply.EnsureSuccessStatusCode();
                        queried = true;
                    }
                    if (BackendMaintenance.Check(state, [], owner) is null) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            if (state is null || state["simulated"]?.GetValue<bool>() != true || BackendMaintenance.Check(state, [], owner) is not null)
                throw new InvalidOperationException("Simulated maintenance host did not become idle and stationary: " +
                    (state is null ? "no state" : BackendMaintenance.Check(state, [], owner)));
            if (state["backend_restart"]?["supported"]?.GetValue<bool>() != true ||
                state["backend_restart"]?["mode"]?.GetValue<string>() != (pluginOwned ? "plugin" : "supervisor") ||
                state["backend_restart"]?["plugin_owner"]?.GetValue<string>() != owner ||
                !pluginOwned && (state["backend_restart"]?["supervisor_process_id"]?.GetValue<int>() != Environment.ProcessId ||
                    state["backend_restart"]?["supervisor_instance_id"]?.GetValue<string>() != supervisor))
                throw new InvalidOperationException("Host lost its exact restart supervisor/plugin identity");
            string token = state["token"]!.GetValue<string>();
            var metadata = new Metadata { { "x-piper-token", token } };
            var body = new JsonObject { ["owner"] = owner, ["backend_process_id"] = process.Id,
                ["backend_instance_id"] = state["backend_instance_id"]!.DeepClone(), ["release_grippers"] = true };
            using (var unauthenticated = await Post(body, "obsolete-token"))
                if (unauthenticated.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("Maintenance HTTP token guard was bypassed");
            var wrongOwner = body.DeepClone().AsObject(); wrongOwner["owner"] = Guid.NewGuid().ToString();
            using (var foreign = await Post(wrongOwner, token))
                if (foreign.StatusCode != HttpStatusCode.Conflict) throw new InvalidOperationException("Maintenance session-owner guard was bypassed");
            var stale = body.DeepClone().AsObject(); stale["backend_instance_id"] = "old-process-instance";
            using (var old = await Post(stale, token))
                if (old.StatusCode != HttpStatusCode.Conflict) throw new InvalidOperationException("Maintenance admitted an obsolete backend session");
            var noRelease = body.DeepClone().AsObject(); noRelease["release_grippers"] = false;
            var denied = await client.ExecuteAsync(new ControlRequest { Route = BackendMaintenance.RestartRoute, Json = noRelease.ToJsonString() }, metadata);
            if (denied.Status != 409 || process.HasExited) throw new InvalidOperationException("Maintenance did not require the explicit gripper-release acknowledgement");
            observer = client.WatchState(new StateRequest());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            if (!await observer.ResponseStream.MoveNext(deadline.Token)) throw new InvalidOperationException("Maintenance observer ended before restart");
            string reply;
            started = Stopwatch.GetTimestamp();
            if (grpc)
            {
                var accepted = await client.ExecuteAsync(new ControlRequest { Route = BackendMaintenance.RestartRoute, Json = body.ToJsonString() }, metadata);
                if (accepted.Status != 202) throw new InvalidOperationException("gRPC restart rejected: " + accepted.Json);
                reply = accepted.Json;
            }
            else
            {
                using var accepted = await Post(body, token);
                reply = await accepted.Content.ReadAsStringAsync();
                if (accepted.StatusCode != HttpStatusCode.Accepted) throw new InvalidOperationException("HTTP restart rejected: " + reply);
            }
            var result = JsonNode.Parse(reply)!;
            if (result["ok"]?.GetValue<bool>() != true || result["backend_process_id"]?.GetValue<int>() != process.Id ||
                result["backend_instance_id"]?.GetValue<string>() != state["backend_instance_id"]?.GetValue<string>() ||
                result["restart_mode"]?.GetValue<string>() != (pluginOwned ? "plugin" : "supervisor") ||
                !pluginOwned && result["supervisor_instance_id"]?.GetValue<string>() != supervisor ||
                result["releases_gripper_torque"]?.GetValue<bool>() != true || result["recovery_preserved"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Restart response was not flushed with exact identity and power/recovery semantics");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (process.ExitCode != BackendMaintenance.RestartExitCode) throw new InvalidOperationException("Restart did not return reserved supervisor exit75");
            while (await observer.ResponseStream.MoveNext(deadline.Token)) { }
            Console.WriteLine($"Host restart {(pluginOwned ? "plugin-owned" : "supervised")} {(grpc ? "gRPC" : "HTTP")} passed: exact identity/auth/ack guards; response received, observer drained, clean exit75 in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; hardware disabled.");
        }
        catch
        {
            if (process.HasExited) Console.Error.WriteLine(await stdout + await stderr);
            throw;
        }
        finally
        {
            observer?.Dispose();
            if (!process.HasExited)
            {
                var current = JsonNode.Parse(await http.GetStringAsync("/api/state"))!;
                using var shutdown = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown") {
                    Content = new StringContent(new JsonObject { ["owner"] = owner }.ToJsonString(), Encoding.UTF8, "application/json") };
                shutdown.Headers.Add("X-Piper-Token", current["token"]!.GetValue<string>());
                using var response = await http.SendAsync(shutdown);
                response.EnsureSuccessStatusCode();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }
            Directory.Delete(directory, recursive: true);
        }

        Task<HttpResponseMessage> Post(JsonObject body, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, BackendMaintenance.RestartRoute) {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Piper-Token", token);
            return Send();
            async Task<HttpResponseMessage> Send() { using (request) return await http.SendAsync(request); }
        }
    }
}
