using System.Text.Json;
using LuckyRobots.Piper.Pika;

if (args.Length == 2 && args[0] == "--device" && args[1] is "/dev/pika-fixture" or "/dev/pika-monitor-fixture" or "/dev/pika-monitor-die-fixture")
{
    bool monitoringFixture = args[1] != "/dev/pika-fixture";
    Console.WriteLine("{\"type\":\"ready\",\"connected\":true}"); Console.Out.Flush();
    bool enabled = monitoringFixture;
    while (Console.ReadLine() is { } line)
    {
        using var data = JsonDocument.Parse(line); var root = data.RootElement;
        string action = root.GetProperty("action").GetString()!;
        if (monitoringFixture && action is not ("heartbeat" or "disconnect"))
            throw new Exception("A read-only monitor sent a motor power/position command.");
        bool ok = action != "enable" || root.GetProperty("clear").GetBoolean();
        if (ok && action == "enable") enabled = true;
        if (!monitoringFixture && action is "disable" or "disconnect") enabled = false;
        Console.WriteLine(JsonSerializer.Serialize(new { type = "state", connected = true, fresh = true, enabled,
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
Console.WriteLine("Pika C# tests passed: wire packets, linkage, fragmented telemetry, invalid feedback, ownership and explicit reconnect; native Windows COM is unavailable.");
