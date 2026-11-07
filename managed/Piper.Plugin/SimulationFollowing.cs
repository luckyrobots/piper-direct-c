using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private sealed class SimulationSession(RobotLink robot, string backend, long permission, string modelAsset, string modelRevision,
        string pikaDevice, string pikaDistribution)
    {
        public readonly RobotLink Robot = robot;
        public readonly string Backend = backend, ModelAsset = modelAsset, ModelRevision = modelRevision;
        public string ProfileId = "", FirstFailure = "";
        public readonly long Permission = permission;
        public readonly string PikaDevice = pikaDevice, PikaDistribution = pikaDistribution;
        public string TrajectoryTransport { get; init; } = SimulationTrajectoryTransport.PositionVelocity;
        public readonly string PrepareId = Guid.NewGuid().ToString("N");
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource SetupRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ProducerRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource InitialRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string RunId = "", PikaSession = "";
        public long ArmRequest, Sequence;
        public double OriginalTimeScale = 1;
        public volatile bool NeedsMatch, Aligning;
        public volatile bool ReadyForPlay, PlayStarted;
        public volatile bool AdmissionStarted;
        public bool MatchSubmitted;
        public SimulationFollowSample? StartSample;
        public bool PikaPositionRequested;
        public string PikaMatchRequest = "";
        public long PikaMatchAckTicks, PikaMatchAckSequence, PikaSettledSince, PikaSettledSequence;
        public double PikaSettledFeedbackTime;
        public string PikaSettleReason = "";
        public long PikaSettleReportedAt;
        public bool HardwareRequested;
        public bool ArmRequested;
        public long ArmAdmissionRetiredTicks;
        public long ArmHoldAcknowledgedTicks, ArmHoldAcknowledgedSequence;
        public int Ending;
        public string Phase = "setup", Operation = "validate-hardware";
        public SimulationFollowSample? LastSample;
        public SimulationFollowDiagnostics? Diagnostics;
        public Task? Worker;
        public Task? EndingWorker;
        public readonly SemaphoreSlim TargetWrites = new(1);
        public volatile bool PrimingFinished;
        public Task? PrimingWorker;
        public OrderedSimulationExecution? Ordered;
        public OrderedSimulationObservation? SourceObservation;
        public bool SourcePaused;
        public string AcknowledgedEpisode = "";
        public readonly object OrderedGate = new();
        public long OrderedEnvelopeTicks;
        public Task? OrderedPublisher;
        public readonly SimulationPublishDiagnostics Publications = new();
        public Pika.PikaFollowAudit? RejectedPikaAudit;
    }

    private SimulationSession? simulationSession;
    private string simulationStatus = "Off · Play runs the simulation only";
    private string simulationDetail = "Follow simulation matches the first frame and waits for Play. The solid leader may run ahead; the measured ghost shows the real robot. Ordered arm and jaw phases follow within the robot's reported limits. Stop following holds both.";
    private string simulationStartComparison = "";
    private double? simulationErrorDegrees, simulationGripperErrorMm;
    private bool SimulationFollowingActive => Volatile.Read(ref simulationSession) is not null || GroupFollowActive;

    private bool SimulationScopeCurrent(SimulationSession session) =>
        ReferenceEquals(Volatile.Read(ref simulationSession), session) &&
        !session.Stop.IsCancellationRequested &&
        Volatile.Read(ref session.Ending) == 0 && session.Permission == Interlocked.Read(ref targetPermissionEpoch) &&
        session.Backend == token && ReferenceEquals(link, session.Robot);

    private bool SimulationAwaitingPlay(SimulationSession session) =>
        SimulationScopeCurrent(session) && Volatile.Read(ref session.RunId).Length == 0;

    private bool InactiveSimulationReady => Volatile.Read(ref simulationSession) is { } session &&
        SimulationScopeCurrent(session) && session.ReadyForPlay && !session.AdmissionStarted &&
        session.ArmRequest == 0 && session.PikaSession.Length == 0;

    private sealed class ReadyFeedbackPendingException(string reason) : InvalidOperationException(reason);

    private void WaitingForReadyFeedback(string reason)
    {
        simulationStatus = "Ready — press Play · waiting for fresh feedback";
        simulationDetail = reason + " The first frame is matched. No motion stream is active; a Play request waits for fresh measurements at that same frame before moving.";
    }

    private JsonObject SimulationRequest(SimulationSession session, string? action = null)
    {
        var request = new JsonObject { ["sceneId"] = session.Robot.SceneId, ["entityId"] = session.Robot.EntityId,
            ["expectedModelAssetId"] = session.ModelAsset, ["expectedModelRevision"] = session.ModelRevision,
            ["prepareId"] = session.PrepareId };
        if (session.RunId.Length > 0) request["runId"] = session.RunId;
        if (action is not null) request["action"] = action;
        if (action == "prepare-start") request["activationHandshake"] = true;
        return request;
    }

    private async Task<JsonObject> ReadSimulation(SimulationSession session, CancellationToken cancellation, bool preparing = false)
    {
        session.Operation = "robot.runtime.read";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Stop.Token);
        timeout.CancelAfter(preparing ? 1000 : 150);
        var response = await engine.RequestAsync("robot.runtime.read", SimulationRequest(session), timeout.Token);
        return JsonNode.Parse(response.GetRawText())!.AsObject();
    }

    private async Task<JsonObject> ControlSimulation(SimulationSession session, string action, CancellationToken cancellation, double? timeScale = null, bool releasing = false, JsonObject? properties = null)
    {
        if (!releasing) session.Operation = "robot.runtime.control:" + action;
        var request = SimulationRequest(session, action);
        if (properties is not null) foreach (var property in properties) request[property.Key] = property.Value?.DeepClone();
        if (timeScale.HasValue) request["timeScale"] = timeScale.Value;
        using var timeout = releasing ? CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Stop.Token);
        timeout.CancelAfter(400);
        var response = await engine.RequestAsync("robot.runtime.control", request, timeout.Token);
        return JsonNode.Parse(response.GetRawText())!.AsObject();
    }

    private static void ValidateSimulationTimeMode(JsonObject snapshot)
    {
        if (snapshot["timeMode"]?.GetValue<string>() is not ("deterministic-realtime" or "realtime"))
            throw new InvalidOperationException("Following requires Deterministic real-time or Real-time scene mode. Select one in scene settings before following the real robot.");
    }

    private SimulationFollowHardware SimulationHardware(SimulationSession session, bool nativeReference = false, TelemetryReceipt? sourceReceipt = null)
    {
        if (!ReferenceEquals(Volatile.Read(ref simulationSession), session) || session.Stop.IsCancellationRequested || Volatile.Read(ref session.Ending) != 0 ||
            !approved || calibrationOpen || session.Permission != Interlocked.Read(ref targetPermissionEpoch))
            throw new InvalidOperationException("Simulation following was disarmed; no old target will resume.");
        bool waiting = InactiveSimulationReady;
        var receipt = sourceReceipt ?? telemetry.Latest ?? throw (waiting ? new ReadyFeedbackPendingException("Controller feedback is unavailable.")
            : new InvalidOperationException("Following stopped: controller feedback is unavailable."));
        var device = waiting ? RobotProfileRouting.Device(preferences, receipt.State) : Device(session.Robot.DeviceId, session.Robot.Generation, session.Backend, receipt);
        _ = SimulationTrajectoryTransport.Validate(session.TrajectoryTransport, receipt.State, session.Robot.DeviceId);
        if (receipt.BackendToken != session.Backend || token != session.Backend || device["id"]?.GetValue<string>() != session.Robot.DeviceId ||
            device["generation"]?.GetValue<long>() != session.Robot.Generation)
            throw new InvalidOperationException("The prepared robot connection or backend changed; prepare a new start.");
        if (waiting && (device["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true ||
            device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true ||
            device["status"]?["error"]?.GetValue<int>() is not 0 || device["status"]?["arm_status"]?.GetValue<int>() is not 0 ||
            device["status"]?["control"]?.GetValue<int>() is not (0 or 1) || device["status"]?["teach_state"]?.GetValue<int>() is not (0 or 2 or 6) ||
            device["teaching"]?["active"]?.GetValue<bool>() == true || device["teaching"]?["return_required"]?.GetValue<bool>() == true ||
            device["joints"] is not JsonArray { Count: 6 } || device["joints"]!.AsArray().Any(j => j?["enabled"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0)))
            throw new InvalidOperationException("The prepared robot power, control mode or fault state changed; prepare a new start.");
        if (!waiting && (TeachingState.Inspect(device).BlocksTargets || device["recovery_required"]?.GetValue<bool>() == true || !RobotPowerState.Inspect(device).AllEnabled))
            throw new InvalidOperationException("Following stopped: all six motors must remain enabled, with CAN control and no recovery fault.");
        var pika = receipt.State["pika"] as JsonObject;
        bool pikaFresh = receipt.HasFreshPika(Stopwatch.GetTimestamp(), SimulationFollowPolicy.MaximumGripperFeedbackAgeMilliseconds);
        if (!pikaOwned || !PikaMatchesProfile(pika) || pika?["owner"]?.GetValue<string>() != owner)
            throw new InvalidOperationException("Following stopped: this robot's Pika connection or control owner changed.");
        if (pika["driver_fault"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Following stopped: " +
                (pika["driver_status_text"]?.GetValue<string>() ?? "Pika reported a gripper-driver fault."));
        if (pika["enabled"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Following stopped: Pika motor is not enabled. " +
                (pika["driver_status_text"]?.GetValue<string>() ?? "Check the gripper's motor status in Settings."));
        if (pika["armed"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Following stopped: Pika control was disarmed.");
        if (!GripperLink.TryMeasuredOpening(pika, out double width, PikaDisplayReferenceAngle(pika)))
            throw new InvalidOperationException("Following stopped: Pika reported a stale or invalid jaw position. Check the gripper feedback in Settings.");
        var joints = device["joints"]!.AsArray();
        bool deviceFresh = receipt.HasFreshDevice(device, Stopwatch.GetTimestamp());
        if (waiting && session.StartSample is { } start &&
            (deviceFresh && joints.Where((j, i) => Math.Abs(j!["actual_deg"]!.GetValue<double>() - start.CommandDegrees[i]) > SimulationFollowPolicy.StartJointToleranceDegrees).Any() ||
             pikaFresh && Math.Abs(width - start.CommandOpeningMm) > SimulationFollowPolicy.StartGripperToleranceMm))
            throw new InvalidOperationException("The real robot moved away from the prepared first frame. Stop following and match it again.");
        if (!pikaFresh || !deviceFresh || joints.Any(j => j?["limits_fresh"]?.GetValue<bool>() != true || j["acceleration_fresh"]?.GetValue<bool>() != true))
        {
            if (waiting) throw new ReadyFeedbackPendingException("Arm, reported limits or Pika feedback is temporarily delayed.");
            throw new InvalidOperationException("Following stopped: enabled Pika feedback from this robot must be no older than 250 ms, including transport delay; arm and reported limits must stay fresh.");
        }
        var limits = joints.Select((joint, i) => new EpisodeReplayJointLimit(i + 1,
            joint!["min_deg"]!.GetValue<double>(), joint["max_deg"]!.GetValue<double>(),
            joint["max_speed_rad_s"]!.GetValue<double>() * 180 / Math.PI,
            joint["max_acceleration_rad_s2"]!.GetValue<double>() * 180 / Math.PI,
            joint["acceleration_fresh"]?.GetValue<bool>() == true)).ToArray();
        var reference = nativeReference ? SimulationFollowNativeReference.Read(receipt, session.Backend, session.Robot.DeviceId,
            session.Robot.Generation, owner, session.ArmRequest, Interlocked.Read(ref session.Sequence), Stopwatch.GetTimestamp()) : null;
        return new(session.Robot.DeviceId, session.Robot.Generation, true,
            joints.All(j => j?["limits_fresh"]?.GetValue<bool>() == true), pikaFresh,
            joints.Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray(), limits, width, reference);
    }

    private async Task SimulationEvent(string action, CancellationToken cancellation)
    {
        if (action == "info") return;
        if (action != "toggle") throw new InvalidOperationException("Unknown simulation-follow action.");
        if (Volatile.Read(ref simulationSession) is { } previous)
        {
            await StopSimulationFollowing(previous, "Stopped · holding real joints and Pika; simulation paused", cancellation);
            return;
        }
        if (!double.IsFinite(preferences.Speed) || preferences.Speed is <= 0 or > 100)
            throw new InvalidOperationException("Choose a joint speed above 0% and at most 100% before preparing Follow simulation.");
        var linked = link ?? throw new InvalidOperationException("Link the selected robot in Edit mode first.");
        if (teleoperating || followRequest != 0) await PauseTeleoperation(cancellation);
        if (state?["motion"]?["active"]?.GetValue<bool>() == true || PikaTeachingRecording)
            throw new InvalidOperationException("Finish the current movement or hand-guided recording before following a simulation.");
        if (state?["trajectory_follow_supported"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Update the controller backend before following a simulation; acceleration-limited trajectory following is required.");
        var model = scene?["robots"]?.AsArray().OfType<JsonObject>().SingleOrDefault(r => r["entityId"]?.GetValue<string>() == linked.EntityId)
            ?? throw new InvalidOperationException("Refresh the scene robot assignment in Settings first.");
        var session = new SimulationSession(linked, token, Interlocked.Read(ref targetPermissionEpoch),
            model["modelAssetId"]!.GetValue<string>(), model["modelRevision"]!.GetValue<string>(), ActiveProfile.PikaDevice, ActiveProfile.WslDistribution)
            { TrajectoryTransport = SimulationTrajectoryTransport.Validate(preferences.SimulationTransport, state, linked.DeviceId), ProfileId = ActiveProfile.Id };
        session.Diagnostics = new(Path.Combine(engine.Start.SettingsDirectory, "diagnostics", "simulation-follow"), session.PrepareId,
            async diagnostic =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                await engine.ReportAsync("error", diagnostic, timeout.Token);
            });
        Volatile.Write(ref simulationSession, session);
        try
        {
            _ = SimulationHardware(session);
            var sample = await ReadSimulation(session, cancellation);
            if (sample["mode"]?.GetValue<string>() != "edit") throw new InvalidOperationException("Stop Play first, then arm Follow simulation. The next Play run will pause before motion.");
            if (sample["startPreparationSupported"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Update Lucky Engine to use automatic start-pose preparation.");
            if (sample["startActivationSupported"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("Update Lucky Engine to use the prepared Play admission handshake.");
            using (var capabilityDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Stop.Token))
            {
                capabilityDeadline.CancelAfter(400);
                var capabilities = await engine.RequestAsync("host.info", cancellation: capabilityDeadline.Token);
                if (!capabilities.TryGetProperty("robotRuntimeFramesSupported", out var supported) || !supported.GetBoolean())
                    throw new InvalidOperationException("Update Lucky Engine to use ordered simulation frames and the episode follower gate.");
            }
            ValidateSimulationTimeMode(sample);
            simulationStatus = "Preparing the first frame · simulation will stay paused";
            simulationStartComparison = "";
            session.Phase = "preparing";
            await ControlSimulation(session, "prepare-start", cancellation);
            _ = SimulationHardware(session);
            simulationDetail = "Preparing a new first frame. The simulation will stay paused until the robot matches and you press Play.";
            message = "Preparing simulation · previous following targets discarded";
            simulationErrorDegrees = simulationGripperErrorMm = null;
            session.Worker = Task.Run(() => FollowSimulation(session, shutdown.Token));
        }
        catch (Exception error)
        {
            RecordSimulationFailure(session, error);
            simulationDetail = error.Message;
            RetireSimulationSetup(session);
            await StopSimulationFollowing(session, "Not armed · " + error.Message, cancellation);
            throw;
        }
        finally { RetireSimulationSetup(session); }
    }

    private static void RetireSimulationSetup(SimulationSession session)
    {
        session.SetupRetired.TrySetResult();
        if (session.Worker is null) session.ProducerRetired.TrySetResult();
    }

    private async Task FollowSimulation(SimulationSession session, CancellationToken stopping)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping, session.Stop.Token);
        var ct = cancellation.Token;
        SimulationFollowPolicy? policy = null;
        long alignmentStarted = 0;
        long preparingStarted = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                session.Operation = "validate-hardware";
                SimulationFollowHardware hardware;
                try { hardware = SimulationHardware(session); }
                catch (ReadyFeedbackPendingException pending) when (InactiveSimulationReady)
                { WaitingForReadyFeedback(pending.Message); await Task.Delay(60, ct); continue; }
                long readStarted = Stopwatch.GetTimestamp();
                JsonObject raw;
                try { raw = session.PlayStarted ? await ReadOrderedFrames(session, ct) : await ReadSimulation(session, ct, preparing: session.RunId.Length == 0); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !session.ReadyForPlay && !session.PlayStarted &&
                    !session.AdmissionStarted && !session.HardwareRequested && Stopwatch.GetElapsedTime(preparingStarted).TotalSeconds < 60)
                {
                    simulationStatus = "Preparing the first frame · waiting for the engine";
                    await Task.Delay(100, ct); continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && InactiveSimulationReady)
                { WaitingForReadyFeedback("The paused simulation view is temporarily delayed."); await Task.Delay(60, ct); continue; }
                catch (OperationCanceledException) when (RetryOrderedFrameRead(session, ct))
                {
                    // Keep the last validated envelope's original expiry. The
                    // independent publisher holds at250ms if this retry also
                    // stalls; a cancelled read never grants fresh authority.
                    simulationStatus = "Simulation view delayed · retrying its retained source frames";
                    continue;
                }
                double roundTrip = Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
                string mode = raw["mode"]?.GetValue<string>() ?? "";
                if (mode is "preparing" or "loading" || mode == "edit" && session.RunId.Length == 0)
                {
                    if (Stopwatch.GetElapsedTime(preparingStarted).TotalSeconds > 60)
                        throw new InvalidOperationException("Preparing the simulation's first frame timed out. No start-pose target was sent.");
                    await Task.Delay(60, ct); continue;
                }
                if (mode == "edit") throw new InvalidOperationException("Play ended · old targets discarded; arm following again for the next run.");
                if (raw["ready"]?.GetValue<bool>() != true) throw new InvalidOperationException("The selected runtime robot is not ready.");
                var sample = SimulationFollowSample.Read(raw, session.Robot.JointPrefix);
                session.LastSample = sample;
                if (raw["prepareId"]?.GetValue<string>() != session.PrepareId)
                    throw new InvalidOperationException("The prepared simulation belongs to another Follow request.");
                bool firstRuntime = session.RunId.Length == 0;
                if (firstRuntime)
                {
                    session.RunId = sample.Identity.RunId;
                    session.OriginalTimeScale = sample.TimeScale;
                }
                ValidateSimulationTimeMode(raw);
                if (firstRuntime)
                {
                    await ControlSimulation(session, "pause", ct, .2);
                    session.NeedsMatch = true;
                    continue; // Read the actual paused start, after the pause acknowledgement.
                }
                if (policy is null)
                {
                    session.Phase = "matching-first-frame";
                    if (mode != "pause") throw new InvalidOperationException("The engine resumed before start-pose matching was ready. Following is stopped.");
                    var identity = new SimulationFollowIdentity(session.Robot.SceneId, session.RunId, session.Robot.EntityId, session.ModelAsset, session.ModelRevision);
                    SimulationFollowPolicy.ValidateCandidate(sample, identity, hardware, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), roundTrip);
                    session.StartSample ??= sample;
                    SimulationFollowPolicy.ValidatePausedStart(sample, session.StartSample);
                    simulationStartComparison = DescribeSimulationStart(sample, hardware);
                    if (!session.MatchSubmitted)
                    {
                        session.MatchSubmitted = true;
                        bool armDiffers = sample.CommandDegrees.Zip(hardware.JointDegrees, (target, actual) => Math.Abs(target - actual))
                            .Any(error => error > SimulationFollowPolicy.StartJointToleranceDegrees);
                        bool pikaDiffers = Math.Abs(sample.CommandOpeningMm - hardware.GripperOpeningMm) > SimulationFollowPolicy.StartGripperToleranceMm;
                        session.Aligning = armDiffers || pikaDiffers;
                        alignmentStarted = Stopwatch.GetTimestamp();
                        simulationStatus = session.Aligning ? "Moving the real robot to the first frame · Stop following holds both" : "Start pose matches · preparing linked controls";
                        // Once submitted, drain the bounded RPC acknowledgement even
                        // after Stop. Otherwise cancellation can hide a late admission
                        // from the final hold barrier. Call still has its 3s deadline.
                        if (armDiffers)
                        {
                            session.Operation = "match-arm-pose";
                            _ = SimulationHardware(session); session.HardwareRequested = session.ArmRequested = true;
                            Volatile.Write(ref session.ArmAdmissionRetiredTicks, 0);
                            try
                            {
                                await Command("pose", new JsonObject { ["degrees"] = JsonSerializer.SerializeToNode(sample.CommandDegrees), ["synchronized"] = true }, CancellationToken.None,
                                    expectedDeviceId: session.Robot.DeviceId, expectedGeneration: session.Robot.Generation, expectedToken: session.Backend,
                                    expectedPermissionEpoch: session.Permission, speedOverridePercent: 10);
                            }
                            finally { Volatile.Write(ref session.ArmAdmissionRetiredTicks, Stopwatch.GetTimestamp()); }
                            session.ArmRequest = lease;
                        }
                        if (pikaDiffers)
                        {
                            session.Operation = "match-pika-position";
                            _ = SimulationHardware(session); session.HardwareRequested = session.PikaPositionRequested = true;
                            var moved = await Call("/api/pika/position", new JsonObject { ["owner"] = owner, ["value"] = Pika.PikaProtocol.Angle(sample.CommandOpeningMm) }, CancellationToken.None, session.Backend);
                            session.PikaMatchRequest = moved["motor_request_id"]?.GetValue<string>() is { Length: > 0 } request ? request
                                : throw new InvalidOperationException("Pika did not acknowledge its start-position request identity. Update the controller backend.");
                            session.PikaMatchAckTicks = Stopwatch.GetTimestamp();
                            session.PikaMatchAckSequence = telemetry.Latest?.Sequence ?? 0;
                        }
                        _ = SimulationHardware(session);
                    }
                    if (session.Aligning)
                    {
                        simulationStatus = "Moving the real robot to the first frame · slow movement · Stop following holds both";
                        if (session.ArmRequest != 0 && (state?["motion"]?["request_id"]?.GetValue<long>() != session.ArmRequest || state?["motion"]?["active"]?.GetValue<bool>() == true) ||
                            state?["pika"]?["pending"]?.GetValue<bool>() == true)
                        {
                            if (Stopwatch.GetElapsedTime(alignmentStarted).TotalSeconds > 180) throw new InvalidOperationException("Start-pose matching timed out.");
                            await Task.Delay(40, ct); continue;
                        }
                        session.Aligning = false;
                        session.ArmRequest = 0;
                    }
                    if (!PikaStartSettled(session, out string pikaSettleReason))
                    {
                        if (Stopwatch.GetElapsedTime(alignmentStarted).TotalSeconds > 180)
                            throw new InvalidOperationException("Pika did not confirm an idle, settled start position after its acknowledged request.");
                        simulationStatus = "Waiting for Pika · " + pikaSettleReason;
                        simulationDetail = pikaSettleReason + " The simulation remains paused; Stop following is available.";
                        if (session.PikaSettleReason != pikaSettleReason &&
                            (session.PikaSettleReportedAt == 0 || Stopwatch.GetElapsedTime(session.PikaSettleReportedAt).TotalSeconds >= 1))
                        {
                            session.PikaSettleReason = pikaSettleReason;
                            session.PikaSettleReportedAt = Stopwatch.GetTimestamp();
                            await WritePrimaryActionReceipt("settling-pika", pikaSettleReason);
                        }
                        await Task.Delay(40, ct); continue;
                    }
                    try { policy = SimulationFollowPolicy.Start(sample, identity, SimulationHardware(session), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), roundTrip); }
                    catch (SimulationFollowAlignmentException reason)
                    {
                        if (Stopwatch.GetElapsedTime(alignmentStarted).TotalSeconds > 180)
                            throw new InvalidOperationException("The real robot did not reach the prepared first frame: " + reason.Message);
                        simulationStatus = "Waiting for the real robot to settle at the first frame · Stop following is available";
                        simulationDetail = reason.Message;
                        await Task.Delay(80, ct); continue;
                    }
                    session.NeedsMatch = false;
                    _ = SimulationHardware(session);
                    await PrepareOrderedSource(session, raw, sample, hardware, policy, ct);
                    await ControlSimulation(session, "mark-start-ready", ct);
                    _ = SimulationHardware(session);
                    session.ReadyForPlay = true;
                    session.Phase = "ready-paused";
                    simulationStatus = "Ready — press Play · the real arm and Pika match the first frame";
                    simulationDetail = "The simulation stays paused without an active arm or Pika motion stream. Press Play to connect both controllers, then start this exact first frame. Stop following cancels preparation.";
                    message = "Simulation ready · press Play when the workspace is clear";
                }
                else
                {
                    var currentHardware = SimulationHardware(session);
                    if (session.ReadyForPlay)
                    {
                        simulationStartComparison = DescribeSimulationStart(sample, currentHardware);
                        if (mode != "pause" || raw["startReady"]?.GetValue<bool>() != true)
                            throw new InvalidOperationException("The engine advanced before the prepared Play admission handshake.");
                        long readyObservedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (!SimulationFollowPolicy.ReadyObservationFresh(sample, readyObservedAt, roundTrip))
                        { WaitingForReadyFeedback("The paused first-frame observation is temporarily delayed."); await Task.Delay(60, ct); continue; }
                        policy.ValidateReady(sample, currentHardware, readyObservedAt, roundTrip);
                        if (raw["startRequested"]?.GetValue<bool>() != true)
                        {
                            simulationStatus = "Ready — press Play · the real arm and Pika match the first frame";
                            await Task.Delay(30, ct); continue;
                        }
                        await AdmitSimulationStart(session, policy, sample, ct);
                        session.ReadyForPlay = false; session.PlayStarted = true;
                        session.Phase = "following-play";
                        continue; // Read a new Play sample; never replay a pre-admission sample.
                    }
                    await ExecuteOrderedSource(session, raw, sample, currentHardware, roundTrip, ct);
                }
                await Task.Delay(30, ct);
                }
                catch (ReadyFeedbackPendingException pending) when (InactiveSimulationReady)
                { WaitingForReadyFeedback(pending.Message); await Task.Delay(60, ct); }
            }
        }
        catch (OperationCanceledException error) when (error is not OrderedFrameReadCancellation { DeadlineExpired: true } &&
            (ct.IsCancellationRequested || error is OrderedFrameReadCancellation { ExpectedStop: true }))
        {
            if (!stopping.IsCancellationRequested && Volatile.Read(ref session.Ending) == 0)
                RecordSimulationFailure(session, error);
        }
        catch (Exception error)
        {
            RecordSimulationFailure(session, error);
            simulationDetail = session.Diagnostics?.FirstReason is { Length: > 0 } firstReason ? firstReason : error.Message;
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await StopSimulationFollowing(session, "Following stopped · " + simulationDetail, release.Token, fromWorker: true);
        }
        finally
        {
            try
            {
                if (ReferenceEquals(Volatile.Read(ref simulationSession), session))
                {
                    using var release = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    string reason = session.Diagnostics?.FirstReason is { Length: > 0 } firstReason
                        ? "Following stopped · " + firstReason : "Following disarmed · previous targets discarded";
                    await StopSimulationFollowing(session, reason, release.Token, fromWorker: true);
                }
            }
            finally
            {
                await FinishStartPriming(session);
                if (session.OrderedPublisher is { } publisher) await publisher;
                session.ProducerRetired.TrySetResult();
            }
        }
    }

    private async Task AdmitSimulationStart(SimulationSession session, SimulationFollowPolicy policy, SimulationFollowSample start, CancellationToken cancellation)
    {
        _ = SimulationHardware(session); // Still an inactive gate: an age crossing here waits without admission.
        _ = SimulationTrajectoryTransport.Validate(session.TrajectoryTransport, state, session.Robot.DeviceId);
        session.AdmissionStarted = true;
        session.Phase = "admitting-requested-play";
        simulationStatus = "Starting linked playback · simulation stays paused until both controllers acknowledge";
        session.HardwareRequested = session.ArmRequested = true;
        Volatile.Write(ref session.ArmAdmissionRetiredTicks, 0);
        JsonObject armStart;
        try
        {
            session.Operation = "arm-follow-start";
            armStart = await Command("follow-start", SimulationTrajectoryTransport.StartFields(session.TrajectoryTransport), CancellationToken.None,
                expectedDeviceId: session.Robot.DeviceId, expectedGeneration: session.Robot.Generation, expectedToken: session.Backend,
                expectedPermissionEpoch: session.Permission);
        }
        finally { Volatile.Write(ref session.ArmAdmissionRetiredTicks, Stopwatch.GetTimestamp()); }
        long acknowledgedRequest = armStart["request_id"]?.GetValue<long>() ?? 0;
        if (acknowledgedRequest <= 0) throw new InvalidOperationException("The arm backend did not identify its admitted following request.");
        session.ArmRequest = acknowledgedRequest;
        SimulationTrajectoryTransport.ValidateAcknowledgement(session.TrajectoryTransport, armStart);
        long nativeAcknowledgementSequence = telemetry.Latest?.Sequence ?? 0;
        if (armStart["trajectory_follow"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The arm backend did not acknowledge acceleration-limited trajectory following.");
        _ = SimulationHardware(session);
        session.PrimingWorker = PrimeSimulationStart(session, start);
        session.Operation = "pika-follow-start";
        var pikaStart = await Call("/api/pika/follow-start", new JsonObject { ["owner"] = owner }, CancellationToken.None, session.Backend);
        Volatile.Write(ref session.PikaSession, pikaStart["follow_session"]?.GetValue<string>() ?? throw new InvalidOperationException("Pika did not acknowledge a following session."));
        await SendSimulationTarget(session, start.CommandDegrees, start.CommandOpeningMm, cancellation);
        await WaitForNativeAdmissionReference(session, nativeAcknowledgementSequence, cancellation);
        long readStarted = Stopwatch.GetTimestamp();
        var pinned = SimulationFollowSample.Read(await ReadSimulation(session, cancellation), session.Robot.JointPrefix);
        session.LastSample = pinned;
        policy.ValidateReady(pinned, SimulationHardware(session), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds);
        long activatingStarted = Stopwatch.GetTimestamp();
        var activation = await ControlSimulation(session, "activate-start", cancellation, session.OriginalTimeScale);
        _ = SimulationHardware(session);
        if (activation["prepareId"]?.GetValue<string>() != session.PrepareId || activation["startRequested"]?.GetValue<bool>() != true ||
            activation["startActivated"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The prepared Play activation acknowledgement changed its request.");
        var activated = SimulationFollowSample.Read(activation, session.Robot.JointPrefix);
        session.LastSample = activated;
        policy.ActivatePreparedStart(activated, SimulationHardware(session), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Stopwatch.GetElapsedTime(activatingStarted).TotalMilliseconds, orderedSource: true);
        session.SourceObservation = new(activated);
        await FinishStartPriming(session);
        _ = SimulationHardware(session);
        Volatile.Write(ref session.OrderedEnvelopeTicks, Stopwatch.GetTimestamp());
        session.OrderedPublisher = PublishOrderedTargets(session);
    }

    private async Task PrimeSimulationStart(SimulationSession session, SimulationFollowSample start)
    {
        long began = Stopwatch.GetTimestamp();
        try
        {
            while (!session.PrimingFinished && !session.Stop.IsCancellationRequested)
            {
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 2)
                    throw new InvalidOperationException("Prepared Play admission timed out; static start targets retired without replay.");
                await SendSimulationTarget(session, start.CommandDegrees, start.CommandOpeningMm, session.Stop.Token, admittedOnly: true);
                await Task.Delay(30, session.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            RecordSimulationFailure(session, error);
            session.Stop.Cancel();
        }
    }

    private static async Task FinishStartPriming(SimulationSession session)
    {
        session.PrimingFinished = true;
        if (session.PrimingWorker is { } worker) await worker;
    }

    private static string DescribeSimulationStart(SimulationFollowSample sample, SimulationFollowHardware hardware) =>
        "             Real now      First frame       Change\n" +
        string.Join("\n", Enumerable.Range(0, 6).Select(i =>
            $"J{i + 1,-5}{hardware.JointDegrees[i],10:F2}°   {sample.CommandDegrees[i],10:F2}°   {sample.CommandDegrees[i] - hardware.JointDegrees[i]:+0.00;-0.00;0.00}°")) +
        $"\nPika  {hardware.GripperOpeningMm,10:F1} mm {sample.CommandOpeningMm,10:F1} mm {sample.CommandOpeningMm - hardware.GripperOpeningMm:+0.0;-0.0;0.0} mm";

    private bool PikaStartSettled(SimulationSession session, out string reason)
    {
        var receipt = telemetry.Latest;
        long now = Stopwatch.GetTimestamp();
        var pika = receipt?.State["pika"];
        static bool Number(JsonNode? value, out double number)
        {
            number = double.NaN;
            return value is JsonValue scalar && scalar.TryGetValue<double>(out number) && double.IsFinite(number);
        }
        reason = !SimulationScopeCurrent(session) || receipt is null || receipt.BackendToken != session.Backend ? "the prepared connection changed"
            : !receipt.HasFreshPika(now, 150) ? "gripper feedback is delayed (150 ms limit)"
            : pika?["owner"]?.GetValue<string>() != owner ? "gripper control owner changed"
            : pika["device"]?.GetValue<string>() != session.PikaDevice || OperatingSystem.IsWindows() && pika["distribution"]?.GetValue<string>() != session.PikaDistribution ? "the assigned gripper connection changed"
            : pika["enabled"]?.GetValue<bool>() != true || pika["armed"]?.GetValue<bool>() != true ? "gripper control is not enabled"
            : pika["pending"]?.GetValue<bool>() != false ? "the gripper is finishing its target"
            : pika["following"]?.GetValue<bool>() == true || pika["resuming_hold"]?.GetValue<bool>() == true ? "the previous gripper stream is finishing"
            : !Number(pika["telemetry"]?["motor"]?["Speed"], out double speed) || Math.Abs(speed) > .02 ? "the gripper has not stopped moving"
            : "";
        double feedback = double.NaN;
        if (reason.Length == 0 && !Number(pika?["feedback_time_s"], out feedback)) reason = "the gripper feedback clock is unavailable";
        if (reason.Length == 0 && session.PikaPositionRequested &&
            (receipt!.ReceivedTicks <= session.PikaMatchAckTicks || receipt.Sequence <= session.PikaMatchAckSequence ||
             pika?["last_motor_request"]?.GetValue<string>() != session.PikaMatchRequest ||
             !Number(pika["request_time_s"], out double requested) || requested < 0 || feedback <= requested))
            reason = "waiting for feedback from this start-position request";
        if (reason.Length > 0)
        { session.PikaSettledSince = session.PikaSettledSequence = 0; return false; }
        if (session.PikaSettledSince == 0)
        {
            session.PikaSettledSince = now; session.PikaSettledSequence = receipt!.Sequence;
            session.PikaSettledFeedbackTime = feedback; reason = "checking two fresh stationary gripper samples"; return false;
        }
        // An ACK or a repeated old idle packet is not arrival. Observe two
        // distinct fresh motor samples while the agent reports no target.
        bool settled = Stopwatch.GetElapsedTime(session.PikaSettledSince).TotalMilliseconds >= 100 &&
            receipt!.Sequence > session.PikaSettledSequence && feedback > session.PikaSettledFeedbackTime;
        reason = settled ? "" : "checking two fresh stationary gripper samples";
        return settled;
    }

    private void AddSimulationControlPatches(Dictionary<string, JsonObject> patches)
    {
        if (!SimulationFollowingActive) return;
        patches["speed"] = new() { ["enabled"] = false, ["hint"] = "This follower keeps the speed selected when it was prepared. Stop following before changing speed." };
        foreach (string id in patches.Keys.Where(id => id is "enable" or "disable" or "link" or "zero.all" or "teleoperate.toggle" or
            "pika.target" or "pika.minus" or "pika.plus" or "pika.zero" or "pika.reference.set" or "pika.reference.clear" or
            "teaching.return" or "teaching.return.confirm" or "teaching.reset.begin" or "teaching.reset.confirm" or
            "teaching.pika.free" or "teaching.pika.confirm" or "teaching.pika.hold" ||
            id.StartsWith("teleoperate.joint.", StringComparison.Ordinal) ||
            id.StartsWith("joint.", StringComparison.Ordinal) && (id.EndsWith(".target", StringComparison.Ordinal) || id.EndsWith(".minus", StringComparison.Ordinal) ||
                id.EndsWith(".plus", StringComparison.Ordinal) || id.EndsWith(".zero", StringComparison.Ordinal))).ToArray())
        {
            patches[id]["enabled"] = false;
            patches[id]["hint"] = "Following owns controls — Stop following first.";
        }
        patches["motion.hold"] = new() { ["enabled"] = true, ["hint"] = "Stop following and hold both the arm and Pika. The simulation stays paused." };
    }

    private async Task SendSimulationTarget(SimulationSession session, IReadOnlyList<double> degrees, double openingMm, CancellationToken cancellation, bool admittedOnly = false)
    {
        long waitingAt = Stopwatch.GetTimestamp();
        await session.TargetWrites.WaitAsync(cancellation);
        try
        {
        session.Operation = "follow-update:arm+pika";
        _ = SimulationHardware(session);
        long sequence = Interlocked.Increment(ref session.Sequence);
        var timing = session.Publications.Begin(sequence, Stopwatch.GetTimestamp(), Stopwatch.GetElapsedTime(waitingAt).TotalMilliseconds);
        var arm = Call("/api/command", new JsonObject { ["action"] = "follow-update", ["request_id"] = session.ArmRequest,
            ["owner"] = owner, ["device_id"] = session.Robot.DeviceId, ["generation"] = session.Robot.Generation,
            ["standalone"] = true, ["sequence"] = sequence, ["degrees"] = JsonSerializer.SerializeToNode(degrees) }, cancellation, session.Backend);
        string pikaSession = Volatile.Read(ref session.PikaSession);
        var pika = admittedOnly && pikaSession.Length == 0 ? Task.CompletedTask : Call("/api/pika/follow-update", new JsonObject { ["owner"] = owner, ["follow_session"] = pikaSession,
            ["sequence"] = sequence, ["opening_mm"] = openingMm }, cancellation, session.Backend);
        try
        {
            await Task.WhenAll(session.Publications.Observe(timing, arm, arm: true),
                admittedOnly && pikaSession.Length == 0 ? Task.CompletedTask : session.Publications.Observe(timing, CapturePikaRejection(session, pika), arm: false));
        }
        finally { session.Publications.Complete(timing); }
        }
        finally { session.TargetWrites.Release(); }
    }

    private static async Task CapturePikaRejection(SimulationSession session, Task request)
    {
        try { await request; }
        catch (Pika.PikaFollowRejectedException error)
        {
            // When both channels fail, WhenAll may throw the arm exception.
            // Retain the independent negative Pika ACK before either cleanup.
            Interlocked.CompareExchange(ref session.RejectedPikaAudit, error.Audit, null);
            throw;
        }
    }

    private void CancelSimulationFollowingInput() => Volatile.Read(ref simulationSession)?.Stop.Cancel();

    private async Task StopSimulationFollowing(SimulationSession session, string reason, CancellationToken cancellation, bool fromWorker = false)
    {
        if (!ReferenceEquals(Volatile.Read(ref simulationSession), session)) return;
        if (Interlocked.CompareExchange(ref session.Ending, 1, 0) == 0)
        {
            session.Stop.Cancel();
            Interlocked.Increment(ref targetPermissionEpoch);
            uiEvents?.InvalidateControlEvents();
            if (lease == session.ArmRequest) lease = 0; // Stop renewing an acknowledged Match/follow lease immediately.
            simulationStatus = reason;
            // This cleanup owns its deadlines. A cancelled UI request must not
            // cancel the release or allow a replacement while admission is pending.
            session.EndingWorker = Task.Run(() => EndSimulationFollowing(session, reason));
        }
        try
        {
            // A producer cannot await the barrier completed by its own finally.
            await (fromWorker ? session.InitialRelease.Task : session.Ended.Task)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellation);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            if (ReferenceEquals(Volatile.Read(ref simulationSession), session))
                simulationDetail = reason + " · stopping; an outstanding request is still retiring. Following cannot be armed again yet.";
        }
    }

    private async Task EndSimulationFollowing(SimulationSession session, string reason)
    {
        try
        {
            var failures = new List<string>();
            failures.AddRange(await ReleaseSimulationOwnership(session));
            session.InitialRelease.TrySetResult();

            // Retain Ending for however long the actual producers take to retire.
            // A timeout only ends the caller's wait, never this ownership barrier.
            await Task.WhenAll(session.SetupRetired.Task, session.ProducerRetired.Task);

            // An accepted Match/start whose acknowledgement arrived after the
            // first hold is now known to be retired. Hold once more after it.
            failures.AddRange(await ReleaseSimulationOwnership(session, restoreTimeScale: true));
            if (failures.Count > 0)
            {
                RecordSimulationFailure(session, new InvalidOperationException(reason));
                session.Diagnostics?.AppendCleanup(failures.Select(failure => SafeSimulationDiagnosticText(session, failure)));
                simulationDetail = (session.Diagnostics?.FirstReason is { Length: > 0 } firstReason ? firstReason : reason) + " · release acknowledgement: " + string.Join("; ", failures.Distinct());
            }
            await FinishSimulationDiagnostics(session);
            if (failures.Count == 0)
                simulationDetail = (session.Diagnostics?.FirstReason is { Length: > 0 } firstReason ? firstReason + " " : "") +
                    "Following has ended. Previous targets are discarded; return to Edit to prepare another run.";
            if (lease == session.ArmRequest) lease = 0;
            Interlocked.CompareExchange(ref simulationSession, null, session);
            session.Ended.TrySetResult();
        }
        catch (Exception error)
        {
            RecordSimulationFailure(session, new InvalidOperationException(reason));
            session.Diagnostics?.AppendCleanup([SafeSimulationDiagnosticText(session, error.Message)]);
            // Keep the blocked Ending context on an unexpected cleanup failure.
            // Neither a replacement nor old producer input may acquire authority.
            simulationDetail = (session.Diagnostics?.FirstReason is { Length: > 0 } firstReason ? firstReason : reason) + " · release could not finish: " + error.Message;
        }
        finally { session.InitialRelease.TrySetResult(); }
    }

    private string SafeSimulationDiagnosticText(SimulationSession session, string text)
    {
        if (session.Backend.Length > 0) text = text.Replace(session.Backend, "[redacted]", StringComparison.Ordinal);
        text = text.Replace(owner, "[redacted]", StringComparison.Ordinal);
        return text.Length <= 2000 ? text : text[..2000];
    }

    private void RecordSimulationFailure(SimulationSession session, Exception error, SimulationFollowFailureOrdered? ordered = null)
    {
        if (error is OrderedFrameReadCancellation { ExpectedStop: true } && Volatile.Read(ref session.Ending) != 0) return;
        string detail = error is OperationCanceledException ? "Runtime request timed out or its control permission was canceled: " + error.Message : error.Message;
        detail = SafeSimulationDiagnosticText(session, detail);
        string visibleReason = detail + (ordered is null ? "" : " " + ordered.Describe());
        // Cleanup's hold acknowledgement can replace the footer. Keep the first
        // trigger separately until another user action clears it. An old session
        // must never label a newly selected robot.
        if (Interlocked.CompareExchange(ref session.FirstFailure, visibleReason, "").Length == 0 &&
            ReferenceEquals(Volatile.Read(ref simulationSession), session) && session.Backend == token && session.ProfileId == ActiveProfile.Id)
            uiActionError = "Following stopped: " + visibleReason;
        try
        {
            var sample = session.LastSample;
            var receipt = telemetry.Latest;
            var device = receipt?.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d =>
                d["id"]?.GetValue<string>() == session.Robot.DeviceId);
            var motion = receipt?.State["motion"];
            var pika = receipt?.State["pika"] as JsonObject;
            var followAudit = error is Pika.PikaFollowRejectedException rejected ? rejected.Audit :
                Volatile.Read(ref session.RejectedPikaAudit) ?? pika?["follow_audit"]?.Deserialize<Pika.PikaFollowAudit>();
            static double? Milliseconds(JsonNode? number) => number is JsonValue value && value.TryGetValue<double>(out double seconds) && double.IsFinite(seconds)
                ? seconds * 1000 : null;
            double? combinedPikaAge = receipt is null ? null : receipt.PikaEvidenceAgeMilliseconds(Stopwatch.GetTimestamp());
            if (combinedPikaAge is { } age && !double.IsFinite(age)) combinedPikaAge = null;
            SimulationFollowFailureHardware? hardware = device is null || receipt is null ? null : new(
                receipt.Sequence, receipt.AgeMilliseconds(Stopwatch.GetTimestamp()), device["id"]!.GetValue<string>(),
                device["generation"]?.GetValue<long>() ?? 0, motion?["request_id"]?.GetValue<long>() ?? 0,
                motion?["active"]?.GetValue<bool>() == true, device["recovery_required"]?.GetValue<bool>() == true,
                motion?["follow_sequence"]?.GetValue<long>() ?? 0, motion?["following"]?.GetValue<bool>() == true,
                SafeSimulationDiagnosticText(session, receipt.State["message"]?.GetValue<string>() ?? ""),
                device["joints"]!.AsArray().Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray(),
                GripperLink.TryMeasuredOpening(pika, out double width, PikaDisplayReferenceAngle(pika)) ? width : null,
                new(PikaMatchesProfile(pika), pika?["owner"]?.GetValue<string>() == owner, pikaOwned,
                    pika?["connected"]?.GetValue<bool>() == true, pika?["fresh"]?.GetValue<bool>() == true,
                    pika?["armed"]?.GetValue<bool>() == true, pika?["enabled"]?.GetValue<bool>() == true,
                    receipt.SourceAgeMilliseconds, Math.Max(0, receipt.AgeMilliseconds(Stopwatch.GetTimestamp()) - receipt.SourceAgeMilliseconds),
                    Milliseconds(pika?["age_s"]), combinedPikaAge, Milliseconds(pika?["agent_diagnostics"]?["maximum_valid_decode_interval_s"]),
                    Milliseconds(pika?["agent_diagnostics"]?["maximum_stdout_emit_s"]), followAudit));
            var runtime = sample is null ? null : new SimulationFollowFailureRuntime(sample.Mode, sample.Sequence,
                sample.SimulationTimeNs, sample.CapturedMonotonicNs, sample.TimeScale, sample.CommandDegrees.ToArray(),
                sample.MeasuredDegrees.ToArray(), sample.CommandOpeningMm, sample.MeasuredOpeningMm, sample.SimTimeSeconds);
            session.Diagnostics?.Capture(new(DateTimeOffset.UtcNow, session.Phase, session.Operation, error.GetType().Name,
                detail, session.ArmRequest, Interlocked.Read(ref session.Sequence), new(session.Robot.SceneId,
                    session.RunId, session.Robot.EntityId, session.ModelAsset, session.ModelRevision, session.Robot.DeviceId,
                    session.Robot.Generation, session.PikaDevice, receipt?.State["backend_process_id"]?.GetValue<long>(), session.Robot.JointPrefix), runtime, hardware, session.Publications.Snapshot(), ordered));
        }
        catch (Exception diagnosticError)
        { Trace.TraceWarning("Could not capture Follow diagnostics: {0}", diagnosticError.GetType().Name); }
    }

    private static async Task FinishSimulationDiagnostics(SimulationSession session)
    {
        if (session.Diagnostics is not { } diagnostics) return;
        try { await diagnostics.Completion.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { /* The independent writer can finish after release. */ }
        catch (Exception error) { Trace.TraceWarning("Follow diagnostic writer: {0}", error.GetType().Name); }
    }

    private async Task<string[]> ReleaseSimulationOwnership(SimulationSession session, bool restoreTimeScale = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var cancellation = deadline.Token;
        var releases = new List<Task>();
        var scopeFailures = new List<string>();
        if (token == session.Backend)
        {
            if (session.ArmRequested)
            {
                long admissionRetired = Volatile.Read(ref session.ArmAdmissionRetiredTicks);
                long holdAcknowledged = Volatile.Read(ref session.ArmHoldAcknowledgedTicks);
                long holdSequence = Volatile.Read(ref session.ArmHoldAcknowledgedSequence);
                long barrier = Math.Max(admissionRetired, holdAcknowledged);
                bool PostBarrier(TelemetryReceipt? current) => admissionRetired > 0 && current is not null &&
                    (holdAcknowledged == 0 || current.Sequence > holdSequence) &&
                    current.ReceivedTicks - current.SourceAgeMilliseconds * Stopwatch.Frequency / 1000 > barrier;
                bool RetiredArm(TelemetryReceipt? current)
                {
                    if (!PostBarrier(current) || current!.BackendToken != session.Backend) return false;
                    var observed = current.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d =>
                        d["id"]?.GetValue<string>() == session.Robot.DeviceId && d["generation"]?.GetValue<long>() == session.Robot.Generation);
                    bool acknowledgedThisRequest = holdAcknowledged > admissionRetired && session.ArmRequest != 0 &&
                        current.State["motion"]?["request_id"]?.GetValue<long>() == session.ArmRequest;
                    return observed is not null && current.HasFreshDevice(observed, Stopwatch.GetTimestamp()) &&
                        (session.ArmRequest == 0 || current.State["motion"]?["request_id"]?.GetValue<long>() == session.ArmRequest) &&
                        current.State["motion"]?["active"]?.GetValue<bool>() == false &&
                        (acknowledgedThisRequest || observed["status"]?["stationary_s"]?.GetValue<double>() is >= .4);
                }
                if (restoreTimeScale)
                {
                    long waiting = Stopwatch.GetTimestamp();
                    while (!RetiredArm(telemetry.Latest) && Stopwatch.GetElapsedTime(waiting).TotalMilliseconds < 300)
                        await Task.Delay(20, cancellation);
                }
                bool alreadyIdle = RetiredArm(telemetry.Latest);
                // Idle has no hold command in the native controller. A Pika-only
                // match never owned an arm action; an already idle, stationary
                // arm needs no packet. Unknown/in-flight admissions retain the
                // scoped hold barrier before and after producer retirement.
                if (!alreadyIdle)
                {
                    var hold = new JsonObject { ["action"] = "hold", ["owner"] = owner, ["device_id"] = session.Robot.DeviceId, ["generation"] = session.Robot.Generation, ["standalone"] = true };
                    if (session.ArmRequest != 0) hold["request_id"] = session.ArmRequest;
                    releases.Add(HoldSimulationArm(session, hold, cancellation));
                }
            }
            var pika = state?["pika"];
            bool pikaScope = pika?["owner"]?.GetValue<string>() == owner && pika["device"]?.GetValue<string>() == session.PikaDevice &&
                (!OperatingSystem.IsWindows() || pika["distribution"]?.GetValue<string>() == session.PikaDistribution);
            if (pikaScope && (session.HardwareRequested || session.PikaSession.Length > 0 || session.PikaPositionRequested))
                releases.Add(Call("/api/pika/hold", new JsonObject { ["owner"] = owner }, cancellation, session.Backend));
            else if (session.HardwareRequested)
                scopeFailures.Add("Pika connection or owner changed; the old hold was not sent to its replacement.");
        }
        else if (session.HardwareRequested)
            scopeFailures.Add("Controller session changed; holds on the old hardware session could not be verified.");
        // Restore only after the setup/producer barrier. An in-flight resume
        // can still set the following scale after the first best-effort pause.
        // The exact run/model scope rejects this if Play has already changed.
        // An unsupported original scale must never prevent the final pause.
        double? originalScale = restoreTimeScale && double.IsFinite(session.OriginalTimeScale) &&
            session.OriginalTimeScale is >= .01 and <= 1 ? session.OriginalTimeScale : null;
        releases.Add(ControlSimulation(session, "cancel-start", cancellation, releasing: true));
        if (session.RunId.Length > 0)
            releases.Add(ControlSimulation(session, "pause", cancellation, originalScale, releasing: true));
        var outcomes = await Task.WhenAll(releases.Select(async release => { try { await release; return ""; } catch (Exception error) { return error.Message; } }));
        return outcomes.Where(e => e.Length > 0).Concat(scopeFailures).ToArray();
    }

    private async Task HoldSimulationArm(SimulationSession session, JsonObject hold, CancellationToken cancellation)
    {
        await Call("/api/command", hold, cancellation, session.Backend);
        Volatile.Write(ref session.ArmHoldAcknowledgedSequence, telemetry.Latest?.Sequence ?? 0);
        Volatile.Write(ref session.ArmHoldAcknowledgedTicks, Stopwatch.GetTimestamp());
    }
}
