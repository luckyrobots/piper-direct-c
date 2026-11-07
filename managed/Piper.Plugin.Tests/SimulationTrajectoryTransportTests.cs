using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationTrajectoryTransportTests
{
    public static void Run(Action<bool, string> check)
    {
        const string pv = SimulationTrajectoryTransport.PositionVelocity, js = SimulationTrajectoryTransport.StreamedPosition;
        var snapshot = new JsonObject { ["selected"] = "can-a", ["streamed_position_transport_supported"] = true,
            ["streamed_position_wire_validation_supported"] = true,
            ["devices"] = new JsonArray(new JsonObject { ["id"] = "can-a", ["hardware"] = "H-V1.2-1", ["firmware"] = "S-V1.9-0", ["streamed_position_supported"] = true }) };
        check(SimulationTrajectoryTransport.Validate(pv, null, "can-a") == pv && !SimulationTrajectoryTransport.StartFields(pv).ContainsKey("trajectory_transport"),
            "Default position-velocity mode acquired a new backend requirement or silently selected JS");
        check(SimulationTrajectoryTransport.Validate(js, snapshot, "can-a") == js && SimulationTrajectoryTransport.StartFields(js)["trajectory_transport"]!.GetValue<string>() == js &&
            SimulationTrajectoryTransport.StartFields(js)["trajectory"]!.GetValue<bool>(), "Explicit JS transport was lost at command admission");
        foreach (string change in new[] { "backend", "wire-validation", "device", "selection", "hardware", "future", "older" })
        {
            var changed = snapshot.DeepClone().AsObject();
            if (change == "backend") changed.Remove("streamed_position_transport_supported");
            if (change == "wire-validation") changed.Remove("streamed_position_wire_validation_supported");
            if (change == "device") changed["devices"]![0]!["streamed_position_supported"] = false;
            if (change == "selection") changed["selected"] = "can-b";
            if (change == "hardware") changed["devices"]![0]!["hardware"] = "H-V1.2-2";
            if (change == "future") changed["devices"]![0]!["firmware"] = "S-V1.9-2";
            if (change == "older") changed["devices"]![0]!["firmware"] = "S-V1.8-9";
            bool rejected = false;
            try { SimulationTrajectoryTransport.Validate(js, changed, "can-a"); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Experimental transport bypassed its reviewed scope: " + change);
        }
        foreach (string? invalid in new[] { null, "", "mit", "streamed", "Streamed-position" })
        {
            bool rejected = false;
            try { SimulationTrajectoryTransport.Name(invalid); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "An unknown or ambiguous trajectory transport selected a wire mode");
        }
        SimulationTrajectoryTransport.ValidateAcknowledgement(pv, new());
        SimulationTrajectoryTransport.ValidateAcknowledgement(js, new() { ["trajectory_transport"] = js, ["trajectory_transport_validated"] = true });
        bool unvalidatedRejected = false;
        try { SimulationTrajectoryTransport.ValidateAcknowledgement(js, new() { ["trajectory_transport"] = js }); }
        catch (InvalidOperationException) { unvalidatedRejected = true; }
        check(unvalidatedRejected, "A legacy JS acknowledgement bypassed production wire validation");
        foreach ((string requested, string? actual) in new[] { (js, (string?)null), (js, pv), (pv, js), (pv, "other") })
        {
            bool rejected = false;
            try { SimulationTrajectoryTransport.ValidateAcknowledgement(requested, new() { ["trajectory_transport"] = actual }); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "The backend acknowledged a different or missing experimental wire transport");
        }
        var preferences = RobotProfileRouting.Normalize(new Preferences(Robots:
            [new("a", "Robot A", SimulationTransport: js), new("b", "Robot B")], ActiveRobotId: "a"));
        check(preferences.SimulationTransport == js, "Active robot's explicitly configured transport was not projected");
        preferences = RobotProfileRouting.Activate(preferences, "b");
        check(preferences.SimulationTransport == pv, "Experimental transport leaked into another robot profile");
        preferences = RobotProfileRouting.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(preferences))!);
        preferences = RobotProfileRouting.Activate(preferences, "a");
        check(preferences.SimulationTransport == js && JsonSerializer.Deserialize<Preferences>("{}")!.SimulationTransport == pv,
            "Transport persistence lost explicit opt-in or migrated old preferences to JS");
    }
}
