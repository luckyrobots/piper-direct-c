namespace LuckyRobots.Piper.Plugin;

/// <summary>The Shell component (P5): what the plugin itself remembers, apart
/// from the robot, the workspace and the calibration. It is made of parts; each
/// part owns its fields, is their only writer, and carries its own field table.
/// <code>
///   part          machine                          table on
///   Backend       Plugin-4 owned backend, restart  OwnedBackend
///   Events        Plugin-6 UI dispatch actor       UiEventDispatch (its states), below (the reference)
///   Loops         background loops                 LoopSupervision
///   StopFollowUp  detached work after a STOP       StopFollowUpQueue
///
///   field   written by                                  readers see
///   events  plugin start (Open), once, before the       the actor, or null before start
///           first view is published (B13)               (isolated fixtures run handlers inline)
/// </code>
/// <b>Relation to <see cref="RobotCommandAuthority"/>.</b> The authority stays
/// the only owner of permission epochs. Nothing here grants motion, and no
/// transition here runs inside the authority's epoch gate. The STOP path reads
/// <see cref="Events"/> without a lock, and reaches <see cref="StopFollowUp"/>
/// only after the hardware request has been answered.
///
/// Not owned here: the workflows and views (they stay on <c>PipersPlugin</c>),
/// the order in which the plugin is taken down (<c>DisposeAsync</c>), and the
/// plugin's remaining fields: preferences, the operator's status lines, motion
/// approval and the robot link.</summary>
internal sealed class ShellComponent
{
    private UiEventDispatch? events;

    public OwnedBackend Backend { get; } = new();
    public LoopSupervision Loops { get; } = new();
    public StopFollowUpQueue StopFollowUp { get; } = new();

    public UiEventDispatch? Events => Volatile.Read(ref events);

    /// <summary>Installs the UI actor. There is one for the plugin's lifetime.</summary>
    public void Open(UiEventDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (Interlocked.CompareExchange(ref events, dispatch, null) is not null)
            throw new InvalidOperationException("The UI actor is already installed.");
    }
}
