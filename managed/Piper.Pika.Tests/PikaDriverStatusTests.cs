using System.Text.Json;
using LuckyRobots.Piper.Pika;

internal static class PikaDriverStatusTests
{
    public static void Run()
    {
        foreach (var (raw, enabled, _, faults) in new[]
        {
            ("0x00", false, false, 0), ("0x20", false, false, 0x20), ("0x40", true, false, 0),
            ("0x60", true, false, 0x20), ("0x41", true, false, 1), ("0x80", false, true, 0), ("0xC0", true, true, 0), ("0xFF", true, true, 0x3f)
        })
        {
            Check(PikaDriverStatus.TryRead(raw, out var status) && status!.Enabled == enabled &&
                status.FaultMask == faults && status.HasFault == (faults != 0), "Driver power and fault bits were conflated: " + raw);
        }
        foreach (string raw in new[] { "", "40", "0x", "0x100", "0xFFFFFFFF", "0xGG" })
            Check(!PikaDriverStatus.TryRead(raw, out _), "Invalid/out-of-byte-range motor status became fresh feedback: " + raw);
        PikaDriverStatus.TryRead("0x20", out var driverError);
        Check(driverError!.Text.Contains("driver error") && driverError.Text.Contains("0x20"),
            "The documented0x20 driver fault was reported as ordinary disabled/learning mode");
        var combined = new PikaDriverStatus(0x60);
        using (var data = JsonDocument.Parse("{\"motor\":{\"Position\":1.3,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x60\"}}"))
            Check(PikaProtocol.Telemetry(data.RootElement, out _, out _, out bool enabled) && enabled,
                "Reporting a combined enabled+fault status invented disabled motor power");
        var writes = new List<double>();
        var follow = new PikaFollowSession(writes.Add);
        string id = follow.Start(0); follow.Update(id, 1, 80, .01);
        follow.ObserveFeedback(.02, 1.3, 0, true, "0x60");
        try { follow.Tick(.02, 1.3, true, combined); Check(false, "Enabled+driver-error kept following alive"); }
        catch (InvalidOperationException) { }
        var stopped = follow.LastRetirement!;
        Check(stopped.Cause == "driver-fault" && stopped.Enabled == true && stopped.Reason.Contains("driver error") &&
            stopped.RecentFeedback[^1].MotorStatus == "0x60" && !follow.Active && writes.Count == 0,
            "A combined driver error lost its raw power/status proof or sent another target");
        follow.Tick(.03, 1.3, true, new(0x40));
        Check(!follow.Active && writes.Count == 0 && ReferenceEquals(stopped, follow.LastRetirement),
            "Recovered driver status automatically resumed a retired motion stream");
        var held = new PikaFollowSession(writes.Add);
        string heldId = held.Start(0); held.Update(heldId, 1, 80, .01);
        try { held.Hold(1.3, now: .02, enabled: true, driverStatus: combined); Check(false, "A measured hold wrote through an enabled driver fault"); }
        catch (InvalidOperationException) { }
        Check(writes.Count == 0 && !held.Active && !held.Holding && held.LastRetirement?.Cause == "driver-fault",
            "A rejected faulty hold retained targets, wrote a target or claimed successful holding");
        held.Hold(1.3, now: .03, enabled: true, driverStatus: new(0x40));
        Check(writes.Count == 1 && held.Holding && writes[0] == 1.3,
            "Fault reporting broke an explicit healthy measured-position hold");

        // Exact observed values must be literal packet values, not merged motor
        // fields or a default position synthesized by fragmentation/recovery.
        static string Packet(double angle, string status) => JsonSerializer.Serialize(new { motor = new { Position = angle, Speed = 0 }, motorstatus = new { Status = status } });
        string wire = Packet(1.3665, "0x40") + Packet(-2, "0x20") + Packet(1.3665, "0x20") + Packet(1.3665, "0x40");
        foreach (int fragment in new[] { 1, 2, 5, 17, 64, 4096 })
        {
            var parser = new PikaJsonStream(); var received = new List<(double Angle, string Status)>();
            for (int at = 0; at < wire.Length; at += fragment)
                foreach (var row in parser.Push(wire.Substring(at, Math.Min(fragment, wire.Length - at))))
                    if (PikaProtocol.Telemetry(row, out double angle, out _, out _)) received.Add((angle, row.GetProperty("motorstatus").GetProperty("Status").GetString()!));
            Check(received.SequenceEqual(new[] { (1.3665, "0x40"), (-2d, "0x20"), (1.3665, "0x20"), (1.3665, "0x40") }) && parser.Diagnostics.FramingResyncs == 0,
                "Fragmented parsing changed, hid or combined a driver-fault telemetry burst");
        }
        var recovery = new PikaJsonStream();
        var rows = recovery.Push("{invalid prefix" + Packet(-2, "0x20") + Packet(1.3665, "0x40")).Where(row => PikaProtocol.Telemetry(row, out _, out _, out _)).ToArray();
        Check(rows.Length == 2 && rows[0].GetProperty("motor").GetProperty("Position").GetDouble() == -2 &&
            rows[0].GetProperty("motorstatus").GetProperty("Status").GetString() == "0x20",
            "Malformed-prefix recovery manufactured or filtered an independently complete fault packet");
    }
}
