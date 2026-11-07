using System.Text.Json;

namespace LuckyRobots.Piper.Host;

internal static class ControlJson
{
    internal static bool HasUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
                if (!names.Add(member.Name) || !HasUniqueKeys(member.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!HasUniqueKeys(child)) return false;
        return true;
    }
}
