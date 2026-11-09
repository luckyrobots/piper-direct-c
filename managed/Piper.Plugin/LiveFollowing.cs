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
    // Every transition is a compare-exchange from the state its writer observed,
    // so a STOP on the telemetry thread can never be overwritten by a stale
    // gesture writer: a stale writer's exchange fails and it backs out.
    internal ViewportDrag viewportDrag = ViewportDrag.Off;
    private ViewportDrag Drag => Volatile.Read(ref viewportDrag);
    private bool TryAdvanceDrag(ViewportDrag observed, ViewportDrag next) =>
        ReferenceEquals(Interlocked.CompareExchange(ref viewportDrag, next, observed), observed);
    private bool teleoperating => Drag.Phase != ViewportDragPhase.Off;
    private long followRequest => Drag is { Phase: ViewportDragPhase.Following } drag ? drag.Request : 0;
    private string teleoperationJoint => Drag.Joint;
    private string followJoint => Drag.FollowJoint;

    /// <summary>Leaves teleoperation entirely and discards any gesture.</summary>
    internal void StopViewportDrag()
    {
        ViewportDrag current;
        do current = Drag; while (!TryAdvanceDrag(current, ViewportDrag.Off with { Joint = current.Joint }));
        followInput.End();
    }

    /// <summary>Ends the current gesture; teleoperation, if active, stays selected.</summary>
    private void EndViewportGesture() => EndViewportGesture(Drag);

    /// <summary>Following → Teleoperating only if the drag is still the
    /// <paramref name="observed"/> one; a STOP that landed since stays Off.</summary>
    internal void EndViewportGesture(ViewportDrag observed)
    {
        if (observed.Phase == ViewportDragPhase.Following)
            TryAdvanceDrag(observed, observed with { Phase = ViewportDragPhase.Teleoperating, Request = 0 });
        followInput.End();
    }

    /// <summary>Teleoperating → Following only if the drag is still the
    /// <paramref name="observed"/> one; a concurrent pause or STOP wins.</summary>
    internal bool TryBeginViewportFollowing(ViewportDrag observed, long request, string joint) =>
        request > 0 && observed.Phase == ViewportDragPhase.Teleoperating &&
        TryAdvanceDrag(observed, observed with { Phase = ViewportDragPhase.Following, Request = request, FollowJoint = joint });

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
        var observed = Drag;
        await engine.RequestAsync("robot.teleoperate", new JsonObject {
            ["sceneId"] = linked.SceneId, ["entityId"] = linked.EntityId,
            ["enabled"] = true, ["joint"] = joint }, cancellation);
        if (!TryAdvanceDrag(observed, new(ViewportDragPhase.Teleoperating, joint)))
            throw new InvalidOperationException("Teleoperate was stopped while it started. Select the joint again.");
        message = "Teleoperate · drag the blue ring · release to finish the target · Esc or Stop movement to hold";
    }

    private Task HoldFollowing(CancellationToken cancellation)
    {
        var observed = Drag;
        EndViewportGesture(observed);
        return SendFollowEnd(observed.Phase == ViewportDragPhase.Following ? observed.Request : 0, cancellation);
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
                var idle = Drag;
                var acknowledgement = await Command("follow-start", null, cancellation);
                long admitted = acknowledgement["request_id"]?.GetValue<long>() ?? 0;
                // Only the teleoperation that issued follow-start can enter Following;
                // any pause or STOP since then wins and the admitted request is ended.
                if (admitted > 0 && !TryBeginViewportFollowing(idle, admitted, edited))
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
