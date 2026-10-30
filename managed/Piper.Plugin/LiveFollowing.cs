using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private readonly FollowInputGate followInput = new();
    private volatile bool teleoperating;
    private long followRequest;
    private string teleoperationJoint = "joint6";
    private string followJoint = "joint6";

    private async Task SelectTeleoperationJoint(string joint, CancellationToken cancellation)
    {
        var linked = link ?? throw new InvalidOperationException("Link the robot first.");
        if (!approved || !linkCommandsEnabled)
            throw new InvalidOperationException("Enable robot before starting Teleoperate.");
        var device = Device();
        if (TeachingState.Inspect(device).BlocksTargets)
            throw new InvalidOperationException("Hand guiding or onboard playback owns the arm. Finish the teaching return flow before starting Teleoperate.");
        if (device["recovery_required"]?.GetValue<bool>() == true ||
            !device["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true))
            throw new InvalidOperationException("All six motors must be enabled and recovery clear.");
        if (state?["motion"]?["active"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Finish the current movement first.");
        await engine.RequestAsync("robot.teleoperate", new JsonObject {
            ["sceneId"] = linked.SceneId, ["entityId"] = linked.EntityId,
            ["enabled"] = true, ["joint"] = joint }, cancellation);
        teleoperationJoint = joint; teleoperating = true;
        message = "Teleoperate · drag the blue ring · release to finish the target · Esc or Stop movement to hold";
    }

    private async Task HoldFollowing(CancellationToken cancellation)
    {
        long request = followRequest;
        followRequest = 0; followInput.End();
        if (request == 0 || link is not { } linked) return;
        if (state?["motion"]?["request_id"]?.GetValue<long>() == request && state["motion"]?["active"]?.GetValue<bool>() == false) return;
        await Call("/api/command", new JsonObject {
            ["action"] = "follow-end", ["request_id"] = request, ["owner"] = owner,
            ["device_id"] = linked.DeviceId, ["generation"] = linked.Generation }, cancellation);
    }

    private async Task PauseTeleoperation(CancellationToken cancellation)
    {
        teleoperating = false;
        try { await HoldFollowing(cancellation); }
        finally
        {
            if (link is { } linked)
                await engine.RequestAsync("robot.teleoperate", new JsonObject {
                    ["sceneId"] = linked.SceneId, ["entityId"] = linked.EntityId, ["enabled"] = false }, cancellation);
        }
    }

    private async Task FollowViewport(JsonObject input, CancellationToken cancellation)
    {
        try
        {
            if (!teleoperating || !approved || !linkCommandsEnabled || link is not { } linked)
                throw new InvalidOperationException("Start Teleoperate before dragging a live joint.");
            if (input["sceneId"]?.GetValue<string>() != linked.SceneId || input["entityId"]?.GetValue<string>() != linked.EntityId)
                throw new InvalidOperationException("Gesture belongs to another robot.");
            string phase = input["phase"]!.GetValue<string>();
            if (phase == "begin" && followRequest != 0)
                await HoldFollowing(cancellation);
            followInput.Accept(input, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (phase == "end")
            {
                await HoldFollowing(cancellation);
                message = "Movement stopped · holding measured pose";
                return;
            }
            double[] target = linked.Target(input, Device());
            if (phase == "begin")
            {
                followJoint = input["editedJoint"]!.GetValue<string>();
                await Command("follow-start", null, cancellation);
                followRequest = lease;
            }
            if (followRequest == 0) throw new InvalidOperationException("Following expired. Start a new drag.");
            await Call("/api/command", new JsonObject {
                ["action"] = phase == "commit" ? "follow-commit" : "follow-update", ["request_id"] = followRequest, ["owner"] = owner,
                ["device_id"] = linked.DeviceId, ["generation"] = linked.Generation,
                ["sequence"] = input["sequence"]!.DeepClone(),
                ["degrees"] = JsonSerializer.SerializeToNode(target) }, cancellation);
            if (phase == "commit") message = "Movement in progress to released target";
        }
        catch
        {
            try { await PauseTeleoperation(cancellation); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The independent 250 ms controller input deadline still holds
                // measured pose if the transport cannot carry this release.
                System.Diagnostics.Trace.TraceWarning("Following release: {0}", error.Message);
            }
            throw;
        }
    }
}
