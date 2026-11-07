using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Explicit experimental wire-transport selection. This never changes
/// joint limits, position targets or the native reference/obstruction guards.</summary>
public static class SimulationTrajectoryTransport
{
    public const string PositionVelocity = "position-velocity";
    public const string StreamedPosition = "streamed-position";

    public static string Name(string? transport) => transport is PositionVelocity or StreamedPosition
        ? transport : throw new InvalidOperationException("Unknown simulation trajectory transport; use position-velocity or streamed-position.");

    public static string Validate(string? transport, JsonObject? snapshot, string deviceId)
    {
        string name = Name(transport);
        if (name == PositionVelocity) return name;
        var device = snapshot?["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == deviceId);
        if (snapshot?["streamed_position_transport_supported"]?.GetValue<bool>() != true ||
            snapshot["streamed_position_wire_validation_supported"]?.GetValue<bool>() != true ||
            snapshot["selected"]?.GetValue<string>() != deviceId || device?["streamed_position_supported"]?.GetValue<bool>() != true ||
            device["hardware"]?.GetValue<string>() != "H-V1.2-1" || device["firmware"]?.GetValue<string>() is not ("S-V1.9-0" or "S-V1.9-1"))
            throw new InvalidOperationException("Experimental streamed-position following requires the updated backend and reviewed H-V1.2-1 / S-V1.9-0 or S-V1.9-1 firmware on the assigned robot.");
        return name;
    }

    public static JsonObject StartFields(string transport)
    {
        var fields = new JsonObject { ["trajectory"] = true };
        // Default PV remains compatible with backends predating this experiment.
        if (Name(transport) == StreamedPosition) fields["trajectory_transport"] = StreamedPosition;
        return fields;
    }

    public static void ValidateAcknowledgement(string transport, JsonObject acknowledgement)
    {
        string expected = Name(transport);
        string actual = acknowledgement["trajectory_transport"]?.GetValue<string>() ?? PositionVelocity;
        if (actual != expected || expected == StreamedPosition && acknowledgement["trajectory_transport_validated"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The arm backend did not acknowledge the requested trajectory transport; following cannot start.");
    }
}
