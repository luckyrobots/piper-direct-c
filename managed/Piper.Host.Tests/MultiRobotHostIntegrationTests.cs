using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Host;

internal static class MultiRobotHostIntegrationTests
{
    internal static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "piper-two-lane-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string owner = Guid.NewGuid().ToString();
        using var a = new TcpListener(IPAddress.Loopback, 0); using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start(); b.Start(); int port = ((IPEndPoint)a.LocalEndpoint).Port, grpcPort = ((IPEndPoint)b.LocalEndpoint).Port;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(PiperRpcContext).Assembly.Location, "--simulate", "--sim-robots", "2", "--port", port.ToString(),
            "--grpc-port", grpcPort.ToString(), "--state-dir", directory, "--session-owner", owner }) start.ArgumentList.Add(arg);
        a.Stop(); b.Stop();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start two-device simulator");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
        var client = new PiperControl.PiperControlClient(channel);
        JsonObject? state = null; string token = "";
        var requests = new Dictionary<string, long>(); var generations = new Dictionary<string, long>();
        async Task<JsonObject> Read() => JsonNode.Parse(await http.GetStringAsync("/api/state"))!.AsObject();
        async Task<(int Status, JsonObject Body)> Send(string route, JsonObject body, bool grpc = false, string? requestOwner = null)
        {
            body["owner"] = requestOwner ?? owner;
            if (grpc) { var r = await client.ExecuteAsync(new ControlRequest { Route = route, Json = body.ToJsonString() }, new Metadata { { "x-piper-token", token } }); return (r.Status, JsonNode.Parse(r.Json)!.AsObject()); }
            using var message = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Piper-Token", token); using var reply = await http.SendAsync(message);
            return ((int)reply.StatusCode, JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject());
        }
        JsonObject Identity(string id) => new() { ["device_id"] = id, ["generation"] = generations[id], ["standalone"] = true, ["clear"] = true, ["supported"] = true };
        JsonObject Motion(JsonObject snapshot, string id) => snapshot["motions"]!.AsArray().OfType<JsonObject>().Single(l => l["device_id"]!.GetValue<string>() == id);
        bool ActiveRequest(JsonObject snapshot, string id, long request) =>
            Motion(snapshot, id) is var lane && lane["active"]?.GetValue<bool>() == true && lane["request_id"]?.GetValue<long>() == request;
        async Task Poll(Func<JsonObject, bool> condition, string failure, int seconds = 10)
        {
            long began = Stopwatch.GetTimestamp();
            do {
                state = await Read();
                foreach (var pair in requests) {
                    if (!ActiveRequest(state, pair.Key, pair.Value)) continue;
                    var beat = Identity(pair.Key); beat["request_id"] = pair.Value;
                    var ack = await Send("/api/heartbeat", beat, pair.Key == "simulation:2");
                    if (ack.Status < 400) continue;
                    // The lane can finish between the state read and the renewal.
                    // Only a lease that is still active for this request is a failure.
                    if (ack.Status == 409 && ack.Body["error"]?.GetValue<string>() == "No matching active control lease." &&
                        !ActiveRequest(await Read(), pair.Key, pair.Value)) continue;
                    throw new Exception("Device heartbeat rejected: " + ack.Body);
                }
                if (condition(state)) return;
                await Task.Delay(50);
            } while (Stopwatch.GetElapsedTime(began).TotalSeconds < seconds);
            throw new Exception(failure + ": " + state);
        }
        try
        {
            long began = Stopwatch.GetTimestamp();
            while (state is null && Stopwatch.GetElapsedTime(began).TotalSeconds < 15) {
                if (process.HasExited) throw new Exception("Two-device host exited during startup");
                try { state = await Read(); } catch (HttpRequestException) { await Task.Delay(50); }
            }
            if (state?["simulated"]?.GetValue<bool>() != true || state["backend_process_id"]?.GetValue<int>() != process.Id ||
                state["devices"] is not JsonArray { Count: 2 } devices ||
                state["multi_device_lanes_supported"]?.GetValue<bool>() != true) throw new Exception("Host did not expose two independent simulated lanes");
            var grpcState = JsonNode.Parse((await client.GetStateAsync(new(), deadline: DateTime.UtcNow.AddSeconds(3))).Json);
            if (grpcState?["simulated"]?.GetValue<bool>() != true || grpcState["backend_process_id"]?.GetValue<int>() != process.Id)
                throw new Exception("gRPC endpoint is not the two-lane simulator fixture");
            token = state["token"]!.GetValue<string>();
            foreach (var d in devices.OfType<JsonObject>()) generations.Add(d["id"]!.GetValue<string>(), d["generation"]!.GetValue<long>());
            string foreign = Guid.NewGuid().ToString();
            foreach (bool grpc in new[] { false, true })
            {
                var denied = await Send("/api/select", Identity("simulation:2"), grpc, foreign);
                if (denied.Status != 409 || denied.Body["error"]?.GetValue<string>() != LuckyRobots.Piper.SessionOwnerGuard.Denied ||
                    denied.Body["ok"]?.GetValue<bool>() != false)
                    throw new Exception("A foreign caller acquired a lane or an adapter changed its owner-error response");
            }
            if (!JsonNode.DeepEquals((await Read())["selected"], state["selected"])) throw new Exception("Denied owner changed the selected lane");
            foreach (string id in generations.Keys) {
                var result = await Send("/api/query", Identity(id), id == "simulation:2");
                if (result.Status != 202) throw new Exception("Device query rejected: " + result.Body);
            }
            await Poll(s => s["any_query_active"]?.GetValue<bool>() == false && s["devices"]!.AsArray().All(d => d?["firmware_supported"]?.GetValue<bool>() == true), "Both device queries did not finish");
            foreach (string id in generations.Keys) {
                var body = Identity(id); body["action"] = "enable";
                var result = await Send("/api/command", body, id == "simulation:2");
                if (result.Status != 202) throw new Exception("Enable rejected: " + result.Body);
                requests[id] = result.Body["request_id"]!.GetValue<long>();
            }
            await Poll(s => s["any_motion_active"]?.GetValue<bool>() == false && s["devices"]!.AsArray().All(d =>
                d!["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true) && d["status"]?["pose_stationary_s"]?.GetValue<double>() >= .4), "Both enable actions did not settle");
            foreach (string id in generations.Keys) {
                var body = Identity(id); body["action"] = "joint"; body["joint"] = 6; body["degrees"] = id == "simulation:one" ? 20 : -20;
                body["speed_percent"] = 5; body["tolerance_deg"] = .2;
                var result = await Send("/api/command", body, id == "simulation:2");
                if (result.Status != 202) throw new Exception("Concurrent joint action rejected: " + result.Body);
                requests[id] = result.Body["request_id"]!.GetValue<long>();
            }
            if (requests.Values.Distinct().Count() != 2) throw new Exception("Request IDs collided across devices");
            await Poll(s => s["motions"]!.AsArray().All(l => l?["active"]?.GetValue<bool>() == true) &&
                s["devices"]!.AsArray().All(d => Math.Abs(d!["joints"]![5]!["actual_deg"]!.GetValue<double>()) > .1), "Both devices did not move independently");
            var selected = await Send("/api/select", Identity("simulation:one"));
            if (selected.Status >= 400) throw new Exception("Presentation selection was blocked by other lane");
            state = await Read();
            if (state["motions"]!.AsArray().Any(l => l?["active"]?.GetValue<bool>() != true)) throw new Exception("Selection cancelled a lane");
            var hold = Identity("simulation:one"); hold["action"] = "hold";
            if ((await Send("/api/command", hold)).Status >= 400) throw new Exception("Scoped hold failed");
            await Poll(s => s["motion"]?["active"]?.GetValue<bool>() == false && Motion(s, "simulation:2")["active"]?.GetValue<bool>() == true, "Scoped hold stopped the other arm");
            if ((await Send("/api/shutdown", new())).Status != 409) throw new Exception("Shutdown ignored nonselected movement");
            var restart = new JsonObject { ["backend_process_id"] = process.Id, ["backend_instance_id"] = state!["backend_instance_id"]!.DeepClone(), ["release_grippers"] = true };
            if ((await Send(BackendMaintenance.RestartRoute, restart, true)).Status != 409 || process.HasExited) throw new Exception("Restart interrupted a nonselected device lane");
            var stop = await Send("/api/stop/coordinated", new(), true);
            if (stop.Status >= 400) throw new Exception("Global coordinated STOP failed: " + stop.Body);
            await Poll(s => s["any_motion_active"]?.GetValue<bool>() == false && s["motions"]!.AsArray().All(l => l?["active"]?.GetValue<bool>() == false), "Global STOP left a lane active");
            if ((await Send("/api/shutdown", new())).Status != 200) throw new Exception("Stopped simulator did not allow shutdown");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (process.ExitCode != 0) throw new Exception("Two-lane host did not close cleanly");
            Console.WriteLine("Two-device host passed: concurrent HTTP/gRPC actions and heartbeats, selection isolation, nonselected restart/shutdown rejection, all-lane coordinated STOP; hardware disabled.");
        }
        catch { if (process.HasExited) Console.Error.WriteLine(await stdout + await stderr); throw; }
        finally
        {
            if (!process.HasExited && token.Length > 0) {
                try { await Send("/api/stop", new()); await Send("/api/shutdown", new()); } catch (HttpRequestException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }
            if (process.HasExited) Directory.Delete(directory, true);
        }
    }
}
