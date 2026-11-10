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
    RobotProfile[]? Robots = null, string ActiveRobotId = "", string SimulationTransport = SimulationTrajectoryTransport.PositionVelocity, bool ShowMeasuredGhosts = true,
    BenchReference? BenchReference = null);

/// <summary>Why a /api/command is sent. Control needs this session's approval and the
/// Controls page. Recovery covers the operator-acknowledged reset, verify and
/// recover-at-pose steps; TeachingReturn covers teach-return. Each exemption applies
/// only to those actions; any other action under them is checked as Control.</summary>
internal enum CommandPurpose { Control, Recovery, TeachingReturn }

public sealed partial class PipersPlugin : ILuckyPlugin
{
    private const string Panel = "luckyrobots.pipers.controls";
    internal readonly string owner = Guid.NewGuid().ToString();
    internal readonly CancellationTokenSource shutdown = new();
    internal readonly GrpcChannel channel;
    internal readonly SemaphoreSlim commands = new(1);
    internal PluginContext engine = null!;
    internal PiperControl.PiperControlClient client = null!;
    internal Preferences preferences = new();
    // Published by the Telemetry component; this shell only reads them.
    private JsonObject? state => telemetry.State;
    private string token => telemetry.BackendToken;
    internal string message = "Connecting to the existing Piper server…";
    internal string uiActionError = "";
    internal RobotCommandAuthority CommandAuthority { get; } = new();
    internal volatile bool approved;
    internal volatile RobotLink? link;
    internal volatile bool linkCommandsEnabled;
    private double? linkErrorDegrees;
    internal JsonObject? scene;
    private Task? worker;
    internal UiEventDispatch? uiEvents;
    private readonly CameraPreviewSlots<CameraCapture> cameras = new();
    private readonly ConcurrentDictionary<string, string> cameraStatus = new();
    private readonly ConcurrentDictionary<string, (int Width, int Height)> cameraDimensions = new();

    public PipersPlugin(string address = ControllerEndpoint.GrpcAddress) : this(address, TimeProvider.System) { }

    internal PipersPlugin(string address, TimeProvider followClock)
    {
        // Follow compares coordinator timestamps with Stopwatch-stamped receipts.
        // Injected fixtures must supply both in the same monotonic domain.
        if (followClock.TimestampFrequency != Stopwatch.Frequency)
            throw new ArgumentException("The Follow clock must use the telemetry timestamp frequency.", nameof(followClock));
        follow = new(followClock);
        recovery = new(new PikaTeachingSession(() => engine.Start.SettingsDirectory));
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
        // The dispatcher exists before the first view is published, so a click
        // during startup is queued instead of running a full handler inline on
        // the SDK reader thread, where it would delay a following STOP.
        uiEvents = new(HandleEventCoreAsync, () => ControlInputEpoch,
            error => { uiActionError = message = error.Message; Trace.TraceWarning("UI action: {0}", error.Message); }, shutdown.Token);
        await Render(cancellation);
        passiveMeasuredTrackingStarted = true;
        worker = Watch(shutdown.Token);
        foreach (string camera in new[] { "bench", "gripper", "depth" }) await RestartCamera(camera);
    }

    private JsonObject Device(string? expectedDeviceId = null, long? expectedGeneration = null, string? expectedToken = null,
        TelemetryReceipt? sourceReceipt = null)
    {
        var receipt = sourceReceipt ?? telemetry.Latest ?? throw new InvalidOperationException("Waiting for the controller server.");
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

    /// <summary>Fresh feedback for exactly the pinned profile, backend, CAN device and generation.</summary>
    private JsonObject Device(RobotCommandScope expected, TelemetryReceipt? sourceReceipt = null)
    {
        if (ActiveProfile.Id != expected.ProfileId)
            throw new InvalidOperationException("The selected robot changed; the previous confirmation no longer authorizes commands.");
        return Device(expected.Device, expected.Generation, expected.Backend, sourceReceipt);
    }

    private async Task<JsonObject> Call(string route, JsonObject body, CancellationToken cancellation, string? expectedToken = null)
    {
        if (route.StartsWith("/api/pika/", StringComparison.Ordinal) && route != "/api/pika/devices")
        {
            body["pika_device"] ??= ActiveProfile.PikaDevice;
            if (!body.ContainsKey("pika_distribution")) body["pika_distribution"] = ActiveProfile.PikaDistribution;
        }
        string authorization = expectedToken ?? token;
        if (expectedToken is not null && token != expectedToken) throw new InvalidOperationException("The controller session changed; no command was sent under the old confirmation.");
        var reply = await client.ExecuteAsync(new ControlRequest { Route = route, Json = body.ToJsonString() },
            new Metadata { { "x-piper-token", authorization } }, deadline: DateTime.UtcNow.AddSeconds(route.EndsWith("/connect") ? 20 : 3), cancellationToken: cancellation);
        var result = JsonNode.Parse(reply.Json)!.AsObject();
        if (reply.Status >= 400)
        {
            string reason = result["error"]?.GetValue<string>() ?? "Controller rejected the request.";
            if (result["pika_follow_audit"]?.Deserialize<Pika.PikaFollowAudit>() is { } audit)
                throw new Pika.PikaFollowRejectedException(reason, audit);
            throw new InvalidOperationException(reason);
        }
        return result;
    }

    /// <summary>Removes session tokens and owner ids before text reaches the panel or a diagnostic file.</summary>
    internal static string RedactSecrets(string text, params string[] secrets)
    {
        foreach (string secret in secrets)
            if (secret.Length > 0) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    internal static void Materialize(JsonNode? node)
    {
        // JsonNode lazily builds child dictionaries. Finish that work on the
        // receive thread before publishing a snapshot to UI/control readers.
        if (node is JsonObject obj) foreach (var member in obj) Materialize(member.Value);
        else if (node is JsonArray array) foreach (var child in array) Materialize(child);
    }

    /// <param name="expected">The connection a prior confirmation authorized; null uses the current one.</param>
    internal async Task<JsonObject> Command(string action, JsonObject? fields, CancellationToken cancellation,
        CommandPurpose purpose = CommandPurpose.Control, RobotCommandScope? expected = null, long? expectedPermissionEpoch = null,
        double? speedOverridePercent = null, ResetCompletionPrompt? resetCompletion = null)
    {
        long permissionEpoch = expectedPermissionEpoch ?? CommandAuthority.Epoch;
        cancellation.ThrowIfCancellationRequested();
        // B1: only the operator's consumed answer to the reset question can attest
        // that a reset completed, for exactly the connection and epoch it named.
        if ((action == "verify-reset") != (resetCompletion is not null) ||
            resetCompletion is not null && (resetCompletion.Scope != expected || resetCompletion.Epoch != permissionEpoch))
            throw new InvalidOperationException("Controller reset verification needs the operator to confirm that the reset or power cycle completed. Nothing was sent.");
        string expectedToken = expected?.Backend ?? token;
        bool recoveryOnly = purpose == CommandPurpose.Recovery && action is "reset" or "verify-reset" or "verify-current" or "recover-at-pose";
        bool releasingPower = action == "disable";
        bool teachingOnly = purpose == CommandPurpose.TeachingReturn && action == "teach-return";
        if (Navigation.Page != WorkspacePage.Controls && !recoveryOnly && !teachingOnly && !releasingPower && action != "hold") throw new InvalidOperationException("Return to Controls before enabling or moving the real robot.");
        if (!approved && !recoveryOnly && !teachingOnly && !releasingPower) throw new InvalidOperationException("Use Enable robot and confirm the workspace is clear first.");
        var device = expected is { } pinned ? Device(pinned) : Device(expectedToken: expectedToken);
        var scope = new RobotCommandScope(ActiveProfile.Id, expectedToken, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>());
        if (GroupFollowActive && !releasingPower && action != "hold")
            throw new InvalidOperationException("Stop following in the left sidebar before changing arm control.");
        if (!teachingOnly && !recoveryOnly && !releasingPower && TeachingState.Inspect(device).BlocksTargets)
            throw new InvalidOperationException("The physical teaching controller owns the arm. End teaching and return to CAN before sending new targets.");
        var body = fields ?? new();
        body["action"] = action;
        body["device_id"] = scope.Device;
        body["generation"] = scope.Generation;
        body["owner"] = owner;
        AddOperatorConfirmations(body, action, resetConfirmed: recoveryOnly && resetCompletion is not null);
        body["speed_percent"] = speedOverridePercent ?? preferences.Speed; body["tolerance_deg"] = preferences.Tolerance;
        if (permissionEpoch != CommandAuthority.Epoch) throw new InvalidOperationException("Control permission changed while preparing the command; start a new action.");
        cancellation.ThrowIfCancellationRequested();
        var result = await Call("/api/command", body, cancellation, expectedToken);
        if (token != scope.Backend || ActiveProfile.Id != scope.ProfileId ||
            !CommandAuthority.TryAcknowledge(permissionEpoch, scope, result["request_id"]?.GetValue<long>()))
            throw new InvalidOperationException("Control permission changed before acknowledgement; the old action will not be renewed or resumed.");
        message = result["message"]?.GetValue<string>() ?? "Command accepted.";
        return result;
    }

    /// <summary>Attaches only the operator confirmations that the controller's
    /// admission of <paramref name="action"/> reads (src/controller.c). Each flag
    /// is a gate: sending it on every request would make the gate meaningless.</summary>
    internal static void AddOperatorConfirmations(JsonObject body, string action, bool resetConfirmed)
    {
        // Every /api/command admission requires the standalone acknowledgement.
        body["standalone"] = true;
        // Power release and recovery verification do not read the clear-path flag.
        if (action is not ("disable" or "verify-reset" or "verify-disabled")) body["clear"] = true;
        // Motor-power and recovery transitions require a supported arm.
        if (action is "enable" or "disable" or "reset" or "verify-reset" or "verify-disabled" or "verify-current" or "teach-return" or "recover-at-pose")
            body["supported"] = true;
        // Only the operator's explicit answer to the reset question confirms that a reset completed.
        if (resetConfirmed && action == "verify-reset") body["reset_completed"] = true;
    }

    internal long ControlInputEpoch => CommandAuthority.Epoch + follow.ControlEpoch;

    public async Task HandleEventAsync(UiEvent input, CancellationToken cancellation)
    {
        long inputEpoch = ControlInputEpoch;
        var eventProfile = ActiveProfile;
        string eventBackend = token;
        if (CaptureStopProfile(input.WidgetId, eventProfile) is { } stopProfile)
        { await StopImmediately(stopProfile, eventBackend, GroupFollowActive); return; }
        string eventId = UnscopePublishedWorkspaceEvent(input.WidgetId, eventProfile.Id);
        if (eventId.Length == 0) return;
        if (eventId == "recovery.guide.cancel")
        {
            bool running = CurrentRecovery is not null;
            CancelHandGuideRecovery();
            message = running ? "Recovery cancellation requested · keep the arm supported while the operation retires."
                : "Recovery guide closed. Check the live motor state before removing support.";
            using var presentation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Update(presentation.Token); }
            catch (Exception error) { Trace.TraceWarning("Recovery cancellation presentation: {0}", error.Message); }
            return;
        }
        if (eventId == "disable" && uiEvents is { } urgentDispatch)
        {
            follow.CancelInput("Disable requested · stopping the follower group");
            DisarmTelemetryTargets("Disable requested; previous actions discarded", clearLink: false, forgetApproval: eventProfile.Id);
            CancelHandGuideRecovery();
            uiActionError = "";
            message = "Disable requested · cancelling the previous action and releasing motor power…";
            if (!urgentDispatch.EnqueueUrgent(input, ControlInputEpoch))
                uiActionError = "Disable is already pending. STOP remains available immediately.";
            return;
        }
        if (GroupFollowActive && eventId is "simulation.stop" or "motion.hold" or "group.follow.cancel")
        {
            // The SDK delivers button events serially. Leave retirement with
            // the coordinator so a queued emergency STOP can run immediately.
            follow.CancelInput("Stopping all selected followers · holding");
            return;
        }
        if (GroupFollowActive && GroupBlocksControlEvent(eventId)) return;
        if (uiEvents is { } dispatch)
        {
            if (!dispatch.Enqueue(input, inputEpoch, ControlEventNeedsPermission(eventId)))
                message = "Controls are busy; that action was not queued.";
            return;
        }
        // Unstarted isolated fixtures use the same handler without a worker.
        await HandleEventCoreAsync(input, cancellation);
    }

    internal static bool ControlEventNeedsPermission(string id)
    {
        // Releasing power is not a resumable movement target. It must survive
        // the epoch change used to retire an active simulation follower first.
        // Selecting a profile is navigation; stale feedback on the current arm
        // must not discard that request. Hardware assignments/targets stay guarded.
        if (id == "disable" || id.StartsWith("profiles.select.", StringComparison.Ordinal)) return false;
        return GroupBlocksControlEvent(id) || WorkspaceCommandPolicy.RequiresControls(id);
    }

    internal async Task HandleEventCoreAsync(UiEvent input, CancellationToken cancellation)
    {
        string queued = UnscopePublishedWorkspaceEvent(input.WidgetId);
        if (queued.Length == 0) return;
        if (CurrentRecovery is not null && queued != "disable")
        {
            message = "Recovery is in progress. STOP cancels the remaining stages.";
            await Update(cancellation); return;
        }
        // STOP must not wait behind a WSL startup; cancelling also defers the next
        // retry so this event acquires the command gate first.
        await telemetry.Pika.CancelAsync();
        await commands.WaitAsync(cancellation);
        try
        {
            string id = UnscopePublishedWorkspaceEvent(input.WidgetId);
            if (id.Length == 0) return; // Late gesture from a previously selected robot.
            uiActionError = "";
            if (TryStrip(id, "group.follow.", out string rest)) { await GroupFollowWorkspaceEvent(rest, input.Value, cancellation); return; }
            bool simulation = TryStrip(id, "simulation.", out rest);
            if (!simulation) await AdmitControlEvent(id, cancellation);
            if (simulation) await GroupFollowEvent(rest switch {
                "toggle" or "match" => "toggle", "stop" => "cancel",
                _ => throw new InvalidOperationException("Unknown simulation follow action.") }, cancellation);
            else if (TryStrip(id, "global.", out rest)) await GlobalSettingsEvent(rest, input.Value, cancellation);
            else if (TryStrip(id, "workspace.", out rest)) await WorkspaceEvent(rest, cancellation);
            else if (TryStrip(id, "height.", out rest)) { if (rest != "info") await HeightCheckEvent(rest, input.Value, cancellation); }
            else if (TryStrip(id, "replay.", out rest)) await ReplayEvent(rest, input.Value, cancellation);
            else if (TryStrip(id, "wizard.", out rest))
            {
                if (!calibrationOpen) throw new InvalidOperationException("Open Calibration before continuing its guided measurements.");
                await WizardEvent(rest, input.Value, cancellation);
            }
            else if (TryStrip(id, "profiles.", out rest))
            {
                if (calibrationOpen && (id.StartsWith("profiles.select.", StringComparison.Ordinal) || id is "profiles.add" or "profiles.remove"))
                { await StopHeightCheck(cancellation); await ClearWizardHighlight(cancellation); }
                await ProfileEvent(rest, cancellation);
                // Selecting a robot returns to its controls through the same
                // publication gate as every other navigation.
                await PublishWorkspaceView(cancellation, Navigation.Navigate("controls"));
            }
            else if (TryStrip(id, "pika.reference.", out rest)) { await PikaReferenceEvent(rest, cancellation); return; }
            else await ControlEvent(id, input, cancellation);
            await Update(cancellation);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            uiActionError = "Action failed: " + error.Message;
            message = uiActionError;
            await Update(cancellation);
        }
        finally { commands.Release(); }
    }

    private static bool TryStrip(string id, string prefix, out string rest)
    {
        bool match = id.StartsWith(prefix, StringComparison.Ordinal);
        rest = match ? id[prefix.Length..] : "";
        return match;
    }

    /// <summary>Guards shared by every queued event except Follow's own controls.</summary>
    private async Task AdmitControlEvent(string id, CancellationToken cancellation)
    {
        if (GroupFollowActive && id is "disable" or "motion.hold")
            await StopGroupSimulationFollowing("Operator stopped the follower group", cancellation);
        if (GroupFollowActive && GroupBlocksControlEvent(id))
            throw new InvalidOperationException("Stop following in the left sidebar before changing robot controls or assignments.");
        if (Navigation.Page != WorkspacePage.Controls && WorkspaceCommandPolicy.RequiresControls(id))
            throw new InvalidOperationException("Return to Controls before enabling or moving the robot.");
    }

    /// <summary>Robot, camera and preference controls that have no prefix handler.</summary>
    private async Task ControlEvent(string id, UiEvent input, CancellationToken cancellation)
    {
        if (teleoperating && id is "stop" or "disable" or "recovery.run" or "link")
        {
            try { await PauseTeleoperation(cancellation); }
            catch (Exception error) when (error is not OperationCanceledException) { message = error.Message; }
        }
        if (TryStrip(id, "backend.", out string rest))
            await BackendEvent(rest, input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString() ?? "" : "", cancellation);
        else if (TryStrip(id, "teaching.pika.", out rest))
            await PikaTeachingEvent(rest, cancellation);
        else if (id == "enable")
        {
            await PrimaryRobotAction(cancellation);
        }
        else if (id == "safety.accept")
        {
            await RunPrimaryAction(async ct =>
            {
                var confirmation = await AcceptWorkspaceClearance(input.WidgetId, ct);
                await CompleteRobotEnable(confirmation.Epoch, confirmation.Scope, ct);
            }, cancellation);
            await Render(cancellation);
        }
        else if (id == "safety.cancel") CommandAuthority.DismissConfirmation(DisplayedConfirmation(input.WidgetId, id));
        else if (id == "disable")
        {
            link = null;
            await Command("disable", null, cancellation);
            if (telemetry.Pika.Owned) await Call("/api/pika/disable", new JsonObject { ["owner"] = owner }, cancellation);
        }
        else if (id == "recovery.home") await StartRecoveryHome(cancellation);
        else if (id is "recovery.card.run" or "recovery.run") await RunRecoveryCard(cancellation, verifyOnly: id == "recovery.run");
        else if (id == "recovery.reset.confirm") await ConfirmResetCompletion(cancellation);
        else if (id == "recovery.reset.cancel") CancelResetCompletion();
        else if (id.StartsWith("camera.", StringComparison.Ordinal))
        {
            string[] parts = id.Split('.'); string camera = parts[1];
            var settings = CameraSettings(camera);
            if (parts[2] == "settings") await WorkspaceEvent("settings.cameras." + camera, cancellation);
            else if (parts[2].StartsWith("sim", StringComparison.Ordinal))
            {
                await CameraComparisonEvent(camera, parts[2], input.Value, input.WidgetId, cancellation);
            }
            else if (parts[2].StartsWith("calibration", StringComparison.Ordinal))
                await CameraCalibrationEvent(camera, parts[2], cancellation);
            else
            {
                if (parts[2] == "device") settings = settings with { Device = CameraDeviceChoices.Resolve(cameraDeviceChoices, input.Value, CameraSettings(camera).Device), Enabled = true };
                if (parts[2] == "toggle") settings = settings with { Enabled = !settings.Enabled };
                if (camera == "depth" && parts[2] is "view" or "selectcolor" or "selectdepth") settings = D405View.Select(settings, parts[2] == "view" ? input.Value.GetInt32() : parts[2] == "selectdepth" ? 1 : 0);
                if (parts[2] == "serial") preferences = preferences with { DepthSerial = CameraDeviceChoices.Resolve(depthDeviceChoices, input.Value, "") };
                preferences = camera switch { "bench" => preferences with { Bench = settings }, "gripper" => preferences with { Gripper = settings }, _ => preferences with { Depth = settings } };
                await Save(cancellation); await RestartCamera(camera);
                await Render(cancellation);
            }
        }
        else if (id == "link")
        {
            if (link is not null)
            {
                RememberMeasuredTracking(false);
                await Save(cancellation);
                link = null; linkCommandsEnabled = false; message = "Robot unlinked · automatic measured tracking off for this robot";
                await engine.RequestAsync("robot.unsubscribe", cancellation: cancellation);
            }
            else
            {
                RememberMeasuredTracking(true);
                await Save(cancellation);
                await StartMeasuredLink(automatic: false, cancellation);
                message = "Measured pose linked. Start Teleoperate to drag joints; release finishes the target. Calibration is not verified.";
                await Render(cancellation);
            }
        }
        else if (id == "teleoperate.toggle")
        {
            if (teleoperating) { await PauseTeleoperation(cancellation); message = "Teleoperate paused · measured pose tracking continues"; }
            else await SelectTeleoperationJoint(link?.JointNames.Contains(teleoperationJoint, StringComparer.Ordinal) == true
                ? teleoperationJoint : link?.JointNames.Last() ?? ActiveProfile.RobotJointPrefix + "joint6", cancellation);
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
            await SelectTeleoperationJoint((link?.JointPrefix ?? ActiveProfile.RobotJointPrefix) + "joint" + id.Split('.')[2], cancellation);
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
            if (id == "speed" && SimulationFollowingActive)
                throw new InvalidOperationException("Stop following before changing its prepared speed.");
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
            long epoch = CommandAuthority.Epoch;
            var profile = ActiveProfile;
            var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for the controller server.");
            var device = Device(expectedToken: receipt.BackendToken, sourceReceipt: receipt);
            var pika = receipt.State["pika"] as JsonObject;
            if (ManualPikaBlocker(device, pika) is { } blocked) throw new InvalidOperationException(blocked);
            double opening;
            if (id == "pika.target") opening = FiniteControlNumber(input.Value, "Gripper opening");
            else if (id == "pika.zero") opening = 0;
            else
            {
                if (!PikaDisplayOpening(pika!, out double currentOpening))
                    throw new InvalidOperationException("Waiting for a valid gripper opening. If the physical jaws are closed, set their display reference first.");
                opening = Math.Clamp(currentOpening + (id == "pika.plus" ? 1 : -1), PikaProtocol.MinimumOpeningMm, PikaProtocol.MaximumOpeningMm);
            }
            if (epoch != CommandAuthority.Epoch || profile != ActiveProfile)
                throw new InvalidOperationException("Control permission changed before the gripper target was sent.");
            await Call("/api/pika/position", new JsonObject { ["owner"] = owner, ["value"] = PikaProtocol.Angle(opening),
                ["pika_device"] = profile.PikaDevice, ["pika_distribution"] = profile.PikaDistribution }, cancellation, receipt.BackendToken);
        }
    }

    internal async Task Enable(CancellationToken cancellation, bool includePika = true, RobotCommandScope? expected = null,
        long? expectedPermissionEpoch = null)
    {
        long enableEpoch = expectedPermissionEpoch ?? CommandAuthority.Epoch;
        if (calibrationOpen) throw new InvalidOperationException("Calibrate robot is read-only. Use Back to robot controls before enabling motors.");
        var device = expected is { } pinned ? Device(pinned) : Device(expectedToken: token);
        var scope = expected ?? new RobotCommandScope(ActiveProfile.Id, token, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>());
        string expectedToken = scope.Backend;
        JsonObject CheckEnableScope()
        {
            if (!approved || enableEpoch != CommandAuthority.Epoch)
                throw new InvalidOperationException("Enable was cancelled by STOP or a control-scope change. No later power action will resume.");
            var current = Device(scope);
            var teaching = TeachingState.Inspect(current);
            if (teaching.BlocksTargets) throw new InvalidOperationException(teaching.NextStep);
            return current;
        }
        // Enable never verifies or clears a controller latch: only the Controls
        // recovery card sends recovery-purpose verification (with the operator's
        // reset acknowledgement), and readiness already routes there.
        var initial = CheckEnableScope();
        if (initial["recovery_required"]?.GetValue<bool>() == true || initial["reset_pending"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("The controller needs recovery first. Use Recover robot on Controls; Enable will not clear STOP or verify a reset.");
        if (includePika) { await ConfigureProfilePika(expectedToken, cancellation); CheckEnableScope(); }
        if (!device["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true))
        {
            // The id comes from this command's own acknowledgement. The tracked lease is
            // shared state: telemetry may already have ended it for a fast enable.
            var accepted = await Command("enable", null, cancellation, expected: scope, expectedPermissionEpoch: enableEpoch);
            long request = accepted["request_id"]?.GetValue<long>() ?? 0;
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                var current = CheckEnableScope();
                var motion = state?["motion"];
                if (motion?["request_id"]?.GetValue<long>() == request && motion["active"]?.GetValue<bool>() == false)
                {
                    if (current["joints"]!.AsArray().All(j => j?["enabled"]?.GetValue<bool>() == true) && current["recovery_required"]?.GetValue<bool>() != true) break;
                    throw new InvalidOperationException(state?["message"]?.GetValue<string>() ?? "The robot did not enable all six motors.");
                }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 10)
                    throw new InvalidOperationException("Waiting for robot enable feedback timed out. Check the controller status.");
                await Task.Delay(25, cancellation);
            }
        }
        CheckEnableScope();
        if (includePika && pikaTeachingDisableRequest.Length == 0 && PikaMatchesProfile() && state?["pika"]?["configured"]?.GetValue<bool>() == true)
        {
            bool gripperAlreadyReady = telemetry.Pika.Owned && state["pika"]?["fresh"]?.GetValue<bool>() == true &&
                state["pika"]?["owner"]?.GetValue<string>() == owner && state["pika"]?["enabled"]?.GetValue<bool>() == true && state["pika"]?["armed"]?.GetValue<bool>() == true;
            if (!gripperAlreadyReady)
            {
                var role = telemetry.Pika.Observe();
                await Call("/api/pika/connect", new JsonObject { ["owner"] = owner, ["take_over"] = true }, cancellation, expectedToken);
                CheckEnableScope();
                if (!telemetry.Pika.TryAdvance(role, PikaSessionRole.Owned))
                    throw new InvalidOperationException("The Pika session was released while Enable connected it. Enable again when the gripper is reconnected.");
                long started = Stopwatch.GetTimestamp();
                while (state?["pika"]?["fresh"]?.GetValue<bool>() != true)
                {
                    CheckEnableScope();
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3)
                        throw new InvalidOperationException("Pika connected but fresh motor feedback has not arrived. Enable again when feedback is live.");
                    await Task.Delay(25, cancellation);
                }
                var current = CheckEnableScope();
                if (current["recovery_required"]?.GetValue<bool>() == true ||
                    current["reset_pending"]?.GetValue<bool>() == true || !RobotPowerState.Inspect(current).AllEnabled)
                    throw new InvalidOperationException("Arm holding control changed before the gripper could be enabled. Check the robot status.");
                await Call("/api/pika/enable", new JsonObject { ["owner"] = owner, ["clear"] = true }, cancellation, expectedToken);
                CheckEnableScope();
            }
        }
        message = !includePika ? "Six arm motors enabled · Pika power unchanged" : pikaTeachingDisableRequest.Length > 0 ? "Arm controls ready · Pika remains free; use Hold jaws here to resume at its measured opening" : PikaMatchesProfile() ? "Controls enabled · robot and assigned Pika use the same controller session" : "Robot controls enabled · no Pika assigned to this robot";
        if (CurrentRecovery?.Epoch != enableEpoch) RememberControlApproval(enableEpoch, activate: true);
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

    private Task RestartCamera(string id) => cameras.ReplaceAsync(id, () =>
    {
        if (id == "depth")
        {
            // Color and depth can share a serial/resolution but not intrinsics.
            // Keep cached images hidden until this new worker publishes its frame.
            calibration.Lens.RemoveProfile(id); cameraDimensions.TryRemove(id, out _);
            cameraStatus[id] = "Connecting D405 " + D405View.Mode(CameraSettings(id)) + " view…";
        }
        var camera = new CameraCapture(engine, id, CameraSettings(id), (key, value) => cameraStatus[key] = value, preferences.DepthSerial,
            (key, width, height) => cameraDimensions[key] = (width, height),
            (key, profile) => { Materialize(profile); calibration.Lens.InstallProfile(key, profile); }, CameraImageId(id));
        camera.Start();
        return camera;
    });

    private bool ManualPikaControlAllowed(JsonObject? device) => approved && device?["fresh"]?.GetValue<bool>() == true &&
        device["status"]?["arm_status"]?.GetValue<int>() != 1 && device["status"]?["error"]?.GetValue<int>() == 0 &&
        device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true && !TeachingState.Inspect(device).BlocksTargets;

    /// <summary>The one admission rule for manual Pika opening targets. Render
    /// enables the widgets only when this is null; the handler rejects with it,
    /// so a queued or hotkey event cannot bypass what the panel shows.</summary>
    private string? ManualPikaBlocker(JsonObject? device, JsonObject? pika)
    {
        if (PikaTeachingRecording) return "Use Free jaws / Hold jaws here while teaching, or finish the observation session before sending an opening target.";
        if (switchingProfile) return "Changing robot assignment; wait for measured feedback.";
        if (!ManualPikaControlAllowed(device))
            return "Restore robot controls before sending a gripper target. STOP and recovery retire manual arm and gripper targets.";
        if (!ProfilePikaRouting.Matches(ActiveProfile, pika)) return "The Pika serial assignment does not match this robot.";
        if (!telemetry.Pika.Owned || pika!["owner"]?.GetValue<string>() != owner || pika["armed"]?.GetValue<bool>() != true)
            return "Pika is monitoring only. Use Enable robot before commanding its opening.";
        if (pika["fresh"]?.GetValue<bool>() != true) return "Waiting for fresh gripper feedback before changing its opening.";
        if (pika["enabled"]?.GetValue<bool>() != true) return "Pika motor power is off. Use Enable robot before commanding its opening.";
        if (pika["pending"]?.GetValue<bool>() == true) return "Wait for the previous gripper target to finish before sending another.";
        return null;
    }

    internal Task Update(CancellationToken cancellation)
    {
        if (CaptureWorkspacePatchScope() is not { } scope) return Task.CompletedTask;
        var evidence = new WorkspacePatchEvidence(telemetry.Latest, state, token, CommandAuthority.Epoch);
        if (evidence.Receipt is { } captured && !ReferenceEquals(captured.State, evidence.State)) return Task.CompletedTask;
        var snapshot = evidence.State;
        var settings = scope.Preferences;
        var profile = RobotProfileRouting.Active(settings);
        var patches = new Dictionary<string, JsonObject>
        {
            ["link"] = new() { ["text"] = link is null ? "Link robot" : "Unlink robot" }
        };
        patches["safety"] = new() { ["visible"] = scope.View?.Confirmation is { } presented &&
            ReferenceEquals(CommandAuthority.Current.Confirmation, presented) };
        AddResetCompletionPatches(patches);
        patches["backend.restart.confirmation"] = new() { ["visible"] = backendRestart == BackendRestartPhase.Confirming };
        patches["backend.status"] = new() { ["text"] = backendStatus };
        patches["backend.prerequisites"] = new() { ["text"] = setupStatus };
        patches["workspace.action-error"] = new() { ["text"] = uiActionError, ["hint"] = uiActionError, ["visible"] = uiActionError.Length > 0 };
        AddProfilePatches(patches);
        JsonObject? device = null;
        if (evidence.Receipt is { } receipt)
        {
            try { device = Device(sourceReceipt: receipt); }
            catch (InvalidOperationException) { }
        }
        bool movable = scope.Context.Page == WorkspacePage.Controls && approved && !TeachingState.Inspect(device).BlocksTargets && snapshot?["motion"]?["active"]?.GetValue<bool>() != true && device?["fresh"]?.GetValue<bool>() == true && device["recovery_required"]?.GetValue<bool>() != true && string.IsNullOrEmpty(device["blocker"]?.GetValue<string>());
        var joints = device?["joints"]?.AsArray();
        bool ownMove = snapshot?["motion"]?["active"]?.GetValue<bool>() == true && snapshot["motion"]?["owner"]?.GetValue<string>() == owner;
        var remaining = joints?.Select((j, i) => new { Joint = i + 1, Actual = j?["actual_deg"]?.GetValue<double>(), Target = j?["target_deg"]?.GetValue<double>() })
            .Where(j => j.Actual.HasValue && j.Target.HasValue && Math.Abs(j.Target.Value - j.Actual.Value) > settings.Tolerance)
            .Select(j => $"J{j.Joint} {j.Actual:F1}° → {j.Target:F1}°").ToArray() ?? [];
        patches["motion.progress"] = new() { ["text"] = ownMove
            ? (snapshot?["motion"]?["following"]?.GetValue<bool>() == true ? "Following drag · " : "Movement in progress · ") + (remaining.Length > 0 ? string.Join(" · ", remaining) : "settling at target")
            : device?["recovery_required"]?.GetValue<bool>() == true ? "Stopped · recovery required" : device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? "Range overrun · move the affected joint inward or Move all to zero" : "Idle" };
        patches["motion.hold"] = new() { ["enabled"] = ownMove && joints?.Any(j => j?["target_deg"] is not null) == true };
        bool canTeleoperate = movable && link is not null && linkCommandsEnabled && device?["range_recovery"]?["required"]?.GetValue<bool>() != true && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true) == true;
        patches["teleoperate.toggle"] = new() { ["enabled"] = teleoperating || canTeleoperate };
        for (int i = 1; i <= 6; i++) patches["teleoperate.joint." + i] = new() {
            ["text"] = teleoperating && teleoperationJoint == (link?.JointPrefix ?? profile.RobotJointPrefix) + "joint" + i ? "[J" + i + "]" : "J" + i,
            ["enabled"] = teleoperating && canTeleoperate };
        for (int i = 1; i <= 6; i++)
        {
            var joint = joints?.ElementAtOrDefault(i - 1);
            bool available = movable && joint?["enabled"]?.GetValue<bool>() == true && joint["limits_fresh"]?.GetValue<bool>() == true && joint["can_move"]?.GetValue<bool>() != false;
            double? actual = joint?["actual_deg"]?.GetValue<double>();
            patches[$"joint.{i}.value"] = new() { ["text"] = actual?.ToString("F1") ?? "—" };
            patches[$"joint.{i}.current"] = new() { ["text"] = joint?["current_a"]?.GetValue<double>().ToString("F2") ?? "—" };
            string rangeHint = joint?["motion_blocker"]?.GetValue<string>() ?? $"Reported range {joint?["min_deg"]}° to {joint?["max_deg"]}°";
            patches[$"joint.{i}.target"] = new() { ["value"] = actual ?? 0, ["min"] = joint?["min_deg"]?.DeepClone() ?? JsonValue.Create(-180), ["max"] = joint?["max_deg"]?.DeepClone() ?? JsonValue.Create(180), ["enabled"] = available, ["hint"] = rangeHint };
            patches[$"joint.{i}.minus"] = new() { ["enabled"] = available && joint?["can_move_negative"]?.GetValue<bool>() != false, ["hint"] = rangeHint };
            patches[$"joint.{i}.plus"] = new() { ["enabled"] = available && joint?["can_move_positive"]?.GetValue<bool>() != false, ["hint"] = rangeHint };
            patches[$"joint.{i}.zero"] = new() { ["enabled"] = available, ["hint"] = rangeHint };
        }
        patches["zero.all"] = new() { ["enabled"] = movable && joints?.All(j => j?["enabled"]?.GetValue<bool>() == true && j["limits_fresh"]?.GetValue<bool>() == true) == true && (device?["range_recovery"]?["required"]?.GetValue<bool>() != true || device["range_recovery"]?["can_move_to_zero"]?.GetValue<bool>() == true), ["hint"] = device?["range_recovery"]?["required"]?.GetValue<bool>() == true ? device["range_recovery"]!["reason"]?.GetValue<string>() ?? "Slow range entry, then synchronized zero position. Encoder zeros are unchanged." : "Move all joints to zero angles. This does not calibrate encoder zeros." };
        var pika = PikaMatchesProfile() ? snapshot?["pika"] : null;
        double? opening = PikaDisplayOpening(pika as JsonObject, out double measuredOpening) ? measuredOpening : null;
        string pikaReading = string.IsNullOrEmpty(profile.PikaDevice) ? "Unassigned" : opening.HasValue ? $"{opening.Value:F1} mm"
            : pika?["connected"]?.GetValue<bool>() != true ? "Disconnected" : GripperLink.ClosedDisplayReferenceNeeded(pika as JsonObject) ? "Set closed ref." : "No feedback";
        patches["pika.value"] = new() { ["text"] = pikaReading, ["hint"] = telemetry.Pika.Status + " Raw encoder: " +
            (pika?["telemetry"]?["motor"]?["Position"]?.ToJsonString() ?? "unavailable") + " rad. " +
            (PikaDisplayReferenceAngle(pika as JsonObject) is double angle ? $"Closed display reference {angle:F5} rad; motor targets unchanged." : "Nominal reference; a negative closed reading may need an operator-confirmed display reference.") };
        bool manualPika = scope.Context.Page == WorkspacePage.Controls && ManualPikaBlocker(device, pika as JsonObject) is null;
        patches["pika.target"] = new() { ["value"] = opening ?? 0, ["enabled"] = manualPika };
        patches["pika.monitor"] = new() { ["text"] = telemetry.Pika.Status };
        var recoveryCard = RecoveryCardState(device);
        bool verifiable = VerifiesOnly(recoveryCard) && RecoveryCardCanRun(recoveryCard);
        patches["recovery.run"] = new() { ["enabled"] = verifiable, ["text"] = verifiable ? recoveryCard.ActionText : "Verify controller state",
            ["hint"] = recoveryCard.NextStep };
        foreach (string id in new[] { "bench", "gripper", "depth" })
        {
            patches["camera." + id + ".image"] = new() { ["visible"] = CameraFrameReady(id) };
            patches["camera." + id + ".info"] = new() { ["text"] = cameraStatus.GetValueOrDefault(id, "Waiting for a fresh frame…") };
        }
        patches["camera.gripper.calibration.status"] = new() { ["text"] = lens.Status };
        AddTeachingPatches(patches, device);
        AddHeightCheckPatches(patches, device);
        AddCameraComparisonPatches(patches);
        foreach (string action in new[] { "minus", "plus", "zero" }) patches["pika." + action] = new() { ["enabled"] = manualPika };
        AddWorkspacePatches(patches, device);
        AddPikaReferencePatches(patches, pika as JsonObject);
        AddUnifiedWorkspacePatches(patches, device);
        AddRecoveryHomePatches(patches);
        AddGroupFollowPatches(patches);
        return PublishWorkspacePatch(scope, patches, cancellation, evidence);
    }

    public async ValueTask DisposeAsync()
    {
        var cameraRetirement = cameras.DisposeAsync().AsTask();
        passiveMeasuredTrackingStarted = false;
        await Quietly(ct => StopGroupSimulationFollowing("Plugin closed · following stopped", ct), TimeSpan.FromSeconds(8), "Group following");
        if (uiEvents is { } dispatch) await Quietly(dispatch.DisposeAsync().AsTask(), "UI actions");
        if (CurrentRecovery is { } recovery)
        {
            recovery.Cancel();
            await Quietly(recovery.Worker?.WaitAsync(TimeSpan.FromSeconds(5)), "Recovery");
        }
        await Quietly(RetireCameraDiscovery(), "Camera discovery");
        using (var markerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await Quietly(StopHeightCheck(markerTimeout.Token), "Height guide");
            await Quietly(ClearWizardHighlight(markerTimeout.Token), "Calibration marker");
        }
        await Quietly(telemetry.Pika.StopAsync(), "Pika monitor");
        await Quietly(ct => FinishPikaTeaching("Plugin closed", ct), TimeSpan.FromSeconds(1), "Pika teaching");
        await Quietly(HoldFollowing, TimeSpan.FromSeconds(1), "Following");
        // Keep telemetry/heartbeats running until this session's Pika disable is
        // verified. Closing an external backend must not strand its old owner.
        await Quietly(ReleaseOwnedPikas, TimeSpan.FromSeconds(3), "Pika release");
        await shutdown.CancelAsync();
        await Quietly(StopFollowUp, "STOP follow-up");
        await Quietly(wizardHighlightMaintenance, "Calibration marker maintenance");
        await Quietly(calibration.Height.MaintenanceIdle, "Height guide maintenance");
        await Quietly(calibration.Lens.Worker, "Lens calibration");
        await Quietly(cameraRetirement, "Camera");
        await Quietly(worker, "Telemetry");
        await StopOwnedBackend();
        // An externally managed server keeps running; its existing lease expiry handles a lost client.
        channel.Dispose(); commands.Dispose(); shutdown.Dispose();
    }

    /// <summary>Awaits one shutdown step and logs its failure instead of aborting disposal.
    /// Cancellation is silent once <paramref name="expected"/> (default: plugin shutdown)
    /// has been requested; a step's own timeout is still logged.</summary>
    private async Task Quietly(Task? task, string label, CancellationToken? expected = null)
    {
        if (task is null) return;
        try { await task; }
        catch (Exception error) when ((expected ?? shutdown.Token).IsCancellationRequested &&
            error is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled }) { }
        catch (Exception error) { Trace.TraceWarning("{0} shutdown: {1}", label, error.Message); }
    }

    private async Task Quietly(Func<CancellationToken, Task> step, TimeSpan budget, string label)
    {
        using var timeout = new CancellationTokenSource(budget);
        Task task;
        try { task = step(timeout.Token); }
        catch (Exception error) { task = Task.FromException(error); }
        await Quietly(task, label);
    }
}
