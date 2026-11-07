using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationFollowNativeReferenceTests
{
    public static void Run(Action<bool, string> check)
    {
        JsonObject Snapshot() => new()
        {
            ["token"] = "backend-a", ["selected"] = "can-a",
            ["motion"] = new JsonObject { ["active"] = true, ["following"] = true, ["trajectory_follow"] = true,
                ["owner"] = "owner-a", ["request_id"] = 77L, ["follow_sequence"] = 12L },
            ["devices"] = new JsonArray(new JsonObject { ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true,
                ["status"] = new JsonObject { ["age_s"] = .001 },
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(j => (JsonNode)new JsonObject
                    { ["fresh"] = true, ["age_s"] = .001, ["command_deg"] = (double)j }).ToArray()) })
        };
        SimulationFollowNativeReference Read(JsonObject snapshot, double age = 0, string backend = "backend-a")
        {
            long now = Stopwatch.GetTimestamp();
            return SimulationFollowNativeReference.Read(new(snapshot, backend, 1, now, age), "backend-a", "can-a", 7, "owner-a", 77, 12, now);
        }
        var valid = Read(Snapshot());
        check(valid.DeviceId == "can-a" && valid.Generation == 7 && valid.RequestId == 77 && valid.Sequence == 12 && valid.JointDegrees.SequenceEqual(new[] { 1d, 2, 3, 4, 5, 6 }),
            "Native tracking reference must preserve the exact scoped transmitted command, not the path destination or measured encoders");
        foreach (string change in new[] { "backend", "state-backend", "selected", "generation", "owner", "request", "sequence", "negative-sequence", "active", "following", "trajectory", "missing", "nonfinite", "device-stale" })
        {
            var snapshot = Snapshot();
            if (change == "state-backend") snapshot["token"] = "backend-b";
            if (change == "selected") snapshot["selected"] = "can-b";
            if (change == "generation") snapshot["devices"]![0]!["generation"] = 8L;
            if (change == "owner") snapshot["motion"]!["owner"] = "owner-b";
            if (change == "request") snapshot["motion"]!["request_id"] = 78L;
            if (change == "sequence") snapshot["motion"]!["follow_sequence"] = 13L;
            if (change == "negative-sequence") snapshot["motion"]!["follow_sequence"] = -1L;
            if (change == "active") snapshot["motion"]!["active"] = false;
            if (change == "following") snapshot["motion"]!["following"] = false;
            if (change == "trajectory") snapshot["motion"]!["trajectory_follow"] = false;
            if (change == "missing") snapshot["devices"]![0]!["joints"]![0]!.AsObject().Remove("command_deg");
            if (change == "nonfinite") snapshot["devices"]![0]!["joints"]![0]!["command_deg"] = double.NaN;
            if (change == "device-stale") snapshot["devices"]![0]!["fresh"] = false;
            bool rejected = false;
            try { Read(snapshot, backend: change == "backend" ? "backend-b" : "backend-a"); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "A mismatched or invalid native reference was accepted: " + change);
        }
        bool expired = false;
        try { Read(Snapshot(), 251); } catch (InvalidOperationException) { expired = true; }
        check(expired, "A cached native reference must not gain a new250ms lease from a later renderer/UI read");

        long received = Stopwatch.GetTimestamp();
        long acknowledged = received - Stopwatch.Frequency / 100;
        bool Ready(JsonObject snapshot, long sequence = 100, double age = 0) => SimulationFollowNativeReference.AdmissionReady(
            new(snapshot, "backend-a", sequence, received, age), "backend-a", "can-a", 7, "owner-a", 77, 12, acknowledged, 99, received);
        check(Ready(Snapshot()), "Fresh matching native reference captured after its ACK must release the paused admission gate");
        check(!Ready(Snapshot(), sequence: 99), "An already-cached matching receipt must not substitute for new post-ACK native feedback");
        check(!Ready(Snapshot(), age: 20), "A buffered pre-ACK source snapshot must not become new proof merely because it arrived later");
        var old = Snapshot(); old["motion"]!["request_id"] = 76L; old["motion"]!["active"] = false;
        old["motion"]!["owner"] = ""; old["devices"]![0]!["joints"]![0]!.AsObject().Remove("command_deg");
        check(!Ready(old), "An older native request is cached pre-admission state, not a reason to reuse a missing command reference");
        bool regressed = false;
        try { SimulationFollowNativeReference.AdmissionReady(new(Snapshot(), "backend-a", 100, received, 0), "backend-a", "can-a", 7,
            "owner-a", 77, 12, acknowledged, 99, received, minimumNativeSequence: 13); } catch (InvalidOperationException) { regressed = true; }
        check(regressed, "Native reference sequence must not regress while waiting for post-ACK receipt evidence");
        foreach (string change in new[] { "request", "owner", "generation", "active", "missing" })
        {
            var snapshot = Snapshot();
            if (change == "request") snapshot["motion"]!["request_id"] = 78L;
            if (change == "owner") snapshot["motion"]!["owner"] = "owner-b";
            if (change == "generation") snapshot["devices"]![0]!["generation"] = 8L;
            if (change == "active") snapshot["motion"]!["active"] = false;
            if (change == "missing") snapshot["devices"]![0]!["joints"]![0]!.AsObject().Remove("command_deg");
            bool rejected = false;
            try { Ready(snapshot); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "A true post-ACK mismatch or missing reference must fail rather than becoming a cached-frame wait: " + change);
        }
    }
}
