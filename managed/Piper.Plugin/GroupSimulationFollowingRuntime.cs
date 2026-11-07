using System.Diagnostics;
using System.Globalization;
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
        try
        {
            ct.ThrowIfCancellationRequested();
            foreach (var lane in group.Lanes) _ = GroupHardware(group, lane);
            var edit = await GroupEngine(group, "robot.runtime.read", null, ct);
            if (edit["mode"]?.GetValue<string>() != "edit") throw new InvalidOperationException("Stop Play before arming followers.");
            ValidateSimulationTimeMode(edit);
            var armed = await GroupEngine(group, "robot.runtime.control", "arm-next-play", ct);
            if (armed["mode"]?.GetValue<string>() != "edit" || armed["startArmedForNextPlay"]?.GetValue<bool>() != true ||
                armed["prepareId"]?.GetValue<string>() != group.PrepareId)
                throw new InvalidOperationException("The engine did not acknowledge an unchanged Edit-mode arm-next-play latch.");
            group.Stage = "armed";
            GroupFollowStatus = $"Armed · {group.Lanes.Length} robot(s) · press Play";
            GroupFollowDetail = "The editor is still in Edit mode. Your next Play pauses at its first frame, matches every selected robot and gripper, then starts the shared episode.";
            group.Armed.TrySetResult();
            group.Heartbeats = GroupHeartbeats(group, ct);
            JsonObject raw;
            while (true)
            {
                foreach (var lane in group.Lanes) _ = GroupHardware(group, lane);
                raw = await GroupEngine(group, "robot.runtime.read", null, ct);
                string mode = raw["mode"]?.GetValue<string>() ?? "";
                if (mode == "pause" && raw["ready"]?.GetValue<bool>() == true) break;
                if (mode is not ("edit" or "preparing" or "loading")) throw new InvalidOperationException("The armed scene did not pause at its prepared first frame.");
                await Task.Delay(TimeSpan.FromMilliseconds(80), group.Clock, ct);
            }
            group.RunId = raw["runId"]!.GetValue<string>();
            if (group.RunId.Length == 0 || raw["prepareId"]?.GetValue<string>() != group.PrepareId || raw["startRequested"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The user-requested Play run did not match this grouped preparation.");
            group.OriginalScale = raw["timeScale"]!.GetValue<double>();
            await GroupEngine(group, "robot.runtime.control", "pause", ct, new() { ["timeScale"] = .2 });
            raw = await GroupEngine(group, "robot.runtime.read", null, ct);
            group.Stage = "matching"; GroupFollowStatus = "Matching all selected robots to the first frame";
            await MatchGroupStart(group, raw, ct);
            raw = await GroupEngine(group, "robot.runtime.read", null, ct);
            foreach (var lane in group.Lanes)
            {
                var sample = SimulationFollowSample.Read(raw, lane.Robot.JointPrefix);
                SimulationFollowPolicy.ValidatePausedStart(sample, lane.First!);
                var hardware = GroupHardware(group, lane);
                lane.Policy = SimulationFollowPolicy.Start(sample, sample.Identity, hardware, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0);
                lane.Execution = new(sample, hardware, lane.Profile.Speed / 100, preserveSemanticPhases: true);
            }
            await GroupEngine(group, "robot.runtime.control", "claim-episode-follower", ct,
                new() { ["episodeId"] = raw["episodeId"]?.GetValue<string>() ?? "" });
            var mappings = new JsonArray(group.Lanes.Select(l =>
            { var mapping = l.Robot.OrderedCaptureMapping(raw); mapping["id"] = l.Profile.Id; return (JsonNode)mapping; }).ToArray());
            var initial = await GroupEngine(group, "robot.runtime.frames", "start", ct,
                new() { ["tracks"] = mappings, ["afterFrameCursor"] = "0", ["maxFrames"] = 128 });
            AppendGroupFrames(group, initial, initial: true);
            await GroupEngine(group, "robot.runtime.control", "mark-start-ready", ct);
            await AdmitGroup(group, ct);
            var activation = await GroupEngine(group, "robot.runtime.control", "activate-start", ct, new() { ["timeScale"] = group.OriginalScale });
            if (activation["prepareId"]?.GetValue<string>() != group.PrepareId || activation["startActivated"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The shared Play activation acknowledgement changed its request.");
            foreach (var lane in group.Lanes)
            {
                var sample = SimulationFollowSample.Read(activation, lane.Robot.JointPrefix);
                lane.Policy!.ActivatePreparedStart(sample, GroupHardware(group, lane), group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), 0, orderedSource: true);
            }
            group.Observation = new(SimulationFollowSample.Read(activation, group.Lanes[0].Robot.JointPrefix));
            group.PrimingComplete = true;
            if (group.Primer is { } primer) await primer;
            Volatile.Write(ref group.EnvelopeTicks, group.Clock.GetTimestamp());
            group.Stage = "following";
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
                    raw = await GroupEngine(group, "robot.runtime.frames", null, ct, new()
                        { ["afterFrameCursor"] = after.ToString(CultureInfo.InvariantCulture), ["maxFrames"] = Math.Min(space, 128) });
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && OrderedFrameReadRetry.Allowed(
                    group.Clock.GetElapsedTime(Volatile.Read(ref group.EnvelopeTicks)).TotalMilliseconds, false,
                    GroupScopeCurrent(group), group.Publisher is { IsCompleted: false }))
                {
                    // A lost read response consumes no source nodes and grants
                    // no new authority. Retry the SAME cursor while the original
                    // validated envelope's independent250ms lease is still live.
                    GroupFollowStatus = "Simulation view delayed · retrying retained group frames";
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested &&
                    group.Clock.GetElapsedTime(Volatile.Read(ref group.EnvelopeTicks)).TotalMilliseconds >= 250)
                { throw new InvalidOperationException("The shared simulation source expired after 250 ms; all follower targets were retired."); }
                var sample = SimulationFollowSample.Read(raw, group.Lanes[0].Robot.JointPrefix);
                bool paused = raw["sourcePausedForFollower"]?.GetValue<bool>() == true;
                group.Observation.Validate(sample, group.Clock.GetUtcNow().ToUnixTimeMilliseconds(), group.Clock.GetElapsedTime(began).TotalMilliseconds,
                    paused, raw["episodeComplete"]?.GetValue<bool>() == true, raw["episodeId"]?.GetValue<string>() ?? "", group.AcknowledgedEpisode.Length > 0);
                AppendGroupFrames(group, raw);
                if (await CompleteGroupEpisode(group, ct)) break;
                int buffered;
                lock (group.Gate) buffered = group.Lanes.Max(l => l.Execution!.Path.FrameCount);
                if (!paused && buffered >= OrderedSimulationPath.HighWaterFrames)
                    await GroupEngine(group, "robot.runtime.control", "pause-follower-source", ct);
                else if (paused && buffered <= OrderedSimulationPath.LowWaterFrames)
                    await GroupEngine(group, "robot.runtime.control", "resume-follower-source", ct);
                await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { FailGroup(group, error); group.Armed.TrySetException(error); }
        finally
        {
            try
            {
                group.Stage = "stopping"; group.Stop.Cancel(); group.PrimingComplete = true;
                // Drain all in-flight admissions/publications before the final holds.
                // No replacement group may acquire these devices during this barrier.
                foreach (var worker in new[] { group.Primer, group.Publisher, group.Heartbeats })
                    if (worker is not null) try { await worker; } catch (Exception error) { FailGroup(group, error); }
                var lanes = await ReleaseGroup(group);
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
        group.Stage = "admitting"; GroupFollowStatus = "Starting selected followers · simulation remains paused";
        foreach (var lane in group.Lanes)
        {
            var admitted = await GroupArmCommand(group, lane, FollowArmAction.Start, SimulationTrajectoryTransport.StartFields(SimulationTrajectoryTransport.PositionVelocity));
            SimulationTrajectoryTransport.ValidateAcknowledgement(SimulationTrajectoryTransport.PositionVelocity, admitted);
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
            while (!group.PrimingComplete && !ct.IsCancellationRequested)
            {
                if (group.Clock.GetElapsedTime(began).TotalSeconds > 4) throw new InvalidOperationException("Grouped first-frame admission timed out; no playback was started.");
                await SendGroupTargets(group, group.Lanes.Where(l => l.ArmRequest > 0).ToDictionary(l => l.Profile.Id,
                    l => new OrderedSimulationTarget(l.First!.CommandDegrees, l.First.CommandOpeningMm)), ct);
                await Task.Delay(TimeSpan.FromMilliseconds(30), group.Clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { FailGroup(group, error); group.Stop.Cancel(); }
    }

    private void AppendGroupFrames(GroupSession group, JsonObject response, bool initial = false)
    {
        if (response["prepareId"]?.GetValue<string>() != group.PrepareId || response["runId"]?.GetValue<string>() != group.RunId)
            throw new InvalidOperationException("The grouped tape preparation or Play run changed.");
        var frames = GroupSimulationFrames.Read(response, group.Lanes.Select(l => l.Profile.Id).ToArray());
        lock (group.Gate)
        {
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
            catch { group.Stop.Cancel(); throw; } // No publisher may consume a partially rejected atomic batch.
            group.SourcePaused = response["sourcePausedForFollower"]?.GetValue<bool>() == true;
            Volatile.Write(ref group.EnvelopeTicks, group.Clock.GetTimestamp());
        }
    }

    private async Task PublishGroupTargets(GroupSession group, CancellationToken ct)
    {
        try
        {
            var cadence = new SimulationPublishCadence(group.Clock.GetTimestamp(), group.Clock.TimestampFrequency, TimeSpan.FromMilliseconds(30));
            while (!ct.IsCancellationRequested)
            {
                if (group.Clock.GetElapsedTime(Volatile.Read(ref group.EnvelopeTicks)).TotalMilliseconds > 250)
                    throw new InvalidOperationException("The shared simulation source expired after 250 ms; all follower targets were retired.");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { FailGroup(group, error); group.Stop.Cancel(); }
    }

    private async Task SendGroupTargets(GroupSession group, IReadOnlyDictionary<string, OrderedSimulationTarget> targets, CancellationToken ct)
    {
        long waiting = Stopwatch.GetTimestamp();
        await group.Writes.WaitAsync(ct);
        var attempts = new List<(GroupLane Lane, SimulationPublishDiagnostics.Attempt Timing)>();
        try
        {
            var writes = new List<Task>();
            var receipt = telemetry.Latest;
            foreach (var lane in group.Lanes) _ = GroupHardware(group, lane, source: receipt);
            foreach (var lane in group.Lanes.Where(l => targets.ContainsKey(l.Profile.Id)))
            {
                var target = targets[lane.Profile.Id];
                long sequence = Interlocked.Increment(ref lane.Sequence);
                var timing = lane.Publications.Begin(sequence, Stopwatch.GetTimestamp(), Stopwatch.GetElapsedTime(waiting).TotalMilliseconds);
                attempts.Add((lane, timing));
                writes.Add(lane.Publications.Observe(timing, GroupArmCommand(group, lane, FollowArmAction.Update, new() { ["request_id"] = lane.ArmRequest, ["sequence"] = sequence,
                    ["degrees"] = JsonSerializer.SerializeToNode(target.JointDegrees) }), arm: true));
                string pikaSession = Volatile.Read(ref lane.PikaSession);
                if (pikaSession.Length > 0) writes.Add(lane.Publications.Observe(timing, ObservePika(lane, GroupPika(group, lane, FollowPikaAction.Update, new()
                    { ["follow_session"] = pikaSession, ["sequence"] = sequence, ["opening_mm"] = target.OpeningMm })), arm: false));
            }
            await Task.WhenAll(writes);
        }
        finally
        {
            foreach (var (lane, timing) in attempts) lane.Publications.Complete(timing);
            group.Writes.Release();
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { FailGroup(group, error); group.Stop.Cancel(); }
    }

    private async Task<bool> CompleteGroupEpisode(GroupSession group, CancellationToken ct)
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
            await GroupEngine(group, "robot.runtime.control", "ack-episode", ct, new() { ["episodeId"] = complete.EpisodeId,
                ["episodeOrdinal"] = complete.EpisodeOrdinal, ["finalFrameCursor"] = complete.Cursor.ToString(CultureInfo.InvariantCulture) });
            group.AcknowledgedEpisode = complete.EpisodeId;
        }
        return false;
    }

    private async Task<GroupLaneRetirement[]> ReleaseGroup(GroupSession group)
    {
        var outcomes = await Task.WhenAll(group.Lanes.Select(async lane =>
        {
            // Start both channels independently. A failed lookup or hold on
            // one channel cannot suppress its partner or another robot.
            var arm = ReleaseArm(lane);
            var pika = ReleasePika(lane);
            return new GroupLaneRetirement(lane.Profile.Name, await arm, await pika);
        }));
        try { await GroupEngine(group, "robot.runtime.control", "cancel-start", CancellationToken.None, release: true); }
        catch (Exception error) { FailGroup(group, error); }
        if (group.RunId.Length > 0)
            try { await GroupEngine(group, "robot.runtime.control", "pause", CancellationToken.None, new() { ["timeScale"] = group.OriginalScale }, release: true); }
            catch (Exception error) { FailGroup(group, error); }
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
                    receipt.ReceivedTicks - receipt.SourceAgeMilliseconds * group.Clock.TimestampFrequency / 1000 > retired &&
                    receipt.HasFreshDevice(device, group.Clock.GetTimestamp()) && device["motion"]?["device_id"]?.GetValue<string>() == lane.Robot.DeviceId &&
                    device["motion"]?["generation"]?.GetValue<long>() == lane.Robot.Generation && device["motion"]?["active"]?.GetValue<bool>() == false &&
                    (lane.ArmRequest == 0 || device["motion"]?["request_id"]?.GetValue<long>() == lane.ArmRequest);
                if (measuredIdle) return GroupReleaseEvidence.MeasuredIdle;
                await GroupArmCommand(group, lane, FollowArmAction.Hold, lane.ArmRequest > 0 ? new() { ["request_id"] = lane.ArmRequest } : null, release: true);
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
                await GroupPika(group, lane, FollowPikaAction.Hold, release: true);
                return GroupReleaseEvidence.HoldAcknowledged;
            }
            catch (Exception error) { FailGroup(group, error); return GroupReleaseEvidence.Unverified; }
        }
    }

}
