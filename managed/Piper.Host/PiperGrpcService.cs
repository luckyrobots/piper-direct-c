using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Host;

public sealed class PiperRpcContext(ControllerService controller, PikaProcessService pika, bool simulated, string sessionOwner = "")
{
    private long sequence;
    public ControllerService Controller { get; } = controller;
    public PikaProcessService Pika { get; } = pika;
    public bool Simulated { get; } = simulated;
    public string SessionOwner { get; } = sessionOwner;
    public StateFrame Snapshot()
    {
        var state = JsonNode.Parse(Controller.Snapshot())!.AsObject();
        state["pika"] = Pika.Snapshot();
        state["backend_process_id"] = Environment.ProcessId;
        return new StateFrame
        {
            Json = state.ToJsonString(), Sequence = Interlocked.Increment(ref sequence),
            TimestampUnixMicroseconds = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10,
            ClockDomain = "host-utc"
        };
    }
}

public sealed class PiperGrpcService(PiperRpcContext state) : PiperControl.PiperControlBase
{
    public override Task<StateFrame> GetState(StateRequest request, ServerCallContext context) => Task.FromResult(state.Snapshot());

    public override async Task WatchState(StateRequest request, IServerStreamWriter<StateFrame> stream, ServerCallContext context)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        do { await stream.WriteAsync(state.Snapshot(), context.CancellationToken); }
        while (await timer.WaitForNextTickAsync(context.CancellationToken));
    }

    public override async Task<ControlReply> Execute(ControlRequest request, ServerCallContext context)
    {
        if (context.RequestHeaders.GetValue("x-piper-token") != state.Controller.Token)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Read the current server session before sending commands."));
        if (Encoding.UTF8.GetByteCount(request.Json) > 16384 || request.Json.Contains("\\u", StringComparison.Ordinal) || request.Json.Contains('\0'))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid control request encoding or size."));
        try
        {
            using var json = JsonDocument.Parse(request.Json, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !Unique(json.RootElement))
                throw new JsonException("Expected an object with unique keys.");
            if (!SessionOwnerGuard.Allows(state.SessionOwner, request.Route, json.RootElement))
                return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = SessionOwnerGuard.Denied }) };
            if (request.Route.StartsWith("/api/pika/", StringComparison.Ordinal))
            {
                if (state.Simulated) return new ControlReply { Status = 409, Json = "{\"error\":\"Pika hardware is disabled in simulation\"}" };
                if (request.Route == "/api/pika/configure" && JsonNode.Parse(state.Controller.Snapshot())?["motion"]?["active"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException("Finish the active arm movement before changing Pika assignment");
                var result = await state.Pika.ExecuteAsync(request.Route[10..], json.RootElement, context.CancellationToken);
                return new ControlReply { Status = 200, Json = result.ToJsonString() };
            }
            if (request.Route is not ("/api/command" or "/api/select" or "/api/stop" or "/api/heartbeat" or "/api/query" or "/api/reconnect"))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Unsupported control route."));
            var reply = state.Controller.Execute(request.Route, request.Json);
            return new ControlReply { Status = reply.Status, Json = reply.Json };
        }
        catch (JsonException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or ArgumentException)
        {
            return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = error.Message }) };
        }
    }

    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
                if (!names.Add(member.Name) || !Unique(member.Value)) return false;
        }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!Unique(child)) return false;
        return true;
    }
}
