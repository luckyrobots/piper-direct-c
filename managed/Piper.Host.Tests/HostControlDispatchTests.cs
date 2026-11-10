using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;

internal static class HostControlDispatchTests
{
    internal static async Task Run(HttpClient http, PiperControl.PiperControlClient grpc, JsonObject dashboard, int processId)
    {
        string token = dashboard["token"]!.GetValue<string>();
        var headers = new Metadata { { "x-piper-token", token } };
        var frame = await grpc.GetStateAsync(new(), deadline: DateTime.UtcNow.AddSeconds(3));
        var state = JsonNode.Parse(frame.Json)!.AsObject();
        Check(frame.Sequence > 0 && frame.TimestampUnixMicroseconds > 0 && frame.ClockDomain == "host-utc" &&
            state["simulated"]?.GetValue<bool>() == true && dashboard["backend_process_id"]?.GetValue<int>() == processId &&
            state["backend_process_id"]?.GetValue<int>() == processId, "Both adapters must identify the fixture host and keep the gRPC time envelope");
        foreach (string field in new[] { "token", "backend_instance_id", "backend_restart", "pika", "pikas" })
            Check(dashboard.ContainsKey(field) && JsonNode.DeepEquals(dashboard[field], state[field]), "Shared snapshot differs for " + field);
        Check(dashboard["backend"] is JsonValue && !state.ContainsKey("backend"), "Dashboard-only state fields moved into gRPC");
        Check(!dashboard.ContainsKey("camera") && !state.ContainsKey("camera"), "The managed host exposed the C-only bench-camera state");

        var browserEnable = await Post("/api/pika/enable", "{}");
        var grpcEnable = await Execute("/api/pika/enable", "{}");
        var grpcError = JsonNode.Parse(grpcEnable.Json)!.AsObject();
        Check(browserEnable.Status == HttpStatusCode.Conflict && grpcEnable.Status == 409 &&
            browserEnable.Body["ok"]?.GetValue<bool>() == false &&
            browserEnable.Body["error"]?.GetValue<string>() == "Pika hardware is disabled in simulation" &&
            JsonNode.DeepEquals(browserEnable.Body, grpcError), "Pika simulation rejection differs between the adapters");
        // Simulated inventory is the same empty list on every transport and verb.
        var browserDevices = await Post("/api/pika/devices", "{}");
        var grpcDevices = await Execute("/api/pika/devices", "{}");
        Check(browserDevices.Status == HttpStatusCode.OK && grpcDevices.Status == 200 &&
            browserDevices.Body["devices"] is JsonArray { Count: 0 } &&
            JsonNode.DeepEquals(browserDevices.Body, JsonNode.Parse(grpcDevices.Json)), "Simulated Pika inventory differs between the adapters");

        await UnsupportedGrpc("/api/shutdown", "{}");
        await UnsupportedGrpc("/api/camera", """{"enabled":false}""");

        var device = dashboard["devices"]![0]!;
        string scope = new JsonObject { ["device_id"] = device["id"]?.DeepClone(), ["generation"] = device["generation"]?.DeepClone() }.ToJsonString();
        // The bench webcam belongs to the C server; the managed host has no camera routes.
        var camera = await Post("/api/camera", scope);
        Check(camera.Status == HttpStatusCode.NotFound && camera.Body["error"]?.GetValue<string>() == "Unknown API route.",
            "Rejected gRPC shutdown froze HTTP controls or the managed host kept a camera stub");
        using (var cameraFrame = await http.GetAsync("/api/camera/frame"))
            Check(cameraFrame.StatusCode == HttpStatusCode.NotFound, "The managed host kept the camera frame stub");
        var unknown = await Post("/api/unsupported-fixture", scope);
        Check(unknown.Status == HttpStatusCode.NotFound && unknown.Body["error"]?.GetValue<string>() == "Unknown API route.",
            "Unknown HTTP control route did not reach native route validation");
        await UnsupportedGrpc("/api/unsupported-fixture", scope);

        string owner = new JsonObject { ["owner"] = Guid.NewGuid().ToString() }.ToJsonString();
        var stopped = await Post("/api/stop/coordinated", owner);
        Check(stopped.Status == HttpStatusCode.OK && stopped.Body["ok"]?.GetValue<bool>() == true &&
            stopped.Body["arm"]?["ok"]?.GetValue<bool>() == true && stopped.Body["pika"]?["ok"]?.GetValue<bool>() == true,
            "HTTP coordinated STOP lost an independent simulated channel outcome");
        var ownerless = await Post("/api/stop/coordinated", "{}");
        Check(ownerless.Status == HttpStatusCode.OK && ownerless.Body["arm"]?["ok"]?.GetValue<bool>() == true &&
            ownerless.Body["pika"]?["ok"]?.GetValue<bool>() == true,
            "An emergency coordinated STOP that names nobody was refused: " + ownerless.Body);
        var emptyScope = JsonNode.Parse(owner)!.AsObject();
        emptyScope["device_id"] = "";
        var empty = await Execute("/api/stop/coordinated", emptyScope.ToJsonString());
        Check(empty.Status == 200 && JsonNode.Parse(empty.Json)?["pika"]?["ok"]?.GetValue<bool>() == true,
            "An empty legacy arm selector stopped all arms but did not hold all Pikas");
        emptyScope["generation"] = device["generation"]?.DeepClone();
        var staleScope = await Post("/api/stop/coordinated", emptyScope.ToJsonString());
        Check(staleScope.Status == HttpStatusCode.Conflict && staleScope.Body["arm"]?["ok"]?.GetValue<bool>() == false &&
            staleScope.Body["pika"]?["ok"]?.GetValue<bool>() == false &&
            staleScope.Body["pika"]?["message"]?.GetValue<string>().Contains("Every Pika was asked to hold instead. All Pika holds acknowledged.", StringComparison.Ordinal) == true,
            "A STOP whose scope names no arm was refused instead of holding everything and saying so: " + staleScope.Body);
        emptyScope.Remove("generation");
        emptyScope["device_id"] = null;
        var malformed = await Post("/api/stop/coordinated", emptyScope.ToJsonString());
        Check(malformed.Status == HttpStatusCode.Conflict && malformed.Body["arm"]?["ok"]?.GetValue<bool>() == false &&
            malformed.Body["pika"]?["ok"]?.GetValue<bool>() == false &&
            malformed.Body["pika"]?["message"]?.GetValue<string>().Contains("device_id is not an adapter identity. Every Pika was asked to hold instead.", StringComparison.Ordinal) == true,
            "A STOP with a malformed arm selector was refused instead of holding everything and saying so: " + malformed.Body);
        emptyScope["device_id"] = device["id"]?.DeepClone();
        var scoped = await Execute("/api/stop/coordinated", emptyScope.ToJsonString());
        var scopeResult = JsonNode.Parse(scoped.Json);
        Check(scoped.Status == 409 && scopeResult?["arm"]?["ok"]?.GetValue<bool>() == true &&
            scopeResult?["pika"]?["ok"]?.GetValue<bool>() == false &&
            scopeResult?["pika"]?["message"]?.GetValue<string>().Contains("it names an arm but no pika_device. Every Pika was asked to hold instead.", StringComparison.Ordinal) == true,
            "An emergency STOP that names no gripper blocked the arm STOP or did not hold everything: " + scoped.Json);
        emptyScope["request_id"] = 1;
        var cancel = JsonNode.Parse((await Execute("/api/stop/coordinated", emptyScope.ToJsonString())).Json);
        Check(cancel?["pika"]?["message"]?.GetValue<string>().Contains("explicit pika_device", StringComparison.Ordinal) == true,
            "A cancel that names no gripper held an unrelated Pika: " + cancel);
        Console.WriteLine("Host dispatch passed: common snapshots, simulated Pika rejection, legacy HTTP routes, gRPC route rejection and independent STOP results.");

        async Task<(HttpStatusCode Status, JsonObject Body)> Post(string route, string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Piper-Token", token);
            using var response = await http.SendAsync(request);
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
        }

        async Task<ControlReply> Execute(string route, string json) => await grpc.ExecuteAsync(new ControlRequest { Route = route, Json = json },
            headers, deadline: DateTime.UtcNow.AddSeconds(3));

        Task UnsupportedGrpc(string route, string json) => RejectAsync<RpcException>(() => Execute(route, json),
            "gRPC accepted HTTP-only or unsupported route " + route,
            error => error.StatusCode == StatusCode.InvalidArgument && error.Status.Detail == "Unsupported control route.");
    }
}
