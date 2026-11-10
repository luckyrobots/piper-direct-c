using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Host;

/// <summary>Exactly one of: a control lease, a finished reply (a denial or a
/// restart decision), or a prepared shutdown whose one-shot commit runs after
/// the response.</summary>
internal readonly record struct HostControlAdmission(IDisposable? Lease = null, HostControlResult? Reply = null,
    Action<Action>? Shutdown = null) : IDisposable
{
    public void Dispose() => Lease?.Dispose();
}

/// <summary>One control reply. Both transports send <see cref="ToJson"/> with
/// <see cref="Status"/>, so HTTP and gRPC bodies are identical.</summary>
internal readonly record struct HostControlResult(int Status, object? Payload = null, string? RawJson = null,
    string? Error = null, PikaFollowAudit? PikaAudit = null)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal string ToJson()
    {
        if (RawJson is { } raw) return raw;
        if (Error is { } error)
        {
            var reply = new JsonObject { ["ok"] = false, ["error"] = error };
            // The plugin deserializes the audit with default (PascalCase) names.
            if (PikaAudit is { } audit) reply["pika_follow_audit"] = JsonSerializer.SerializeToNode(audit);
            return reply.ToJsonString();
        }
        return Payload is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(Payload, WebJson);
    }
}

/// <summary>Application policy shared by the two authenticated, parsed transports.</summary>
internal sealed class HostControlDispatcher(ControllerService controller, PikaSessionRegistry pika, bool simulated,
    string sessionOwner, BackendMaintenance maintenance)
{
    private static object EmptySimulatedInventory { get; } = new
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
        return pika.Snapshot(device, distribution ?? pika.DefaultAssignment.Distribution, includeRawEvidence: true);
    }

    /// <summary>The inventory read (GET). It is the inventory control's own request and
    /// handler, so both verbs resolve the distribution alike: an absent one means the
    /// registry's current assignment. A read acquires nothing and passes no admission.</summary>
    internal Task<HostControlResult> ReadPikaInventoryAsync(string? distribution, CancellationToken cancellation)
    {
        var request = PiperApi.Requests.PikaDevices(
            pikaDistribution: distribution is null ? default : new ApiOptional<string?>(true, distribution));
        using var input = JsonDocument.Parse(request.Json);
        return ExecuteAsync(request.Route.Path, request.Json, input.RootElement.Clone(), cancellation);
    }

    /// <summary>gRPC never offers shutdown: the definition does not mark it for the
    /// tunnel, so that transport rejects it before admission.</summary>
    internal HostControlAdmission Admit(string route, JsonElement input)
    {
        if (!SessionOwnerGuard.Allows(sessionOwner, route, input)) return Denied(SessionOwnerGuard.Denied);
        try
        {
            if (route == PiperApi.Paths.Restart)
            {
                var restart = maintenance.Prepare(input, out var commit);
                commit?.Invoke();
                return new(Reply: new HostControlResult(restart.Ok ? 202 : 409, restart));
            }
            if (route == PiperApi.Paths.Shutdown)
                return maintenance.PrepareShutdown(out var shutdown) is { } error ? Denied(error) : new(Shutdown: shutdown);
        }
        // The exit checks read controller state; both transports reject alike.
        catch (Exception error) when (IsControlOperationError(error)) { return Denied(error.Message); }
        return maintenance.TryEnterControl(route, out var lease, out var denied)
            ? new(Lease: lease) : Denied(denied!);

        static HostControlAdmission Denied(string error) => new(Reply: new HostControlResult(409, Error: error));
    }

    internal async Task<HostControlResult> ExecuteAsync(string route, string body, JsonElement input,
        CancellationToken cancellation)
    {
        // One mapping for both transports: a controller-operation failure is a
        // 409 whether it came from the native core or from a Pika session.
        try { return await DispatchAsync(route, body, input, cancellation); }
        catch (PikaFollowRejectedException error) { return new(409, Error: error.Message, PikaAudit: error.Audit); }
        catch (Exception error) when (IsControlOperationError(error)) { return new(409, Error: error.Message); }
    }

    private async Task<HostControlResult> DispatchAsync(string route, string body, JsonElement input,
        CancellationToken cancellation)
    {
        // The definition is the route table: a path it does not give the managed host
        // reaches neither the core nor a gripper.
        if (PiperApi.ManagedControl(route) is not { } api) return new(404, Error: "Unknown API route.");
        if (api == PiperApi.CoordinatedStop)
        {
            var stopped = await CoordinatedStopAsync(input);
            return new(stopped.Ok ? 200 : 409, stopped);
        }
        if (api.PikaAction is not { } action)
        {
            var native = controller.Execute(api.Path, body, cancellation);
            return new(native.Status, RawJson: native.Json);
        }

        if (simulated)
            return api == PiperApi.PikaDevices ? new(200, EmptySimulatedInventory) : new(409, Error: "Pika hardware is disabled in simulation");
        if (api == PiperApi.PikaConfigure)
        {
            const string activeMessage = "Finish active arm movements before changing Pika assignment";
            if (ControllerActivity.AnyMotion(controller.SnapshotJson()))
                return new(409, Error: activeMessage);
            return new(200, await pika.ConfigureAsync(input, cancellation,
                commit => controller.CommitWhenArmIdle(commit, activeMessage, cancellation)));
        }
        return new(200, await pika.ExecuteAsync(action, input, cancellation));
    }

    private Task<CoordinatedStopResult> CoordinatedStopAsync(JsonElement input)
    {
        // Clone the payload: request cancellation/disposal must not terminate the
        // second stop attempt. A scoped stop keeps its device/generation/owner guards.
        JsonElement command = input.Clone();
        string body = command.GetRawText();
        return CoordinatedStop.RunAsync(
            _ =>
            {
                var arm = controller.Execute(PiperApi.Paths.Stop, body);
                return Task.FromResult(new StopChannelResult(arm.Status is >= 200 and < 300, arm.Json));
            },
            async cancellation =>
            {
                // The gripper leg mirrors the arm leg; CoordinatedStopScope says which
                // grippers this request holds. An emergency is never refused without holding.
                var scope = CoordinatedStopScope.Of(command);
                if (scope.Leg == GripperStopLeg.RefuseCancel) return new(false, CoordinatedStopScope.CancelNeedsGripper);
                var result = scope.Leg == GripperStopLeg.HoldNamed
                    ? await pika.ExecuteAsync(PiperApi.PikaHold.PikaAction!, command, cancellation)
                    : await pika.HoldAllAsync(cancellation);
                bool held = result["ok"]?.GetValue<bool>() == true;
                string message = result["message"]?.GetValue<string>() ?? "Pika hold command acknowledged.";
                return scope.Degraded is null ? new(held, message) :
                    new(false, $"This hold could not be scoped as asked: {scope.Degraded}. Every Pika was asked to hold instead. {message}");
            });
    }

    private static bool IsControlOperationError(Exception error) =>
        error is InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
