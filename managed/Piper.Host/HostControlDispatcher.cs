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

    /// <summary>A null distribution means the registry's current default; the
    /// registry itself treats null as native Linux.</summary>
    internal async Task<HostControlResult> DiscoverPikaAsync(string? distribution, CancellationToken cancellation)
    {
        if (simulated) return new(200, EmptySimulatedInventory);
        try { return new(200, await pika.DiscoverAsync(distribution ?? pika.DefaultAssignment.Distribution, cancellation)); }
        catch (Exception error) when (IsControlOperationError(error)) { return new(409, Error: error.Message); }
    }

    /// <summary>gRPC never offers shutdown: <see cref="HostRoutes.IsGrpcControl"/>
    /// rejects it before admission.</summary>
    internal HostControlAdmission Admit(string route, JsonElement input)
    {
        if (!SessionOwnerGuard.Allows(sessionOwner, route, input)) return Denied(SessionOwnerGuard.Denied);
        try
        {
            if (route == HostRoutes.Restart)
            {
                var restart = maintenance.Prepare(input, out var commit);
                commit?.Invoke();
                return new(Reply: new HostControlResult(restart.Ok ? 202 : 409, restart));
            }
            if (route == HostRoutes.Shutdown)
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
        if (route == HostRoutes.CoordinatedStop)
        {
            var stopped = await CoordinatedStopAsync(input);
            return new(stopped.Ok ? 200 : 409, stopped);
        }
        if (!HostRoutes.IsPika(route))
        {
            var native = controller.Execute(route, body, cancellation);
            return new(native.Status, RawJson: native.Json);
        }

        if (simulated)
            return route == HostRoutes.PikaDevices ? new(200, EmptySimulatedInventory) : new(409, Error: "Pika hardware is disabled in simulation");
        if (route == HostRoutes.PikaConfigure)
        {
            const string activeMessage = "Finish active arm movements before changing Pika assignment";
            if (ControllerActivity.AnyMotion(controller.SnapshotJson()))
                return new(409, Error: activeMessage);
            return new(200, await pika.ConfigureAsync(input, cancellation,
                commit => controller.CommitWhenArmIdle(commit, activeMessage, cancellation)));
        }
        return new(200, await pika.ExecuteAsync(HostRoutes.PikaAction(route), input, cancellation));
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
                var arm = controller.Execute(HostRoutes.Stop, body);
                return Task.FromResult(new StopChannelResult(arm.Status is >= 200 and < 300, arm.Json));
            },
            async cancellation =>
            {
                // The gripper leg mirrors the arm leg. A cancel (an arm with a
                // request_id) is a client's cleanup of its own action: it holds
                // only the gripper it names. Everything else is an emergency and
                // is never refused without holding: it holds the gripper it names,
                // or every gripper, whoever owns it and whoever asks, when it names
                // none. When its scope cannot be honoured it says so.
                bool hasArm = command.TryGetProperty("device_id", out var arm);
                bool scoped = hasArm && arm.ValueKind == JsonValueKind.String && arm.GetString()!.Length > 0;
                bool hasRequest = command.TryGetProperty("request_id", out _);
                bool namesGripper = command.TryGetProperty("pika_device", out var gripper) &&
                    gripper.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(gripper.GetString());
                if (scoped && hasRequest && !namesGripper)
                    return new(false, "A scoped STOP requires an explicit pika_device to verify the gripper hold.");
                string? unscopable =
                    hasArm && arm.ValueKind != JsonValueKind.String ? "device_id is not an adapter identity" :
                    !scoped && (hasRequest || command.TryGetProperty("generation", out _)) ? "generation or request_id came without device_id" :
                    scoped && !namesGripper ? "it names an arm but no pika_device" : null;
                var result = scoped && namesGripper ? await pika.ExecuteAsync("hold", command, cancellation) :
                    await pika.HoldAllAsync(cancellation);
                bool held = result["ok"]?.GetValue<bool>() == true;
                string message = result["message"]?.GetValue<string>() ?? "Pika hold command acknowledged.";
                return unscopable is null ? new(held, message) :
                    new(false, $"This hold could not be scoped as asked: {unscopable}. Every Pika was asked to hold instead. {message}");
            });
    }

    private static bool IsControlOperationError(Exception error) =>
        error is InvalidOperationException or IOException or TimeoutException or ArgumentException;
}
