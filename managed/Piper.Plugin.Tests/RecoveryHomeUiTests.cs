using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

internal static class RecoveryHomeUiTests
{
    public static async Task Run(Action<bool, string> check)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        string root = Path.Combine(Path.GetTempPath(), "piper-recovery-home-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plugin = new PipersPlugin();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, fields)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, fields)!.GetValue(plugin)!;
        var errors = new ConcurrentQueue<Exception>();
        var calls = new HomeCalls(Get<string>("owner"));
        var engine = (PluginContext)Activator.CreateInstance(typeof(PluginContext), fields, null,
            [new PluginStart("fixture", root, Path.GetTempPath()),
                (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { })))], null)!;
        Set("engine", engine); Set("client", new PiperControl.PiperControlClient(calls)); Set("token", "home-ui-fixture");
        Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "resting-arm")], ActiveRobotId: "piper-1")));
        Set("workspaceViewEpoch", 1L);
        long sequence = 0;
        void Publish()
        {
            JsonObject snapshot = calls.Snapshot();
            typeof(PipersPlugin).GetMethod("Materialize", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [snapshot]);
            Set("state", snapshot);
            Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "home-ui-fixture", Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), 0));
        }
        Publish();
        using var pumping = new CancellationTokenSource();
        Task pump = Task.Run(async () => { while (!pumping.IsCancellationRequested) { Publish(); await Task.Delay(5, pumping.Token); } });
        await using var dispatch = new UiEventDispatch((input, cancellation) =>
                (Task)typeof(PipersPlugin).GetMethod("HandleEventCoreAsync", fields)!.Invoke(plugin, [input, cancellation])!,
            () => Get<long>("targetPermissionEpoch"), _ => true, errors.Enqueue);
        Set("uiEvents", dispatch);
        try
        {
            await plugin.HandleEventAsync(new("fixture", WorkspaceEventScope.Wrap(1, "piper-1", "recovery.home.incident"), "click",
                JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            long started = Stopwatch.GetTimestamp();
            while (typeof(PipersPlugin).GetField("recoveryHomeWorker", fields)!.GetValue(plugin) is null)
            {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new TimeoutException("Recovery UI event did not start its worker: " + string.Join("; ", errors));
                await Task.Delay(5);
            }
            await Get<Task>("recoveryHomeWorker").WaitAsync(TimeSpan.FromSeconds(4));
            check(calls.Actions.SequenceEqual(["enable", "move-to-zero"]),
                "The single real UI-actor healthy/all-off recovery event cancelled itself, reset unnecessarily or failed to finish: " + Get<string>("message"));
            check(errors.IsEmpty && !Get<bool>("recoveryHomeBusy") && Get<bool>("approved"),
                "Successful UI recovery did not finish with usable controls");
            var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "recovery", "piper-1", "last-recovery-to-zero.json")))!;
            check(report["stage"]!.GetValue<string>().StartsWith("Recovery complete") && report["automaticResume"]?.GetValue<bool>() == false &&
                report["goal"]?.GetValue<string>() == "all-joints-zero" && !report.ToJsonString().Contains("home-ui-fixture"),
                "Recovery did not durably save its completed zero goal without a reusable backend token or resume intent");
            check(calls.AllCommandsScoped && calls.SpeedPercents.All(s => s == 10),
                "Recovery UI sent unscoped motor commands or inherited the user's faster motion speed");
        }
        finally
        {
            pumping.Cancel(); try { await pump; } catch (OperationCanceledException) { }
            await dispatch.StopAsync(); Get<CancellationTokenSource>("shutdown").Cancel();
            ((IDisposable)Get<object>("channel")).Dispose(); Get<SemaphoreSlim>("commands").Dispose();
            Get<CancellationTokenSource>("shutdown").Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class HomeCalls(string owner) : CallInvoker
    {
        private readonly object gate = new();
        private long request = 10;
        private bool enabled;
        private string phase = "monitor";
        private double[] angles = [0, -2.064, 2.105, 0, 21.346, 0];
        public List<string> Actions { get; } = [];
        public List<double> SpeedPercents { get; } = [];
        public bool AllCommandsScoped { get; private set; } = true;
        public JsonObject Snapshot()
        {
            lock (gate) return new()
            {
                ["token"] = "home-ui-fixture", ["selected"] = "resting-arm", ["phase"] = phase, ["message"] = "Fixture state",
                ["motion"] = new JsonObject { ["active"] = false, ["request_id"] = request, ["owner"] = "" },
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "resting-arm", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
                    ["firmware_supported"] = true, ["recovery_required"] = false, ["reset_pending"] = false,
                    ["status"] = new JsonObject { ["arm_status"] = 0, ["control"] = 0, ["motion_mode"] = 1,
                        ["teach_state"] = 0, ["error"] = 0, ["stationary_s"] = 1d, ["age_s"] = 0d },
                    ["joints"] = new JsonArray(angles.Select((angle, i) => (JsonNode)new JsonObject
                    {
                        ["actual_deg"] = angle, ["fresh"] = true, ["age_s"] = 0d, ["enabled"] = enabled, ["fault"] = 0,
                        ["limits_fresh"] = true, ["min_deg"] = i == 1 ? 0d : -180d, ["max_deg"] = i == 2 ? 0d : 180d,
                        ["max_speed_rad_s"] = 3d
                    }).ToArray())
                })
            };
        }
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest input)
        {
            var command = input as ControlRequest ?? throw new InvalidOperationException("Unexpected fixture RPC");
            var body = JsonNode.Parse(command.Json)!;
            options.CancellationToken.ThrowIfCancellationRequested();
            string step = body["action"]!.GetValue<string>();
            if (command.Route != "/api/command" || step is not ("reset" or "enable" or "move-to-zero"))
                throw new InvalidOperationException("Recovery sent an unexpected route/action");
            long accepted;
            lock (gate)
            {
                Actions.Add(step); SpeedPercents.Add(body["speed_percent"]!.GetValue<double>());
                AllCommandsScoped &= body["owner"]?.GetValue<string>() == owner && body["device_id"]?.GetValue<string>() == "resting-arm" &&
                    body["generation"]?.GetValue<long>() == 7 && body["supported"]?.GetValue<bool>() == true && body["clear"]?.GetValue<bool>() == true;
                enabled = step != "reset";
                if (step == "move-to-zero") angles = new double[6];
                phase = "complete"; accepted = ++request;
            }
            return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 202,
                Json = new JsonObject { ["request_id"] = accepted, ["message"] = step + " accepted" }.ToJsonString() }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
