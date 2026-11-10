using System.Text.Json;

namespace LuckyRobots.Piper.Host;

/// <summary>What the gripper leg of a coordinated STOP does.</summary>
internal enum GripperStopLeg
{
    /// <summary>Hold the one gripper the request names.</summary>
    HoldNamed,
    /// <summary>Hold every gripper, whoever owns it.</summary>
    HoldAll,
    /// <summary>A cancel that names an arm but no gripper: nothing is held and the leg fails.</summary>
    RefuseCancel
}

/// <summary>The gripper leg of one coordinated STOP request, classified from which fields
/// the request carries. Every request classifies: no field is required, so an emergency is
/// never refused for what it left out. A cancel (an arm with a request_id) is a client's
/// cleanup of its own action and holds only the gripper it names. Everything else is an
/// emergency: it holds the gripper it names, or every gripper when it names none, and
/// <see cref="Degraded"/> says why when the scope it asked for could not be honoured.</summary>
internal readonly record struct CoordinatedStopScope(GripperStopLeg Leg, string? Degraded)
{
    internal const string CancelNeedsGripper = "A scoped STOP requires an explicit pika_device to verify the gripper hold.";

    internal static CoordinatedStopScope Of(JsonElement request)
    {
        bool hasArm = request.TryGetProperty("device_id", out var arm);
        bool scoped = hasArm && arm.ValueKind == JsonValueKind.String && arm.GetString()!.Length > 0;
        bool hasRequest = request.TryGetProperty("request_id", out _);
        bool namesGripper = request.TryGetProperty("pika_device", out var gripper) &&
            gripper.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(gripper.GetString());
        if (scoped && hasRequest && !namesGripper) return new(GripperStopLeg.RefuseCancel, null);
        string? degraded =
            hasArm && arm.ValueKind != JsonValueKind.String ? "device_id is not an adapter identity" :
            !scoped && (hasRequest || request.TryGetProperty("generation", out _)) ? "generation or request_id came without device_id" :
            scoped && !namesGripper ? "it names an arm but no pika_device" : null;
        return new(scoped && namesGripper ? GripperStopLeg.HoldNamed : GripperStopLeg.HoldAll, degraded);
    }
}
