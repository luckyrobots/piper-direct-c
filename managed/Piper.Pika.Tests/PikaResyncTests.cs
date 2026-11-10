using System.Text;
using System.Text.Json;
using LuckyRobots.Piper.Pika;

internal static class PikaResyncTests
{
    public static void Run()
    {
        // Exact corruption shape from the first captured firmware overflow:
        // prior tail + truncated property name + a completely new motor root.
        const string prefix = "usCurrent\":0\r\n}\r\n\r\n}\r\n{\r\n\"motor\":{\r\n\"Speed\":0.000,\r\n\"C";
        string Packet(double angle) => "{\r\n\"motor\":{\r\n\"Speed\":0.000,\r\n\"Current\":-13,\r\n\"Position\":" +
            angle.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "\r\n}\r\n,\r\n\"motorstatus\":{\r\n\"Voltage\":23.9,\r\n\"DriverTemp\":39,\r\n\"MotorTemp\":33,\r\n\"Status\":\"0x00\",\r\n\"BusCurrent\":0\r\n}\r\n\r\n}";
        string first = Packet(.4511), second = Packet(.4525), wire = prefix + first + second;
        void Valid(JsonElement[] recovered, PikaJsonStream parser, string label)
        {
            Check(recovered.Length == 2 && recovered.All(r => PikaProtocol.Telemetry(r, out _, out _, out _)), "Real firmware framing recovery lost/spliced telemetry: " + label);
            Check(recovered[0].GetProperty("motor").GetProperty("Position").GetDouble() == .4511 &&
                recovered[1].GetProperty("motor").GetProperty("Position").GetDouble() == .4525, "Recovery changed packet positions/order: " + label);
            Check(parser.Diagnostics.FramingResyncs == 1 && parser.Diagnostics.DiscardedPrefixBytes == Encoding.UTF8.GetByteCount(prefix) &&
                parser.Diagnostics.OverflowResets == 0, "Recovery waited for an overflow or hid discarded prefix bytes: " + label);
        }
        for (int split = 0; split <= wire.Length; ++split)
        {
            var parser = new PikaJsonStream();
            var recovered = parser.Push(wire[..split]).Concat(parser.Push(wire[split..])).ToArray();
            Valid(recovered, parser, "split " + split);
        }
        var bytewise = new PikaJsonStream();
        Valid(wire.SelectMany(c => bytewise.Push(c.ToString())).ToArray(), bytewise, "one character at a time");
        Check(bytewise.Diagnostics.FramingCharactersScanned <= wire.Length * 2,
            "Normal fragmented framing rescanned the entire buffered prefix on every character");
        string stream = string.Concat(Enumerable.Repeat(prefix + first, 1000));
        var bounded = new PikaJsonStream();
        Check(bounded.Push(stream).Count() == 1000 && bounded.Diagnostics.FramingResyncs == 1000 && bounded.Diagnostics.OverflowResets == 0,
            "Repeated firmware truncations dropped complete independently valid packets");
        Check(bounded.Diagnostics.MaximumBufferedCharacters <= PikaJsonStream.MaximumBufferedCharacters + 4096 &&
            bounded.Diagnostics.FramingCharactersScanned <= stream.Length * 64L && bounded.Diagnostics.SyntaxProofCharacters <= stream.Length * 64L,
            "Recovery work or buffered memory grew beyond bounded per-input limits");
        var garbage = new PikaJsonStream();
        Check(!garbage.Push(new string('x', 1_000_000)).Any() && garbage.Diagnostics.MaximumBufferedCharacters <= PikaJsonStream.MaximumBufferedCharacters + 4096 &&
            garbage.Diagnostics.FramingCharactersScanned == 1_000_000, "Unbounded garbage grew memory or caused repeated whole-buffer rescanning");
        Check(garbage.Push(first).Count() == 1, "A bounded overflow prevented later intact telemetry");
        var braces = new PikaJsonStream();
        string braceFlood = new string('{', 16000) + "}";
        Check(!braces.Push(braceFlood).Any() && braces.Diagnostics.SyntaxProofCharacters <= braceFlood.Length * 4L &&
            braces.Diagnostics.MaximumBufferedCharacters <= PikaJsonStream.MaximumBufferedCharacters + 4096,
            "Malformed brace floods caused quadratic suffix encoding/proof work");
    }
}
