using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Collections.Concurrent;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class TelemetryConnectionPresentationTests
{
    public static async Task RunNavigation()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string field, object value) => typeof(PipersPlugin).GetField(field, flags)!.SetValue(plugin, value);
        T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, flags)!.GetValue(plugin)!;
        Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Piper 1", "can-a"), new("piper-2", "Piper 2", "can-b")], ActiveRobotId: "piper-2")));
        Set("state", new JsonObject { ["selected"] = "can-b", ["devices"] = new JsonArray(new JsonObject { ["id"] = "can-b", ["fresh"] = false }) });
        UiEvent Input(string id) => new("fixture", WorkspaceEventScope.Wrap(0, "piper-2", id), "click", JsonSerializer.SerializeToElement(true));
        bool Permission(UiEvent input) => PipersPlugin.ControlEventNeedsPermission(plugin.UnscopePublishedWorkspaceEvent(input.WidgetId));
        UiEvent navigation = Input("profiles.select.piper-1"), target = Input("joint.6.plus");
        Check(!Permission(navigation) && Permission(target), "Profile navigation inherited the stale arm's movement-permission epoch or joint targets lost their guard");
        Check(Permission(Input("profiles.can")) && Permission(Input("profiles.pika")), "Navigation exception also exempted hardware assignment changes");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>(); var discarded = new ConcurrentQueue<string>();
        await using (var dispatch = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "block") { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); return; }
            executed.Enqueue(input.WidgetId);
            if (input.WidgetId == navigation.WidgetId) navigated.TrySetResult();
        }, () => plugin.CommandAuthority.Epoch, error => throw new InvalidOperationException("Unexpected navigation fixture error", error),
            discarded: (input, _) => discarded.Enqueue(input.WidgetId)))
        {
            dispatch.Enqueue(new("fixture", "block", "click", default), 0, false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            dispatch.Enqueue(navigation, 0, Permission(navigation)); dispatch.Enqueue(target, 0, Permission(target));
            // Simulate the heartbeat's next disarm while CAN feedback stays stale.
            plugin.CommandAuthority.Revoke(); release.TrySetResult();
            await navigated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            long began = Stopwatch.GetTimestamp();
            while (discarded.IsEmpty)
            {
                if (Stopwatch.GetElapsedTime(began).TotalSeconds > 2) throw new InvalidOperationException("Stale queued target was not retired");
                await Task.Delay(5);
            }
            Check(executed.SequenceEqual([navigation.WidgetId]) && discarded.SequenceEqual([target.WidgetId]),
                "CAN feedback loss discarded robot navigation or allowed an old queued movement to execute");
            Check(!Get<bool>("approved") && plugin.CommandAuthority.Current is { Lease: null, Epoch: 1 },
                "Navigation eligibility restored power approval, an old lease or movement permission");
        }
        ((IDisposable)Get<object>("channel")).Dispose(); Get<SemaphoreSlim>("commands").Dispose(); Get<CancellationTokenSource>("shutdown").Dispose();
    }

    public static void Run()
    {
        using var fixture = new Fixture();
        fixture.Observe();
        Check(fixture.Message.StartsWith("Connected", StringComparison.Ordinal) && fixture.Error.Length == 0,
            "Fresh selected-device feedback retained the previous stream-ended footer");
        fixture.ResetMessage(); fixture.Stale = true;
        fixture.Device["connection_error"] = "Windows USB-CAN send failed (transport 14, WinUSB 121).";
        fixture.Observe();
        Check(fixture.Message.Contains("WinUSB 121", StringComparison.Ordinal) && fixture.Error.Contains("Piper 2", StringComparison.Ordinal) &&
            fixture.Error.Contains("WinUSB 121", StringComparison.Ordinal) && fixture.Error.Contains("Reconnect only this adapter’s USB cable", StringComparison.Ordinal) &&
            fixture.Error.Contains("leave robot power and CAN connected", StringComparison.Ordinal),
            "Stale CAN feedback was called connected or hid its specific USB-only recovery instruction and native transport failure");
        fixture.Device["connection_error"] = ""; fixture.Observe();
        Check(!fixture.Error.Contains("Reconnect only", StringComparison.Ordinal), "Ordinary stale feedback incorrectly told the user to unplug a USB adapter");
        fixture.Stale = false; fixture.Device["connection_error"] = ""; fixture.Observe();
        Check(fixture.Message.StartsWith("Connected", StringComparison.Ordinal) && fixture.Error.Length == 0,
            "Recovered CAN feedback did not clear exactly its own connection error");
        fixture.ResetMessage(); fixture.Error = "Recovery refused: keep the arm supported."; fixture.Observe();
        Check(fixture.Message.StartsWith("Connected", StringComparison.Ordinal) && fixture.Error == "Recovery refused: keep the arm supported.",
            "Connection recovery erased an unrelated user-action or recovery failure");
        fixture.Error = ""; fixture.ResetMessage(); fixture.State["selected"] = "can-other"; fixture.Observe();
        Check(!fixture.Message.StartsWith("Connected", StringComparison.Ordinal) && fixture.Error.Contains("not active", StringComparison.Ordinal),
            "Fresh feedback from another selected CAN adapter healed the assigned robot's connection presentation");
        fixture.State["selected"] = "can-b"; fixture.ResetMessage(); fixture.Error = ""; fixture.Observe(sourceAgeMilliseconds: 600);
        Check(fixture.Message.StartsWith("Controller telemetry interrupted", StringComparison.Ordinal), "An old telemetry envelope healed the stream-ended footer");
        fixture.Observe(backend: "different-backend");
        Check(fixture.Message.StartsWith("Controller telemetry interrupted", StringComparison.Ordinal) && fixture.Permission == 0,
            "A different backend's receipt healed the old connection or presentation changed control permission");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin = new();
        public JsonObject Device { get; } = new()
        {
            ["id"] = "can-b", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true, ["connection_error"] = "",
            ["status"] = new JsonObject { ["age_s"] = .001 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject { ["fresh"] = true, ["age_s"] = .001 }).ToArray())
        };
        public JsonObject State { get; }
        public string Message => Get<string>("message");
        public string Error { get => Get<string>("uiActionError"); set => Set("uiActionError", value); }
        public long Permission => plugin.CommandAuthority.Epoch;
        public bool Stale { set { Device["fresh"] = !value; foreach (var joint in Device["joints"]!.AsArray()) joint!["fresh"] = !value; } }
        public Fixture()
        {
            State = new JsonObject { ["selected"] = "can-b", ["devices"] = new JsonArray(Device) };
            Set("token", "connection-fixture");
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-2", "Piper 2", "can-b")], ActiveRobotId: "piper-2")));
            ResetMessage();
        }
        public void ResetMessage() => Set("message", "Controller telemetry interrupted: Controller state stream ended.");
        public void Observe(double sourceAgeMilliseconds = 0, string backend = "connection-fixture") =>
            typeof(PipersPlugin).GetMethod("ObserveTelemetryConnectionPresentation", Flags)!.Invoke(plugin,
                [new TelemetryReceipt(State, backend, 1, Stopwatch.GetTimestamp(), sourceAgeMilliseconds)]);
        private T Get<T>(string field) => (T)typeof(PipersPlugin).GetField(field, Flags)!.GetValue(plugin)!;
        private void Set(string field, object value) => typeof(PipersPlugin).GetField(field, Flags)!.SetValue(plugin, value);
        public void Dispose() { ((IDisposable)Get<object>("channel")).Dispose(); Get<SemaphoreSlim>("commands").Dispose(); Get<CancellationTokenSource>("shutdown").Dispose(); }
    }
}
