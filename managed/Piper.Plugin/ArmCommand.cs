namespace LuckyRobots.Piper.Plugin;

/// <summary>The part of one arm command that its caller chooses. Identity, owner, operator
/// confirmations, speed and tolerance belong to the command gate, which adds them when it
/// builds the request (<see cref="PiperApi.Requests.Command"/>).</summary>
internal readonly record struct ArmCommandFields(long? Joint = null, ApiNumbers? Degrees = null, bool? Synchronized = null,
    long? RequestId = null, bool? ButtonOff = null, bool? SamePoseConfirmed = null, bool? ZeroPoseConfirmed = null);

/// <summary>The operator confirmations one arm command carries. A flag that is not sent is
/// null: each flag is a gate, and sending it on every request would make the gate meaningless.</summary>
internal readonly record struct OperatorConfirmations(bool Standalone, bool? Clear, bool? Supported, bool? ResetCompleted)
{
    /// <summary>Only the confirmations that the controller's admission of
    /// <paramref name="action"/> reads (src/controller.c).</summary>
    public static OperatorConfirmations For(string action, bool resetConfirmed) => new(
        // Every /api/command admission requires the standalone acknowledgement.
        Standalone: true,
        // Power release and recovery verification do not read the clear-path flag.
        Clear: action is not ("disable" or "verify-reset" or "verify-disabled") ? true : null,
        // Motor-power and recovery transitions require a supported arm.
        Supported: action is "enable" or "disable" or "reset" or "verify-reset" or "verify-disabled" or "verify-current" or "teach-return" or "recover-at-pose" ? true : null,
        // Only the operator's explicit answer to the reset question confirms that a reset completed.
        ResetCompleted: resetConfirmed && action == "verify-reset" ? true : null);
}
