using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>Device health and scope gates for arrival live in <see cref="FollowLaneObservation"/>.</summary>
internal static class SimulationEndpointStationaryTests
{
    public static void Run(Action<bool, string> check)
    {
        var status = new JsonObject { ["stationary_s"] = 0d, ["pose_stationary_s"] = 67d };
        bool Settled() => SimulationEndpointStationary.PoseDwellSatisfied(status);
        check(Settled(), "A held pose corroborated by native joint/encoder windows was rejected because raw velocity noise reset the stricter handoff clock");
        status["stationary_s"] = 1d; status["pose_stationary_s"] = 0d;
        check(!Settled(), "Real pose drift was hidden by a quiet legacy velocity timer");
        foreach (JsonNode? invalid in new JsonNode?[] { null, JsonValue.Create(-1d), JsonValue.Create("NaN") })
        { status["pose_stationary_s"] = invalid; check(!Settled(), "Invalid present pose evidence fell back to the legacy timer"); }
        status.Remove("pose_stationary_s");
        check(Settled(), "An older backend with only a valid strict stationary clock lost its arrival gate");
    }
}
