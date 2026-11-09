using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static class PikaDisplayReferenceTests
{
    private const double ClosedAngle = -.0354;
    private const string DevicePath = "/dev/pika-fixture";
    private static string Distribution => OperatingSystem.IsWindows() ? "Ubuntu" : "";
    private static JsonObject Pika() => new()
    {
        ["device"] = DevicePath, ["distribution"] = Distribution, ["connected"] = true, ["fresh"] = true,
        ["configured"] = true, ["pending"] = false, ["enabled"] = false, ["armed"] = false,
        ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = ClosedAngle, ["Speed"] = 0.0 } }
    };

    public static async Task Run(Action<bool, string> check)
    {
        var pika = Pika();
        check(!GripperLink.TryMeasuredOpening(pika, out _) && GripperLink.ClosedDisplayReferenceNeeded(pika),
            "Observed -0.0354 rad was silently treated as small closed-stop noise");
        var reference = PikaClosedDisplayReference.Capture("piper-1", DevicePath, Distribution, pika, false);
        check(reference.Valid && reference.MotorAngle == ClosedAngle && reference.Matches("piper-1", DevicePath, Distribution, pika),
            "Explicit stationary closed display reference did not bind its profile/device/distro");
        check(GripperLink.TryMeasuredOpening(pika, out double opening, reference.MotorAngle) && opening == 0,
            "Confirmed closed encoder reference did not map the scene/readout to zero");
        var restored = JsonSerializer.Deserialize<PikaClosedDisplayReference>(JsonSerializer.Serialize(reference, PluginProtocol.Json), PluginProtocol.Json)!;
        check(restored == reference, "Closed display reference and receipt time did not survive JSON persistence");
        foreach (var mismatch in new[] { ("other", DevicePath, Distribution), ("piper-1", "/dev/other", Distribution), ("piper-1", DevicePath, "OtherDistro") })
            check(!restored.Matches(mismatch.Item1, mismatch.Item2, mismatch.Item3, pika), "A display reference leaked across profile/device/distro identity");
        foreach (string field in new[] { "fresh", "connected" })
        {
            var invalid = (JsonObject)pika.DeepClone(); invalid[field] = false;
            check(PikaClosedDisplayReference.Blocker("piper-1", DevicePath, Distribution, invalid, false).Length > 0,
                "A disconnected/stale gripper permitted setting a new closed display reference");
        }
        check(!restored.Matches("piper-1", DevicePath, Distribution, new JsonObject { ["device"] = DevicePath, ["distribution"] = Distribution, ["connected"] = false }),
            "A disconnected gripper used its saved display reference");
        var disconnected = Pika(); disconnected["connected"] = false;
        check(!GripperLink.TryMeasuredOpening(disconnected, out _, ClosedAngle), "A disconnected state with a retained fresh flag moved scene fingers");
        check(PikaClosedDisplayReference.Blocker("piper-1", DevicePath, Distribution, pika, true).Contains("stationary"),
            "A moving arm permitted capturing a closed display reference");
        foreach (string condition in new[] { "pending", "moving", "far" })
        {
            var invalid = (JsonObject)pika.DeepClone();
            if (condition == "pending") invalid["pending"] = true;
            if (condition == "moving") invalid["telemetry"]!["motor"]!["Speed"] = .1;
            if (condition == "far") invalid["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(20);
            check(PikaClosedDisplayReference.Blocker("piper-1", DevicePath, Distribution, invalid, false).Length > 0,
                "Pending, moving or far-open gripper permitted redefining its closed reference");
        }
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 1.0 })
            check(!GripperLink.TryMeasuredOpening(pika, out _, invalid), "An invalid reference angle was accepted");
        foreach (double width in new[] { 0.0, 20.0, 45.0, 90.0 })
        {
            pika["telemetry"]!["motor"]!["Position"] = ClosedAngle + PikaProtocol.Angle(width);
            check(GripperLink.TryMeasuredOpening(pika, out opening, ClosedAngle) && Math.Abs(opening - width) < 1e-7,
                "Display reference did not normalize subsequent encoder readings consistently");
        }
        foreach (double width in new[] { 90.0858, 90.249, 90.2501 })
        {
            pika["telemetry"]!["motor"]!["Position"] = ClosedAngle + MeasuredAngle(width);
            var raw = pika.DeepClone();
            bool accepted = GripperLink.TryMeasuredOpening(pika, out opening, ClosedAngle);
            check(width <= 90.25 ? accepted && opening == 90 : !accepted,
                "Full-open noise allowance was not applied after the saved closed reference, or expanded beyond its bound");
            check(JsonNode.DeepEquals(pika, raw), "Reference-normalized endpoint presentation changed raw Pika telemetry");
        }
        pika["fresh"] = false;
        check(!GripperLink.TryMeasuredOpening(pika, out _, ClosedAngle), "Display reference promoted stale data into a live reading");
        await RunRendered(check);
    }

    private static double MeasuredAngle(double width)
    {
        double low = PikaProtocol.Angle(89), high = PikaProtocol.Angle(90) + .2;
        for (int i = 0; i < 60; i++)
        {
            double middle = (low + high) * .5;
            if (PikaProtocol.Width(middle) < width) low = middle; else high = middle;
        }
        return low;
    }

    private static async Task RunRendered(Action<bool, string> check)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, hidden)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, hidden)!.GetValue(plugin)!;
        Task Invoke(string name, params object[] args) => (Task)typeof(PipersPlugin).GetMethod(name, hidden)!.Invoke(plugin, args)!;
        string folder = Path.Combine(Path.GetTempPath(), "piper-display-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a", RobotId: "fixture-robot", PikaDevice: DevicePath, WslDistribution: Distribution)], ActiveRobotId: "piper-1"));
        Set("preferences", prefs); Set("token", "fixture-session"); Set("pikaMonitoring", true);
        var model = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad" }).ToArray());
        const double travel = .0470221912391186;
        model.Add(new JsonObject { ["name"] = "joint7", ["unit"] = "m", ["limited"] = true, ["min"] = -travel, ["max"] = 0 });
        model.Add(new JsonObject { ["name"] = "joint8", ["unit"] = "m", ["limited"] = true, ["min"] = 0, ["max"] = travel });
        var device = new JsonObject
        {
            ["id"] = "can-a", ["name"] = "Fixture", ["generation"] = 1L, ["fresh"] = true, ["present"] = true, ["up"] = true,
            ["firmware_supported"] = true, ["status"] = new JsonObject { ["age_s"] = .001, ["control"] = 1, ["teach_state"] = 0, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["actual_deg"] = 0.0, ["fresh"] = true, ["age_s"] = .001, ["fault"] = 0, ["enabled"] = false, ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3 }).ToArray())
        };
        var pika = Pika();
        var state = new JsonObject { ["token"] = "fixture-session", ["selected"] = "can-a", ["devices"] = new JsonArray(device), ["pika"] = pika, ["motion"] = new JsonObject { ["active"] = false } };
        Set("state", state);
        void Fresh() => Get<LatestTelemetry>("telemetry").Publish(new(state, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
        var engineCalls = new List<string>(); var feedback = new List<JsonObject>();
        var rpc = new FakeCalls(); Set("client", new PiperControl.PiperControlClient(rpc));
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), hidden, null,
            [new PluginStart("fixture", folder, folder), (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((method, args, _) =>
                {
                    engineCalls.Add(method); Fresh();
                    JsonObject reply;
                    if (method == "scene.robots") reply = new() { ["sceneId"] = "fixture-scene", ["robots"] = new JsonArray(new JsonObject { ["entityId"] = "fixture-robot", ["name"] = "Fixture", ["joints"] = model.DeepClone() }) };
                    else if (method == "robot.feedback")
                    {
                        feedback.Add((JsonObject)args.DeepClone());
                        reply = new() { ["applied"] = true, ["pose"] = new JsonObject { ["sceneId"] = "fixture-scene", ["entityId"] = "fixture-robot", ["joints"] = new JsonArray(args["joints"]!.AsObject().Select(j => (JsonNode)new JsonObject { ["name"] = j.Key, ["position"] = j.Value!.DeepClone() }).ToArray()) } };
                    }
                    else if (method == "robot.subscribe") reply = new() { ["sceneId"] = "fixture-scene", ["entityId"] = "fixture-robot", ["joints"] = model.DeepClone() };
                    else throw new InvalidOperationException("Unexpected display-reference engine request: " + method);
                    return Task.FromResult(JsonSerializer.SerializeToElement(reply));
                })], null)!;
        Set("engine", context);
        async Task Event(string id, double? value = null)
        {
            Fresh();
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", WorkspaceEventScope.Wrap(Get<long>("workspaceViewEpoch"), "piper-1", id), "click", JsonSerializer.SerializeToElement(value)), CancellationToken.None);
        }
        try
        {
            await Invoke("Render", CancellationToken.None); await Event("pika.reference.set");
            string path = Path.Combine(folder, "gripper-reference", "piper-1.json");
            check(File.Exists(path) && JsonSerializer.Deserialize<PikaClosedDisplayReference>(File.ReadAllText(path), PluginProtocol.Json)?.MotorAngle == ClosedAngle,
                "Actual display-reference action did not atomically persist the observed closed position");
            Set("pikaDisplayReferenceKey", ""); Set("pikaDisplayReference", null);
            await Event("link");
            check(feedback.Count == 1 && feedback[0]["joints"]?["joint7"]?.GetValue<double>() == -travel && feedback[0]["joints"]?["joint8"]?.GetValue<double>() == travel,
                "Initial link failed to restore the saved reference and send both closed finger positions");
            check(Get<RobotLink>("link").GripperErrorMm == 0, "Initial scene link did not verify both gripper acknowledgements");
            pika["telemetry"]!["motor"]!["Position"] = ClosedAngle + PikaProtocol.Angle(20); Fresh();
            await Invoke("PublishMeasuredScene", CancellationToken.None);
            var updated = feedback[^1]["joints"]!;
            double gap = 2000 * travel + 1000 * (updated["joint7"]!.GetValue<double>() - updated["joint8"]!.GetValue<double>());
            check(Math.Abs(gap - 20) < 1e-7 && Get<RobotLink>("link").GripperErrorMm == 0,
                "Continuing telemetry omitted the saved display reference or failed to verify actual finger geometry");
            pika["telemetry"]!["motor"]!["Position"] = ClosedAngle + MeasuredAngle(90.0858); Fresh();
            await Invoke("PublishMeasuredScene", CancellationToken.None);
            updated = feedback[^1]["joints"]!;
            double openGap = 2000 * travel + 1000 * (updated["joint7"]!.GetValue<double>() - updated["joint8"]!.GetValue<double>());
            check(Math.Abs(openGap - 90) < 1e-7 && Get<RobotLink>("link").GripperErrorMm == 0,
                "Observed full-open endpoint noise left the reference-normalized scene gripper unsynchronized");
            pika["telemetry"]!["motor"]!["Position"] = ClosedAngle; Fresh();
            await Invoke("PublishMeasuredScene", CancellationToken.None);
            check(feedback[^1]["joints"]!["joint7"]!.GetValue<double>() == -travel, "Continuing closed feedback left the scene gripper open");
            pika["device"] = "/dev/other"; Fresh(); await Invoke("PublishMeasuredScene", CancellationToken.None);
            check(feedback[^1]["joints"]!.AsObject().Count == 6, "A saved reference was applied to another gripper assignment");
            pika["device"] = DevicePath; pika["fresh"] = false; Fresh(); await Invoke("PublishMeasuredScene", CancellationToken.None);
            check(feedback[^1]["joints"]!.AsObject().Count == 6, "Stale feedback moved the scene fingers using a saved reference");
            pika["fresh"] = true; pika["armed"] = true; pika["enabled"] = true; pika["owner"] = Get<string>("owner"); Set("pikaOwned", true);
            await Event("pika.target", 40);
            check(rpc.Requests.Count == 0, "A held gripper could accept a manual target after robot approval was revoked");
            Set("approved", true);
            device["recovery_required"] = true;
            await Event("pika.plus");
            check(rpc.Requests.Count == 0, "A gripper target bypassed arm recovery");
            device["recovery_required"] = false;
            pika["pending"] = true; await Event("pika.plus");
            pika["pending"] = false; pika["enabled"] = false; await Event("pika.zero");
            check(rpc.Requests.Count == 0, "A queued gripper target bypassed the pending-move or unpowered-jaws blocker shown by the panel");
            pika["enabled"] = true;
            await Event("pika.plus"); await Event("pika.target", 40);
            check(rpc.Requests.Count == 2 && rpc.Requests.All(r => r.Route == "/api/pika/position") &&
                Math.Abs(JsonNode.Parse(rpc.Requests[0].Json)!["value"]!.GetValue<double>() - PikaProtocol.Angle(1)) < 1e-12 &&
                Math.Abs(JsonNode.Parse(rpc.Requests[1].Json)!["value"]!.GetValue<double>() - PikaProtocol.Angle(40)) < 1e-12,
                "Confirmed closed display reference kept normal controls blocked or changed the vendor outgoing target-angle formula");
            check(engineCalls.All(m => m is "scene.robots" or "robot.feedback" or "robot.subscribe"), "Setting a display reference requested engine teleoperation, reset, enable or target motion");
            await Event("pika.reference.clear");
            check(!File.Exists(path) && Get<PikaClosedDisplayReference?>("pikaDisplayReference") is null,
                "Clear display reference did not remove its persisted correction");
        }
        finally { Get<Grpc.Net.Client.GrpcChannel>("channel").Dispose(); Directory.Delete(folder, true); }
    }

    private sealed class FakeCalls : CallInvoker
    {
        public List<ControlRequest> Requests { get; } = [];
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (request is not ControlRequest command) throw new InvalidOperationException("Unexpected fixture RPC");
            Requests.Add(command);
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{}" }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
