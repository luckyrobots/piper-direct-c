using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The controller's teaching mode. Off and Ended are CAN-owned; HandGuiding,
/// Playback and PlaybackPaused are owned by the physical controller; Stopped is a
/// latched controller STOP whose teach byte may be stale; Unknown means no fresh feedback.</summary>
public enum TeachingPhase { Unknown, Off, HandGuiding, Playback, PlaybackPaused, Ended, Stopped, Unrecognized }

/// <summary>Teaching belongs to the controller, not to the dashboard motion lease.</summary>
public sealed record TeachingState(TeachingPhase Phase, bool Active, bool ReturnRequired, bool CanReturn, string Label, string NextStep)
{
    public bool HandGuiding => Phase == TeachingPhase.HandGuiding;
    public bool BlocksTargets => Active || ReturnRequired || Phase == TeachingPhase.Stopped;
    public bool RequiresZero { get; init; } = true;
    public bool SamePoseReturn => !RequiresZero;
    public string ReturnConfirmation => SamePoseReturn
        ? "Keep the teach light OFF and hold still. Return to CAN will hold the measured pose and discard previous targets. It will not home, replay or change motor power."
        : "Confirm the physical arm is at zero, the teach light is OFF and the workspace is clear. Support the arm during the standby/CAN handoff.";
    internal ArmCommandFields ReturnFlags() => SamePoseReturn
        ? new(ButtonOff: true, SamePoseConfirmed: true)
        : new(ButtonOff: true, ZeroPoseConfirmed: true);

    public static TeachingState Inspect(JsonObject? device)
    {
        if (device?["fresh"]?.GetValue<bool>() != true)
            return new(TeachingPhase.Unknown, false, false, false, "Waiting for teaching-state feedback", "Keep the current support/clearance until live controller feedback returns.");
        int control = device["status"]?["control"]?.GetValue<int>() ?? -1;
        int teach = device["status"]?["teach_state"]?.GetValue<int>() ?? -1;
        if (device["status"]?["arm_status"]?.GetValue<int>() == 1)
            return Stopped(); // Older backends can still mislabel the stale teach byte as active.
        // The backend's qualified phase wins; older backends report only the raw bytes.
        TeachingPhase phase = device["teaching"]?["phase"]?.GetValue<string>() is { } reported ? reported switch
        {
            "hand-guiding" => TeachingPhase.HandGuiding, "playback" => TeachingPhase.Playback,
            "playback-paused" => TeachingPhase.PlaybackPaused, "ended" => TeachingPhase.Ended, "off" => TeachingPhase.Off,
            "stopped" => TeachingPhase.Stopped, _ => TeachingPhase.Unrecognized
        } : teach switch
        {
            1 => TeachingPhase.HandGuiding, 3 or 5 or 7 => TeachingPhase.Playback, 4 => TeachingPhase.PlaybackPaused,
            _ when control == 2 => TeachingPhase.Ended, _ => TeachingPhase.Off
        };
        bool active = phase is TeachingPhase.HandGuiding or TeachingPhase.Playback or TeachingPhase.PlaybackPaused;
        bool returning = device["teaching"]?["return_required"]?.GetValue<bool>() ?? phase == TeachingPhase.Ended;
        bool canReturn = device["teaching"]?["can_return"]?.GetValue<bool>() == true;
        // Trust only the backend's firmware-qualified strategy. Old backends
        // and absent/unknown capabilities retain the legacy zero requirement.
        bool samePose = device["teaching"]?["seamless_supported"]?.GetValue<bool>() == true &&
            device["teaching"]?["return_requires_zero"]?.GetValue<bool>() == false &&
            device["teaching"]?["return_strategy"]?.GetValue<string>() == "measured-pose";
        TeachingState result = phase switch
        {
            TeachingPhase.Stopped => Stopped(),
            TeachingPhase.HandGuiding => new(phase, true, true, false, "Hand guiding · real robot leads the simulation",
                "Move the arm by hand; measured scene tracking stays live. Single-click the physical teach button to finish. A new onboard recording replaces its previous trajectory."),
            TeachingPhase.Playback or TeachingPhase.PlaybackPaused => new(phase, true, true, false,
                phase == TeachingPhase.Playback ? "Onboard trajectory playback · plugin monitoring only" : "Onboard trajectory playback paused · plugin monitoring only",
                "The robot's controller owns this trajectory. End playback with the physical/vendor controls before returning to CAN. The plugin never starts replay automatically."),
            TeachingPhase.Ended => new(phase, false, returning, canReturn, "Hand guiding off · resume controls when ready",
                device["teaching"]?["handoff_in_progress"]?.GetValue<bool>() == true ? (samePose
                    ? "Holding the measured pose while CAN control is acknowledged. Previous targets are discarded; no power change, homing or replay."
                    : "Returning through standby to CAN; waiting for fresh controller acknowledgements. No movement or enable command is sent.")
                : (samePose ? "Hand guiding has ended. Click Resume controls once, then hold the arm still while it reconnects at this measured pose; there is no need to move to zero. Previous movement will not resume."
                    : "If needed, single-click to hand-guide again, support the arm and bring every joint to its physical zero pose using the live readouts. Then single-click to turn the teach light off. Return to CAN uses one explicit confirmation; no automatic reset, homing or replay is sent.")),
            TeachingPhase.Off => new(phase, false, returning, canReturn, returning ? "Return from teaching is pending" : "CAN / standby · teaching inactive",
                returning ? "Complete the supported return-to-CAN procedure before enabling controls." : "Joint targets are available after Enable robot. Hand guiding and playback are detected from live controller state."),
            _ => new(phase, active, true, false, "Unrecognized teaching state · monitoring only", "Use the physical/vendor controls to end teaching. No automatic reset or mode change is sent.")
        };
        return result with { RequiresZero = !samePose };
    }

    private static TeachingState Stopped() => new(TeachingPhase.Stopped, false, false, false, "Controller stopped",
        "The controller STOP is still latched; the teach flag may be left over. Support the arm and use the displayed reset-assisted recovery. Toggling the teach button does not clear this STOP.");
}
