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
    internal HostControlDispatcher Control { get; } = new(controller, pika, simulated, sessionOwner, maintenance);
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    public StateFrame Snapshot()
    {
        // Source ages in the JSON are measured during sampling. Timestamp the
        // start so composition and serialization never make them younger.
        long captured = (Clock.GetUtcNow().Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
        var state = Control.ReadState();
        return new StateFrame
        {
            Json = state.ToJsonString(), Sequence = Interlocked.Increment(ref sequence),
            TimestampUnixMicroseconds = captured,
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
            using var admission = state.Control.Admit(request.Route, json.RootElement, HostControlTransport.Grpc);
            if (admission.Error is { } denied)
                return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = denied }) };
            if (admission.Restart is { } restarting)
                return new ControlReply { Status = restarting.Ok ? 202 : 409, Json = JsonSerializer.Serialize(restarting, WebJson) };
            if (!HostRoutes.IsGrpcControl(request.Route))
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
    }

}
