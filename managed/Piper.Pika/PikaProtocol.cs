using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Pika;

public static class PikaProtocol
{
    // Model-derived opening tolerance; not an independent jaw calibration.
    public const double ArrivalToleranceMm = 1.0;
    private const double MaximumAngle = (180 - 43.99) * Math.PI / 180;
    private static double Linkage(double angle)
    {
        double a = MaximumAngle - angle, h = .0325 * Math.Sin(a);
        return (Math.Sqrt(.058 * .058 - (h - .01456) * (h - .01456)) + .0325 * Math.Cos(a)) * 1000;
    }
    public static double Width(double angle) => 2 * (Linkage(angle) - Linkage(0));
    public static bool ValidAngle(double angle) => double.IsFinite(angle) && angle >= 0 && angle <= MaximumAngle;
    public static double Angle(double millimeters)
    {
        if (!double.IsFinite(millimeters) || millimeters < 0 || millimeters > 90)
            throw new ArgumentOutOfRangeException(nameof(millimeters));
        double low = 0, high = MaximumAngle;
        for (int n = 0; n < 40; n++) { double middle = (low + high) / 2; if (Width(middle) < millimeters) low = middle; else high = middle; }
        return (low + high) / 2;
    }
    public static byte[] Packet(int command, double value = 0)
    {
        if (command is not (10 or 11 or 22) || !double.IsFinite(value) || !float.IsFinite((float)value))
            throw new ArgumentException("Unsupported Pika command or value");
        byte[] result = [(byte)command, 0, 0, 0, 0, 13, 10];
        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(1), (float)value);
        return result;
    }
    public static bool Telemetry(JsonElement data, out double position, out double speed, out bool enabled)
    {
        position = speed = 0; enabled = false;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("motor", out var motor) || !data.TryGetProperty("motorstatus", out var status) ||
            motor.ValueKind != JsonValueKind.Object || status.ValueKind != JsonValueKind.Object ||
            !motor.TryGetProperty("Position", out var p) || !motor.TryGetProperty("Speed", out var s) ||
            p.ValueKind != JsonValueKind.Number || s.ValueKind != JsonValueKind.Number ||
            !p.TryGetDouble(out position) || !s.TryGetDouble(out speed) || !double.IsFinite(position) || !double.IsFinite(speed)) return false;
        if (!status.TryGetProperty("Status", out var state)) return false;
        string? raw = state.ValueKind == JsonValueKind.String ? state.GetString() : null;
        if (raw is null || !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int bits)) return false;
        enabled = (bits & 0x40) != 0;
        return true;
    }
}

/// <summary>Fragmented vendor JSON, including quoted braces and trailing commas.</summary>
public sealed class PikaJsonStream
{
    private readonly StringBuilder buffer = new();
    public IEnumerable<JsonElement> Push(string text)
    {
        buffer.Append(text);
        while (true)
        {
            int start = -1, end = -1, depth = 0; bool quoted = false, escape = false;
            for (int i = 0; i < buffer.Length; i++)
            {
                char c = buffer[i];
                if (start < 0) { if (c != '{') continue; start = i; }
                if (quoted) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') quoted = false; }
                else if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { end = i + 1; break; }
            }
            if (end < 0) { if (buffer.Length > 16384) buffer.Clear(); yield break; }
            string raw = buffer.ToString(start, end - start); buffer.Remove(0, end);
            JsonElement? parsed = null;
            try
            {
                using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 16 });
                // Some vendor packets repeat temperature/status fields. Do not
                // pick an arbitrary status or publish ambiguous JSON to clients.
                // Ignore this packet; only a subsequent valid one refreshes age.
                if (UniqueFields(document.RootElement)) parsed = document.RootElement.Clone();
            }
            catch (JsonException) { }
            if (parsed is { } value) yield return value;
        }
    }

    private static bool UniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueFields(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueFields(item)) return false;
        return true;
    }
}
