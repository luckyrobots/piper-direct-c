using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Host;

internal enum HostControlTransport { Http, Grpc }

internal readonly record struct HostControlAdmission(IDisposable? Lease = null, string? Error = null,
    BackendRestartResult? Restart = null, bool Shutdown = false) : IDisposable
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
        var state = JsonNode.Parse(controller.Snapshot())!.AsObject();
        if (dashboardBackend is not null) state["backend"] = dashboardBackend;
        state["pika"] = pika.Snapshot();
        state["pikas"] = pika.Snapshots();
        state["backend_process_id"] = Environment.ProcessId;
        state["backend_instance_id"] = maintenance.InstanceId;
        state["backend_restart"] = maintenance.Snapshot();
        return state;
    }

    internal HostControlAdmission Admit(string route, JsonElement input)
    {
        if (!SessionOwnerGuard.Allows(sessionOwner, route, input)) return new(Error: SessionOwnerGuard.Denied);
        if (route == BackendMaintenance.RestartRoute)
        {
            var restart = maintenance.Prepare(input);
            if (restart.Ok) maintenance.Commit();
            return new(Restart: restart);
        }
        if (route == "/api/shutdown")
            return maintenance.PrepareShutdown() is { } error ? new(Error: error) : new(Shutdown: true);
        return maintenance.TryEnterControl(route, out var lease, out var denied)
            ? new(Lease: lease) : new(Error: denied);
    }

    internal async Task<HostControlResult> ExecuteAsync(string route, string body, JsonElement input,
        HostControlTransport transport, CancellationToken cancellation)
    {
        if (route == CoordinatedStopRequest.Route)
        {
            var stopped = await CoordinatedStopRequest.ExecuteAsync(controller, pika, input);
            return new(stopped.Ok ? 200 : 409, stopped);
        }
        if (!route.StartsWith("/api/pika/", StringComparison.Ordinal))
        {
            var native = controller.Execute(route, body, cancellation);
            return new(native.Status, RawJson: native.Json);
        }

        bool grpc = transport == HostControlTransport.Grpc;
        if (simulated)
        {
            if (grpc && route == "/api/pika/devices") return new(200, EmptySimulatedInventory);
            return new(409, Error: "Pika hardware is disabled in simulation");
        }
        if (route == "/api/pika/configure" && ControllerActivity.AnyMotion(JsonNode.Parse(controller.Snapshot())))
            return new(409, Error: grpc ? "Finish the active arm movement before changing Pika assignment" :
                "Finish active arm movements before changing Pika assignment");
        try { return new(200, await pika.ExecuteAsync(route[10..], input, cancellation)); }
        catch (PikaFollowRejectedException error) { return new(409, Error: error.Message, PikaAudit: error.Audit); }
        catch (Exception error) when (IsControlOperationError(error)) { return new(409, Error: error.Message); }
    }

    internal static bool IsControlOperationError(Exception error) =>
        error is InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
