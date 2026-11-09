using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-C viewport joint drag.
/// <code>
///   Off --select joint--> Teleoperating(joint) --gesture begin + follow-start ack--> Following(request)
///   Following --gesture end, hold, commit release, ownership change--> Teleoperating
///   any --pause, permission or link loss, profile switch, recovery--> Off
/// </code>
/// A follow request therefore always belongs to an active teleoperation.</summary>
internal enum ViewportDragPhase { Off, Teleoperating, Following }
internal sealed record ViewportDrag(ViewportDragPhase Phase, string Joint = "joint6", long Request = 0, string FollowJoint = "joint6")
{
    public static readonly ViewportDrag Off = new(ViewportDragPhase.Off);
}

public sealed partial class PipersPlugin
{
    private readonly FollowInputGate followInput = new();
    // One state field, written only by the transitions below; readers use the projections.
    private ViewportDrag viewportDrag = ViewportDrag.Off;
    private ViewportDrag Drag => Volatile.Read(ref viewportDrag);
    private bool teleoperating => Drag.Phase != ViewportDragPhase.Off;
    private long followRequest => Drag is { Phase: ViewportDragPhase.Following } drag ? drag.Request : 0;
    private string teleoperationJoint => Drag.Joint;
    private string followJoint => Drag.FollowJoint;

    /// <summary>Leaves teleoperation entirely and discards any gesture.</summary>
    private void StopViewportDrag()
    {
        Volatile.Write(ref viewportDrag, ViewportDrag.Off with { Joint = Drag.Joint });
        followInput.End();
    }

    /// <summary>Ends the current gesture; teleoperation, if active, stays selected.</summary>
    private void EndViewportGesture()
    {
        var current = Drag;
        if (current.Phase == ViewportDragPhase.Following) Volatile.Write(ref viewportDrag, current with { Phase = ViewportDragPhase.Teleoperating, Request = 0 });
        followInput.End();
    }

    private async Task SelectTeleoperationJoint(string joint, CancellationToken cancellation)
    {
        var linked = link ?? throw new InvalidOperationException("Link the robot first.");
        if (!linked.JointNames.Contains(joint, StringComparer.Ordinal))
            throw new InvalidOperationException("Select a joint belonging to this robot's assigned model namespace.");
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
        Volatile.Write(ref viewportDrag, new(ViewportDragPhase.Teleoperating, joint));
        message = "Teleoperate · drag the blue ring · release to finish the target · Esc or Stop movement to hold";
    }

    private Task HoldFollowing(CancellationToken cancellation)
    {
        long request = followRequest;
        EndViewportGesture();
        return SendFollowEnd(request, cancellation);
    }

    private async Task SendFollowEnd(long request, CancellationToken cancellation)
    {
        string expectedToken = linkBackendToken;
        if (request == 0 || link is not { } linked) return;
        if (state?["motion"]?["request_id"]?.GetValue<long>() == request && state["motion"]?["active"]?.GetValue<bool>() == false) return;
        await Call("/api/command", new JsonObject {
            ["action"] = "follow-end", ["request_id"] = request, ["owner"] = owner,
            ["device_id"] = linked.DeviceId, ["generation"] = linked.Generation }, cancellation, expectedToken);
    }

    private async Task PauseTeleoperation(CancellationToken cancellation)
    {
        long request = followRequest;
        StopViewportDrag();
        try { await SendFollowEnd(request, cancellation); }
        finally
        {
            if (link is { } linked)
                await engine.RequestAsync("robot.teleoperate", new JsonObject {
                    ["sceneId"] = linked.SceneId, ["entityId"] = linked.EntityId, ["enabled"] = false }, cancellation);
        }
    }

    private async Task FollowViewport(JsonObject input, CancellationToken cancellation)
    {
        long permissionEpoch = CommandAuthority.Epoch;
        long sceneEpoch = Interlocked.Read(ref scenePermissionEpoch);
        string expectedToken = linkBackendToken;
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
            if (permissionEpoch != CommandAuthority.Epoch || sceneEpoch != Interlocked.Read(ref scenePermissionEpoch) || !ReferenceEquals(link, linked))
                throw new InvalidOperationException("The previous gesture was discarded after a connection or scene change; start a new drag.");
            double[] target = linked.Target(input, Device(linked.DeviceId, linked.Generation, expectedToken));
            if (phase == "begin")
            {
                string edited = input["editedJoint"]!.GetValue<string>();
                var acknowledgement = await Command("follow-start", null, cancellation);
                long admitted = acknowledgement["request_id"]?.GetValue<long>() ?? 0;
                // Only a live teleoperation can enter Following; a concurrent pause wins.
                if (admitted > 0 && Drag is { Phase: ViewportDragPhase.Teleoperating } idle)
                    Volatile.Write(ref viewportDrag, idle with { Phase = ViewportDragPhase.Following, Request = admitted, FollowJoint = edited });
                else if (admitted > 0)
                {
                    await SendFollowEnd(admitted, cancellation);
                    throw new InvalidOperationException("Teleoperate was paused while the drag started; that drag was ended.");
                }
            }
            if (followRequest == 0) throw new InvalidOperationException("Following expired. Start a new drag.");
            if (permissionEpoch != CommandAuthority.Epoch || sceneEpoch != Interlocked.Read(ref scenePermissionEpoch) || !ReferenceEquals(link, linked))
                throw new InvalidOperationException("The previous gesture is no longer authorized; start a new drag.");
            await Call("/api/command", new JsonObject {
                ["action"] = phase == "commit" ? "follow-commit" : "follow-update", ["request_id"] = followRequest, ["owner"] = owner,
                ["device_id"] = linked.DeviceId, ["generation"] = linked.Generation,
                ["sequence"] = input["sequence"]!.DeepClone(),
                ["degrees"] = JsonSerializer.SerializeToNode(target) }, cancellation, expectedToken);
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
