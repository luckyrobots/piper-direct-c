using System.Text.Json;
using LuckyRobots.Piper.Pika;

public static class PikaResyncAdversarialTests
{
    private const string Packet = "{\"motor\":{\"Position\":0.25,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"}}";
    private const string CorruptPrefix = "{\r\n\"motor\":{\r\n\"Speed\":0.000,\r\n\"C";

    public static void Run(Action<bool, string> check)
    {
        bool IsTelemetry(JsonElement value) => PikaProtocol.Telemetry(value, out _, out _, out _);
        foreach (string wrapper in new[]
        {
            "{\"wrapper\":" + Packet + "}",
            "{\"wrapper\":[" + Packet + "]}",
            JsonSerializer.Serialize(new { note = Packet, braces = "} quoted {", other = "\\\"motor\\\"" })
        })
        foreach (int chunk in new[] { 1, 7, 19, wrapper.Length })
        {
            var stream = new PikaJsonStream();
            var observed = new List<JsonElement>();
            for (int at = 0; at < wrapper.Length; at += chunk)
                observed.AddRange(stream.Push(wrapper.Substring(at, Math.Min(chunk, wrapper.Length - at))));
            check(observed.Count == 1 && !observed.Any(IsTelemetry) && stream.Diagnostics.FramingResyncs == 0,
                "A complete/fragmented valid wrapper or escaped JSON string was harvested as standalone telemetry");
            check(stream.Push(Packet).Count(IsTelemetry) == 1,
                "A valid outer wrapper stranded the subsequent standalone telemetry packet");
        }

        foreach (bool array in new[] { false, true })
        {
            string prefix = array ? "{\"unknown\":[" : "{\"unknown\":";
            string suffix = array ? "]}" : "}";
            var stream = new PikaJsonStream();
            check(!stream.Push(prefix + Packet).Any(), "Incomplete valid unknown wrapper exposed its complete nested telemetry");
            var outer = stream.Push(suffix).ToArray();
            check(outer.Length == 1 && !outer.Any(IsTelemetry) && stream.Diagnostics.FramingResyncs == 0,
                "Completing a valid unknown wrapper changed its packet boundary or nested schema");
        }

        foreach (int depth in new[] { 17, 64 })
        {
            string opening = string.Concat(Enumerable.Repeat("{\"nested\":", depth));
            string closing = new('}', depth);
            var stream = new PikaJsonStream();
            check(!stream.Push(opening + Packet).Any(), "Depth-policy rejection harvested nested telemetry from an incomplete valid wrapper");
            check(!stream.Push(closing).Any() && stream.Diagnostics.FramingResyncs == 0 && stream.Diagnostics.AcceptedObjects == 0,
                "Depth-policy rejection was mistaken for malformed-prefix proof and harvested nested telemetry");
            check(stream.Push(Packet).Count(IsTelemetry) == 1, "A deep rejected wrapper prevented later standalone packet recovery");
        }
        var extremeDepth = new PikaJsonStream();
        check(!extremeDepth.Push("{\"nested\":" + new string('[', 17000) + Packet).Any(IsTelemetry) &&
            extremeDepth.Diagnostics.FramingResyncs == 0,
            "A proof-reader depth limit below the append-buffer bound harvested deeply nested telemetry before overflow reset");

        foreach ((string raw, string path) in new[]
        {
            ("{\"motor\":{\"Position\":0,\"Speed\":0},\"motor\":{\"Position\":1,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"}}", "$.motor"),
            ("{\"motor\":{\"Position\":0,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"},\"motorstatus\":{\"Status\":\"0x40\"}}", "$.motorstatus"),
            ("{\"motor\":{\"Position\":0,\"Position\":1,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"}}", "$.motor.Position"),
            ("{\"motor\":{\"Position\":0,\"Speed\":0,\"Speed\":1},\"motorstatus\":{\"Status\":\"0x00\"}}", "$.motor.Speed"),
            ("{\"motor\":{\"Position\":0,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\",\"Status\":\"0x40\"}}", "$.motorstatus.Status")
        })
        {
            var stream = new PikaJsonStream();
            check(!stream.Push(raw).Any() && stream.Diagnostics.DuplicateKeyObjects == 1 &&
                stream.Diagnostics.DuplicatePathCounts.GetValueOrDefault(path) == 1 && stream.Diagnostics.AcceptedObjects == 0,
                "Critical duplicate telemetry fields changed acceptance or lost rejection counters: " + path);
            check(stream.Push(Packet).Count(IsTelemetry) == 1 && stream.Diagnostics.DuplicateKeyObjects == 1,
                "A duplicate-field rejection prevented later independent packet recovery");
            stream = new PikaJsonStream();
            check(!stream.Push(CorruptPrefix + raw).Any() && stream.Diagnostics.AcceptedObjects == 0,
                "Malformed-prefix recovery accepted an ambiguous critical-field packet: " + path);
            check(stream.Push(Packet).Count(IsTelemetry) == 1,
                "An ambiguous recovery candidate stranded a subsequent complete valid packet: " + path);
        }

        foreach (bool fragmented in new[] { false, true })
        {
            var stream = new PikaJsonStream();
            string wrapper = "{\"unknown\":" + Packet + "}";
            string first = fragmented ? wrapper[..^1] : wrapper;
            check(!stream.Push(CorruptPrefix + first).Any(IsTelemetry),
                "A corrupt prefix authorized harvesting nested telemetry from an unknown wrapper");
            if (fragmented)
                check(!stream.Push("}").Any(IsTelemetry), "Closing a fragmented unknown wrapper exposed its inner telemetry after corruption");
            check(stream.Push(Packet).Count(IsTelemetry) == 1,
                "Skipping an unknown wrapper after corruption prevented the next legitimate standalone packet from recovering");
        }

        // A motor fragment and a later status fragment are never one sample.
        var mixed = new PikaJsonStream();
        check(!mixed.Push("{\"motor\":{\"Position\":0.75,\"Speed\":0},\"broken\":oops\r\n" +
            "{\"motorstatus\":{\"Status\":\"0x40\"}}").Any(IsTelemetry),
            "Recovery spliced motor and motorstatus fields across incomplete packets");
        var recovered = mixed.Push(Packet).Where(IsTelemetry).ToArray();
        check(recovered.Length == 1 && PikaProtocol.Telemetry(recovered[0], out double position, out _, out bool enabled) && position == .25 && !enabled,
            "Recovered telemetry borrowed a position or power bit from a discarded fragment");
    }
}
