using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace LuckyRobots.Piper.Pika;

/// <summary>One accepted telemetry packet, decoded once per frame.</summary>
public readonly record struct PikaTelemetrySample(double Position, double Speed, PikaDriverStatus Driver, string RawStatus)
{
    public bool Enabled => Driver.Enabled;
}

/// <summary>Single-byte opcodes of the vendor serial protocol.</summary>
public enum PikaCommand : byte { Disable = 10, Enable = 11, Position = 22 }

public static class PikaProtocol
{
    /// <summary>USB identity of the Pika CH340K adapter (sysfs spelling).</summary>
    public const string UsbVendorId = "1a86", UsbProductId = "7522";
    /// <summary>Windows PnP instance prefix of the same adapter (compare case-insensitively).</summary>
    public const string WindowsInstanceIdPrefix = "USB\\VID_" + UsbVendorId + "&PID_" + UsbProductId + "\\";
    public const int BaudRate = 460800;
    /// <summary>Commandable jaw opening range.</summary>
    public const double MinimumOpeningMm = 0, MaximumOpeningMm = 90;
    /// <summary>Jaw speed at or below which the motor counts as stationary.</summary>
    public const double StationarySpeedRadiansPerSecond = .02;
    /// <summary>A telemetry sample older than this is not fresh evidence.</summary>
    public const double FeedbackFreshSeconds = 2;
    /// <summary>The agent disables an armed gripper whose client heartbeat is older than this.</summary>
    public const double HeartbeatTimeoutSeconds = 1.5;
    /// <summary>True for a finite opening inside the commandable range.</summary>
    public static bool ValidOpening(double millimeters) => double.IsFinite(millimeters) &&
        millimeters >= MinimumOpeningMm && millimeters <= MaximumOpeningMm;

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
        if (!ValidOpening(millimeters)) throw new ArgumentOutOfRangeException(nameof(millimeters));
        double low = 0, high = MaximumAngle;
        for (int n = 0; n < 40; n++) { double middle = (low + high) / 2; if (Width(middle) < millimeters) low = middle; else high = middle; }
        return (low + high) / 2;
    }
    public static byte[] Packet(PikaCommand command, double value = 0)
    {
        if (!Enum.IsDefined(command) || !double.IsFinite(value) || !float.IsFinite((float)value))
            throw new ArgumentException("Unsupported Pika command or value");
        byte[] result = [(byte)command, 0, 0, 0, 0, 13, 10];
        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(1), (float)value);
        return result;
    }
    public static bool Telemetry(JsonElement data, out double position, out double speed, out bool enabled)
    {
        bool valid = TryReadTelemetry(data, out var sample, out _);
        position = sample.Position; speed = sample.Speed; enabled = valid && sample.Driver.Enabled;
        return valid;
    }

    /// <summary>Validates one vendor packet and decodes it once: position, speed
    /// and the parsed driver status. On rejection, <paramref name="rejectionPath"/>
    /// names the first offending known field path.</summary>
    public static bool TryReadTelemetry(JsonElement data, out PikaTelemetrySample sample, out string rejectionPath)
    {
        rejectionPath = ValidateTelemetry(data, out var driver);
        if (rejectionPath.Length > 0) { sample = default; return false; }
        var motor = data.GetProperty("motor");
        sample = new(motor.GetProperty("Position").GetDouble(), motor.GetProperty("Speed").GetDouble(), driver!,
            data.GetProperty("motorstatus").GetProperty("Status").GetString()!);
        return true;
    }

    // Diagnostics classify only known field paths; no telemetry values, unknown
    // property names or authorization data are copied into rejection messages.
    // Returns "" for an accepted packet.
    public static string ValidateTelemetry(JsonElement data) => ValidateTelemetry(data, out _);

    private static string ValidateTelemetry(JsonElement data, out PikaDriverStatus? driver)
    {
        driver = null;
        if (data.ValueKind != JsonValueKind.Object) return "$";
        if (!data.TryGetProperty("motor", out var motor) || motor.ValueKind != JsonValueKind.Object) return "$.motor";
        if (!data.TryGetProperty("motorstatus", out var status) || status.ValueKind != JsonValueKind.Object) return "$.motorstatus";
        // Broad wire-plausibility envelopes, NOT operating/enable limits. Raw
        // faulted encoders may be signed; canonical jaw control still requires
        // ValidAngle separately. Never repair shifted fields or infer power
        // from an otherwise corrupt frame. Any rejected packet renews no age.
        if (!Scalar(motor, "Position", -2 * Math.PI, 2 * Math.PI, required: true)) return "$.motor.Position";
        if (!Scalar(motor, "Speed", -100, 100, required: true)) return "$.motor.Speed";
        if (!Scalar(motor, "Current", -100000, 100000, integral: true)) return "$.motor.Current";
        if (!Scalar(status, "Voltage", 0, 1000)) return "$.motorstatus.Voltage";
        if (!Scalar(status, "DriverTemp", -1000, 1000, integral: true)) return "$.motorstatus.DriverTemp";
        if (!Scalar(status, "MotorTemp", -1000, 1000, integral: true)) return "$.motorstatus.MotorTemp";
        if (!Scalar(status, "BusCurrent", -100000, 100000, integral: true)) return "$.motorstatus.BusCurrent";
        if (!status.TryGetProperty("Status", out var state) || state.ValueKind != JsonValueKind.String ||
            !PikaDriverStatus.TryRead(state.GetString(), out driver)) return "$.motorstatus.Status";
        // A healthy enabled jaw cannot report an arbitrary signed motor turn.
        // Preserve the established closed-reference allowance and GripperLink's
        // 0.25 mm endpoint feedback noise allowance; commands remain 0–90 mm. Fault
        // and disabled packets retain raw signed readings for diagnosis; a
        // genuine fault is never hidden merely by its invalid encoder value.
        if (driver!.Enabled && !driver.HasFault)
        {
            double angle = motor.GetProperty("Position").GetDouble(), width = Width(angle);
            if (angle < -.15 || angle > MaximumAngle || width < -ArrivalToleranceMm || width > MaximumOpeningMm + .25)
                return "$.motor.Position";
        }
        return "";
    }

    private static bool Scalar(JsonElement obj, string name, double minimum, double maximum, bool required = false, bool integral = false)
    {
        // Metadata omitted by older/minimal packets remains optional; a field
        // that is present must have its real type and a plausible whole value.
        if (!obj.TryGetProperty(name, out var value)) return !required;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) &&
            number >= minimum && number <= maximum && (!integral || Math.Truncate(number) == number);
    }
}

public sealed record PikaFramingOverflowSample(int BufferCharacters, int FirstCandidateOffset, int BraceDepth,
    bool InsideString, bool EscapePending, string DecodedPrefixUtf8Base64, string DecodedSuffixUtf8Base64);
public sealed record PikaJsonDiagnostics(long CandidateObjects, long AcceptedObjects, long MalformedObjects,
    long DuplicateKeyObjects, long OverflowResets, IReadOnlyDictionary<string, long> DuplicatePathCounts, string[] LastDuplicatePaths,
    PikaFramingOverflowSample? FirstOverflowSample, long FramingResyncs, long DiscardedPrefixBytes,
    long RecoveryCandidates, long RejectedRecoveryCandidates, long FramingCharactersScanned, long SyntaxProofCharacters, int MaximumBufferedCharacters);

/// <summary>Fragmented vendor JSON, including quoted braces and trailing commas.</summary>
public sealed class PikaJsonStream
{
    public const int MaximumBufferedCharacters = 16384;
    private const int ProofMaximumDepth = MaximumBufferedCharacters + 4096 + 1;
    private readonly StringBuilder buffer = new(MaximumBufferedCharacters + 4096);
    private long candidates, accepted, malformed, duplicateObjects, overflowResets;
    private long resyncs, discardedBytes, recoveryCandidates, rejectedRecoveryCandidates, scanWork, proofWork;
    private int maximumBuffered, scanAt, start = -1, depth, recoveryAt;
    private bool quoted, escape, recoveryMayBeComplete, malformedRecoveryPrefix;
    private readonly Dictionary<string, long> duplicatePaths = new(StringComparer.Ordinal);
    private string[] lastDuplicatePaths = [];
    private PikaFramingOverflowSample? firstOverflowSample;
    public PikaJsonDiagnostics Diagnostics => new(candidates, accepted, malformed, duplicateObjects, overflowResets,
        new Dictionary<string, long>(duplicatePaths), (string[])lastDuplicatePaths.Clone(), firstOverflowSample,
        resyncs, discardedBytes, recoveryCandidates, rejectedRecoveryCandidates, scanWork, proofWork, maximumBuffered);
    public IEnumerable<JsonElement> Push(string text)
    {
        for (int offset = 0; offset < text.Length;)
        {
            int amount = Math.Min(4096, text.Length - offset);
            buffer.Append(text, offset, amount); offset += amount;
            maximumBuffered = Math.Max(maximumBuffered, buffer.Length);
            while (true)
            {
                int end = -1;
                while (scanAt < buffer.Length)
                {
                    int i = scanAt++; ++scanWork;
                    char c = buffer[i];
                    if (c == '}') recoveryMayBeComplete = true;
                    if (start < 0) { if (c != '{') continue; start = i; recoveryAt = i + 1; }
                    if (quoted) { if (escape) escape = false; else if (c == '\\') escape = true; else if (c == '"') quoted = false; }
                    else if (c == '"') quoted = true;
                    else if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) { end = i + 1; break; }
                }
                JsonElement? parsed = null;
                bool syntaxInvalid = false;
                if (end >= 0)
                {
                    ++candidates;
                    try
                    {
                        using var document = JsonDocument.Parse(buffer.ToString(start, end - start), new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 16 });
                        var paths = new HashSet<string>(StringComparer.Ordinal);
                        if (UniqueFields(document.RootElement, "$", paths)) { parsed = document.RootElement.Clone(); ++accepted; }
                        else RecordDuplicates(paths);
                    }
                    catch (JsonException) { ++malformed; syntaxInvalid = true; }
                    // A complete valid generic object (including a duplicate-
                    // rejected object) always wins over nested motor headers.
                    if (!syntaxInvalid)
                    {
                        Consume(end);
                        if (parsed is { } value) yield return value;
                        continue;
                    }
                }
                if (start >= 0 && recoveryMayBeComplete && TryRecover(out var recovered, out int recoveredEnd))
                { Consume(recoveredEnd); yield return recovered; continue; }
                if (end >= 0) { Consume(end); continue; }
                if (buffer.Length > MaximumBufferedCharacters)
                {
                    int length = Math.Min(512, buffer.Length);
                    firstOverflowSample ??= new(buffer.Length, start, depth, quoted, escape,
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(buffer.ToString(0, length))),
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(buffer.ToString(buffer.Length - length, length))));
                    ++overflowResets; Consume(buffer.Length);
                }
                break;
            }
        }
    }

    private void Consume(int count)
    {
        buffer.Remove(0, count); scanAt = 0; start = -1; depth = recoveryAt = 0; quoted = escape = recoveryMayBeComplete = malformedRecoveryPrefix = false;
    }

    private void RecordDuplicates(HashSet<string> paths)
    {
        ++duplicateObjects; lastDuplicatePaths = paths.Order(StringComparer.Ordinal).ToArray();
        foreach (string path in paths) duplicatePaths[path] = duplicatePaths.GetValueOrDefault(path) + 1;
    }

    private bool TryRecover(out JsonElement recovered, out int recoveredEnd)
    {
        recovered = default; recoveredEnd = 0; recoveryMayBeComplete = false;
        string source = buffer.ToString();
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        int byteAt = 0, charAt = 0;
        while (recoveryAt < source.Length)
        {
            int candidate = source.IndexOf('{', recoveryAt);
            if (candidate < 0) { recoveryAt = source.Length; return false; }
            byteAt += Encoding.UTF8.GetByteCount(source.AsSpan(charAt, candidate - charAt)); charAt = candidate;
            int first = candidate + 1;
            while (first < source.Length && source[first] is ' ' or '\t' or '\r' or '\n') ++first;
            ++recoveryCandidates;
            if (first == source.Length) { recoveryAt = candidate; return false; }
            // An object must start with a property name or its closing brace.
            // This avoids allocating an exception for every brace in garbage.
            if (source[first] is not ('"' or '}')) { ++proofWork; recoveryAt = candidate + 1; continue; }
            // The proof depth exceeds every possible nesting in this bounded
            // buffer. A normal depth-policy rejection is NOT syntax proof.
            var reader = new Utf8JsonReader(bytes.AsSpan(byteAt), false, new JsonReaderState(new() { AllowTrailingCommas = true, MaxDepth = ProofMaximumDepth }));
            JsonDocument? complete = null;
            try
            {
                if (!JsonDocument.TryParseValue(ref reader, out complete))
                {
                    // An earlier independently extendable object protects its
                    // nested headers, even while its final brace is missing.
                    proofWork += source.Length - candidate; recoveryAt = candidate; return false;
                }
            }
            catch (JsonException) { proofWork += Math.Max(1, reader.BytesConsumed); recoveryAt = candidate + 1; continue; }
            using (complete)
            {
                proofWork += reader.BytesConsumed;
                int end = candidate + Encoding.UTF8.GetCharCount(bytes.AsSpan(byteAt, checked((int)reader.BytesConsumed)));
                recoveryAt = end; // Never harvest a nested header from a valid wrapper.
                if (!MotorHeader(source, candidate, out int headerEnd)) continue;
                if (!malformedRecoveryPrefix) malformedRecoveryPrefix = MalformedPrefix(source.AsSpan(start, headerEnd - start));
                if (!malformedRecoveryPrefix) continue;
                try
                {
                    using var strict = JsonDocument.Parse(source.AsMemory(candidate, end - candidate), new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 16 });
                    var paths = new HashSet<string>(StringComparer.Ordinal);
                    if (!UniqueFields(strict.RootElement, "$", paths)) { ++rejectedRecoveryCandidates; ++candidates; RecordDuplicates(paths); continue; }
                    if (!PikaProtocol.Telemetry(strict.RootElement, out _, out _, out _)) { ++rejectedRecoveryCandidates; continue; }
                    recovered = strict.RootElement.Clone(); recoveredEnd = end;
                    ++resyncs; ++candidates; ++accepted;
                    discardedBytes += Encoding.UTF8.GetByteCount(source.AsSpan(0, candidate));
                    return true;
                }
                catch (JsonException) { ++rejectedRecoveryCandidates; }
            }
        }
        return false;
    }

    private bool MalformedPrefix(ReadOnlySpan<char> prefix)
    {
        proofWork += prefix.Length;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(prefix.ToString()), false,
            new JsonReaderState(new() { AllowTrailingCommas = true, MaxDepth = ProofMaximumDepth }));
        try { while (reader.Read()) { } return false; }
        catch (JsonException) { return true; }
    }

    private static bool MotorHeader(string source, int start, out int end)
    {
        int i = start + 1;
        while (i < source.Length && source[i] is ' ' or '\t' or '\r' or '\n') ++i;
        const string name = "\"motor\"";
        if (i + name.Length > source.Length || !source.AsSpan(i, name.Length).SequenceEqual(name)) { end = 0; return false; }
        i += name.Length;
        while (i < source.Length && source[i] is ' ' or '\t' or '\r' or '\n') ++i;
        end = i + 1; return i < source.Length && source[i] == ':';
    }

    private static bool UniqueFields(JsonElement element, string path, HashSet<string> duplicates)
    {
        bool unique = true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                string next = KnownPath(path, property.Name);
                if (!names.Add(property.Name)) { unique = false; duplicates.Add(next); }
                if (!UniqueFields(property.Value, next, duplicates)) unique = false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueFields(item, "$.[other]", duplicates)) unique = false;
        return unique;
    }

    private static string KnownPath(string parent, string name) => (parent, name) switch
    {
        ("$", "motor") => "$.motor", ("$", "motorstatus") => "$.motorstatus",
        ("$.motor", "Position" or "Speed" or "Current") => parent + "." + name,
        ("$.motorstatus", "Status" or "Voltage" or "DriverTemp" or "MotorTemp" or "BusCurrent") => parent + "." + name,
        _ => "$.[other]"
    };
}
