using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

static class SimulationCameraRoutingTests
{
    public static void Run()
    {
        var preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [
            new("a", "Piper 1", RobotId: "robot-a"), new("b", "Piper 2", RobotId: "robot-b")], ActiveRobotId: "a"));
        var cameras = JsonNode.Parse("""
            {"sceneId":"source-a","runId":"","cameras":[
             {"entityId":"bench","name":"Lumina Raw"},
             {"entityId":"wrist-a","name":"Pika fisheye","robotEntityId":"robot-a"},
             {"entityId":"depth-a","name":"D405","robotEntityId":"robot-a"},
             {"entityId":"wrist-b","name":"Pika fisheye","robotEntityId":"robot-b"},
             {"entityId":"depth-b","name":"D405","robotEntityId":"robot-b"}]}
            """)!.AsObject();
        var bound = SimulationCameraRouting.Refresh(preferences, cameras);
        Check(bound.SimulationCameras?["bench"].CameraId == "bench" && bound.SimulationCameras["gripper"].CameraId == "wrist-a" &&
            bound.SimulationCameras["depth"].CameraId == "depth-a", "Initial preview discovery did not bind the selected robot's exact camera ownership");
        Check(bound.Robots![1].SimulationCameras?["gripper"].CameraId == "wrist-b" &&
            bound.Robots[1].SimulationCameras?["depth"].CameraId == "depth-b", "Inactive robot did not retain its separate sim counterparts");
        var switched = RobotProfileRouting.Activate(bound, "b");
        Check(switched.SimulationCameras?["bench"].CameraId == "bench" && switched.SimulationCameras["gripper"].CameraId == "wrist-b",
            "Selecting another robot reused the first robot's simulated wrist or changed the shared bench view");

        // Play copies retain camera UUIDs, while the source session UUID changes on reload.
        cameras["runId"] = "play-run-1";
        var play = SimulationCameraRouting.Refresh(switched, cameras);
        Check(SimulationCameraRouting.IsAvailable(play.SimulationCameras!["gripper"], cameras), "Play mode lost a source-scene camera identity");
        Check(JsonSerializer.Serialize(play, PluginProtocol.Json) == JsonSerializer.Serialize(switched, PluginProtocol.Json),
            "Ordinary camera inventory refresh caused repeated preference writes");
        cameras["sceneId"] = "source-reloaded";
        var reloaded = SimulationCameraRouting.Refresh(play, cameras);
        Check(reloaded.Robots!.All(profile => profile.SimulationCameras!.Values.All(binding => binding.SceneId == "source-reloaded")) &&
            reloaded.SimulationCameras!["bench"].SceneId == "source-reloaded", "Scene reload left inactive robot or shared camera bindings stale");
        Check(!SimulationCameraRouting.IsAvailable(play.SimulationCameras!["gripper"], cameras), "A stale source-scene camera was presented as available");
        var persisted = RobotProfileRouting.Normalize(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(reloaded, PluginProtocol.Json), PluginProtocol.Json)!);
        Check(persisted.SimulationCameras!["gripper"].CameraId == "wrist-b", "Per-robot counterpart did not survive persistence");

        var explicitBinding = persisted with { SimulationCameras = new(persisted.SimulationCameras!) { ["gripper"] = new("old-source", "wrist-b", true) } };
        var kept = SimulationCameraRouting.Refresh(explicitBinding, cameras);
        Check(kept.SimulationCameras!["gripper"] is { CameraId: "wrist-b", Overlay: true, SceneId: "source-reloaded" },
            "Automatic binding overwrote a valid manually selected counterpart or overlay preference");

        var ambiguous = cameras.DeepClone().AsObject();
        ambiguous["cameras"]!.AsArray().Add(new JsonObject { ["entityId"] = "second-wrist-a", ["name"] = "Pika fisheye", ["robotEntityId"] = "robot-a" });
        var refused = SimulationCameraRouting.Refresh(preferences, ambiguous);
        Check(!refused.SimulationCameras!.ContainsKey("gripper"), "Ambiguous wrist cameras were silently resolved by array order");
        var foreign = JsonNode.Parse("""{"sceneId":"other","cameras":[{"entityId":"foreign-wrist","name":"Piper 1 Pika","robotEntityId":"robot-b"}]}""")!.AsObject();
        Check(!SimulationCameraRouting.Refresh(preferences, foreign).SimulationCameras!.ContainsKey("gripper"),
            "A name match overrode a different robot's explicit native camera ownership");
        var legacy = JsonNode.Parse("""{"sceneId":"legacy","cameras":[{"entityId":"ten","name":"Piper 10 Pika"},{"entityId":"one","name":"Piper 1 Pika"}]}""")!.AsObject();
        Check(SimulationCameraRouting.Refresh(preferences, legacy).SimulationCameras!["gripper"].CameraId == "one",
            "Legacy profile-label binding confused Piper 1 with Piper 10");
        Check(!SimulationCameraRouting.IsAvailable(new("source-reloaded", "removed-camera"), cameras), "Removed cameras remained visible");
        var defaultVisibility = JsonSerializer.Deserialize<Preferences>("{}", PluginProtocol.Json)!;
        Check(defaultVisibility.ShowMeasuredGhosts && !JsonSerializer.Deserialize<Preferences>(
            JsonSerializer.Serialize(defaultVisibility with { ShowMeasuredGhosts = false }, PluginProtocol.Json), PluginProtocol.Json)!.ShowMeasuredGhosts,
            "Ghost visibility default or remembered opt-out changed");

        var compoundProfiles = RobotProfileRouting.Normalize(new Preferences(Robots: [new("a", "Piper 1", RobotId: "compound"),
            new("b", "Piper 3", RobotId: "compound", RobotJointPrefix: "piper3_")], ActiveRobotId: "a"));
        var compoundCameras = JsonNode.Parse("""
            {"sceneId":"compound-source","cameras":[
            {"entityId":"pika-one","name":"Pika fisheye","robotEntityId":"compound","ancestorJointNames":["joint1","joint2","joint3","joint4","joint5","joint6"]},
            {"entityId":"pika-two","name":"Pika fisheye","robotEntityId":"compound","ancestorJointNames":["piper3_joint1","piper3_joint2","piper3_joint3","piper3_joint4","piper3_joint5","piper3_joint6"]}]}
            """)!.AsObject();
        var compoundBound = SimulationCameraRouting.Refresh(compoundProfiles, compoundCameras);
        Check(compoundBound.Robots![0].SimulationCameras!["gripper"].CameraId == "pika-one" &&
            compoundBound.Robots[1].SimulationCameras!["gripper"].CameraId == "pika-two", "Two articulated robots in one physics world borrowed each other's camera despite different joint ancestry");
        var reboundProfile = RobotProfileRouting.Activate(compoundBound, "b");
        reboundProfile = reboundProfile with { SimulationCameras = new() { ["gripper"] = new("compound-source", "pika-one") } };
        Check(SimulationCameraRouting.Refresh(reboundProfile, compoundCameras).SimulationCameras!["gripper"].CameraId == "pika-two",
            "An obsolete saved camera binding survived a robot joint-scope change");

        var colorDevices = new[] { new ColorCameraDevice("DECXIN  CAMERA", "path-a", "11-1"), new ColorCameraDevice("DECXIN  CAMERA", "path-b", "15-1") };
        Check(CameraDeviceChoices.ResolveSaved("DECXIN  CAMERA", "/dev/pika/by-host/11-4", colorDevices) == "path-a" &&
            CameraDeviceChoices.ResolveSaved("DECXIN  CAMERA", "/dev/pika/by-host/15-4", colorDevices) == "path-b",
            "Duplicate fisheyes were not resolved by verified gripper/camera USB hub identity");
        Check(CameraDeviceChoices.ResolveSaved("DECXIN  CAMERA", "unscoped-by-id", colorDevices) == "DECXIN  CAMERA",
            "Ambiguous camera friendly name was resolved without a verified hardware association");
        var choices = CameraDeviceChoices.Merge([], colorDevices);
        var updatedChoices = CameraDeviceChoices.Merge(choices, [colorDevices[1],colorDevices[0],new("Bench","path-c","6-1")]);
        Check(updatedChoices.Select(choice => choice.Id).SequenceEqual(new[]{"path-a","path-b","path-c"}) && choices.All(choice => !choice.Label.Contains("path-")),
            "Hotplug/reordering changed dropdown indices or exposed raw camera paths in labels");
        var disconnected = CameraDeviceChoices.Merge(updatedChoices, []);
        Check(CameraDeviceChoices.Merge(disconnected, []).Select(choice => choice.Label).SequenceEqual(disconnected.Select(choice => choice.Label)),
            "Repeated missing-device refresh grew disconnected labels indefinitely");
        using var choiceEvent = JsonDocument.Parse("2");
        Check(CameraDeviceChoices.Resolve(updatedChoices,choiceEvent.RootElement,"") == "path-b", "Camera picker returned a friendly name rather than its stable device identity");
    }
}
