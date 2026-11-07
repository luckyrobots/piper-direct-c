using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Host;

await BackendMaintenanceTests.Run();
await MultiRobotHostIntegrationTests.Run();

// Run the actual HTTP/gRPC host with its hardware-disabled simulator. Keep
// the observer connected through shutdown: cancelling it first hides the bug.
string directory = Path.Combine(Path.GetTempPath(), "piper-host-shutdown-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
using var httpReservation = new TcpListener(IPAddress.Loopback, 0);
using var grpcReservation = new TcpListener(IPAddress.Loopback, 0);
httpReservation.Start(); grpcReservation.Start();
int httpPort = ((IPEndPoint)httpReservation.LocalEndpoint).Port;
int grpcPort = ((IPEndPoint)grpcReservation.LocalEndpoint).Port;
var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
string host = typeof(PiperRpcContext).Assembly.Location;
foreach (string argument in new[] { host, "--simulate", "--port", httpPort.ToString(), "--grpc-port", grpcPort.ToString(), "--state-dir", directory })
    start.ArgumentList.Add(argument);
httpReservation.Stop(); grpcReservation.Stop();
using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the simulated host");
Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"), Timeout = TimeSpan.FromSeconds(2) };
using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
AsyncServerStreamingCall<StateFrame>? observer = null;
bool shutdownRequested = false;
try
{
    JsonObject? state = null;
    long started = Stopwatch.GetTimestamp();
    while (Stopwatch.GetElapsedTime(started).TotalSeconds < 15)
    {
        if (process.HasExited) throw new InvalidOperationException("Simulated host exited during startup");
        try { state = JsonNode.Parse(await http.GetStringAsync("/api/state"))!.AsObject(); break; }
        catch (HttpRequestException) { await Task.Delay(50); }
    }
    if (state is null || state["simulated"]?.GetValue<bool>() != true || state["motion"]?["active"]?.GetValue<bool>() != false)
        throw new InvalidOperationException("Shutdown fixture did not start an idle hardware-disabled simulator");
    if (state["pikas"] is not JsonArray { Count: 0 } || state["pika"]?["connected"]?.GetValue<bool>() != false)
        throw new InvalidOperationException("Host did not expose independent gripper sessions without opening hardware.");
    var inventory = JsonNode.Parse(await http.GetStringAsync("/api/pika/devices"));
    if (inventory?["devices"] is not JsonArray { Count: 0 }) throw new InvalidOperationException("Simulated host enumerated physical USB grippers.");
    var grpcClient = new PiperControl.PiperControlClient(channel);
    var metadata = new Metadata { { "x-piper-token", state["token"]!.GetValue<string>() } };
    var inventoryReply = await grpcClient.ExecuteAsync(new ControlRequest { Route = "/api/pika/devices", Json = "{}" }, metadata);
    if (inventoryReply.Status != 200 || JsonNode.Parse(inventoryReply.Json)?["devices"] is not JsonArray { Count: 0 })
        throw new InvalidOperationException("gRPC inventory route differs from HTTP or opened simulated hardware.");
    await ControlJsonValidationTests.Run(http, grpcClient, state["token"]!.GetValue<string>());

    observer = new PiperControl.PiperControlClient(channel).WatchState(new StateRequest());
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    long sequence = 0;
    for (int i = 0; i < 3; ++i)
    {
        if (!await observer.ResponseStream.MoveNext(deadline.Token)) throw new InvalidOperationException("Observer ended before shutdown");
        var frame = observer.ResponseStream.Current;
        if (frame.Sequence <= sequence || JsonNode.Parse(frame.Json)?["simulated"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Observer did not receive live simulated state");
        sequence = frame.Sequence;
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown")
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    };
    request.Headers.Add("X-Piper-Token", state["token"]!.GetValue<string>());
    started = Stopwatch.GetTimestamp();
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode(); shutdownRequested = true;
    // The client has not cancelled/disposed its stream. The host must end it
    // itself, drain Kestrel and dispose the native controller before exiting.
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    if (process.ExitCode != 0) throw new InvalidOperationException("Simulated host did not exit cleanly");
    while (await observer.ResponseStream.MoveNext(deadline.Token)) { }
    Console.WriteLine($"Host shutdown passed: active gRPC observer ended normally; clean simulated host exit in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms without cancelling the client.");
}
catch
{
    Console.Error.WriteLine(await ReadLogs());
    throw;
}
finally
{
    observer?.Dispose();
    if (!process.HasExited)
    {
        if (!shutdownRequested)
        {
            try
            {
                var state = JsonNode.Parse(await http.GetStringAsync("/api/state"));
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shutdown") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                request.Headers.Add("X-Piper-Token", state!["token"]!.GetValue<string>());
                using var response = await http.SendAsync(request);
            }
            catch (HttpRequestException) { }
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
    }
    Directory.Delete(directory, recursive: true);
}

await BackendRestartIntegrationTests.Run();

async Task<string> ReadLogs()
{
    if (process.HasExited) return await output + await errors;
    return "Simulated host still running; shutdown did not finish within the test deadline.";
}
