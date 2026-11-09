using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

internal static class PikaTelemetryIntegrityTests
{
    public static void Run(Action<bool, string> check)
    {
        // Exact untruncated RX chunks captured before the false0x21 event on
        //2026-11-05. Corrupt values already existed in the incoming ASCII.
        string[] chunks = [
            "ZSI6MjMuOSwNCiJEcml2ZXJUZW1wIjo0MCwNCiJNb3Q=",
            "b3JUZW1wIjozMywNCiJTdGF0dXMiOiIweDQwIiwNCiI=",
            "QnVzQ3VycmVudCI6MA0KfQ0KDQp9DQo=",
            "ew0KIm1vdG9yIjp7DQoiU3BlZWQiOjAuMDAwLA0KIkM=",
            "dXJyZW50IjotMTIyLA0KIlBvc2l0aW9uIjowLjgzNTgNCn0NCiwNCiJtb3RvcnN0YXR1cyI6ew0KIlZvbHRhZw==",
            "ZSI6MjMuOSwNCiJEcml2ZXJUZW1wIjo0MCwNCiJNb3Q=",
            "b3JUZW1wIjozMywNCiJTdGF0dXMiOiIweDQwIiwNCiI=",
            "QnVzQ3VycmVudCI6MA0KfQ0KDQp9DQp7DQoibW90b3I=",
            "Ijp7DQoiU3BlZWQiOjAuMDAwLA0KIkN1cnJlbnQiOi0=",
            "MTIwLA0KIlBvc2l0aW9uIjowLjgzNTgNCn0NCiwNCiJtb3RvcnN0YXR1cyI6ew0KIlZvbHRhZ2UiOjI2ODE1Ng==",
            "MTU4NTk4ODUxODgwMDAwMDAwMDAwMDAwMDAwMDAwMDA=",
            "MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMA==",
            "MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA=",
            "MDAwMDAwMDAwMDAwMDAwMDAwMDAwLjAsDQoiRHJpdmVyVGVtcCI6MTA3NzQwNTI4NiwNCiJNb3RvclRlbXAiOg==",
            "NDAsDQoiU3RhdHVzIjoiMHgyMSIsDQoiQnVzQ3VycmU=",
            "bnQiOjY0DQp9DQoNCn0NCg=="];
        string wire = string.Concat(chunks.Select(chunk => Encoding.UTF8.GetString(Convert.FromBase64String(chunk))));
        var candidates = new PikaJsonStream().Push(wire).ToArray();
        var corrupt = candidates.Single(row => row.GetProperty("motorstatus").GetProperty("Status").GetString() == "0x21");
        var healthy = candidates.Single(row => row.GetProperty("motorstatus").GetProperty("Status").GetString() == "0x40");
        check(wire.Contains(corrupt.GetRawText(), StringComparison.Ordinal) && corrupt.GetProperty("motorstatus").GetProperty("Voltage").GetDouble() > 1e100,
            "Regression bytes no longer preserve the literal impossible incoming UART voltage");
        uint voltageHighWord = (uint)((ulong)BitConverter.DoubleToInt64Bits(23.9) >> 32);
        check(corrupt.GetProperty("motorstatus").GetProperty("DriverTemp").GetInt32() == voltageHighWord && voltageHighWord == 1077405286 &&
            corrupt.GetProperty("motorstatus").GetProperty("MotorTemp").GetInt32() == 40 && corrupt.GetProperty("motorstatus").GetProperty("BusCurrent").GetInt32() == 64,
            "Captured argument-shift fingerprint was changed or incorrectly blamed on JSON scalar conversion");
        check(!PikaProtocol.Telemetry(corrupt, out double rejectedPosition, out double rejectedSpeed, out bool rejectedEnabled) &&
            rejectedPosition == 0 && rejectedSpeed == 0 && !rejectedEnabled && PikaProtocol.ValidateTelemetry(corrupt) == "$.motorstatus.Voltage",
            "A whole corrupt frame exposed a valid-looking position/power bit or renewed the physical sample");
        var repairedVoltage = JsonNode.Parse(corrupt.GetRawText())!.AsObject();
        repairedVoltage["motorstatus"]!["Voltage"] = 23.9;
        using (var document = JsonDocument.Parse(repairedVoltage.ToJsonString()))
            check(!PikaProtocol.Telemetry(document.RootElement, out _, out _, out _) && PikaProtocol.ValidateTelemetry(document.RootElement) == "$.motorstatus.DriverTemp",
                "A plausible voltage hid the independently impossible shifted temperature");

        foreach (int fragment in new[] { 1, 2, 7, 32, 64, 4096 })
        {
            string input = wire + healthy.GetRawText();
            var parser = new PikaJsonStream(); var accepted = new List<JsonElement>();
            for (int at = 0; at < input.Length; at += fragment)
                accepted.AddRange(parser.Push(input.Substring(at, Math.Min(fragment, input.Length - at)))
                    .Where(row => PikaProtocol.Telemetry(row, out _, out _, out _)));
            check(accepted.Count == 2 && accepted.All(row => row.GetProperty("motorstatus").GetProperty("Status").GetString() == "0x40"),
                "Fragmentation published a corrupt fault or discarded the next intact enabled packet");
            parser = new(); accepted.Clear();
            input = "{\r\n\"motor\":{\r\n\"Speed\":0.000,\r\n\"C" + corrupt.GetRawText() + healthy.GetRawText();
            for (int at = 0; at < input.Length; at += fragment)
                accepted.AddRange(parser.Push(input.Substring(at, Math.Min(fragment, input.Length - at)))
                    .Where(row => PikaProtocol.Telemetry(row, out _, out _, out _)));
            check(accepted.Count == 1 && accepted[0].GetProperty("motorstatus").GetProperty("Status").GetString() == "0x40" &&
                parser.Diagnostics.RejectedRecoveryCandidates > 0,
                "Malformed-prefix recovery bypassed whole-frame plausibility or stranded the next intact packet");
        }

        JsonObject Packet(string status = "0x40") => JsonNode.Parse(healthy.GetRawText())!.AsObject() is { } value ? SetStatus(value, status) : throw new Exception();
        static JsonObject SetStatus(JsonObject value, string status) { value["motorstatus"]!["Status"] = status; return value; }
        foreach (var (change, path) in new (Action<JsonObject>, string)[]
        {
            (p => p["motor"]!["Position"] = 1000, "$.motor.Position"),
            (p => p["motor"]!["Speed"] = 1000, "$.motor.Speed"),
            (p => p["motor"]!["Current"] = 1e9, "$.motor.Current"),
            (p => p["motor"]!["Current"] = -.5, "$.motor.Current"),
            (p => p["motorstatus"]!["Voltage"] = 1e154, "$.motorstatus.Voltage"),
            (p => p["motorstatus"]!["Voltage"] = -1, "$.motorstatus.Voltage"),
            (p => p["motorstatus"]!["Voltage"] = "23.9", "$.motorstatus.Voltage"),
            (p => p["motorstatus"]!["DriverTemp"] = 1077405286, "$.motorstatus.DriverTemp"),
            (p => p["motorstatus"]!["DriverTemp"] = true, "$.motorstatus.DriverTemp"),
            (p => p["motorstatus"]!["MotorTemp"] = null, "$.motorstatus.MotorTemp"),
            (p => p["motorstatus"]!["MotorTemp"] = 33.5, "$.motorstatus.MotorTemp"),
            (p => p["motorstatus"]!["BusCurrent"] = 1e9, "$.motorstatus.BusCurrent")
        })
        {
            var bad = Packet(); change(bad);
            using var document = JsonDocument.Parse(bad.ToJsonString());
            check(!PikaProtocol.Telemetry(document.RootElement, out _, out _, out _) && PikaProtocol.ValidateTelemetry(document.RootElement) == path,
                "Present wrong-type/implausible metadata became fresh, or rejection reporting disagreed with acceptance: " + path);
        }
        foreach (var (status, volts, driverTemp, motorTemp) in new[] { ("0x21", 0d, -40, -30), ("0x21", 8d, 40, 33), ("0x2A", 23.9, 350, 300) })
        {
            var genuine = Packet(status); genuine["motor"]!["Current"] = -32000; genuine["motorstatus"]!["BusCurrent"] = -4000;
            genuine["motorstatus"]!["Voltage"] = volts; genuine["motorstatus"]!["DriverTemp"] = driverTemp; genuine["motorstatus"]!["MotorTemp"] = motorTemp;
            using var document = JsonDocument.Parse(genuine.ToJsonString());
            check(PikaProtocol.Telemetry(document.RootElement, out double angle, out _, out bool enabled) && !enabled && PikaProtocol.ValidateTelemetry(document.RootElement) == "",
                "Broad corruption checks filtered a legitimate low-voltage/hot fault or signed current");
            PikaDriverStatus.TryRead(status, out var decoded);
            var writes = new List<double>(); var follow = new PikaFollowSession(writes.Add);
            string id = follow.Start(0); follow.Update(id, 1, 44, .01);
            try { follow.Tick(.02, angle, enabled, decoded); check(false, "A genuine fault was hidden from the active controller"); }
            catch (InvalidOperationException) { }
            check(!follow.Active && follow.LastRetirement?.Cause == "driver-fault" && writes.Count == 0,
                "A valid fault was repaired/ignored or allowed another movement target");
        }
    }
}
