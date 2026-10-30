using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class WorkspaceViewTests
{
    public static void Run(Action<bool, string> check)
    {
        const long epoch = 12;
        const string profile = "piper-1";
        foreach (string id in new[] { "joint.6.target", "enable", "safety.accept", "pika.target", "calibration.capture", "calibration.width", "speed", "camera.gripper.settings" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            check(wire == "view.12.robot.piper-1." + id, "Action was not scoped to its rendered view and robot: " + id);
            check(WorkspaceEventScope.Unwrap(epoch, profile, wire) == id, "A current rendered event was rejected: " + id);
            check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire).Length == 0, "A previous page event survived navigation: " + id);
            check(WorkspaceEventScope.Unwrap(epoch, "piper-2", wire).Length == 0, "A rendered event crossed robot profiles: " + id);
            check(WorkspaceEventScope.Unwrap(epoch, profile, RobotProfileRouting.ScopeEvent(profile, id)).Length == 0,
                "A legacy event without a rendered-view epoch remained actionable: " + id);
        }

        foreach (string id in new[] { "profiles.select.piper-2", "profiles.add", "camera.bench.settings", "camera.bench.stop", "backend.connect" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            check(wire == "view.12." + id && WorkspaceEventScope.Unwrap(epoch, profile, wire) == id,
                "A shared workspace event lost its view scope: " + id);
            check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire).Length == 0,
                "An old shared bench/navigation event survived a new view: " + id);
            check(WorkspaceEventScope.Unwrap(epoch, profile, id).Length == 0,
                "A shared event bypassed the rendered-view scope: " + id);
        }

        foreach (string id in new[] { "stop", "teaching.reset.abort", "motion.hold" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            check(wire == "robot.piper-1." + id, "Persistent hold/stop gained a page epoch: " + id);
            check(WorkspaceEventScope.Unwrap(epoch + 1, profile, wire) == id,
                "Navigation prevented stopping the same physical robot: " + id);
            check(WorkspaceEventScope.Unwrap(epoch + 1, "piper-2", wire).Length == 0,
                "Persistent hold/stop escaped its physical robot profile: " + id);
        }

        foreach (string id in new[] { "engine.robot.target", "engine.robot.follow" })
        {
            string wire = WorkspaceEventScope.Wrap(epoch, profile, id);
            check(wire == id && WorkspaceEventScope.Unwrap(epoch + 1, profile, wire) == id,
                "A native engine event incorrectly required a UI-render epoch: " + id);
            check(WorkspaceCommandPolicy.RequiresControls(id), "A native target could bypass the controls-page policy: " + id);
        }

        foreach (string wire in new[]
        {
            "", "joint.6.target", "robot.piper-1.joint.6.target", "view.12.joint.6.target", "view.120.robot.piper-1.joint.6.target",
            "view.12x.robot.piper-1.joint.6.target", "view.-12.robot.piper-1.joint.6.target", "view..robot.piper-1.joint.6.target",
            "view.9223372036854775808.robot.piper-1.joint.6.target", "view.12.robot.piper-11.joint.6.target",
            "view.12.robot.piper-2.joint.6.target", "view.12.view.12.robot.piper-1.joint.6.target", "view.12."
        })
            check(WorkspaceEventScope.Unwrap(epoch, profile, wire).Length == 0, "Malformed or foreign event was interpreted as current: " + wire);

        foreach (string id in new[]
        {
            "enable", "safety.accept", "teleoperate.toggle", "teleoperate.joint.6", "joint.1.minus", "joint.3.target", "joint.6.zero", "zero.all", "pika.target"
        })
            check(WorkspaceCommandPolicy.RequiresControls(id), "A motion/enable action remained valid outside robot controls: " + id);

        foreach (string id in new[]
        {
            "stop", "motion.hold", "teaching.reset.abort", "disable", "recovery.run", "calibration.capture", "calibration.inspect", "camera.bench.settings", "profiles.select.piper-2"
        })
            check(!WorkspaceCommandPolicy.RequiresControls(id), "A stop, passive setup or explicit recovery action required the controls page: " + id);
    }

    public static async Task RunRendered(Action<bool, string> check)
    {
        // The real renderer/event handler is exercised without StartAsync: no
        // background workers, sockets, camera captures or device commands run.
        var plugin = new PipersPlugin();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, flags)!.GetValue(plugin)!;
        Task Invoke(string name, params object[] args) => (Task)typeof(PipersPlugin).GetMethod(name, flags)!.Invoke(plugin, args)!;
        string folder = Path.Combine(Path.GetTempPath(), "piper-workspace-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1"));
        Set("preferences", prefs); Set("calibrationProfile", "piper-1");
        JsonObject? rendered = null, patched = null;
        var engineRequests = new List<(string Method, JsonObject Arguments)>();
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), flags, null,
            [new PluginStart("fixture", folder, folder),
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
                })], null)!;
        Set("engine", context);
        IEnumerable<JsonObject> Walk(JsonObject node)
        {
            yield return node;
            foreach (var child in node["children"]?.AsArray().OfType<JsonObject>() ?? [])
                foreach (var descendant in Walk(child)) yield return descendant;
        }
        int Depth(JsonObject node) => 1 + (node["children"]?.AsArray().OfType<JsonObject>().Select(Depth).DefaultIfEmpty(0).Max() ?? 0);
        async Task CheckView(string page, string category = "robot", string camera = "bench")
        {
            Set("workspacePage", page); Set("settingsCategory", category); Set("settingsCamera", camera); Set("cameraDetail", camera);
            Set("calibrationOpen", page == "calibration");
            await Invoke("Render", CancellationToken.None);
            var nodes = Walk(rendered!).ToArray();
            string[] ids = nodes.Select(n => n["id"]!.GetValue<string>()).ToArray();
            string label = page + "/" + category + "/" + camera;
            check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "Duplicate widget IDs in rendered " + label);
            check(ids.Length <= 2048 && Depth(rendered!) <= 24, "Rendered view exceeds public SDK limits: " + label);
            check(ids.Count(id => id == "robot.piper-1.stop") == 1, "Persistent profile-bound STOP is missing/duplicated: " + label);
            long current = Get<long>("workspaceViewEpoch");
            check(ids.All(id => WorkspaceEventScope.Unwrap(current, "piper-1", id).Length > 0), "Rendered widget escaped its current view/profile scope: " + label);
            await Invoke("Update", CancellationToken.None);
            check(Get<long>("workspaceViewEpoch") == current && patched!.All(p => WorkspaceEventScope.Unwrap(current, "piper-1", p.Key).Length > 0),
                "Telemetry update used an old view epoch or rebuilt the page: " + label);
            if (page != "controls")
                check(patched![WorkspaceEventScope.Wrap(current, "piper-1", "enable")]?["enabled"]?.GetValue<bool>() == false,
                    "A setup page re-enabled hardware controls through its common telemetry patch: " + label);
        }
        try
        {
            await CheckView("controls");
            foreach (string category in new[] { "robot", "controller", "diagnostics" }) await CheckView("settings", category);
            foreach (string camera in new[] { "bench", "depth", "gripper" })
            {
                await CheckView("settings", "cameras", camera);
                await CheckView("camera", camera: camera);
            }
            await CheckView("recording");
            foreach (CalibrationWizardStep step in Enum.GetValues<CalibrationWizardStep>())
            {
                Set("wizardProfile", "piper-1");
                Set("calibrationWizard", CalibrationWizardFlow.New("piper-1") with { Step = step });
                Set("calibrationArchiveConfirmation", step == CalibrationWizardStep.InspectSaved);
                await CheckView("calibration");
            }

            // Navigation must leave measured tracking intact but discard a live
            // target gesture and its approval before its controls disappear.
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
                }), ["motion"] = new JsonObject { ["active"] = true, ["owner"] = Get<string>("owner"), ["request_id"] = 7L }
            };
            var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
            var binding = new RobotLink("fixture-scene", "fixture-robot", model, state["devices"]![0]!.AsObject());
            var calls = new WorkspaceCalls(request =>
            {
                if (request.Route == "/api/command") state["motion"]!["active"] = false;
            });
            Set("client", new PiperControl.PiperControlClient(calls)); Set("state", state); Set("token", "fixture-session");
            Get<LatestTelemetry>("telemetry").Publish(new(state, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
            Set("workspacePage", "controls"); Set("calibrationOpen", false); Set("approved", true); Set("linkCommandsEnabled", true);
            Set("approvalBeforeTeaching", true); Set("teleoperating", true); Set("followRequest", 7L); Set("lease", 7L);
            Set("link", binding); Set("linkBackendToken", "fixture-session");
            long previousPermission = Get<long>("targetPermissionEpoch");
            await Invoke("WorkspaceEvent", "settings.robot", CancellationToken.None);
            check(calls.Requests.Count == 1 && calls.Requests[0].Route == "/api/command" &&
                JsonNode.Parse(calls.Requests[0].Json)?["action"]?.GetValue<string>() == "follow-end", "Leaving Controls replayed a target or failed to end its gesture");
            check(!Get<bool>("approved") && !Get<bool>("approvalBeforeTeaching") && !Get<bool>("linkCommandsEnabled") && !Get<bool>("teleoperating") && Get<long>("followRequest") == 0,
                "Page navigation preserved target approval or a live gesture");
            check(ReferenceEquals(Get<RobotLink>("link"), binding) && Get<long>("targetPermissionEpoch") > previousPermission,
                "Page navigation dropped the measured binding or retained its old command epoch");
            check(engineRequests.Any(r => r.Method == "robot.teleoperate" && r.Arguments["enabled"]?.GetValue<bool>() == false),
                "Navigation left the native viewport teleoperation tool enabled");
            int previousCalls = calls.Requests.Count;
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", "engine.robot.target", "commit", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            check(calls.Requests.Count == previousCalls && !Get<bool>("approved"), "A native target bypassed the off-Controls policy");
            await Invoke("WorkspaceEvent", "controls", CancellationToken.None);
            check(!Get<bool>("approved") && !Get<bool>("linkCommandsEnabled") && calls.Requests.Count == previousCalls,
                "Returning to Controls re-enabled or replayed a previous target");
        }
        finally
        {
            Get<Grpc.Net.Client.GrpcChannel>("channel").Dispose();
            Directory.Delete(folder);
        }
    }

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
