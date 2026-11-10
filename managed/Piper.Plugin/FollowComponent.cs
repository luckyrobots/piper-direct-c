namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-B: the outer Follow lifecycle around a <see cref="GroupSession"/> (whose own phase is <see cref="GroupFollowPhase"/>).
/// <code>
///   Idle/Summary/SetupFailed --arm--> Discovering --session installed--> Session --retired--> Summary
///   Discovering --setup error or cancel--> SetupFailed / Idle;  any idle stage --selection change--> Idle
/// </code>
/// Session is derived from the installed session; the other stages are one immutable outcome,
/// so a setup failure and a retirement summary can never both be set.</summary>
internal enum GroupFollowStage { Idle, Discovering, Session, Summary, SetupFailed }
internal sealed record GroupFollowOutcome(GroupFollowStage Stage, string Summary = "")
{
    public static readonly GroupFollowOutcome Idle = new(GroupFollowStage.Idle);
}

/// <summary>The line the Follow controls show while a session exists, and its longer explanation.</summary>
internal sealed record FollowStatus(string Headline, string Detail)
{
    public static readonly FollowStatus Initial = new("Simulation only",
        "Choose the robots that should follow, then arm Follow simulation. Playback begins only when you press the editor's Play button.");
}

/// <summary>The Follow component (P5): whether a grouped simulation Follow run
/// exists, how the last one ended, what its controls say, and the viewport
/// joint drag. It owns the fields below; nothing outside this class can assign
/// them. Every reader sees one immutable value (or one session object) through
/// one volatile reference and takes no lock.
/// <code>
///   field         written by                                          readers see
///   session       the UI actor that armed it (TryInstall); the        one volatile reference to a
///                 session's own coordinator, or the fixture that      GroupSession whose scene,
///                 installed it (Retire). Both are a                   lanes and input epoch never
///                 compare-and-swap on the slot.                       change
///   outcome       the UI actor (BeginDiscovery, AbandonDiscovery,     one volatile reference to an
///                 FailSetup, ClearOutcome) and the retiring           immutable GroupFollowOutcome
///                 session's coordinator (Summarize)
///   inputEpoch    any thread that cancels Follow input (CancelInput)  interlocked read
///   controlEpoch  TryInstall and Retire, once each per session        interlocked read
///   status        the session's coordinator and workers (Report),     one volatile reference to an
///                 CancelInput and FailSetup                           immutable FollowStatus
///   Viewport      its own transitions (table on ViewportDragSession)  one volatile reference to an
///                                                                     immutable ViewportDrag
/// </code>
/// A session's own marks are on <see cref="GroupSession"/> and <see cref="GroupLane"/>
/// (Plugin-A lane admission, Plugin-E ordered execution); the coordinator and
/// the publisher share that session's <see cref="GroupSession.Gate"/>.
///
/// <b>Relation to <see cref="RobotCommandAuthority"/>.</b> The authority stays
/// the only owner of permission epochs. No Follow transition runs inside its
/// epoch gate, because none has to: a session carries the input epoch and the
/// control approvals it was armed under, and every command it sends re-checks
/// both (<c>PipersPlugin.GroupScopeCurrent</c>). A STOP that lands before,
/// during or after <see cref="TryInstall"/> therefore leaves a session that
/// fails its next scope check and retires. The STOP path takes no lock here:
/// <see cref="CancelInput"/> is one interlocked increment, one status exchange,
/// one phase compare-and-swap and one cancellation, and
/// <see cref="ViewportDragSession.Stop"/> is a compare-and-swap.
///
/// Not owned here: <c>approved</c> and <c>linkCommandsEnabled</c> (motion
/// permission, written beside the authority), the control approvals and the
/// tracked lease (the authority's), <c>link</c>, <c>linkBackendToken</c> and
/// <c>scenePermissionEpoch</c> (the scene binding), which robots are selected
/// as followers (<c>Preferences.Robots[].FollowSimulationSelected</c>), and
/// <c>uiActionError</c> and <c>message</c> (the shell's). The UI actor is
/// whoever holds <c>PipersPlugin.commands</c>.</summary>
internal sealed class FollowComponent(TimeProvider clock)
{
    private GroupSession? session;
    private GroupFollowOutcome outcome = GroupFollowOutcome.Idle;
    private FollowStatus status = FollowStatus.Initial;
    private long inputEpoch, controlEpoch;

    /// <summary>The clock every session and lane measures with; it shares the telemetry timestamp domain.</summary>
    public TimeProvider Clock { get; } = clock;

    /// <summary>Plugin-C: the viewport joint drag.</summary>
    public ViewportDragSession Viewport { get; } = new();

    /// <summary>The installed session; null when none is.</summary>
    public GroupSession? Session => Volatile.Read(ref session);
    public bool Active => Session is not null;
    /// <summary>How the last arming attempt or session ended. It is shown while no session is installed.</summary>
    public GroupFollowOutcome Outcome => Volatile.Read(ref outcome);
    /// <summary>The Plugin-B stage.</summary>
    public GroupFollowStage Stage => Active ? GroupFollowStage.Session : Outcome.Stage;
    /// <summary>Advances whenever Follow input is cancelled; a discovery or a session from an older epoch is retired.</summary>
    public long InputEpoch => Interlocked.Read(ref inputEpoch);
    /// <summary>Advances when a session takes or returns control ownership, so control events queued across that edge are discarded.</summary>
    public long ControlEpoch => Interlocked.Read(ref controlEpoch);
    public FollowStatus Status => Volatile.Read(ref status);

    // ---- Plugin-B: arming, the session slot and its outcome ----

    /// <summary>Enters Discovering and returns the input epoch the discovery runs under.</summary>
    public long BeginDiscovery()
    {
        Volatile.Write(ref outcome, new(GroupFollowStage.Discovering));
        return InputEpoch;
    }

    /// <summary>A cancelled discovery returns to Idle, unless a session or another outcome replaced it.</summary>
    public void AbandonDiscovery()
    {
        var current = Outcome;
        if (!Active && current.Stage == GroupFollowStage.Discovering)
            Interlocked.CompareExchange(ref outcome, GroupFollowOutcome.Idle, current);
    }

    /// <summary>Arming failed; the reason stays visible until the next attempt or selection change.</summary>
    public void FailSetup(string reason)
    {
        Volatile.Write(ref outcome, new(GroupFollowStage.SetupFailed, "Follow not armed\n" + reason));
        Volatile.Write(ref status, new("Follow not armed", reason));
    }

    /// <summary>The follower selection changed, so the last outcome no longer describes it.</summary>
    public void ClearOutcome() => Volatile.Write(ref outcome, GroupFollowOutcome.Idle);

    /// <summary>Takes the free slot and advances the control epoch. False while
    /// another session holds the slot, including one that is still retiring.</summary>
    public bool TryInstall(GroupSession installed)
    {
        if (Interlocked.CompareExchange(ref session, installed, null) is not null) return false;
        Interlocked.Increment(ref controlEpoch);
        return true;
    }

    /// <summary>Records how a retiring session ended. Its coordinator calls this before <see cref="Retire"/>.</summary>
    public void Summarize(string summary) => Volatile.Write(ref outcome, new(GroupFollowStage.Summary, summary));

    /// <summary>Advances the control epoch and frees the slot, only for the session that holds it.</summary>
    public bool Retire(GroupSession retired)
    {
        if (!ReferenceEquals(Session, retired)) return false;
        Interlocked.Increment(ref controlEpoch);
        return ReferenceEquals(Interlocked.CompareExchange(ref session, null, retired), retired);
    }

    /// <summary>STOP, Stop following, a connection change and shutdown: retire
    /// read-only discovery and begin retiring the installed session, which is
    /// returned so the caller can wait for it. Lock-free; safe from any thread.</summary>
    public GroupSession? CancelInput(string reason = "Following canceled")
    {
        Interlocked.Increment(ref inputEpoch); // Also retires read-only discovery before a session exists.
        if (Session is not { } group) return null;
        Report(reason);
        group.BeginRetirement();
        return group;
    }

    // ---- status ----

    /// <summary>Replaces the headline and keeps the explanation.</summary>
    public void Report(string headline)
    {
        FollowStatus current;
        do current = Status;
        while (!ReferenceEquals(Interlocked.CompareExchange(ref status, current with { Headline = headline }, current), current));
    }

    public void Report(string headline, string detail) => Volatile.Write(ref status, new(headline, detail));
}
