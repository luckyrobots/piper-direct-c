using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public enum PikaMonitorConnection { Skip, Connect, AdoptReadOnly }

/// <summary>Automatic observation never takes a live/powered session. Adoption
/// requires a backend attestation, revalidated privately by that backend.</summary>
public static class PikaMonitorConnectionPolicy
{
    public static PikaMonitorConnection Inspect(JsonObject? session)
    {
        if (session is null) return PikaMonitorConnection.Connect;
        if (!IsFalse(session, "connected")) return PikaMonitorConnection.Skip;
        foreach (string flag in new[] { "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" })
            if (session[flag] is JsonValue value && (!value.TryGetValue<bool>(out bool active) || active)) return PikaMonitorConnection.Skip;
        if (session["configuration_blocked"] is not JsonValue blocked || !blocked.TryGetValue<bool>(out bool reserved))
            return PikaMonitorConnection.Skip;
        if (!reserved) return PikaMonitorConnection.Connect;
        // Older backends cannot make this private assertion. Do not turn a
        // stale public snapshot into an unrestricted take_over request.
        return session["read_only_adoptable"] is JsonValue capability && capability.TryGetValue<bool>(out bool mayAdopt) && mayAdopt &&
            new[] { "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" }.All(flag => IsFalse(session, flag))
            ? PikaMonitorConnection.AdoptReadOnly : PikaMonitorConnection.Skip;
    }

    private static bool IsFalse(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) && !flag;
}
