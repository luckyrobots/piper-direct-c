using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One Follow operation, sent to the arm and/or its Pika. Match moves to the
/// first-frame pose or opening; Start and Update stream targets; Hold retires them.</summary>
internal enum FollowOp { Match, Start, Update, Hold }

/// <summary>A lane's command identity is fixed for the whole follow session.</summary>
internal sealed class FollowLaneController(string backend, string owner, RobotProfile profile, long generation,
    Func<string, JsonObject, CancellationToken, string?, Task<JsonObject>> send)
{
    public Task<JsonObject> Arm(FollowOp action, JsonObject? fields)
    {
        var body = fields ?? new();
        body["action"] = action switch
        {
            FollowOp.Match => "pose",
            FollowOp.Start => "follow-start",
            FollowOp.Update => "follow-update",
            FollowOp.Hold => "hold",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        body["owner"] = owner;
        body["device_id"] = profile.CanDeviceId;
        body["generation"] = generation;
        // The controller reads "standalone" on every admitted /api/command and
        // "clear" only when it admits new motion. Follow never changes motor
        // power, so "supported" and "reset_completed" are never sent.
        body["standalone"] = true;
        if (action is FollowOp.Match or FollowOp.Start) body["clear"] = true;
        body["speed_percent"] = action == FollowOp.Match ? 10 : profile.Speed;
        body["tolerance_deg"] = profile.Tolerance;
        return send("/api/command", body, CancellationToken.None, backend);
    }

    public Task<JsonObject> Pika(FollowOp action, JsonObject? fields)
    {
        var body = fields ?? new();
        body["owner"] = owner;
        body["pika_device"] = profile.PikaDevice;
        body["pika_distribution"] = profile.PikaDistribution;
        string route = action switch
        {
            FollowOp.Match => "/api/pika/position",
            FollowOp.Start => "/api/pika/follow-start",
            FollowOp.Update => "/api/pika/follow-update",
            FollowOp.Hold => "/api/pika/hold",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return send(route, body, CancellationToken.None, backend);
    }

    public Task<JsonObject> Heartbeat(long requestId, CancellationToken cancellation) => send("/api/heartbeat",
        new() { ["owner"] = owner, ["request_id"] = requestId, ["device_id"] = profile.CanDeviceId,
            ["generation"] = generation }, cancellation, backend);
}
