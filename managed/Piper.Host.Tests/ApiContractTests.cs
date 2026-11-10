using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Host;

/// <summary>The API definition (piper-api.json) against the running host. Every example the
/// definition gives is sent over HTTP to one fresh simulated host and over gRPC to another,
/// in the definition's order, and must be answered identically with a status the definition
/// lists. The generated route table must say what the definition says and what the host does.</summary>
internal static class ApiContractTests
{
    private const string AdapterPlaceholder = "usb:YOUR_ADAPTER_SERIAL";
    /// <summary>The only answer fields that name the answering process, so the only ones two hosts may differ in.</summary>
    private static readonly string[] ProcessIdentity = ["backend_process_id", "backend_instance_id"];

    internal static async Task Run()
    {
        var definition = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "piper-api.json")))!.AsObject();
        var routes = definition["routes"]!.AsArray().Select(route => route!.AsObject()).ToArray();
        CheckTable(definition, routes);
        CheckBuilders(routes);

        string[] common = [.. definition["common_responses"]!.AsObject().Select(pair => pair.Key)];
        await using var browser = await SimulatedHost.Start();
        await using var tunnel = await SimulatedHost.Start();
        int sent = 0;
        foreach (var route in routes.Where(route => route["grpc"]?.GetValue<bool>() == true))
        {
            string path = route["path"]!.GetValue<string>();
            string[] documented = [.. route["responses"]!.AsObject().Select(pair => pair.Key)];
            foreach (var example in route["examples"]!.AsArray())
            {
                string name = $"{path} example {example!["name"]!.GetValue<string>()}";
                var over = await browser.Send(path, await browser.Fill(example["body"]!), grpc: false);
                var through = await tunnel.Send(path, await tunnel.Fill(example["body"]!), grpc: true);
                Check(over.Status == through.Status && JsonNode.DeepEquals(over.Body, through.Body),
                    $"{name} is answered differently over HTTP ({over.Status} {over.Body.ToJsonString()}) and gRPC ({through.Status} {through.Body.ToJsonString()})");
                string status = over.Status.ToString();
                Check(documented.Contains(status) || common.Contains(status), $"{name} was answered {status}, which the definition does not list");
                ++sent;
            }
        }
        Check(sent >= routes.Count(route => route["grpc"]?.GetValue<bool>() == true), "A tunnelled route has no example");
        // A route the definition keeps off the tunnel is refused there, and a path it does
        // not give the managed host is no route on either transport.
        foreach (var route in routes.Where(route => route["method"]!.GetValue<string>() == "POST"))
        {
            string path = route["path"]!.GetValue<string>();
            bool managed = route["backend"]!.GetValue<string>() != "c";
            if (managed && route["grpc"]!.GetValue<bool>()) continue;
            var refused = await tunnel.Refused(path);
            Check(refused == StatusCode.InvalidArgument, $"gRPC carried {path}, which the definition keeps off the tunnel");
            if (!managed) Check((await browser.Send(path, "{}", grpc: false)).Status == 404, $"The managed host served the C-only route {path}");
        }
        Console.WriteLine($"API contract passed: {sent} examples of {routes.Length} routes answered identically over HTTP and gRPC.");
    }

    /// <summary>The generated table is the definition, row for row.</summary>
    private static void CheckTable(JsonObject definition, JsonObject[] routes)
    {
        Check(PiperApi.Routes.Count == routes.Length, "The generated route table and the definition differ in length");
        Check(PiperApi.MaxBodyBytes == definition["limits"]!["max_body_bytes"]!.GetValue<int>() &&
            PiperApi.MaxDepth == definition["limits"]!["max_depth"]!.GetValue<int>(), "The generated limits differ from the definition");
        using var empty = JsonDocument.Parse("{}");
        foreach (var (route, generated) in routes.Zip(PiperApi.Routes))
        {
            string path = route["path"]!.GetValue<string>(), method = route["method"]!.GetValue<string>();
            bool post = method == "POST", managed = route["backend"]!.GetValue<string>() != "c";
            bool owned = route["owner"]?.GetValue<string>() == "session", stop = route["class"]!.GetValue<string>() == "stop";
            Check(generated.Id == route["id"]!.GetValue<string>() && generated.Method == method && generated.Path == path &&
                generated.Class.ToString().Equals(route["class"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase) &&
                generated.Backend.ToString().Equals(route["backend"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase) &&
                generated.SessionOwned == owned && generated.Grpc == (route["grpc"]?.GetValue<bool>() == true),
                $"The generated row for {method} {path} differs from the definition");
            if (!post) continue;
            Check(ReferenceEquals(PiperApi.ManagedControl(path), managed ? generated : null), $"{path} resolves to the wrong control");
            // The session guard and the maintenance freeze read the same row.
            Check(SessionOwnerGuard.Allows("plugin-session", path, empty.RootElement) == (managed && !owned),
                $"The session guard and the definition disagree about {path}");
            Check(PiperApi.IsStop(path) == (managed && stop), $"The STOP class of {path} differs from the definition");
            if (stop) Check(!owned && generated.Grpc, $"STOP route {path} can be refused for ownership or transport");
        }
        Check(PiperApi.ManagedControl("/api/unlisted") is null && !SessionOwnerGuard.Allows("plugin-session", "/api/unlisted", empty.RootElement),
            "A path outside the definition is a route or passes the session guard");
    }

    /// <summary>The generated C# builders produce the definition's own examples.</summary>
    private static void CheckBuilders(JsonObject[] routes)
    {
        JsonNode Example(string id, string name) => routes.Single(route => route["id"]!.GetValue<string>() == id)["examples"]!.AsArray()
            .Single(example => example!["name"]!.GetValue<string>() == name)!["body"]!;
        void Same(ApiRequest built, string id, string name) =>
            Check(built.Route.Id == id && JsonNode.DeepEquals(built.Body, Example(id, name)), $"The generated {id} builder does not produce the {name} example: {built.Json}");
        const string owner = "YOUR_UNIQUE_CLIENT_ID", gripperOwner = "00000000-0000-4000-8000-000000000000";
        Same(PiperApi.Requests.Stop(owner: owner), "stop", "emergency");
        Same(PiperApi.Requests.Stop(owner: owner, deviceId: AdapterPlaceholder, requestId: 1), "stop", "cancel");
        Same(PiperApi.Requests.CoordinatedStop(), "coordinatedStop", "unnamed");
        Same(PiperApi.Requests.Select(AdapterPlaceholder, owner), "select", "select");
        Same(PiperApi.Requests.Heartbeat(owner, 1, AdapterPlaceholder, 1), "heartbeat", "renew");
        Same(PiperApi.Requests.PikaConnect(gripperOwner, takeOver: false), "pikaConnect", "connect");
        Same(PiperApi.Requests.PikaPosition(gripperOwner, .5), "pikaPosition", "position");
        // An optional field that may be null is absent, null or a value: three different requests.
        Check(!PiperApi.Requests.PikaHold(gripperOwner).Body.ContainsKey("pika_distribution") &&
            PiperApi.Requests.PikaHold(gripperOwner, pikaDistribution: null).Json.Contains("\"pika_distribution\":null", StringComparison.Ordinal) &&
            PiperApi.Requests.PikaHold(gripperOwner, pikaDistribution: "Ubuntu").Body["pika_distribution"]!.GetValue<string>() == "Ubuntu",
            "A nullable optional field does not distinguish absent from null");
    }

    /// <summary>One hardware-disabled host process and both of its transports.</summary>
    private sealed class SimulatedHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly string directory;
        private readonly HttpClient http;
        private readonly GrpcChannel channel;
        private readonly PiperControl.PiperControlClient client;
        private string token = "";

        private SimulatedHost(Process process, string directory, int port, int grpcPort)
        {
            this.process = process; this.directory = directory;
            http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}");
            client = new PiperControl.PiperControlClient(channel);
        }

        internal static async Task<SimulatedHost> Start()
        {
            string directory = Path.Combine(Path.GetTempPath(), "piper-api-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var a = new TcpListener(IPAddress.Loopback, 0); using var b = new TcpListener(IPAddress.Loopback, 0);
            a.Start(); b.Start(); int port = ((IPEndPoint)a.LocalEndpoint).Port, grpcPort = ((IPEndPoint)b.LocalEndpoint).Port;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { typeof(PiperRpcContext).Assembly.Location, "--simulate", "--port", port.ToString(),
                "--grpc-port", grpcPort.ToString(), "--state-dir", directory }) start.ArgumentList.Add(argument);
            a.Stop(); b.Stop();
            var host = new SimulatedHost(Process.Start(start) ?? throw new InvalidOperationException("Cannot start the contract host"), directory, port, grpcPort);
            long began = Stopwatch.GetTimestamp();
            while (host.token.Length == 0)
            {
                if (host.process.HasExited || Stopwatch.GetElapsedTime(began).TotalSeconds > 15) throw new InvalidOperationException("The contract host did not start");
                try { host.token = (await host.Read())["token"]!.GetValue<string>(); } catch (HttpRequestException) { await Task.Delay(50); }
            }
            return host;
        }

        private async Task<JsonObject> Read() => JsonNode.Parse(await http.GetStringAsync(PiperApi.Paths.State))!.AsObject();

        /// <summary>Waits until no arm action or query is in flight, then names this host's
        /// selected arm and its generation where the example has placeholders.</summary>
        internal async Task<string> Fill(JsonNode example)
        {
            JsonObject state;
            long began = Stopwatch.GetTimestamp();
            while (true)
            {
                state = await Read();
                if (state["any_motion_active"]?.GetValue<bool>() != true && state["any_query_active"]?.GetValue<bool>() != true) break;
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 15) throw new InvalidOperationException("The contract host did not become idle: " + state);
                await Task.Delay(25);
            }
            var body = example.DeepClone().AsObject();
            string selected = state["selected"]!.GetValue<string>();
            if (body["device_id"]?.GetValue<string>() == AdapterPlaceholder)
            {
                body["device_id"] = selected;
                if (body.ContainsKey("generation"))
                    body["generation"] = state["devices"]!.AsArray().Single(device => device!["id"]!.GetValue<string>() == selected)!["generation"]!.DeepClone();
            }
            return body.ToJsonString();
        }

        internal async Task<(int Status, JsonNode Body)> Send(string path, string body, bool grpc)
        {
            if (grpc)
            {
                var reply = await client.ExecuteAsync(new ControlRequest { Route = path, Json = body }, new Metadata { { "x-piper-token", token } });
                return (reply.Status, Anonymous(JsonNode.Parse(reply.Json)!));
            }
            using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            message.Headers.Add("X-Piper-Token", token);
            using var response = await http.SendAsync(message);
            return ((int)response.StatusCode, Anonymous(JsonNode.Parse(await response.Content.ReadAsStringAsync())!));
        }

        private static JsonNode Anonymous(JsonNode answer)
        {
            if (answer is JsonObject fields) foreach (string name in ProcessIdentity) if (fields.ContainsKey(name)) fields[name] = "(this process)";
            return answer;
        }

        /// <summary>The gRPC status that refuses a route the tunnel does not carry, or OK when it was carried.</summary>
        internal async Task<StatusCode> Refused(string path)
        {
            try { await client.ExecuteAsync(new ControlRequest { Route = path, Json = "{}" }, new Metadata { { "x-piper-token", token } }); return StatusCode.OK; }
            catch (RpcException refused) { return refused.StatusCode; }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) await Send(PiperApi.Paths.Shutdown, "{}", grpc: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or TaskCanceledException) { if (!process.HasExited) process.Kill(); }
            channel.Dispose(); http.Dispose(); process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
