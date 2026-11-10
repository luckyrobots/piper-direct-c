using System.Text.Json;

namespace LuckyRobots.Piper;

/// <summary>Prevents a session-scoped controller from being acquired by another UI.
/// Which routes acquire nothing (reading back and stopping) is the API definition's
/// statement, so any authenticated caller may use those. A path the definition does
/// not know is treated as owned.</summary>
public static class SessionOwnerGuard
{
    public const string Denied = "This server belongs to an active Pipers plugin session. Use an externally started dashboard server for shared control.";

    public static bool Allows(string? sessionOwner, string route, JsonElement input)
    {
        if (string.IsNullOrEmpty(sessionOwner) || PiperApi.ManagedControl(route) is { SessionOwned: false }) return true;
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.String && owner.GetString() == sessionOwner;
    }
}
