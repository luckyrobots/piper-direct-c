using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class WorkspacePublicationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var fixture = new Fixture();
        await fixture.Render();
        long initial = fixture.Epoch;
        string oldTarget = fixture.Wire("joint.6.target"), oldNavigation = fixture.Wire("workspace.settings.robot");
        check(fixture.Unscope(oldTarget) == "joint.6.target", "Successfully published controls did not accept their own epoch");
        int attempts = 0; long uncertainEpoch = 0;
        fixture.Publish = (root, _) =>
        {
            attempts++;
            check(fixture.Epoch == initial && fixture.Unscope(oldTarget).Length == 0 && fixture.Unscope("engine.robot.target").Length == 0,
                "Unconfirmed publication advanced the active epoch or accepted an old/native movement target");
            check(fixture.Unscope("robot.one.stop") == "stop" && fixture.Unscope("robot.one.disable") == "disable" &&
                fixture.Unscope("robot.one.recovery.guide.cancel") == "recovery.guide.cancel", "Publication uncertainty blocked persistent STOP/Disable/Cancel");
            if (attempts == 1) { uncertainEpoch = Epoch(root); throw new IOException("write failed after possible delivery"); }
            return Task.CompletedTask;
        };
        await fixture.Render();
        check(attempts == 2 && fixture.Epoch > uncertainEpoch && fixture.Unscope(oldTarget).Length == 0 &&
            fixture.Unscope(WorkspaceEventScope.Wrap(uncertainEpoch, "one", "joint.6.target")).Length == 0 && fixture.Unscope(fixture.Wire("joint.6.target")) == "joint.6.target",
            "Unknown delivery was not replaced by exactly one confirmed fresh-epoch view");

        using (var cancelled = new CancellationTokenSource())
        {
            attempts = 0;
            fixture.Publish = (_, ct) => { if (++attempts == 1) { cancelled.Cancel(); return Task.FromCanceled(ct); } return Task.CompletedTask; };
            bool retired = false;
            try { await fixture.Render(cancelled.Token); } catch (OperationCanceledException) { retired = true; }
            check(retired && attempts == 2 && fixture.Unscope(fixture.Wire("joint.6.target")) == "joint.6.target",
                "Cancelled delivery left the visible panel dead or reused the cancelled token for its bounded recovery publication");
        }

        attempts = 0; long beforeFailure = fixture.Epoch;
        fixture.Publish = (_, _) => { attempts++; throw new IOException("transport unavailable"); };
        bool failed = false;
        try { await fixture.Render(); } catch (InvalidOperationException) { failed = true; }
        check(failed && attempts == 2 && fixture.Epoch == beforeFailure && fixture.Unscope(fixture.Wire("joint.6.target")).Length == 0 &&
            fixture.Get<string>("uiActionError").Contains("Reopen Pipers", StringComparison.Ordinal) && fixture.Unscope("robot.one.disable") == "disable",
            "Repeated publication failure retried without a bound, lost its visible error, or left uncertain movement controls active");
        fixture.Publish = (_, _) => Task.CompletedTask;
        await fixture.Render();
        check(fixture.Unscope(fixture.Wire("joint.6.target")) == "joint.6.target", "A later successful publication could not restore controls after transport recovery");

        oldTarget = fixture.Wire("joint.6.target"); oldNavigation = fixture.Wire("workspace.settings.robot");
        fixture.Plugin.Navigation = WorkspaceNavigation.At(WorkspacePage.Settings);
        check(fixture.Unscope(oldTarget).Length == 0 && fixture.Unscope(oldNavigation).Length == 0,
            "A page change accepted the previous page's controls before its replacement view was published");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await fixture.Render(cancelled.Token); } catch (OperationCanceledException) { }
            check(fixture.Unscope(oldTarget).Length == 0, "Cancellation before the render gate restored old targets from a different page");
        }
        fixture.Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("two", "Piper 2")], ActiveRobotId: "two")));
        check(fixture.Unscope("robot.one.disable").Length == 0 && fixture.Unscope("robot.two.disable") == "disable",
            "Publication recovery redirected a persistent release request to a different robot profile");

        var serial = new Fixture();
        await serial.Render();
        long serialInitial = serial.Epoch;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight = 0, maximumInFlight = 0, serialCalls = 0;
        serial.Publish = async (_, ct) =>
        {
            maximumInFlight = Math.Max(maximumInFlight, ++inFlight);
            if (++serialCalls == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            inFlight--;
        };
        var first = serial.Render();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = serial.Render();
        using (var cancelled = new CancellationTokenSource())
        {
            var navigation = serial.Navigate("settings.cameras.depth", cancelled.Token);
            cancelled.Cancel();
            bool retired = false;
            try { await navigation; } catch (OperationCanceledException) { retired = true; }
            check(retired && serial.Plugin.Navigation == WorkspaceNavigation.Initial, "Cancelled navigation committed a destination before entering the view publication gate");
        }
        check(serialCalls == 1 && serial.Epoch == serialInitial, "Concurrent renders published out of order or committed an in-flight candidate");
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        check(maximumInFlight == 1 && serialCalls == 2 && serial.Epoch == serialInitial + 2 && Epoch(serial.LastRoot!) == serial.Epoch,
            "Serialized publications did not finish with the last confirmed tree and epoch together");

        var updating = new Fixture();
        await updating.Render();
        string oldStatus = updating.Wire("workspace.action-error");
        var patchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var patchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<IReadOnlyDictionary<string, JsonObject>>();
        updating.PublishPatch = async (widgets, ct) =>
        {
            sent.Add(widgets);
            if (sent.Count == 1)
            {
                patchEntered.TrySetResult();
                await patchRelease.Task.WaitAsync(ct);
            }
        };
        updating.Set("uiActionError", "Old profile");
        var activePatch = updating.Update();
        await patchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queuedPatch = updating.Update();
        updating.Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("two", "Piper 2")], ActiveRobotId: "two")));
        updating.Plugin.Navigation = WorkspaceNavigation.At(WorkspacePage.Recording);
        var replacement = updating.Render();
        check(!replacement.IsCompleted && sent.Count == 1, "A full view overtook an in-flight patch");
        patchRelease.TrySetResult();
        await Task.WhenAll(activePatch, queuedPatch, replacement).WaitAsync(TimeSpan.FromSeconds(3));
        check(sent.Count == 1 && sent[0][oldStatus]["text"]?.GetValue<string>() == "Old profile",
            "A queued patch from the old profile/view was sent after its scope changed");
        updating.Set("uiActionError", "New profile");
        await updating.Update();
        check(sent.Count == 2 && !sent[1].ContainsKey(oldStatus) &&
            sent[1][updating.Wire("workspace.action-error")]["text"]?.GetValue<string>() == "New profile",
            "A fresh update did not use the confirmed replacement view");

        static long Epoch(Widget root) => long.Parse(root.Id.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Fixture
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly PipersPlugin plugin = new();
        public PipersPlugin Plugin => plugin;
        public Func<Widget, CancellationToken, Task> Publish = (_, _) => Task.CompletedTask;
        public Func<IReadOnlyDictionary<string, JsonObject>, CancellationToken, Task> PublishPatch = (_, _) => Task.CompletedTask;
        public Widget? LastRoot;
        public Fixture()
        {
            Set("preferences", RobotProfileRouting.Normalize(new Preferences(Robots: [new("one", "Piper 1")], ActiveRobotId: "one")));
            var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), Flags, null,
                [new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
                    (Func<object, CancellationToken, Task>)(async (message, ct) =>
                    {
                        if (message.GetType().GetProperty("root")?.GetValue(message) is Widget root)
                        {
                            LastRoot = root;
                            await Publish(root, ct);
                        }
                        else if (message.GetType().GetProperty("widgets")?.GetValue(message) is IReadOnlyDictionary<string, JsonObject> widgets)
                            await PublishPatch(widgets, ct);
                        else throw new InvalidOperationException("Unexpected fixture publication");
                    }),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => throw new InvalidOperationException("Unexpected engine request"))], null)!;
            Set("engine", context);
        }
        public long Epoch => Get<long>("workspaceViewEpoch");
        public T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, Flags)!.GetValue(plugin)!;
        public void Set(string name, object value) => typeof(PipersPlugin).GetField(name, Flags)!.SetValue(plugin, value);
        public string Wire(string id) => WorkspaceEventScope.Wrap(Epoch, RobotProfileRouting.Active(Get<Preferences>("preferences")).Id, id);
        public string Unscope(string id) => (string)typeof(PipersPlugin).GetMethod("UnscopeEvent", Flags)!.Invoke(plugin, [id])!;
        public Task Render(CancellationToken ct = default) => (Task)typeof(PipersPlugin).GetMethod("Render", Flags)!.Invoke(plugin, [ct])!;
        public Task Navigate(string action, CancellationToken ct) => (Task)typeof(PipersPlugin).GetMethod("WorkspaceEvent", Flags)!.Invoke(plugin, [action, ct])!;
        public Task Update() => (Task)typeof(PipersPlugin).GetMethod("Update", Flags)!.Invoke(plugin, [CancellationToken.None])!;
    }
}
