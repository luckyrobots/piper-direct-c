using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Teaching belongs to the controller, not to the dashboard motion lease.</summary>
public sealed record TeachingState(string Phase, bool Active, bool ReturnRequired, bool CanReturn, string Label, string NextStep)
{
    public bool HandGuiding => Phase == "hand-guiding";
    public bool BlocksTargets => Active || ReturnRequired;

    public static TeachingState Inspect(JsonObject? device)
    {
        if (device?["fresh"]?.GetValue<bool>() != true)
            return new("unknown", false, false, false, "Waiting for teaching-state feedback", "Keep the current support/clearance until live controller feedback returns.");
        int control = device["status"]?["control"]?.GetValue<int>() ?? -1;
        int teach = device["status"]?["teach_state"]?.GetValue<int>() ?? -1;
        string phase = device["teaching"]?["phase"]?.GetValue<string>() ?? (teach switch
        {
            1 => "hand-guiding", 3 or 5 or 7 => "playback", 4 => "playback-paused",
            _ when control == 2 => "ended", _ => "off"
        });
        bool active = phase is "hand-guiding" or "playback" or "playback-paused";
        bool returning = device["teaching"]?["return_required"]?.GetValue<bool>() ?? phase == "ended";
        bool canReturn = device["teaching"]?["can_return"]?.GetValue<bool>() == true;
        return phase switch
        {
            "hand-guiding" => new(phase, true, true, false, "Hand guiding · real robot leads the simulation",
                "Move the arm by hand; measured scene tracking stays live. Single-click the physical teach button to finish. A new onboard recording replaces its previous trajectory."),
            "playback" or "playback-paused" => new(phase, true, true, false,
                phase == "playback" ? "Onboard trajectory playback · plugin monitoring only" : "Onboard trajectory playback paused · plugin monitoring only",
                "The robot's controller owns this trajectory. End playback with the physical/vendor controls before returning to CAN. The plugin never starts replay automatically."),
            "ended" => new(phase, false, returning, canReturn, "Teaching ended · return to CAN control required",
                device["teaching"]?["handoff_in_progress"]?.GetValue<bool>() == true ? "Returning through standby to CAN; waiting for fresh controller acknowledgements. No movement or enable command is sent."
                : (device["teaching"]?["return_reason"]?.GetValue<string>() is { Length: > 0 } why ? why + " " : "") +
                    "If needed, single-click to hand-guide again, support the arm and bring every joint to its physical zero pose using the live readouts. Then single-click to turn the teach light off. Return to CAN uses one explicit confirmation; no automatic reset, homing or replay is sent."),
            "off" => new(phase, false, returning, canReturn, returning ? "Return from teaching is pending" : "CAN / standby · teaching inactive",
                returning ? "Complete the supported return-to-CAN procedure before enabling controls." : "Joint targets are available after Enable robot. Hand guiding and playback are detected from live controller state."),
            _ => new(phase, active, true, false, "Unrecognized teaching state · monitoring only", "Use the physical/vendor controls to end teaching. No automatic reset or mode change is sent.")
        };
    }
}
