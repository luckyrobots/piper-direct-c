namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-5 primary robot action: Idle → Running → Idle. Running reports
/// stages (received → enable → link-scene, or for a resume waiting-for-stillness →
/// returning-to-controls); ResumingControls is Running while ResumeControls owns the
/// arm. Idle keeps the last status (complete or failed) visible.</summary>
internal enum PrimaryActionPhase { Idle, Running, ResumingControls }

/// <summary>One primary action: its phase, the id its diagnostic receipts carry and the status line it shows.</summary>
internal sealed record PrimaryActionState(PrimaryActionPhase Phase, string Id, string Status);

/// <summary>Owns the Plugin-5 state. Part of <see cref="WorkspaceComponent"/>.
/// <code>
///   transition    from              to                 written by
///   Begin         any               Running, new id    the UI actor (RunPrimaryAction)
///   Report        any               same phase, text   the UI actor (progress and failure)
///   EnterResume   Running           ResumingControls   the UI actor (ResumeControls)
///   LeaveResume   ResumingControls  Running            the UI actor (ResumeControls, finally)
///   Finish        any               Idle, text kept    the UI actor (RunPrimaryAction, finally)
/// </code>
/// The UI actor is whoever holds <c>PipersPlugin.commands</c>, so there is one
/// writer. The telemetry loop and renders read <see cref="Current"/>: one
/// volatile reference to an immutable value, so a phase is never seen with
/// another action's id or status.</summary>
internal sealed class PrimaryAction
{
    private volatile PrimaryActionState current = new(PrimaryActionPhase.Idle, "", "");

    public PrimaryActionState Current => current;
    public bool Busy => current.Phase != PrimaryActionPhase.Idle;
    public bool ResumingControls => current.Phase == PrimaryActionPhase.ResumingControls;

    /// <summary>Starts an action under a fresh receipt id. The previous status stays until the first report.</summary>
    public void Begin() => current = new(PrimaryActionPhase.Running, Guid.NewGuid().ToString("N"), current.Status);

    public void Report(string status) => current = current with { Status = status };

    /// <summary>ResumeControls takes the arm. Only a running action may resume.</summary>
    public void EnterResume()
    {
        var running = current;
        if (running.Phase != PrimaryActionPhase.Running)
            throw new InvalidOperationException("Resume controls runs only as the primary robot action.");
        current = running with { Phase = PrimaryActionPhase.ResumingControls };
    }

    public void LeaveResume()
    {
        var resuming = current;
        if (resuming.Phase == PrimaryActionPhase.ResumingControls) current = resuming with { Phase = PrimaryActionPhase.Running };
    }

    public void Finish() => current = current with { Phase = PrimaryActionPhase.Idle };
}
