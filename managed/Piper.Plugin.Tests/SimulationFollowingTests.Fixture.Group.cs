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
    /// <summary>Grouped-follow controls: sidebar, follower selection, scene bindings and the group pose.</summary>
    private sealed partial class Fixture
    {
        public PipersPlugin.GroupSession? Session => plugin.groupSimulation;
        public bool GroupActive => plugin.groupSimulation is not null;
        public GroupFollowPhase? GroupPhase => Session?.Phase;
        public async Task GroupEvent(string action)
        { await plugin.GroupFollowEvent(action, CancellationToken.None); }
        public FollowPreparationReport Preparation() => plugin.GroupPreparation(null, null, null);
        public string SidebarStatus() => Sidebar().Children!.Single(w => w.Id == "group.follow.status").Text!;
        public bool SidebarFollowEnabled() => Sidebar().Children!.Single(w => w.Id == "group.follow.toggle").Enabled != false;
        private Widget Sidebar() => plugin.GroupFollowControls();
        public Task ClickSidebarGroupStop()
        {
            var sidebar = plugin.GroupFollowControls();
            var stop = sidebar.Children!.Single(w => w.Text == "Stop following" && w.Visible != false);
            string wire = WorkspaceEventScope.Wrap(plugin.workspace.Epoch, "piper-1", stop.Id);
            return plugin.HandleEventAsync(new("fixture", wire, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        }
        public void RequestGroupPlay() { if (!groupArmed || Mode != "edit") throw new InvalidOperationException("Group is not armed"); startRequested = true; Mode = "pause"; }
        public bool GroupArmedForPlay => groupArmed && GroupActive;
        public void SelectGroupFollowers(params string[] ids)
        {
            var current = plugin.preferences;
            plugin.preferences = current with { Robots = current.Robots!.Select(p => p with { FollowSimulationSelected = ids.Contains(p.Id) }).ToArray() };
            Refresh();
        }
        public void SetGroupSceneEvidence(JsonObject? cached, JsonObject? current = null)
        {
            plugin.scene = cached;
            currentSceneEvidence = current;
        }
        public void SetSecondSceneBinding(string robotId, string prefix)
        {
            var current = plugin.preferences;
            plugin.preferences = current with { Robots = current.Robots!.Select(p => p.Id == "piper-2" ? p with { RobotId = robotId, RobotJointPrefix = prefix } : p).ToArray() };
            var authority = plugin.CommandAuthority;
            authority.TrySaveApproval(authority.Epoch, "piper-2",
                authority.Current.Approvals["piper-2"] with { SceneRobot = robotId, JointPrefix = prefix });
        }
        public void FocusGroupSecond()
        { plugin.preferences = RobotProfileRouting.Activate(plugin.preferences, "piper-2"); Edit(snapshot => snapshot["selected"] = "can-b"); }
        public void ConfigureGroup()
        {
            GroupMode = true; EpisodeId = "episode-group";
            var profiles = new[] { new RobotProfile("piper-1", "First", "can-a", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-a", FollowSimulationSelected: true),
                new RobotProfile("piper-2", "Second", "can-b", Speed: 10, RobotId: "robot-a", PikaDevice: "/dev/pika-b", RobotJointPrefix: "piper3_", FollowSimulationSelected: true) };
            plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: profiles, ActiveRobotId: "piper-1"));
            plugin.scene!["robots"]![0]!["joints"] = GroupPose()["joints"]!.DeepClone();
            Snapshot["multi_device_lanes_supported"] = true;
            var second = (JsonObject)Snapshot["devices"]![0]!.DeepClone(); second["id"] = "can-b"; second["generation"] = 8L;
            Snapshot["devices"]!.AsArray().Add(second);
            for (int i = 0; i < 2; i++)
            {
                var device = Snapshot["devices"]![i]!; device["status"]!["pose_stationary_s"] = 10d;
                var motion = (JsonObject)Snapshot["motion"]!.DeepClone(); motion["device_id"] = profiles[i].CanDeviceId; motion["generation"] = i == 0 ? 7L : 8L;
                device["motion"] = motion;
            }
            var firstPika = (JsonObject)Snapshot["pika"]!.DeepClone(); var secondPika = (JsonObject)firstPika.DeepClone(); secondPika["device"] = "/dev/pika-b";
            Snapshot["pikas"] = new JsonArray(firstPika, secondPika);
            for (int i = 0; i < profiles.Length; i++)
            {
                var p = profiles[i];
                plugin.CommandAuthority.TrySaveApproval(plugin.CommandAuthority.Epoch, p.Id,
                    new("fixture-backend", p.CanDeviceId, i == 0 ? 7L : 8L, p.RobotId, p.RobotJointPrefix, p.PikaDevice, p.WslDistribution));
            }
            Rpc.Accepted = (route, body) =>
            {
                lock (snapshotGate) Accept(route, body);
            };
            void Accept(string route, JsonObject body)
            {
                var device = Snapshot["devices"]!.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == body["device_id"]?.GetValue<string>());
                string action = body["action"]?.GetValue<string>() ?? "";
                if (device is not null && route == "/api/command")
                {
                    var motion = device["motion"]!;
                    if (action == "follow-start" && !DelayNativeAdmissionTelemetry) { motion["active"] = true; motion["following"] = true; motion["trajectory_follow"] = true; motion["request_id"] = 77L; motion["follow_sequence"] = 0L; }
                    if (action == "follow-update")
                    {
                        pendingNativeSequence = body["sequence"]!.GetValue<long>(); pendingNativeCommand = body["degrees"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                        if (!DelayNativeAdmissionTelemetry)
                        {
                            motion["follow_sequence"] = body["sequence"]!.DeepClone();
                            for (int i = 0; i < 6; i++) { device["joints"]![i]!["command_deg"] = body["degrees"]![i]!.DeepClone(); device["joints"]![i]!["actual_deg"] = body["degrees"]![i]!.DeepClone(); }
                        }
                    }
                    if (action == "hold") { motion["active"] = false; motion["following"] = false; }
                }
                var pika = Snapshot["pikas"]!.AsArray().OfType<JsonObject>().SingleOrDefault(p => p["device"]?.GetValue<string>() == body["pika_device"]?.GetValue<string>());
                if (pika is not null && route == "/api/pika/follow-update") pika["telemetry"]!["motor"]!["Position"] = PikaProtocol.Angle(body["opening_mm"]!.GetValue<double>());
                if (pika is not null && route == "/api/pika/position") pika["telemetry"]!["motor"]!["Position"] = body["value"]!.DeepClone();
                Refresh();
            }
            Refresh();
            groupTelemetryWorker = Task.Run(async () =>
            { while (!groupTelemetryStop.IsCancellationRequested) { Refresh(); await Task.Delay(10); } });
        }

        private JsonObject GroupPose(double time = 0, long poseSequence = 1)
        {
            var pose = Fixtures.SimulationPose(time, poseSequence);
            if (GroupMode)
            {
                foreach (var joint in pose["joints"]!.AsArray().ToArray()) { var copy = (JsonObject)joint!.DeepClone(); copy["name"] = "piper3_" + copy["name"]!.GetValue<string>(); pose["joints"]!.AsArray().Add(copy); }
                foreach (var actuator in pose["actuators"]!.AsArray().ToArray()) { var copy = (JsonObject)actuator!.DeepClone(); copy["name"] = "piper3_" + copy["name"]!.GetValue<string>(); copy["targetJoint"] = "piper3_" + copy["targetJoint"]!.GetValue<string>(); pose["actuators"]!.AsArray().Add(copy); }
            }
            return pose;
        }
    }
}
