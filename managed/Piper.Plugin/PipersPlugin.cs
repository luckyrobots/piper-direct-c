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
    private string token = "", message = "Connecting to the existing Piper server…";
    private long lease, lastCommand;
    private volatile bool approved, pikaOwned;
    private volatile RobotLink? link;
    private volatile bool linkCommandsEnabled, recoveryInProgress;
    private double? linkErrorDegrees;
    private JsonObject? scene;
    private Task? worker;
    private readonly Dictionary<string, CameraCapture> cameras = [];
    private readonly ConcurrentDictionary<string, string> cameraStatus = new();
    private readonly ConcurrentDictionary<string, (int Width, int Height)> cameraDimensions = new();

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
        var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for the controller server.");
        if (!receipt.IsFresh(Stopwatch.GetTimestamp()))
            throw new InvalidOperationException("Robot feedback is stale.");
        if (receipt.BackendToken != token) throw new InvalidOperationException("The controller session changed; wait for fresh feedback.");
        if (switchingProfile) throw new InvalidOperationException("Changing robot assignment; wait for measured feedback.");
        var device = RobotProfileRouting.Device(preferences, receipt.State);
        if (!receipt.HasFreshDevice(device, Stopwatch.GetTimestamp())) throw new InvalidOperationException("Robot joint feedback is stale.");
        if (expectedDeviceId is not null && device["id"]?.GetValue<string>() != expectedDeviceId ||
            expectedGeneration.HasValue && device["generation"]?.GetValue<long>() != expectedGeneration.Value ||
            expectedToken is not null && token != expectedToken)
            throw new InvalidOperationException("The arm connection or backend session changed; the previous confirmation no longer authorizes commands.");
        return device;
    }

    private Task Watch(CancellationToken cancellation) => Task.WhenAll(
        ReadTelemetry(cancellation), ProcessTelemetry(cancellation),
        ArmHeartbeats(cancellation), PikaHeartbeats(cancellation));

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
        long permissionEpoch = Interlocked.Read(ref targetPermissionEpoch);
        expectedToken ??= token;
        bool recoveryOnly = recoveryAcknowledged && action is "reset" or "verify-reset" or "verify-current";
        bool releasingPower = action == "disable";
        bool teachingOnly = teachingAcknowledged && action == "teach-return";
        if (calibrationOpen && !recoveryOnly && !teachingOnly && !releasingPower && action != "hold") throw new InvalidOperationException("Calibrate robot is read-only. Use Back to robot controls before sending arm targets or enable commands.");
        if (workspacePage != "controls" && !recoveryOnly && !teachingOnly && !releasingPower && action != "hold") throw new InvalidOperationException("Return to Controls before enabling or moving the real robot.");
        if (!approved && !recoveryOnly && !teachingOnly && !releasingPower) throw new InvalidOperationException("Use Enable robot and confirm the workspace is clear first.");
        var device = Device(expectedDeviceId, expectedGeneration, expectedToken);
        if (!teachingOnly && !recoveryOnly && TeachingState.Inspect(device).BlocksTargets)
            throw new InvalidOperationException("The physical teaching controller owns the arm. End teaching and return to CAN before sending new targets.");
        var body = fields ?? new();
        body["action"] = action;
        body["device_id"] = expectedDeviceId is not null ? JsonValue.Create(expectedDeviceId) : device["id"]!.DeepClone();
        body["generation"] = expectedGeneration.HasValue ? JsonValue.Create(expectedGeneration.Value) : device["generation"]!.DeepClone();
        body["owner"] = owner; body["standalone"] = true; body["clear"] = true; body["supported"] = true; body["reset_completed"] = true;
        body["speed_percent"] = preferences.Speed; body["tolerance_deg"] = preferences.Tolerance;
        if (permissionEpoch != Interlocked.Read(ref targetPermissionEpoch)) throw new InvalidOperationException("Control permission changed while preparing the command; start a new action.");
        var result = await Call("/api/command", body, cancellation, expectedToken);
        if (permissionEpoch != Interlocked.Read(ref targetPermissionEpoch) || token != expectedToken)
            throw new InvalidOperationException("Control permission changed before acknowledgement; the old action will not be renewed or resumed.");
        lease = result["request_id"]?.GetValue<long>() ?? lease;
        lastCommand = result["request_id"]?.GetValue<long>() ?? 0;
        message = result["message"]?.GetValue<string>() ?? "Command accepted.";
    }

    public async Task HandleEventAsync(UiEvent input, CancellationToken cancellation)
    {
        if (UnscopeEvent(input.WidgetId).Length > 0) await CancelPikaMonitorAttempt();
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
            if (workspacePage != "controls" && WorkspaceCommandPolicy.RequiresControls(id)) throw new InvalidOperationException("Return to Controls before enabling or moving the robot.");
            if (CalibrationCommandGuard.Blocks(id, calibrationOpen)) throw new InvalidOperationException("Calibrate robot is read-only. Use Back to robot controls before enabling or moving the real robot.");
            if (id.StartsWith("workspace.", StringComparison.Ordinal))
            {
                await WorkspaceEvent(id[10..], cancellation); await Update(cancellation); return;
            }
            if (id.StartsWith("wizard.", StringComparison.Ordinal))
            {
                if (!calibrationOpen) throw new InvalidOperationException("Open Calibration before continuing its guided measurements.");
                await WizardEvent(id[7..], input.Value, cancellation); await Update(cancellation); return;
            }
            if (id.StartsWith("profiles.", StringComparison.Ordinal))
            {
                if (calibrationOpen && (id.StartsWith("profiles.select.", StringComparison.Ordinal) || id is "profiles.add" or "profiles.remove")) await ClearWizardHighlight(cancellation);
                await ProfileEvent(id[9..], input.Value, cancellation);
                if (id.StartsWith("profiles.select.", StringComparison.Ordinal) || id is "profiles.add" or "profiles.remove") { workspacePage = "controls"; calibrationOpen = alignmentOpen = false; }
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
            else if (id.StartsWith("calibration.", StringComparison.Ordinal))
                await CalibrationEvent(id[12..], input.Value, cancellation);
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
                if (parts[2] == "settings") await WorkspaceEvent("settings.cameras." + camera, cancellation);
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
                    if (camera == "depth" && parts[2] is "view" or "selectcolor" or "selectdepth") settings = D405View.Select(settings, parts[2] == "view" ? input.Value.GetInt32() : parts[2] == "selectdepth" ? 1 : 0);
                    if (parts[2] == "serial") preferences = preferences with { DepthSerial = input.Value.GetString() ?? "" };
                    preferences = camera switch { "bench" => preferences with { Bench = settings }, "gripper" => preferences with { Gripper = settings }, _ => preferences with { Depth = settings } };
                    await Save(cancellation); await RestartCamera(camera);
                    await Render(cancellation);
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
                    string expectedLinkToken = token;
                    scene = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
                    if (scene["robots"] is JsonArray { Count: 1 } available && string.IsNullOrEmpty(preferences.RobotId))
                        preferences = preferences with { RobotId = available[0]!["entityId"]!.GetValue<string>() };
                    var robot = scene["robots"]!.AsArray().OfType<JsonObject>().SingleOrDefault(r => r["entityId"]?.GetValue<string>() == preferences.RobotId)
                        ?? throw new InvalidOperationException("Select the scene robot before linking.");
                    var next = new RobotLink(scene["sceneId"]!.GetValue<string>(), preferences.RobotId, robot["joints"]!.AsArray(), Device(expectedToken: expectedLinkToken));
                    var requested = next.Feedback(Device(expectedToken: expectedLinkToken), state?["pika"] as JsonObject, PikaMatchesProfile());
                    var applied = JsonNode.Parse((await engine.RequestAsync("robot.feedback", requested, cancellation)).GetRawText())!.AsObject();
                    if (applied["applied"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException("Finish the active viewport joint edit, then link again.");
                    linkErrorDegrees = next.FeedbackError(requested, applied);
                    await engine.RequestAsync("robot.subscribe", new JsonObject { ["sceneId"] = next.SceneId, ["entityId"] = next.EntityId }, cancellation);
                    await Save(cancellation);
                      var currentDevice = Device(next.DeviceId, next.Generation, expectedLinkToken);
                      linkBackendToken = expectedLinkToken;
                      link = next; linkCommandsEnabled = currentDevice["recovery_required"]?.GetValue<bool>() != true;
                      sceneTrackingStatus = "Measured scene link established";
                      RetryLinkedPikaMonitor();
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
                double value = FiniteControlNumber(input.Value, id);
                preferences = id switch { "speed" => preferences with { Speed = Math.Clamp(value, 0, 100) }, "step" => preferences with { Step = Math.Clamp(value, .1, 5) }, _ => preferences with { Tolerance = Math.Clamp(value, .05, 1) } };
                await Save(cancellation);
            }
            else if (id == "zero.all") await Command("move-to-zero", null, cancellation);
            else if (id.StartsWith("joint.", StringComparison.Ordinal))
            {
                string[] parts = id.Split('.'); int joint = int.Parse(parts[1]);
                string action = parts[2];
                await Command(action == "target" ? "joint" : action == "zero" ? "zero" : "jog",
                    new() { ["joint"] = joint, ["degrees"] = action == "target" ? input.Value.GetDouble() : action == "plus" ? preferences.Step : action == "minus" ? -preferences.Step : 0 }, cancellation);
            }
            else if (id is "pika.target" or "pika.plus" or "pika.minus" or "pika.zero")
            {
                Device();
                if (!PikaMatchesProfile()) throw new InvalidOperationException("The Pika serial assignment does not match this robot.");
                if (!pikaOwned || state?["pika"]?["armed"]?.GetValue<bool>() != true)
                    throw new InvalidOperationException("Pika is monitoring only. Use Enable robot before commanding its opening.");
                double currentOpening = PikaProtocol.Width(state!["pika"]!["telemetry"]!["motor"]!["Position"]!.GetValue<double>());
                double opening = id == "pika.target" ? FiniteControlNumber(input.Value, "Gripper opening") : id == "pika.zero" ? 0 : Math.Clamp(currentOpening + (id == "pika.plus" ? 1 : -1), 0, 90);
                await Call("/api/pika/position", new JsonObject { ["owner"] = owner, ["value"] = PikaProtocol.Angle(opening) }, cancellation);
            }
            await Update(cancellation);
        }
        catch (Exception error) when (error is not OperationCanceledException) { message = error.Message; await Update(cancellation); }
        finally { commands.Release(); }
    }

    private async Task Enable(CancellationToken cancellation, bool includePika = true, string? expectedDeviceId = null, long? expectedGeneration = null, string? expectedToken = null)
    {
        if (calibrationOpen) throw new InvalidOperationException("Calibrate robot is read-only. Use Back to robot controls before enabling motors.");
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
            pikaMonitorStatus = "Pika control session active.";
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
        if (id == "depth")
        {
            // Color and depth can share a serial/resolution but not intrinsics.
            // Keep cached images hidden until this new worker publishes its frame.
            cameraCalibration.TryRemove(id, out _); cameraDimensions.TryRemove(id, out _);
            cameraStatus[id] = "Connecting D405 " + D405View.Mode(CameraSettings(id)) + " view…";
        }
        var camera = new CameraCapture(engine, id, CameraSettings(id), (key, value) => cameraStatus[key] = value, preferences.DepthSerial,
            (key, width, height) => cameraDimensions[key] = (width, height),
            (key, profile) => { Materialize(profile); cameraCalibration[key] = profile; }, CameraImageId(id));
        cameras[id] = camera; camera.Start();
    }

    private async Task Update(CancellationToken cancellation)
    {
        var patches = new Dictionary<string, JsonObject>
        {
            ["status"] = new() { ["text"] = ownedBackend is null ? message : message + " · controller closes with Pipers" }, ["link"] = new() { ["text"] = link is null ? "Link robot" : "Unlink robot" },
            ["link.status"] = new() { ["text"] = link is null ? "Unlinked · use Edit mode · calibration not verified" : $"{(linkCommandsEnabled ? "Linked" : "Tracking only — motion blocked")} · joint error {linkErrorDegrees?.ToString("F4") ?? "—"}° · {link.GripperStatus} · {sceneTrackingStatus} · calibration unverified" }
        };
        patches["backend.setup"] = new() { ["visible"] = backendSetupOpen || state is null };
        patches["backend.status"] = new() { ["text"] = backendStatus };
        patches["backend.prerequisites"] = new() { ["text"] = setupStatus };
        AddProfilePatches(patches);
        JsonObject? device = null;
        try { device = Device(); } catch (InvalidOperationException) { }
        bool movable = workspacePage == "controls" && approved && !TeachingState.Inspect(device).BlocksTargets && state?["motion"]?["active"]?.GetValue<bool>() != true && device?["fresh"]?.GetValue<bool>() == true && device["recovery_required"]?.GetValue<bool>() != true && string.IsNullOrEmpty(device["blocker"]?.GetValue<string>());
        var joints = device?["joints"]?.AsArray();
        bool ownMove = state?["motion"]?["active"]?.GetValue<bool>() == true && state["motion"]?["owner"]?.GetValue<string>() == owner;
        var remaining = joints?.Select((j, i) => new { Joint = i + 1, Actual = j?["actual_deg"]?.GetValue<double>(), Target = j?["target_deg"]?.GetValue<double>() })
            .Where(j => j.Actual.HasValue && j.Target.HasValue && Math.Abs(j.Target.Value - j.Actual.Value) > preferences.Tolerance)
            .Select(j => $"J{j.Joint} {j.Actual:F1}° → {j.Target:F1}°").ToArray() ?? [];
        patches["motion.progress"] = new() { ["text"] = ownMove
            ? (state?["motion"]?["following"]?.GetValue<bool>() == true ? "Following drag · " : "Movement in progress · ") + (remaining.Length > 0 ? string.Join(" · ", remaining) : "settling at target")
            : device?["recovery_required"]?.GetValue<bool>() == true ? "Stopped · recovery required" : device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? "Range overrun · move the affected joint inward or Move all to zero" : "Idle" };
        patches["motion.hold"] = new() { ["enabled"] = ownMove && joints?.Any(j => j?["target_deg"] is not null) == true };
        bool canTeleoperate = movable && link is not null && linkCommandsEnabled && device?["range_recovery"]?["required"]?.GetValue<bool>() != true && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true) == true;
        patches["teleoperate.toggle"] = new() { ["text"] = teleoperating ? "Pause Teleoperate" : "Start Teleoperate", ["enabled"] = teleoperating || canTeleoperate };
        for (int i = 1; i <= 6; i++) patches["teleoperate.joint." + i] = new() {
            ["text"] = teleoperating && teleoperationJoint == "joint" + i ? "[J" + i + "]" : "J" + i,
            ["enabled"] = teleoperating && canTeleoperate };
        for (int i = 1; i <= 6; i++)
        {
            var joint = joints?.ElementAtOrDefault(i - 1);
            bool available = movable && joint?["enabled"]?.GetValue<bool>() == true && joint["limits_fresh"]?.GetValue<bool>() == true && joint["can_move"]?.GetValue<bool>() != false;
            double? actual = joint?["actual_deg"]?.GetValue<double>();
            patches[$"joint.{i}.value"] = new() { ["text"] = actual?.ToString("F3") ?? "—" };
            patches[$"joint.{i}.current"] = new() { ["text"] = joint?["current_a"]?.GetValue<double>().ToString("F2") ?? "—" };
            string rangeHint = joint?["motion_blocker"]?.GetValue<string>() ?? $"Reported range {joint?["min_deg"]}° to {joint?["max_deg"]}°";
            patches[$"joint.{i}.target"] = new() { ["value"] = actual ?? 0, ["min"] = joint?["min_deg"]?.DeepClone() ?? JsonValue.Create(-180), ["max"] = joint?["max_deg"]?.DeepClone() ?? JsonValue.Create(180), ["enabled"] = available, ["hint"] = rangeHint };
            patches[$"joint.{i}.minus"] = new() { ["enabled"] = available && joint?["can_move_negative"]?.GetValue<bool>() != false, ["hint"] = rangeHint };
            patches[$"joint.{i}.plus"] = new() { ["enabled"] = available && joint?["can_move_positive"]?.GetValue<bool>() != false, ["hint"] = rangeHint };
            patches[$"joint.{i}.zero"] = new() { ["enabled"] = available, ["hint"] = rangeHint };
        }
        patches["zero.all"] = new() { ["enabled"] = movable && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true && j["limits_fresh"]?.GetValue<bool>() == true) == true && (device?["range_recovery"]?["required"]?.GetValue<bool>() != true || device["range_recovery"]?["can_move_to_zero"]?.GetValue<bool>() == true), ["hint"] = device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? device["range_recovery"]!["reason"]?.GetValue<string>() ?? "Slow range entry, then synchronized zero position. Encoder zeros are unchanged." : "Move all joints to zero angles. This does not calibrate encoder zeros." };
        var pika = PikaMatchesProfile() ? state?["pika"] : null;
        double? pikaAngle = pika?["telemetry"]?["motor"]?["Position"]?.GetValue<double>();
        double? opening = pikaAngle.HasValue ? PikaProtocol.Width(pikaAngle.Value) : null;
        patches["pika.value"] = new() { ["text"] = string.IsNullOrEmpty(ActiveProfile.PikaDevice) ? "Unassigned" : $"{opening?.ToString("F2") ?? "—"} mm", ["hint"] = pikaMonitorStatus };
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
        AddCalibrationPatches(patches, device);
        AddCameraComparisonPatches(patches);
        AddResetAssistedPatches(patches, device);
        foreach (string action in new[] { "minus", "plus", "zero" }) patches["pika." + action] = new() { ["enabled"] = workspacePage == "controls" && pikaOwned && pika?["fresh"]?.GetValue<bool>() == true && pika["armed"]?.GetValue<bool>() == true && pika["pending"]?.GetValue<bool>() != true };
        AddWorkspacePatches(patches, device);
        await Patch(patches, cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        using (var markerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { await ClearWizardHighlight(markerTimeout.Token); }
            catch (Exception error) { Trace.TraceWarning("Calibration marker shutdown: {0}", error.Message); }
        }
        await StopPikaMonitorRetries();
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
        if (wizardHighlightMaintenance is not null) { try { await wizardHighlightMaintenance; } catch (OperationCanceledException) { } }
        if (calibrationWorker is not null) { try { await calibrationWorker; } catch (OperationCanceledException) { } }
        foreach (var camera in cameras.Values) await camera.DisposeAsync();
        if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled) { } }
        await StopOwnedBackend();
        // An externally managed server keeps running; its existing lease expiry handles a lost client.
        channel.Dispose(); commands.Dispose(); shutdown.Dispose();
    }
}
