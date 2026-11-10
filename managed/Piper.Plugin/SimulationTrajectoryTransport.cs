using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Follow uses only the position-velocity wire transport. The retired
/// streamed-position name is still recognised in saved profiles so that
/// preparation can name it and refuse to arm.</summary>
public static class SimulationTrajectoryTransport
{
    public const string PositionVelocity = "position-velocity";
    public const string StreamedPosition = "streamed-position";

    public static string Name(string? transport) => transport is PositionVelocity or StreamedPosition
        ? transport : throw new InvalidOperationException("Unknown simulation trajectory transport; use position-velocity.");

    // Backends predating the transport field default to position-velocity.
    public static JsonObject StartFields() => new() { ["trajectory"] = true };

    public static void ValidateAcknowledgement(JsonObject acknowledgement)
    {
        if ((acknowledgement["trajectory_transport"]?.GetValue<string>() ?? PositionVelocity) != PositionVelocity)
            throw new InvalidOperationException("The arm backend did not acknowledge the requested trajectory transport; following cannot start.");
    }
}
