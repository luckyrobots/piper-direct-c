using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private sealed class GroupLane(RobotProfile profile, RobotLink robot, ProfileApproval approval, double? reference)
    {
        public readonly RobotProfile Profile = profile;
        public readonly RobotLink Robot = robot;
        public readonly ProfileApproval Approval = approval;
        public readonly double? ClosedReference = reference;
        public SimulationFollowSample? First;
        public SimulationFollowPolicy? Policy;
        public OrderedSimulationExecution? Execution;
        public long ArmRequest, Sequence, AdmissionTicks, AdmissionSequence;
        public string PikaSession = "";
        public bool ArmTouched, PikaTouched;
        public double SettledFeedback;
        public long SettledAt, SettledSequence;
    }

    private sealed class GroupSession(string backend, string sceneId, string entityId, string asset, string revision, GroupLane[] lanes)
    {
        public readonly string Backend = backend, Scene = sceneId, Entity = entityId, Asset = asset, Revision = revision;
        public readonly GroupLane[] Lanes = lanes;
        public readonly string PrepareId = Guid.NewGuid().ToString("N");
        public readonly CancellationTokenSource Stop = new();
        public readonly SemaphoreSlim Writes = new(1);
        public readonly object Gate = new();
        public readonly GroupSimulationPhaseBarrier Barrier = new();
        public string RunId = "", FirstFailure = "", AcknowledgedEpisode = "";
        public volatile string Stage = "arming";
        public double OriginalScale = 1;
        public long EnvelopeTicks;
        public volatile bool PrimingComplete;
        public Task? Worker, Publisher, Heartbeats, Primer;
        public OrderedSimulationObservation? Observation;
        public bool SourcePaused;
    }

    private GroupSession? groupSimulation;
    private bool GroupFollowActive => Volatile.Read(ref groupSimulation) is not null;
    private string GroupFollowStatus = "Simulation only";
    private string GroupFollowDetail = "Choose the robots that should follow, then arm Follow simulation. Playback begins only when you press the editor's Play button.";
    private bool GroupFollowIncludes(string profileId) => Volatile.Read(ref groupSimulation)?.Lanes.Any(l => l.Profile.Id == profileId) == true;

    private void CancelGroupSimulationFollowingInput(string reason = "Following canceled")
    {
        if (Volatile.Read(ref groupSimulation) is not { } group) return;
        GroupFollowStatus = reason;
        group.Stop.Cancel();
    }

    private async Task StopGroupSimulationFollowing(CancellationToken cancellation)
    {
        var group = Volatile.Read(ref groupSimulation);
        if (group is null) return;
        group.Stop.Cancel();
        if (group.Worker is { } worker) await worker.WaitAsync(cancellation);
    }

    private Task StopGroupSimulationFollowing(string reason, CancellationToken cancellation)
    { CancelGroupSimulationFollowingInput(reason); return StopGroupSimulationFollowing(cancellation); }

    private async Task GroupFollowEvent(string action, CancellationToken cancellation)
    {
        if (action is not ("toggle" or "cancel")) throw new InvalidOperationException("Unknown grouped follow action.");
        if (GroupFollowActive)
        { CancelGroupSimulationFollowingInput("Stopping all selected followers · holding"); await StopGroupSimulationFollowing(cancellation); return; }
        if (action == "cancel") return;
        groupFollowFailure = "";
        try
        {
            if (Volatile.Read(ref simulationSession) is not null || teleoperating || followRequest != 0 || calibrationOpen ||
                handGuideRecovery is not null || teachingResetBusy || recoveryHomeBusy || PikaTeachingRecording)
                throw new InvalidOperationException("Finish the current control or calibration action before arming grouped following.");
            var profiles = SelectedFollowProfiles();
            var preparation = GroupPreparation(profiles);
            if (!preparation.Ready) throw new InvalidOperationException(preparation.Summary);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(1500);
            var capabilities = await engine.RequestAsync("host.info", cancellation: deadline.Token);
            if (!capabilities.TryGetProperty("robotRuntimeTracksSupported", out var tracks) || !tracks.GetBoolean())
                throw new InvalidOperationException("Update Lucky Engine to use atomic multi-robot frame tracks.");
            var discovered = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: deadline.Token)).GetRawText())!.AsObject();
            // Discovery is read-only. Recheck the whole group from one current
            // receipt before installing its immutable lanes or arming the engine.
            var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for controller feedback.");
            preparation = GroupPreparation(profiles, receipt, discovered);
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
                var expected = new ProfileApproval(receipt.BackendToken, profile.CanDeviceId, generation, profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution);
                if (!controlApprovals.TryGetValue(profile.Id, out var approval) || approval != expected)
                    throw new InvalidOperationException(profile.Name + ": use Enable robot and confirm its workspace before selecting it as a follower.");
                var robot = new RobotLink(sceneId, entity, model["joints"]!.AsArray(), device, profile.RobotJointPrefix);
                var pika = ProfilePikaRouting.Find(receipt.State, profile);
                lanes.Add(new(profile, robot, approval, GroupClosedReference(profile, pika)));
            }
            var group = new GroupSession(receipt.BackendToken, sceneId, entity, model["modelAssetId"]!.GetValue<string>(), model["modelRevision"]!.GetValue<string>(), lanes.ToArray());
            if (Interlocked.CompareExchange(ref groupSimulation, group, null) is not null) throw new InvalidOperationException("Another grouped follower is still retiring.");
            try
            {
                foreach (var lane in group.Lanes)
                {
                    _ = GroupHardware(group, lane);
                    if (GroupReceipt(group, lane).State["motion"]?["active"]?.GetValue<bool>() == true)
                        throw new InvalidOperationException(lane.Profile.Name + ": finish its current motion before arming the group.");
                }
                var raw = await GroupEngine(group, "robot.runtime.read", null, cancellation);
                if (raw["mode"]?.GetValue<string>() != "edit") throw new InvalidOperationException("Stop Play before arming followers.");
                ValidateSimulationTimeMode(raw);
                var armed = await GroupEngine(group, "robot.runtime.control", "arm-next-play", cancellation);
                if (armed["mode"]?.GetValue<string>() != "edit" || armed["startArmedForNextPlay"]?.GetValue<bool>() != true ||
                    armed["prepareId"]?.GetValue<string>() != group.PrepareId)
                    throw new InvalidOperationException("The engine did not acknowledge an unchanged Edit-mode arm-next-play latch.");
                GroupFollowStatus = $"Armed · {group.Lanes.Length} robot(s) · press Play";
                GroupFollowDetail = "The editor is still in Edit mode. Your next Play pauses at its first frame, matches every selected robot and gripper, then starts the shared episode.";
                group.Worker = Task.Run(() => RunGroupSimulation(group));
            }
            catch
            {
                group.Stop.Cancel(); await ReleaseGroup(group);
                Interlocked.CompareExchange(ref groupSimulation, null, group); throw;
            }
        }
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

    private double? GroupClosedReference(RobotProfile profile, JsonObject? pika)
    {
        try
        {
            string distribution = OperatingSystem.IsWindows() ? profile.WslDistribution : "";
            string path = Path.Combine(engine.Start.SettingsDirectory, "gripper-reference", profile.Id + ".json");
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<PikaClosedDisplayReference>(File.ReadAllText(path), PluginProtocol.Json);
            return saved?.Matches(profile.Id, profile.PikaDevice, distribution, pika) == true ? saved.MotorAngle : null;
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private bool GroupScopeCurrent(GroupSession group) => ReferenceEquals(Volatile.Read(ref groupSimulation), group) &&
        !group.Stop.IsCancellationRequested && group.Backend == token && group.Lanes.All(l =>
            preferences.Robots?.Any(p => p.Id == l.Profile.Id && p.CanDeviceId == l.Profile.CanDeviceId && p.RobotId == l.Profile.RobotId &&
                p.RobotJointPrefix == l.Profile.RobotJointPrefix && p.PikaDevice == l.Profile.PikaDevice && p.WslDistribution == l.Profile.WslDistribution) == true &&
            controlApprovals.TryGetValue(l.Profile.Id, out var approval) && approval == l.Approval);

    private TelemetryReceipt GroupReceipt(GroupSession group, GroupLane lane, TelemetryReceipt? source = null)
    {
        if (!GroupScopeCurrent(group)) throw new InvalidOperationException("A selected follower assignment, confirmation or backend changed; previous targets were discarded.");
        var receipt = source ?? telemetry.Latest ?? throw new InvalidOperationException("Controller feedback is unavailable.");
        if (receipt.BackendToken != group.Backend || !receipt.IsFresh(Stopwatch.GetTimestamp())) throw new InvalidOperationException("Controller feedback is stale or belongs to another session.");
        return GroupSimulationLaneTelemetry.Project(receipt, lane.Profile, lane.Robot.Generation);
    }

    private SimulationFollowHardware GroupHardware(GroupSession group, GroupLane lane, bool native = false, TelemetryReceipt? source = null)
    {
        var receipt = GroupReceipt(group, lane, source);
        long now = Stopwatch.GetTimestamp();
        var device = receipt.State["devices"]![0]!.AsObject();
        var joints = device["joints"]!.AsArray();
        if (!receipt.HasFreshDevice(device, now) || device["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true ||
            device["firmware_supported"]?.GetValue<bool>() != true || device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true ||
            TeachingState.Inspect(device).BlocksTargets || device["status"]?["control"]?.GetValue<int>() != 1 ||
            device["status"]?["arm_status"]?.GetValue<int>() != 0 || device["status"]?["error"]?.GetValue<int>() != 0 ||
            joints.Count != 6 || joints.Any(j => j?["enabled"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0 ||
                j["limits_fresh"]?.GetValue<bool>() != true || j["acceleration_fresh"]?.GetValue<bool>() != true))
            throw new InvalidOperationException(lane.Profile.Name + ": fresh healthy CAN control, six enabled motors and reported limits are required.");
        var motion = receipt.State["motion"]!;
        if (motion["active"]?.GetValue<bool>() == true && motion["owner"]?.GetValue<string>() != owner)
            throw new InvalidOperationException(lane.Profile.Name + ": another controller owns its active motion.");
        var pika = receipt.State["pika"]!.AsObject();
        if (!receipt.HasFreshPika(now, 250) || pika["owner"]?.GetValue<string>() != owner || pika["enabled"]?.GetValue<bool>() != true ||
            pika["armed"]?.GetValue<bool>() != true || pika["driver_fault"]?.GetValue<bool>() == true ||
            !GripperLink.TryMeasuredOpening(pika, out double width, lane.ClosedReference))
            throw new InvalidOperationException(lane.Profile.Name + ": its enabled Pika needs fresh owned, fault-free feedback (250 ms limit).");
        var limits = joints.Select((j, i) => new EpisodeReplayJointLimit(i + 1, j!["min_deg"]!.GetValue<double>(), j["max_deg"]!.GetValue<double>(),
            j["max_speed_rad_s"]!.GetValue<double>() * 180 / Math.PI, j["max_acceleration_rad_s2"]!.GetValue<double>() * 180 / Math.PI, true)).ToArray();
        var reference = native ? SimulationFollowNativeReference.Read(receipt, group.Backend, lane.Robot.DeviceId, lane.Robot.Generation,
            owner, lane.ArmRequest, Interlocked.Read(ref lane.Sequence), now) : null;
        return new(lane.Robot.DeviceId, lane.Robot.Generation, true, true, true,
            joints.Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray(), limits, width, reference);
    }

    private JsonObject GroupRequest(GroupSession group, string? action)
    {
        var request = new JsonObject { ["sceneId"] = group.Scene, ["entityId"] = group.Entity, ["expectedModelAssetId"] = group.Asset,
            ["expectedModelRevision"] = group.Revision, ["prepareId"] = group.PrepareId };
        if (group.RunId.Length > 0) request["runId"] = group.RunId;
        if (action is not null) request["action"] = action;
        if (action == "arm-next-play") request["activationHandshake"] = true;
        return request;
    }

    private async Task<JsonObject> GroupEngine(GroupSession group, string method, string? action, CancellationToken cancellation,
        JsonObject? properties = null, bool release = false)
    {
        if (!release && !GroupScopeCurrent(group)) throw new InvalidOperationException("Grouped following was disarmed.");
        var request = GroupRequest(group, action);
        if (properties is not null) foreach (var property in properties) request[property.Key] = property.Value?.DeepClone();
        using var timeout = release ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : CancellationTokenSource.CreateLinkedTokenSource(cancellation, group.Stop.Token);
        timeout.CancelAfter(method == "robot.runtime.frames" && action != "start" ? 150 : 1000);
        return JsonNode.Parse((await engine.RequestAsync(method, request, timeout.Token)).GetRawText())!.AsObject();
    }

    private async Task<JsonObject> GroupArmCommand(GroupSession group, GroupLane lane, string action, JsonObject? fields = null, bool release = false)
    {
        if (!release) _ = GroupHardware(group, lane);
        var body = fields ?? new();
        body["action"] = action; body["owner"] = owner; body["device_id"] = lane.Robot.DeviceId; body["generation"] = lane.Robot.Generation;
        body["standalone"] = true; body["supported"] = true; body["clear"] = true; body["reset_completed"] = true;
        body["speed_percent"] = action == "pose" ? 10 : lane.Profile.Speed; body["tolerance_deg"] = lane.Profile.Tolerance;
        return await Call("/api/command", body, CancellationToken.None, group.Backend);
    }

    private Task<JsonObject> GroupPika(GroupSession group, GroupLane lane, string action, JsonObject? fields = null, bool release = false)
    {
        if (!release) _ = GroupHardware(group, lane);
        var body = fields ?? new(); body["owner"] = owner; body["pika_device"] = lane.Profile.PikaDevice; body["pika_distribution"] = lane.Profile.WslDistribution;
        return Call("/api/pika/" + action, body, CancellationToken.None, group.Backend);
    }
}
