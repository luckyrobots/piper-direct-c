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

/// <summary>The Plugin-C part of the Follow component: the viewport drag and
/// the gate that orders its gestures.
/// <code>
///   field  written by                                           readers see
///   drag   the UI actor (TrySelectJoint, TryBeginFollowing,     one volatile reference to an
///          EndGesture) and any thread that stops it (Stop)      immutable ViewportDrag
///   input  the UI actor (Accept); any thread ends the active    its active gesture is a
///          gesture (Stop, EndGesture)                           volatile read
/// </code>
/// Every drag transition is a compare-and-swap from the state its writer
/// observed, so a STOP on the telemetry thread can never be overwritten by a
/// stale gesture writer: the stale writer's exchange fails and it backs out.
/// <see cref="Stop"/> takes no lock.</summary>
internal sealed class ViewportDragSession
{
    private readonly FollowInputGate input = new();
    private ViewportDrag drag = ViewportDrag.Off;

    public ViewportDrag Current => Volatile.Read(ref drag);
    public bool Teleoperating => Current.Phase != ViewportDragPhase.Off;
    /// <summary>The follow request of the gesture in progress, or 0 when none is.</summary>
    public long FollowRequest => Current is { Phase: ViewportDragPhase.Following } current ? current.Request : 0;

    private bool TryAdvance(ViewportDrag observed, ViewportDrag next) =>
        ReferenceEquals(Interlocked.CompareExchange(ref drag, next, observed), observed);

    /// <summary>Enters Teleoperating on <paramref name="joint"/> only if the drag
    /// is still the <paramref name="observed"/> one; a concurrent STOP wins.</summary>
    public bool TrySelectJoint(ViewportDrag observed, string joint) =>
        TryAdvance(observed, new(ViewportDragPhase.Teleoperating, joint));

    /// <summary>Teleoperating → Following only if the drag is still the
    /// <paramref name="observed"/> one; a concurrent pause or STOP wins.</summary>
    public bool TryBeginFollowing(ViewportDrag observed, long request, string joint) =>
        request > 0 && observed.Phase == ViewportDragPhase.Teleoperating &&
        TryAdvance(observed, observed with { Phase = ViewportDragPhase.Following, Request = request, FollowJoint = joint });

    /// <summary>Following → Teleoperating only if the drag is still the
    /// <paramref name="observed"/> one; a STOP that landed since stays Off.
    /// The gesture ends either way.</summary>
    public void EndGesture(ViewportDrag observed)
    {
        if (observed.Phase == ViewportDragPhase.Following)
            TryAdvance(observed, observed with { Phase = ViewportDragPhase.Teleoperating, Request = 0 });
        input.End();
    }

    /// <summary>Leaves teleoperation entirely and discards any gesture; the selected joint is remembered.</summary>
    public void Stop()
    {
        ViewportDrag current;
        do current = Current; while (!TryAdvance(current, ViewportDrag.Off with { Joint = current.Joint }));
        input.End();
    }

    /// <summary>Admits one viewport gesture message in order; throws when it is queued, reordered or already completed.</summary>
    public void Accept(JsonObject gesture, long nowMilliseconds) => input.Accept(gesture, nowMilliseconds);
}
