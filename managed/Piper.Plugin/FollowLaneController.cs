using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>One Follow operation, sent to the arm and/or its Pika. Match moves to the
/// first-frame pose or opening; Start and Update stream targets; Hold retires them.</summary>
internal enum FollowOp { Match, Start, Update, Hold }

/// <summary>The hardware a Follow command addresses.</summary>
internal enum FollowChannel { Arm, Pika }

/// <summary>One Follow command of a lane, ready to send.</summary>
internal readonly record struct FollowCommand(FollowChannel Channel, FollowOp Op, ApiRequest Request);

/// <summary>A lane's command identity is fixed for the whole follow session. Every Follow
/// request of the lane is built here, from the generated request types.</summary>
internal sealed class FollowLaneController(string backend, string owner, RobotProfile profile, long generation,
    Func<ApiRequest, CancellationToken, string?, Task<JsonObject>> send)
{
    public FollowCommand MatchArm(IReadOnlyList<double> degrees) => Arm(FollowOp.Match, "pose", degrees: degrees.ToArray(), synchronized: true);

    // The transport is not named: backends predating the transport field default to position-velocity.
    public FollowCommand StartArm() => Arm(FollowOp.Start, "follow-start", trajectory: true);

    public FollowCommand UpdateArm(long requestId, long sequence, IReadOnlyList<double> degrees) =>
        Arm(FollowOp.Update, "follow-update", degrees: degrees.ToArray(), requestId: requestId, sequence: sequence);

    /// <param name="requestId">The follow request to hold, or null to hold whatever the arm is doing.</param>
    public FollowCommand HoldArm(long? requestId) => Arm(FollowOp.Hold, "hold", requestId: requestId);

    public FollowCommand MatchPika(double angle) => new(FollowChannel.Pika, FollowOp.Match,
        PiperApi.Requests.PikaPosition(owner, angle, profile.PikaDevice, profile.PikaDistribution));

    public FollowCommand StartPika() => new(FollowChannel.Pika, FollowOp.Start,
        PiperApi.Requests.PikaFollowStart(owner, profile.PikaDevice, profile.PikaDistribution));

    public FollowCommand UpdatePika(string followSession, long sequence, double openingMm) => new(FollowChannel.Pika, FollowOp.Update,
        PiperApi.Requests.PikaFollowUpdate(owner, followSession, sequence, openingMm, profile.PikaDevice, profile.PikaDistribution));

    public FollowCommand HoldPika() => new(FollowChannel.Pika, FollowOp.Hold,
        PiperApi.Requests.PikaHold(owner, profile.PikaDevice, profile.PikaDistribution));

    public Task<JsonObject> Send(FollowCommand command) => send(command.Request, CancellationToken.None, backend);

    public Task<JsonObject> Heartbeat(long requestId, CancellationToken cancellation) =>
        send(PiperApi.Requests.Heartbeat(owner, requestId, profile.CanDeviceId, generation), cancellation, backend);

    private FollowCommand Arm(FollowOp op, string action, ApiNumbers? degrees = null, bool? synchronized = null,
        long? requestId = null, long? sequence = null, bool? trajectory = null) => new(FollowChannel.Arm, op,
        PiperApi.Requests.Command(profile.CanDeviceId, generation, action, owner,
            // The controller reads "standalone" on every admitted /api/command and
            // "clear" only when it admits new motion. Follow never changes motor
            // power, so "supported" and "reset_completed" are never sent.
            standalone: true, clear: op is FollowOp.Match or FollowOp.Start ? true : null,
            degrees: degrees, synchronized: synchronized, requestId: requestId, sequence: sequence, trajectory: trajectory,
            speedPercent: op == FollowOp.Match ? 10 : profile.Speed, toleranceDeg: profile.Tolerance));
}
