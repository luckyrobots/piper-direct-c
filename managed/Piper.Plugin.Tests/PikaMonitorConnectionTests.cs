using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class PikaMonitorConnectionTests
{
    public static void Run()
    {
        Check(PikaMonitorConnectionPolicy.Inspect(null) == PikaMonitorConnection.Connect, "Unregistered assigned Pika never started its passive monitor.");
        var normal = new JsonObject { ["connected"] = false, ["configuration_blocked"] = false };
        Check(PikaMonitorConnectionPolicy.Inspect(normal) == PikaMonitorConnection.Connect, "Released gripper stopped connecting read-only.");
        var abandoned = new JsonObject { ["connected"] = false, ["fresh"] = false, ["owner"] = "old-plugin",
            ["configuration_blocked"] = true, ["read_only_adoptable"] = true,
            ["enabled"] = false, ["armed"] = false, ["pending"] = false, ["following"] = false,
            ["power_requested"] = false, ["power_uncertain"] = false };
        Check(PikaMonitorConnectionPolicy.Inspect(abandoned) == PikaMonitorConnection.AdoptReadOnly,
            "A privately attested dead unpowered monitor stayed blocked after plugin restart.");
        foreach (string flag in new[] { "connected", "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" })
        {
            var unsafeState = abandoned.DeepClone().AsObject(); unsafeState[flag] = true;
            Check(PikaMonitorConnectionPolicy.Inspect(unsafeState) == PikaMonitorConnection.Skip, "Automatic monitor stole an unsafe session: " + flag);
            unsafeState[flag] = "false";
            Check(PikaMonitorConnectionPolicy.Inspect(unsafeState) == PikaMonitorConnection.Skip, "Malformed adoption flag granted takeover: " + flag);
        }
        foreach (string flag in new[] { "read_only_adoptable", "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" })
        {
            var unsupported = abandoned.DeepClone().AsObject(); unsupported.Remove(flag);
            Check(PikaMonitorConnectionPolicy.Inspect(unsupported) == PikaMonitorConnection.Skip,
                "Missing backend proof granted an automatic takeover: " + flag);
        }
        var oldBackend = abandoned.DeepClone().AsObject(); oldBackend["read_only_adoptable"] = false;
        Check(PikaMonitorConnectionPolicy.Inspect(oldBackend) == PikaMonitorConnection.Skip, "A backend that denied adoption received an automatic takeover.");
        foreach (string field in new[] { "read_only_adoptable", "configuration_blocked" })
        {
            var malformed = abandoned.DeepClone().AsObject(); malformed[field] = "true";
            Check(PikaMonitorConnectionPolicy.Inspect(malformed) == PikaMonitorConnection.Skip, "Malformed backend reservation/capability granted adoption.");
        }
        RunStatus();
    }

    /// <summary>Plugin-F: the status text has one source, the role plus the monitor's outcome.</summary>
    private static void RunStatus()
    {
        var plugin = new PipersPlugin();
        try
        {
            Check(plugin.PikaMonitorStatus.StartsWith("Pika telemetry starts read-only", StringComparison.Ordinal), "Unmonitored Pika lost its initial status");
            var none = plugin.ObservePikaRole();
            Check(plugin.TryAdvancePikaRole(none, PikaSessionRole.Owned) && plugin.PikaMonitorStatus == "Pika control session active.",
                "An Enable's owned role did not show the control session without a second status write");
            plugin.pikaMonitor.Report(new(plugin.ObservePikaRole(), "Pika is owned by another client; fresh shared telemetry is read without taking over."));
            Check(plugin.PikaMonitorStatus.StartsWith("Pika is owned by another client", StringComparison.Ordinal), "The monitor's outcome under the current role was hidden");
            plugin.ReleasePikaRole();
            Check(plugin.PikaMonitorStatus.StartsWith("Pika telemetry starts read-only", StringComparison.Ordinal),
                "A revoked role kept showing a monitor outcome reported under the earlier role");
            var released = plugin.ObservePikaRole();
            Check(plugin.TryAdvancePikaRole(released, PikaSessionRole.Monitoring) && plugin.PikaMonitorStatus == "Pika monitoring only · motor power unchanged.",
                "A monitoring role did not report read-only monitoring");
        }
        finally { plugin.channel.Dispose(); plugin.commands.Dispose(); }
    }

    /// <summary>Each attempt owns its cancellation: cancelling one that already ended
    /// (the UI actor racing the telemetry loop's next retry) cannot fault or reach its successor.</summary>
    public static async Task RunAttemptCancellation()
    {
        var retry = new PikaMonitorRetry(TimeSpan.FromSeconds(3));
        using var lifetime = new CancellationTokenSource();
        static async Task UntilCancelled(CancellationToken cancellation)
        {
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) { }
        }
        Check(retry.TryStart(lifetime.Token, _ => Task.CompletedTask), "The first monitor attempt did not start");
        var first = retry.Attempt!;
        await first.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!retry.TryStart(lifetime.Token, UntilCancelled), "A monitor attempt started inside the backoff");
        retry.ExpireBackoff();
        Check(retry.TryStart(lifetime.Token, UntilCancelled) && !ReferenceEquals(retry.Attempt, first), "An elapsed backoff did not start a new attempt");
        var second = retry.Attempt!;
        await first.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(second.Running && retry.Running, "Cancelling a finished attempt cancelled the attempt that replaced it");
        await retry.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(!second.Running && !retry.Running, "Cancel did not end the running attempt");
        Check(!retry.TryStart(lifetime.Token, UntilCancelled), "A cancelled attempt did not restart the backoff");
        retry.ExpireBackoff();
        Check(retry.TryStart(lifetime.Token, UntilCancelled), "The monitor did not retry after a cancelled attempt");
        var third = retry.Attempt!;
        await retry.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        retry.ExpireBackoff();
        Check(!third.Running && retry.Stopped && !retry.TryStart(lifetime.Token, UntilCancelled), "A stopped monitor started another attempt");
        await lifetime.CancelAsync();
    }
}
