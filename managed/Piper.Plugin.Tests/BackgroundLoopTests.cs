using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

/// <summary>The watchdog and heartbeat loops: none may end unseen, and stale
/// feedback withdraws permission once instead of on every tick.</summary>
internal static class BackgroundLoopTests
{
    private const string Backend = "loop-session";

    private static PipersPlugin Plugin(out JsonObject snapshot)
    {
        var plugin = new PipersPlugin();
        plugin.client = new PiperControl.PiperControlClient(new EmergencyStopTests.FakeCalls());
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-one")], ActiveRobotId: "piper-1"));
        plugin.telemetry.AdoptBackend(Backend);
        snapshot = Fixtures.Snapshot(Backend, Fixtures.Arm("can-one", generation: 1));
        plugin.telemetry.PublishState(snapshot);
        plugin.engine = Fixtures.Context(new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
            (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
            (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false }))));
        return plugin;
    }

    private static void Close(PipersPlugin plugin) { plugin.shutdown.Cancel(); plugin.channel.Dispose(); plugin.commands.Dispose(); }

    private static async Task<bool> Until(Func<bool> ready, int maximumMs = 5000)
    {
        long started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > maximumMs) return false;
            await Task.Delay(5);
        }
        return true;
    }

    public static async Task RunStaleFeedbackEdge()
    {
        var plugin = Plugin(out var snapshot);
        try
        {
            var authority = plugin.CommandAuthority;
            var scope = new RobotCommandScope("piper-1", Backend, "can-one", 1);
            void Stale() => plugin.telemetry.Publish(new(snapshot, Backend, 1, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2, 0));
            void Fresh() => plugin.telemetry.Publish(new(snapshot, Backend, 2, Stopwatch.GetTimestamp(), 0));
            Stale();
            plugin.approved = true;
            long before = authority.Epoch;
            await plugin.ArmHeartbeatTick(CancellationToken.None);
            long edge = authority.Epoch;
            Check(!plugin.approved && edge > before, "Stale robot feedback did not withdraw motion permission");
            // The operator question raised during the gap must stay answerable.
            plugin.RequestResetCompletion(scope, edge);
            for (int tick = 0; tick < 5; tick++) await plugin.ArmHeartbeatTick(CancellationToken.None);
            Check(authority.Epoch == edge, "Stale feedback advanced the permission epoch on every watchdog tick");
            Check(plugin.recovery.ResetPrompt is { } prompt && prompt.Epoch == authority.Epoch,
                "A reset question raised during a feedback gap went inert on the next watchdog tick");
            // Anything granted while feedback is still stale is withdrawn on the next tick.
            plugin.approved = true;
            await plugin.ArmHeartbeatTick(CancellationToken.None);
            Check(!plugin.approved && authority.Epoch > edge, "Permission granted during stale feedback survived the watchdog");
            long regranted = authority.Epoch;
            authority.TryAcknowledge(regranted, scope, 9);
            await plugin.ArmHeartbeatTick(CancellationToken.None);
            Check(authority.Current.Lease is null && authority.Epoch > regranted, "A lease acquired during stale feedback survived the watchdog");
            // A new fresh-to-stale edge revokes again even with nothing standing.
            Fresh(); await plugin.ArmHeartbeatTick(CancellationToken.None);
            long recovered = authority.Epoch;
            Stale(); await plugin.ArmHeartbeatTick(CancellationToken.None);
            Check(authority.Epoch == recovered + 1, "A second loss of feedback did not revoke exactly once");
        }
        finally { Close(plugin); }
    }

    public static async Task RunSupervision()
    {
        // A synthetic loop: throws once, returns once, then runs until cancelled.
        {
            var plugin = Plugin(out _);
            plugin.shell.Loops.RestartDelay = TimeSpan.FromMilliseconds(10);
            using var lifetime = new CancellationTokenSource();
            int runs = 0;
            plugin.approved = true;
            var supervised = plugin.Supervise("Fixture loop", async cancellation =>
            {
                int run = Interlocked.Increment(ref runs);
                if (run == 1) throw new InvalidOperationException("synthetic fault");
                if (run == 2) return;
                await Task.Delay(Timeout.Infinite, cancellation);
            }, lifetime.Token);
            Check(await Until(() => Volatile.Read(ref runs) >= 3), "A faulted background loop was not restarted");
            Check(!supervised.IsCompleted, "A background loop fault ended its supervisor");
            Check(plugin.shell.Loops.Faults == 2, "A loop fault and an unexpected loop return were not both counted");
            Check(!plugin.approved, "A background loop fault left motion permission standing");
            Check(plugin.uiActionError.Contains("Fixture loop", StringComparison.Ordinal) && plugin.uiActionError.Contains("motion permission cleared", StringComparison.Ordinal),
                "A background loop fault was not shown to the operator: " + plugin.uiActionError);
            lifetime.Cancel();
            bool ended = true;
            try { await supervised.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { ended = false; }
            Check(ended, "A supervised loop did not end quietly at shutdown");
            Close(plugin);
        }
        // The real arm heartbeat: malformed motion feedback faults the tick. The
        // watchdog must keep running afterwards, with permission withdrawn.
        {
            var plugin = Plugin(out var snapshot);
            plugin.shell.Loops.RestartDelay = TimeSpan.FromMilliseconds(10);
            using var lifetime = new CancellationTokenSource();
            var authority = plugin.CommandAuthority;
            snapshot["motion"]!["request_id"] = "not-a-number";
            plugin.telemetry.Publish(new(snapshot, Backend, 1, Stopwatch.GetTimestamp(), 0));
            plugin.approved = true;
            authority.TryAcknowledge(authority.Epoch, new("piper-1", Backend, "can-one", 1), 5);
            var supervised = plugin.Supervise("Arm heartbeat", plugin.ArmHeartbeats, lifetime.Token);
            Check(await Until(() => plugin.shell.Loops.Faults >= 1), "Malformed motion feedback did not fault the arm heartbeat");
            Check(!plugin.approved && authority.Current.Lease is null, "A dead arm heartbeat left motion permission or its lease standing");
            Check(plugin.uiActionError.Contains("Arm heartbeat", StringComparison.Ordinal), "A dead arm heartbeat was not shown to the operator");
            // Still watching: once feedback goes stale, a later grant is withdrawn.
            plugin.telemetry.Publish(new(snapshot, Backend, 2, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2, 0));
            plugin.approved = true;
            Check(await Until(() => !plugin.approved), "The feedback watchdog stopped after an arm heartbeat fault");
            Check(!supervised.IsCompleted, "The arm heartbeat supervisor ended after a fault");
            lifetime.Cancel();
            bool ended = true;
            try { await supervised.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { ended = false; }
            Check(ended, "The arm heartbeat supervisor did not end quietly at shutdown");
            Close(plugin);
        }
    }
}
