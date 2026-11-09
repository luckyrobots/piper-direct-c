using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Host;

internal enum HostControlTransport { Http, Grpc }

/// <summary>Exactly one of: a control lease, a denial, a restart reply, or a
/// prepared shutdown whose one-shot commit runs after the response.</summary>
internal readonly record struct HostControlAdmission(IDisposable? Lease = null, string? Error = null,
    BackendRestartResult? Restart = null, Action<Action>? Shutdown = null) : IDisposable
{
    public void Dispose() => Lease?.Dispose();
}

internal readonly record struct HostControlResult(int Status, object? Payload = null, string? RawJson = null,
    string? Error = null, PikaFollowAudit? PikaAudit = null);

/// <summary>Application policy shared by the two authenticated, parsed transports.</summary>
internal sealed class HostControlDispatcher(ControllerService controller, PikaSessionRegistry pika, bool simulated,
    string sessionOwner, BackendMaintenance maintenance)
{
    internal static object EmptySimulatedInventory { get; } = new
    {
        ok = true, devices = Array.Empty<object>(), message = "Pika hardware discovery is disabled in simulation."
    };

    internal JsonObject ReadState(string? dashboardBackend = null)
    {
        var state = controller.SnapshotJson();
        if (dashboardBackend is not null) state["backend"] = dashboardBackend;
        var grippers = pika.SnapshotPair();
        state["pika"] = grippers.Default;
        state["pikas"] = grippers.Sessions;
        state["backend_process_id"] = Environment.ProcessId;
        state["backend_instance_id"] = maintenance.InstanceId;
        state["backend_restart"] = maintenance.Snapshot();
        return state;
    }

    /// <summary>A null distribution means the registry's current assignment, which
    /// /api/pika/configure may have changed since startup.</summary>
    internal JsonObject PikaState(string? device, string? distribution)
    {
        if (device is null) return pika.Snapshot(includeRawEvidence: true);
        return pika.Snapshot(device, distribution ?? pika.Snapshot()["distribution"]?.GetValue<string>(), includeRawEvidence: true);
    }

    internal Task<JsonObject> DiscoverPikaAsync(string? distribution, CancellationToken cancellation) =>
        pika.DiscoverAsync(distribution, cancellation);

    internal HostControlAdmission Admit(string route, JsonElement input, HostControlTransport transport)
    {
        if (!SessionOwnerGuard.Allows(sessionOwner, route, input)) return new(Error: SessionOwnerGuard.Denied);
        try
        {
            if (route == HostRoutes.Restart)
            {
                var restart = maintenance.Prepare(input, out var commit);
                commit?.Invoke();
                return new(Restart: restart);
            }
            if (transport == HostControlTransport.Http && route == HostRoutes.Shutdown)
                return maintenance.PrepareShutdown(out var shutdown) is { } error ? new(Error: error) : new(Shutdown: shutdown);
        }
        // The exit checks read controller state; both transports reject alike.
        catch (Exception error) when (IsControlOperationError(error)) { return new(Error: error.Message); }
        return maintenance.TryEnterControl(route, out var lease, out var denied)
            ? new(Lease: lease) : new(Error: denied);
    }

    internal async Task<HostControlResult> ExecuteAsync(string route, string body, JsonElement input,
        HostControlTransport transport, CancellationToken cancellation)
    {
        // One mapping for both transports: a controller-operation failure is a
        // 409 whether it came from the native core or from a Pika session.
        try { return await DispatchAsync(route, body, input, transport, cancellation); }
        catch (PikaFollowRejectedException error) { return new(409, Error: error.Message, PikaAudit: error.Audit); }
        catch (Exception error) when (IsControlOperationError(error)) { return new(409, Error: error.Message); }
    }

    private async Task<HostControlResult> DispatchAsync(string route, string body, JsonElement input,
        HostControlTransport transport, CancellationToken cancellation)
    {
        if (route == HostRoutes.CoordinatedStop)
        {
            var stopped = await CoordinatedStopRequest.ExecuteAsync(controller, pika, input);
            return new(stopped.Ok ? 200 : 409, stopped);
        }
        if (!HostRoutes.IsPika(route))
        {
            var native = controller.Execute(route, body, cancellation);
            return new(native.Status, RawJson: native.Json);
        }

        bool grpc = transport == HostControlTransport.Grpc;
        if (simulated)
        {
            if (grpc && route == HostRoutes.PikaDevices) return new(200, EmptySimulatedInventory);
            return new(409, Error: "Pika hardware is disabled in simulation");
        }
        if (route == HostRoutes.PikaConfigure)
        {
            string activeMessage = grpc ? "Finish the active arm movement before changing Pika assignment" :
                "Finish active arm movements before changing Pika assignment";
            if (ControllerActivity.AnyMotion(controller.SnapshotJson()))
                return new(409, Error: activeMessage);
            return new(200, await pika.ConfigureAsync(input, cancellation,
                commit => controller.CommitWhenArmIdle(commit, activeMessage, cancellation)));
        }
        return new(200, await pika.ExecuteAsync(HostRoutes.PikaAction(route), input, cancellation));
    }

    internal static bool IsControlOperationError(Exception error) =>
        error is InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
