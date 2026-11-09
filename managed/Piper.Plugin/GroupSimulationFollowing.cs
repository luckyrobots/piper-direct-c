using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

internal enum GroupFollowPhase { Arming, Armed, Matching, Admitting, Activating, DrainingPrimer, Following, Retiring, Retired }

public sealed partial class PipersPlugin
{
    internal sealed class GroupLane(RobotProfile profile, RobotLink robot, FollowControlApproval approval, double? reference,
        FollowLaneTelemetry feedback, FollowLaneController controller)
    {
        public readonly RobotProfile Profile = profile;
        public readonly RobotLink Robot = robot;
        public readonly FollowControlApproval Approval = approval;
        public readonly double? ClosedReference = reference;
        public readonly FollowLaneTelemetry Feedback = feedback;
        public readonly FollowLaneController Controller = controller;
        public SimulationFollowSample? First, LastSample;
        public OrderedSimulationExecution? Execution;
        public SimulationFollowDiagnostics? Diagnostics;
        public readonly SimulationPublishDiagnostics Publications = new();
        public PikaFollowAudit? RejectedPikaAudit;
        public SimulationFollowHardware? LastHardware;
        public bool LastStationary;
        public double LastGripperSpeed, LastGripperFeedback;
        public long Sequence, ArmAdmissionRetiredTicks;

        // Arm and Pika admission are immutable records published with Volatile:
        // the coordinator writes them, heartbeats and retirement read them.
        private LaneArm arm = LaneArm.Untouched;
        private LanePika pika = LanePika.Untouched;
        public LaneArm Arm => Volatile.Read(ref arm);
        public LanePika Pika => Volatile.Read(ref pika);
        public void TouchArm() { if (Arm.Phase == LanePhase.Untouched) Volatile.Write(ref arm, LaneArm.Touched); }
        public void MatchRequested(long request) => Volatile.Write(ref arm, new(LanePhase.Matching, MatchRequest: request));
        public void Matched() { if (Arm.Phase == LanePhase.Matching) Volatile.Write(ref arm, LaneArm.MatchedPose); }
        public void FollowAdmitted(long request, long ticks, long sequence) =>
            Volatile.Write(ref arm, new(LanePhase.Following, FollowRequest: request, AdmissionTicks: ticks, AdmissionSequence: sequence));
        public void TouchPika() { if (!Pika.Touched) Volatile.Write(ref pika, LanePika.TouchedOnly); }
        public void PikaFollowing(string session) => Volatile.Write(ref pika, new(true, session));
    }

    /// <summary>Plugin-A lane arm admission.
    /// <code>
    ///   Untouched --pose or follow-start sent--> Touched
    ///   Touched --pose acknowledged--> Matching(MatchRequest) --every lane settled--> Matched
    ///   Untouched|Touched|Matched --follow-start acknowledged--> Following(FollowRequest, admission ticks/sequence)
    /// </code>
    /// A pose request and a follow request are separate fields; only the current
    /// one is heartbeated and held at retirement.</summary>
    internal enum LanePhase { Untouched, Touched, Matching, Matched, Following }

    internal sealed record LaneArm(LanePhase Phase, long MatchRequest = 0, long FollowRequest = 0, long AdmissionTicks = 0, long AdmissionSequence = 0)
    {
        public static readonly LaneArm Untouched = new(LanePhase.Untouched), Touched = new(LanePhase.Touched), MatchedPose = new(LanePhase.Matched);
        public long CurrentRequest => Phase switch { LanePhase.Matching => MatchRequest, LanePhase.Following => FollowRequest, _ => 0 };
    }

    /// <summary>A Pika session exists only on a touched channel.</summary>
    internal sealed record LanePika(bool Touched, string Session = "")
    {
        public static readonly LanePika Untouched = new(false), TouchedOnly = new(true);
    }

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
        public string RunId = "", FirstFailure = "", AcknowledgedEpisode = "", EndReason = "";
        private long sourceObservedAt;
        public Task? Publisher, Heartbeats, Primer;
        public OrderedSimulationObservation? Observation;
        private int phase = (int)GroupFollowPhase.Arming;
        public GroupFollowPhase Phase => (GroupFollowPhase)Volatile.Read(ref phase);
        public bool IsRetiring => Phase is GroupFollowPhase.Retiring or GroupFollowPhase.Retired;
        public bool CanPrime => Phase is GroupFollowPhase.Admitting or GroupFollowPhase.Activating;

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

    private readonly TimeProvider followClock;
    internal GroupSession? groupSimulation;
    /// <summary>Outer Follow lifecycle around a <see cref="GroupSession"/> (whose own phase is <see cref="GroupFollowPhase"/>).
    /// <code>
    ///   Idle/Summary/SetupFailed --arm--> Discovering --session installed--> Session --retired--> Summary
    ///   Discovering --setup error or cancel--> SetupFailed / Idle;  any idle stage --selection change--> Idle
    /// </code>
    /// Session is derived from <c>groupSimulation</c>; the other stages are one immutable outcome,
    /// so a setup failure and a retirement summary can no longer both be set.</summary>
    internal enum GroupFollowStage { Idle, Discovering, Session, Summary, SetupFailed }
    internal sealed record GroupFollowOutcome(GroupFollowStage Stage, string Summary = "");
    private static readonly GroupFollowOutcome GroupFollowIdle = new(GroupFollowStage.Idle);
    internal GroupFollowOutcome groupFollowOutcome = GroupFollowIdle;
    private GroupFollowStage GroupStage => GroupFollowActive ? GroupFollowStage.Session : Volatile.Read(ref groupFollowOutcome).Stage;
    private void SetGroupOutcome(GroupFollowOutcome next) => Volatile.Write(ref groupFollowOutcome, next);
    internal long groupInputEpoch;
    internal long groupControlEpoch;
    private bool GroupFollowActive => Volatile.Read(ref groupSimulation) is not null;
    private bool SimulationFollowingActive => GroupFollowActive;
    internal string groupFollowStatus = "Simulation only";
    internal string groupFollowDetail = "Choose the robots that should follow, then arm Follow simulation. Playback begins only when you press the editor's Play button.";
    private bool GroupFollowIncludes(string profileId) => Volatile.Read(ref groupSimulation)?.Lanes.Any(l => l.Profile.Id == profileId) == true;
    private bool GroupWaitingForUserPlay(GroupSession group) =>
        group.Phase is GroupFollowPhase.Arming or GroupFollowPhase.Armed && group.RunId.Length == 0 && GroupScopeCurrent(group);

    private static bool GroupBlocksControlEvent(string id) => id is "speed" or "recovery.run" or "recovery.card.run" or "robots.select" or "link" or "backend.restart.confirm" ||
        WorkspaceCommandPolicy.RequiresControls(id) && id is not ("simulation.toggle" or "simulation.match") ||
        id.StartsWith("global.robot.", StringComparison.Ordinal) ||
        id.StartsWith("teaching.", StringComparison.Ordinal) || id.StartsWith("pika.reference.", StringComparison.Ordinal) ||
        id.StartsWith("profiles.", StringComparison.Ordinal) && !id.StartsWith("profiles.select.", StringComparison.Ordinal);

    private string? GroupStartConflict()
    {
        if (CommandAuthority.Current.Lease is { } lease)
        {
            string robot = preferences.Robots?.FirstOrDefault(profile => profile.Id == lease.Scope.ProfileId)?.Name ?? "the robot";
            return $"Finish the current arm action for {robot} before arming Follow simulation.";
        }
        return teleoperating || followRequest != 0 || calibrationOpen || CurrentGuide?.Gate is not null ||
            CurrentRecovery is not null || PikaTeachingRecording
            ? "Finish the current control, recovery, or calibration action before arming Follow simulation." : null;
    }

    private GroupSession? CancelGroupSimulationFollowingInput(string reason = "Following canceled")
    {
        Interlocked.Increment(ref groupInputEpoch); // Also retires read-only discovery before a session exists.
        if (Volatile.Read(ref groupSimulation) is not { } group) return null;
        groupFollowStatus = reason;
        group.BeginRetirement();
        return group;
    }

    internal Task StopGroupSimulationFollowing(string reason, CancellationToken cancellation) =>
        CancelGroupSimulationFollowingInput(reason)?.Retired.Task.WaitAsync(cancellation) ?? Task.CompletedTask;

    internal async Task GroupFollowEvent(string action, CancellationToken cancellation)
    {
        if (action is not ("toggle" or "cancel")) throw new InvalidOperationException("Unknown grouped follow action.");
        if (GroupFollowActive)
        { await StopGroupSimulationFollowing("Stopping all selected followers · holding", cancellation); return; }
        if (action == "cancel") return;
        SetGroupOutcome(new(GroupFollowStage.Discovering));
        long inputEpoch = Interlocked.Read(ref groupInputEpoch);
        try
        {
            var profiles = SelectedFollowProfiles();
            var initial = GroupPreparation(profiles);
            // Inventory evidence can change within the same scene. Resolve it
            // through read-only discovery before applying the full arming gate.
            initial = initial with { Issues = initial.Issues.Where(issue => !issue.RefreshableSceneEvidence).ToArray() };
            if (!initial.Ready) throw new InvalidOperationException(initial.Summary);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(1500);
            var capabilities = await engine.RequestAsync("host.info", cancellation: deadline.Token);
            if (!capabilities.TryGetProperty("robotRuntimeTracksSupported", out var tracks) || !tracks.GetBoolean())
                throw new InvalidOperationException("Update Lucky Engine to use atomic multi-robot frame tracks.");
            var discovered = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: deadline.Token)).GetRawText())!.AsObject();
            if (inputEpoch != Interlocked.Read(ref groupInputEpoch)) throw new OperationCanceledException("Following preparation was canceled.");
            // Discovery is read-only. Recheck the whole group from one current
            // receipt before installing its immutable lanes or arming the engine.
            var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for controller feedback.");
            var preparation = GroupPreparation(profiles, receipt, discovered);
            if (!preparation.Ready) throw new InvalidOperationException(preparation.Summary);
            string sceneId = discovered["sceneId"]!.GetValue<string>();
            var lanes = new List<GroupLane>();
            string entity = profiles[0].RobotId;
            var model = discovered["robots"]?.AsArray().OfType<JsonObject>().SingleOrDefault(r => r["entityId"]?.GetValue<string>() == entity)
                ?? throw new InvalidOperationException("The selected scene world is unavailable.");
            foreach (var profile in profiles)
            {
                var device = receipt.State["devices"]!.AsArray().OfType<JsonObject>().Single(d => d["id"]?.GetValue<string>() == profile.CanDeviceId);
                long generation = device["generation"]!.GetValue<long>();
                var expected = new FollowControlApproval(receipt.BackendToken, profile.CanDeviceId, generation, profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution);
                if (!controlApprovals.TryGetValue(profile.Id, out var approval) || approval != expected)
                    throw new InvalidOperationException(profile.Name + ": use Enable robot and confirm its workspace before selecting it as a follower.");
                var robot = new RobotLink(sceneId, entity, model["joints"]!.AsArray(), device, profile.RobotJointPrefix);
                var pika = ProfilePikaRouting.Find(receipt.State, profile);
                double? closedReference = PikaClosedDisplayReference.TryLoad(engine.Start.SettingsDirectory, profile)?.AngleFor(profile, pika);
                lanes.Add(new(profile, robot, approval, closedReference,
                    new(profile, generation, closedReference, () => telemetry.Latest, followClock),
                    new(receipt.BackendToken, owner, profile, generation, Call)));
            }
            var group = new GroupSession(receipt.BackendToken, sceneId, entity, model["modelAssetId"]!.GetValue<string>(), model["modelRevision"]!.GetValue<string>(),
                lanes.ToArray(), inputEpoch, followClock);
            foreach (var lane in group.Lanes)
                lane.Diagnostics = new(Path.Combine(engine.Start.SettingsDirectory, "diagnostics", "simulation-follow"), group.PrepareId + "-" + lane.Profile.Id,
                    async diagnostic => { using var timeout = new CancellationTokenSource(400); await engine.ReportAsync("error", diagnostic, timeout.Token); });
            if (Interlocked.CompareExchange(ref groupSimulation, group, null) is not null) throw new InvalidOperationException("Another grouped follower is still retiring.");
            Interlocked.Increment(ref groupControlEpoch);
            using var cancelSetup = cancellation.Register(() =>
            { if (ReferenceEquals(Volatile.Read(ref groupSimulation), group)) CancelGroupSimulationFollowingInput("Preparation canceled · previous targets discarded"); });
            _ = Task.Run(() => RunGroupSimulation(group));
            await group.Armed.Task.WaitAsync(cancellation);
        }
        catch (OperationCanceledException) when (inputEpoch != Interlocked.Read(ref groupInputEpoch))
        { if (!GroupFollowActive && GroupStage == GroupFollowStage.Discovering) SetGroupOutcome(GroupFollowIdle); }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            // This includes failures before a group exists. The sidebar must
            // retain the actionable setup cause instead of claiming it is ready.
            string reason = RedactSecrets(error.Message, token, owner);
            SetGroupOutcome(new(GroupFollowStage.SetupFailed, "Follow not armed\n" + reason));
            groupFollowStatus = "Follow not armed";
            groupFollowDetail = reason;
            throw;
        }
    }

    private bool GroupScopeCurrent(GroupSession group) => ReferenceEquals(Volatile.Read(ref groupSimulation), group) &&
        !group.IsRetiring && group.InputEpoch == Interlocked.Read(ref groupInputEpoch) && group.Backend == token && group.Lanes.All(l =>
            preferences.Robots?.Any(p => p.Id == l.Profile.Id && p.CanDeviceId == l.Profile.CanDeviceId && p.RobotId == l.Profile.RobotId &&
                p.RobotJointPrefix == l.Profile.RobotJointPrefix && p.PikaDevice == l.Profile.PikaDevice && p.WslDistribution == l.Profile.WslDistribution) == true &&
            controlApprovals.TryGetValue(l.Profile.Id, out var approval) && approval == l.Approval);

    private void RequireGroupScope(GroupSession group)
    {
        if (GroupScopeCurrent(group)) return;
        if (group.IsRetiring) throw new OperationCanceledException("Grouped following was disarmed.", group.Stop.Token);
        throw new InvalidOperationException("A selected follower assignment, confirmation or backend changed; previous targets were discarded.");
    }

    /// <summary>One scope check and one immutable observation per lane, all from one receipt.</summary>
    private FollowLaneObservation[] ObserveGroup(GroupSession group, TelemetryReceipt? source = null)
    {
        RequireGroupScope(group);
        var receipt = source ?? telemetry.Latest ?? throw new InvalidOperationException("Controller feedback is unavailable.");
        return Array.ConvertAll(group.Lanes, lane => lane.Feedback.Read(group.Backend, receipt));
    }

    private FollowLaneObservation ObserveLane(GroupSession group, GroupLane lane)
    {
        RequireGroupScope(group);
        return lane.Feedback.Read(group.Backend);
    }

    private SimulationFollowHardware LaneHardware(GroupSession group, GroupLane lane, FollowLaneObservation observation, bool native = false) =>
        observation.Hardware(owner, group.Clock.GetTimestamp(), native ? lane.Arm.CurrentRequest : null,
            native ? Interlocked.Read(ref lane.Sequence) : 0);

    /// <summary>Fresh, healthy, owned hardware for every lane from one receipt.</summary>
    private SimulationFollowHardware[] GroupHardware(GroupSession group)
    {
        var observations = ObserveGroup(group);
        var hardware = new SimulationFollowHardware[group.Lanes.Length];
        for (int i = 0; i < hardware.Length; i++) hardware[i] = LaneHardware(group, group.Lanes[i], observations[i]);
        return hardware;
    }

    private enum FollowChannel { Arm, Pika }

    /// <summary>The single path for every arm and Pika Follow command. Match and
    /// Start belong to their group phases and mark the channel touched, so
    /// retirement holds it; Hold runs only while retiring.</summary>
    private async Task<JsonObject> GroupCommand(GroupSession group, GroupLane lane, FollowChannel channel, FollowOp op, JsonObject? fields = null,
        bool prevalidated = false)
    {
        GroupFollowPhase? phase = op switch
        {
            FollowOp.Match => GroupFollowPhase.Matching, FollowOp.Start => GroupFollowPhase.Admitting,
            FollowOp.Hold => GroupFollowPhase.Retiring, _ => null
        };
        if (phase is { } required) group.Require(required);
        if (op != FollowOp.Hold && !prevalidated) _ = LaneHardware(group, lane, ObserveLane(group, lane));
        if (op == FollowOp.Update) group.RequireTargetPublication();
        bool admission = op is FollowOp.Match or FollowOp.Start;
        if (channel == FollowChannel.Pika)
        {
            if (admission) lane.TouchPika();
            return await lane.Controller.Pika(op, fields);
        }
        if (admission) lane.TouchArm();
        try { return await lane.Controller.Arm(op, fields); }
        finally { if (admission) Volatile.Write(ref lane.ArmAdmissionRetiredTicks, group.Clock.GetTimestamp()); }
    }
}
