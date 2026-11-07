using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class PikaMonitorConnectionTests
{
    public static void Run(Action<bool, string> check)
    {
        check(PikaMonitorConnectionPolicy.Inspect(null) == PikaMonitorConnection.Connect, "Unregistered assigned Pika never started its passive monitor.");
        var normal = new JsonObject { ["connected"] = false, ["configuration_blocked"] = false };
        check(PikaMonitorConnectionPolicy.Inspect(normal) == PikaMonitorConnection.Connect, "Released gripper stopped connecting read-only.");
        var abandoned = new JsonObject { ["connected"] = false, ["fresh"] = false, ["owner"] = "old-plugin",
            ["configuration_blocked"] = true, ["read_only_adoptable"] = true,
            ["enabled"] = false, ["armed"] = false, ["pending"] = false, ["following"] = false,
            ["power_requested"] = false, ["power_uncertain"] = false };
        check(PikaMonitorConnectionPolicy.Inspect(abandoned) == PikaMonitorConnection.AdoptReadOnly,
            "A privately attested dead unpowered monitor stayed blocked after plugin restart.");
        foreach (string flag in new[] { "connected", "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" })
        {
            var unsafeState = abandoned.DeepClone().AsObject(); unsafeState[flag] = true;
            check(PikaMonitorConnectionPolicy.Inspect(unsafeState) == PikaMonitorConnection.Skip, "Automatic monitor stole an unsafe session: " + flag);
            unsafeState[flag] = "false";
            check(PikaMonitorConnectionPolicy.Inspect(unsafeState) == PikaMonitorConnection.Skip, "Malformed adoption flag granted takeover: " + flag);
        }
        foreach (string flag in new[] { "read_only_adoptable", "enabled", "armed", "pending", "following", "power_requested", "power_uncertain" })
        {
            var unsupported = abandoned.DeepClone().AsObject(); unsupported.Remove(flag);
            check(PikaMonitorConnectionPolicy.Inspect(unsupported) == PikaMonitorConnection.Skip,
                "Missing backend proof granted an automatic takeover: " + flag);
        }
        var oldBackend = abandoned.DeepClone().AsObject(); oldBackend["read_only_adoptable"] = false;
        check(PikaMonitorConnectionPolicy.Inspect(oldBackend) == PikaMonitorConnection.Skip, "A backend that denied adoption received an automatic takeover.");
        foreach (string field in new[] { "read_only_adoptable", "configuration_blocked" })
        {
            var malformed = abandoned.DeepClone().AsObject(); malformed[field] = "true";
            check(PikaMonitorConnectionPolicy.Inspect(malformed) == PikaMonitorConnection.Skip, "Malformed backend reservation/capability granted adoption.");
        }
    }
}
