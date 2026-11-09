using System.Text.Json;
using System.Diagnostics;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Pika.Agent;

if (args.Length >= 2 && args[0] == "--device" && args[1] is "/dev/pika-garbage-fixture" or "/dev/pika-garbage-hang-fixture")
{
    // B2 fixture: interleaves malformed stdout lines with valid ones, then floods
    // garbage on a position of .9 rad. The hang variant then ignores stdin EOF.
    if (Environment.GetEnvironmentVariable("PIKA_FIXTURE_PID_FILE") is { Length: > 0 } pidFile)
        File.WriteAllText(pidFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Console.WriteLine("{\"type\":\"ready\",\"connected\":true}"); Console.Out.Flush();
    bool garbageEnabled = false;
    var garbageClock = Stopwatch.StartNew();
    string garbageRequest = ""; double garbageRequestAt = -100;
    while (Console.ReadLine() is { } line)
    {
        using var data = JsonDocument.Parse(line); var root = data.RootElement;
        string action = root.GetProperty("action").GetString()!, id = root.GetProperty("id").GetString()!;
        if (action == "position" && root.GetProperty("value").GetDouble() == .9)
        {
            for (int i = 0; i < PikaProcessService.MaximumConsecutiveMalformedLines + 8; i++) Console.WriteLine("{\"type\":\"state\",\"fresh\":\"flood\"}");
            Console.Out.Flush();
            if (args[1] == "/dev/pika-garbage-hang-fixture") Thread.Sleep(Timeout.Infinite);
            continue;
        }
        foreach (string bad in new[] { "not json", "{\"type\":5}", "[1,2]", "{\"type\":\"state\",\"fresh\":\"yes\",\"enabled\":true}",
            "{\"type\":\"ack\",\"id\":7,\"ok\":true}", "{\"type\":\"ack\",\"id\":\"" + id + "\",\"ok\":\"yes\"}" })
            Console.WriteLine(bad);
        if (action == "enable") garbageEnabled = true;
        if (action is "disable" or "disconnect") garbageEnabled = false;
        if (action is "enable" or "disable") { garbageRequest = id; garbageRequestAt = garbageClock.Elapsed.TotalSeconds; }
        Console.WriteLine(JsonSerializer.Serialize(new { type = "state", connected = true, fresh = true, enabled = garbageEnabled, armed = garbageEnabled,
            pending = false, following = false, age_s = .02, last_motor_request = garbageRequest,
            request_time_s = garbageRequest.Length == 0 ? (double?)null : garbageRequestAt, feedback_time_s = garbageClock.Elapsed.TotalSeconds }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "ack", id, ok = true, message = "garbage fixture accepted" }));
        Console.Out.Flush();
        if (action == "disconnect") break;
    }
    return;
}
if (args.Length >= 2 && args[0] == "--device" && args[1] is "/dev/pika-fixture" or "/dev/pika-monitor-fixture" or "/dev/pika-monitor-die-fixture" or
    "/dev/pika-powered-disabled-die-fixture" or "/dev/pika-powered-stale-die-fixture" or "/dev/pika-powered-pending-die-fixture" or
    "/dev/pika-powered-cancel-die-fixture" or "/dev/pika-powered-buffered-die-fixture" or "/dev/pika-enable-die-fixture" or "/dev/pika-unpowered-monitor-die-fixture")
{
    bool monitoringFixture = args[1] is "/dev/pika-monitor-fixture" or "/dev/pika-monitor-die-fixture" or "/dev/pika-unpowered-monitor-die-fixture";
    Console.WriteLine("{\"type\":\"ready\",\"connected\":true}"); Console.Out.Flush();
    bool enabled = monitoringFixture && args[1] != "/dev/pika-unpowered-monitor-die-fixture";
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
        Console.WriteLine(JsonSerializer.Serialize(new { type = "state", connected = true, fresh = true, enabled, armed = enabled && !monitoringFixture, pending, following = false, age_s,
            last_motor_request, request_time_s, feedback_time_s,
            telemetry = new { motor = new { Position = .5756, Speed = 0 }, motorstatus = new { Status = enabled ? "0x40" : "0x00" } } }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "ack", id = root.GetProperty("id").GetString(), ok, message = ok ? "fixture accepted" : "finger-clear required" }));
        Console.Out.Flush();
        if (action == "heartbeat" && args[1] is "/dev/pika-monitor-die-fixture" or "/dev/pika-unpowered-monitor-die-fixture") Environment.Exit(0);
        if (action == "position" && root.GetProperty("value").GetDouble() == .75) Environment.Exit(0); // Simulated enabled agent failure.
        if (action == "disconnect") break;
    }
    return;
}
foreach (bool requested in new[] { false, true })
{
    var acknowledgement = new PikaPowerFeedback(requested, 10);
    Check(acknowledgement.IsConfirmed(requested, 10.01), "Fresh matching motor feedback did not acknowledge the display state");
    Check(!acknowledgement.IsConfirmed(!requested, 10.01), "Opposite motor state acknowledged enable/disable display status");
    foreach (double time in new[] { 9.9, 10, double.NaN, double.PositiveInfinity })
        Check(!acknowledgement.IsConfirmed(requested, time), "Old or invalid feedback acknowledged a motor-power display request");
    Check(!new PikaPowerFeedback(requested, double.NaN).IsConfirmed(requested, 11), "Invalid request time acknowledged power feedback");
}
await PikaFollowTests.RunAsync();
await PikaMultiDeviceTests.Run();
await PikaReadOnlyAdoptionTests.Run();
PikaMeasuredHoldResumeTests.Run();
await PikaDisabledEvidenceTests.Run();
PikaDiagnosticsTests.Run();
PikaDriverStatusTests.Run();
PikaTelemetryIntegrityTests.Run();
PikaHealthyPositionTests.Run();
await PikaDriverFaultEvidenceTests.Run();
PikaResyncTests.Run();
PikaResyncAdversarialTests.Run();
JsonElement Input(string owner, bool clear = false) => JsonSerializer.SerializeToElement(new { owner, clear });
Check(Convert.ToHexString(PikaProtocol.Packet(PikaCommand.Disable)) == "0A000000000D0A", "disable packet");
Check(Convert.ToHexString(PikaProtocol.Packet(PikaCommand.Position, 1)) == "160000803F0D0A", "position wire order");
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
// The fixtures below run this test executable as the agent, directly on every OS.
static PikaProcessService Fixture(string device, string? agent = null) => new(device, agent ?? Environment.ProcessPath!, directLaunch: true);
static bool ProcessEnded(int pid)
{
    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
    catch (ArgumentException) { return true; }
}
// B2: malformed stdout lines are skipped; a sustained run faults the agent,
// which is stopped (tree-killed if it ignores EOF), detached and replaceable.
foreach (string garbage in new[] { "/dev/pika-garbage-fixture", "/dev/pika-garbage-hang-fixture" })
{
    string pidFile = Path.GetTempFileName();
    Environment.SetEnvironmentVariable("PIKA_FIXTURE_PID_FILE", pidFile);
    try
    {
        await using var garbled = Fixture(garbage);
        await garbled.ExecuteAsync("connect", Input(owner), default);
        await garbled.ExecuteAsync("enable", Input(owner, true), default);
        await garbled.ExecuteAsync("heartbeat", Input(owner), default);
        var healthy = garbled.Snapshot();
        Check(healthy["connected"]!.GetValue<bool>() && healthy["fresh"]!.GetValue<bool>() && healthy["enabled"]!.GetValue<bool>() &&
            healthy["agent_phase"]!.GetValue<string>() == "ready", "Isolated malformed agent lines ended the pump or corrupted state");
        int pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        bool failed = false;
        try { await garbled.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner, value = .9 }), default); }
        catch (IOException) { failed = true; }
        if (garbage == "/dev/pika-garbage-hang-fixture")
        {
            // The agent ignores EOF, so its retirement holds the operation gate
            // for the bounded exit wait. A scoped STOP hold reports that
            // retirement at once instead of timing out behind it.
            var retiringStopwatch = Stopwatch.StartNew();
            using var stopBound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var retiringHold = await garbled.HoldForOwnerAsync(owner, stopBound.Token);
            Check(garbled.Phase is PikaAgentPhase.Faulted or PikaAgentPhase.Closing && retiringStopwatch.Elapsed < TimeSpan.FromSeconds(1) &&
                retiringHold.Outcome == PikaHoldOutcome.Unverified && retiringHold.Message!.Contains("retiring", StringComparison.Ordinal),
                "A STOP hold on a faulted, retiring agent waited behind its close or reported a generic unverified result");
        }
        for (int i = 0; i < 1000 && garbled.Phase != PikaAgentPhase.Idle; i++) await Task.Delay(10);
        var faulted = garbled.Snapshot();
        Check(failed && garbled.Phase == PikaAgentPhase.Idle && ProcessEnded(pid), "A faulted Pika agent was not stopped and detached: " + garbage);
        Check(!faulted["connected"]!.GetValue<bool>() && faulted["owner"] is null && faulted["power_uncertain"]!.GetValue<bool>() &&
            !faulted["power_requested"]!.GetValue<bool>() && faulted["configuration_blocked"]!.GetValue<bool>() &&
            faulted["message"]!.GetValue<string>().Contains("malformed", StringComparison.Ordinal),
            "A faulted powered agent discarded its unresolved power evidence or hid the fault");
        await RejectAsync<InvalidOperationException>(() => garbled.ExecuteAsync("connect", Input(other), default), "A foreign owner took over a faulted powered session");
        var reconnected = await garbled.ExecuteAsync("connect", Input(owner), default);
        await garbled.ExecuteAsync("heartbeat", Input(owner), default);
        Check(reconnected["message"]!.GetValue<string>() == "Linux serial connected; reading only" &&
            garbled.Snapshot()["connected"]!.GetValue<bool>() && !garbled.Snapshot()["power_uncertain"]!.GetValue<bool>(),
            "The owner could not reconnect after an agent fault, or fresh disabled feedback did not resolve it");
        await garbled.ExecuteAsync("disconnect", Input(owner), default);
    }
    finally { Environment.SetEnvironmentVariable("PIKA_FIXTURE_PID_FILE", null); File.Delete(pidFile); }
}
{
    await using (var monitor = Fixture("/dev/pika-monitor-fixture"))
    {
        await monitor.ExecuteAsync("connect", Input(owner), default);
        await monitor.ExecuteAsync("heartbeat", Input(owner), default);
        Check(monitor.Snapshot()["enabled"]!.GetValue<bool>() && !monitor.Snapshot()["power_requested"]!.GetValue<bool>() && !monitor.Snapshot()["power_uncertain"]!.GetValue<bool>(),
            "Reading externally enabled hardware invented power ownership");
        await RejectAsync<InvalidOperationException>(() => monitor.ExecuteAsync("connect", Input(other), default), "A monitor stole another reader's live owner");
        await monitor.ExecuteAsync("disconnect", Input(owner), default);
        Check(monitor.Snapshot()["owner"] is null && !monitor.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Read-only disconnect stranded an owner or demanded a motor disable");
        await monitor.ExecuteAsync("configure", Configure(other, "/dev/pika-monitor-fixture"), default);
        await monitor.ExecuteAsync("connect", Input(other), default);
        await monitor.ExecuteAsync("heartbeat", Input(other), default);
        Check(monitor.Snapshot()["enabled"]!.GetValue<bool>(), "Read-only disconnect changed external motor power");
        await monitor.ExecuteAsync("disconnect", Input(other), default);
    }
    await using (var failedMonitor = Fixture("/dev/pika-fixture", "/nonexistent-pika-agent-" + Guid.NewGuid()))
    {
        bool failed = false;
        try { await failedMonitor.ExecuteAsync("connect", Input(owner), default); } catch (System.ComponentModel.Win32Exception) { failed = true; }
        Check(failed && failedMonitor.Snapshot()["owner"] is null && !failedMonitor.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Failed monitor setup stranded serial ownership before process start");
    }
    await using (var exitedMonitor = Fixture("/dev/pika-monitor-die-fixture"))
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
        await using var expiredOwner = Fixture("/dev/pika-powered-" + ending + "-die-fixture");
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
            await RejectAsync<InvalidOperationException>(() => expiredOwner.ExecuteAsync("connect", Input(other), default), "A passive observer took over an unverified powered session");
            await RejectAsync<InvalidOperationException>(() => expiredOwner.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "Unverified final data permitted device reassignment");
        }
    }
    await using (var failedEnable = Fixture("/dev/pika-enable-die-fixture"))
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
        var unverified = await failedEnable.HoldForOwnerAsync(owner, default);
        Check(unverified.Outcome == PikaHoldOutcome.Unverified &&
            failedEnable.Snapshot()["power_uncertain"]?.GetValue<bool>() == true &&
            (await failedEnable.HoldForOwnerAsync(other, default)).Outcome == PikaHoldOutcome.Skipped,
            "Global hold lost uncertainty from an exited owner or acted on it for a different owner");
        await RejectAsync<InvalidOperationException>(() => failedEnable.ExecuteAsync("connect", Input(other), default), "Lost enable result allowed passive foreign takeover");
        await RejectAsync<InvalidOperationException>(() => failedEnable.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "Lost enable result allowed serial reassignment");
    }
    await using (var canceledPower = Fixture("/dev/pika-powered-cancel-die-fixture"))
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
        await RejectAsync<InvalidOperationException>(() => canceledPower.ExecuteAsync("connect", Input(other), default), "Cancellation allowed a foreign passive takeover");
    }
    await using var service = Fixture("/dev/pika-fixture");
    await service.ExecuteAsync("connect", Input(owner), default);
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "foreign owner reconfigured a live serial agent");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("configure", Configure(owner, "/dev/pika-other"), default), "owner reconfigured without disconnecting");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("connect", Input(other), default), "second owner connected");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("disable", Input(other), default), "foreign owner accepted");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("enable", Input(owner), default), "enable without confirmation accepted");
    await service.ExecuteAsync("enable", Input(owner, true), default);
    await service.ExecuteAsync("heartbeat", Input(owner), default);
    Check(service.Snapshot()["connected"]!.GetValue<bool>() && service.Snapshot()["fresh"]!.GetValue<bool>(), "agent feedback not received");
    Check((await service.HoldForOwnerAsync(owner, default)).Outcome == PikaHoldOutcome.Acknowledged,
        "The owned live gripper did not acknowledge its hold");
    await service.ExecuteAsync("connect", JsonSerializer.SerializeToElement(new { owner=other, take_over=true }), default);
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner, value=.5 }), default), "old owner moved after takeover");
    await service.ExecuteAsync("heartbeat", Input(other), default);
    await service.ExecuteAsync("disconnect", Input(other), default);
    Check(!service.Snapshot()["connected"]!.GetValue<bool>(), "disconnect not verified");
    await service.ExecuteAsync("configure", Configure(other, "/dev/pika-fixture"), default);
    await service.ExecuteAsync("connect", Input(other), default);
    await service.ExecuteAsync("enable", Input(other, true), default);
    await service.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner = other, value = .75 }), default);
    for (int i = 0; i < 100 && service.Snapshot()["connected"]!.GetValue<bool>(); i++) await Task.Delay(10);
    Check(!service.Snapshot()["connected"]!.GetValue<bool>(), "failed fixture agent did not exit");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("configure", Configure(owner, "/dev/pika-other"), default), "dead enabled process lost its foreign ownership guard");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("configure", Configure(other, "/dev/pika-other"), default), "dead enabled process was treated as disabled");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("disconnect", Input(other), default), "dead enabled process could discard uncertain motor state");
    Check(service.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-fixture", "failed reconfiguration changed routing");
    await service.ExecuteAsync("connect", Input(other), default);
    await service.ExecuteAsync("disable", Input(other), default);
    await service.ExecuteAsync("disconnect", Input(other), default);
    await service.ExecuteAsync("configure", Configure(other, "/dev/pika-reassigned"), default);
    Check(service.Snapshot()["device"]!.GetValue<string>() == "/dev/pika-reassigned", "verified released session could not be reassigned");
}
if (!OperatingSystem.IsLinux())
{
    await using var service = new PikaProcessService("/dev/pika-fixture", "unused");
    await RejectAsync<InvalidOperationException>(() => service.ExecuteAsync("connect", Input(owner), default), "native Windows WCH path must be unavailable");
    Check(service.Snapshot()["owner"] is null && !service.Snapshot()["configuration_blocked"]!.GetValue<bool>(), "Rejected native Windows transport setup stranded an owner");
}
Console.WriteLine($"Pika C# tests passed ({Checks} checks): wire packets, linkage, fragmented telemetry, invalid feedback, ownership, causally verified disabled reconnect, pre-enable/buffered/stale/pending/canceled retention and advancing evidence age; native Windows COM is unavailable.");
