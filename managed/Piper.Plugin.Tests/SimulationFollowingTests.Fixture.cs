using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static partial class SimulationFollowingTests
{
    private sealed class FollowTestClock : TimeProvider
    {
        private long offset, frozenAt;
        public override long GetTimestamp()
        {
            long frozen = Volatile.Read(ref frozenAt);
            return (frozen == 0 ? Stopwatch.GetTimestamp() : frozen) + Interlocked.Read(ref offset);
        }
        public void Freeze() => Volatile.Write(ref frozenAt, Stopwatch.GetTimestamp());
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref offset, (long)(elapsed.TotalSeconds * TimestampFrequency));
    }

    private sealed partial class Fixture : IAsyncDisposable
    {
        private readonly PipersPlugin plugin;
        public PipersPlugin Plugin => plugin;
        private readonly TimeProvider followClock;
        private readonly object resumeFrameGate = new();
        private bool publishResumeFrameCopies;
        public readonly FakeCalls Rpc = new();
        public ConcurrentQueue<(string Method, string Action, JsonObject Body)> EngineRequests { get; } = new();
        public ConcurrentQueue<(string Method, string Action, JsonObject Body)> AppliedEngineRequests { get; } = new();
        public ConcurrentQueue<string> EngineReports { get; } = new();
        private readonly string settingsDirectory = Path.Combine(Path.GetTempPath(), "piper-follow-fixture-" + Guid.NewGuid().ToString("N"));
        public JsonObject Snapshot { get; }
        public volatile string Mode = "edit";
        public volatile string? TimeMode = "deterministic-realtime";
        public volatile string RunId = "run-a", DelayedRuntimeAction = "";
        public volatile bool InvalidModel, UnsettledStart;
        public volatile bool RuntimeReadTimeout;
        public volatile bool DelayNativeAdmissionTelemetry;
        private long pendingNativeSequence;
        private double[] pendingNativeCommand = new double[6];
        public volatile bool LoseNextOrderedReply;
        public int CompletedOrderedReads;
        public string LostReplyCursor = "";
        public TaskCompletionSource OrderedReplyLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool ChangeActivationFrame;
        public bool WrongPlayPreparation;
        public bool FreezeClockAtActivation;
        public bool PreparationSupported = true, ActivationSupported = true, AutoPrepare = true, FramesSupported = true;
        public volatile bool FrameCursorGap, TerminalFrame;
        public bool ScriptedMissedGrasp, ScriptedSourceMiss;
        public bool GroupMode;
        private JsonObject? currentSceneEvidence;
        private string[] capturedGroupTracks = [];
        private bool groupArmed;
        private readonly CancellationTokenSource groupTelemetryStop = new();
        private Task? groupTelemetryWorker;
        public string EpisodeId = "";
        public double FrameSourceAgeMs;
        public ulong SealedFrameCursor;
        private double pikaRequestAt;
        private volatile bool readyGate;
        private string prepareId = "";
        private readonly ConcurrentDictionary<string, byte> canceledPreparations = new();
        private double simulationTime;
        private ulong frameCursor;
        private readonly List<JsonObject> sourceFrames = [];
        public bool Ready => readyGate;
        private volatile bool startRequested, activated;
        public bool PlayStarted => GroupPhase == GroupFollowPhase.Following;
        public bool TryUserPlay() { if (!readyGate || Mode != "pause") return false; startRequested = true; return true; }
        public TaskCompletionSource RuntimeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRuntime { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double scale = 1;
        public double Scale { get => Volatile.Read(ref scale); set => Volatile.Write(ref scale, value); }
        private long sequence;
        public double EnvelopeAgeMs;
        public bool FreezeTelemetry;
        public int LinkEditorDelayMilliseconds;
        public string PrimaryReceipt => Path.Combine(settingsDirectory, "diagnostics", "primary-action", "latest.json");
        public SemaphoreSlim Commands => plugin.commands;
        public void SetSpeed(double speed) => plugin.preferences = plugin.preferences with { Speed = speed };
        public void SetApproved(bool value) => plugin.approved = value;
        public void SetTransport(string transport) => plugin.preferences = plugin.preferences with { SimulationTransport = transport };
        public void SetProfileTransport(string profile, string transport)
        {
            var current = RobotProfileRouting.Capture(plugin.preferences);
            current = current with { Robots = current.Robots!.Select(p => p.Id == profile ? p with { SimulationTransport = transport } : p).ToArray() };
            if (current.ActiveRobotId == profile) current = current with { SimulationTransport = transport };
            plugin.preferences = current; Refresh();
        }

        public Fixture(bool mismatch = false, TimeProvider? followClock = null)
        {
            this.followClock = followClock ?? TimeProvider.System;
            plugin = new("http://127.0.0.1:8766", this.followClock);
            plugin.client = new PiperControl.PiperControlClient(Rpc);
            plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
                [new("piper-1", "Fixture", "can-a", RobotId: "robot-a", PikaDevice: "/dev/pika-a")], ActiveRobotId: "piper-1"));
            plugin.token = "fixture-backend"; plugin.approved = true; plugin.pikaRole = (long)PikaSessionRole.Owned; plugin.linkCommandsEnabled = true;
            var model = Fixtures.SimulationPose();
            var sample = SimulationFollowSample.Read(model);
            var arm = Fixtures.Arm(joint: (index, joint) =>
            {
                double degrees = sample.MeasuredDegrees[index - 1] + (index == 1 && mismatch ? 2 : 0);
                joint["actual_deg"] = degrees; joint["command_deg"] = degrees;
                joint["max_speed_rad_s"] = 1d; joint["max_acceleration_rad_s2"] = 3d; joint["acceleration_fresh"] = true;
            });
            Snapshot = Fixtures.Snapshot("fixture-backend", arm,
                Fixtures.Pika("/dev/pika-a", PikaDistribution.ForCurrentPlatform(null), plugin.owner, enabled: true, position: PikaProtocol.Angle(20)),
                new JsonObject { ["active"] = false, ["request_id"] = 0L, ["owner"] = plugin.owner,
                    ["following"] = false, ["trajectory_follow"] = false, ["follow_sequence"] = 0L });
            Snapshot["trajectory_follow_supported"] = true;
            plugin.state = Snapshot; plugin.telemetryScope = new TelemetryScope("fixture-backend", "can-a", 7);
            Rpc.Accepted = (route, body) =>
            {
                string action = body["action"]?.GetValue<string>() ?? "";
                if (route == "/api/command" && action == "pose")
                {
                    for (int i = 0; i < 6; i++) Snapshot["devices"]![0]!["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.GetValue<double>();
                    Snapshot["motion"]!["request_id"] = 77L; Snapshot["motion"]!["active"] = false;
                }
                if (route == "/api/pika/position")
                {
                    Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = body["value"]!.GetValue<double>();
                    pikaRequestAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                }
                if (route == "/api/pika/connect")
                {
                    Snapshot["pika"]!["owner"] = plugin.owner; Snapshot["pika"]!["connected"] = true; Snapshot["pika"]!["fresh"] = true;
                    Refresh();
                }
                if (route == "/api/pika/enable")
                {
                    Snapshot["pika"]!["enabled"] = true; Snapshot["pika"]!["armed"] = true; Snapshot["pika"]!["read_only"] = false;
                    Refresh();
                }
                if (route == "/api/command" && action == "follow-start")
                {
                    pendingNativeSequence = 0;
                    pendingNativeCommand = Snapshot["devices"]![0]!["joints"]!.AsArray().Select(joint => joint!["actual_deg"]!.GetValue<double>()).ToArray();
                    if (!DelayNativeAdmissionTelemetry) PublishNativeAdmissionReference();
                }
                if (route == "/api/command" && action == "follow-update")
                {
                    pendingNativeSequence = body["sequence"]!.GetValue<long>();
                    pendingNativeCommand = body["degrees"]!.AsArray().Select(joint => joint!.GetValue<double>()).ToArray();
                    if (!DelayNativeAdmissionTelemetry) PublishNativeAdmissionReference();
                    else Refresh();
                }
                if (route == "/api/command" && action == "hold") { Snapshot["motion"]!["active"] = false; Snapshot["motion"]!["following"] = false; }
                if (ScriptedMissedGrasp && route == "/api/pika/follow-update")
                    Snapshot["pika"]!["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(body["opening_mm"]!.GetValue<double>());
            };
            Rpc.ArmActive = () => Snapshot["motion"]?["active"]?.GetValue<bool>() == true;
            plugin.linkBackendToken = "fixture-backend";
            plugin.link = new RobotLink("source-a", "robot-a", model["joints"]!.AsArray(), Snapshot["devices"]![0]!.AsObject());
            plugin.scene = new JsonObject { ["sceneId"] = "source-a", ["robots"] = new JsonArray(new JsonObject
                { ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a" }) };
            Refresh();
            var context = Fixtures.Context(new PluginStart("fixture", settingsDirectory, Path.GetTempPath()),
                    (Func<object, CancellationToken, Task>)((message, _) =>
                    {
                        var sent = JsonSerializer.SerializeToElement(message);
                        if (sent.GetProperty("type").GetString() == "log") EngineReports.Enqueue(sent.GetProperty("message").GetString()!);
                        return Task.CompletedTask;
                    }),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, body, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested(); Refresh();
                        string action = body["action"]?.GetValue<string>() ?? "";
                        EngineRequests.Enqueue((method, action, (JsonObject)body.DeepClone()));
                        if (method is "scene.robots" or "robot.feedback" or "robot.subscribe" && LinkEditorDelayMilliseconds > 0)
                        { await Task.Delay(LinkEditorDelayMilliseconds, cancellation); Refresh(); }
                        if (method == "scene.robots") return JsonSerializer.SerializeToElement(currentSceneEvidence ?? new JsonObject { ["sceneId"] = "source-a", ["robots"] = new JsonArray(
                            new JsonObject { ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a", ["joints"] = GroupPose()["joints"]!.DeepClone() }) });
                        if (method == "robot.subscribe") return JsonSerializer.SerializeToElement(new { sceneId = "source-a", entityId = "robot-a" });
                        if (method == "host.info") return JsonSerializer.SerializeToElement(new { robotRuntimeFramesSupported = FramesSupported, robotRuntimeTracksSupported = GroupMode });
                        if (method is "robot.runtime.read" or "robot.runtime.frames" && RuntimeReadTimeout)
                            await Task.Delay(Timeout.Infinite, cancellation);
                        if (method == "robot.feedback")
                        {
                            if (Mode != "edit") throw new InvalidOperationException("Robot linking requires an open scene in Edit mode");
                            var measuredPose = Fixtures.SimulationPose();
                            measuredPose["sceneId"] = "source-a";
                            foreach (var joint in measuredPose["joints"]!.AsArray().OfType<JsonObject>())
                                joint["position"] = body["joints"]![joint["name"]!.GetValue<string>()]!.DeepClone();
                            return JsonSerializer.SerializeToElement(new { applied = true, pose = measuredPose });
                        }
                        if (method == "robot.runtime.control")
                        {
                            string nonce = body["prepareId"]?.GetValue<string>() ?? "";
                            if (action == DelayedRuntimeAction) { RuntimeReached.TrySetResult(); await ReleaseRuntime.Task; }
                            if (action == "arm-next-play")
                            {
                                if (Mode != "edit") throw new InvalidOperationException("Arm-next-play requires Edit");
                                prepareId = nonce; groupArmed = true;
                                return JsonSerializer.SerializeToElement(new { applied = true, mode = Mode, prepareId, startArmedForNextPlay = true, runId = "" });
                            }
                            if (action is "pause" or "resume" or "set-time-scale" or "mark-start-ready" or "activate-start" &&
                                (Mode == "edit" || body["runId"]?.GetValue<string>() != RunId))
                                throw new InvalidOperationException("The exact runtime run changed");
                            if (action == "mark-start-ready")
                            {
                                if (nonce != prepareId || canceledPreparations.ContainsKey(nonce)) throw new InvalidOperationException("Preparation was canceled");
                                readyGate = true;
                            }
                            if (action == "set-time-scale" && Mode != "pause") throw new InvalidOperationException("Set time scale requires Pause");
                            if (action == "cancel-start")
                            {
                                canceledPreparations.TryAdd(nonce, 0);
                                if (body["runId"] is JsonValue expectedRun && expectedRun.GetValue<string>() != RunId)
                                    throw new InvalidOperationException("The exact preparation run changed");
                                if (nonce == prepareId) { groupArmed = readyGate = startRequested = false; if (Mode == "preparing") Mode = "edit"; else if (Mode != "edit") Mode = "pause"; }
                            }
                            if (body["timeScale"] is JsonValue requested && requested.TryGetValue<double>(out double requestedScale) &&
                                (!double.IsFinite(requestedScale) || requestedScale is < .01 or > 1))
                                throw new InvalidOperationException("Native time scale must be within .01 and 1");
                            if (body["timeScale"] is JsonValue value && value.TryGetValue<double>(out double next)) scale = next;
                            if (action == "pause") Mode = "pause";
                            if (action == "activate-start")
                            {
                                if (!readyGate || !startRequested || nonce != prepareId || canceledPreparations.ContainsKey(nonce) || Mode != "pause")
                                    throw new InvalidOperationException("The exact prepared Play request is unavailable");
                                activated = true; Mode = "play";
                            }
                            if (action == "resume") { if (!readyGate || !activated) throw new InvalidOperationException("The prepared start is not activated"); Mode = "play"; }
                            AppliedEngineRequests.Enqueue((method, action, (JsonObject)body.DeepClone()));
                            if (action != "activate-start") return JsonSerializer.SerializeToElement(new { applied = true, mode = Mode });
                        }
                        if (Mode == "play" && action != "activate-start") simulationTime += .005;
                        var pose = GroupPose(simulationTime, Interlocked.Increment(ref sequence));
                        if (ScriptedMissedGrasp)
                        {
                            foreach (var joint in pose["joints"]!.AsArray().OfType<JsonObject>().Where(j => Enumerable.Range(1, 6).Any(i => j["name"]!.GetValue<string>().EndsWith("joint" + i, StringComparison.Ordinal))))
                                joint["position"] = 0d;
                            foreach (var actuator in pose["actuators"]!.AsArray().OfType<JsonObject>().Where(a => a["name"]!.GetValue<string>() != "gripper"))
                                actuator["control"] = 0d;
                        }
                        pose["mode"] = Mode; pose["timeScale"] = scale;
                        pose["startPreparationSupported"] = PreparationSupported;
                        pose["startActivationSupported"] = ActivationSupported;
                        pose["startRequested"] = startRequested;
                        pose["startActivated"] = activated;
                        if (method == "robot.runtime.control") pose["applied"] = true;
                        pose["prepareId"] = WrongPlayPreparation && method == "robot.runtime.read" && Mode == "pause" ? "different-preparation" : prepareId;
                        pose["startReady"] = readyGate; pose["startMatchRequired"] = !readyGate;
                        pose["ready"] = Mode is "edit" or "pause" or "play";
                        pose["timeMode"] = TimeMode;
                        pose["runId"] = RunId; pose["sceneId"] = RunId;
                        pose["capturedMonotonicNs"] = ((long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency))).ToString();
                        pose["capturedAtUnixMicroseconds"] = ((DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10).ToString();
                        if (Mode == "edit") { pose["runId"] = ""; pose["sceneId"] = "source-a"; }
                        if (InvalidModel) pose["actuators"]![0]!["positionControl"] = false;
                        if (UnsettledStart) pose["joints"]![0]!["position"] = .2;
                        if (action == "activate-start" && ChangeActivationFrame) pose["joints"]![0]!["position"] = .3;
                        pose["episodeId"] = EpisodeId; pose["episodeOrdinal"] = 0L; pose["episodeComplete"] = TerminalFrame;
                        pose["episodeSucceeded"] = TerminalFrame;
                        if (method == "robot.runtime.frames")
                        {
                            if (GroupMode && action == "start") capturedGroupTracks = body["tracks"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToArray();
                            var source = SimulationFollowSample.Read(pose);
                            if (SealedFrameCursor == 0)
                            {
                            if (FrameCursorGap) frameCursor++;
                            var nextFrame = new JsonObject { ["cursor"] = (++frameCursor).ToString(), ["simulationTimeNs"] = pose["simulationTimeNs"]!.DeepClone(),
                                ["mujocoTimeSeconds"] = source.SimTimeSeconds,
                                ["commandJoints"] = JsonSerializer.SerializeToNode(source.CommandDegrees.Select(q => q * Math.PI / 180)),
                                ["measuredJoints"] = JsonSerializer.SerializeToNode(source.MeasuredDegrees.Select(q => q * Math.PI / 180)),
                                ["commandOpeningMm"] = source.CommandOpeningMm, ["measuredOpeningMm"] = source.MeasuredOpeningMm,
                                ["episodeId"] = EpisodeId, ["episodeOrdinal"] = 0L, ["phase"] = simulationTime < .015 ? "begin:0" : "wait:1",
                                ["episodeComplete"] = TerminalFrame, ["episodeSucceeded"] = TerminalFrame };
                            if (GroupMode)
                            {
                                var rows = new JsonArray();
                                foreach (var profile in plugin.preferences.Robots!.Where(p => capturedGroupTracks.Contains(p.Id)))
                                {
                                    var armSample = SimulationFollowSample.Read(pose, profile.RobotJointPrefix);
                                    rows.Add(new JsonObject { ["id"] = profile.Id, ["commandJoints"] = JsonSerializer.SerializeToNode(armSample.CommandDegrees.Select(q=>q*Math.PI/180)),
                                        ["measuredJoints"] = JsonSerializer.SerializeToNode(armSample.MeasuredDegrees.Select(q=>q*Math.PI/180)),
                                        ["commandOpeningMm"] = armSample.CommandOpeningMm, ["measuredOpeningMm"] = armSample.MeasuredOpeningMm });
                                }
                                nextFrame["tracks"] = rows;
                            }
                            if (ScriptedMissedGrasp)
                            {
                                int row = sourceFrames.Count;
                                string prefix = ScriptedSourceMiss ? "missed-grasp-" : "";
                                nextFrame["phase"] = row switch { 0 => "begin:0", 1 or 2 => "gripper:1", 3 => prefix + "wait:2",
                                    4 => prefix + "gripper:3", 5 => prefix + "home:4", _ => prefix + "finish:5" };
                                nextFrame["commandOpeningMm"] = row is 2 or 3 ? 10d : 20d;
                                nextFrame["measuredOpeningMm"] = row is 2 or 3 ? 14d : 20d;
                                nextFrame["episodeComplete"] = TerminalFrame = row >= 6;
                                nextFrame["episodeSucceeded"] = TerminalFrame && !ScriptedSourceMiss;
                                pose["episodeComplete"] = TerminalFrame;
                                pose["episodeSucceeded"] = TerminalFrame && !ScriptedSourceMiss;
                                if (nextFrame["tracks"] is JsonArray scriptedTracks)
                                    foreach (var track in scriptedTracks)
                                        foreach (string key in new[] { "commandJoints", "measuredJoints", "commandOpeningMm", "measuredOpeningMm" })
                                            track![key] = nextFrame[key]!.DeepClone();
                            }
                            sourceFrames.Add(nextFrame);
                            if (TerminalFrame) SealedFrameCursor = frameCursor;
                            }
                            ulong after = ulong.Parse(body["afterFrameCursor"]!.GetValue<string>());
                            pose["oldestCursor"] = "1"; pose["newestCursor"] = frameCursor.ToString(); pose["sourcePausedForFollower"] = false;
                            pose["frames"] = new JsonArray(sourceFrames.Where(row => ulong.Parse(row["cursor"]!.GetValue<string>()) > after)
                                .Take(body["maxFrames"]!.GetValue<int>()).Select(row => (JsonNode)row.DeepClone()).ToArray());
                            if (FrameSourceAgeMs > 0) pose["capturedAtUnixMicroseconds"] = ((DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10 - (long)(FrameSourceAgeMs * 1000)).ToString();
                        }

                        if (method == "robot.runtime.frames" && action != "start")
                        {
                            if (LoseNextOrderedReply)
                            {
                                LoseNextOrderedReply = false;
                                LostReplyCursor = body["afterFrameCursor"]!.GetValue<string>();
                                OrderedReplyLost.TrySetResult();
                                await Task.Delay(Timeout.Infinite, cancellation);
                            }
                            Interlocked.Increment(ref CompletedOrderedReads);
                        }
                        if (action == "activate-start" && FreezeClockAtActivation && followClock is FollowTestClock clock)
                            clock.Freeze();
                        return JsonSerializer.SerializeToElement(pose);
                    }));
            plugin.engine = context;
        }

        public void PublishNativeAdmissionReference()
        {
            DelayNativeAdmissionTelemetry = false;
            Snapshot["motion"]!["request_id"] = 77L; Snapshot["motion"]!["active"] = true;
            Snapshot["motion"]!["following"] = true; Snapshot["motion"]!["trajectory_follow"] = true;
            Snapshot["motion"]!["follow_sequence"] = pendingNativeSequence;
            if (GroupMode)
            {
                var motion = Snapshot["devices"]![0]!["motion"]!;
                motion["request_id"] = 77L; motion["active"] = true; motion["following"] = true;
                motion["trajectory_follow"] = true; motion["follow_sequence"] = pendingNativeSequence;
            }
            for (int joint = 0; joint < 6; joint++) Snapshot["devices"]![0]!["joints"]![joint]!["command_deg"] = pendingNativeCommand[joint];
            Refresh();
        }

        public void Refresh()
        {
            if (FreezeTelemetry) return;
            if (publishResumeFrameCopies)
            {
                lock (resumeFrameGate)
                {
                    // Resume can retain a receipt across several checks.
                    var frame = Snapshot.DeepClone().AsObject();
                    plugin.state = frame;
                    Publish(frame);
                }
                return;
            }
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            Snapshot["pika"]!["last_motor_request"] = "pika-position-fixture";
            Snapshot["pika"]!["request_time_s"] = pikaRequestAt;
            Snapshot["pika"]!["feedback_time_s"] = now;
            Snapshot["pika"]!["pending"] = false;
            if (GroupMode) foreach (var pika in Snapshot["pikas"]!.AsArray().OfType<JsonObject>()) pika["feedback_time_s"] = now;
            Publish(Snapshot);

            void Publish(JsonObject frame) => plugin.telemetry.Publish(
                new(frame, "fixture-backend", Interlocked.Increment(ref sequence), followClock.GetTimestamp(), EnvelopeAgeMs));
        }
        public void RemoveMeasuredLink(int editorDelayMilliseconds) { plugin.link = null; LinkEditorDelayMilliseconds = editorDelayMilliseconds; }
        public ConcurrentQueue<UiEvent> DiscardedUiEvents { get; } = new();
        public void UseUiDispatcher()
        {
            plugin.uiEvents = new UiEventDispatch((input, cancellation) => plugin.HandleEventCoreAsync(input, cancellation),
                () => plugin.ControlInputEpoch,
                error => throw new InvalidOperationException("Unexpected fixture UI actor failure", error),
                discarded: (input, _) => DiscardedUiEvents.Enqueue(input));
        }
        public Task Navigate(string action) => plugin.WorkspaceEvent(action, CancellationToken.None);
        public Task Click(string id)
        {
            Refresh();
            string wireId = WorkspaceEventScope.Wrap(plugin.workspaceViewEpoch, "piper-1", id);
            return plugin.HandleEventAsync(new("fixture", wireId, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void SetDetail(string detail) => plugin.groupFollowDetail = detail;
        public string GroupSetupFailure()
        {
            var outcome = plugin.groupFollowOutcome;
            return outcome.Stage == PipersPlugin.GroupFollowStage.SetupFailed ? outcome.Summary : "";
        }
        public Task PublishMeasured() { Refresh(); return plugin.PublishMeasuredScene(CancellationToken.None); }
        public async Task Gap(Func<CancellationToken, Task> worker)
        {
            using var duration = new CancellationTokenSource(80);
            try { await worker(duration.Token); } catch (OperationCanceledException) { }
        }
        public Task StopEvent() => plugin.HandleEventAsync(new("fixture", "robot.piper-1.simulation.stop", "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            Rpc.ReleaseDelayed.TrySetResult();
            ReleaseRuntime.TrySetResult();
            if (GroupActive)
                await plugin.StopGroupSimulationFollowing("Fixture disposed", CancellationToken.None);
            groupTelemetryStop.Cancel(); if (groupTelemetryWorker is not null) await groupTelemetryWorker;
            if (plugin.uiEvents is { } dispatch) await dispatch.DisposeAsync();
            plugin.shutdown.Cancel();
            plugin.channel.Dispose(); Commands.Dispose();
            if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, recursive: true);
        }
    }
}
