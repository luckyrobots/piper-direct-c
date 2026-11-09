namespace LuckyRobots.Piper.Pika;

/// <summary>The one convention for where a gripper's Linux serial device lives.
/// <list type="bullet">
/// <item><c>null</c> means native Linux: the backend itself runs on Linux.</item>
/// <item>A non-empty name means that WSL distribution, on Windows.</item>
/// </list>
/// The empty string is never a distribution, and none is implied: a Windows
/// caller must name one explicitly (there is no implicit "Ubuntu").</summary>
public static class PikaDistribution
{
    public const int MaximumNameLength = 128;

    /// <summary>Projects a profile's WSL setting onto the running OS: the
    /// normalized name on Windows, and <c>null</c> (native Linux) elsewhere.</summary>
    public static string? ForCurrentPlatform(string? wslDistribution) =>
        OperatingSystem.IsWindows() ? Normalize(wslDistribution) : null;

    /// <summary>Maps the legacy empty or whitespace spelling to <c>null</c>.</summary>
    public static string? Normalize(string? distribution) => string.IsNullOrWhiteSpace(distribution) ? null : distribution;

    /// <summary>WSL distribution names compare case-insensitively; native Linux equals only native Linux.</summary>
    public static bool Same(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Text form for persisted records and composite string keys only:
    /// <c>""</c> for native Linux. Never pass the result back as a distribution;
    /// read it with <see cref="Normalize"/>.</summary>
    public static string ToKeyText(string? distribution) => Normalize(distribution) ?? "";

    /// <summary>The snapshot <c>transport</c> name for a configured device.</summary>
    public static string Transport(string? distribution) => distribution is null ? "linux-serial" : "wsl-linux-serial";

    /// <summary>Boundary validation for operator and HTTP input. Native Linux
    /// (<c>null</c>) is rejected on Windows, where WCH COM serial is disabled.</summary>
    public static void Validate(string? distribution)
    {
        if (distribution is not null && (distribution.Length is 0 or > MaximumNameLength || distribution.Any(char.IsControl)))
            throw new ArgumentException("Invalid WSL distribution name.");
        if (OperatingSystem.IsWindows() && distribution is null)
            throw new InvalidOperationException("Native Windows WCH serial is disabled. Choose a WSL distribution.");
    }
}
