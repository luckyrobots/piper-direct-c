using System.Text.Json;
using System.Text;
using LuckyRobots.Piper.Pika;

internal static class PikaDiagnosticsTests
{
    public static void Run(Action<bool, string> check)
    {
        var stream = new PikaJsonStream();
        check(!stream.Push("{\"motor\":{\"Position\":").Any() && stream.Diagnostics.CandidateObjects == 0,
            "Fragmented JSON was diagnosed as a rejected complete packet");
        check(stream.Push("0,\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"}}").Count() == 1 &&
            stream.Diagnostics is { CandidateObjects: 1, AcceptedObjects: 1, MalformedObjects: 0, DuplicateKeyObjects: 0 },
            "Valid fragmented telemetry changed acceptance or diagnostics counts");
        check(!stream.Push("{\"broken\":oops}").Any() && stream.Diagnostics.MalformedObjects == 1, "Malformed JSON did not retain rejection semantics/counts");
        check(!stream.Push("{\"motor\":{\"Position\":1,\"Position\":2,\"Speed\":0,\"Speed\":1},\"motorstatus\":{\"Status\":\"0x00\",\"Status\":\"0x40\",\"MotorTemp\":24,\"MotorTemp\":25}}").Any(),
            "Diagnostics accepted ambiguous fields that were previously rejected");
        var diagnostics = stream.Diagnostics;
        check(diagnostics.DuplicateKeyObjects == 1 && diagnostics.DuplicatePathCounts.Count == 4 &&
            new[] { "$.motor.Position", "$.motor.Speed", "$.motorstatus.Status", "$.motorstatus.MotorTemp" }
                .All(path => diagnostics.DuplicatePathCounts.GetValueOrDefault(path) == 1), "Diagnostics hid later critical duplicates behind an earlier noncritical duplicate");
        string unknown = "do-not-copy-arbitrary-property-content";
        check(!stream.Push("{\"" + unknown + "\":1,\"" + unknown + "\":2}").Any() &&
            stream.Diagnostics.DuplicatePathCounts.GetValueOrDefault("$.[other]") == 1 &&
            !JsonSerializer.Serialize(stream.Diagnostics).Contains(unknown), "Unknown property content leaked into bounded parser diagnostics");
        check(!stream.Push("{" + new string(' ', 16385)).Any() && stream.Diagnostics.OverflowResets == 1, "Incomplete overflow recovery changed or was not counted");
        check(stream.Push("{}").Count() == 1 && stream.Diagnostics.CandidateObjects == 5 && stream.Diagnostics.AcceptedObjects == 2 &&
            stream.Diagnostics.MalformedObjects == 1 && stream.Diagnostics.DuplicateKeyObjects == 2, "Counters lost packet conservation after overflow recovery");
        var firstOverflow = stream.Diagnostics.FirstOverflowSample;
        check(firstOverflow is { BraceDepth: 1, InsideString: false, EscapePending: false, FirstCandidateOffset: 0 } &&
            firstOverflow.BufferCharacters > 16384 && Convert.FromBase64String(firstOverflow.DecodedPrefixUtf8Base64).Length == 512 &&
            Convert.FromBase64String(firstOverflow.DecodedSuffixUtf8Base64).Length == 512,
            "Overflow sample did not preserve bounded prefix/suffix and exact framing state");
        check(!stream.Push("{\"value\":\"" + new string('x', 16385)).Any() && stream.Diagnostics.OverflowResets == 2 &&
            stream.Diagnostics.FirstOverflowSample == firstOverflow,
            "Later overflows replaced the bounded first sample or changed recovery behavior");
        var quoted = new PikaJsonStream();
        quoted.Push("{\"value\":\"" + new string('x', 16385)).ToArray();
        check(quoted.Diagnostics.FirstOverflowSample is { InsideString: true, BraceDepth: 1 } quotedSample &&
            Encoding.UTF8.GetString(Convert.FromBase64String(quotedSample.DecodedPrefixUtf8Base64)).StartsWith("{\"value\":\""),
            "A stranded quote was not distinguished from a stranded brace");
        ((Dictionary<string, long>)diagnostics.DuplicatePathCounts)["$.motor.Position"] = 999;
        check(stream.Diagnostics.DuplicatePathCounts["$.motor.Position"] == 1, "A diagnostics snapshot mutated active parser counters");
        foreach ((string json, string reason) in new[]
        {
            ("[]", "$"), ("{}", "$.motor"), ("{\"motor\":{}}", "$.motorstatus"),
            ("{\"motor\":{\"Position\":\"bad\",\"Speed\":0},\"motorstatus\":{\"Status\":\"0x00\"}}", "$.motor.Position"),
            ("{\"motor\":{\"Position\":0,\"Speed\":\"bad\"},\"motorstatus\":{\"Status\":\"0x00\"}}", "$.motor.Speed"),
            ("{\"motor\":{\"Position\":0,\"Speed\":0},\"motorstatus\":{\"Status\":\"bad\"}}", "$.motorstatus.Status")
        })
        {
            using var document = JsonDocument.Parse(json);
            check(!PikaProtocol.Telemetry(document.RootElement, out _, out _, out _) && PikaProtocol.TelemetryRejectionPath(document.RootElement) == reason,
                "Telemetry rejection diagnostics changed acceptance or classified the wrong field");
        }
    }
}
