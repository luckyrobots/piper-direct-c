using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using Microsoft.Extensions.Hosting;

namespace LuckyRobots.Piper.Host;

public sealed class PiperRpcContext(ControllerService controller, PikaSessionRegistry pika, bool simulated, string sessionOwner, BackendMaintenance maintenance)
{
    private long sequence;
    public ControllerService Controller { get; } = controller;
    public PikaSessionRegistry Pika { get; } = pika;
    public bool Simulated { get; } = simulated;
    public string SessionOwner { get; } = sessionOwner;
    public BackendMaintenance Maintenance { get; } = maintenance;
    internal HostControlDispatcher Control { get; } = new(controller, pika, simulated, sessionOwner, maintenance);
    public StateFrame Snapshot()
    {
        var state = Control.ReadState();
        return new StateFrame
        {
            Json = state.ToJsonString(), Sequence = Interlocked.Increment(ref sequence),
            TimestampUnixMicroseconds = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10,
            ClockDomain = "host-utc"
        };
    }
}

public sealed class PiperGrpcService(PiperRpcContext state, IHostApplicationLifetime lifetime) : PiperControl.PiperControlBase
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public override Task<StateFrame> GetState(StateRequest request, ServerCallContext context) => Task.FromResult(state.Snapshot());

    public override async Task WatchState(StateRequest request, IServerStreamWriter<StateFrame> stream, ServerCallContext context)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            // A connected observer must not keep Kestrel draining until its
            // shutdown deadline before the owning USB services can close.
            while (!cancellation.IsCancellationRequested)
            {
                await stream.WriteAsync(state.Snapshot(), cancellation.Token);
                if (!await timer.WaitForNextTickAsync(cancellation.Token)) break;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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
            if (json.RootElement.ValueKind != JsonValueKind.Object || !ControlJson.HasUniqueKeys(json.RootElement))
                throw new JsonException("Expected an object with unique keys.");
            using var admission = state.Control.Admit(request.Route, json.RootElement);
            if (admission.Error is { } denied)
                return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = denied }) };
            if (admission.Restart is { } restarting)
                return new ControlReply { Status = restarting.Ok ? 202 : 409, Json = JsonSerializer.Serialize(restarting, WebJson) };
            if (request.Route is not ("/api/command" or "/api/select" or "/api/stop" or "/api/heartbeat" or "/api/query" or "/api/reconnect" or CoordinatedStopRequest.Route) &&
                !request.Route.StartsWith("/api/pika/", StringComparison.Ordinal))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Unsupported control route."));
            var result = await state.Control.ExecuteAsync(request.Route, request.Json, json.RootElement,
                HostControlTransport.Grpc, context.CancellationToken);
            string reply;
            if (result.PikaAudit is { } audit) reply = JsonSerializer.Serialize(new { error = result.Error, pika_follow_audit = audit });
            else if (result.Error is { } error) reply = JsonSerializer.Serialize(new { error });
            else reply = result.RawJson ?? (result.Payload is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(result.Payload, WebJson));
            return new ControlReply { Status = result.Status, Json = reply };
        }
        catch (JsonException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
        catch (Exception error) when (HostControlDispatcher.IsControlOperationError(error))
        {
            return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = error.Message }) };
        }
    }

}
