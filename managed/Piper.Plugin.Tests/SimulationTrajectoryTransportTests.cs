using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationTrajectoryTransportTests
{
    public static void Run()
    {
        const string pv = SimulationTrajectoryTransport.PositionVelocity, js = SimulationTrajectoryTransport.StreamedPosition;
        var start = new FollowLaneController("backend", "owner", new RobotProfile("profile", "Profile", "can"), 1,
            (_, _, _) => Task.FromResult(new JsonObject())).StartArm().Request.Body;
        Check(!start.ContainsKey("trajectory_transport") && start["trajectory"]!.GetValue<bool>(),
            "Position-velocity admission acquired a new backend requirement or lost its trajectory flag");
        foreach (string? invalid in new[] { null, "", "mit", "streamed", "Streamed-position" })
        {
            bool rejected = false;
            try { SimulationTrajectoryTransport.Name(invalid); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "An unknown or ambiguous trajectory transport selected a wire mode");
        }
        SimulationTrajectoryTransport.ValidateAcknowledgement(new());
        SimulationTrajectoryTransport.ValidateAcknowledgement(new() { ["trajectory_transport"] = pv });
        foreach (string actual in new[] { js, "other" })
        {
            bool rejected = false;
            try { SimulationTrajectoryTransport.ValidateAcknowledgement(new() { ["trajectory_transport"] = actual }); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "The backend acknowledged a different wire transport");
        }
        var preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
            [new("a", "Robot A", SimulationTransport: js), new("b", "Robot B")], ActiveRobotId: "a"));
        Check(preferences.SimulationTransport == js, "Active robot's saved transport was not projected");
        preferences = RobotProfileRouting.Activate(preferences, "b");
        Check(preferences.SimulationTransport == pv, "A saved retired transport leaked into another robot profile");
        preferences = RobotProfileRouting.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences))!);
        preferences = RobotProfileRouting.Activate(preferences, "a");
        Check(preferences.SimulationTransport == js && JsonSerializer.Deserialize<Preferences>("{}")!.SimulationTransport == pv,
            "Transport persistence lost a saved profile value or migrated old preferences away from position-velocity");
    }
}
