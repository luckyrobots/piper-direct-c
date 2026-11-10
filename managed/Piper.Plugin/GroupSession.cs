using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

internal enum GroupFollowPhase { Arming, Armed, Matching, Admitting, Activating, DrainingPrimer, Following, Retiring, Retired }

/// <summary>What the publisher measured for one lane when it last stepped that lane's execution.</summary>
internal sealed record LaneStepEvidence(SimulationFollowHardware Hardware, bool Stationary, double GripperSpeed, double GripperFeedback);

/// <summary>One follower robot inside a <see cref="GroupSession"/>: its fixed
/// assignment, and the marks its session's workers leave on it. Nothing outside
/// this class can assign them.
/// <code>
///   field              written by                                     readers see
///   diagnostics        the UI actor, once, before the session is      volatile read
///                      installed (AttachDiagnostics)
///   arm, pika          the coordinator (Plugin-A transitions below)   one volatile reference to an
///                                                                     immutable LaneArm / LanePika
///   first, lastSample  the coordinator (RecordFirst, RecordSample)    volatile read
///   execution          the coordinator, once (BeginExecution)         volatile read; the execution
///                                                                     itself is used under Gate
///   lastStep           the publisher, under the session's Gate        one volatile reference to an
///                      (RecordStep)                                   immutable LaneStepEvidence
///   sequence           the primer, then the publisher (NextSequence)  interlocked read
///   armAdmissionRetiredTicks
///                      the coordinator, when an arm admission         volatile read
///                      command returns (ArmAdmissionReturned)
///   rejectedPikaAudit  the first rejected Pika write                  volatile read
///                      (TryRecordRejectedPika, a compare-and-swap)
/// </code>
/// The coordinator is the one task running the session. The primer and the
/// publisher are its two target writers; the coordinator joins the primer
/// before it starts the publisher.</summary>
internal sealed class GroupLane(RobotProfile profile, RobotLink robot, FollowControlApproval approval, double? reference,
    FollowLaneTelemetry feedback, FollowLaneController controller)
{
    public readonly RobotProfile Profile = profile;
    public readonly RobotLink Robot = robot;
    public readonly FollowControlApproval Approval = approval;
    public readonly double? ClosedReference = reference;
    public readonly FollowLaneTelemetry Feedback = feedback;
    public readonly FollowLaneController Controller = controller;
    public readonly SimulationPublishDiagnostics Publications = new();

    private SimulationFollowDiagnostics? diagnostics;
    private SimulationFollowSample? first, lastSample;
    private OrderedSimulationExecution? execution;
    private LaneStepEvidence? lastStep;
    private PikaFollowAudit? rejectedPikaAudit;
    private long sequence, armAdmissionRetiredTicks;
    private LaneArm arm = LaneArm.Untouched;
    private LanePika pika = LanePika.Untouched;

    public SimulationFollowDiagnostics? Diagnostics => Volatile.Read(ref diagnostics);
    /// <summary>The validated first frame every later frame must bind to.</summary>
    public SimulationFollowSample? First => Volatile.Read(ref first);
    public SimulationFollowSample? LastSample => Volatile.Read(ref lastSample);
    /// <summary>Plugin-E: this lane's ordered execution. Use it under the session's Gate.</summary>
    public OrderedSimulationExecution? Execution => Volatile.Read(ref execution);
    public LaneStepEvidence? LastStep => Volatile.Read(ref lastStep);
    public PikaFollowAudit? RejectedPikaAudit => Volatile.Read(ref rejectedPikaAudit);
    /// <summary>The sequence of the last target sent to this lane.</summary>
    public long Sequence => Interlocked.Read(ref sequence);
    /// <summary>When the last arm admission command returned; 0 before any did.</summary>
    public long ArmAdmissionRetiredTicks => Volatile.Read(ref armAdmissionRetiredTicks);
    public LaneArm Arm => Volatile.Read(ref arm);
    public LanePika Pika => Volatile.Read(ref pika);

    public void AttachDiagnostics(SimulationFollowDiagnostics writer) => Volatile.Write(ref diagnostics, writer);
    public void RecordFirst(SimulationFollowSample sample) { Volatile.Write(ref first, sample); Volatile.Write(ref lastSample, sample); }
    public void RecordSample(SimulationFollowSample sample) => Volatile.Write(ref lastSample, sample);
    public void BeginExecution(OrderedSimulationExecution started) => Volatile.Write(ref execution, started);
    public LaneStepEvidence RecordStep(LaneStepEvidence evidence) { Volatile.Write(ref lastStep, evidence); return evidence; }
    public long NextSequence() => Interlocked.Increment(ref sequence);
    public void ArmAdmissionReturned(long ticks) => Volatile.Write(ref armAdmissionRetiredTicks, ticks);
    /// <summary>Keeps the first rejection only.</summary>
    public void TryRecordRejectedPika(PikaFollowAudit audit) => Interlocked.CompareExchange(ref rejectedPikaAudit, audit, null);

    public void TouchArm() { if (Arm is LaneArm.UntouchedArm) Volatile.Write(ref arm, LaneArm.Touched); }
    public void MatchRequested(long request) => Volatile.Write(ref arm, new LaneArm.Matching(request));
    public void Matched() { if (Arm is LaneArm.Matching) Volatile.Write(ref arm, LaneArm.MatchedPose); }
    public void FollowAdmitted(long request, long ticks, long admittedSequence) =>
        Volatile.Write(ref arm, new LaneArm.Following(request, ticks, admittedSequence));
    public void TouchPika() { if (!Pika.Touched) Volatile.Write(ref pika, LanePika.TouchedOnly); }
    public void PikaFollowing(string session) => Volatile.Write(ref pika, new(true, session));
}

/// <summary>Plugin-A lane arm admission.
/// <code>
///   Untouched --pose or follow-start sent--> Touched
///   Touched --pose acknowledged--> Matching(MatchRequest) --every lane settled--> Matched
///   Untouched|Touched|Matched --follow-start acknowledged--> Following(FollowRequest, admission ticks/sequence)
/// </code>
/// Each phase is its own type, so a pose request and a follow request can never
/// be held at once; only the current one is heartbeated and held at retirement.</summary>
internal abstract record LaneArm
{
    private LaneArm() { }
    internal sealed record UntouchedArm : LaneArm;
    internal sealed record TouchedArm : LaneArm;
    internal sealed record Matching(long MatchRequest) : LaneArm;
    internal sealed record MatchedArm : LaneArm;
    internal sealed record Following(long FollowRequest, long AdmissionTicks, long AdmissionSequence) : LaneArm;

    public static readonly LaneArm Untouched = new UntouchedArm(), Touched = new TouchedArm(), MatchedPose = new MatchedArm();
    /// <summary>The native request this lane currently owns, or 0 before any acknowledgement.</summary>
    public long CurrentRequest => this switch { Matching matching => matching.MatchRequest, Following following => following.FollowRequest, _ => 0 };
}

/// <summary>A Pika session exists only on a touched channel.</summary>
internal sealed record LanePika(bool Touched, string Session = "")
{
    public static readonly LanePika Untouched = new(false), TouchedOnly = new(true);
}

/// <summary>One grouped Follow run, from arming to retirement: the scene and
/// lanes it was armed for (fixed at construction), its phase, and the marks its
/// coordinator leaves. Nothing outside this class can assign them.
/// <code>
///   field                written by                                    readers see
///   phase                the coordinator (Advance); any thread may     volatile read
///                        begin retirement (BeginRetirement). Both are
///                        a compare-and-swap, so retirement always wins.
///   runId                the coordinator, once Play is bound (BindRun) volatile read
///   firstFailure         the first worker to fail (TryRecordFailure,   volatile read
///                        a compare-and-swap)
///   endReason            the coordinator (EndWith)                     volatile read
///   acknowledgedEpisode  the coordinator (AcknowledgeEpisode)          volatile read
///   heartbeats, primer,  the coordinator, once each (Attach...)        volatile read
///   publisher
///   observation          the coordinator, once (BeginObservation)      volatile read
///   sourceObservedAt     the coordinator (RecordValidatedSource)       volatile read
/// </code>
/// <see cref="Gate"/> is the session's one lock: it guards every lane's ordered
/// execution and path between the coordinator, which appends frames, and the
/// publisher, which steps them. The STOP path never takes it:
/// <see cref="BeginRetirement"/> is one compare-and-swap and one cancellation.</summary>
internal sealed class GroupSession(string backend, string sceneId, string entityId, string asset, string revision, GroupLane[] lanes, long inputEpoch,
    TimeProvider clock)
{
    public readonly string Backend = backend, Scene = sceneId, Entity = entityId, Asset = asset, Revision = revision;
    public readonly GroupLane[] Lanes = lanes;
    public readonly long InputEpoch = inputEpoch;
    public readonly TimeProvider Clock = clock;
    public readonly TaskCompletionSource Armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly string PrepareId = Guid.NewGuid().ToString("N");
    public readonly CancellationTokenSource Stop = new();
    public readonly object Gate = new();
    public readonly GroupSimulationPhaseBarrier Barrier = new();

    private string runId = "", firstFailure = "", acknowledgedEpisode = "", endReason = "";
    private long sourceObservedAt;
    private Task? publisher, heartbeats, primer;
    private OrderedSimulationObservation? observation;
    private int phase = (int)GroupFollowPhase.Arming;

    public GroupFollowPhase Phase => (GroupFollowPhase)Volatile.Read(ref phase);
    public bool IsRetiring => Phase is GroupFollowPhase.Retiring or GroupFollowPhase.Retired;
    public bool CanPrime => Phase is GroupFollowPhase.Admitting or GroupFollowPhase.Activating;
    /// <summary>The Play run this session is bound to; empty until the operator pressed Play.</summary>
    public string RunId => Volatile.Read(ref runId);
    public string FirstFailure => Volatile.Read(ref firstFailure);
    public string EndReason => Volatile.Read(ref endReason);
    public string AcknowledgedEpisode => Volatile.Read(ref acknowledgedEpisode);
    public Task? Publisher => Volatile.Read(ref publisher);
    public Task? Heartbeats => Volatile.Read(ref heartbeats);
    public Task? Primer => Volatile.Read(ref primer);
    public OrderedSimulationObservation? Observation => Volatile.Read(ref observation);

    public void BindRun(string run) => Volatile.Write(ref runId, run);
    /// <summary>Keeps the first failure only. False when one was already recorded.</summary>
    public bool TryRecordFailure(string reason) => Interlocked.CompareExchange(ref firstFailure, reason, "").Length == 0;
    public void EndWith(string reason) => Volatile.Write(ref endReason, reason);
    public void AcknowledgeEpisode(string episodeId) => Volatile.Write(ref acknowledgedEpisode, episodeId);
    public void AttachHeartbeats(Task worker) => Volatile.Write(ref heartbeats, worker);
    public void AttachPrimer(Task worker) => Volatile.Write(ref primer, worker);
    public void AttachPublisher(Task worker) => Volatile.Write(ref publisher, worker);
    public void BeginObservation(OrderedSimulationObservation started) => Volatile.Write(ref observation, started);

    public void RecordValidatedSource(long observedAt) => Volatile.Write(ref sourceObservedAt, observedAt);

    public void RequireOrderedSourceLease()
    {
        var age = Clock.GetElapsedTime(Volatile.Read(ref sourceObservedAt));
        if (age >= TimeSpan.Zero && age < TimeSpan.FromMilliseconds(SimulationFollowPolicy.MaximumSourceAgeMilliseconds)) return;
        throw new InvalidOperationException("The shared simulation source expired after 250 ms; all follower targets were retired.");
    }

    public void Advance(GroupFollowPhase next)
    {
        var expected = next switch
        {
            GroupFollowPhase.Armed => GroupFollowPhase.Arming,
            GroupFollowPhase.Matching => GroupFollowPhase.Armed,
            GroupFollowPhase.Admitting => GroupFollowPhase.Matching,
            GroupFollowPhase.Activating => GroupFollowPhase.Admitting,
            GroupFollowPhase.DrainingPrimer => GroupFollowPhase.Activating,
            GroupFollowPhase.Following => GroupFollowPhase.DrainingPrimer,
            GroupFollowPhase.Retired => GroupFollowPhase.Retiring,
            _ => throw new ArgumentOutOfRangeException(nameof(next))
        };
        var before = (GroupFollowPhase)Interlocked.CompareExchange(ref phase, (int)next, (int)expected);
        if (before != expected) RejectPhase(before, next.ToString());
    }

    public void Require(GroupFollowPhase expected)
    {
        var current = Phase;
        if (current != expected) RejectPhase(current, expected.ToString());
    }

    public void RequireTargetPublication()
    {
        var current = Phase;
        // A priming batch already admitted to the write gate can finish
        // while the coordinator joins it, but no new batch starts there.
        if (current is not (GroupFollowPhase.Admitting or GroupFollowPhase.Activating or GroupFollowPhase.DrainingPrimer or GroupFollowPhase.Following))
            RejectPhase(current, "target publication");
        if (current == GroupFollowPhase.Following) RequireOrderedSourceLease();
    }

    private void RejectPhase(GroupFollowPhase current, string operation)
    {
        if (current is GroupFollowPhase.Retiring or GroupFollowPhase.Retired)
            throw new OperationCanceledException("Grouped following was disarmed.", Stop.Token);
        throw new InvalidOperationException($"Cannot enter {operation} while Follow is {current}.");
    }

    public void BeginRetirement()
    {
        while (true)
        {
            var current = Phase;
            if (current is GroupFollowPhase.Retiring or GroupFollowPhase.Retired) return;
            if (Interlocked.CompareExchange(ref phase, (int)GroupFollowPhase.Retiring, (int)current) != (int)current) continue;
            Stop.Cancel();
            return;
        }
    }
}
