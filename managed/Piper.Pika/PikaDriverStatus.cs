using System.Globalization;

namespace LuckyRobots.Piper.Pika;

/// <summary>AgileX pika_sdk API_Doc status bits:00 disabled,01 undervoltage,
///02 motor temperature,04 overcurrent,08 driver temperature,10 sensor,
///20 driver error,40 enabled,80 homed. Fault and power bits are independent.</summary>
public sealed record PikaDriverStatus(int Bits)
{
    public bool Enabled => (Bits & 0x40) != 0;
    public int FaultMask => Bits & 0x3f;
    public bool HasFault => FaultMask != 0;
    public string Text
    {
        get
        {
            var faults = new List<string>();
            if ((Bits & 0x01) != 0) faults.Add("low voltage");
            if ((Bits & 0x02) != 0) faults.Add("motor over-temperature");
            if ((Bits & 0x04) != 0) faults.Add("motor overcurrent");
            if ((Bits & 0x08) != 0) faults.Add("driver over-temperature");
            if ((Bits & 0x10) != 0) faults.Add("abnormal sensor state");
            if ((Bits & 0x20) != 0) faults.Add("driver error");
            string state = HasFault ? "Pika driver fault: " + string.Join(", ", faults) : Enabled ? "Pika driver enabled" : "Pika driver disabled";
            return state + $" (status 0x{Bits:X2})";
        }
    }
    public static bool TryRead(string? text, out PikaDriverStatus? status)
    {
        status = null;
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bits) || bits is < 0 or > 255) return false;
        status = new(bits); return true;
    }
}
