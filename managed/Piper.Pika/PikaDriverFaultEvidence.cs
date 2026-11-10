using System.Text.Json;

namespace LuckyRobots.Piper.Pika;

public sealed record PikaSerialReadEvidence(double AgentTimeSeconds, int ByteCount, string PrefixBase64, string SuffixBase64, bool Truncated);
public sealed record PikaSerialWriteEvidence(double StartedSeconds, double? CompletedSeconds, int Command, double Value,
    string PacketBase64, string? ErrorType);
public sealed record PikaMotorEvidence(double AgentTimeSeconds, double PositionRadians, double SpeedRadiansPerSecond,
    string Status, bool Enabled, double? Voltage, double? DriverTemperature, double? MotorTemperature, double? MotorCurrent, double? BusCurrent);
public sealed record PikaDriverFaultSummary(long TransitionOrdinal, double AgentTimeSeconds, string Status, string Description,
    int FaultMask, double? Voltage, double? DriverTemperature, double? MotorTemperature, bool RawEvidenceAvailable);
public sealed record PikaDriverFaultEvidence(long TransitionOrdinal, double AgentTimeSeconds, PikaDriverFaultSummary Summary,
    string RawTelemetryJson, bool RawTelemetryTruncated, IReadOnlyList<PikaMotorEvidence> RecentFeedback,
    IReadOnlyList<PikaSerialReadEvidence> RecentReads, IReadOnlyList<PikaSerialWriteEvidence> RecentWrites);

/// <summary>Passive, bounded wire evidence. Captures literal bytes/JSON without
/// replacing telemetry, changing a fault, retrying a write or owning a transport.</summary>
public sealed class PikaDriverFaultRecorder
{
    private readonly Queue<PikaSerialReadEvidence> reads = new();
    private readonly Queue<WriteAttempt> writes = new();
    private readonly Queue<PikaMotorEvidence> feedback = new();
    private bool previouslyFaulted;
    private long ordinal;
    public PikaDriverFaultSummary? LastSummary { get; private set; }
    public sealed class WriteAttempt
    {
        internal readonly double Started, Value;
        internal readonly int Command;
        internal readonly string Packet;
        internal double? Completed;
        internal string? Error;
        internal WriteAttempt(double at, int command, double value, string packet) { Started = at; Command = command; Value = value; Packet = packet; }
        internal PikaSerialWriteEvidence Snapshot() => new(Started, Completed, Command, Value, Packet, Error);
    }
    public void Read(double now, ReadOnlySpan<byte> bytes)
    {
        bool truncated = bytes.Length > 256;
        reads.Enqueue(new(now, bytes.Length, Convert.ToBase64String(bytes[..Math.Min(bytes.Length, truncated ? 128 : 256)]),
            truncated ? Convert.ToBase64String(bytes[^128..]) : "", truncated));
        while (reads.Count > 16) reads.Dequeue();
    }
    public WriteAttempt BeginWrite(double now, PikaCommand command, double value, ReadOnlySpan<byte> packet)
    {
        var attempt = new WriteAttempt(now, (int)command, value, Convert.ToBase64String(packet));
        writes.Enqueue(attempt); while (writes.Count > 16) writes.Dequeue(); return attempt;
    }
    public static void FinishWrite(WriteAttempt attempt, double now, Exception? error = null)
    { attempt.Completed = now; attempt.Error = error?.GetType().Name; }

    public PikaDriverFaultEvidence? Observe(double now, JsonElement data, double angle, double speed, bool enabled)
    {
        string rawStatus = data.GetProperty("motorstatus").GetProperty("Status").GetString()!;
        return PikaDriverStatus.TryRead(rawStatus, out var driver) ? Observe(now, data, angle, speed, enabled, driver!, rawStatus) : null;
    }

    /// <summary>Records one accepted packet whose status the caller already decoded.</summary>
    public PikaDriverFaultEvidence? Observe(double now, JsonElement data, in PikaTelemetrySample telemetry) =>
        Observe(now, data, telemetry.Position, telemetry.Speed, telemetry.Enabled, telemetry.Driver, telemetry.RawStatus);

    private PikaDriverFaultEvidence? Observe(double now, JsonElement data, double angle, double speed, bool enabled, PikaDriverStatus driver, string rawStatus)
    {
        static double? Number(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
        var status = data.GetProperty("motorstatus"); var motor = data.GetProperty("motor");
        var sample = new PikaMotorEvidence(now, angle, speed, rawStatus, enabled, Number(status, "Voltage"), Number(status, "DriverTemp"),
            Number(status, "MotorTemp"), Number(motor, "Current"), Number(status, "BusCurrent"));
        feedback.Enqueue(sample); while (feedback.Count > 4) feedback.Dequeue();
        bool transition = driver.HasFault && !previouslyFaulted; previouslyFaulted = driver.HasFault;
        if (!transition) return null;
        string raw = data.GetRawText();
        LastSummary = new(++ordinal, now, rawStatus, driver.Text, driver.FaultMask, sample.Voltage,
            sample.DriverTemperature, sample.MotorTemperature, true);
        int length = Math.Min(raw.Length, 4096);
        // Keep the rare event below the existing32768-character pipe limit,
        // including JSON escaping of unexpected non-ASCII firmware text.
        while (length > 0 && JsonSerializer.Serialize(raw[..length]).Length > 8192) length /= 2;
        return new(ordinal, now, LastSummary, raw[..length], length < raw.Length,
            Array.AsReadOnly(feedback.ToArray()), Array.AsReadOnly(reads.ToArray()), Array.AsReadOnly(writes.Select(w => w.Snapshot()).ToArray()));
    }
}
