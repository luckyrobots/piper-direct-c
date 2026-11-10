using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static partial class SimulationFollowingTests
{
    /// <summary>Primary-action and teaching-resume controls.</summary>
    private sealed partial class Fixture
    {
        public void SetGripperUnready()
        {
            plugin.telemetry.Pika.Enter(PikaSessionRole.None);
            Snapshot["pika"]!["enabled"] = false; Snapshot["pika"]!["armed"] = false; Snapshot["pika"]!["read_only"] = true;
            Refresh();
        }
        public string PrimaryActionName() => (plugin.Readiness(Snapshot["devices"]![0]!.AsObject())).Action;
        public Task PrimaryResume() => plugin.PrimaryRobotAction(CancellationToken.None);
        public JsonObject PrimaryResumePatch()
        {
            var patches = new Dictionary<string, JsonObject> { ["teleoperate.toggle"] = new() };
            var device = Snapshot["devices"]![0]!.AsObject();
            plugin.AddTeachingPatches(patches, device);
            plugin.AddUnifiedWorkspacePatches(patches, device);
            return patches["enable"];
        }
        public void ConfigureTeachingResume(bool completeImmediately = true)
        {
            resumeFrames = true;
            var device = Snapshot["devices"]![0]!;
            device["status"]!["control"] = 2; device["status"]!["teach_state"] = 2;
            device["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = false,
                ["return_reason"] = "Hold the arm still until all six motor encoders confirm stationary feedback for 0.4 seconds.",
                ["seamless_supported"] = true, ["return_requires_zero"] = false, ["return_strategy"] = "measured-pose" };
            device["motion"] = Snapshot["motion"]!.DeepClone();
            plugin.approved = false; plugin.recovery.ObserveTeaching(TeachingState.Inspect(device.AsObject()));
            Rpc.Accepted = (route, body) =>
            {
                if (route != "/api/command" || body["action"]?.GetValue<string>() != "teach-return") return;
                lock (snapshotGate)
                {
                    device["motion"]!["request_id"] = 77L;
                    device["motion"]!["active"] = !completeImmediately;
                    device["motion"]!["phase"] = completeImmediately ? "complete" : "teaching-return";
                    device["status"]!["control"] = 1; device["teaching"]!["phase"] = "off";
                    device["teaching"]!["return_required"] = false;
                    Refresh();
                }
            };
            Refresh();
        }
        public void SetResumeEligible()
        {
            lock (snapshotGate)
            {
                Snapshot["devices"]![0]!["teaching"]!["can_return"] = true;
                Refresh();
            }
        }
        public void CompleteResume()
        {
            lock (snapshotGate)
            {
                var motion = Snapshot["devices"]![0]!["motion"]!;
                motion["active"] = false;
                motion["phase"] = "complete";
                Refresh();
            }
        }
        public void CompleteResumeOnHeartbeat()
        {
            var previous = Rpc.Accepted;
            Rpc.Accepted = (route, body) => { previous?.Invoke(route, body); if (route == "/api/heartbeat") CompleteResume(); };
            Rpc.RejectHeartbeatAfterAccepted = true;
        }
        public void InterruptResume(string kind)
        {
            lock (snapshotGate)
            {
                var device = Snapshot["devices"]![0]!;
                if (kind == "permission") plugin.CommandAuthority.Revoke();
                if (kind == "generation") device["generation"] = 8L;
                if (kind == "playback") device["teaching"]!["phase"] = "playback";
                if (kind == "fault") device["joints"]![3]!["fault"] = 1;
                Refresh();
            }
        }
    }
}
