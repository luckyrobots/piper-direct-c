using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // Store each rendered list separately: filtering out other robots' arms
    // must never reinterpret a dropdown index using another robot's choices.
    private Dictionary<string, SceneRobotBindingOption[]> settingsRobotChoices = new(StringComparer.Ordinal);

    private Widget SceneArmAssignment(RobotProfile profile, SceneRobotBindingOption[] robots)
    {
        var selected = robots.SingleOrDefault(r => r.Matches(profile));
        bool conflict = profile.RobotId.Length > 0 && preferences.Robots!.Any(p => p.Id != profile.Id &&
            p.RobotId == profile.RobotId && p.RobotJointPrefix == profile.RobotJointPrefix);
        string description = selected?.Name ?? (profile.RobotId.Length > 0 ? "Assigned arm is not in this scene" : "No simulation arm assigned");
        return Widget.Label("global.robot." + profile.Id + ".scene.assignment", profile.Name + " → " + description +
            (conflict ? " · already assigned to another robot" : "")) with {
                Appearance = "muted", Height = 48,
                Hint = selected?.Details ?? "Choose the one simulated arm that represents this real robot. Each simulated arm can be assigned once." };
    }
}
