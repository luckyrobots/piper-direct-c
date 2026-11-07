using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

internal enum GroupFollowPhase { Arming, Armed, Matching, Admitting, Activating, DrainingPrimer, Following, Retiring, Retired }

public sealed partial class PipersPlugin
{
    private sealed class GroupLane(RobotProfile profile, RobotLink robot, FollowControlApproval approval, double? reference,
        FollowLaneTelemetry feedback, FollowLaneController controller)
    {
        public readonly RobotProfile Profile = profile;
        public readonly RobotLink Robot = robot;
        public readonly FollowControlApproval Approval = approval;
        public readonly double? ClosedReference = reference;
        public readonly FollowLaneTelemetry Feedback = feedback;
        public readonly FollowLaneController Controller = controller;
        public SimulationFollowSample? First, LastSample;
        public SimulationFollowPolicy? Policy;
        public OrderedSimulationExecution? Execution;
        public SimulationFollowDiagnostics? Diagnostics;
        public readonly SimulationPublishDiagnostics Publications = new();
        public PikaFollowAudit? RejectedPikaAudit;
        public SimulationFollowHardware? LastHardware;
        public bool LastStationary;
        public double LastGripperSpeed, LastGripperFeedback;
        public long ArmRequest, Sequence, AdmissionTicks, AdmissionSequence, ArmAdmissionRetiredTicks;
        public string PikaSession = "";
        public bool ArmTouched, PikaTouched;
        public double SettledFeedback;
        public long SettledAt, SettledSequence;
    }

    private sealed class GroupSession(string backend, string sceneId, string entityId, string asset, string revision, GroupLane[] lanes, long inputEpoch,
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
        public double OriginalScale = 1;
        public long EnvelopeTicks;
        public Task? Publisher, Heartbeats, Primer;
        public OrderedSimulationObservation? Observation;
        private int phase = (int)GroupFollowPhase.Arming;
        public GroupFollowPhase Phase => (GroupFollowPhase)Volatile.Read(ref phase);
        public bool IsRetiring => Phase is GroupFollowPhase.Retiring or GroupFollowPhase.Retired;
        public bool CanPrime => Phase is GroupFollowPhase.Admitting or GroupFollowPhase.Activating;

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
            if (current == GroupFollowPhase.Following) RequireOrderedSourceLease(this);
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
    private GroupSession? groupSimulation;
    private GroupRetirement? groupRetirement;
    private long groupInputEpoch;
    private long groupControlEpoch;
    private bool GroupFollowActive => Volatile.Read(ref groupSimulation) is not null;
    private bool SimulationFollowingActive => GroupFollowActive;
    private string GroupFollowStatus = "Simulation only";
    private string GroupFollowDetail = "Choose the robots that should follow, then arm Follow simulation. Playback begins only when you press the editor's Play button.";
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
        return teleoperating || followRequest != 0 || calibrationOpen || handGuideRecovery is not null ||
            CurrentRecovery is not null || PikaTeachingRecording
            ? "Finish the current control, recovery, or calibration action before arming Follow simulation." : null;
    }

    private GroupSession? CancelGroupSimulationFollowingInput(string reason = "Following canceled")
    {
        Interlocked.Increment(ref groupInputEpoch); // Also retires read-only discovery before a session exists.
        if (Volatile.Read(ref groupSimulation) is not { } group) return null;
        GroupFollowStatus = reason;
        group.BeginRetirement();
        return group;
    }

    private Task StopGroupSimulationFollowing(string reason, CancellationToken cancellation) =>
        CancelGroupSimulationFollowingInput(reason)?.Retired.Task.WaitAsync(cancellation) ?? Task.CompletedTask;

    private static void ValidateSimulationTimeMode(JsonObject snapshot)
    {
        if (snapshot["timeMode"]?.GetValue<string>() is not ("deterministic-realtime" or "realtime"))
            throw new InvalidOperationException("Following requires Deterministic real-time or Real-time scene mode. Select one in scene settings before following the real robot.");
    }

    private async Task GroupFollowEvent(string action, CancellationToken cancellation)
    {
        if (action is not ("toggle" or "cancel")) throw new InvalidOperationException("Unknown grouped follow action.");
        if (GroupFollowActive)
        { await StopGroupSimulationFollowing("Stopping all selected followers · holding", cancellation); return; }
        if (action == "cancel") return;
        groupFollowFailure = "";
        Volatile.Write(ref groupRetirement, null);
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
                double? closedReference = null;
                try
                {
                    string distribution = OperatingSystem.IsWindows() ? profile.WslDistribution : "";
                    string path = Path.Combine(engine.Start.SettingsDirectory, "gripper-reference", profile.Id + ".json");
                    if (File.Exists(path))
                    {
                        var saved = JsonSerializer.Deserialize<PikaClosedDisplayReference>(File.ReadAllText(path), PluginProtocol.Json);
                        if (saved?.Matches(profile.Id, profile.PikaDevice, distribution, pika) == true) closedReference = saved.MotorAngle;
                    }
                }
                catch (Exception error) when (error is IOException or JsonException) { }
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
        catch (OperationCanceledException) when (inputEpoch != Interlocked.Read(ref groupInputEpoch)) { }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            // This includes failures before a group exists. The sidebar must
            // retain the actionable setup cause instead of claiming it is ready.
            string reason = error.Message;
            foreach (string secret in new[] { token, owner })
                if (secret.Length > 0) reason = reason.Replace(secret, "[redacted]", StringComparison.Ordinal);
            groupFollowFailure = "Follow not armed\n" + reason;
            GroupFollowStatus = "Follow not armed";
            GroupFollowDetail = reason;
            throw;
        }
    }

    private bool GroupScopeCurrent(GroupSession group) => ReferenceEquals(Volatile.Read(ref groupSimulation), group) &&
        !group.IsRetiring && group.InputEpoch == Interlocked.Read(ref groupInputEpoch) && group.Backend == token && group.Lanes.All(l =>
            preferences.Robots?.Any(p => p.Id == l.Profile.Id && p.CanDeviceId == l.Profile.CanDeviceId && p.RobotId == l.Profile.RobotId &&
                p.RobotJointPrefix == l.Profile.RobotJointPrefix && p.PikaDevice == l.Profile.PikaDevice && p.WslDistribution == l.Profile.WslDistribution) == true &&
            controlApprovals.TryGetValue(l.Profile.Id, out var approval) && approval == l.Approval);

    private FollowLaneObservation GroupObservation(GroupSession group, GroupLane lane, TelemetryReceipt? source = null)
    {
        if (!GroupScopeCurrent(group))
        {
            if (group.IsRetiring) throw new OperationCanceledException("Grouped following was disarmed.", group.Stop.Token);
            throw new InvalidOperationException("A selected follower assignment, confirmation or backend changed; previous targets were discarded.");
        }
        return lane.Feedback.Read(group.Backend, source);
    }

    private SimulationFollowHardware GroupHardware(GroupSession group, GroupLane lane, bool native = false, TelemetryReceipt? source = null)
    {
        var observation = GroupObservation(group, lane, source);
        return observation.Hardware(owner, group.Clock.GetTimestamp(), native ? lane.ArmRequest : null,
            native ? Interlocked.Read(ref lane.Sequence) : 0);
    }

    private async Task<JsonObject> GroupEngine(GroupSession group, string method, string? action, CancellationToken cancellation,
        JsonObject? properties = null, bool release = false)
    {
        cancellation.ThrowIfCancellationRequested();
        GroupFollowPhase? required = release ? GroupFollowPhase.Retiring : action switch
        {
            "arm-next-play" => GroupFollowPhase.Arming,
            "pause" or "claim-episode-follower" or "start" or "mark-start-ready" => GroupFollowPhase.Matching,
            "activate-start" => GroupFollowPhase.Activating,
            "pause-follower-source" or "resume-follower-source" or "ack-episode" => GroupFollowPhase.Following,
            _ when method == "robot.runtime.frames" => GroupFollowPhase.Following,
            _ => null
        };
        if (required is { } phase) group.Require(phase);
        if (!release && !GroupScopeCurrent(group)) throw new InvalidOperationException("Grouped following was disarmed.");
        var request = new JsonObject { ["sceneId"] = group.Scene, ["entityId"] = group.Entity, ["expectedModelAssetId"] = group.Asset,
            ["expectedModelRevision"] = group.Revision, ["prepareId"] = group.PrepareId };
        if (group.RunId.Length > 0) request["runId"] = group.RunId;
        if (action is not null) request["action"] = action;
        if (action == "arm-next-play") request["activationHandshake"] = true;
        if (properties is not null) foreach (var property in properties) request[property.Key] = property.Value?.DeepClone();
        using var timeout = release ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : CancellationTokenSource.CreateLinkedTokenSource(cancellation, group.Stop.Token);
        timeout.CancelAfter(method == "robot.runtime.frames" && action != "start" ? 150 : 1000);
        var reply = await engine.RequestAsync(method, request, timeout.Token);
        if (!release && !GroupScopeCurrent(group)) throw new OperationCanceledException("Following was canceled while the editor request retired.");
        return JsonNode.Parse(reply.GetRawText())!.AsObject();
    }

    private async Task<JsonObject> GroupArmCommand(GroupSession group, GroupLane lane, FollowArmAction action, JsonObject? fields = null)
    {
        switch (action)
        {
            case FollowArmAction.MatchPose: group.Require(GroupFollowPhase.Matching); break;
            case FollowArmAction.Start: group.Require(GroupFollowPhase.Admitting); break;
            case FollowArmAction.Hold: group.Require(GroupFollowPhase.Retiring); break;
        }
        if (action != FollowArmAction.Hold) _ = GroupHardware(group, lane);
        if (action == FollowArmAction.Update) group.RequireTargetPublication();
        bool admission = action is FollowArmAction.MatchPose or FollowArmAction.Start;
        if (admission) lane.ArmTouched = true;
        try { return await lane.Controller.Arm(action, fields); }
        finally { if (admission) Volatile.Write(ref lane.ArmAdmissionRetiredTicks, group.Clock.GetTimestamp()); }
    }

    private async Task<JsonObject> GroupPika(GroupSession group, GroupLane lane, FollowPikaAction action, JsonObject? fields = null)
    {
        switch (action)
        {
            case FollowPikaAction.MatchOpening: group.Require(GroupFollowPhase.Matching); break;
            case FollowPikaAction.Start: group.Require(GroupFollowPhase.Admitting); break;
            case FollowPikaAction.Hold: group.Require(GroupFollowPhase.Retiring); break;
        }
        if (action != FollowPikaAction.Hold) _ = GroupHardware(group, lane);
        if (action == FollowPikaAction.Update) group.RequireTargetPublication();
        if (action is FollowPikaAction.MatchOpening or FollowPikaAction.Start) lane.PikaTouched = true;
        return await lane.Controller.Pika(action, fields);
    }
}
