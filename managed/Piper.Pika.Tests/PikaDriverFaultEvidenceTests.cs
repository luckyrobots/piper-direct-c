using System.Reflection;
using System.Text;
using System.Text.Json;
using LuckyRobots.Piper.Pika;

internal static class PikaDriverFaultEvidenceTests
{
    public static async Task Run(Action<bool, string> check)
    {
        static JsonElement Packet(double angle, string status, double volts) => JsonSerializer.SerializeToElement(new
        {
            motor = new { Position = angle, Speed = 0, Current = -52 },
            motorstatus = new { Status = status, Voltage = volts, DriverTemp = 40, MotorTemp = 33, BusCurrent = 0 }
        });
        var recorder = new PikaDriverFaultRecorder();
        byte[] read = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        for (int i = 0; i < 40; i++) recorder.Read(i * .001, read);
        byte[] packet = PikaProtocol.Packet(PikaCommand.Position, PikaProtocol.Angle(44));
        for (int i = 0; i < 40; i++)
        {
            var write = recorder.BeginWrite(.04 + i * .001, PikaCommand.Position, PikaProtocol.Angle(44), packet);
            PikaDriverFaultRecorder.FinishWrite(write, .0401 + i * .001);
        }
        check(recorder.Observe(.1, Packet(.835, "0x40", 23.9), .835, 0, true) is null,
            "Healthy telemetry generated a raw fault event");
        var faultPacket = Packet(-2, "0x21", 8);
        var evidence = recorder.Observe(.11, faultPacket, -2, 0, false)!;
        check(evidence.RawTelemetryJson == faultPacket.GetRawText() && !evidence.RawTelemetryTruncated && evidence.Summary.FaultMask == 0x21 &&
            evidence.Summary.Voltage == 8 && evidence.RecentFeedback[0].Status == "0x40" && evidence.RecentFeedback[^1].PositionRadians == -2 &&
            evidence.RecentFeedback[^1].MotorCurrent == -52 && evidence.Summary.DriverTemperature == 40,
            "An immutable fault transition lost its literal frame or before/fault electrical measurements");
        check(evidence.RecentReads.Count == 16 && evidence.RecentWrites.Count == 16 && evidence.RecentReads.All(r => r.Truncated &&
            Convert.FromBase64String(r.PrefixBase64).Length + Convert.FromBase64String(r.SuffixBase64).Length == 256) &&
            Convert.FromBase64String(evidence.RecentWrites[^1].PacketBase64).SequenceEqual(packet) && evidence.RecentWrites.All(w => w.CompletedSeconds > w.StartedSeconds),
            "Raw chunk/write history exceeded its bounds or changed a transmitted7-byte packet");
        check(recorder.Observe(.12, Packet(.835, "0x21", 8), .835, 0, false) is null,
            "Repeated fault samples emitted a full raw payload on every periodic packet");
        recorder.Observe(.13, Packet(.835, "0x40", 23.9), .835, 0, true);
        var second = recorder.Observe(.14, Packet(.835, "0x20", 23.9), .835, 0, false)!;
        check(second.TransitionOrdinal == 2 && evidence.TransitionOrdinal == 1 && evidence.RecentFeedback.Count == 2 &&
            evidence.RecentFeedback[^1].Status == "0x21" && evidence.RawTelemetryJson == faultPacket.GetRawText(),
            "Post-fault recovery or a new transition rewrote the prior immutable evidence");
        string malformedOptional = "{\"motor\":{\"Position\":0.8,\"Speed\":0,\"Current\":\"bad\"},\"motorstatus\":{\"Status\":\"0x40\",\"Voltage\":\"bad\"}}";
        using (var document = JsonDocument.Parse(malformedOptional)) recorder.Observe(.15, document.RootElement, .8, 0, true);
        var next = recorder.Observe(.16, Packet(.8, "0x20", 23.9), .8, 0, false)!;
        check(next.RecentFeedback[^2].Voltage is null && next.RecentFeedback[^2].MotorCurrent is null,
            "Malformed optional diagnostics could interrupt the control loop");
        var noisy = new PikaDriverFaultRecorder();
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(new { motor = new { Position = -2, Speed = 0 },
            motorstatus = new { Status = "0x21" }, extra = new string('測', 20000) })))
        {
            var bounded = noisy.Observe(.1, document.RootElement, -2, 0, false)!;
            check(bounded.RawTelemetryTruncated && bounded.RawTelemetryJson.Length <= 4096 &&
                JsonSerializer.Serialize(new { type = "driver_fault_evidence", evidence = bounded }).Length < 32768,
                "Unexpected long firmware text bypassed the existing bounded pipe event size");
        }
        await using var service = new PikaProcessService("/dev/pika-fixture", "unused");
        var retain = typeof(PikaProcessService).GetMethod("RetainDriverFaultEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 1; i <= 10; i++)
            retain.Invoke(service, [JsonSerializer.SerializeToElement(new { evidence = evidence with { TransitionOrdinal = i } })]);
        var summary = service.Snapshot();
        string ordinary = summary.ToJsonString();
        check(summary["driver_fault_evidence"] is not null && summary["driver_fault_evidence_raw"] is null &&
            !ordinary.Contains("RawTelemetryJson") && !ordinary.Contains("PacketBase64") && !ordinary.Contains("RecentReads"),
            "Periodic controller snapshots leaked the heavy raw evidence payload");
        var raw = service.Snapshot(includeRawEvidence: true)["driver_fault_evidence_raw"]!.AsArray();
        check(raw.Count == 4 && raw[0]!["TransitionOrdinal"]!.GetValue<long>() == 7 && raw[^1]!["TransitionOrdinal"]!.GetValue<long>() == 10 &&
            raw[^1]!["RawTelemetryJson"]!.GetValue<string>() == faultPacket.GetRawText(),
            "Read-only raw retrieval lost its literal fault packet or retained an unbounded transition history");
    }
}
