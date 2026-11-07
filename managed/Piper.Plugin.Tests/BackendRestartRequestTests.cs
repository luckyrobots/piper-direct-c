using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class BackendRestartRequestTests
{
    internal static void Run(Action<bool, string> check)
    {
        string owner = Guid.NewGuid().ToString("N"), instance = Guid.NewGuid().ToString("N");
        var state = new JsonObject { ["backend_process_id"] = 123, ["backend_instance_id"] = instance,
            ["backend_restart"] = new JsonObject { ["supported"] = true },
            ["motion"] = new JsonObject { ["active"] = false } };
        var request = BackendRestartRequest.Create(state, owner, false, true);
        check(request["backend_process_id"]!.GetValue<int>() == 123 && request["backend_instance_id"]!.GetValue<string>() == instance &&
            request["owner"]!.GetValue<string>() == owner && request["release_grippers"]!.GetValue<bool>(),
            "Restart did not bind acknowledgement to the exact backend and requesting owner");
        void Reject(Func<JsonObject> action, string reason)
        {
            try { action(); check(false, reason); }
            catch (InvalidOperationException error) { check(error.Message.Length > 10, "Restart blocker did not explain the next step"); }
        }
        Reject(() => BackendRestartRequest.Create(state, owner, false, false), "Restart released Pika torque without empty-jaw acknowledgement");
        Reject(() => BackendRestartRequest.Create(state, owner, true, true), "Restart interrupted an active simulation follower");
        state["motion"]!["active"] = true;
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "Restart interrupted an active native movement");
        state["motion"]!["active"] = false;
        state["motions"] = new JsonArray(new JsonObject { ["device_id"] = "second", ["active"] = true });
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "Restart ignored the nonselected robot's native lane");
        state.Remove("motions");
        state["any_query_active"] = true;
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "Restart interrupted another device's metadata query");
        state.Remove("any_query_active");
        state["backend_restart"]!["supported"] = false;
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "An unsupported external backend was shut down");
        state["backend_restart"]!["supported"] = true;
        state["backend_restart"]!["mode"] = "plugin";
        state["backend_restart"]!["plugin_owner"] = "another-owner";
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "Another plugin's backend could be stopped for relaunch");
        state["backend_restart"]!["plugin_owner"] = owner;
        check(BackendRestartRequest.Create(state, owner, false, true)["backend_process_id"]!.GetValue<int>() == 123,
            "The exact plugin owner could not request its managed restart");
        state["backend_instance_id"] = "";
        Reject(() => BackendRestartRequest.Create(state, owner, false, true), "Restart accepted an unidentified backend session");
    }
}
