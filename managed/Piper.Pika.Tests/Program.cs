using System.Text.Json;
using System.Diagnostics;
using LuckyRobots.Piper.Pika;

if (args.Length == 2 && args[0] == "--device" && args[1] is "/dev/pika-fixture" or "/dev/pika-monitor-fixture" or "/dev/pika-monitor-die-fixture" or
    "/dev/pika-powered-disabled-die-fixture" or "/dev/pika-powered-stale-die-fixture" or "/dev/pika-powered-pending-die-fixture" or
    "/dev/pika-powered-cancel-die-fixture" or "/dev/pika-powered-buffered-die-fixture" or "/dev/pika-enable-die-fixture")
{
    bool monitoringFixture = args[1] is "/dev/pika-monitor-fixture" or "/dev/pika-monitor-die-fixture";
    Console.WriteLine("{\"type\":\"ready\",\"connected\":true}"); Console.Out.Flush();
    bool enabled = monitoringFixture;
    var fixtureClock = Stopwatch.StartNew();
    string last_motor_request = ""; double request_time_s = -100;
    while (Console.ReadLine() is { } line)
    {
        using var data = JsonDocument.Parse(line); var root = data.RootElement;
        string action = root.GetProperty("action").GetString()!;
        if (monitoringFixture && action is not ("heartbeat" or "disconnect"))
            throw new Exception("A read-only monitor sent a motor power/position command.");
        if (action == "position" && args[1] == "/dev/pika-powered-cancel-die-fixture")
        { Thread.Sleep(200); Environment.Exit(0); } // Request was sent; no result/disable evidence reaches the caller.
        if (action == "enable" && args[1] == "/dev/pika-enable-die-fixture")
        { Thread.Sleep(30); Environment.Exit(0); } // Power write succeeded but no post-write state or acknowledgement was emitted.
        bool ok = action != "enable" || root.GetProperty("clear").GetBoolean();
        if (ok && action == "enable") enabled = true;
        if (!monitoringFixture && action is "disable" or "disconnect") enabled = false;
        bool dyingPower = action == "position" && args[1].StartsWith("/dev/pika-powered-", StringComparison.Ordinal);
        if (dyingPower) enabled = false; // Last real feedback after a client-expiry disable.
        bool pending = dyingPower && args[1] == "/dev/pika-powered-pending-die-fixture";
        double age_s = dyingPower && args[1] == "/dev/pika-powered-stale-die-fixture" ? 2.1 : .02;
        if (ok && action is "enable" or "position" or "disable")
        { last_motor_request = root.GetProperty("id").GetString()!; request_time_s = fixtureClock.Elapsed.TotalSeconds; }
        double feedback_time_s = dyingPower && args[1] == "/dev/pika-powered-buffered-die-fixture" ? request_time_s - .01 : fixtureClock.Elapsed.TotalSeconds;
        Console.WriteLine(JsonSerializer.Serialize(new { type = "state", connected = true, fresh = true, enabled, armed = enabled && !monitoringFixture, pending, age_s,
            last_motor_request, request_time_s, feedback_time_s,
            telemetry = new { motor = new { Position = .5756, Speed = 0 }, motorstatus = new { Status = enabled ? "0x40" : "0x00" } } }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "ack", id = root.GetProperty("id").GetString(), ok, message = ok ? "fixture accepted" : "finger-clear required" }));
        Console.Out.Flush();
        if (action == "heartbeat" && args[1] == "/dev/pika-monitor-die-fixture") Environment.Exit(0);
        if (action == "position" && root.GetProperty("value").GetDouble() == .75) Environment.Exit(0); // Simulated enabled agent failure.
        if (action == "disconnect") break;
    }
    return;
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
JsonElement Input(string owner, bool clear = false) => JsonSerializer.SerializeToElement(new { owner, clear });
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (InvalidOperationException) { return; }
    throw new Exception(message);
}
Check(Convert.ToHexString(PikaProtocol.Packet(10)) == "0A000000000D0A", "disable packet");
Check(Convert.ToHexString(PikaProtocol.Packet(22, 1)) == "160000803F0D0A", "position wire order");
for (int width = 0; width <= 90; width++) Check(Math.Abs(PikaProtocol.Width(PikaProtocol.Angle(width)) - width) < 1e-6, "linkage inversion");
Check(!PikaProtocol.ValidAngle(double.NaN) && !PikaProtocol.ValidAngle(8), "noncanonical angles rejected");
var parser = new PikaJsonStream();
Check(!parser.Push("noise {\"motor\":{\"Position\":0.5756,").Any(), "fragment must wait");
var telemetry = parser.Push("\"Speed\":0,},\"motorstatus\":{\"Status\":\"0x40\",},\"note\":\"} quoted {\",}").Single();
Check(PikaProtocol.Telemetry(telemetry, out var position, out _, out bool enabledStatus) && enabledStatus && position == .5756, "vendor trailing commas and fragmented JSON");
Check(!PikaProtocol.Telemetry(JsonSerializer.SerializeToElement(new { motor = "bad" }), out _, out _, out _), "malformed motor is not fresh telemetry");
Check(!parser.Push("{\"motorstatus\":{\"MotorTemp\":24,\"MotorTemp\":25,\"Status\":\"0x40\",\"Status\":\"0x00\"}}").Any(), "duplicate vendor status packet rejected");
Check(!parser.Push("{\"data\":[{\"value\":1,\"value\":2}]}").Any(), "nested array duplicates rejected");
Check(parser.Push("{\"x\":1,\"x\":2}{\"motor\":{\"Position\":0.5,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x40\"}}").Count() == 1, "valid packet after ambiguous packet must recover immediately");
Check(!PikaProtocol.Telemetry(JsonSerializer.SerializeToElement(new { motor = new { Position = "bad", Speed = 0 }, motorstatus = new { Status = "0x00" } }), out _, out _, out _), "malformed position rejected");
string owner = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString();
JsonElement Configure(string by, string path) => JsonSerializer.SerializeToElement(new { owner = by, device = path, distribution = OperatingSystem.IsWindows() ? "Ubuntu" : null });
await using (var disconnected = new PikaProcessService("/dev/pika-old", "unused", OperatingSystem.IsWindows() ? "Ubuntu" : null))
{
    await disconnected.ExecuteAsync("configure", Configure(owner, "/dev/pika-new"), default);
    Check(disconnected.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-new" && !disconnected.Snapshot()["connected"]!.GetValue<bool>(), "configuration opened a serial device or did not update identity");
    bool invalidPath = false;
    try { await disconnected.ExecuteAsync("configure", Configure(owner, "COM9"), default); } catch (ArgumentException) { invalidPath = true; }
    Check(invalidPath && disconnected.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-new", "native Windows serial path was accepted or mutated active configuration");
}
if (OperatingSystem.IsLinux())
{
    await using (var monitor = new PikaProcessService("/dev/pika-monitor-fixture", Environment.ProcessPath!))
    {
        await monitor.ExecuteAsync("connect", Input(owner), default);
        await monitor.ExecuteAsync("heartbeat", Input(owner), default);
        Check(monitor.Snapshot()["enabled"]!.GetValue<bool>() && !monitor.Snapshot()["power_requested"]!.GetValue<bool>() && !monitor.Snapshot()["power_uncertain"]!.GetValue<bool>(),
            "Reading externally enabled hardware invented power ownership");
        await Reject(() => monitor.ExecuteAsync("connect", Input(other), default), "A monitor stole another reader's live owner");
        await monitor.ExecuteAsync("disconnect", Input(owner), default);
        Check(monitor.Snapshot()["owner"] is null && !monitor.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Read-only disconnect stranded an owner or demanded a motor disable");
        await monitor.ExecuteAsync("configure", Configure(other, "/dev/pika-monitor-fixture"), default);
        await monitor.ExecuteAsync("connect", Input(other), default);
        await monitor.ExecuteAsync("heartbeat", Input(other), default);
        Check(monitor.Snapshot()["enabled"]!.GetValue<bool>(), "Read-only disconnect changed external motor power");
        await monitor.ExecuteAsync("disconnect", Input(other), default);
    }
    await using (var failedMonitor = new PikaProcessService("/dev/pika-fixture", "/nonexistent-pika-agent-" + Guid.NewGuid()))
    {
        bool failed = false;
        try { await failedMonitor.ExecuteAsync("connect", Input(owner), default); } catch (System.ComponentModel.Win32Exception) { failed = true; }
        Check(failed && failedMonitor.Snapshot()["owner"] is null && !failedMonitor.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Failed monitor setup stranded serial ownership before process start");
    }
    await using (var exitedMonitor = new PikaProcessService("/dev/pika-monitor-die-fixture", Environment.ProcessPath!))
    {
        await exitedMonitor.ExecuteAsync("connect", Input(owner), default);
        await exitedMonitor.ExecuteAsync("heartbeat", Input(owner), default);
        for (int i = 0; i < 100 && exitedMonitor.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
        Check(!exitedMonitor.Snapshot()["connected"]!.GetValue<bool>(), "Dead read-only fixture did not exit");
        await exitedMonitor.ExecuteAsync("connect", Input(other), default);
        Check(exitedMonitor.Snapshot()["owner"]!.GetValue<string>() == other, "Dead read-only observer stranded the next reader's ownership");
        await exitedMonitor.ExecuteAsync("disconnect", Input(other), default);
    }
    foreach (string ending in new[] { "disabled", "stale", "pending", "buffered" })
    {
        await using var expiredOwner = new PikaProcessService("/dev/pika-powered-" + ending + "-die-fixture", Environment.ProcessPath!);
        await expiredOwner.ExecuteAsync("connect", Input(owner), default);
        await expiredOwner.ExecuteAsync("enable", Input(owner, true), default);
        await expiredOwner.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner, value = .75 }), default);
        for (int i = 0; i < 100 && expiredOwner.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
        var ended = expiredOwner.Snapshot();
        Check(!ended["connected"]!.GetValue<bool>() && !ended["fresh"]!.GetValue<bool>(), "Expired powered fixture remained connected/fresh");
        double initialAge = ended["age_s"]!.GetValue<double>();
        await Task.Delay(100);
        Check(expiredOwner.Snapshot()["age_s"]!.GetValue<double>() >= initialAge + .09, "Disconnected telemetry retained its cached source age");
        if (ending == "disabled")
        {
            Check(!ended["power_requested"]!.GetValue<bool>() && !ended["power_uncertain"]!.GetValue<bool>(), "Verified disabled dead owner stranded an obsolete power request");
            await expiredOwner.ExecuteAsync("connect", Input(other), default);
            await expiredOwner.ExecuteAsync("heartbeat", Input(other), default);
            var observed = expiredOwner.Snapshot();
            Check(observed["owner"]!.GetValue<string>() == other && observed["fresh"]!.GetValue<bool>() &&
                !observed["enabled"]!.GetValue<bool>() && !observed["power_requested"]!.GetValue<bool>() && !observed["power_uncertain"]!.GetValue<bool>(),
                "Passive reconnect changed power or retained a dead owner's control authority");
            await expiredOwner.ExecuteAsync("disconnect", Input(other), default);
            Check(expiredOwner.Snapshot()["owner"] is null, "Recovered observer stranded serial ownership");
        }
        else
        {
            Check(ended["power_requested"]!.GetValue<bool>() && ended["owner"]!.GetValue<string>() == owner, "Stale/pending final data retired a power owner");
            await Reject(() => expiredOwner.ExecuteAsync("connect", Input(other), default), "A passive observer took over an unverified powered session");
            await Reject(() => expiredOwner.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "Unverified final data permitted device reassignment");
        }
    }
    await using (var failedEnable = new PikaProcessService("/dev/pika-enable-die-fixture", Environment.ProcessPath!))
    {
        await failedEnable.ExecuteAsync("connect", Input(owner), default);
        await failedEnable.ExecuteAsync("heartbeat", Input(owner), default);
        Check(failedEnable.Snapshot()["fresh"]!.GetValue<bool>() && !failedEnable.Snapshot()["enabled"]!.GetValue<bool>(), "Enable-failure fixture did not establish the pre-request disabled packet");
        bool failed = false;
        try { await failedEnable.ExecuteAsync("enable", Input(owner, true), default); }
        catch (IOException) { failed = true; }
        for (int i = 0; i < 100 && failedEnable.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
        var ended = failedEnable.Snapshot();
        Check(failed && !ended["connected"]!.GetValue<bool>() && ended["power_requested"]!.GetValue<bool>() &&
            ended["power_uncertain"]!.GetValue<bool>() && ended["owner"]!.GetValue<string>() == owner,
            "A pre-enable disabled packet erased an unacknowledged power write");
        await Reject(() => failedEnable.ExecuteAsync("connect", Input(other), default), "Lost enable result allowed passive foreign takeover");
        await Reject(() => failedEnable.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "Lost enable result allowed serial reassignment");
    }
    await using (var canceledPower = new PikaProcessService("/dev/pika-powered-cancel-die-fixture", Environment.ProcessPath!))
    {
        await canceledPower.ExecuteAsync("connect", Input(owner), default);
        await canceledPower.ExecuteAsync("enable", Input(owner, true), default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        bool canceled = false;
        try { await canceledPower.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner, value = .75 }), cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        for (int i = 0; i < 100 && canceledPower.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
        var ended = canceledPower.Snapshot();
        Check(canceled && !ended["connected"]!.GetValue<bool>() && ended["power_requested"]!.GetValue<bool>() &&
            ended["power_uncertain"]!.GetValue<bool>() && ended["owner"]!.GetValue<string>() == owner,
            "Cancellation discarded a transmitted request or inferred disabled hardware from EOF");
        await Reject(() => canceledPower.ExecuteAsync("connect", Input(other), default), "Cancellation allowed a foreign passive takeover");
    }
    await using var service = new PikaProcessService("/dev/pika-fixture", Environment.ProcessPath!);
    await service.ExecuteAsync("connect", Input(owner), default);
    await Reject(() => service.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "foreign owner reconfigured a live serial agent");
    await Reject(() => service.ExecuteAsync("configure", Configure(owner, "/dev/pika-other"), default), "owner reconfigured without disconnecting");
    await Reject(() => service.ExecuteAsync("connect", Input(other), default), "second owner connected");
    await Reject(() => service.ExecuteAsync("disable", Input(other), default), "foreign owner accepted");
    await Reject(() => service.ExecuteAsync("enable", Input(owner), default), "enable without confirmation accepted");
    await service.ExecuteAsync("enable", Input(owner, true), default);
    await service.ExecuteAsync("heartbeat", Input(owner), default);
    Check(service.Snapshot()["connected"]!.GetValue<bool>() && service.Snapshot()["fresh"]!.GetValue<bool>(), "agent feedback not received");
    await service.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner=other, take_over=true }), default);
    await Reject(() => service.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner, value=.5 }), default), "old owner moved after takeover");
    await service.ExecuteAsync("heartbeat", Input(other), default);
    await service.ExecuteAsync("disconnect", Input(other), default);
    Check(!service.Snapshot()["connected"]!.GetValue<bool>(), "disconnect not verified");
    await service.ExecuteAsync("configure", Configure(other, "/dev/pika-fixture"), default);
    await service.ExecuteAsync("connect", Input(other), default);
    await service.ExecuteAsync("enable", Input(other, true), default);
    await service.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner = other, value = .75 }), default);
    for (int i = 0; i < 100 && service.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
    Check(!service.Snapshot()["connected"]!.GetValue<bool>(), "failed fixture agent did not exit");
    await Reject(() => service.ExecuteAsync("configure", Configure(owner, "/dev/pika-other"), default), "dead enabled process lost its foreign ownership guard");
    await Reject(() => service.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "dead enabled process was treated as disabled");
    await Reject(() => service.ExecuteAsync("disconnect", Input(other), default), "dead enabled process could discard uncertain motor state");
    Check(service.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-fixture", "failed reconfiguration changed routing");
    await service.ExecuteAsync("connect", Input(other), default);
    await service.ExecuteAsync("disable", Input(other), default);
    await service.ExecuteAsync("disconnect", Input(other), default);
    await service.ExecuteAsync("configure", Configure(other, "/dev/pika-reassigned"), default);
    Check(service.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-reassigned", "verified released session could not be reassigned");
}
else
{
    await using var service = new PikaProcessService("/dev/pika-fixture", "unused");
    await Reject(() => service.ExecuteAsync("connect", Input(owner), default), "native Windows WCH path must be unavailable");
    Check(service.Snapshot()["owner"] is null && !service.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Rejected native Windows transport setup stranded an owner");
}
Console.WriteLine("Pika C# tests passed: wire packets, linkage, fragmented telemetry, invalid feedback, ownership, causally verified disabled reconnect, pre-enable/buffered/stale/pending/canceled retention and advancing evidence age; native Windows COM is unavailable.");
