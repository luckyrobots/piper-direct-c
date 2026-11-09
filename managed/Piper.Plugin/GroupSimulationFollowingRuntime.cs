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
            foreach (var lane in group.Lanes) _ = GroupHardware(group, lane);
            await prepared.ArmAsync(ct);
            group.Advance(GroupFollowPhase.Armed);
            GroupFollowStatus = $"Armed · {group.Lanes.Length} robot(s) · press Play";
            GroupFollowDetail = "The editor is still in Edit mode. Your next Play pauses at its first frame, matches every selected robot and gripper, then starts the shared episode.";
            group.Armed.TrySetResult();
            group.Heartbeats = GroupHeartbeats(group, ct);
            while (true)
            {
                foreach (var lane in group.Lanes) _ = GroupHardware(group, lane);
                if (await prepared.TryBindPlayAsync(ct)) break;
                await Task.Delay(TimeSpan.FromMilliseconds(80), group.Clock, ct);
            }
            if (GroupStartConflict() is { } matchingConflict) throw new InvalidOperationException(matchingConflict);
            group.Advance(GroupFollowPhase.Matching);
            var raw = await prepared.PauseForMatchingAsync(ct);
            GroupFollowStatus = "Matching all selected robots to the first frame";
            await MatchGroupStart(group, raw, ct);
            raw = await prepared.ReadMatchingFrameAsync(ct);
            var starts = new (SimulationFollowSample Sample, SimulationFollowHardware Hardware)[group.Lanes.Length];
            for (int i = 0; i < group.Lanes.Length; i++)
            {
                var lane = group.Lanes[i];
                var sample = SimulationFollowSample.Read(raw, lane.Robot.JointPrefix);
                SimulationFollowPolicy.ValidatePausedStart(sample, lane.First!);
                var hardware = GroupHardware(group, lane);
                SimulationFollowPolicy.ValidateStart(sample, sample.Identity, hardware, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
                starts[i] = (sample, hardware);
                lane.Execution = new(sample, hardware, lane.Profile.Speed / 100);
            }
            var initial = await prepared.OpenFramesAsync(raw, ct);
            AppendGroupFrames(group, initial, initial: true);
            await prepared.MarkReadyAsync(ct);
            await AdmitGroup(group, ct);
            group.Advance(GroupFollowPhase.Activating);
            var activation = await prepared.ActivateAsync(ct);
            long activationObserved = group.Clock.GetTimestamp();
            for (int i = 0; i < group.Lanes.Length; i++)
                SimulationFollowPolicy.ValidateActivation(starts[i].Sample, starts[i].Hardware,
                    SimulationFollowSample.Read(activation, group.Lanes[i].Robot.JointPrefix), GroupHardware(group, group.Lanes[i]),
                    group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
            group.Observation = new(SimulationFollowSample.Read(activation, group.Lanes[0].Robot.JointPrefix));
            group.Advance(GroupFollowPhase.DrainingPrimer);
            if (group.Primer is { } primer) await primer;
            ct.ThrowIfCancellationRequested();
            group.Require(GroupFollowPhase.DrainingPrimer);
            group.RecordValidatedSource(activationObserved);
            group.RequireOrderedSourceLease();
            group.Advance(GroupFollowPhase.Following);
            group.Publisher = PublishGroupTargets(group, ct);
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
                    GroupFollowStatus = "Simulation view delayed · retrying retained group frames";
                    continue;
                }
                var sample = SimulationFollowSample.Read(raw, group.Lanes[0].Robot.JointPrefix);
                bool paused = raw["sourcePausedForFollower"]?.GetValue<bool>() == true;
                group.Observation.Validate(sample, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), group.Clock.GetElapsedTime(began).TotalMilliseconds,
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
                GroupFollowStatus = heading;
                Volatile.Write(ref groupRetirement, new GroupRetirement(heading, lanes));
                foreach (var lane in group.Lanes)
                    if (lane.Diagnostics is { } diagnostics)
                        try { await diagnostics.Completion.WaitAsync(TimeSpan.FromSeconds(1)); }
                        catch (Exception error) { Trace.TraceWarning("Follow diagnostics writer: {0}", error.GetType().Name); }
            }
            finally
            {
                group.Armed.TrySetCanceled(group.Stop.Token);
                group.Advance(GroupFollowPhase.Retired);
                Interlocked.Increment(ref groupControlEpoch);
                Interlocked.CompareExchange(ref groupSimulation, null, group);
                group.Retired.TrySetResult();
            }
        }
    }

    private async Task MatchGroupStart(GroupSession group, JsonObject raw, CancellationToken ct)
    {
        foreach (var lane in group.Lanes)
        {
            var sample = SimulationFollowSample.Read(raw, lane.Robot.JointPrefix);
            var identity = new SimulationFollowIdentity(group.Scene, group.RunId, group.Entity, group.Asset, group.Revision);
            var hardware = GroupHardware(group, lane);
            SimulationFollowPolicy.ValidateCandidate(sample, identity, hardware, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
            lane.First = lane.LastSample = sample;
            if (sample.CommandDegrees.Zip(sample.MeasuredDegrees, (a, b) => Math.Abs(a - b)).Any(e => e > SimulationFollowPolicy.StartJointToleranceDegrees) ||
                Math.Abs(sample.CommandOpeningMm - sample.MeasuredOpeningMm) > SimulationFollowPolicy.StartGripperToleranceMm)
                throw new InvalidOperationException(lane.Profile.Name + ": simulated first-frame controls and measured pose disagree.");
        }
        foreach (var lane in group.Lanes)
        {
            var hardware = GroupHardware(group, lane); var first = lane.First!;
            if (first.CommandDegrees.Zip(hardware.JointDegrees, (a, b) => Math.Abs(a - b)).Any(e => e > SimulationFollowPolicy.StartJointToleranceDegrees))
            {
                var admitted = await GroupArmCommand(group, lane, FollowArmAction.MatchPose, new() { ["degrees"] = JsonSerializer.SerializeToNode(first.CommandDegrees), ["synchronized"] = true });
                lane.ArmRequest = admitted["request_id"]?.GetValue<long>() ?? throw new InvalidOperationException("Start matching has no arm request identity.");
            }
            if (Math.Abs(first.CommandOpeningMm - hardware.GripperOpeningMm) > SimulationFollowPolicy.StartGripperToleranceMm)
            {
                await GroupPika(group, lane, FollowPikaAction.MatchOpening, new() { ["value"] = PikaProtocol.Angle(first.CommandOpeningMm) });
            }
        }
        long began = group.Clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var source = telemetry.Latest!;
            bool all = true;
            foreach (var lane in group.Lanes)
            {
                var hardware = GroupHardware(group, lane, source: source);
                var observation = GroupObservation(group, lane, source);
                bool atPose = lane.First!.CommandDegrees.Zip(hardware.JointDegrees, (a, b) => Math.Abs(a - b)).All(e => e <= SimulationFollowPolicy.StartJointToleranceDegrees) &&
                    Math.Abs(lane.First.CommandOpeningMm - hardware.GripperOpeningMm) <= SimulationFollowPolicy.StartGripperToleranceMm &&
                    !observation.Motion.Active && observation.PikaIdle && Math.Abs(observation.PikaSpeed) <= .02 &&
                    observation.ArmStationary(group.Clock.GetTimestamp());
                double feedback = observation.PikaFeedbackTime;
                if (!atPose) lane.SettledAt = 0;
                else if (lane.SettledAt == 0)
                { lane.SettledAt = group.Clock.GetTimestamp(); lane.SettledSequence = observation.Sequence; lane.SettledFeedback = feedback; }
                bool settled = atPose && lane.SettledAt != 0 && group.Clock.GetElapsedTime(lane.SettledAt).TotalMilliseconds >= 100 &&
                    observation.Sequence > lane.SettledSequence && feedback > lane.SettledFeedback;
                all &= settled;
            }
            if (all) { foreach (var lane in group.Lanes) lane.ArmRequest = 0; return; }
            if (group.Clock.GetElapsedTime(began).TotalSeconds > 180) throw new InvalidOperationException("Not all selected robots reached and settled at the first frame.");
            await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
        }
    }

    private async Task AdmitGroup(GroupSession group, CancellationToken ct)
    {
        if (GroupStartConflict() is { } conflict) throw new InvalidOperationException(conflict);
        group.Advance(GroupFollowPhase.Admitting);
        GroupFollowStatus = "Starting selected followers · simulation remains paused";
        foreach (var lane in group.Lanes)
        {
            var admitted = await GroupArmCommand(group, lane, FollowArmAction.Start, SimulationTrajectoryTransport.StartFields());
            SimulationTrajectoryTransport.ValidateAcknowledgement(admitted);
            lane.ArmRequest = admitted["request_id"]?.GetValue<long>() ?? 0;
            if (lane.ArmRequest <= 0 || admitted["trajectory_follow"]?.GetValue<bool>() != true) throw new InvalidOperationException(lane.Profile.Name + ": native following was not acknowledged.");
            lane.AdmissionTicks = group.Clock.GetTimestamp(); lane.AdmissionSequence = telemetry.Latest?.Sequence ?? 0;
            if (group.Primer is null) group.Primer = PrimeGroup(group, ct);
            var pika = await GroupPika(group, lane, FollowPikaAction.Start);
            Volatile.Write(ref lane.PikaSession, pika["follow_session"]?.GetValue<string>() ?? throw new InvalidOperationException("Pika did not acknowledge its following session."));
        }
        long began = group.Clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); bool all = true;
            var source = telemetry.Latest!;
            foreach (var lane in group.Lanes)
            {
                _ = GroupHardware(group, lane, source: source);
                var observation = GroupObservation(group, lane, source);
                all &= observation.AdmissionReady(
                    owner, lane.ArmRequest, Interlocked.Read(ref lane.Sequence), lane.AdmissionTicks, lane.AdmissionSequence, group.Clock.GetTimestamp());
            }
            if (all) return;
            if (group.Clock.GetElapsedTime(began).TotalMilliseconds > 400) throw new InvalidOperationException("A selected controller did not publish its fresh admitted reference in time.");
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
                if (group.Clock.GetElapsedTime(began).TotalSeconds > 4) throw new InvalidOperationException("Grouped first-frame admission timed out; no playback was started.");
                await SendGroupTargets(group, group.Lanes.Where(l => l.ArmRequest > 0).ToDictionary(l => l.Profile.Id,
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
                lane.LastSample = sample;
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
                var hardware = group.Lanes.ToDictionary(l => l.Profile.Id, l => GroupHardware(group, l, native: true, source: source));
                var targets = new Dictionary<string, OrderedSimulationTarget>(StringComparer.Ordinal);
                lock (group.Gate)
                {
                    group.Barrier.Observe(group.Lanes.Select(l => l.Execution!).ToArray());
                    foreach (var lane in group.Lanes)
                    {
                        var observation = GroupObservation(group, lane, source);
                        lane.LastHardware = hardware[lane.Profile.Id];
                        lane.LastStationary = observation.ArmStationary(group.Clock.GetTimestamp());
                        lane.LastGripperSpeed = observation.PikaSpeed; lane.LastGripperFeedback = observation.PikaFeedbackTime;
                        try
                        {
                            targets[lane.Profile.Id] = lane.Execution!.Step(group.Clock.GetTimestamp() / (double)group.Clock.TimestampFrequency,
                                lane.LastHardware, lane.LastStationary, lane.LastGripperSpeed, lane.LastGripperFeedback, group.Barrier.MayStart(lane.Execution));
                        }
                        catch (Exception error) { throw new InvalidOperationException(lane.Profile.Name + ": " + error.Message, error); }
                    }
                    GroupFollowStatus = $"Following {group.Lanes.Length} robots · {group.Lanes.Select(l => l.Execution!.ActivePhase?.End.Phase).FirstOrDefault(p => p is not null) ?? "holding"}";
                    GroupFollowDetail = "All selected arms share phase barriers. Faster arms hold their measured endpoint until their partners arrive; the next episode waits for every arm and gripper.";
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
            if (priming)
            {
                if (!group.CanPrime) return;
            }
            else group.Require(GroupFollowPhase.Following);
            var receipt = telemetry.Latest;
            foreach (var lane in group.Lanes) _ = GroupHardware(group, lane, source: receipt);
            foreach (var lane in group.Lanes.Where(l => targets.ContainsKey(l.Profile.Id)))
            {
                var target = targets[lane.Profile.Id];
                long sequence = Interlocked.Increment(ref lane.Sequence);
                var timing = lane.Publications.Begin(sequence, Stopwatch.GetTimestamp(), 0);
                attempts.Add((lane, timing));
                writes.Add(lane.Publications.Observe(timing, GroupArmCommand(group, lane, FollowArmAction.Update, new() { ["request_id"] = lane.ArmRequest, ["sequence"] = sequence,
                    ["degrees"] = JsonSerializer.SerializeToNode(target.JointDegrees) }), arm: true));
                string pikaSession = Volatile.Read(ref lane.PikaSession);
                if (pikaSession.Length > 0) writes.Add(lane.Publications.Observe(timing, ObservePika(lane, GroupPika(group, lane, FollowPikaAction.Update, new()
                    { ["follow_session"] = pikaSession, ["sequence"] = sequence, ["opening_mm"] = target.OpeningMm })), arm: false));
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
            { Interlocked.CompareExchange(ref lane.RejectedPikaAudit, error.Audit, null); throw; }
        }
    }

    private async Task GroupHeartbeats(GroupSession group, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var source = telemetry.Latest;
                foreach (var lane in group.Lanes)
                {
                    if (lane.ArmRequest <= 0 || source is null) continue;
                    var observation = GroupObservation(group, lane, source);
                    if (observation.Motion.RequestId == lane.ArmRequest && observation.Motion.Active)
                        await lane.Controller.Heartbeat(lane.ArmRequest, ct);
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
            var source = telemetry.Latest!;
            foreach (var lane in group.Lanes)
            {
                var observation = GroupObservation(group, lane, source);
                if (!lane.Execution!.ReturnedHome(GroupHardware(group, lane, native: true, source: source),
                    observation.ArmStationary(group.Clock.GetTimestamp()), observation.PikaSpeed))
                    throw new InvalidOperationException("A missed grasp finished without every selected robot at its verified home and opened-jaw endpoint.");
            }
            group.EndReason = "Grasp not confirmed · all selected robots finished at home";
            return true;
        }
        if (!complete.EpisodeSucceeded) throw new InvalidOperationException("The source episode failed; all selected followers stopped.");
        if (group.AcknowledgedEpisode != complete.EpisodeId)
        {
            await prepared.AcknowledgeEpisodeAsync(complete, ct);
            group.AcknowledgedEpisode = complete.EpisodeId;
        }
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
            if (!lane.ArmTouched) return GroupReleaseEvidence.Untouched;
            if (group.Backend != token) return GroupReleaseEvidence.Unverified;
            try
            {
                var receipt = telemetry.Latest;
                var device = receipt?.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == lane.Robot.DeviceId);
                if (device?["generation"]?.GetValue<long>() != lane.Robot.Generation)
                    throw new InvalidOperationException(lane.Profile.Name + ": arm hold could not be verified on the original CAN connection.");
                long retired = Volatile.Read(ref lane.ArmAdmissionRetiredTicks);
                bool measuredIdle = receipt!.BackendToken == group.Backend && retired > 0 &&
                    receipt.CapturedTicks(group.Clock.TimestampFrequency) > retired &&
                    receipt.HasFreshDevice(device, group.Clock.GetTimestamp()) && device["motion"]?["device_id"]?.GetValue<string>() == lane.Robot.DeviceId &&
                    device["motion"]?["generation"]?.GetValue<long>() == lane.Robot.Generation && device["motion"]?["active"]?.GetValue<bool>() == false &&
                    (lane.ArmRequest == 0 || device["motion"]?["request_id"]?.GetValue<long>() == lane.ArmRequest);
                if (measuredIdle) return GroupReleaseEvidence.MeasuredIdle;
                await GroupArmCommand(group, lane, FollowArmAction.Hold, lane.ArmRequest > 0 ? new() { ["request_id"] = lane.ArmRequest } : null);
                return GroupReleaseEvidence.HoldAcknowledged;
            }
            catch (Exception error) { FailGroup(group, error); return GroupReleaseEvidence.Unverified; }
        }
        async Task<GroupReleaseEvidence> ReleasePika(GroupLane lane)
        {
            if (!lane.PikaTouched) return GroupReleaseEvidence.Untouched;
            if (group.Backend != token) return GroupReleaseEvidence.Unverified;
            try
            {
                var receipt = telemetry.Latest;
                if (receipt is null || ProfilePikaRouting.Find(receipt.State, lane.Profile)?["owner"]?.GetValue<string>() != owner)
                    throw new InvalidOperationException(lane.Profile.Name + ": gripper hold could not be verified for its original assignment and owner.");
                await GroupPika(group, lane, FollowPikaAction.Hold);
                return GroupReleaseEvidence.HoldAcknowledged;
            }
            catch (Exception error) { FailGroup(group, error); return GroupReleaseEvidence.Unverified; }
        }
    }

}
