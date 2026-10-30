using System.Text.Json;
using LuckyRobots.Piper.Pika;

if (args is ["--device", "/dev/pika-fixture"])
{
    Console.WriteLine("{\"type\":\"ready\",\"connected\":true}"); Console.Out.Flush();
    bool enabled = false;
    while (Console.ReadLine() is { } line)
    {
        using var data = JsonDocument.Parse(line); var root = data.RootElement;
        string action = root.GetProperty("action").GetString()!;
        bool ok = action != "enable" || root.GetProperty("clear").GetBoolean();
        if (ok && action == "enable") enabled = true;
        if (action is "disable" or "disconnect") enabled = false;
        Console.WriteLine(JsonSerializer.Serialize(new { type = "state", connected = true, fresh = true, enabled,
            telemetry = new { motor = new { Position = .5756, Speed = 0 }, motorstatus = new { Status = enabled ? "0x40" : "0x00" } } }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "ack", id = root.GetProperty("id").GetString(), ok, message = ok ? "fixture accepted" : "finger-clear required" }));
        Console.Out.Flush(); if (action == "disconnect") break;
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
if (OperatingSystem.IsLinux())
{
    await using var service = new PikaProcessService("/dev/pika-fixture", Environment.ProcessPath!);
    await service.ExecuteAsync("connect", Input(owner), default);
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
    await service.ExecuteAsync("connect", Input(other), default);
    await service.ExecuteAsync("disconnect", Input(other), default);
}
else
{
    await using var service = new PikaProcessService("/dev/pika-fixture", "unused");
    await Reject(() => service.ExecuteAsync("connect", Input(owner), default), "native Windows WCH path must be unavailable");
}
Console.WriteLine("Pika C# tests passed: wire packets, linkage, fragmented telemetry, invalid feedback, ownership and explicit reconnect; native Windows COM is unavailable.");
