using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed record Preferences(double Speed = 10, double Step = 1, double Tolerance = 0.5, string RobotId = "",
    CameraPreferences? Bench = null, CameraPreferences? Gripper = null, CameraPreferences? Depth = null,
    string DepthSerial = "",
    Dictionary<string, SimulationCameraPreferences>? SimulationCameras = null, BackendSettings? Backend = null,
    RobotProfile[]? Robots = null, string ActiveRobotId = "");

public sealed partial class PipersPlugin : ILuckyPlugin
{
    private const string Panel = "luckyrobots.pipers.controls";
    private readonly string owner = Guid.NewGuid().ToString();
    private readonly CancellationTokenSource shutdown = new();
    private readonly GrpcChannel channel;
    private readonly SemaphoreSlim commands = new(1);
    private PluginContext engine = null!;
    private PiperControl.PiperControlClient client = null!;
    private Preferences preferences = new();
    private volatile JsonObject? state;
    private long stateAt;
    private string token = "", message = "Connecting to the existing Piper server…";
    private long lease, lastCommand;
    private bool approved, pikaOwned;
    private RobotLink? link;
    private bool linkCommandsEnabled, recoveryInProgress;
    private double? linkErrorDegrees;
    private JsonObject? scene;
    private Task? worker;
    private readonly Dictionary<string, CameraCapture> cameras = [];
    private readonly ConcurrentDictionary<string, string> cameraStatus = new();
    private readonly ConcurrentDictionary<string, (int Width, int Height)> cameraDimensions = new();
    private readonly HashSet<string> cameraSettingsOpen = [];

    public PipersPlugin(string address = "http://127.0.0.1:8766")
    {
        var uri = new Uri(address);
        if (!uri.IsLoopback || uri.Scheme != "http") throw new ArgumentException("The controller must use a local HTTP/2 endpoint.");
        channel = GrpcChannel.ForAddress(uri);
    }

    public async Task StartAsync(PluginContext context, CancellationToken cancellation)
    {
        engine = context;
        Directory.CreateDirectory(engine.Start.SettingsDirectory);
        string file = Path.Combine(engine.Start.SettingsDirectory, "preferences.json");
        if (File.Exists(file))
        {
            try { preferences = JsonSerializer.Deserialize<Preferences>(await File.ReadAllTextAsync(file, cancellation), PluginProtocol.Json) ?? new(); }
            catch (JsonException) { message = "Invalid saved preferences; defaults restored."; }
        }
        legacyImportPending = preferences.Robots is not { Length: > 0 };
        preferences = RobotProfileRouting.Normalize(preferences);
        LoadAlignmentSummary();
        knownCanIds = preferences.Robots!.Select(p => p.CanDeviceId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        client = new(channel);
        await LoadCameraCalibration(cancellation);
        try { await DiscoverSimulationCameras(cancellation); }
        catch (PluginRequestException error) { message = "Simulation camera discovery: " + error.Message; }
        try
        {
            scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
            Materialize(scene);
        }
        catch (PluginRequestException error) { message = "Scene robot discovery: " + error.Message; }
        await Render(cancellation);
        worker = Watch(shutdown.Token);
        foreach (string camera in new[] { "bench", "gripper", "depth" }) await RestartCamera(camera);
    }

    private JsonObject Device(string? expectedDeviceId = null, long? expectedGeneration = null, string? expectedToken = null)
    {
        var snapshot = state ?? throw new InvalidOperationException("Waiting for the controller server.");
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref stateAt)).TotalMilliseconds > 500)
            throw new InvalidOperationException("Robot feedback is stale.");
        if (switchingProfile) throw new InvalidOperationException("Changing robot assignment; wait for measured feedback.");
        var device = RobotProfileRouting.Device(preferences, snapshot);
        if (expectedDeviceId is not null && device["id"]?.GetValue<string>() != expectedDeviceId ||
            expectedGeneration.HasValue && device["generation"]?.GetValue<long>() != expectedGeneration.Value ||
            expectedToken is not null && token != expectedToken)
            throw new InvalidOperationException("The arm connection or backend session changed; the previous confirmation no longer authorizes commands.");
        return device;
    }

    private async Task Watch(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var stream = client.WatchState(new(), cancellationToken: cancellation);
                long lastUi = 0, lastHeartbeat = 0;
                await foreach (var frame in stream.ResponseStream.ReadAllAsync(cancellation))
                {
                    var next = JsonNode.Parse(frame.Json)!.AsObject();
                    Materialize(next);
                    string nextToken = next["token"]!.GetValue<string>();
                    if (token != nextToken) { teleoperating = false; link = null; linkCommandsEnabled = false; recoveryInProgress = false; lease = 0; approved = false; pikaOwned = pikaMonitoring = false; teachingSeen = approvalBeforeTeaching = false; token = nextToken; }
                    state = next;
                    await ObserveProfileRouting(next, cancellation);
                    if (message.StartsWith("Connecting to", StringComparison.Ordinal) || message.StartsWith("Controller unavailable", StringComparison.Ordinal))
                        message = "Connected · monitoring live robot feedback";
                    Interlocked.Exchange(ref stateAt, Stopwatch.GetTimestamp());
                    await ObserveTeaching(cancellation);
                    if (Stopwatch.GetElapsedTime(lastHeartbeat).TotalMilliseconds > 200)
                    {
                        lastHeartbeat = Stopwatch.GetTimestamp();
                        if (next["motion"]?["request_id"]?.GetValue<long>() >= lease && next["motion"]?["active"]?.GetValue<bool>() != true) lease = 0;
                        if (lease != 0)
                        {
                            try { await Call("/api/heartbeat", new JsonObject { ["request_id"] = lease, ["owner"] = owner }, cancellation); }
                            catch (InvalidOperationException) { lease = 0; }
                        }
                        if (pikaOwned || pikaMonitoring)
                        {
                            try { await Call("/api/pika/heartbeat", new JsonObject { ["owner"] = owner }, cancellation); }
                            catch (InvalidOperationException error) { pikaOwned = pikaMonitoring = false; message = "Pika connection: " + error.Message; }
                        }
                    }
                    if (Stopwatch.GetElapsedTime(lastUi).TotalMilliseconds < 100) continue;
                    lastUi = Stopwatch.GetTimestamp();
                    if (lastCommand != 0 && next["motion"]?["request_id"]?.GetValue<long>() == lastCommand)
                    {
                        message = next["message"]?.GetValue<string>() ?? message;
                        if (next["motion"]?["active"]?.GetValue<bool>() != true) lastCommand = 0;
                    }
                    if (link is { } waiting && TeachingResetAssisted.ExpectedFeedbackGap(teachingResetBusy, next, waiting.DeviceId, waiting.Generation))
                        linkErrorDegrees = null;
                    else if (link is { } current)
                    {
                        try {
                            if (Device()["recovery_required"]?.GetValue<bool>() == true) linkCommandsEnabled = false;
                            var measuredDevice = Device();
                            var requested = current.Feedback(measuredDevice, next["pika"] as JsonObject, PikaMatchesProfile());
                            if (teleoperating && next["motion"]?["finishing_target"]?.GetValue<bool>() == true && next["motion"]?["owner"]?.GetValue<string>() == owner)
                            {
                                int jointIndex = Array.IndexOf(current.JointNames, followJoint);
                                double? target = jointIndex >= 0 ? Device()["joints"]?[jointIndex]?["target_deg"]?.GetValue<double>() : null;
                                if (target.HasValue)
                                {
                                    requested["leaderJoint"] = followJoint;
                                    requested["leaderPosition"] = target.Value * Math.PI / 180;
                                }
                            }
                            var reply = await engine.RequestAsync("robot.feedback", requested, cancellation);
                            if (teleoperating && (!linkCommandsEnabled || (reply.TryGetProperty("teleoperating", out var mode) && !mode.GetBoolean())) && await commands.WaitAsync(0, cancellation))
                            {
                                try { await PauseTeleoperation(cancellation); }
                                finally { commands.Release(); }
                            }
                            if (reply.TryGetProperty("applied", out var applied) && applied.GetBoolean())
                            {
                                var acknowledgement = JsonNode.Parse(reply.GetRawText())!.AsObject();
                                linkErrorDegrees = current.FeedbackError(requested, acknowledgement);
                                acknowledgedScenePose = acknowledgement;
                                acknowledgedSceneAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                SampleAlignment(measuredDevice);
                            }
                        }
                        catch (Exception error) when (error is not OperationCanceledException) { await FinishAlignment("Linked scene unavailable", cancellation); link = null; message = error.Message; }
                    }
                    if (recoveryInProgress)
                    {
                        var device = Device();
                        if (device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true)
                        {
                            recoveryInProgress = false;
                            message = "Recovery verified. The interrupted move was discarded. Enable robot when ready; measured scene tracking continues.";
                        }
                        else if (RecoveryPlan.Inspect(device, next["motion"]?["active"]?.GetValue<bool>() == true).Action == "verify-reset" && await commands.WaitAsync(0, cancellation))
                        {
                            try { await Command("verify-reset", null, cancellation, recoveryAcknowledged: true); recoveryInProgress = false; }
                            catch (InvalidOperationException error) { message = error.Message; recoveryInProgress = false; }
                            finally { commands.Release(); }
                        }
                    }
                    await Update(cancellation);
                }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
                await FinishAlignment("Controller or scene connection interrupted", cancellation);
                teleoperating = false; link = null; lease = 0; pikaOwned = pikaMonitoring = false; approved = false; state = null;
                message = error is RpcException { StatusCode: StatusCode.Unavailable }
                    ? "Controller unavailable on 127.0.0.1:8766. Start the updated backend; reconnecting automatically."
                    : "Controller connection interrupted: " + error.Message;
                await Update(cancellation);
                await Task.Delay(1000, cancellation);
            }
        }
    }

    private async Task<JsonObject> Call(string route, JsonObject body, CancellationToken cancellation, string? expectedToken = null)
    {
        string authorization = expectedToken ?? token;
        if (expectedToken is not null && token != expectedToken) throw new InvalidOperationException("The controller session changed; no command was sent under the old confirmation.");
        var reply = await client.ExecuteAsync(new ControlRequest { Route = route, Json = body.ToJsonString() },
            new Metadata { { "x-piper-token", authorization } }, deadline: DateTime.UtcNow.AddSeconds(route.EndsWith("/connect") ? 20 : 3), cancellationToken: cancellation);
        var result = JsonNode.Parse(reply.Json)!.AsObject();
        if (reply.Status >= 400) throw new InvalidOperationException(result["error"]?.GetValue<string>() ?? "Controller rejected the request.");
        return result;
    }

    private static void Materialize(JsonNode? node)
    {
        // JsonNode lazily builds child dictionaries. Finish that work on the
        // receive thread before publishing a snapshot to UI/control readers.
        if (node is JsonObject obj) foreach (var member in obj) Materialize(member.Value);
        else if (node is JsonArray array) foreach (var child in array) Materialize(child);
    }

    private async Task Command(string action, JsonObject? fields, CancellationToken cancellation, bool recoveryAcknowledged = false, bool teachingAcknowledged = false,
        string? expectedDeviceId = null, long? expectedGeneration = null, string? expectedToken = null)
    {
        bool recoveryOnly = recoveryAcknowledged && action is "reset" or "verify-reset" or "verify-current";
        bool teachingOnly = teachingAcknowledged && action == "teach-return";
        if (!approved && !recoveryOnly && !teachingOnly) throw new InvalidOperationException("Use Enable robot and confirm the workspace is clear first.");
        var device = Device(expectedDeviceId, expectedGeneration, expectedToken);
        if (!teachingOnly && !recoveryOnly && TeachingState.Inspect(device).BlocksTargets)
            throw new InvalidOperationException("The physical teaching controller owns the arm. End teaching and return to CAN before sending new targets.");
        var body = fields ?? new();
        body["action"] = action;
        body["device_id"] = expectedDeviceId is not null ? JsonValue.Create(expectedDeviceId) : device["id"]!.DeepClone();
        body["generation"] = expectedGeneration.HasValue ? JsonValue.Create(expectedGeneration.Value) : device["generation"]!.DeepClone();
        body["owner"] = owner; body["standalone"] = true; body["clear"] = true; body["supported"] = true; body["reset_completed"] = true;
        body["speed_percent"] = preferences.Speed; body["tolerance_deg"] = preferences.Tolerance;
        var result = await Call("/api/command", body, cancellation, expectedToken);
        lease = result["request_id"]?.GetValue<long>() ?? lease;
        lastCommand = result["request_id"]?.GetValue<long>() ?? 0;
        message = result["message"]?.GetValue<string>() ?? "Command accepted.";
    }

    public async Task HandleEventAsync(UiEvent input, CancellationToken cancellation)
    {
        if (teachingResetBusy)
        {
            string pending = UnscopeEvent(input.WidgetId);
            if (pending is "stop" or "teaching.reset.abort") CancelResetAssisted();
            else message = "Reset-assisted return is in progress. Keep the arm supported; use Cancel / STOP to interrupt it.";
            await Update(cancellation); return;
        }
        await commands.WaitAsync(cancellation);
        try
        {
            string id = UnscopeEvent(input.WidgetId);
            if (id.Length == 0) return; // Late gesture from a previously selected robot.
            if (id.StartsWith("profiles.", StringComparison.Ordinal))
            {
                await ProfileEvent(id[9..], input.Value, cancellation);
                await Render(cancellation); await Update(cancellation); return;
            }
            if (teleoperating && id is "stop" or "disable" or "recovery.run" or "robots.select" or "link")
            {
                try { await PauseTeleoperation(cancellation); }
                catch (Exception error) when (error is not OperationCanceledException) { message = error.Message; }
            }
            if (id.StartsWith("backend.", StringComparison.Ordinal))
                await BackendEvent(id[8..], input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString() ?? "" : "", cancellation);
            else if (id.StartsWith("teaching.", StringComparison.Ordinal))
                await TeachingEvent(id[9..], cancellation);
            else if (id.StartsWith("alignment.", StringComparison.Ordinal))
                await AlignmentEvent(id[10..], input.Value, cancellation);
            else if (id == "stop")
            {
                await FinishAlignment("Operator stop", cancellation);
                string stopDevice = ActiveProfile.CanDeviceId;
                if (stopDevice.Length == 0) throw new InvalidOperationException("Assign this robot's CAN adapter before sending its stop.");
                link = null;
                // Stop keeps its best-effort behavior when feedback is stale;
                // the backend still rejects a changed selected adapter.
                await Call("/api/stop", new JsonObject { ["owner"] = owner, ["device_id"] = stopDevice }, cancellation);
                lease = 0;
                if (pikaOwned) await Call("/api/pika/disable", new JsonObject { ["owner"] = owner }, cancellation);
            }
            else if (id == "enable")
            {
                if (!approved) { await Patch(new Dictionary<string, JsonObject> { ["safety"] = new() { ["visible"] = true } }, cancellation); return; }
                await Enable(cancellation);
            }
            else if (id == "safety.accept") { approved = true; await Enable(cancellation); await Render(cancellation); }
            else if (id == "safety.cancel") await Patch(new Dictionary<string, JsonObject> { ["safety"] = new() { ["visible"] = false } }, cancellation);
            else if (id == "disable")
            {
                await FinishAlignment("Robot disabled", cancellation);
                link = null;
                await Command("disable", null, cancellation);
                if (pikaOwned) await Call("/api/pika/disable", new JsonObject { ["owner"] = owner }, cancellation);
            }
            else if (id == "recovery.run")
            {
                var plan = RecoveryPlan.Inspect(Device(), state?["motion"]?["active"]?.GetValue<bool>() == true);
                if (plan.Action is null) throw new InvalidOperationException(plan.NextStep);
                linkCommandsEnabled = false;
                // The explicitly labelled recovery action acknowledges support;
                // it does not enable general motion controls for a new session.
                await Command(plan.Action, null, cancellation, recoveryAcknowledged: true);
                recoveryInProgress = plan.Action == "reset";
            }
            else if (id.StartsWith("camera.", StringComparison.Ordinal))
            {
                string[] parts = id.Split('.'); string camera = parts[1];
                var settings = CameraSettings(camera);
                if (parts[2] == "settings")
                {
                    if (!cameraSettingsOpen.Add(camera)) cameraSettingsOpen.Remove(camera);
                    await Patch(new Dictionary<string, JsonObject> { ["camera." + camera + ".settings.form"] = new() { ["visible"] = cameraSettingsOpen.Contains(camera) } }, cancellation);
                }
                else if (parts[2].StartsWith("sim", StringComparison.Ordinal))
                {
                    await CameraComparisonEvent(camera, parts[2], input.Value, cancellation);
                }
                else if (parts[2].StartsWith("calibration", StringComparison.Ordinal))
                    await CameraCalibrationEvent(camera, parts[2], cancellation);
                else
                {
                    if (parts[2] == "device") settings = settings with { Device = input.Value.GetString() ?? "", Enabled = true };
                    if (parts[2] == "toggle") settings = settings with { Enabled = !settings.Enabled };
                    if (parts[2] == "serial") preferences = preferences with { DepthSerial = input.Value.GetString() ?? "" };
                    preferences = camera switch { "bench" => preferences with { Bench = settings }, "gripper" => preferences with { Gripper = settings }, _ => preferences with { Depth = settings } };
                    await Save(cancellation); await RestartCamera(camera);
                }
            }
            else if (id == "robots.refresh")
            {
                scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
                if (scene["robots"] is JsonArray { Count: 1 } available && string.IsNullOrEmpty(preferences.RobotId))
                    preferences = preferences with { RobotId = available[0]!["entityId"]!.GetValue<string>() };
                await Render(cancellation);
            }
            else if (id == "robots.select")
            {
                await FinishAlignment("Scene robot changed", cancellation);
                link = null;
                preferences = preferences with { RobotId = scene!["robots"]![input.Value.GetInt32()]!["entityId"]!.GetValue<string>() };
                await Save(cancellation);
            }
            else if (id == "link")
            {
                if (link is not null) { await FinishAlignment("Robot unlinked", cancellation); link = null; linkCommandsEnabled = false; message = "Robot unlinked · monitoring continues"; await engine.RequestAsync("robot.unsubscribe", cancellation: cancellation); }
                else
                {
                    scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
                    if (scene["robots"] is JsonArray { Count: 1 } available && string.IsNullOrEmpty(preferences.RobotId))
                        preferences = preferences with { RobotId = available[0]!["entityId"]!.GetValue<string>() };
                    var robot = scene["robots"]!.AsArray().OfType<JsonObject>().SingleOrDefault(r => r["entityId"]?.GetValue<string>() == preferences.RobotId)
                        ?? throw new InvalidOperationException("Select the scene robot before linking.");
                    var next = new RobotLink(scene["sceneId"]!.GetValue<string>(), preferences.RobotId, robot["joints"]!.AsArray(), Device());
                    var requested = next.Feedback(Device(), state?["pika"] as JsonObject, PikaMatchesProfile());
                    var applied = JsonNode.Parse((await engine.RequestAsync("robot.feedback", requested, cancellation)).GetRawText())!.AsObject();
                    if (applied["applied"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException("Finish the active viewport joint edit, then link again.");
                    linkErrorDegrees = next.FeedbackError(requested, applied);
                    await engine.RequestAsync("robot.subscribe", new JsonObject { ["sceneId"] = next.SceneId, ["entityId"] = next.EntityId }, cancellation);
                    await Save(cancellation);
                      link = next; linkCommandsEnabled = Device()["recovery_required"]?.GetValue<bool>() != true;
                      await MonitorLinkedPika(cancellation);
                      message = "Measured pose linked. Start Teleoperate to drag joints; release finishes the target. Calibration is not verified.";
                      await Render(cancellation);
                }
            }
            else if (id == "teleoperate.toggle")
            {
                if (teleoperating) { await PauseTeleoperation(cancellation); message = "Teleoperate paused · measured pose tracking continues"; }
                else await SelectTeleoperationJoint(teleoperationJoint, cancellation);
            }
            else if (id == "motion.hold")
            {
                if (state?["motion"]?["active"]?.GetValue<bool>() == true && state["motion"]?["owner"]?.GetValue<string>() == owner)
                {
                    if (followRequest != 0) await HoldFollowing(cancellation);
                    else await Command("hold", new JsonObject { ["request_id"] = state["motion"]!["request_id"]!.DeepClone() }, cancellation);
                    message = "Movement stopped · holding measured pose";
                }
            }
            else if (id.StartsWith("teleoperate.joint.", StringComparison.Ordinal))
                await SelectTeleoperationJoint("joint" + id.Split('.')[2], cancellation);
            else if (id == "engine.robot.follow")
                await FollowViewport(JsonNode.Parse(input.Value.GetRawText())!.AsObject(), cancellation);
            else if (id == "engine.robot.target" && link is { } linked)
            {
                if (!linkCommandsEnabled) throw new InvalidOperationException("Scene tracking is read-only after a stop. Recover, then Enable robot before sending a new target.");
                var target = JsonNode.Parse(input.Value.GetRawText())!.AsObject();
                await Command("pose", new JsonObject { ["degrees"] = JsonSerializer.SerializeToNode(linked.Target(target, Device())), ["synchronized"] = true }, cancellation);
            }
            else if (id == "speed" || id == "step" || id == "tolerance")
            {
                double value = input.Value.GetDouble();
                preferences = id switch { "speed" => preferences with { Speed = Math.Clamp(value, 0, 100) }, "step" => preferences with { Step = Math.Clamp(value, .1, 5) }, _ => preferences with { Tolerance = Math.Clamp(value, .05, 1) } };
                await Save(cancellation);
            }
            else if (id == "zero.all") await Command("pose", new() { ["degrees"] = JsonSerializer.SerializeToNode(new double[6]), ["synchronized"] = true }, cancellation);
            else if (id.StartsWith("joint.", StringComparison.Ordinal))
            {
                string[] parts = id.Split('.'); int joint = int.Parse(parts[1]);
                string action = parts[2];
                await Command(action == "target" ? "joint" : action == "zero" ? "zero" : "jog",
                    new() { ["joint"] = joint, ["degrees"] = action == "target" ? input.Value.GetDouble() : action == "plus" ? preferences.Step : action == "minus" ? -preferences.Step : 0 }, cancellation);
            }
            else if (id == "pika.target")
            {
                Device();
                if (!PikaMatchesProfile()) throw new InvalidOperationException("The Pika serial assignment does not match this robot.");
                if (!pikaOwned || state?["pika"]?["armed"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("Pika is monitoring only. Use Enable robot before commanding its opening.");
                await Call("/api/pika/position", new JsonObject { ["owner"] = owner, ["value"] = PikaProtocol.Angle(input.Value.GetDouble()) }, cancellation);
            }
            await Update(cancellation);
        }
        catch (Exception error) when (error is not OperationCanceledException) { message = error.Message; await Update(cancellation); }
        finally { commands.Release(); }
    }

    private async Task Enable(CancellationToken cancellation, bool includePika = true, string? expectedDeviceId = null, long? expectedGeneration = null, string? expectedToken = null)
    {
        var device = Device(expectedDeviceId, expectedGeneration, expectedToken);
        if (TeachingState.Inspect(device).BlocksTargets)
            throw new InvalidOperationException("End hand guiding/playback and return to CAN control before enabling plugin targets.");
        if (includePika) await ConfigureProfilePika(cancellation);
        if (device["recovery_required"]?.GetValue<bool>() == true)
            await Command(device["reset_pending"]?.GetValue<bool>() == true ? "verify-reset" : "verify-current", null, cancellation,
                expectedDeviceId: expectedDeviceId, expectedGeneration: expectedGeneration, expectedToken: expectedToken);
        if (!device["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true))
        {
            await Command("enable", null, cancellation, expectedDeviceId: expectedDeviceId, expectedGeneration: expectedGeneration, expectedToken: expectedToken);
            long request = lease;
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                if (TeachingState.Inspect(Device(expectedDeviceId, expectedGeneration, expectedToken)).BlocksTargets) throw new InvalidOperationException("Teaching took control during enable. Previous targets were discarded; finish the teaching return flow first.");
                var motion = state?["motion"];
                if (motion?["request_id"]?.GetValue<long>() == request && motion["active"]?.GetValue<bool>() == false)
                {
                    var enabledDevice = Device(expectedDeviceId, expectedGeneration, expectedToken);
                    if (enabledDevice["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true) && enabledDevice["recovery_required"]?.GetValue<bool>() != true) break;
                    throw new InvalidOperationException(state?["message"]?.GetValue<string>() ?? "The robot did not enable all six motors.");
                }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10)
                    throw new InvalidOperationException("Waiting for robot enable feedback timed out. Check the controller status.");
                await Task.Delay(25, cancellation);
            }
        }
        if (includePika && PikaMatchesProfile() && state?["pika"]?["configured"]?.GetValue<bool>() == true)
        {
            await Call("/api/pika/connect", new JsonObject { ["owner"] = owner, ["take_over"] = true }, cancellation);
            pikaOwned = pikaMonitoring = true;
            long started = Stopwatch.GetTimestamp();
            while (state?["pika"]?["fresh"]?.GetValue<bool>() != true)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3)
                    throw new InvalidOperationException("Pika connected but fresh motor feedback has not arrived. Enable again when feedback is live.");
                await Task.Delay(25, cancellation);
            }
            await Call("/api/pika/enable", new JsonObject { ["owner"] = owner, ["clear"] = true }, cancellation);
        }
        message = !includePika ? "Six arm motors enabled · Pika power unchanged" : PikaMatchesProfile() ? "Controls enabled · robot and assigned Pika use the same controller session" : "Robot controls enabled · no Pika assigned to this robot";
        linkCommandsEnabled = link is not null;
    }

    private Task Save(CancellationToken cancellation)
    {
        preferences = RobotProfileRouting.Capture(preferences);
        return File.WriteAllTextAsync(Path.Combine(engine.Start.SettingsDirectory, "preferences.json"), JsonSerializer.Serialize(preferences, PluginProtocol.Json), cancellation);
    }

    private Task Render(CancellationToken cancellation)
    {
        var robots = scene?["robots"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var widgets = new List<Widget>
        {
            Widget.Row("header", Widget.Button("enable", "Enable robot"), Widget.Button("disable", "Disable"), Widget.Button("stop", "STOP"), Widget.Button("backend.toggle", "Controller setup")),
            Widget.Label("status", message) with { Height = 42 },
            BackendSetup(),
            Widget.Row("safety", Widget.Label("safety.text", "Make sure the workspace is clear and safe to move."), Widget.Button("safety.accept", "Enable controls"), Widget.Button("safety.cancel", "Cancel")) with { Visible = false },
            Widget.Row("scene.binding", Widget.Button("robots.refresh", "Scene robots"), new Widget("dropdown", "robots.select") { Width = 200, Options = robots.Select(r => r["name"]!.GetValue<string>()).ToArray(), Value = Math.Max(0, Array.FindIndex(robots, r => r["entityId"]!.GetValue<string>() == preferences.RobotId)) }, Widget.Button("link", "Link robot")),
            Widget.Label("link.status", "Unlinked · joint/desk/camera calibration not verified") with { Height = 34 },
            TeachingControls(),
            AlignmentControls(),
        };
        var robotControls = new List<Widget>
        {
            Widget.Row("motion", Widget.Label("motion.progress", "Idle") with { Width = 560, Height = 34 },
                Widget.Button("motion.hold", "Stop movement") with { Width = 130, Enabled = false }),
            Widget.Row("teleoperate", new[] { Widget.Button("teleoperate.toggle", "Start Teleoperate") with { Width = 170 } }.Concat(
                Enumerable.Range(1, 6).Select(i => Widget.Button("teleoperate.joint." + i, "J" + i) with { Width = 42, Enabled = false })).ToArray()),
            Widget.Row("cameras", Camera("depth", "Pika D405"), Camera("gripper", "Pika gripper camera")),
            Widget.Row("pika", Widget.Label("pika.value", "Pika · waiting for feedback") with { Width = 190 }, Widget.Slider("pika.target", 0, 0, 90, .1) with { Enabled = false }),
            Widget.Label("pika.monitor", pikaMonitorStatus) with { Height = 28 },
            Widget.Row("preferences", Widget.Label("speed.label", "Speed %"), Widget.Slider("speed", preferences.Speed, 0, 100, 1), Widget.Label("step.label", "Jog °"), Widget.Slider("step", preferences.Step, .1, 5, .1), Widget.Label("tolerance.label", "Tolerance °"), Widget.Slider("tolerance", preferences.Tolerance, .05, 1, .05)),
            Widget.Label("speed.actual", "Waiting for reported speed limits") with { Height = 34 },
        };
        for (int i = 1; i <= 6; i++)
            robotControls.Add(Widget.Row("joint." + i, Widget.Label($"joint.{i}.value", $"J{i} · —") with { Width = 190, Height = 28 }, Widget.Button($"joint.{i}.minus", "−") with { Width = 32 }, Widget.Slider($"joint.{i}.target", 0, -180, 180, .1) with { Enabled = false }, Widget.Button($"joint.{i}.plus", "+") with { Width = 32 }, Widget.Button($"joint.{i}.zero", "Zero") with { Width = 52 }));
        robotControls.Add(Widget.Button("zero.all", "All joints to zero"));
        robotControls.Add(Widget.Label("incident", ""));
        robotControls.Add(Widget.Label("recovery.next", ""));
        robotControls.Add(Widget.Row("recovery", Widget.Button("recovery.run", "Clear & support arm — recover")) with { Visible = false });
        // A focused check remains usable without relying on nested native-panel
        // scrolling. Visibility changes only; acquisition and measured linking run on.
        widgets.Add(Widget.Column("robot.controls", robotControls.ToArray()) with { Visible = !alignmentOpen });
        return engine.PublishViewAsync(Panel, Widget.Row("workspace", ScopeRobotWidget(RobotSidebar()), ScopeRobotWidget(Widget.Column("controls", widgets.ToArray()))), cancellation);
    }

    private CameraPreferences CameraSettings(string id) => id switch
    {
        "bench" => preferences.Bench ?? new(OperatingSystem.IsWindows() ? "Lumina Camera - Raw" : "", OperatingSystem.IsWindows(), "1920x1080", 30, "mjpeg"),
        "gripper" => preferences.Gripper ?? new(OperatingSystem.IsWindows() ? "DECXIN  CAMERA" : "", OperatingSystem.IsWindows()),
        "depth" => preferences.Depth ?? new(OperatingSystem.IsWindows() ? "Intel(R) RealSense(TM) Depth Camera 405  Depth" : "", OperatingSystem.IsWindows()),
        _ => throw new ArgumentException("Unknown camera")
    };

    private async Task RestartCamera(string id)
    {
        if (cameras.Remove(id, out var previous)) await previous.DisposeAsync();
        var camera = new CameraCapture(engine, id, CameraSettings(id), (key, value) => cameraStatus[key] = value, preferences.DepthSerial,
            (key, width, height) => cameraDimensions[key] = (width, height),
            (key, profile) => { Materialize(profile); cameraCalibration[key] = profile; }, CameraImageId(id));
        cameras[id] = camera; camera.Start();
    }

    private Widget Camera(string id, string title) => Widget.Column("camera." + id,
        Widget.Row("camera." + id + ".header", Widget.Label("camera." + id + ".title", title), Widget.Button("camera." + id + ".settings", "⚙") with { Hint = "Choose camera, start/stop and capture settings" }),
        Widget.Label("camera." + id + ".real.title", id == "depth" ? "REAL · depth 7–50 cm · white = near" : "REAL"),
        new Widget("image", "camera." + id + ".image") { ImageId = CameraImageId(id), Visible = CameraFrameReady(id), Height = 150 },
        Widget.Label("camera." + id + ".sim.title", id == "depth" ? "SIM · ideal metric depth · same 7–50 cm scale" : id == "gripper" ? "SIM · fisheye lens" : "SIM · camera viewpoint"),
        new Widget("scene-camera", "camera." + id + ".sim") { Height = 150 },
        Widget.Label("camera." + id + ".sim.status", "Camera alignment not calibrated") with { Height = 34 },
        Widget.Label("camera." + id + ".info", "Connecting…") with { Height = 34 },
        Widget.Column("camera." + id + ".settings.form", new Widget("input", "camera." + id + ".device") { Text = CameraSettings(id).Device, Hint = "Camera name or stable device path" },
            Widget.Button("camera." + id + ".simrefresh", "Find simulation cameras"),
            new Widget("dropdown", "camera." + id + ".simselect") { Options = SimulationCameraNames(), Value = SimulationCameraIndex(id) },
            Widget.Button("camera." + id + ".simoverlay", "Real / simulation overlay") with { Enabled = id != "depth", Hint = "Blend views for alignment. Does not establish calibrated or time-synchronized data." },
            Widget.Column("camera." + id + ".calibration",
                Widget.Button("camera." + id + ".calibrationtools", "Check camera runtime") with { Hint = "Checks the bundled C# camera tools without opening a camera. No Python, SDK installation or runtime download is needed." },
                Widget.Label("camera." + id + ".calibration.help", "Lens calibration: print the 8 × 6 checkerboard at actual size (20 mm squares), keep it flat. Capture 20+ sharp views with different tilts and positions, including all image edges. Keep the robot stationary; move the board."),
                Widget.Button("camera." + id + ".calibrationboard", "Open printable board"),
                Widget.Button("camera." + id + ".calibrationnew", "New capture session"),
                Widget.Button("camera." + id + ".calibrationcapture", "Capture board image"),
                Widget.Button("camera." + id + ".calibrationsolve", "Solve & validate fisheye lens"),
                Widget.Label("camera." + id + ".calibration.status", "No lens calibration. Camera mount remains unverified.")) with { Visible = id == "gripper" },
            new Widget("input", "camera." + id + ".serial") { Text = preferences.DepthSerial, Hint = "D405 serial; blank only when exactly one is connected", Visible = id == "depth" },
            Widget.Button("camera." + id + ".toggle", "Start / Stop"),
            Widget.Label("camera." + id + ".clock", "Preview uses host receipt timestamps. Hardware exposure synchronization and metric depth recording are not calibrated.")) with { Visible = cameraSettingsOpen.Contains(id) });

    private async Task Update(CancellationToken cancellation)
    {
        var patches = new Dictionary<string, JsonObject>
        {
            ["status"] = new() { ["text"] = ownedBackend is null ? message : message + " · controller closes with Pipers" }, ["link"] = new() { ["text"] = link is null ? "Link robot" : "Unlink robot" },
            ["link.status"] = new() { ["text"] = link is null ? "Unlinked · use Edit mode · calibration not verified" : $"{(linkCommandsEnabled ? "Linked" : "Tracking only — motion blocked")} · joint error {linkErrorDegrees?.ToString("F4") ?? "—"}° · {link.GripperStatus} · calibration unverified" }
        };
        patches["backend.setup"] = new() { ["visible"] = backendSetupOpen || state is null };
        patches["backend.status"] = new() { ["text"] = backendStatus };
        patches["backend.prerequisites"] = new() { ["text"] = setupStatus };
        AddProfilePatches(patches);
        JsonObject? device = null;
        try { device = Device(); } catch (InvalidOperationException) { }
        bool movable = approved && !TeachingState.Inspect(device).BlocksTargets && state?["motion"]?["active"]?.GetValue<bool>() != true && device?["fresh"]?.GetValue<bool>() == true && device["recovery_required"]?.GetValue<bool>() != true && string.IsNullOrEmpty(device["blocker"]?.GetValue<string>());
        var joints = device?["joints"]?.AsArray();
        bool ownMove = state?["motion"]?["active"]?.GetValue<bool>() == true && state["motion"]?["owner"]?.GetValue<string>() == owner;
        var remaining = joints?.Select((j, i) => new { Joint = i + 1, Actual = j?["actual_deg"]?.GetValue<double>(), Target = j?["target_deg"]?.GetValue<double>() })
            .Where(j => j.Actual.HasValue && j.Target.HasValue && Math.Abs(j.Target.Value - j.Actual.Value) > preferences.Tolerance)
            .Select(j => $"J{j.Joint} {j.Actual:F1}° → {j.Target:F1}°").ToArray() ?? [];
        patches["motion.progress"] = new() { ["text"] = ownMove
            ? (state?["motion"]?["following"]?.GetValue<bool>() == true ? "Following drag · " : "Movement in progress · ") + (remaining.Length > 0 ? string.Join(" · ", remaining) : "settling at target")
            : device?["recovery_required"]?.GetValue<bool>() == true ? "Stopped · recovery required" : "Idle" };
        patches["motion.hold"] = new() { ["enabled"] = ownMove && joints?.Any(j => j?["target_deg"] is not null) == true };
        bool canTeleoperate = movable && link is not null && linkCommandsEnabled && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true) == true;
        patches["teleoperate.toggle"] = new() { ["text"] = teleoperating ? "Pause Teleoperate" : "Start Teleoperate", ["enabled"] = teleoperating || canTeleoperate };
        for (int i = 1; i <= 6; i++) patches["teleoperate.joint." + i] = new() {
            ["text"] = teleoperating && teleoperationJoint == "joint" + i ? "[J" + i + "]" : "J" + i,
            ["enabled"] = teleoperating && canTeleoperate };
        for (int i = 1; i <= 6; i++)
        {
            var joint = joints?.ElementAtOrDefault(i - 1);
            bool available = movable && joint?["enabled"]?.GetValue<bool>() == true && joint["limits_fresh"]?.GetValue<bool>() == true;
            double? actual = joint?["actual_deg"]?.GetValue<double>();
            patches[$"joint.{i}.value"] = new() { ["text"] = $"J{i} · {actual?.ToString("F3") ?? "—"}° · {joint?["current_a"]?.GetValue<double>().ToString("F2") ?? "—"} A" };
            patches[$"joint.{i}.target"] = new() { ["value"] = actual ?? 0, ["min"] = joint?["min_deg"]?.DeepClone() ?? JsonValue.Create(-180), ["max"] = joint?["max_deg"]?.DeepClone() ?? JsonValue.Create(180), ["enabled"] = available };
            foreach (string action in new[] { "minus", "plus", "zero" }) patches[$"joint.{i}.{action}"] = new() { ["enabled"] = available };
        }
        patches["zero.all"] = new() { ["enabled"] = movable && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true && j["limits_fresh"]?.GetValue<bool>() == true) == true };
        var pika = PikaMatchesProfile() ? state?["pika"] : null;
        double? pikaAngle = pika?["telemetry"]?["motor"]?["Position"]?.GetValue<double>();
        double? opening = pikaAngle.HasValue ? PikaProtocol.Width(pikaAngle.Value) : null;
        patches["pika.value"] = new() { ["text"] = string.IsNullOrEmpty(ActiveProfile.PikaDevice) ? "Pika · unassigned" : $"Pika · {opening?.ToString("F2") ?? "—"} mm" };
        patches["pika.target"] = new() { ["value"] = opening ?? 0, ["enabled"] = device is not null && !switchingProfile && pikaOwned && pika?["armed"]?.GetValue<bool>() == true && pika?["fresh"]?.GetValue<bool>() == true && pika["enabled"]?.GetValue<bool>() == true && pika["pending"]?.GetValue<bool>() != true };
        patches["pika.monitor"] = new() { ["text"] = pikaMonitorStatus };
        bool limitsFresh = joints?.All(j => j?["limits_fresh"]?.GetValue<bool>() == true && j["max_speed_rad_s"]?.GetValue<double>() > 0) == true;
        double speed = limitsFresh ? joints!.Min(j => j!["max_speed_rad_s"]!.GetValue<double>()) * 180 / Math.PI : 0;
        patches["speed.actual"] = new() { ["text"] = limitsFresh ? $"{preferences.Speed:F0}% · {speed * preferences.Speed / 100:F2}°/s target ceiling · measured per-joint limit · jog {preferences.Step:F1}° · tolerance {preferences.Tolerance:F1}°" : "Waiting for fresh robot speed limits" };
        bool needsRecovery = device?["recovery_required"]?.GetValue<bool>() == true || device?["reset_pending"]?.GetValue<bool>() == true;
        var recovery = needsRecovery ? RecoveryPlan.Inspect(device!, state?["motion"]?["active"]?.GetValue<bool>() == true) : null;
        patches["incident"] = new() { ["text"] = recovery?.Diagnosis ?? "" };
        patches["recovery.next"] = new() { ["text"] = recovery?.NextStep ?? "" };
        patches["recovery"] = new() { ["visible"] = needsRecovery };
        patches["recovery.run"] = new() { ["enabled"] = recovery?.Action is not null && !recoveryInProgress,
            ["text"] = recoveryInProgress ? "Verifying recovery…" : recovery?.Action == "reset" ? "Clear & support arm — recover" : "Verify current state (no movement)" };
        foreach (string id in new[] { "bench", "gripper", "depth" })
        {
            patches["camera." + id + ".image"] = new() { ["visible"] = CameraFrameReady(id) };
            patches["camera." + id + ".info"] = new() { ["text"] = cameraStatus.GetValueOrDefault(id, "Waiting for a fresh frame…") };
        }
        patches["camera.gripper.calibration.status"] = new() { ["text"] = calibrationStatus };
        AddTeachingPatches(patches, device);
        AddAlignmentPatches(patches);
        AddCameraComparisonPatches(patches);
        AddResetAssistedPatches(patches, device);
        await Patch(patches, cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        await StopResetAssistedOnDispose();
        using (var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { await FinishAlignment("Plugin closed", saveTimeout.Token); }
            catch (Exception error) { Trace.TraceWarning("Alignment report shutdown: {0}", error.Message); }
        }
        using (var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { await HoldFollowing(releaseTimeout.Token); }
            catch (Exception error) { Trace.TraceWarning("Following shutdown: {0}", error.Message); }
        }
        // Keep telemetry/heartbeats running until this session's Pika disable is
        // verified. Closing an external backend must not strand its old owner.
        using (var pikaReleaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            try { await ReleaseOwnPika(pikaReleaseTimeout.Token); }
            catch (Exception error) { Trace.TraceWarning("Pika shutdown release: {0}", error.Message); }
        }
        await shutdown.CancelAsync();
        if (calibrationWorker is not null) { try { await calibrationWorker; } catch (OperationCanceledException) { } }
        foreach (var camera in cameras.Values) await camera.DisposeAsync();
        if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled) { } }
        await StopOwnedBackend();
        // An externally managed server keeps running; its existing lease expiry handles a lost client.
        channel.Dispose(); commands.Dispose(); shutdown.Dispose();
    }
}
