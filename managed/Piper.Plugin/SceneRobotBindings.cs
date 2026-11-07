using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SceneRobotBindingOption(string EntityId, string JointPrefix, string Name, string ModelName = "")
{
    public bool Matches(RobotProfile profile) => profile.RobotId == EntityId && profile.RobotJointPrefix == JointPrefix;
    public string Details => $"Model: {ModelName}\nScene entity: {EntityId}\nJoint namespace: {(JointPrefix.Length == 0 ? "unprefixed" : JointPrefix)}";
}

/// <summary>A scene instance can contain several independently named Piper
/// arms. A binding always includes both the owning instance and exact names.</summary>
public static class SceneRobotBindings
{
    public static string ValidatePrefix(string prefix)
    {
        if (prefix.Length > 64 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new InvalidOperationException("Robot joint namespace must be an explicit simple prefix.");
        return prefix;
    }

    public static SceneRobotBindingOption[] Options(IEnumerable<JsonObject> robots)
    {
        var result = new List<SceneRobotBindingOption>();
        foreach (var robot in robots)
        {
            if (robot["entityId"]?.GetValue<string>() is not { Length: > 0 } id || robot["joints"] is not JsonArray joints) continue;
            var named = joints.OfType<JsonObject>().GroupBy(j => j["name"]?.GetValue<string>() ?? "", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (string name in named.Keys.Where(n => n.EndsWith("joint1", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string prefix = name[..^6];
                try { ValidatePrefix(prefix); } catch (InvalidOperationException) { continue; }
                bool complete = Enumerable.Range(1, 8).All(i => named.TryGetValue(prefix + "joint" + i, out var matches) && matches.Length == 1 &&
                    matches[0]["unit"]?.GetValue<string>() == (i <= 6 ? "rad" : "m"));
                if (!complete) continue;
                string label = robot["name"]?.GetValue<string>() ?? id;
                result.Add(new(id, prefix, "", label));
            }
        }
        // Labels describe individual arms, not the shared physics container.
        // Sorting exact identities makes discovery/joint array order irrelevant;
        // the label is never used to persist or resolve an assignment.
        return result.DistinctBy(r => (r.EntityId, r.JointPrefix))
            .OrderBy(r => r.EntityId, StringComparer.Ordinal).ThenBy(r => r.JointPrefix, StringComparer.Ordinal)
            .Select((r, i) => r with { Name = "Sim arm " + (i + 1) }).ToArray();
    }

    public static SceneRobotBindingOption[] AvailableFor(IEnumerable<SceneRobotBindingOption> options, IEnumerable<RobotProfile> profiles, RobotProfile profile)
    {
        var others = profiles.Where(p => p.Id != profile.Id && p.RobotId.Length > 0).ToArray();
        // Keep a saved selection visible even if an older file contains a
        // conflict, so it can be explicitly unassigned instead of silently lost.
        return options.Where(option => option.Matches(profile) || !others.Any(option.Matches)).ToArray();
    }
}
