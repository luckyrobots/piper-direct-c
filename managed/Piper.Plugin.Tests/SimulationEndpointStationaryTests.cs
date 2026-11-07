using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationEndpointStationaryTests
{
    public static void Run(Action<bool, string> check)
    {
        var status = new JsonObject { ["age_s"] = .001, ["control"] = 1, ["arm_status"] = 0, ["error"] = 0,
            ["stationary_s"] = 0d, ["pose_stationary_s"] = 67d };
        var device = new JsonObject { ["id"] = "can-a", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
            ["status"] = status, ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject
                { ["enabled"] = true, ["fresh"] = true, ["fault"] = 0, ["age_s"] = .001 }).ToArray()) };
        var state = new JsonObject { ["selected"] = "can-a", ["devices"] = new JsonArray(device) };
        long now = Stopwatch.GetTimestamp();
        var receipt = new TelemetryReceipt(state, "backend-a", 1, now, 1);
        bool Settled() => SimulationEndpointStationary.IsStationary(receipt, "backend-a", "can-a", 7, now);
        check(Settled(), "A held pose corroborated by native joint/encoder windows was rejected because raw velocity noise reset the stricter handoff clock");
        status["stationary_s"] = 1d; status["pose_stationary_s"] = 0d;
        check(!Settled(), "Real pose drift was hidden by a quiet legacy velocity timer");
        foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create(-1d), JsonValue.Create("NaN") })
        { status["pose_stationary_s"] = invalid; check(!Settled(), "Invalid present pose evidence fell back to the legacy timer"); }
        status.Remove("pose_stationary_s");
        check(Settled(), "An older backend with only a valid strict stationary clock lost its arrival gate");
        status["pose_stationary_s"] = 67d;
        device["generation"] = 8L; check(!Settled(), "A replacement generation supplied arrival evidence for the previous arm"); device["generation"] = 7L;
        state["selected"] = "can-b"; check(!Settled(), "Arrival evidence from a different selected arm was accepted"); state["selected"] = "can-a";
        device["fresh"] = false; check(!Settled(), "Stale arm evidence satisfied an endpoint barrier"); device["fresh"] = true;
        status["error"] = 1; check(!Settled(), "A real controller error was masked by an old quiet pose clock"); status["error"] = 0;
        device["recovery_required"] = true; check(!Settled(), "A stopped/recovery arm satisfied an endpoint barrier"); device["recovery_required"] = false;
        check(!SimulationEndpointStationary.IsStationary(receipt, "replacement-backend", "can-a", 7, now) &&
            !SimulationEndpointStationary.IsStationary(receipt with { Connected = false }, "backend-a", "can-a", 7, now) &&
            !SimulationEndpointStationary.IsStationary(receipt, "backend-a", "can-a", 7, now + Stopwatch.Frequency),
            "Backend replacement, disconnected telemetry or an expired receipt retained arrival authority");
    }
}
