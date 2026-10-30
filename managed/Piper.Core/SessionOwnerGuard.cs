using System.Text.Json;

namespace LuckyRobots.Piper;

/// <summary>Prevents a session-scoped controller from being acquired by another UI.</summary>
public static class SessionOwnerGuard
{
    public const string Denied = "This server belongs to an active Pipers plugin session. Use an externally started dashboard server for shared control.";

    public static bool Allows(string? sessionOwner, string route, JsonElement input)
    {
        if (string.IsNullOrEmpty(sessionOwner) || route == "/api/query") return true;
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("owner", out var owner) &&
            owner.ValueKind == JsonValueKind.String && owner.GetString() == sessionOwner;
    }
}
