using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal sealed class FollowFakeCalls : CallInvoker
{
    public ConcurrentQueue<(string Route, JsonObject Body)> Requests { get; } = new();
    public ConcurrentQueue<string> Events { get; } = new();
    public string DelayedOperation = "";
    public string FailedPikaHeartbeatDevice = "";
    public bool LostAcknowledgement;
    public string? TransportAcknowledgementOverride;
    public bool HoldFailure;
    public bool RejectIdleHold;
    public Func<bool>? ArmActive;
    public bool StreamUnavailable;
    public bool RejectHeartbeatAfterAccepted;
    public string UpdateFailure = "";
    public PikaFollowAudit? PikaFailureAudit;
    public bool FailArmWithPikaAudit;
    public CancellationToken AdmissionCancellation;
    public DateTime? AdmissionDeadline;
    public TaskCompletionSource DelayedReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDelayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<string, JsonObject>? Accepted;
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
    {
        var command = request as ControlRequest ?? throw new InvalidOperationException("Unexpected fixture RPC");
        var body = JsonNode.Parse(command.Json)!.AsObject(); Requests.Enqueue((command.Route, body));
        string action = body["action"]?.GetValue<string>() ?? "";
        if (command.Route == "/api/command" && action is "hold" or "follow-update" && body["standalone"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Missing standalone declaration in raw arm control");
        string operation = command.Route switch
        {
            "/api/pika/follow-start" => "pika-start",
            "/api/pika/position" => "pika-position",
            "/api/heartbeat" => "heartbeat",
            _ when action == "follow-start" => "arm-start",
            _ => action
        };
        Events.Enqueue("send:" + (command.Route == "/api/pika/hold" ? "pika-hold" : operation));
        async Task<TResponse> Reply()
        {
            if (RejectIdleHold && action == "hold" && ArmActive?.Invoke() != true)
                return (TResponse)(object)new ControlReply { Status = 400, Json = "{\"error\":\"Confirm that the full motion path is clear and the arm is attended.\"}" };
            if (HoldFailure && action == "hold")
                return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"hold transport failure\"}" };
            if (UpdateFailure.Length > 0 && action == "follow-update")
                return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = UpdateFailure }) };
            if (PikaFailureAudit is { } audit && command.Route == "/api/pika/follow-update")
                return (TResponse)(object)new ControlReply { Status = 409, Json = JsonSerializer.Serialize(new { error = "No matching active Pika following session.", pika_follow_audit = audit }) };
            if (FailArmWithPikaAudit && PikaFailureAudit is not null && action == "follow-update")
                return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"Arm following input expired.\"}" };
            bool failedPika = FailedPikaHeartbeatDevice.Length > 0 && command.Route == "/api/pika/heartbeat" &&
                body["pika_device"]?.GetValue<string>() == FailedPikaHeartbeatDevice;
            if (failedPika || DelayedOperation.Length > 0 && operation == DelayedOperation)
            {
                AdmissionCancellation = options.CancellationToken; AdmissionDeadline = options.Deadline;
                DelayedReached.TrySetResult(); await ReleaseDelayed.Task;
                Events.Enqueue("accepted:" + operation);
            }
            if (failedPika)
                return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"Pika fixture heartbeat failed\"}" };
            Accepted?.Invoke(command.Route, body);
            if (RejectHeartbeatAfterAccepted && command.Route == "/api/heartbeat")
                return (TResponse)(object)new ControlReply { Status = 409, Json = "{\"error\":\"No matching active motion lease\"}" };
            if (LostAcknowledgement && operation == DelayedOperation) throw new RpcException(new Status(StatusCode.Cancelled, "Accepted fixture operation lost its acknowledgement"));
            var response = new JsonObject { ["request_id"] = 77, ["motor_request_id"] = "pika-position-fixture", ["follow_session"] = "pika-fixture", ["trajectory_follow"] = true };
            if (action == "follow-start" && (body["trajectory_transport"] is not null || TransportAcknowledgementOverride is not null))
            {
                response["trajectory_transport"] = TransportAcknowledgementOverride ?? body["trajectory_transport"]!.GetValue<string>();
                response["trajectory_transport_validated"] = true;
            }
            return (TResponse)(object)new ControlReply { Status = 202, Json = response.ToJsonString() };
        }
        return new(Reply(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
        throw (StreamUnavailable ? new RpcException(new Status(StatusCode.Unavailable, "fixture transient stream")) : new NotSupportedException());
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
}
