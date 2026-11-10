using System.Diagnostics;
using System.Text.Json;
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
        try
        {
            if (!ControlJson.TryParse(request.Json, out var json, out var rejected))
                throw new RpcException(new Status(StatusCode.InvalidArgument, rejected.Message));
            using (json)
            {
                if (PiperApi.ManagedControl(request.Route) is not { Grpc: true })
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Unsupported control route."));
                using var admission = state.Control.Admit(request.Route, json.RootElement);
                Debug.Assert(admission.Shutdown is null);
                var result = admission.Reply ?? await state.Control.ExecuteAsync(request.Route, request.Json, json.RootElement,
                    context.CancellationToken);
                return new ControlReply { Status = result.Status, Json = result.ToJson() };
            }
        }
        catch (JsonException error) { throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message)); }
    }

}
