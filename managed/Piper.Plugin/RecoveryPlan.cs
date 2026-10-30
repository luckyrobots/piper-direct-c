using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record RecoveryPlan(string Diagnosis, string NextStep, string? Action)
{
    public static RecoveryPlan Inspect(JsonObject device, bool moving)
    {
        var incident = device["stop_event"];
        string diagnosis = incident?["reason"]?.GetValue<string>() ?? device["stop_reason"]?.GetValue<string>() ?? "Controller recovery is required.";
        int jointNumber = incident?["joint"]?.GetValue<int>() ?? 0;
        if (jointNumber is >= 1 and <= 6 && incident?["joints"] is JsonArray evidence && evidence.Count >= jointNumber)
        {
            var joint = evidence[jointNumber - 1];
            double? actual = joint?["actual_deg"]?.GetValue<double>();
            double? command = joint?["command_deg"]?.GetValue<double>();
            if (actual.HasValue && command.HasValue)
                diagnosis += $" Recorded tracking gap: {Math.Abs(command.Value - actual.Value):F3}°. This is a position-based suspicion, not confirmed contact.";
        }
        if (moving || device["fresh"]?.GetValue<bool>() != true)
            return new(diagnosis, "Waiting for fresh, stationary feedback. No reset or movement will be sent.", null);

        var status = device["status"];
        var joints = device["joints"]?.AsArray();
        if (status is null || joints?.Count != 6 || joints.Any(j => j?["fresh"]?.GetValue<bool>() != true))
            return new(diagnosis, "Waiting for all six joint measurements before choosing a recovery step.", null);
        if ((status["stationary_s"]?.GetValue<double>() ?? 0) < .4)
            return new(diagnosis, "Wait for the arm to stop. Camera previews remain available for inspection.", null);
        if (status["error"]?.GetValue<int>() != 0 || joints.Any(j => j?["fault"]?.GetValue<int>() != 0))
            return new(diagnosis, "A controller/driver fault is still present. Inspect the arm and fault information; automatic reset is withheld.", null);
        int teach = status["teach_state"]?.GetValue<int>() ?? -1;
        if (teach is not (0 or 2 or 6))
            return new(diagnosis, "End the active teaching mode using the vendor procedure before recovery.", null);
        int armStatus = status["arm_status"]?.GetValue<int>() ?? -1;
        int control = status["control"]?.GetValue<int>() ?? -1;
        int mode = status["motion_mode"]?.GetValue<int>() ?? -1;
        bool allOff = joints.All(j => j?["enabled"]?.GetValue<bool>() == false);
        bool allOn = joints.All(j => j?["enabled"]?.GetValue<bool>() == true);
        bool healthy = armStatus == 0 && control is >= 0 and <= 1 && mode is >= 0 and <= 1 && teach is 0 or 2;
        if (device["reset_pending"]?.GetValue<bool>() == true)
            return healthy && allOff
                ? new(diagnosis, "Reset completed; verify fresh disabled-motor feedback. No movement will resume.", "verify-reset")
                : new(diagnosis, "Waiting for the reset to finish with all motors off. Do not resend reset.", null);
        if (healthy && (allOff || allOn))
            return new(diagnosis, "Controller is healthy and stationary. Verify the current state without changing motor power or position.", "verify-current");
        if (armStatus == 1 && control is >= 0 and <= 2 && mode is >= 0 and <= 1)
            return new(diagnosis, "Clear the obstruction and support the arm. Recovery resets the controller and can remove holding torque, then verifies automatically. It will not retry the move or enable motors.", "reset");
        return new(diagnosis, $"Controller status {armStatus}, mode {mode}, control {control}: inspect before recovery. No automatic motion is available.", null);
    }
}
