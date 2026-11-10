using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private enum GroupReleaseEvidence { Untouched, MeasuredIdle, HoldAcknowledged, Unverified }

    private sealed record GroupLaneRetirement(string Name, GroupReleaseEvidence Arm, GroupReleaseEvidence Pika)
    {
        public string Summary => $"{Name}: arm {Describe(Arm)}, gripper {Describe(Pika)}";

        private static string Describe(GroupReleaseEvidence evidence) => evidence switch
        {
            GroupReleaseEvidence.Untouched => "untouched",
            GroupReleaseEvidence.MeasuredIdle => "already idle",
            GroupReleaseEvidence.HoldAcknowledged => "hold acknowledged",
            _ => "hold unverified"
        };
    }

    private sealed record GroupRetirement(string Heading, IReadOnlyList<GroupLaneRetirement> Lanes)
    {
        public string Summary { get; } = string.Join("\n", new[] { Heading }.Concat(Lanes.Select(lane => lane.Summary)));
    }

    private async Task RunGroupSimulation(GroupSession group)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, group.Stop.Token);
        var ct = cancellation.Token;
        var prepared = new PreparedFollowRun(this, group);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (GroupStartConflict() is { } conflict) throw new InvalidOperationException(conflict);
            _ = GroupHardware(group);
            await prepared.ArmAsync(ct);
            group.Advance(GroupFollowPhase.Armed);
            follow.Report($"Armed · {group.Lanes.Length} robot(s) · press Play",
                "The editor is still in Edit mode. Your next Play pauses at its first frame, matches every selected robot and gripper, then starts the shared episode.");
            group.Armed.TrySetResult();
            group.AttachHeartbeats(GroupHeartbeats(group, ct));
            while (true)
            {
                _ = GroupHardware(group);
                if (await prepared.TryBindPlayAsync(ct)) break;
                await Task.Delay(TimeSpan.FromMilliseconds(80), group.Clock, ct);
            }
            if (GroupStartConflict() is { } matchingConflict) throw new InvalidOperationException(matchingConflict);
            group.Advance(GroupFollowPhase.Matching);
            var raw = await prepared.PauseForMatchingAsync(ct);
            follow.Report("Matching all selected robots to the first frame");
            await MatchGroupStart(group, raw, ct);
            raw = await prepared.ReadMatchingFrameAsync(ct);
            // Each lane's validated paused frame and hardware; activation must present exactly these.
            var starts = new (SimulationFollowSample Sample, SimulationFollowHardware Hardware)[group.Lanes.Length];
            var startHardware = GroupHardware(group);
            for (int i = 0; i < group.Lanes.Length; i++)
            {
                var lane = group.Lanes[i];
                var sample = SimulationFollowSample.Read(raw, lane.Robot.JointPrefix);
                SimulationFollowPolicy.ValidatePausedStart(sample, lane.First!);
                SimulationFollowPolicy.ValidateStart(sample, sample.Identity, startHardware[i], group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
                starts[i] = (sample, startHardware[i]);
                lane.BeginExecution(new(sample, startHardware[i], lane.Profile.Speed / 100));
            }
            var initial = await prepared.OpenFramesAsync(raw, ct);
            AppendGroupFrames(group, initial, initial: true);
            await prepared.MarkReadyAsync(ct);
            await AdmitGroup(group, ct);
            group.Advance(GroupFollowPhase.Activating);
            var activation = await prepared.ActivateAsync(ct);
            long activationObserved = group.Clock.GetTimestamp();
            var activationHardware = GroupHardware(group);
            for (int i = 0; i < group.Lanes.Length; i++)
                SimulationFollowPolicy.ValidateActivation(starts[i].Sample, starts[i].Hardware,
                    SimulationFollowSample.Read(activation, group.Lanes[i].Robot.JointPrefix), activationHardware[i],
                    group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
            var observation = new OrderedSimulationObservation(SimulationFollowSample.Read(activation, group.Lanes[0].Robot.JointPrefix));
            group.BeginObservation(observation);
            group.Advance(GroupFollowPhase.DrainingPrimer);
            if (group.Primer is { } primer) await primer;
            ct.ThrowIfCancellationRequested();
            group.Require(GroupFollowPhase.DrainingPrimer);
            group.RecordValidatedSource(activationObserved);
            group.RequireOrderedSourceLease();
            group.Advance(GroupFollowPhase.Following);
            group.AttachPublisher(PublishGroupTargets(group, ct));
            while (!ct.IsCancellationRequested)
            {
                ulong after; int space;
                lock (group.Gate)
                {
                    after = group.Lanes[0].Execution!.Path.LastCursor;
                    space = group.Lanes.Min(l => OrderedSimulationPath.MaximumFrames - l.Execution!.Path.FrameCount);
                }
                if (space <= 0) throw new InvalidOperationException("The shared physical follower buffer is full; no source path was discarded.");
                long began = group.Clock.GetTimestamp();
                try
                {
                    raw = await prepared.ReadFramesAsync(after, space, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    group.RequireOrderedSourceLease();
                    if (!GroupScopeCurrent(group) || group.Publisher is not { IsCompleted: false }) throw;
                    // A lost reply consumes no frames and grants no new lease.
                    follow.Report("Simulation view delayed · retrying retained group frames");
                    continue;
                }
                var sample = SimulationFollowSample.Read(raw, group.Lanes[0].Robot.JointPrefix);
                bool paused = raw["sourcePausedForFollower"]?.GetValue<bool>() == true;
                observation.Validate(sample, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), group.Clock.GetElapsedTime(began).TotalMilliseconds,
                    paused, raw["episodeComplete"]?.GetValue<bool>() == true, raw["episodeId"]?.GetValue<string>() ?? "", group.AcknowledgedEpisode.Length > 0);
                AppendGroupFrames(group, raw);
                if (await CompleteGroupEpisode(group, prepared, ct)) break;
                int buffered;
                lock (group.Gate) buffered = group.Lanes.Max(l => l.Execution!.Path.FrameCount);
                if (!paused && buffered >= OrderedSimulationPath.HighWaterFrames)
                    await prepared.SetSourcePausedAsync(true, ct);
                else if (paused && buffered <= OrderedSimulationPath.LowWaterFrames)
                    await prepared.SetSourcePausedAsync(false, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || group.IsRetiring) { }
        catch (Exception error) { FailGroup(group, error); group.Armed.TrySetException(error); }
        finally
        {
            try
            {
                group.BeginRetirement();
                // Drain all in-flight admissions/publications before the final holds.
                // No replacement group may acquire these devices during this barrier.
                foreach (var worker in new[] { group.Primer, group.Publisher, group.Heartbeats })
                    if (worker is not null) try { await worker; } catch (Exception error) { FailGroup(group, error); }
                var lanes = await ReleaseGroup(group);
                await prepared.RetireAsync();
                string heading = group.FirstFailure.Length > 0 ? "Following stopped\n" + group.FirstFailure :
                    group.EndReason.Length > 0 ? group.EndReason : "Following stopped";
                follow.Report(heading);
                follow.Summarize(new GroupRetirement(heading, lanes).Summary);
                foreach (var lane in group.Lanes)
                    if (lane.Diagnostics is { } diagnostics)
                        try { await diagnostics.Completion.WaitAsync(TimeSpan.FromSeconds(1)); }
                        catch (Exception error) { Trace.TraceWarning("Follow diagnostics writer: {0}", error.GetType().Name); }
            }
            finally
            {
                group.Armed.TrySetCanceled(group.Stop.Token);
                group.Advance(GroupFollowPhase.Retired);
                follow.Retire(group);
                group.Retired.TrySetResult();
            }
        }
    }

    private const double MatchSettleMilliseconds = 100, MatchTimeoutSeconds = 180;
    private const double AdmissionTimeoutMilliseconds = 400, PrimerTimeoutSeconds = 4;

    private async Task MatchGroupStart(GroupSession group, JsonObject raw, CancellationToken ct)
    {
        var identity = new SimulationFollowIdentity(group.Scene, group.RunId, group.Entity, group.Asset, group.Revision);
        var candidates = GroupHardware(group);
        for (int i = 0; i < group.Lanes.Length; i++)
        {
            var lane = group.Lanes[i];
            var sample = SimulationFollowSample.Read(raw, lane.Robot.JointPrefix);
            SimulationFollowPolicy.ValidateCandidate(sample, identity, candidates[i], group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
            lane.RecordFirst(sample);
            if (!SimulationFollowPolicy.MatchesStart(sample.CommandDegrees, sample.MeasuredDegrees, sample.CommandOpeningMm, sample.MeasuredOpeningMm))
                throw new InvalidOperationException(lane.Profile.Name + ": simulated first-frame controls and measured pose disagree.");
        }
        foreach (var lane in group.Lanes)
        {
            var hardware = LaneHardware(group, lane, ObserveLane(group, lane)); var first = lane.First!;
            if (!SimulationFollowPolicy.JointsMatchStart(first.CommandDegrees, hardware.JointDegrees))
            {
                var admitted = await GroupCommand(group, lane, lane.Controller.MatchArm(first.CommandDegrees));
                lane.MatchRequested(admitted["request_id"]?.GetValue<long>() ?? throw new InvalidOperationException("Start matching has no arm request identity."));
            }
            if (!SimulationFollowPolicy.OpeningMatchesStart(first.CommandOpeningMm, hardware.GripperOpeningMm))
                await GroupCommand(group, lane, lane.Controller.MatchPika(PikaProtocol.Angle(first.CommandOpeningMm)));
        }
        // Settling is local to matching: it starts at the first fresh at-pose
        // receipt and needs newer controller and Pika feedback after the dwell.
        var settled = new (long At, long Sequence, double Feedback)[group.Lanes.Length];
        long began = group.Clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var observations = ObserveGroup(group, telemetry.Latest!);
            bool all = true;
            for (int i = 0; i < group.Lanes.Length; i++)
            {
                var lane = group.Lanes[i]; var observation = observations[i];
                var hardware = LaneHardware(group, lane, observation);
                bool atPose = SimulationFollowPolicy.MatchesStart(lane.First!.CommandDegrees, hardware.JointDegrees, lane.First.CommandOpeningMm, hardware.GripperOpeningMm) &&
                    !observation.Motion.Active && observation.PikaIdle && Math.Abs(observation.PikaSpeed) <= PikaProtocol.StationarySpeedRadiansPerSecond &&
                    observation.ArmStationary(group.Clock.GetTimestamp());
                double feedback = observation.PikaFeedbackTime;
                if (!atPose) settled[i] = default;
                else if (settled[i].At == 0) settled[i] = (group.Clock.GetTimestamp(), observation.Sequence, feedback);
                all &= atPose && group.Clock.GetElapsedTime(settled[i].At).TotalMilliseconds >= MatchSettleMilliseconds &&
                    observation.Sequence > settled[i].Sequence && feedback > settled[i].Feedback;
            }
            if (all) { foreach (var lane in group.Lanes) lane.Matched(); return; }
            if (group.Clock.GetElapsedTime(began).TotalSeconds > MatchTimeoutSeconds) throw new InvalidOperationException("Not all selected robots reached and settled at the first frame.");
            await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
        }
    }

    private async Task AdmitGroup(GroupSession group, CancellationToken ct)
    {
        if (GroupStartConflict() is { } conflict) throw new InvalidOperationException(conflict);
        group.Advance(GroupFollowPhase.Admitting);
        follow.Report("Starting selected followers · simulation remains paused");
        foreach (var lane in group.Lanes)
        {
            var admitted = await GroupCommand(group, lane, lane.Controller.StartArm());
            SimulationTrajectoryTransport.ValidateAcknowledgement(admitted);
            long request = admitted["request_id"]?.GetValue<long>() ?? 0;
            // Record an acknowledged request before validating it, so retirement can hold it.
            if (request > 0) lane.FollowAdmitted(request, group.Clock.GetTimestamp(), telemetry.Latest?.Sequence ?? 0);
            if (request <= 0 || admitted["trajectory_follow"]?.GetValue<bool>() != true) throw new InvalidOperationException(lane.Profile.Name + ": native following was not acknowledged.");
            if (group.Primer is null) group.AttachPrimer(PrimeGroup(group, ct));
            var pika = await GroupCommand(group, lane, lane.Controller.StartPika());
            lane.PikaFollowing(pika["follow_session"]?.GetValue<string>() ?? throw new InvalidOperationException("Pika did not acknowledge its following session."));
        }
        long began = group.Clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var observations = ObserveGroup(group, telemetry.Latest!);
            bool all = true;
            for (int i = 0; i < group.Lanes.Length; i++)
            {
                var lane = group.Lanes[i];
                _ = LaneHardware(group, lane, observations[i]);
                if (lane.Arm is not LaneArm.Following arm)
                    throw new InvalidOperationException(lane.Profile.Name + ": native following admission has no valid request acknowledgement.");
                all &= observations[i].AdmissionReady(owner, arm.FollowRequest, lane.Sequence,
                    arm.AdmissionTicks, arm.AdmissionSequence, group.Clock.GetTimestamp());
            }
            if (all) return;
            if (group.Clock.GetElapsedTime(began).TotalMilliseconds > AdmissionTimeoutMilliseconds) throw new InvalidOperationException("A selected controller did not publish its fresh admitted reference in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), group.Clock, ct);
        }
    }

    private async Task PrimeGroup(GroupSession group, CancellationToken ct)
    {
        long began = group.Clock.GetTimestamp();
        try
        {
            while (group.CanPrime && !ct.IsCancellationRequested)
            {
                if (group.Clock.GetElapsedTime(began).TotalSeconds > PrimerTimeoutSeconds) throw new InvalidOperationException("Grouped first-frame admission timed out; no playback was started.");
                await SendGroupTargets(group, group.Lanes.Where(l => l.Arm is LaneArm.Following).ToDictionary(l => l.Profile.Id,
                    l => new OrderedSimulationTarget(l.First!.CommandDegrees, l.First.CommandOpeningMm)), ct, priming: true);
                await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || group.IsRetiring) { }
        catch (Exception error) { FailGroup(group, error); group.BeginRetirement(); }
    }

    private void AppendGroupFrames(GroupSession group, JsonObject response, bool initial = false)
    {
        var frames = GroupSimulationFrames.Read(response, group.Lanes.Select(l => l.Profile.Id).ToArray());
        lock (group.Gate)
        {
            group.Require(initial ? GroupFollowPhase.Matching : GroupFollowPhase.Following);
            if (!initial) group.RequireOrderedSourceLease();
            foreach (var lane in group.Lanes)
            {
                var sample = SimulationFollowSample.Read(response, lane.Robot.JointPrefix);
                lane.RecordSample(sample);
                if (!sample.BindsTo(lane.First!)) throw new InvalidOperationException("A selected arm's source model mapping changed.");
                var batch = frames[lane.Profile.Id];
                if (initial && (batch.Count != 1 || batch[0].Cursor != 1 || batch[0].PhysicsTimeSeconds != lane.First!.SimTimeSeconds ||
                    batch[0].CommandDegrees.Zip(lane.First.CommandDegrees, (a, b) => Math.Abs(a - b)).Any(e => e > 1e-7) ||
                    Math.Abs(batch[0].CommandOpeningMm - lane.First.CommandOpeningMm) > 1e-6))
                    throw new InvalidOperationException("The grouped tape did not preserve every selected robot's exact first frame.");
            }
            try { foreach (var lane in group.Lanes) lane.Execution!.Path.Append(frames[lane.Profile.Id]); }
            catch { group.BeginRetirement(); throw; } // No publisher may consume a partially rejected atomic batch.
            if (!initial) group.RecordValidatedSource(group.Clock.GetTimestamp());
        }
    }

    private async Task PublishGroupTargets(GroupSession group, CancellationToken ct)
    {
        try
        {
            var cadence = new SimulationPublishCadence(group.Clock.GetTimestamp(), group.Clock.TimestampFrequency, TimeSpan.FromMilliseconds(30));
            while (!ct.IsCancellationRequested)
            {
                group.RequireOrderedSourceLease();
                var source = telemetry.Latest!;
                var observations = ObserveGroup(group, source);
                var hardware = new SimulationFollowHardware[group.Lanes.Length];
                for (int i = 0; i < group.Lanes.Length; i++) hardware[i] = LaneHardware(group, group.Lanes[i], observations[i], native: true);
                var targets = new Dictionary<string, OrderedSimulationTarget>(StringComparer.Ordinal);
                lock (group.Gate)
                {
                    group.Barrier.Observe(group.Lanes.Select(l => l.Execution!).ToArray());
                    for (int i = 0; i < group.Lanes.Length; i++)
                    {
                        var lane = group.Lanes[i]; var observation = observations[i];
                        var measured = lane.RecordStep(new(hardware[i], observation.ArmStationary(group.Clock.GetTimestamp()),
                            observation.PikaSpeed, observation.PikaFeedbackTime));
                        try
                        {
                            targets[lane.Profile.Id] = lane.Execution!.Step(group.Clock.GetTimestamp() / (double)group.Clock.TimestampFrequency,
                                measured.Hardware, measured.Stationary, measured.GripperSpeed, measured.GripperFeedback, group.Barrier.MayStart(lane.Execution));
                        }
                        catch (Exception error) { throw new InvalidOperationException(lane.Profile.Name + ": " + error.Message, error); }
                    }
                    follow.Report($"Following {group.Lanes.Length} robots · {group.Lanes.Select(l => l.Execution!.ActivePhase?.End.Phase).FirstOrDefault(p => p is not null) ?? "holding"}",
                        "All selected arms share phase barriers. Faster arms hold their measured endpoint until their partners arrive; the next episode waits for every arm and gripper.");
                }
                await SendGroupTargets(group, targets, ct);
                await cadence.WaitAfterPublication(group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || group.IsRetiring) { }
        catch (Exception error) { FailGroup(group, error); group.BeginRetirement(); }
    }

    private async Task SendGroupTargets(GroupSession group, IReadOnlyDictionary<string, OrderedSimulationTarget> targets, CancellationToken ct, bool priming = false)
    {
        ct.ThrowIfCancellationRequested();
        var attempts = new List<(GroupLane Lane, SimulationPublishDiagnostics.Attempt Timing)>();
        var writes = new List<Task>();
        try
        {
            if (!priming) group.Require(GroupFollowPhase.Following);
            else if (!group.CanPrime) return;
            // Every lane is checked from one receipt before any write starts.
            var observations = ObserveGroup(group, telemetry.Latest);
            for (int i = 0; i < group.Lanes.Length; i++) _ = LaneHardware(group, group.Lanes[i], observations[i]);
            // Every target lane must own an admitted follow request before any write starts.
            var followRequests = group.Lanes.Where(l => targets.ContainsKey(l.Profile.Id)).ToDictionary(l => l.Profile.Id,
                l => l.Arm is LaneArm.Following following ? following.FollowRequest
                    : throw new InvalidOperationException(l.Profile.Name + ": follow admission is missing; no follower target was sent."));
            foreach (var lane in group.Lanes.Where(l => targets.ContainsKey(l.Profile.Id)))
            {
                var target = targets[lane.Profile.Id];
                long sequence = lane.NextSequence();
                var timing = lane.Publications.Begin(sequence, Stopwatch.GetTimestamp());
                attempts.Add((lane, timing));
                writes.Add(lane.Publications.Observe(timing, GroupCommand(group, lane,
                    lane.Controller.UpdateArm(followRequests[lane.Profile.Id], sequence, target.JointDegrees), prevalidated: true), arm: true));
                if (lane.Pika.Session is { Length: > 0 } pikaSession)
                    writes.Add(lane.Publications.Observe(timing, ObservePika(lane, GroupCommand(group, lane,
                        lane.Controller.UpdatePika(pikaSession, sequence, target.OpeningMm), prevalidated: true)), arm: false));
            }
            await Task.WhenAll(writes);
        }
        catch
        {
            // A synchronous payload/evidence failure must still join every
            // write already started before retirement can issue holds.
            try { await Task.WhenAll(writes); } catch { }
            throw;
        }
        finally
        {
            foreach (var (lane, timing) in attempts) lane.Publications.Complete(timing);
        }
        static async Task ObservePika(GroupLane lane, Task operation)
        {
            try { await operation; }
            catch (PikaFollowRejectedException error)
            { lane.TryRecordRejectedPika(error.Audit); throw; }
        }
    }

    private async Task GroupHeartbeats(GroupSession group, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A match (pose) request and a follow request both need a live lease.
                if (telemetry.Latest is { } source && group.Lanes.Any(l => l.Arm.CurrentRequest > 0))
                {
                    RequireGroupScope(group);
                    foreach (var lane in group.Lanes)
                        if (lane.Arm.CurrentRequest is > 0 and var request &&
                            lane.Feedback.Read(group.Backend, source).Motion is { Active: true } motion && motion.RequestId == request)
                            await lane.Controller.Heartbeat(request, ct);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || group.IsRetiring) { }
        catch (Exception error) { FailGroup(group, error); group.BeginRetirement(); }
    }

    private async Task<bool> CompleteGroupEpisode(GroupSession group, PreparedFollowRun prepared, CancellationToken ct)
    {
        OrderedSimulationFrame? complete; bool missed;
        lock (group.Gate)
        {
            if (group.Lanes.Any(l => !l.Execution!.Path.Drained || l.Execution.CompletedCursor != l.Execution.Path.LastCursor)) return false;
            complete = group.Lanes[0].Execution!.Path.LastSource;
            missed = group.Lanes.Any(l => l.Execution!.MissedGrasp);
            if (group.Lanes.Any(l => l.Execution!.Path.LastSource?.Cursor != complete?.Cursor || l.Execution.Path.LastSource?.EpisodeId != complete?.EpisodeId))
                throw new InvalidOperationException("Selected tracks disagreed at the shared episode boundary.");
        }
        if (complete is not { EpisodeComplete: true, EpisodeId.Length: > 0 }) return false;
        if (missed)
        {
            var observations = ObserveGroup(group, telemetry.Latest!);
            for (int i = 0; i < group.Lanes.Length; i++)
                if (!group.Lanes[i].Execution!.ReturnedHome(LaneHardware(group, group.Lanes[i], observations[i], native: true),
                    observations[i].ArmStationary(group.Clock.GetTimestamp()), observations[i].PikaSpeed))
                    throw new InvalidOperationException("A missed grasp finished without every selected robot at its verified home and opened-jaw endpoint.");
            group.EndWith("Grasp not confirmed · all selected robots finished at home");
            return true;
        }
        if (!complete.EpisodeSucceeded) throw new InvalidOperationException("The source episode failed; all selected followers stopped.");
        if (group.AcknowledgedEpisode == complete.EpisodeId) return false;
        await prepared.AcknowledgeEpisodeAsync(complete, ct);
        group.AcknowledgeEpisode(complete.EpisodeId);
        return false;
    }

    private async Task<GroupLaneRetirement[]> ReleaseGroup(GroupSession group)
    {
        group.Require(GroupFollowPhase.Retiring);
        var outcomes = await Task.WhenAll(group.Lanes.Select(async lane =>
        {
            // Start both channels independently. A failed lookup or hold on
            // one channel cannot suppress its partner or another robot.
            var arm = ReleaseArm(lane);
            var pika = ReleasePika(lane);
            return new GroupLaneRetirement(lane.Profile.Name, await arm, await pika);
        }));
        return outcomes;

        async Task<GroupReleaseEvidence> ReleaseArm(GroupLane lane)
        {
            var state = lane.Arm;
            if (state is LaneArm.UntouchedArm) return GroupReleaseEvidence.Untouched;
            if (group.Backend != token) return GroupReleaseEvidence.Unverified;
            try
            {
                long request = state.CurrentRequest;
                var receipt = telemetry.Latest;
                var device = receipt?.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == lane.Robot.DeviceId);
                if (device?["generation"]?.GetValue<long>() != lane.Robot.Generation)
                    throw new InvalidOperationException(lane.Profile.Name + ": arm hold could not be verified on the original CAN connection.");
                long retired = lane.ArmAdmissionRetiredTicks;
                bool measuredIdle = receipt!.BackendToken == group.Backend && retired > 0 &&
                    receipt.CapturedTicks(group.Clock.TimestampFrequency) > retired &&
                    receipt.HasFreshDevice(device, group.Clock.GetTimestamp()) && device["motion"]?["device_id"]?.GetValue<string>() == lane.Robot.DeviceId &&
                    device["motion"]?["generation"]?.GetValue<long>() == lane.Robot.Generation && device["motion"]?["active"]?.GetValue<bool>() == false &&
                    (request == 0 || device["motion"]?["request_id"]?.GetValue<long>() == request);
                if (measuredIdle) return GroupReleaseEvidence.MeasuredIdle;
                await GroupCommand(group, lane, lane.Controller.HoldArm(request > 0 ? request : null));
                return GroupReleaseEvidence.HoldAcknowledged;
            }
            catch (Exception error) { FailGroup(group, error); return GroupReleaseEvidence.Unverified; }
        }
        async Task<GroupReleaseEvidence> ReleasePika(GroupLane lane)
        {
            if (!lane.Pika.Touched) return GroupReleaseEvidence.Untouched;
            if (group.Backend != token) return GroupReleaseEvidence.Unverified;
            try
            {
                var receipt = telemetry.Latest;
                if (receipt is null || ProfilePikaRouting.Find(receipt.State, lane.Profile)?["owner"]?.GetValue<string>() != owner)
                    throw new InvalidOperationException(lane.Profile.Name + ": gripper hold could not be verified for its original assignment and owner.");
                await GroupCommand(group, lane, lane.Controller.HoldPika());
                return GroupReleaseEvidence.HoldAcknowledged;
            }
            catch (Exception error) { FailGroup(group, error); return GroupReleaseEvidence.Unverified; }
        }
    }
}
