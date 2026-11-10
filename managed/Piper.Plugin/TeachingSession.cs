namespace LuckyRobots.Piper.Plugin;

/// <summary>The CAN device and generation a teaching session belongs to. A backend
/// session change releases the session through DisarmTelemetryTargets.</summary>
internal readonly record struct TeachingScope(string Device, long Generation);

/// <summary>Plugin-D teaching ownership and automatic return, one phase per scope:
/// <code>
///   Idle ──physical controller takes the arm──→ Observed(approved at hand-over?)
///   Observed ──hand guiding on Controls──→ Guiding
///   Guiding ──light off, approved at hand-over──→ EligibleReturn ──one same-pose return──→ Attempted
///   Guiding ──light off, not approved──→ Observed
///   Guiding/EligibleReturn/Attempted ──leave Controls, Off, playback, lost feedback, invalidate──→ Observed
///   any ──CAN restored, STOP, recovery, new scope──→ Idle
/// </code>
/// Loading an ended state never grants the return: it needs a hand-guiding cycle
/// observed here, and a rejected return is not retried in the same cycle.</summary>
internal enum TeachingSessionPhase { Idle, Observed, Guiding, EligibleReturn, Attempted }

internal sealed record TeachingSessionState(TeachingScope? Scope, TeachingSessionPhase Phase, bool ApprovedAtHandOver)
{
    public static readonly TeachingSessionState Initial = new(null, TeachingSessionPhase.Idle, false);
    /// <summary>The physical controller has owned the arm since CAN control was last restored.</summary>
    public bool Owned => Phase != TeachingSessionPhase.Idle;
    /// <summary>Restoring CAN may restore the approval that existed at the hand-over.</summary>
    public bool RestoresApproval => Owned && ApprovedAtHandOver;
}

/// <summary>The single owner and writer of <see cref="TeachingSessionState"/>. The
/// telemetry reader and the UI poll both report into it; neither writes its fields.</summary>
internal sealed class TeachingSession
{
    private readonly object sync = new();
    private TeachingSessionState state = TeachingSessionState.Initial;
    public TeachingSessionState Current => Volatile.Read(ref state);

    private void Set(TeachingSessionState next) => Volatile.Write(ref state, next);

    /// <summary>A different scope starts a new, unowned session.</summary>
    public void Track(TeachingScope? scope)
    {
        lock (sync) if (state.Scope != scope) Set(TeachingSessionState.Initial with { Scope = scope });
    }

    /// <summary>Records the hand-over to physical teaching once, keeping whether this
    /// session's control approval existed then. Returns true for the first observation.</summary>
    public bool ObserveHandOver(TeachingScope scope, bool approved)
    {
        lock (sync)
        {
            if (state.Scope != scope) Set(TeachingSessionState.Initial with { Scope = scope });
            if (state.Owned) return false;
            Set(state with { Phase = TeachingSessionPhase.Observed, ApprovedAtHandOver = approved });
            return true;
        }
    }

    /// <summary>CAN control restored, STOP, or a verified recovery: no teaching session remains.</summary>
    public void Release()
    {
        lock (sync) Set(state with { Phase = TeachingSessionPhase.Idle, ApprovedAtHandOver = false });
    }

    /// <summary>A read-only workspace (calibration) withdraws the approval kept at the hand-over.</summary>
    public void WithdrawApproval()
    {
        lock (sync) if (state.ApprovedAtHandOver) Set(state with { ApprovedAtHandOver = false });
    }

    /// <summary>Retires any automatic-return progress, keeping ownership.</summary>
    public void InvalidateReturn()
    {
        lock (sync) Set(state with { Phase = RetiredReturn(state.Phase) });
    }

    private static TeachingSessionPhase RetiredReturn(TeachingSessionPhase phase) =>
        phase is TeachingSessionPhase.Guiding or TeachingSessionPhase.EligibleReturn or TeachingSessionPhase.Attempted ? TeachingSessionPhase.Observed : phase;

    /// <summary>Advances the automatic return for one poll. Returns true exactly once
    /// per approved hand-guiding cycle, when its same-pose return is eligible.</summary>
    public bool ObserveReturn(TeachingScope scope, TeachingState mode, bool controlsPage)
    {
        lock (sync)
        {
            if (state.Scope != scope) Set(TeachingSessionState.Initial with { Scope = scope });
            if (!controlsPage || mode.Phase is TeachingPhase.Unknown or TeachingPhase.Off or TeachingPhase.Playback or TeachingPhase.PlaybackPaused)
            {
                Set(state with { Phase = RetiredReturn(state.Phase) });
                return false;
            }
            if (mode.HandGuiding)
            {
                if (state.Phase != TeachingSessionPhase.Guiding) Set(state with { Phase = TeachingSessionPhase.Guiding });
                return false;
            }
            if (state.Phase == TeachingSessionPhase.Guiding)
                Set(state with { Phase = state.ApprovedAtHandOver ? TeachingSessionPhase.EligibleReturn : TeachingSessionPhase.Observed });
            if (state.Phase != TeachingSessionPhase.EligibleReturn || !mode.SamePoseReturn || mode.Phase != TeachingPhase.Ended || !mode.CanReturn)
                return false;
            Set(state with { Phase = TeachingSessionPhase.Attempted }); // A rejected request is not retried on the next poll.
            return true;
        }
    }
}
