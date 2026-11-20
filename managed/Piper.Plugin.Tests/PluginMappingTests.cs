using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>Profile routing, backend ownership, camera profiles, scene-link
/// mapping and the other small pure checks that predate the per-area suites.</summary>
internal static class PluginMappingTests
{
    public static async Task Run()
    {
        using (var authorized = System.Text.Json.JsonDocument.Parse("{\"owner\":\"plugin-session\"}"))
        using (var other = System.Text.Json.JsonDocument.Parse("{\"owner\":\"another-client\"}"))
        using (var absent = System.Text.Json.JsonDocument.Parse("{}"))
        {
            Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("", "/api/command", absent.RootElement), "External server behavior changed");
            Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", "/api/query", other.RootElement), "Readback query unnecessarily blocked");
            foreach (string route in new[] { "/api/stop", "/api/stop/coordinated" })
                Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, other.RootElement) &&
                    LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, absent.RootElement), "Session guard refused an emergency " + route);
            foreach (string route in new[] { "/api/command", "/api/reconnect", "/api/pika/connect", "/api/pika/configure", "/api/shutdown" })
            {
                Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, authorized.RootElement), "Owner rejected for " + route);
                Check(!LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, other.RootElement), "Another client acquired session for " + route);
                Check(!LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, absent.RootElement), "Missing owner accepted for " + route);
            }
        }
        var discoveryPlugin = new PipersPlugin();
        discoveryPlugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-one")], ActiveRobotId: "piper-1"));
        var discovery = discoveryPlugin.PikaInventoryRequest();
        using (var discoveryBody = System.Text.Json.JsonDocument.Parse(discovery.Json))
            Check(discovery.Route.SessionOwned && LuckyRobots.Piper.SessionOwnerGuard.Allows(discoveryPlugin.owner, discovery.Route.Path, discoveryBody.RootElement),
                "The plugin's gripper discovery was refused by the server it started (missing session owner)");
        // Editing the active profile's simulation arm must stick. Capturing after the substitution
        // copied the stale projected RobotId back, so the Settings dropdown always reverted.
        static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var linked = RobotProfileRouting.Normalize(new Preferences(Robots: [new("a", "A", RobotId: "old-arm"), new("b", "B")], ActiveRobotId: "a"));
        linked = RobotProfileRouting.Activate(linked, "a") with { Speed = 42 };   // a live, not yet captured value
        var relinkedProfile = RobotProfileRouting.Capture(linked).Robots!.Single(p => p.Id == "a") with { RobotId = "new-arm", RobotJointPrefix = "right_" };
        var relinked = RobotProfileRouting.Apply(linked, relinkedProfile, append: false);
        Check(relinked.RobotId == "new-arm" && relinked.Robots!.Single(p => p.Id == "a") is { RobotId: "new-arm", RobotJointPrefix: "right_", Speed: 42 },
            "Changing the active robot's simulation arm reverted, or lost its live settings");
        var grown = RobotProfileRouting.Apply(relinked with { Speed = 17 }, new RobotProfile("c", "C"), append: true);
        Check(grown.ActiveRobotId == "c" && grown.Robots!.Length == 3 && grown.Robots.Single(p => p.Id == "a").Speed == 17,
            "Adding a robot did not capture the outgoing robot's live settings");
        Check(Throws(() => RobotProfileRouting.Apply(linked, new RobotProfile("missing", "M"), append: false)), "Applying an unknown profile was accepted");
        Check(ControllerEndpoint.GrpcAddress == $"http://{ControllerEndpoint.Loopback}:{ControllerEndpoint.GrpcPort}" &&
            ControllerEndpoint.StateUrl == $"http://{ControllerEndpoint.Loopback}:{ControllerEndpoint.HttpPort}/api/state",
            "Controller endpoint constants disagree with their ports");
        var followerPreferences = RobotProfileRouting.Normalize(new Preferences(Robots: [
            new("first", "First", FollowSimulationSelected: true), new("second", "Second")], ActiveRobotId: "first"));
        followerPreferences = RobotProfileRouting.Activate(followerPreferences, "second");
        var restoredFollowers = RobotProfileRouting.Normalize(System.Text.Json.JsonSerializer.Deserialize<Preferences>(
            System.Text.Json.JsonSerializer.Serialize(followerPreferences))!);
        Check(restoredFollowers.Robots![0].FollowSimulationSelected && !restoredFollowers.Robots[1].FollowSimulationSelected,
            "Follower membership changed on profile selection or preference reload");
        Check(RobotProfileRouting.EventForProfile("second", RobotProfileRouting.ScopeEvent("first", "group.follow.selected")) == "",
            "A delayed follower checkbox changed another robot's membership");
        Check(RobotProfileRouting.EventForProfile("second", RobotProfileRouting.ScopeEvent("first", "group.follow.toggle")) == "group.follow.toggle",
            "The shared follower button became tied to the visible robot");
        var ownedServer = new JsonObject { ["backend_process_id"] = 123, ["token"] = "session-a" };
        Check(BackendGuards.IsOwned(ownedServer, 123, "session-a"), "Owned backend identity rejected");
        Check(!BackendGuards.IsOwned(ownedServer, 124, "session-a"), "Unrelated backend process treated as owned");
        Check(!BackendGuards.IsOwned(ownedServer, 123, "session-b"), "Restarted backend token treated as owned");
        Check(!BackendGuards.IsOwned(new JsonObject { ["token"] = "session-a" }, 123, "session-a"), "External legacy server treated as plugin-owned");
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Check(await BackendGuards.PortOccupied(port, CancellationToken.None), "Occupied backend port was not detected");
            listener.Stop();
            Check(!await BackendGuards.PortOccupied(port, CancellationToken.None), "Closed backend port still reported occupied");
        }
        var oldPreferences = System.Text.Json.JsonSerializer.Deserialize<Preferences>("""{"Speed":37,"Step":2,"Tolerance":0.3,"Bench":{"Device":"camera-by-path","Enabled":false,"VideoSize":"1920x1080","FrameRate":30,"InputCodec":"mjpeg"},"Python":"removed-python-path","PythonModules":"old-modules","Ffmpeg":"removed-ffmpeg-path"}""")!;
        Check(oldPreferences.Speed == 37 && oldPreferences.Step == 2 && oldPreferences.Tolerance == .3, "Camera runtime migration changed motion preferences");
        Check(oldPreferences.Bench is { Device: "camera-by-path", Enabled: false, FrameRate: 30 }, "Camera runtime migration lost device settings");
        Check(!System.Text.Json.JsonSerializer.Serialize(oldPreferences).Contains("removed-python-path"), "Obsolete Python runtime setting retained");
        var sharedBench = new CameraPreferences("lumina-shared", true, "1920x1080", 30, "mjpeg");
        var benchBinding = new SimulationCameraPreferences("scene-a", "bench-scene-camera");
        var wristBinding = new SimulationCameraPreferences("scene-a", "wrist-a");
        var legacyProfiles = RobotProfileRouting.Normalize(new Preferences(Speed: 37, Step: 2, Tolerance: .3, RobotId: "scene-robot-a", Bench: sharedBench,
            Gripper: new("wrist-camera-a"), DepthSerial: "depth-a", SimulationCameras: new() { ["bench"] = benchBinding, ["gripper"] = wristBinding }, Backend: new("CustomUbuntu", "/dev/pika-a")));
        Check(legacyProfiles.Robots is { Length: 1 } && legacyProfiles.ActiveRobotId == "piper-1", "Legacy workspace did not migrate to one explicit robot");
        Check(RobotProfileRouting.Active(legacyProfiles) is { PikaDevice: "/dev/pika-a", CameraSlot: 0 } legacyProfile &&
            legacyProfile.WslDistribution == LuckyRobots.Piper.Pika.PikaDistribution.ForCurrentPlatform("CustomUbuntu"), "Legacy Pika identity/distro did not migrate");
        Check(legacyProfiles.Bench == sharedBench && legacyProfiles.SimulationCameras!["bench"] == benchBinding, "Migration changed the shared bench camera");
        var profileA = RobotProfileRouting.Active(legacyProfiles) with { CanDeviceId = "can-a" };
        var profileB = new RobotProfile("piper-2", "Piper 2", "can-b", 55, 4, .2, "scene-robot-b", new("wrist-camera-b"), new("depth-camera-b"), "depth-b",
            new() { ["gripper"] = new("scene-a", "wrist-b") }, "/dev/pika-b", "CustomUbuntu");
        var twoProfiles = RobotProfileRouting.Normalize(legacyProfiles with { Robots = [profileA, profileB] });
        twoProfiles = RobotProfileRouting.Activate(twoProfiles with { Speed = 42, Step = 3, Tolerance = .25 }, "piper-2");
        Check(twoProfiles is { Speed: 55, Step: 4, Tolerance: .2, RobotId: "scene-robot-b", DepthSerial: "depth-b" } && twoProfiles.Gripper?.Device == "wrist-camera-b", "Robot switch leaked the previous robot's controls or camera");
        Check(twoProfiles.Bench == sharedBench && twoProfiles.SimulationCameras!["bench"] == benchBinding && twoProfiles.SimulationCameras["gripper"].CameraId == "wrist-b", "Switch lost shared bench or selected wrong wrist camera");
        twoProfiles = RobotProfileRouting.Activate(twoProfiles, "piper-1");
        Check(twoProfiles is { Speed: 42, Step: 3, Tolerance: .25, RobotId: "scene-robot-a", DepthSerial: "depth-a" }, "Return to robot did not restore its retained settings");
        Check(twoProfiles.SimulationCameras!["gripper"] == wristBinding && !RobotProfileRouting.Active(twoProfiles).SimulationCameras!.ContainsKey("bench"), "Shared bench was copied into robot settings");
        var roundTripProfiles = RobotProfileRouting.Normalize(System.Text.Json.JsonSerializer.Deserialize<Preferences>(System.Text.Json.JsonSerializer.Serialize(twoProfiles))!);
        Check(roundTripProfiles.Robots!.Length == 2 && roundTripProfiles.ActiveRobotId == "piper-1" && roundTripProfiles.Robots.Select(p => p.CameraSlot).Distinct().Count() == 2, "Profile persistence lost active identity or camera slots");
        var routedSnapshot = JsonNode.Parse("""{"selected":"can-a","devices":[{"id":"can-a","generation":1},{"id":"can-b","generation":2}]}""")!.AsObject();
        var inventory = RobotProfileRouting.CanInventoryIds([], twoProfiles, routedSnapshot);
        string inventoryKey = RobotProfileRouting.CanInventoryKey(routedSnapshot);
        routedSnapshot["devices"]![0]!["joints"] = new JsonArray(new JsonObject { ["actual_deg"] = 42 });
        Check(RobotProfileRouting.CanInventoryKey(routedSnapshot) == inventoryKey, "Joint telemetry unnecessarily invalidated dropdown inventory");
        routedSnapshot["devices"]![0]!["present"] = true;
        Check(RobotProfileRouting.CanInventoryKey(routedSnapshot) != inventoryKey, "USB hotplug did not invalidate constructor-only dropdown choices");
        var reorderedInventory = JsonNode.Parse("""{"devices":[{"id":"can-c","present":true},{"id":"can-b","present":true}]}""")!.AsObject();
        Check(RobotProfileRouting.CanInventoryIds(inventory, twoProfiles, reorderedInventory).SequenceEqual(new[] { "can-a", "can-b", "can-c" }), "Hotplug/reordering changed a delayed CAN dropdown index");
        Check(RobotProfileRouting.Device(twoProfiles, routedSnapshot)["id"]!.GetValue<string>() == "can-a", "Assigned adapter not resolved by stable identity");
        routedSnapshot["selected"] = "can-b";
        Reject(() => RobotProfileRouting.Device(twoProfiles, routedSnapshot), "External CAN selection allowed controlling the wrong robot");
        routedSnapshot["selected"] = "can-a"; routedSnapshot["devices"] = new JsonArray(new JsonObject { ["id"] = "can-b" });
        Reject(() => RobotProfileRouting.Device(twoProfiles, routedSnapshot), "Disconnected profile silently fell back to another adapter");
        Reject(() => RobotProfileRouting.Device(RobotProfileRouting.Normalize(new Preferences()), routedSnapshot), "Unassigned profile silently bound to a selected adapter");
        Reject(() => RobotProfileRouting.ValidateCanAssignment(twoProfiles, "piper-2", "can-a"), "Duplicate CAN assignment accepted");
        Reject(() => RobotProfileRouting.Normalize(twoProfiles with { Robots = [profileA, profileA] }), "Duplicate robot identities accepted");
        Reject(() => RobotProfileRouting.Normalize(twoProfiles with { Robots = Enumerable.Range(0, 9).Select(i => new RobotProfile("r" + i, "Robot " + i)).ToArray() }), "Unbounded camera/profile allocation accepted");
        foreach (string id in new[] { "profiles.can", "profiles.name", "profiles.pika", "profiles.wsl", "profiles.remove", "profiles.activate", "joint.6.target", "pika.target", "safety.accept", "camera.gripper.device" })
        {
            string scoped = RobotProfileRouting.ScopeEvent("piper-1", id);
            Check(RobotProfileRouting.EventForProfile("piper-1", scoped) == id, "Current robot event was lost: " + id);
            Check(RobotProfileRouting.EventForProfile("piper-2", scoped) == "", "Late previous-robot event was applied to new robot: " + id);
            Check(RobotProfileRouting.EventForProfile("piper-2", id) == "", "Unscoped robot event bypassed profile identity: " + id);
        }
        Check(RobotProfileRouting.EventForProfile("piper-2", RobotProfileRouting.ScopeEvent("piper-1", "camera.bench.toggle")) == "camera.bench.toggle", "Shared bench became profile-dependent");
        Check(RobotProfileRouting.EventForProfile("piper-2", "profiles.select.piper-1") == "profiles.select.piper-1", "Profile selection unavailable after switching");
        var reusedSlot = RobotProfileRouting.Normalize(twoProfiles with { Robots = [twoProfiles.Robots![0], new("replacement", "Replacement")] });
        Check(reusedSlot.Robots!.All(p => p.CameraSlot is >= 0 and < RobotProfileRouting.MaximumProfiles) && reusedSlot.Robots!.Select(p => p.CameraSlot).Distinct().Count() == 2, "Deleted profile's camera slot was not bounded/reusable");
        var lensProfile = JsonNode.Parse("""{"schema":1,"device":"camera-a","width":640,"height":480,"model":"kannala-brandt","fx":250,"fy":251,"cx":317,"cy":238,"coefficients":[0.01,-0.001,0,0]}""")!.AsObject();
        Check(CameraProfile.MatchesFisheye(lensProfile, "camera-a", 640, 480), "Valid fisheye profile rejected");
        Check(!CameraProfile.MatchesFisheye(lensProfile, "camera-b", 640, 480), "Lens calibration leaked to another camera");
        Check(!CameraProfile.MatchesFisheye(lensProfile, "camera-a", 1280, 720), "Calibration accepted at wrong resolution");
        var lensAsD405 = (JsonObject)lensProfile.DeepClone(); lensAsD405["streamKind"] = "color";
        Check(!CameraProfile.MatchesD405(lensAsD405, "camera-a", 640, 480, "color"), "Fisheye profile accepted for depth");
        lensProfile["fx"] = -1;
        Check(!CameraProfile.MatchesFisheye(lensProfile, "camera-a", 640, 480), "Invalid focal length accepted");
        var legacyD405 = System.Text.Json.JsonSerializer.Deserialize<CameraPreferences>("""{"Device":"d405","Enabled":false}""")!;
        Check(D405View.Mode(legacyD405) == "color" && !legacyD405.Enabled, "Legacy camera preferences did not migrate to color without changing enable state");
        Check(D405View.Mode(null) == "color" && D405View.MetricDepth("depth", new("d405", ViewMode: "depth")) && !D405View.MetricDepth("bench", new("bench", ViewMode: "depth")), "D405 stream kind leaked into another camera");
        var selectedDepth = D405View.Select(legacyD405, 1);
        Check(selectedDepth is { ViewMode: "depth", Enabled: false, Device: "d405" } && D405View.Select(selectedDepth, 0).ViewMode == "color", "View selection changed camera identity/power preference");
        bool invalidD405Index = false;
        try { D405View.Select(legacyD405, 2); } catch (ArgumentOutOfRangeException) { invalidD405Index = true; }
        Check(invalidD405Index, "Invalid D405 dropdown index silently selected a stream");
        var viewProfiles = RobotProfileRouting.Normalize(new Preferences(Depth: new("a", ViewMode: "depth")));
        viewProfiles = viewProfiles with { Robots = [viewProfiles.Robots![0], new("b", "B", Depth: new("b", ViewMode: "color"))] };
        viewProfiles = RobotProfileRouting.Activate(viewProfiles, "b");
        Check(viewProfiles.Depth?.ViewMode == "color", "Robot switch reused another D405 stream mode");
        viewProfiles = RobotProfileRouting.Activate(viewProfiles, "piper-1");
        var savedViews = RobotProfileRouting.Normalize(System.Text.Json.JsonSerializer.Deserialize<Preferences>(System.Text.Json.JsonSerializer.Serialize(viewProfiles))!);
        Check(savedViews.Depth?.ViewMode == "depth" && savedViews.Robots!.Single(p => p.Id == "b").Depth?.ViewMode == "color", "Per-robot D405 view did not survive save/reload");
        var pinhole = JsonNode.Parse("""{"schema":1,"device":"d405","width":640,"height":480,"model":"pinhole","streamKind":"depth","depthScale":0.0001,"fx":386,"fy":386,"cx":315,"cy":234,"coefficients":[0,0,0,0,0]}""")!.AsObject();
        Check(CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth"), "Rectified D405 depth profile rejected");
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "color"), "Same-serial/resolution depth intrinsics were accepted for color");
        pinhole["streamKind"] = "color"; pinhole["depthScale"] = null;
        Check(CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "color"), "Rectified D405 color profile rejected");
        Check(!CameraProfile.MatchesD405(pinhole, "other", 640, 480, "color") && !CameraProfile.MatchesD405(pinhole, "d405", 1280, 720, "color"), "D405 factory profile identity/resolution guard failed");
        pinhole["coefficients"]![0] = -.053;
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "color"), "Engine pinhole renderer accepted unsupported color distortion");
        pinhole["model"] = "realsense-distorted";
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "color"), "Unrectified factory color lens was claimed matched");
        pinhole["streamKind"] = "depth"; pinhole["model"] = "pinhole"; pinhole["coefficients"]![0] = 0.0;
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth"), "Depth profile without factory unit scale was accepted");
        pinhole["depthScale"] = 1e-9;
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth"), "Depth unit scale below the native renderer's range was accepted");
        pinhole["depthScale"] = .1;
        Check(!CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth"), "Depth unit scale above the native renderer's range was accepted");
        pinhole["depthScale"] = 1e-6;
        bool minimumScale = CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth");
        pinhole["depthScale"] = .01;
        Check(minimumScale && CameraProfile.MatchesD405(pinhole, "d405", 640, 480, "depth"), "Native renderer's inclusive depth-scale bounds were rejected");
        var joints = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad", ["position"] = 0.0 }).ToArray());
        var device = new JsonObject { ["id"] = "test-device", ["generation"] = 1L, ["fresh"] = true,
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["actual_deg"] = 90.0, ["min_deg"] = -150.0, ["max_deg"] = 150.0, ["limits_fresh"] = true }).ToArray()) };
        var link = new RobotLink("scene-1", "robot-1", joints, device);
        Check(Math.Abs(link.Feedback(device)["joints"]!["joint1"]!.GetValue<double>() - Math.PI / 2) < 1e-12, "Degree to radian conversion");
        var edit = new JsonObject { ["sceneId"] = "scene-1", ["entityId"] = "robot-1", ["joints"] = joints.DeepClone() };
        edit["joints"]![0]!["position"] = Math.PI / 2;
        Check(Math.Abs(link.Target(edit, device)[0] - 90) < 1e-12, "Radian to degree conversion");
        var oneJoint = (JsonObject)edit.DeepClone();
        oneJoint["editedJoint"] = "joint6";
        oneJoint["joints"]![5]!["position"] = 0.2;
        var oneTarget = link.Target(oneJoint, device);
        Check(oneTarget.Take(5).All(v => v == 90) && Math.Abs(oneTarget[5] - 0.2 * 180 / Math.PI) < 1e-9, "Single-joint edit moved unrelated joints from a stale scene preview");
        oneJoint["editedJoint"] = "joint7";
        Reject(() => link.Target(oneJoint, device), "Unmapped gizmo joint commanded the arm");
        var appliedPose = (JsonArray)joints.DeepClone();
        foreach (var joint in appliedPose) joint!["position"] = Math.PI / 2;
        var applied = new JsonObject { ["pose"] = new JsonObject { ["joints"] = appliedPose } };
        Check(link.FeedbackError(link.Feedback(device), applied) < 1e-9, "Verified scene feedback differs");
        appliedPose[0]!["position"] = 0.0;
        Reject(() => link.FeedbackError(link.Feedback(device), applied), "Unchanged scene falsely reported linked");
        Reject(() => link.FeedbackError(link.Feedback(device), new JsonObject()), "Missing scene acknowledgement accepted");
        var wrong = (JsonObject)edit.DeepClone(); wrong["entityId"] = "other";
        Reject(() => link.Target(wrong, device), "Wrong robot accepted");
        wrong = (JsonObject)edit.DeepClone(); wrong["sceneId"] = "other";
        Reject(() => link.Target(wrong, device), "Wrong scene accepted");
        wrong = (JsonObject)device.DeepClone(); wrong["generation"] = 2L;
        Reject(() => link.Feedback(wrong), "Reconnected device silently relinked");
        wrong = (JsonObject)device.DeepClone(); wrong["fresh"] = false;
        Reject(() => link.Feedback(wrong), "Stale feedback accepted");
        wrong = (JsonObject)device.DeepClone(); wrong["recovery_required"] = true;
        Reject(() => link.Target(edit, wrong), "Faulted arm accepted a target");
        Check(link.Feedback(wrong)["joints"]!["joint1"]!.GetValue<double>() == Math.PI / 2, "Recovery must preserve measured scene tracking");
        wrong = (JsonObject)edit.DeepClone(); wrong["joints"]![0]!["position"] = Math.PI;
        Reject(() => link.Target(wrong, device), "Over-limit scene command accepted");
        var drift = (JsonObject)device.DeepClone();
        drift["joints"]![1]!["actual_deg"] = 150.05;
        var driftEdit = (JsonObject)edit.DeepClone();
        driftEdit["joints"]![1]!["position"] = 150.05 * Math.PI / 180;
        Check(link.Target(driftEdit, drift)[1] == 150, "Unchanged minor overshoot blocked another joint or emitted an invalid target");
        driftEdit["joints"]![1]!["position"] = 150.06 * Math.PI / 180;
        Reject(() => link.Target(driftEdit, drift), "An outward overshoot target was accepted");
        drift["joints"]![1]!["actual_deg"] = 150.2;
        driftEdit["joints"]![1]!["position"] = 150.2 * Math.PI / 180;
        Reject(() => link.Target(driftEdit, drift), "A significant excursion was silently clamped");
        wrong = (JsonObject)device.DeepClone(); wrong["joints"]![0]!["limits_fresh"] = false;
        Reject(() => link.Target(edit, wrong), "Stale limits accepted");
        var badModel = (JsonArray)joints.DeepClone(); badModel[0]!["unit"] = "deg";
        Reject(() => new RobotLink("s", "e", badModel, device), "Model units ignored");

        // APP metadata is skipped; dimensions come from the frame header, preserving the
        // actual camera aspect ratio instead of assuming 4:3.
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe1, 0, 4, 12, 34, 0xff, 0xc0, 0, 8, 8, 1, 104, 2, 128, 0];
        Check(JpegDimensions.Read(jpeg) == (640, 360), "Camera aspect ratio was not read from JPEG");
        jpeg[9] = 0xc2;
        Check(JpegDimensions.Read(jpeg) == (640, 360), "Progressive JPEG dimensions rejected");
        foreach (var invalid in new[] { jpeg[..13], new byte[] { 1, 2, 3, 4 }, new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 0 },
            new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 8, 8, 0, 0, 2, 128, 0 } })
        {
            bool rejected = false;
            try { JpegDimensions.Read(invalid); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Malformed camera frame dimensions accepted");
        }

        var stopped = (JsonObject)device.DeepClone();
        stopped["recovery_required"] = true;
        stopped["status"] = new JsonObject { ["arm_status"] = 1, ["control"] = 0, ["motion_mode"] = 1, ["teach_state"] = 0, ["error"] = 0, ["stationary_s"] = 2.0 };
        foreach (var joint in stopped["joints"]!.AsArray()) { joint!["fresh"] = true; joint["enabled"] = true; joint["fault"] = 0; }
        Check(RecoveryPlan.Inspect(stopped, false).Action == "reset", "Stopped healthy drivers need one supported reset");
        Check(RecoveryPlan.Inspect(stopped, true).Action is null, "Recovery must not reset a moving arm");
        stopped["fresh"] = false;
        Check(RecoveryPlan.Inspect(stopped, false).Action is null, "Stale feedback must not authorize recovery");
        stopped["fresh"] = true; stopped["joints"]![2]!["fault"] = 1;
        Check(RecoveryPlan.Inspect(stopped, false).Action is null, "Driver fault must not blindly reset");
        stopped["joints"]![2]!["fault"] = 0; stopped["reset_pending"] = true;
        Check(RecoveryPlan.Inspect(stopped, false).Action is null, "Pending reset must not resend");
        stopped["status"]!["arm_status"] = 0;
        foreach (var joint in stopped["joints"]!.AsArray()) joint!["enabled"] = false;
        Check(RecoveryPlan.Inspect(stopped, false).Action == "verify-reset", "Completed reset should verify without motor commands");
        stopped["reset_pending"] = false;
        Check(RecoveryPlan.Inspect(stopped, false).Action == "verify-current", "Healthy stationary state needs no reset");
        stopped["status"]!["teach_state"] = 3;
        Check(RecoveryPlan.Inspect(stopped, false).Action is null, "Active teaching must not reset");

        var gate = new FollowInputGate();
        JsonObject Gesture(string phase, long number, long sequence, long timestamp = 1000) => new() {
            ["phase"] = phase, ["gesture"] = number.ToString(), ["sequence"] = sequence,
            ["timestampUnixMilliseconds"] = timestamp };
        gate.Accept(Gesture("begin", 1, 1), 1000);
        gate.Accept(Gesture("update", 1, 2), 1000);
        Check(gate.Active == "1", "Fresh gesture did not remain active");
        Reject(() => gate.Accept(Gesture("update", 1, 2), 1000), "Duplicate update replayed");
        Reject(() => gate.Accept(Gesture("update", 2, 3), 1000), "Another gesture replaced active one");
        Reject(() => gate.Accept(Gesture("update", 1, 3), 1151), "Stale target accepted");
        Reject(() => gate.Accept(Gesture("update", 1, 3, 1051), 1000), "Future target accepted");
        gate.Accept(Gesture("end", 1, 4), 2000);
        Check(gate.Active is null, "Delayed release did not end following");
        Reject(() => gate.Accept(Gesture("begin", 1, 1), 1000), "Completed gesture restarted");
        Reject(() => gate.Accept(Gesture("update", 1, 5), 1000), "Update after release resumed motion");
        gate.Accept(Gesture("begin", 2, 1), 1000);
        gate.End();
        Reject(() => gate.Accept(Gesture("update", 2, 2), 1000), "Cancelled gesture resumed motion");
        gate.Accept(Gesture("begin", 3, 1), 1000);
        Reject(() => gate.Accept(Gesture("commit", 3, 2), 1151), "Stale release committed a target");
        gate.Accept(Gesture("commit", 3, 2), 1000);
        Check(gate.Active is null, "Commit did not close the input gesture");
        Reject(() => gate.Accept(Gesture("update", 3, 3), 1000), "Update replayed after commit");
        Reject(() => gate.Accept(Gesture("commit", 3, 4), 1000), "Commit replayed twice");

        var manualDevice = (JsonObject)device.DeepClone();
        manualDevice["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 1, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 };
        foreach (var joint in manualDevice["joints"]!.AsArray()) { joint!["fresh"] = true; joint["fault"] = 0; joint["enabled"] = true; }
        Check(TeachingState.Inspect(manualDevice) is { HandGuiding: true, Active: true, BlocksTargets: true }, "Physical hand-guiding mode was not recognized");
        Check(!PipersPlugin.TeachingCanResume(manualDevice), "Hand-guiding feedback rearmed motion controls");
        manualDevice["status"]!["teach_state"] = 3;
        Check(TeachingState.Inspect(manualDevice) is { Phase: TeachingPhase.Playback, BlocksTargets: true }, "Onboard playback was not separated from CAN control");
        manualDevice["status"]!["teach_state"] = 4;
        Check(TeachingState.Inspect(manualDevice).Phase == TeachingPhase.PlaybackPaused, "Paused onboard playback was treated as stopped teaching");
        manualDevice["status"]!["teach_state"] = 2;
        Check(TeachingState.Inspect(manualDevice) is { Phase: TeachingPhase.Ended, ReturnRequired: true, CanReturn: false }, "Ended recording with sticky teach control was treated as a fault or normal CAN");
        manualDevice["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = true };
        Check(TeachingState.Inspect(manualDevice).CanReturn, "Native validated teaching return was not offered");
        manualDevice.Remove("teaching"); manualDevice["status"]!["control"] = 1;
        Check(PipersPlugin.TeachingCanResume(manualDevice), "Healthy CAN feedback could not restore previous UI approval without an enable packet");
        manualDevice["status"]!["control"] = 0;
        Check(!PipersPlugin.TeachingCanResume(manualDevice), "Intermediate standby acknowledgement prematurely restored CAN controls");
        manualDevice["status"]!["control"] = 1;
        manualDevice["joints"]![0]!["enabled"] = false;
        Check(!PipersPlugin.TeachingCanResume(manualDevice), "Teaching exit silently treated an unpowered arm as enabled");
        manualDevice["joints"]![0]!["enabled"] = true; manualDevice["recovery_required"] = true;
        Check(!PipersPlugin.TeachingCanResume(manualDevice), "Real controller recovery was bypassed by teaching exit");
        manualDevice["recovery_required"] = false; manualDevice["fresh"] = false;
        Check(!PipersPlugin.TeachingCanResume(manualDevice), "Stale teaching exit rearmed controls");
        manualDevice["fresh"] = true;
        var monitorPika = new JsonObject { ["configured"] = true };
        Check(PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Unowned configured Pika could not be monitored without enabling it");
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, false, "this-reader"), "Read-only monitoring used another robot's serial assignment");
        monitorPika["owner"] = "another-reader";
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Read-only scene linking would take over a foreign Pika session");
        monitorPika["connected"] = false; monitorPika["fresh"] = false;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "A legacy foreign owner without power evidence was reclaimed");
        monitorPika["power_requested"] = false; monitorPika["power_uncertain"] = false;
        Check(PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "A disconnected, explicitly read-only dead monitor could not recover after plugin reload");
        var legacyForeignMonitor = new JsonObject { ["configured"] = true, ["owner"] = "another-reader", ["power_requested"] = false, ["power_uncertain"] = false };
        Check(!PikaMonitorConnectionPolicy.MayConnect(legacyForeignMonitor, true, "this-reader"), "False power flags alone reclaimed a legacy foreign owner with unknown connection state");
        legacyForeignMonitor["connected"] = false;
        Check(!PikaMonitorConnectionPolicy.MayConnect(legacyForeignMonitor, true, "this-reader"), "Missing foreign freshness evidence was treated as a dead monitor");
        Check(PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.None), "Linked read-only telemetry did not retry a dead monitor");
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", false, PikaSessionRole.None), "Unlinked scene started an automatic Pika connection");
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, false, "this-reader", true, PikaSessionRole.None), "Retry connected a mismatched gripper assignment");
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.Owned), "Read-only retry ran during a power-owned session");
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.None, armMoving: true), "WSL monitor startup competed with an active arm movement");
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.None, teleoperating: true), "WSL monitor startup competed with viewport following");
        monitorPika["connected"] = true;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Foreign live warmup could be claimed by a read-only retry");
        monitorPika["connected"] = false; monitorPika["fresh"] = true;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Fresh shared foreign telemetry was claimed");
        monitorPika["fresh"] = false; monitorPika["power_requested"] = true;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "A foreign owner with a power request was reclaimed");
        monitorPika["power_requested"] = false; monitorPika["power_uncertain"] = true;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Unresolved foreign motor power was reclaimed");
        monitorPika["power_uncertain"] = false;
        monitorPika["owner"] = "this-reader";
        Check(PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "The same reader could not retain its existing monitor");
        monitorPika["connected"] = true;
        Check(!PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.Monitoring), "A healthy current read-only monitor reconnected every update");
        monitorPika["connected"] = false;
        Check(PikaMonitorConnectionPolicy.ShouldRetry(monitorPika, true, "this-reader", true, PikaSessionRole.Monitoring), "A previously connected reader never retried after its agent exited");
        monitorPika["power_uncertain"] = true;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "A read-only retry resumed the same owner's unresolved power attempt");
        monitorPika["power_uncertain"] = false;
        monitorPika["configured"] = false;
        Check(!PikaMonitorConnectionPolicy.MayConnect(monitorPika, true, "this-reader"), "Scene linking tried to configure or open an unassigned gripper");

        var resetGapDevice = new JsonObject { ["id"] = "reset-arm", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = false };
        var resetGapSnapshot = new JsonObject { ["selected"] = "reset-arm", ["devices"] = new JsonArray(resetGapDevice) };
        Check(TeachingResetAssisted.ExpectedFeedbackGap(resetGapSnapshot, "reset-arm", 7), "Expected MCU reset gap discarded an unchanged connected scene binding");
        resetGapDevice["generation"] = 8L;
        Check(!TeachingResetAssisted.ExpectedFeedbackGap(resetGapSnapshot, "reset-arm", 7), "A reconnected adapter silently inherited the previous reset binding");
        resetGapDevice["generation"] = 7L; resetGapDevice["present"] = false;
        Check(!TeachingResetAssisted.ExpectedFeedbackGap(resetGapSnapshot, "reset-arm", 7), "Physical USB loss was hidden as an expected MCU reset gap");
    }
}
