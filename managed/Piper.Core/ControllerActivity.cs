using System.Text.Json.Nodes;

namespace LuckyRobots.Piper;

/// <summary>Activity belongs to physical devices, independently of UI selection.</summary>
public static class ControllerActivity
{
    public static bool AnyMotion(JsonNode? state) => state?["any_motion_active"]?.GetValue<bool>() == true ||
        state?["motion"]?["active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["active"]?.GetValue<bool>() == true) ||
        state?["devices"] is JsonArray devices && devices.Any(device => device?["motion"]?["active"]?.GetValue<bool>() == true);

    public static bool AnyQuery(JsonNode? state) => state?["any_query_active"]?.GetValue<bool>() == true ||
        state?["query_active"]?.GetValue<bool>() == true ||
        state?["motions"] is JsonArray lanes && lanes.Any(lane => lane?["query_active"]?.GetValue<bool>() == true) ||
        state?["devices"] is JsonArray devices && devices.Any(device => device?["motion"]?["query_active"]?.GetValue<bool>() == true);
}
