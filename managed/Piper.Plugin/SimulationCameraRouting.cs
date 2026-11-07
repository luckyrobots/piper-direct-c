using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Camera UUIDs are stable across Edit/Play copies and source-scene reloads.</summary>
public static class SimulationCameraRouting
{
    public static Preferences Refresh(Preferences preferences, JsonObject discovery)
    {
        string sceneId = discovery["sceneId"]?.GetValue<string>() ?? "";
        if (sceneId.Length == 0 || discovery["cameras"] is not JsonArray cameras)
            return preferences;
        var inventory = cameras.OfType<JsonObject>().Where(camera => Id(camera).Length > 0).ToArray();
        var current = RobotProfileRouting.Capture(preferences);
        var shared = new Dictionary<string, SimulationCameraPreferences>(current.SimulationCameras ?? []);
        Bind(shared, "bench", null, sceneId, inventory);
        var profiles = current.Robots?.Select(profile =>
        {
            var bindings = new Dictionary<string, SimulationCameraPreferences>(profile.SimulationCameras ?? []);
            Bind(bindings, "gripper", profile, sceneId, inventory);
            Bind(bindings, "depth", profile, sceneId, inventory);
            return profile with { SimulationCameras = bindings };
        }).ToArray();
        if (profiles is null || profiles.Length == 0)
            return current with { SimulationCameras = shared };
        var active = profiles.Single(profile => profile.Id == current.ActiveRobotId);
        var visible = new Dictionary<string, SimulationCameraPreferences>(active.SimulationCameras ?? []);
        if (shared.TryGetValue("bench", out var bench))
            visible["bench"] = bench;
        return current with { Robots = profiles, SimulationCameras = visible };
    }

    private static void Bind(Dictionary<string, SimulationCameraPreferences> bindings, string role, RobotProfile? profile,
        string sceneId, JsonObject[] inventory)
    {
        if (bindings.TryGetValue(role, out var saved) && inventory.Any(camera => Id(camera) == saved.CameraId &&
            (profile is null || string.IsNullOrEmpty(camera["robotEntityId"]?.GetValue<string>()) ||
             camera["robotEntityId"]?.GetValue<string>() == profile.RobotId && MatchesJointScope(camera, profile))))
        {
            bindings[role] = saved with { SceneId = sceneId };
            return;
        }
        bindings.Remove(role);
        var candidates = inventory.Where(camera => MatchesRole(camera, role));
        if (profile is not null)
        {
            var owned = candidates.Where(camera => profile.RobotId.Length > 0 && camera["robotEntityId"]?.GetValue<string>() == profile.RobotId &&
                MatchesJointScope(camera, profile)).ToArray();
            candidates = owned.Length > 0 ? owned : candidates.Where(camera =>
                (profile.RobotId.Length == 0 || string.IsNullOrEmpty(camera["robotEntityId"]?.GetValue<string>())) && MatchesProfileName(camera, profile));
        }
        var matches = candidates.Take(2).ToArray();
        // A missing/ambiguous counterpart stays explicit rather than borrowing a
        // different robot's wrist view or guessing the first scene camera.
        if (matches.Length == 1)
            bindings[role] = new(sceneId, Id(matches[0]), saved?.Overlay ?? false);
    }

    public static bool IsAvailable(SimulationCameraPreferences? binding, JsonObject? discovery) =>
        binding is not null && discovery?["sceneId"]?.GetValue<string>() == binding.SceneId &&
        discovery?["cameras"] is JsonArray cameras && cameras.OfType<JsonObject>().Any(camera => Id(camera) == binding.CameraId);

    private static string Id(JsonObject camera) => camera["entityId"]?.GetValue<string>() ?? "";
    private static bool MatchesJointScope(JsonObject camera, RobotProfile profile)
    {
        if (camera["ancestorJointNames"] is not JsonArray ancestors)
            return profile.RobotJointPrefix.Length == 0;
        var names = ancestors.Select(name => name?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(1, 6).All(joint => names.Contains(profile.RobotJointPrefix + "joint" + joint));
    }
    private static string[] Words(string text) => Regex.Split(text.ToLowerInvariant(), "[^a-z0-9]+").Where(word => word.Length > 0).ToArray();
    private static bool MatchesRole(JsonObject camera, string role)
    {
        var words = Words(camera["name"]?.GetValue<string>() ?? "");
        return role switch
        {
            "bench" => words.Contains("lumina") || words.Contains("bench"),
            "gripper" => words.Contains("pika") || words.Contains("fisheye"),
            "depth" => words.Contains("d405") || words.Contains("depth"),
            _ => false
        };
    }
    private static bool MatchesProfileName(JsonObject camera, RobotProfile profile)
    {
        string[] profileWords = Words(profile.Name), nameWords = Words(camera["name"]?.GetValue<string>() ?? "");
        return profileWords.Length > 0 && nameWords.Length >= profileWords.Length &&
            profileWords.SequenceEqual(nameWords.Take(profileWords.Length));
    }
}
