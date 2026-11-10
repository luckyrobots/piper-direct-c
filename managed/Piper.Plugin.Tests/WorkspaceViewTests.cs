using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class WorkspaceViewTests
{
    public static void Run()
    {
        foreach (int enabled in Enumerable.Range(0, 7))
        {
            var device = PowerDevice(enabled);
            var power = RobotPowerState.Inspect(device);
            string expected = enabled == 0 ? "Disabled" : enabled == 6 ? "Enabled" : $"{enabled}/6 enabled";
            Check(power.EnabledCount == enabled && power.AllEnabled == (enabled == 6) && power.Label == expected,
                "Motor power did not reflect all six fresh enabled flags: " + enabled);
            foreach (int teaching in new[] { 0, 1, 3 })
            {
                device["status"]!["teach_state"] = teaching;
                Check(RobotPowerState.Inspect(device) == power,
                    "Teaching mode changed reported motor power instead of independent control permission: " + teaching);
            }
        }
        foreach (int missing in Enumerable.Range(0, 8))
        {
            var device = PowerDevice(6);
            if (missing == 0) device.Remove("fresh");
            if (missing == 1) device["fresh"] = false;
            if (missing == 2) device.Remove("joints");
            if (missing == 3) device["joints"]!.AsArray().RemoveAt(5);
            if (missing == 4) device["joints"]!.AsArray().Add(new JsonObject { ["fresh"] = true, ["enabled"] = true });
            if (missing == 5) device["joints"]![2]!["fresh"] = false;
            if (missing == 6) device["joints"]![2]!.AsObject().Remove("enabled");
            if (missing == 7) device["joints"]![2]!["enabled"] = "true";
            var power = RobotPowerState.Inspect(device);
            Check(power.EnabledCount is null && !power.AllEnabled && power.Label == "Power unavailable",
                "Incomplete or stale motor feedback was presented as known power: " + missing);
        }
        Check(RobotPowerState.Inspect(null) is { EnabledCount: null, AllEnabled: false, Label: "Power unavailable" },
            "A disconnected robot retained an enabled power indication");

        const long epoch = 12;
        const string profile = "piper-1";
        foreach (string id in new[] { "joint.6.target", "enable", "safety.accept", "pika.target", "calibration.capture", "calibration.width", "speed", "camera.gripper.settings", "group.follow.selected" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            Check(wire == "view.12.robot.piper-1." + id, "Action was not scoped to its rendered view and robot: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch, profile, wire) == id, "A current rendered event was rejected: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire).Length == 0, "A previous page event survived navigation: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch, "piper-2", wire).Length == 0, "A rendered event crossed robot profiles: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch, profile, RobotProfileRouting.ScopeEvent(profile, id)).Length == 0,
                "A legacy event without a rendered-view epoch remained actionable: " + id);
        }

        foreach (string id in new[] { "profiles.select.piper-2", "profiles.add", "camera.bench.settings", "camera.bench.stop", "backend.connect" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            Check(wire == "view.12." + id && WorkspaceEventScope.Unwrap(epoch, profile, wire) == id,
                "A shared workspace event lost its view scope: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire).Length == 0,
                "An old shared bench/navigation event survived a new view: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch, profile, id).Length == 0,
                "A shared event bypassed the rendered-view scope: " + id);
        }

        foreach (string id in new[] { "stop", "disable", "recovery.guide.cancel", "motion.hold" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            Check(wire == "robot.piper-1." + id, "Persistent hold/stop gained a page epoch: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire) == id,
                "Navigation prevented stopping the same physical robot: " + id);
            Check(WorkspaceEventScope.Unwrap(epoch + 1, "piper-2", wire).Length == 0,
                "Persistent hold/stop escaped its physical robot profile: " + id);
        }

        foreach (string id in new[] { "engine.robot.target", "engine.robot.follow" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            Check(wire == id && WorkspaceEventScope.Unwrap(epoch + 1, profile, wire) == id,
                "A native engine event incorrectly required a UI-render epoch: " + id);
            Check(WorkspaceCommandPolicy.RequiresControls(id), "A native target could bypass the controls-page policy: " + id);
        }

        foreach (string wire in new[]
        {
            "", "joint.6.target", "robot.piper-1.joint.6.target", "view.12.joint.6.target", "view.120.robot.piper-1.joint.6.target",
            "view.12x.robot.piper-1.joint.6.target", "view.-12.robot.piper-1.joint.6.target", "view..robot.piper-1.joint.6.target",
            "view.9223372036854775808.robot.piper-1.joint.6.target", "view.12.robot.piper-11.joint.6.target",
            "view.12.robot.piper-2.joint.6.target", "view.12.view.12.robot.piper-1.joint.6.target", "view.12."
        })
            Check(WorkspaceEventScope.Unwrap(epoch, profile, wire).Length == 0, "Malformed or foreign event was interpreted as current: " + wire);

        foreach (string id in new[]
        {
            "enable", "safety.accept", "teleoperate.toggle", "teleoperate.joint.6", "joint.1.minus", "joint.3.target", "joint.6.zero", "zero.all", "pika.target"
        })
            Check(WorkspaceCommandPolicy.RequiresControls(id), "A motion/enable action remained valid outside robot controls: " + id);

        foreach (string id in new[]
        {
            "stop", "motion.hold", "disable", "recovery.run", "calibration.capture", "calibration.inspect", "camera.bench.settings", "profiles.select.piper-2"
        })
            Check(!WorkspaceCommandPolicy.RequiresControls(id), "A stop, passive setup or explicit recovery action required the controls page: " + id);
    }

    public static async Task RunRendered()
    {
        // The real renderer/event handler is exercised without StartAsync: no
        // background workers, sockets, camera captures or device commands run.
        var plugin = new PipersPlugin();
        string folder = Path.Combine(Path.GetTempPath(), "piper-workspace-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1"));
        plugin.preferences = prefs; plugin.calibrationProfile = "piper-1";
        JsonObject? rendered = null, patched = null;
        var engineRequests = new List<(string Method, JsonObject Arguments)>();
        var context = Fixtures.Context(new PluginStart("fixture", folder, folder),
                (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!.AsObject();
                    if (json["type"]?.GetValue<string>() == "view") rendered = json["root"]!.AsObject();
                    if (json["type"]?.GetValue<string>() == "patch") patched = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((method, args, _) =>
                {
                    engineRequests.Add((method, (JsonObject)args.DeepClone()));
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false }));
                }));
        plugin.engine = context;
        IEnumerable<JsonObject> Walk(JsonObject node)
        {
            yield return node;
            foreach (var child in node["children"]?.AsArray().OfType<JsonObject>() ?? [])
                foreach (var descendant in Walk(child)) yield return descendant;
        }
        string WidgetId(string suffix) => Walk(rendered!).Single(n => n["id"]!.GetValue<string>().EndsWith("." + suffix, StringComparison.Ordinal))["id"]!.GetValue<string>();
        Task Click(string id, int? selection = null) => plugin.HandleEventAsync(new("luckyrobots.pipers.controls", id,
            selection.HasValue ? "change" : "click", JsonSerializer.SerializeToElement(selection)), CancellationToken.None);
        static JsonObject CameraInventory(string sceneId, params string[] ids) => new()
        {
            ["sceneId"] = sceneId, ["cameras"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["entityId"] = id, ["name"] = id }).ToArray())
        };
        int Depth(JsonObject node) => 1 + (node["children"]?.AsArray().OfType<JsonObject>().Select(Depth).DefaultIfEmpty(0).Max() ?? 0);
        async Task CheckView(string page, string category = "robot", string camera = "bench")
        {
            plugin.workspace.Commit(WorkspaceNavigation.At(Enum.Parse<WorkspacePage>(page, true), Enum.Parse<SettingsCategory>(category, true))
                with { SettingsCamera = camera, CameraDetail = camera });
            bool calibration = page == "settings" && category == "bench";
            Check(plugin.workspace.Navigation.CalibrationOpen == calibration, "Calibration was not derived from the bench settings page: " + page + "/" + category);
            await plugin.Render(CancellationToken.None);
            var nodes = Walk(rendered!).ToArray();
            string[] ids = nodes.Select(n => n["id"]!.GetValue<string>()).ToArray();
            string label = page + "/" + category + "/" + camera;
            Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "Duplicate widget IDs in rendered " + label);
            Check(ids.Length <= 2048 && Depth(rendered!) <= 24, "Rendered view exceeds public SDK limits: " + label);
            if (calibration)
            {
                Check(Enumerable.Range(0, 5).All(row => ids.Any(id => id.EndsWith("sheet.capture." + row, StringComparison.Ordinal))), "The calibration sheet hides uncaptured rows behind navigation");
                Check(Enumerable.Range(0, 5).All(row => new[] { "left", "back" }.All(axis => ids.Any(id => id.EndsWith($"sheet.field.p{row}{axis}", StringComparison.Ordinal)))), "All five measurement pairs must be editable on one page");
                Check(!ids.Any(id => id.Contains("wizard.navigation", StringComparison.Ordinal) || id.Contains(".wizard.", StringComparison.Ordinal) && (id.EndsWith(".back", StringComparison.Ordinal) || id.EndsWith(".next", StringComparison.Ordinal))), "The single-page calibration retained Next/Back questions");
                Check(nodes.Any(n => n["text"]?.GetValue<string>()?.Contains("height = 0 mm", StringComparison.Ordinal) == true) && !ids.Any(id => id.EndsWith(".sheet.field.height", StringComparison.Ordinal)), "Desk contact calibration must describe tabletop contact and expose no hover-height input");
                Check(nodes.Where(n => n["type"]?.GetValue<string>() == "input").All(n => n["commitOnly"]?.GetValue<bool>() == false), "Calibration measurements wait for blur/Enter instead of publishing edits live");
            }
            Check(ids.Count(id => id == "robot.piper-1.stop") == 1, "Persistent profile-bound STOP is missing/duplicated: " + label);
            long current = plugin.workspace.Epoch;
            Check(ids.All(id => WorkspaceEventScope.Unwrap(current, "piper-1", id).Length > 0), "Rendered widget escaped its current view/profile scope: " + label);
            await plugin.Update(CancellationToken.None);
            Check(plugin.workspace.Epoch == current && patched!.All(p => WorkspaceEventScope.Unwrap(current, "piper-1", p.Key).Length > 0),
                "Telemetry update used an old view epoch or rebuilt the page: " + label);
            if (page != "controls")
                Check(patched![WorkspaceEventScope.Wrap(current, "piper-1", "enable")]?["enabled"]?.GetValue<bool>() == false,
                    "A setup page re-enabled hardware controls through its common telemetry patch: " + label);
        }
        try
        {
            await CheckView("controls");
            JsonObject HeaderPatch(string id) => patched![WorkspaceEventScope.Wrap(plugin.workspace.Epoch, "piper-1", id)]!.AsObject();
            Check(HeaderPatch("workspace.power")["text"]?.GetValue<string>() == "Power unavailable" &&
                HeaderPatch("enable")["enabled"]?.GetValue<bool>() == false,
                "The disconnected header presented known motor power or actionable enable controls");
            long headerSequence = 0;
            foreach (var sample in new[]
            {
                (Enabled: 0, Approved: false, Teach: 0, Power: "Disabled", Button: "Enable robot", Visible: true, Actionable: true),
                (Enabled: 3, Approved: false, Teach: 0, Power: "3/6 enabled", Button: "Enable robot", Visible: true, Actionable: true),
                (Enabled: 6, Approved: false, Teach: 0, Power: "Enabled", Button: "Enable robot", Visible: true, Actionable: true),
                (Enabled: 6, Approved: true, Teach: 0, Power: "Enabled", Button: "Enabled", Visible: true, Actionable: false),
                (Enabled: 6, Approved: false, Teach: 1, Power: "Enabled", Button: "Hand guiding active", Visible: true, Actionable: false),
                (Enabled: 6, Approved: true, Teach: 3, Power: "Enabled", Button: "Playback active", Visible: true, Actionable: false)
            })
            {
                var device = PowerDevice(sample.Enabled);
                device["status"]!["teach_state"] = sample.Teach;
                var snapshot = new JsonObject { ["token"] = "header-fixture", ["selected"] = "can-a", ["devices"] = new JsonArray(device) };
                plugin.telemetry.PublishState(snapshot); plugin.telemetry.AdoptBackend("header-fixture"); plugin.approved = sample.Approved;
                plugin.telemetry.Publish(new(snapshot, "header-fixture", ++headerSequence, Stopwatch.GetTimestamp(), 0));
                await CheckView("controls");
                Check(HeaderPatch("workspace.power")["text"]?.GetValue<string>() == sample.Power &&
                    HeaderPatch("enable")["text"]?.GetValue<string>() == sample.Button &&
                    HeaderPatch("enable")["visible"]?.GetValue<bool>() == sample.Visible &&
                    HeaderPatch("enable")["enabled"]?.GetValue<bool>() == sample.Actionable,
                    $"Header confused motor power with approval/teaching permission: {sample.Enabled}/{sample.Approved}/{sample.Teach}");
                if (sample.Teach != 0)
                    Check(HeaderPatch("disable")["enabled"]?.GetValue<bool>() == false &&
                        HeaderPatch("workspace.power")["hint"]?.GetValue<string>()?.Contains("All six motors report enabled", StringComparison.Ordinal) == true,
                        "Teaching hid the distinction between powered motors and blocked plugin controls");
            }
            plugin.preferences = prefs; plugin.link = null; plugin.telemetry.Pika.Release();
            // A real disconnect invalidates the receipt while leaving its last
            // complete snapshot available to the settings projection.
            plugin.telemetry.Disconnect(); plugin.approved = false;
            foreach (string category in new[] { "robot", "diagnostics" }) await CheckView("settings", category);
            var bench = WorkspaceNavigation.Initial.Navigate("calibration");
            var enlarged = bench.Navigate("camera.gripper");
            Check(bench == WorkspaceNavigation.Calibration && enlarged is { Page: WorkspacePage.Camera, CameraReturn: WorkspacePage.Settings, CalibrationOpen: true } &&
                enlarged.Navigate("camera.bench").CameraReturn == WorkspacePage.Settings && enlarged.Navigate("camera-back") is { Page: WorkspacePage.Settings, CalibrationOpen: true } &&
                !bench.Navigate("controls").CalibrationOpen && bench.Navigate("settings.robot").Navigate("settings.controller").Category == SettingsCategory.Robot,
                "Workspace navigation lost the calibration page, a camera's return page, or accepted an unreachable settings category");
            plugin.workspace.Commit(WorkspaceNavigation.Calibration);
            string? heightBlocker = null;
            try { plugin.HeightContext(); }
            catch (InvalidOperationException error) { heightBlocker = error.Message; }
            Check(heightBlocker is not null && !heightBlocker.Contains("Open Calibration", StringComparison.Ordinal),
                "The jaw-height check remained unreachable from the bench calibration page");
            foreach (string camera in new[] { "bench", "depth", "gripper" })
            {
                await CheckView("settings", "cameras", camera);
                await CheckView("camera", camera: camera);
            }

            plugin.workspace.ToggleCameraStreamDetails();
            plugin.simulationCameras = CameraInventory("source-a", "camera-a", "camera-b");
            await CheckView("settings", "cameras");
            string selector = WidgetId("camera.bench.simselect");
            plugin.simulationCameras = CameraInventory("source-a", "camera-b", "camera-a");
            await Click(selector, 1);
            Check(plugin.preferences.SimulationCameras?.GetValueOrDefault("bench") is { CameraId: "camera-a", SceneId: "source-a" },
                "A reordered camera inventory routed a rendered selection to the wrong camera");
            plugin.simulationCameras = CameraInventory("source-a", "camera-b");
            await Click(selector, 1);
            Check(plugin.preferences.SimulationCameras?.GetValueOrDefault("bench")?.CameraId == "camera-a" &&
                plugin.uiActionError.Contains("Simulation cameras changed", StringComparison.Ordinal),
                "A removed camera selection silently rebound the bench preview");
            plugin.simulationCameras = CameraInventory("source-b", "camera-a", "camera-b");
            await Click(selector, 0);
            Check(plugin.preferences.SimulationCameras?.GetValueOrDefault("bench")?.SceneId == "source-a",
                "An Automatic selection from an old scene changed the new scene's binding");

            var installed = new GroupSession(plugin.telemetry.BackendToken, "source-a", "robot-a", "asset-a", "revision-a", [],
                plugin.follow.InputEpoch, TimeProvider.System);
            Check(plugin.follow.TryInstall(installed), "The fixture could not install a Follow session");
            try
            {
                await CheckView("settings", "cameras");
                long before = plugin.workspace.Epoch;
                string back = WidgetId("workspace.controls@settings");
                await Click(WidgetId("workspace.settings.bench"));
                Check(plugin.workspace.Navigation.Category == SettingsCategory.Cameras && plugin.workspace.Epoch == before &&
                    plugin.uiActionError.Contains("Stop following", StringComparison.Ordinal),
                    "Rejected Bench navigation mutated the published camera page");
                await Click(back);
                Check(plugin.workspace.Navigation.Page == WorkspacePage.Controls && plugin.workspace.Epoch > before,
                    "The old page became unclickable after rejected Bench navigation");
            }
            finally { plugin.follow.Retire(installed); }
            await CheckView("recording");
            plugin.workspace.Replay.Load(EpisodeReplayTests.ValidArtifact(), "piper-1");
            await CheckView("replay");
            var replayStart = Walk(rendered!).Single(n => n["id"]?.GetValue<string>()?.EndsWith(".replay.start", StringComparison.Ordinal) == true);
            Check(replayStart["enabled"]?.GetValue<bool>() == false && replayStart["hint"]?.GetValue<string>() == EpisodeReplayAvailability.Reason,
                "Physical replay review offered execution without coordinated Pika streaming/stop support");
            Check(Walk(rendered!).Any(n => n["id"]?.GetValue<string>()?.EndsWith(".replay.timing", StringComparison.Ordinal) == true &&
                n["text"]?.GetValue<string>()?.Contains("acceleration", StringComparison.Ordinal) == true),
                "Replay review hid the requirement for fresh reported acceleration caps");
            foreach (CalibrationWizardStep step in Enum.GetValues<CalibrationWizardStep>())
            {
                plugin.wizardProfile = "piper-1";
                plugin.calibrationWizard = CalibrationWizardFlow.New("piper-1") with { Step = step };
                plugin.calibrationArchiveConfirmation = step == CalibrationWizardStep.InspectSaved;
                await CheckView("settings", "bench");
            }

            // Exercise the actual edit/save/reload path without starting any
            // hardware, camera, backend or editor workers.
            var savedDraft = CalibrationWizardFlow.New("piper-1", 77);
            plugin.calibrationWizard = savedDraft;
            await plugin.WizardEvent("77.sheet.field.p0back", JsonSerializer.SerializeToElement("200"), CancellationToken.None);
            await plugin.WizardEvent("77.sheet.field.p0left", JsonSerializer.SerializeToElement("697"), CancellationToken.None);
            var freshPlugin = new PipersPlugin();
            freshPlugin.preferences = prefs;
            freshPlugin.engine = context;
            freshPlugin.LoadWizard();
            var restoredDraft = freshPlugin.calibrationWizard!;
            Check(restoredDraft.Answers["p0back"] == "200" && restoredDraft.Answers["p0left"] == "697", "Real calibration field events did not survive a fresh plugin session");
            Check(plugin.sheetSaveStatus.StartsWith("Saved locally", StringComparison.Ordinal), "Successful disk save was not reported to the user");
            freshPlugin.channel.Dispose();

            // Browsing settings ends a viewport gesture, preserves a healthy
            // approval and never replays an earlier target on return.
            var state = new JsonObject
            {
                ["token"] = "fixture-session", ["selected"] = "can-a",
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true,
                    ["status"] = new JsonObject { ["age_s"] = .001, ["control"] = 1, ["teach_state"] = 0, ["motion_mode"] = 1, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 },
                    ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
                    {
                        ["actual_deg"] = 0.0, ["fresh"] = true, ["age_s"] = .001, ["enabled"] = true, ["fault"] = 0,
                        ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
                    }).ToArray())
                }), ["motion"] = new JsonObject { ["active"] = true, ["owner"] = plugin.owner, ["request_id"] = 7L }
            };
            var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
            var binding = new RobotLink("fixture-scene", "fixture-robot", model, state["devices"]![0]!.AsObject());
            var calls = new WorkspaceCalls(request =>
            {
                if (request.Route == "/api/command") state["motion"]!["active"] = false;
            });
            plugin.client = new PiperControl.PiperControlClient(calls); plugin.telemetry.PublishState(state); plugin.telemetry.AdoptBackend("fixture-session");
            plugin.telemetry.Publish(new(state, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
            plugin.workspace.Commit(WorkspaceNavigation.Initial); plugin.approved = true; plugin.linkCommandsEnabled = true;
            plugin.recovery.Teaching.ObserveHandOver(new(binding.DeviceId, binding.Generation), approved: true); plugin.follow.Viewport.Follow(7);
            plugin.CommandAuthority.TryAcknowledge(plugin.CommandAuthority.Epoch, new("piper-1", "fixture-session", binding.DeviceId, binding.Generation), 7);
            plugin.link = binding; plugin.linkBackendToken = "fixture-session";
            long previousPermission = plugin.CommandAuthority.Epoch;
            await plugin.WorkspaceEvent("settings.robot", CancellationToken.None);
            Check(calls.Requests.Count == 1 && calls.Requests[0].Route == "/api/command" &&
                JsonNode.Parse(calls.Requests[0].Json)?["action"]?.GetValue<string>() == "follow-end", "Leaving Controls replayed a target or failed to end its gesture");
            Check(plugin.approved && plugin.recovery.Teaching.Current.RestoresApproval && plugin.linkCommandsEnabled && plugin.follow.Viewport.Current.Phase == ViewportDragPhase.Off,
                "Browsing settings revoked readiness or retained a live viewport gesture");
            Check(ReferenceEquals(plugin.link, binding) && plugin.CommandAuthority.Epoch == previousPermission,
                "Browsing settings dropped the measured binding or revoked healthy idle approval");
            Check(engineRequests.Any(r => r.Method == "robot.teleoperate" && r.Arguments["enabled"]?.GetValue<bool>() == false),
                "Navigation left the native viewport teleoperation tool enabled");
            int previousCalls = calls.Requests.Count;
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", "engine.robot.target", "commit", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            Check(calls.Requests.Count == previousCalls && plugin.approved, "A native target bypassed the off-Controls policy or viewing settings cleared approval");
            await plugin.WorkspaceEvent("controls", CancellationToken.None);
            Check(plugin.approved && plugin.linkCommandsEnabled && calls.Requests.Count == previousCalls,
                "Returning to Controls revoked readiness or replayed a previous target");
            await plugin.WorkspaceEvent("replay", CancellationToken.None);
            long replayEpoch = plugin.workspace.Epoch;
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", WorkspaceEventScope.Wrap(replayEpoch, "piper-1", "replay.start"), "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
            Check(calls.Requests.Count == previousCalls && plugin.approved,
                "A forged replay Start event bypassed the explicit execution-unavailable policy");
        }
        finally
        {
            plugin.channel.Dispose();
            Directory.Delete(folder, true);
        }
    }

    private static JsonObject PowerDevice(int enabled) => new()
    {
        ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true,
        ["status"] = new JsonObject { ["age_s"] = .001, ["control"] = 1, ["teach_state"] = 0, ["motion_mode"] = 1, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 },
        ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode)new JsonObject
        {
            ["actual_deg"] = 0.0, ["fresh"] = true, ["age_s"] = .001, ["enabled"] = i < enabled, ["fault"] = 0,
            ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
        }).ToArray())
    };

    private sealed class WorkspaceCalls(Action<ControlRequest> execute) : CallInvoker
    {
        public List<ControlRequest> Requests { get; } = [];
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var command = (ControlRequest)(object)request!; Requests.Add(command); execute(command);
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{\"ok\":true}" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
