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
    public static async Task Run()
    {
        await RunRenderedRecovery(cancelMove: false);
        await RunRenderedRecovery(cancelMove: true);
        await RunRenderedRecovery(cancelMove: false, resetPending: true);
    }

    private static async Task RunRenderedRecovery(bool cancelMove, bool resetPending = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "piper-recovery-home-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plugin = new PipersPlugin();
        var errors = new ConcurrentQueue<Exception>();
        var calls = new HomeCalls(plugin.owner, cancelMove) { ResetPending = resetPending };
        JsonObject? rendered = null, patches = null;
        var engine = (PluginContext)Activator.CreateInstance(typeof(PluginContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new PluginStart("fixture", root, Path.GetTempPath()),
                (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!.AsObject();
                    if (json["type"]?.GetValue<string>() == "view") rendered = json["root"]!.AsObject();
                    if (json["type"]?.GetValue<string>() == "patch") patches = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { })))], null)!;
        plugin.engine = engine; plugin.client = new PiperControl.PiperControlClient(calls); plugin.token = "home-ui-fixture";
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "resting-arm")], ActiveRobotId: "piper-1"));
        static IEnumerable<JsonObject> Walk(JsonObject node)
        {
            yield return node;
            foreach (var child in node["children"]?.AsArray().OfType<JsonObject>() ?? [])
                foreach (var nested in Walk(child)) yield return nested;
        }
        long sequence = 0;
        void Publish()
        {
            JsonObject snapshot = calls.Snapshot();
            PipersPlugin.Materialize(snapshot);
            plugin.state = snapshot;
            plugin.telemetry.Publish(new(snapshot, "home-ui-fixture", Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), 0));
        }
        Publish();
        using var pumping = new CancellationTokenSource();
        Task pump = Task.CompletedTask;
        await using var dispatch = new UiEventDispatch((input, cancellation) =>
                plugin.HandleEventCoreAsync(input, cancellation),
            () => plugin.CommandAuthority.Epoch, errors.Enqueue);
        plugin.uiEvents = dispatch;
        try
        {
            await plugin.Render(CancellationToken.None);
            await plugin.Update(CancellationToken.None);
            string card = Walk(rendered!).Single(node => node["id"]!.GetValue<string>().EndsWith(".workspace.incident", StringComparison.Ordinal))["id"]!.GetValue<string>();
            string button = Walk(rendered!).Single(node => node["id"]!.GetValue<string>().EndsWith(".recovery.home", StringComparison.Ordinal))["id"]!.GetValue<string>();
            string cancel = Walk(rendered!).Single(node => node["id"]!.GetValue<string>().EndsWith(".recovery.guide.cancel", StringComparison.Ordinal))["id"]!.GetValue<string>();
            Check(patches?[card]?["visible"]?.GetValue<bool>() == true && patches[button]?["enabled"]?.GetValue<bool>() == true,
                "The published recovery card did not expose its real recover-to-zero action");
            pump = Task.Run(async () => { while (!pumping.IsCancellationRequested) { Publish(); await Task.Delay(5, pumping.Token); } });
            await plugin.HandleEventAsync(new("fixture", button, "click",
                JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            long started = Stopwatch.GetTimestamp();
            Task? recoveryWorker;
            while ((recoveryWorker = plugin.recoveryOperation?.Worker) is null)
            {
                // A run that stops at the reset question may retire before this poll.
                if (resetPending && plugin.resetCompletionPrompt is not null) { recoveryWorker = Task.CompletedTask; break; }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new TimeoutException("Recovery UI event did not start its worker: " + string.Join("; ", errors));
                await Task.Delay(5);
            }
            if (cancelMove)
            {
                while (plugin.telemetry.Latest?.State["motion"]?["active"]?.GetValue<bool>() != true)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 3) throw new TimeoutException("Recovery did not report its in-flight zero move.");
                    await Task.Delay(5);
                }
                await plugin.HandleEventAsync(new("fixture", cancel, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
            }
            await recoveryWorker.WaitAsync(TimeSpan.FromSeconds(4));
            if (resetPending)
            {
                // B1: recovery to zero stops at the operator's reset question.
                string question = Walk(rendered!).Single(node => node["id"]!.GetValue<string>().EndsWith(".recovery.reset", StringComparison.Ordinal))["id"]!.GetValue<string>();
                string confirm = Walk(rendered!).Single(node => node["id"]!.GetValue<string>().EndsWith(".recovery.reset.confirm", StringComparison.Ordinal))["id"]!.GetValue<string>();
                Check(calls.Actions.Count == 0 && plugin.resetCompletionPrompt is not null && plugin.recoveryOperation is null &&
                    patches?[question]?["visible"]?.GetValue<bool>() == true,
                    "Recovery to zero verified a pending reset instead of stopping at the operator's confirmation: " + plugin.message);
                await plugin.HandleEventAsync(new("fixture", confirm, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
                started = Stopwatch.GetTimestamp();
                while (calls.Actions.Count == 0 && Stopwatch.GetElapsedTime(started).TotalSeconds < 3) await Task.Delay(5);
                await plugin.Update(CancellationToken.None);
                Check(calls.Actions.SequenceEqual(["verify-reset"]) && calls.ResetCompletedAttestations == 1 && plugin.resetCompletionPrompt is null &&
                    patches?[question]?["visible"]?.GetValue<bool>() == false,
                    "Confirming the reset question did not send exactly one attested verify-reset: " + string.Join("; ", errors));
                return;
            }
            Check(calls.Actions.SequenceEqual(["enable", "move-to-zero"]),
                "The single real UI-actor healthy/all-off recovery event cancelled itself, reset unnecessarily or failed to finish: " + plugin.message);
            Check(errors.IsEmpty && plugin.recoveryOperation is null && plugin.approved == !cancelMove,
                "Retired UI recovery published incorrect approval");
            if (cancelMove)
                Check(calls.ScopedStopRequest == 12 && plugin.CommandAuthority.Current.Lease is null,
                    "Cancel failed to stop the exact active zero request or kept its old heartbeat lease");
            var report = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "recovery", "piper-1", "last-recovery-to-zero.json")))!;
            Check(report["stage"]!.GetValue<string>().StartsWith(cancelMove ? "Recovery to zero cancelled" : "Recovery complete") &&
                report["automaticResume"]?.GetValue<bool>() == false &&
                report["goal"]?.GetValue<string>() == "all-joints-zero" && !report.ToJsonString().Contains("home-ui-fixture"),
                "Recovery did not durably save its completed zero goal without a reusable backend token or resume intent");
            Check(calls.AllCommandsScoped && calls.SpeedPercents.All(s => s == 10),
                "Recovery UI sent unscoped motor commands or inherited the user's faster motion speed");
        }
        finally
        {
            pumping.Cancel(); try { await pump; } catch (OperationCanceledException) { }
            await dispatch.StopAsync(); plugin.shutdown.Cancel();
            plugin.channel.Dispose(); plugin.commands.Dispose();
            plugin.shutdown.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class HomeCalls(string owner, bool leaveMoveActive) : CallInvoker
    {
        private readonly object gate = new();
        private long request = 10;
        private bool enabled;
        private bool moving;
        private string phase = "monitor";
        private double[] angles = [0, -2.064, 2.105, 0, 21.346, 0];
        public List<string> Actions { get; } = [];
        public List<double> SpeedPercents { get; } = [];
        public bool AllCommandsScoped { get; private set; } = true;
        public bool ResetPending;
        public int ResetCompletedAttestations { get; private set; }
        public long ScopedStopRequest { get; private set; }
        public JsonObject Snapshot()
        {
            lock (gate) return new()
            {
                ["token"] = "home-ui-fixture", ["selected"] = "resting-arm", ["phase"] = phase, ["message"] = "Fixture state",
                ["motion"] = new JsonObject { ["active"] = moving, ["request_id"] = request, ["owner"] = moving ? owner : "" },
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "resting-arm", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
                    ["firmware_supported"] = true, ["recovery_required"] = ResetPending, ["reset_pending"] = ResetPending,
                    ["range_recovery"] = new JsonObject { ["required"] = angles[1] < 0 || angles[2] > 0 },
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
            if (command.Route == "/api/stop")
            {
                lock (gate)
                {
                    if (moving && body["request_id"]?.GetValue<long>() == request &&
                        body["device_id"]?.GetValue<string>() == "resting-arm" && body["generation"]?.GetValue<long>() == 7 &&
                        body["owner"]?.GetValue<string>() == owner)
                    {
                        ScopedStopRequest = request;
                        moving = false;
                    }
                }
                return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{}" }),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            }
            string step = body["action"]!.GetValue<string>();
            if (command.Route == "/api/command" && step == "verify-reset")
            {
                lock (gate)
                {
                    Actions.Add(step);
                    if (body["reset_completed"]?.GetValue<bool>() == true) ++ResetCompletedAttestations;
                    ResetPending = false;
                }
                return new(Task.FromResult((TResponse)(object)new ControlReply { Status = 200, Json = "{\"message\":\"verified\"}" }),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            }
            if (command.Route != "/api/command" || step is not ("reset" or "enable" or "move-to-zero"))
                throw new InvalidOperationException("Recovery sent an unexpected route/action");
            long accepted;
            lock (gate)
            {
                Actions.Add(step); SpeedPercents.Add(body["speed_percent"]!.GetValue<double>());
                AllCommandsScoped &= body["owner"]?.GetValue<string>() == owner && body["device_id"]?.GetValue<string>() == "resting-arm" &&
                    body["generation"]?.GetValue<long>() == 7 && body["clear"]?.GetValue<bool>() == true &&
                    (body["supported"]?.GetValue<bool>() == true) == (step is "reset" or "enable") && body["reset_completed"] is null;
                enabled = step != "reset";
                moving = step == "move-to-zero" && leaveMoveActive;
                if (step == "move-to-zero" && !moving) angles = new double[6];
                phase = moving ? "moving" : "complete"; accepted = ++request;
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
