using System.Text.Json;
using LuckyRobots.Piper.Pika;

internal static class PikaReadOnlyAdoptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string oldOwner = Guid.NewGuid().ToString(), newOwner = Guid.NewGuid().ToString();
        JsonElement Request(string owner, bool adopt = false) => JsonSerializer.SerializeToElement(new {
            owner, take_over = adopt, read_only_adopt = adopt,
            // Public claims must not replace the service's private evidence.
            connected = false, enabled = false, armed = false, pending = false, following = false, power_requested = false, power_uncertain = false });
        await using (var unopened = new PikaProcessService("/dev/pika-fixture", "unused", OperatingSystem.IsWindows() ? "Ubuntu" : null))
        {
            await Reject(() => unopened.ExecuteAsync("connect", Request(newOwner, true), default));
            await Reject(() => unopened.ExecuteAsync("disable", Request(newOwner, true), default));
            bool invalid = false;
            try { await unopened.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner = newOwner, read_only_adopt = "true" }), default); }
            catch (ArgumentException) { invalid = true; }
            check(invalid && unopened.Snapshot()["owner"] is null && unopened.Snapshot()["read_only_adoptable"]?.GetValue<bool>() == false,
                "Unproven/malformed automatic adoption reached transport setup or changed ownership.");
        }
        if (!OperatingSystem.IsLinux()) return;

        await using (var live = new PikaProcessService("/dev/pika-monitor-fixture", Environment.ProcessPath!))
        {
            await live.ExecuteAsync("connect", Request(oldOwner), default);
            await live.ExecuteAsync("heartbeat", Request(oldOwner), default);
            await Reject(() => live.ExecuteAsync("connect", Request(newOwner, true), default));
            check(live.Snapshot()["connected"]?.GetValue<bool>() == true && live.Snapshot()["owner"]?.GetValue<string>() == oldOwner,
                "A stale client decision stole a connected foreign monitor.");
        }
        await using (var expired = new PikaProcessService("/dev/pika-unpowered-monitor-die-fixture", Environment.ProcessPath!))
        {
            await expired.ExecuteAsync("connect", Request(oldOwner), default);
            await expired.ExecuteAsync("heartbeat", Request(oldOwner), default);
            await WaitEnded(expired);
            var ended = expired.Snapshot();
            check(ended["read_only_adoptable"]?.GetValue<bool>() == true && ended["configuration_blocked"]?.GetValue<bool>() == true &&
                ended["owner"]?.GetValue<string>() == oldOwner && ended["power_requested"]?.GetValue<bool>() == false,
                "A confirmed exited unpowered observer never advertised safe read-only adoption.");
            var reply = await expired.ExecuteAsync("connect", Request(newOwner, true), default);
            var adopted = expired.Snapshot();
            check(reply["message"]?.GetValue<string>() == "Linux serial connected; reading only" &&
                adopted["connected"]?.GetValue<bool>() == true && adopted["owner"]?.GetValue<string>() == newOwner &&
                adopted["power_requested"]?.GetValue<bool>() == false && adopted["read_only_adoptable"]?.GetValue<bool>() == false,
                "Read-only adoption reused old power/targets or failed to establish the new passive owner.");
            // A second automatic claimant must lose after the first claimant
            // has connected, even if it copied the old true capability bit.
            await Reject(() => expired.ExecuteAsync("connect", Request(oldOwner, true), default));
            check(expired.Snapshot()["owner"]?.GetValue<string>() == newOwner, "Concurrent automatic adopters replaced a newly connected owner.");
        }
        await using (var poweredObserver = new PikaProcessService("/dev/pika-monitor-die-fixture", Environment.ProcessPath!))
        {
            await poweredObserver.ExecuteAsync("connect", Request(oldOwner), default);
            await poweredObserver.ExecuteAsync("heartbeat", Request(oldOwner), default);
            await WaitEnded(poweredObserver);
            check(poweredObserver.Snapshot()["enabled"]?.GetValue<bool>() == true && poweredObserver.Snapshot()["read_only_adoptable"]?.GetValue<bool>() == false,
                "A last-known externally powered observer advertised unpowered adoption.");
            await Reject(() => poweredObserver.ExecuteAsync("connect", Request(newOwner, true), default));
            check(poweredObserver.Snapshot()["owner"]?.GetValue<string>() == oldOwner, "Powered observer's ownership was discarded by automatic adoption.");
        }
        await using (var uncertain = new PikaProcessService("/dev/pika-fixture", Environment.ProcessPath!))
        {
            await uncertain.ExecuteAsync("connect", Request(oldOwner), default);
            await uncertain.ExecuteAsync("enable", JsonSerializer.SerializeToElement(new { owner = oldOwner, clear = true }), default);
            await uncertain.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner = oldOwner, value = .75 }), default);
            await WaitEnded(uncertain);
            await Reject(() => uncertain.ExecuteAsync("connect", Request(newOwner, true), default));
            check(uncertain.Snapshot()["owner"]?.GetValue<string>() == oldOwner && uncertain.Snapshot()["power_uncertain"]?.GetValue<bool>() == true,
                "Automatic adoption erased an uncertain transmitted motor request.");
        }

        async Task Reject(Func<Task> operation)
        {
            bool rejected = false;
            try { await operation(); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Read-only adoption requires", StringComparison.Ordinal)) { rejected = true; }
            check(rejected, "Automatic adoption was not rejected by private evidence before any session change.");
        }
    }

    private static async Task WaitEnded(PikaProcessService session)
    {
        for (int i = 0; i < 200 && session.Snapshot()["connected"]?.GetValue<bool>() == true; i++) await Task.Delay(10);
        if (session.Snapshot()["connected"]?.GetValue<bool>() != false) throw new Exception("Adoption fixture did not exit.");
    }
}
