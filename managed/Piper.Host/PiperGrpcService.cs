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
    public StateFrame Snapshot()
    {
        var state = JsonNode.Parse(Controller.Snapshot())!.AsObject();
        state["pika"] = Pika.Snapshot();
        state["pikas"] = Pika.Snapshots();
        state["backend_process_id"] = Environment.ProcessId;
        state["backend_instance_id"] = Maintenance.InstanceId;
        state["backend_restart"] = Maintenance.Snapshot();
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
            if (!SessionOwnerGuard.Allows(state.SessionOwner, request.Route, json.RootElement))
                return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = SessionOwnerGuard.Denied }) };
            if (request.Route == BackendMaintenance.RestartRoute)
            {
                var restarting = state.Maintenance.Prepare(json.RootElement);
                if (restarting.Ok) state.Maintenance.Commit();
                return new ControlReply { Status = restarting.Ok ? 202 : 409,
                    Json = JsonSerializer.Serialize(restarting, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
            if (!state.Maintenance.TryEnterControl(request.Route, out var controlAdmission))
                return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = BackendMaintenance.PendingMessage }) };
            using var admission = controlAdmission;
            if (request.Route == CoordinatedStopRequest.Route)
            {
                var stopped = await CoordinatedStopRequest.ExecuteAsync(state.Controller, state.Pika, json.RootElement);
                return new ControlReply { Status = stopped.Ok ? 200 : 409, Json = JsonSerializer.Serialize(stopped, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
            if (request.Route.StartsWith("/api/pika/", StringComparison.Ordinal))
            {
                if (request.Route == "/api/pika/devices" && state.Simulated)
                    return new ControlReply { Status = 200, Json = "{\"ok\":true,\"devices\":[],\"message\":\"Pika hardware discovery is disabled in simulation.\"}" };
                if (state.Simulated) return new ControlReply { Status = 409, Json = "{\"error\":\"Pika hardware is disabled in simulation\"}" };
                if (request.Route == "/api/pika/configure" && ControllerActivity.AnyMotion(JsonNode.Parse(state.Controller.Snapshot())))
                    throw new InvalidOperationException("Finish the active arm movement before changing Pika assignment");
                var result = await state.Pika.ExecuteAsync(request.Route[10..], json.RootElement, context.CancellationToken);
                return new ControlReply { Status = 200, Json = result.ToJsonString() };
            }
            if (request.Route is not ("/api/command" or "/api/select" or "/api/stop" or "/api/heartbeat" or "/api/query" or "/api/reconnect"))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Unsupported control route."));
            var reply = state.Controller.Execute(request.Route, request.Json, context.CancellationToken);
            return new ControlReply { Status = reply.Status, Json = reply.Json };
        }
        catch (JsonException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
        catch (PikaFollowRejectedException error)
        { return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = error.Message, pika_follow_audit = error.Audit }) }; }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException or ArgumentException)
        {
            return new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = error.Message }) };
        }
    }

}
