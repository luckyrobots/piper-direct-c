using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

internal enum RecoveryKind { HoldHere, ReturnToZero }

/// <summary>One running recovery: what it does, the robot connection and the
/// permission epoch it was admitted under, and its cancellation. Everything but
/// the two marks below is fixed at construction.
/// <code>
///   field    written by                          readers see
///   worker   the UI actor that started it, once  volatile read
///   resetAt  the recovery's own worker           volatile read
/// </code></summary>
internal sealed class RecoveryOperation(RecoveryKind kind, RobotCommandScope scope, long epoch, CancellationTokenSource cancellation) : IDisposable
{
    private Task? worker;
    private long resetAt;

    public RecoveryKind Kind { get; } = kind;
    public RobotCommandScope Scope { get; } = scope;
    public long Epoch { get; } = epoch;
    public CancellationToken Token { get; } = cancellation.Token;
    /// <summary>The task running the recovery; null until it has been started.</summary>
    public Task? Worker => Volatile.Read(ref worker);
    /// <summary>When the running step is a controller reset, the time it was sent; otherwise 0.</summary>
    public long ResetAt => Volatile.Read(ref resetAt);

    public void Attach(Task running) => Volatile.Write(ref worker, running);

    /// <summary>Marks the step about to run: the time a reset is sent, or 0 for any other step. Only a reset may be followed by a bounded feedback gap.</summary>
    public void BeginStep(long resetSentAt) => Volatile.Write(ref resetAt, resetSentAt);

    public void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { } // Retirement may have completed concurrently.
    }

    public void Dispose() => cancellation.Dispose();
}

/// <summary>The Recovery component (P5): whether a recovery is running, what the
/// recovery guide shows, which operator question is pending and who owns the arm
/// during teaching. It owns the fields below; nothing outside this class can
/// assign them. Every reader sees one immutable value through one volatile
/// reference and takes no lock.
/// <code>
///   field         written by                                        readers see
///   operation     the UI actor, inside the authority's epoch gate   one volatile reference to a
///                 (TryAdmit); the recovery's worker or the fixture  RecoveryOperation whose
///                 that admitted it (TryRetire). Both are a          identity, scope and epoch
///                 compare-and-swap on the slot.                     never change
///   guide         the UI actor, the telemetry loop and a recovery   one volatile reference to an
///                 worker, each through a compare-and-swap; STOP     immutable RecoveryGuide
///                 and Cancel write Closed (CloseGuide, Cancel)
///   resetPrompt   a recovery worker or the UI actor, inside the     one volatile reference to an
///                 authority's epoch gate (AskResetCompletion); the  immutable ResetCompletionPrompt
///                 UI actor takes it by exchange (TakeResetCompletion)
///   homeProgress  the UI actor and the return-to-zero worker        volatile string
///   observed      the telemetry loop (ObserveTeaching)              one volatile reference to an
///                                                                   immutable TeachingState
///   Teaching      its own lock (transitions listed on               one volatile reference to an
///                 TeachingSession)                                  immutable TeachingSessionState
///   PikaTeaching  its own lock (table on PikaTeachingSession)       one volatile reference to an
///                                                                   immutable PikaTeachingState
/// </code>
/// <b>Relation to <see cref="RobotCommandAuthority"/>.</b> The authority stays
/// the only owner of permission epochs. A transition that must not survive a
/// STOP (<see cref="TryAdmit"/>, <see cref="AskResetCompletion"/>) is called by
/// the shell from inside <c>CommandAuthority.TryInEpoch</c>, so it happens
/// under the authority's lock and only while the epoch it was decided in is
/// still current. This class takes no lock of its own there, and none on the
/// STOP path: <see cref="Cancel"/> is one cancellation and one volatile write.
///
/// Plugin-2 (the old recovery phase) is gone: the panel renders the controller
/// evidence and <see cref="RecoveryOperation.Kind"/>, and the one operator
/// acknowledgement is <see cref="ResetPrompt"/>. Plugin-3 is <see cref="Guide"/>;
/// <see cref="Cancel"/> is its single reset.
///
/// Not owned here: <c>approved</c> and <c>linkCommandsEnabled</c> (motion
/// permission, written beside the authority) and <c>acknowledgedScenePose</c>
/// (the scene pose the measured link was accepted at, written with
/// <c>linkErrorDegrees</c> by the scene-link code). The UI actor is whoever
/// holds <c>PipersPlugin.commands</c>.</summary>
internal sealed class RecoveryComponent(PikaTeachingSession pikaTeaching)
{
    private RecoveryOperation? operation;
    private RecoveryGuide? guide;
    private ResetCompletionPrompt? resetPrompt;
    private volatile string homeProgress = "";
    private volatile TeachingState observed = TeachingState.Inspect(null);

    /// <summary>Plugin-D: teaching ownership, its scope and the automatic return.</summary>
    public TeachingSession Teaching { get; } = new();

    /// <summary>Plugin-G: Pika teaching, its recording and the save of its observations.</summary>
    public PikaTeachingSession PikaTeaching { get; } = pikaTeaching;

    /// <summary>The running recovery; null when none is.</summary>
    public RecoveryOperation? Operation => Volatile.Read(ref operation);
    /// <summary>Plugin-3: the recovery guide; null is Closed.</summary>
    public RecoveryGuide? Guide => Volatile.Read(ref guide);
    /// <summary>B1: the pending "has the reset completed?" question; null when none is.</summary>
    public ResetCompletionPrompt? ResetPrompt => Volatile.Read(ref resetPrompt);
    /// <summary>The stage the return-to-zero recovery last reported.</summary>
    public string HomeProgress => homeProgress;
    /// <summary>The teaching mode the telemetry loop last finished observing.</summary>
    public TeachingState ObservedTeaching => observed;

    // ---- the running recovery ----

    /// <summary>Takes the free slot. The caller is inside the authority's epoch
    /// gate, so a STOP either precedes this (and it is not called) or follows it
    /// (and finds the operation to cancel). False when a recovery already runs.</summary>
    public bool TryAdmit(RecoveryOperation admitted) => Interlocked.CompareExchange(ref operation, admitted, null) is null;

    /// <summary>Frees the slot only for the operation that holds it. False means
    /// the caller did not own the slot, which the shell treats as a broken invariant.</summary>
    public bool TryRetire(RecoveryOperation retired) =>
        ReferenceEquals(Interlocked.CompareExchange(ref operation, null, retired), retired);

    /// <summary>STOP, Cancel, Disable and a connection change: cancel the running
    /// recovery and close the guide. Lock-free; safe from any thread.</summary>
    public void Cancel()
    {
        Operation?.Cancel();
        CloseGuide();
    }

    // ---- Plugin-3: the guide ----

    /// <summary>Opens the guide for a robot connection unless it already shows that connection.</summary>
    public void OpenGuide(string scope)
    {
        if (Guide?.Scope != scope) Volatile.Write(ref guide, new RecoveryGuide.Open(scope));
    }

    /// <summary>Arms the guide to wait for one green-light hand-guiding cycle.</summary>
    public void ArmGuide(string scope, HandGuideRecoveryGate gate, string message) =>
        Volatile.Write(ref guide, new RecoveryGuide.Armed(scope, gate, message));

    public void CloseGuide() => Volatile.Write(ref guide, null);

    public void ShowGuideMessage(string text) => Change(current => current with { Message = text });

    /// <summary>A supported STOP recovery started from the recorded stop <paramref name="stopped"/>.</summary>
    public void RecordSupportedStop(JsonObject stopped, string message) =>
        Change(current => current with { SupportedStop = stopped, Message = message });

    /// <summary>The card sent the return command itself.</summary>
    public void MarkCommandSent(string message) => Change(current => current.Sent(message));

    /// <summary>The recorded stop changed: the hand-guiding proof is retired, the guide stays open.</summary>
    public void DisarmGuide(string message) => Change(current => current.HandGuideGate is null ? current : current.Disarmed(message));

    /// <summary>Records a newer observation of the gate an armed guide still carries.</summary>
    public void ObserveGate(HandGuideRecoveryGate seen, HandGuideRecoveryGate next) =>
        Change(current => current is RecoveryGuide.Armed armed && armed.Gate == seen ? armed with { Gate = next } : current);

    /// <summary>Claims the armed guide's single same-pose return. Only one caller
    /// wins, and a claimed return is never offered again.</summary>
    public bool TryClaimReturn(RecoveryGuide.Armed armed) => ReferenceEquals(Interlocked.CompareExchange(ref guide,
        new RecoveryGuide.Attempted(armed.Scope, armed.Gate, armed.Message, armed.SupportedStop), armed), armed);

    /// <summary>The claimed return was sent.</summary>
    public void MarkClaimedReturnSent(string message) =>
        Change(current => current is RecoveryGuide.Attempted ? current.Sent(message) : current);

    /// <summary>Applies <paramref name="change"/> to the open guide. A closed guide stays
    /// closed, so a late worker update cannot reopen what Cancel or STOP retired.</summary>
    private void Change(Func<RecoveryGuide, RecoveryGuide> change)
    {
        while (Guide is { } current)
            if (ReferenceEquals(Interlocked.CompareExchange(ref guide, change(current), current), current)) return;
    }

    // ---- B1: the reset question ----

    /// <summary>Raises the question. The caller is inside the authority's epoch
    /// gate for <see cref="ResetCompletionPrompt.Epoch"/>, so a STOP that already
    /// retired the asking recovery leaves no question behind.</summary>
    public void AskResetCompletion(ResetCompletionPrompt prompt) => Volatile.Write(ref resetPrompt, prompt);

    /// <summary>Removes and returns the question; only one caller gets it.</summary>
    public ResetCompletionPrompt? TakeResetCompletion() => Interlocked.Exchange(ref resetPrompt, null);

    // ---- progress and teaching observation ----

    public void ReportHomeProgress(string stage) => homeProgress = stage;

    public void ObserveTeaching(TeachingState seen) => observed = seen;
}
