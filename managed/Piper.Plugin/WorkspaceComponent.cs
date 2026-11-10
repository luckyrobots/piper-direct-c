namespace LuckyRobots.Piper.Plugin;

/// <summary>The simulation cameras a published Settings view offered, by position.</summary>
internal sealed record SimulationCameraChoices(string SceneId, (string Id, string Name)[] Cameras);

/// <summary>What a published view shows: one profile on one navigation value.</summary>
internal readonly record struct WorkspaceViewContext(string Profile, WorkspaceNavigation Navigation)
{
    public WorkspacePage Page => Navigation.Page;
    public SettingsCategory SettingsCategory => Navigation.Category;
    public string SettingsCamera => Navigation.SettingsCamera;
    public bool Calibration => Navigation.CalibrationOpen;
}

/// <summary>One confirmed view: the epoch its widget IDs carry and everything an
/// event from that view may rely on.</summary>
internal sealed record PublishedWorkspaceView(long Epoch, WorkspaceViewContext Context, RobotProfile StopProfile,
    WorkspaceClearConfirmation? Confirmation, SimulationCameraChoices CameraChoices);

/// <summary>The Workspace component (P5): which page the operator is on, which
/// view the host has confirmed, what the primary robot action is doing and what
/// the replay review holds. It owns the fields below; nothing outside this class
/// can assign them. Every reader sees an already-published immutable value
/// without taking a lock, so STOP (which reads <see cref="Published"/> to find
/// the profile whose STOP button is on screen) never waits on the publication gate.
/// <code>
///   field          written by                                       readers see
///   navigation     the UI actor, inside the publication gate        one volatile reference to an
///                  (Commit, from PipersPlugin.PublishWorkspaceView)  immutable WorkspaceNavigation
///   published      whoever holds the publication gate (Confirm)     one volatile reference to an
///                                                                   immutable PublishedWorkspaceView,
///                                                                   which carries the confirmed epoch
///   reservedEpoch  the gate holder (ReserveEpoch)                   nobody else
///   uncertain      the gate holder (BeginPublication, Confirm)      volatile read
///   detail toggles the UI actor (Toggle*)                           volatile bool
///   PrimaryAction  the UI actor (transitions listed on              one volatile reference to an
///                  PrimaryAction)                                   immutable PrimaryActionState
///   Replay         the UI actor and the telemetry loop, through     one volatile reference to an
///                  its compare-and-swap (ReplayReviewSession)       immutable ReplayReview
/// </code>
/// Plugin-1: <c>Navigate(candidate)</c> yields a value, the shell runs the guards
/// and cleanup, and <see cref="Commit"/> adopts it inside the gate just before
/// the view that shows it is sent. <see cref="BeginPublication"/> marks the
/// visible tree unknown until <see cref="Confirm"/> records the one the host
/// accepted; while it is unknown no view-bound event or patch is admitted.
/// The UI actor is whoever holds <c>PipersPlugin.commands</c>. Fixtures place a
/// page with <see cref="Commit"/> from their one thread before they render.</summary>
internal sealed class WorkspaceComponent
{
    private volatile WorkspaceNavigation navigation = WorkspaceNavigation.Initial;
    private volatile PublishedWorkspaceView? published;
    private long reservedEpoch;
    private volatile bool uncertain;
    private volatile bool connectionDetailsOpen, cameraStreamDetailsOpen, cameraLensDetailsOpen;

    /// <summary>Serializes every view and patch sent to the host panel. STOP never enters it.</summary>
    public SemaphoreSlim PublicationGate { get; } = new(1, 1);

    /// <summary>Plugin-5: the one primary robot action and its visible status.</summary>
    public PrimaryAction PrimaryAction { get; } = new();

    /// <summary>Plugin-I: the loaded replay episode and what was checked against it.</summary>
    public ReplayReviewSession Replay { get; } = new();

    /// <summary>The committed page. It may be ahead of <see cref="Published"/> while its view is being sent.</summary>
    public WorkspaceNavigation Navigation => navigation;
    /// <summary>The last view the host accepted; null before the first.</summary>
    public PublishedWorkspaceView? Published => published;
    /// <summary>The epoch of the last accepted view; 0 before the first.</summary>
    public long Epoch => published?.Epoch ?? 0;
    /// <summary>A view was sent and not confirmed, so the visible tree is unknown.</summary>
    public bool PublicationUncertain => uncertain;
    public bool ConnectionDetailsOpen => connectionDetailsOpen;
    public bool CameraStreamDetailsOpen => cameraStreamDetailsOpen;
    public bool CameraLensDetailsOpen => cameraLensDetailsOpen;

    /// <summary>The published view when it is confirmed and still shows <paramref name="current"/>.</summary>
    public PublishedWorkspaceView? Confirmed(WorkspaceViewContext current) =>
        published is { } view && !uncertain && view.Context == current ? view : null;

    /// <summary>Adopts a navigation value. The caller holds the publication gate.</summary>
    public void Commit(WorkspaceNavigation next) => navigation = next;

    /// <summary>The visible tree is unknown from here until <see cref="Confirm"/>. The caller holds the gate.</summary>
    public void BeginPublication() => uncertain = true;

    /// <summary>A fresh epoch for the view about to be sent; never reused, even when the send fails. The caller holds the gate.</summary>
    public long ReserveEpoch() => ++reservedEpoch;

    /// <summary>Records the view the host accepted. The caller holds the gate.</summary>
    public void Confirm(PublishedWorkspaceView view)
    {
        published = view;
        uncertain = false;
    }

    public void ToggleConnectionDetails() => connectionDetailsOpen = !connectionDetailsOpen;
    public void ToggleCameraStreamDetails() => cameraStreamDetailsOpen = !cameraStreamDetailsOpen;
    public void ToggleCameraLensDetails() => cameraLensDetailsOpen = !cameraLensDetailsOpen;
}
