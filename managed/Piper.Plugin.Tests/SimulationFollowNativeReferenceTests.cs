using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>Device, backend and freshness scope belong to <see cref="FollowLaneTelemetry"/>;
/// these checks cover the motion evidence it captures from one receipt.</summary>
internal static class SimulationFollowNativeReferenceTests
{
    public static void Run()
    {
        JsonObject Snapshot() => new()
        {
            ["motion"] = new JsonObject { ["active"] = true, ["following"] = true, ["trajectory_follow"] = true,
                ["owner"] = "owner-a", ["request_id"] = 77L, ["follow_sequence"] = 12L },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(j => (JsonNode)new JsonObject { ["command_deg"] = (double)j }).ToArray())
        };
        FollowNativeMotionEvidence Capture(JsonObject snapshot) => FollowNativeMotionEvidence.Capture(snapshot["motion"], snapshot["joints"] as JsonArray);
        SimulationFollowNativeReference Read(JsonObject snapshot) => Capture(snapshot).Read("can-a", 7, "owner-a", 77, 12);
        var valid = Read(Snapshot());
        Check(valid.DeviceId == "can-a" && valid.Generation == 7 && valid.RequestId == 77 && valid.Sequence == 12 && valid.JointDegrees.SequenceEqual(new[] { 1d, 2, 3, 4, 5, 6 }),
            "Native tracking reference must preserve the exact scoped transmitted command, not the path destination or measured encoders");
        foreach (string change in new[] { "owner", "request", "sequence", "negative-sequence", "active", "following", "trajectory", "missing", "nonfinite" })
        {
            var snapshot = Snapshot();
            if (change == "owner") snapshot["motion"]!["owner"] = "owner-b";
            if (change == "request") snapshot["motion"]!["request_id"] = 78L;
            if (change == "sequence") snapshot["motion"]!["follow_sequence"] = 13L;
            if (change == "negative-sequence") snapshot["motion"]!["follow_sequence"] = -1L;
            if (change == "active") snapshot["motion"]!["active"] = false;
            if (change == "following") snapshot["motion"]!["following"] = false;
            if (change == "trajectory") snapshot["motion"]!["trajectory_follow"] = false;
            if (change == "missing") snapshot["joints"]![0]!.AsObject().Remove("command_deg");
            if (change == "nonfinite") snapshot["joints"]![0]!["command_deg"] = double.NaN;
            bool rejected = false;
            try { Read(snapshot); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "A mismatched or invalid native reference was accepted: " + change);
        }

        long received = Stopwatch.GetTimestamp();
        long acknowledged = received - Stopwatch.Frequency / 100;
        bool Ready(JsonObject snapshot, long sequence = 100, double age = 0) => Capture(snapshot).AdmissionReady(
            new(new JsonObject(), "backend-a", sequence, received, age), "can-a", 7, "owner-a", 77, 12, acknowledged, 99, Stopwatch.Frequency);
        Check(Ready(Snapshot()), "Fresh matching native reference captured after its ACK must release the paused admission gate");
        Check(!Ready(Snapshot(), sequence: 99), "An already-cached matching receipt must not substitute for new post-ACK native feedback");
        Check(!Ready(Snapshot(), age: 20), "A buffered pre-ACK source snapshot must not become new proof merely because it arrived later");
        var old = Snapshot(); old["motion"]!["request_id"] = 76L; old["motion"]!["active"] = false;
        old["motion"]!["owner"] = ""; old["joints"]![0]!.AsObject().Remove("command_deg");
        Check(!Ready(old), "An older native request is cached pre-admission state, not a reason to reuse a missing command reference");
        foreach (string change in new[] { "request", "owner", "active", "missing" })
        {
            var snapshot = Snapshot();
            if (change == "request") snapshot["motion"]!["request_id"] = 78L;
            if (change == "owner") snapshot["motion"]!["owner"] = "owner-b";
            if (change == "active") snapshot["motion"]!["active"] = false;
            if (change == "missing") snapshot["joints"]![0]!.AsObject().Remove("command_deg");
            bool rejected = false;
            try { Ready(snapshot); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "A true post-ACK mismatch or missing reference must fail rather than becoming a cached-frame wait: " + change);
        }
    }
}
