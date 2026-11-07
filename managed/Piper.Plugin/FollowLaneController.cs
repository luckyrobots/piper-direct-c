using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

internal enum FollowArmAction { MatchPose, Start, Update, Hold }
internal enum FollowPikaAction { MatchOpening, Start, Update, Hold }

/// <summary>A lane's command identity is fixed for the whole follow session.</summary>
internal sealed class FollowLaneController(string backend, string owner, RobotProfile profile, long generation,
    Func<string, JsonObject, CancellationToken, string?, Task<JsonObject>> send)
{
    public Task<JsonObject> Arm(FollowArmAction action, JsonObject? fields)
    {
        var body = fields ?? new();
        body["action"] = action switch
        {
            FollowArmAction.MatchPose => "pose",
            FollowArmAction.Start => "follow-start",
            FollowArmAction.Update => "follow-update",
            FollowArmAction.Hold => "hold",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        body["owner"] = owner;
        body["device_id"] = profile.CanDeviceId;
        body["generation"] = generation;
        body["standalone"] = true;
        body["supported"] = true;
        body["clear"] = true;
        body["reset_completed"] = true;
        body["speed_percent"] = action == FollowArmAction.MatchPose ? 10 : profile.Speed;
        body["tolerance_deg"] = profile.Tolerance;
        return send("/api/command", body, CancellationToken.None, backend);
    }

    public Task<JsonObject> Pika(FollowPikaAction action, JsonObject? fields)
    {
        var body = fields ?? new();
        body["owner"] = owner;
        body["pika_device"] = profile.PikaDevice;
        body["pika_distribution"] = profile.WslDistribution;
        string route = action switch
        {
            FollowPikaAction.MatchOpening => "/api/pika/position",
            FollowPikaAction.Start => "/api/pika/follow-start",
            FollowPikaAction.Update => "/api/pika/follow-update",
            FollowPikaAction.Hold => "/api/pika/hold",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return send(route, body, CancellationToken.None, backend);
    }

    public Task<JsonObject> Heartbeat(long requestId, CancellationToken cancellation) => send("/api/heartbeat",
        new() { ["owner"] = owner, ["request_id"] = requestId, ["device_id"] = profile.CanDeviceId,
            ["generation"] = generation }, cancellation, backend);
}
