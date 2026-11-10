using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class TeachingResetAssisted
{
    /// <summary>During a confirmed reset the MCU briefly stops reporting fresh
    /// samples while the same adapter and CAN generation stay connected.</summary>
    public static bool ExpectedFeedbackGap(JsonObject snapshot, string deviceId, long generation)
    {
        if (snapshot["selected"]?.GetValue<string>() != deviceId) return false;
        var device = snapshot["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == deviceId);
        return device?["generation"]?.GetValue<long>() == generation && device["present"]?.GetValue<bool>() == true &&
            device["up"]?.GetValue<bool>() == true && device["fresh"]?.GetValue<bool>() == false;
    }
}
