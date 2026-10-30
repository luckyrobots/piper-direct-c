using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

int checks = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
void Reject(Action action, string description)
{
    try { action(); } catch (InvalidOperationException) { checks++; return; }
    throw new Exception(description);
}
using (var authorized = System.Text.Json.JsonDocument.Parse("{\"owner\":\"plugin-session\"}"))
using (var other = System.Text.Json.JsonDocument.Parse("{\"owner\":\"another-client\"}"))
using (var absent = System.Text.Json.JsonDocument.Parse("{}"))
{
    Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("", "/api/command", absent.RootElement), "External server behavior changed");
    Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", "/api/query", other.RootElement), "Readback query unnecessarily blocked");
    foreach (string route in new[] { "/api/command", "/api/stop", "/api/reconnect", "/api/pika/connect", "/api/pika/configure", "/api/shutdown" })
    {
        Check(LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, authorized.RootElement), "Owner rejected for " + route);
        Check(!LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, other.RootElement), "Another client acquired session for " + route);
        Check(!LuckyRobots.Piper.SessionOwnerGuard.Allows("plugin-session", route, absent.RootElement), "Missing owner accepted for " + route);
    }
}
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
Check(RobotProfileRouting.Active(legacyProfiles) is { PikaDevice: "/dev/pika-a", WslDistribution: "CustomUbuntu", CameraSlot: 0 }, "Legacy Pika identity/distro did not migrate");
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
var reloadedBindings = RobotProfileRouting.RefreshCameraBindings(twoProfiles, "new-scene-session", ["bench-scene-camera", "wrist-a", "wrist-b"]);
Check(reloadedBindings.SimulationCameras!["bench"].SceneId == "new-scene-session" && reloadedBindings.SimulationCameras["gripper"].SceneId == "new-scene-session" &&
    reloadedBindings.Robots![1].SimulationCameras!["gripper"].SceneId == "new-scene-session", "Scene reload left an inactive robot or shared bench bound to the previous scene session");
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
Check(CameraProfile.Matches(lensProfile, "camera-a", 640, 480, false), "Valid fisheye profile rejected");
Check(!CameraProfile.Matches(lensProfile, "camera-b", 640, 480, false), "Lens calibration leaked to another camera");
Check(!CameraProfile.Matches(lensProfile, "camera-a", 1280, 720, false), "Calibration accepted at wrong resolution");
Check(!CameraProfile.Matches(lensProfile, "camera-a", 640, 480, true), "Fisheye profile accepted for depth");
lensProfile["fx"] = -1;
Check(!CameraProfile.Matches(lensProfile, "camera-a", 640, 480, false), "Invalid focal length accepted");
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
Check(TeachingState.Inspect(manualDevice) is { Phase: "playback", BlocksTargets: true }, "Onboard playback was not separated from CAN control");
manualDevice["status"]!["teach_state"] = 4;
Check(TeachingState.Inspect(manualDevice).Phase == "playback-paused", "Paused onboard playback was treated as stopped teaching");
manualDevice["status"]!["teach_state"] = 2;
Check(TeachingState.Inspect(manualDevice) is { Phase: "ended", ReturnRequired: true, CanReturn: false }, "Ended recording with sticky teach control was treated as a fault or normal CAN");
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
var reference = new AlignmentReference("Desk cross", .205, .75, 0, 80, "grasp_site");
AlignmentCheck.ValidateReference(reference);
Reject(() => AlignmentCheck.ValidateReference(reference with { ObjectHeightMm = double.NaN }), "Invalid reference height entered a calibration report");
var checkSession = new AlignmentCheck("profile-a", "test-device", 1, "scene-1", "robot-1", reference, "S-V1.9-0");
var checkedPose = new JsonObject { ["pose"] = new JsonObject { ["sceneId"] = "scene-1", ["entityId"] = "robot-1", ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["position"] = Math.PI / 2 }).ToArray()) } };
var observation = checkSession.Read(1000, "profile-a", manualDevice, link, checkedPose, 999);
checkSession.Sample(observation); checkSession.Sample(observation with { HostUnixMilliseconds = 1050 });
Check(checkSession.Samples == 1, "Alignment trace exceeded its 10 Hz bound");
checkSession.Observe(observation, true, null);
Reject(() => checkSession.Observe(observation, true, null), "Repeated identical approach counted as independent spatial evidence");
Reject(() => checkSession.Read(1600, "profile-a", manualDevice, link, checkedPose, 999), "Stale scene acknowledgement accepted as current alignment");
Reject(() => checkSession.Read(1000, "profile-b", manualDevice, link, checkedPose, 999), "Another robot's alignment observation was accepted");
var oldSceneAcknowledgement = (JsonObject)checkedPose.DeepClone(); oldSceneAcknowledgement["pose"]!["sceneId"] = "old-scene";
Reject(() => checkSession.Read(1000, "profile-a", manualDevice, link, oldSceneAcknowledgement, 999), "A previous scene's cached pose entered an alignment report");
var mappedGripperModel = (JsonArray)joints.DeepClone();
mappedGripperModel.Add(new JsonObject { ["name"] = "joint7", ["unit"] = "m", ["limited"] = true, ["min"] = -.05, ["max"] = 0.0 });
mappedGripperModel.Add(new JsonObject { ["name"] = "joint8", ["unit"] = "m", ["limited"] = true, ["min"] = 0.0, ["max"] = .05 });
var alignmentWithGripper = new RobotLink("scene-1", "robot-1", mappedGripperModel, manualDevice);
var gripperRequest = alignmentWithGripper.Feedback(manualDevice, new JsonObject { ["fresh"] = true,
    ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = LuckyRobots.Piper.Pika.PikaProtocol.Angle(30) } } }, true);
var gripperAcknowledgement = (JsonObject)checkedPose.DeepClone();
gripperAcknowledgement["pose"]!["joints"]!.AsArray().Add(new JsonObject { ["name"] = "joint7", ["position"] = gripperRequest["joints"]!["joint7"]!.GetValue<double>() });
gripperAcknowledgement["pose"]!["joints"]!.AsArray().Add(new JsonObject { ["name"] = "joint8", ["position"] = gripperRequest["joints"]!["joint8"]!.GetValue<double>() });
alignmentWithGripper.FeedbackError(gripperRequest, gripperAcknowledgement);
string gripperStatusBeforeObservation = alignmentWithGripper.GripperStatus;
double? gripperErrorBeforeObservation = alignmentWithGripper.GripperErrorMm;
checkSession.Read(1000, "profile-a", manualDevice, alignmentWithGripper, gripperAcknowledgement, 999);
Check(alignmentWithGripper.GripperStatus == gripperStatusBeforeObservation && alignmentWithGripper.GripperErrorMm == gripperErrorBeforeObservation && gripperErrorBeforeObservation == 0,
    "Reading an alignment observation reset or unlinked independently verified Pika feedback");
var alignmentReport = checkSession.Finish("Test complete");
Check(Math.Abs(alignmentReport.Observations.Single().ReferenceTopMetres[1] - .83) < 1e-12, "Known object height was measured from the rendered cross rather than physical desk surface");
Check(!alignmentReport.MetricCalibrationValid && !alignmentReport.ExposureSynchronized && alignmentReport.JointUnit == "degrees" && alignmentReport.Firmware == "S-V1.9-0",
    "Copied joint angles or operator confirmation were incorrectly promoted to metric/timing calibration");
Reject(() => checkSession.Observe(observation, true, null), "Finished alignment report accepted new observations");
GripperLinkTests.Run(Check);
var monitorPika = new JsonObject { ["configured"] = true };
Check(PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Unowned configured Pika could not be monitored without enabling it");
Check(!PikaMonitorPolicy.MayConnect(monitorPika, false, "this-reader"), "Read-only monitoring used another robot's serial assignment");
monitorPika["owner"] = "another-reader";
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Read-only scene linking would take over a foreign Pika session");
monitorPika["connected"] = false; monitorPika["fresh"] = false;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "A legacy foreign owner without power evidence was reclaimed");
monitorPika["power_requested"] = false; monitorPika["power_uncertain"] = false;
Check(PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "A disconnected, explicitly read-only dead monitor could not recover after plugin reload");
var legacyForeignMonitor = new JsonObject { ["configured"] = true, ["owner"] = "another-reader", ["power_requested"] = false, ["power_uncertain"] = false };
Check(!PikaMonitorPolicy.MayConnect(legacyForeignMonitor, true, "this-reader"), "False power flags alone reclaimed a legacy foreign owner with unknown connection state");
legacyForeignMonitor["connected"] = false;
Check(!PikaMonitorPolicy.MayConnect(legacyForeignMonitor, true, "this-reader"), "Missing foreign freshness evidence was treated as a dead monitor");
Check(PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, false, false), "Linked read-only telemetry did not retry a dead monitor");
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", false, false, false), "Unlinked scene started an automatic Pika connection");
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, false, "this-reader", true, false, false), "Retry connected a mismatched gripper assignment");
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, true, false), "Read-only retry ran during a power-owned session");
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, false, false, armMoving: true), "WSL monitor startup competed with an active arm movement");
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, false, false, teleoperating: true), "WSL monitor startup competed with viewport following");
monitorPika["connected"] = true;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Foreign live warmup could be claimed by a read-only retry");
monitorPika["connected"] = false; monitorPika["fresh"] = true;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Fresh shared foreign telemetry was claimed");
monitorPika["fresh"] = false; monitorPika["power_requested"] = true;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "A foreign owner with a power request was reclaimed");
monitorPika["power_requested"] = false; monitorPika["power_uncertain"] = true;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Unresolved foreign motor power was reclaimed");
monitorPika["power_uncertain"] = false;
monitorPika["owner"] = "this-reader";
Check(PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "The same reader could not retain its existing monitor");
monitorPika["connected"] = true;
Check(!PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, false, true), "A healthy current read-only monitor reconnected every update");
monitorPika["connected"] = false;
Check(PikaMonitorPolicy.ShouldRetry(monitorPika, true, "this-reader", true, false, true), "A previously connected reader never retried after its agent exited");
monitorPika["power_uncertain"] = true;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "A read-only retry resumed the same owner's unresolved power attempt");
monitorPika["power_uncertain"] = false;
monitorPika["configured"] = false;
Check(!PikaMonitorPolicy.MayConnect(monitorPika, true, "this-reader"), "Scene linking tried to configure or open an unassigned gripper");

await TeachingResetTests.Run(Check);
var resetGapDevice = new JsonObject { ["id"] = "reset-arm", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = false };
var resetGapSnapshot = new JsonObject { ["selected"] = "reset-arm", ["devices"] = new JsonArray(resetGapDevice) };
Check(TeachingResetAssisted.ExpectedFeedbackGap(true, resetGapSnapshot, "reset-arm", 7), "Expected MCU reset gap discarded an unchanged connected scene binding");
Check(!TeachingResetAssisted.ExpectedFeedbackGap(false, resetGapSnapshot, "reset-arm", 7), "Ordinary stale feedback silently preserved a live scene link");
resetGapDevice["generation"] = 8L;
Check(!TeachingResetAssisted.ExpectedFeedbackGap(true, resetGapSnapshot, "reset-arm", 7), "A reconnected adapter silently inherited the previous reset binding");
resetGapDevice["generation"] = 7L; resetGapDevice["present"] = false;
Check(!TeachingResetAssisted.ExpectedFeedbackGap(true, resetGapSnapshot, "reset-arm", 7), "Physical USB loss was hidden as an expected MCU reset gap");

if (args.Length == 1)
{
    // This optional integration suite is restricted to the separate simulated server.
    if (args[0] != "http://127.0.0.1:18766") throw new ArgumentException("Use the isolated simulation endpoint on 18766.");
    using var channel = GrpcChannel.ForAddress(args[0]);
    var client = new PiperControl.PiperControlClient(channel);
    var frame = await client.GetStateAsync(new(), deadline: DateTime.UtcNow.AddSeconds(5));
    var snapshot = JsonNode.Parse(frame.Json)!.AsObject();
    Check(frame.Sequence > 0 && frame.ClockDomain == "host-utc", "Snapshot metadata missing");
    string token = snapshot["token"]!.GetValue<string>();
    try { await client.ExecuteAsync(new() { Route = "/api/query", Json = "{}" }); throw new Exception("Unauthenticated command accepted"); }
    catch (RpcException e) when (e.StatusCode == StatusCode.Unauthenticated) { checks++; }
    try { await client.ExecuteAsync(new() { Route = "/api/query", Json = "{\"a\":1,\"a\":2}" }, new Metadata { { "x-piper-token", token } }); throw new Exception("Duplicate command keys accepted"); }
    catch (RpcException e) when (e.StatusCode == StatusCode.InvalidArgument) { checks++; }
    var pika = await client.ExecuteAsync(new() { Route = "/api/pika/connect", Json = "{}" }, new Metadata { { "x-piper-token", token } });
    Check(pika.Status == 409, "Simulation opened real Pika hardware");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var stream = client.WatchState(new(), cancellationToken: cancellation.Token);
    long previous = 0;
    for (int i = 0; i < 5; i++)
    {
        Check(await stream.ResponseStream.MoveNext(cancellation.Token), "State stream ended");
        Check(stream.ResponseStream.Current.Sequence > previous, "Non-monotonic stream sequence");
        previous = stream.ResponseStream.Current.Sequence;
    }
}
Console.WriteLine($"Passed {checks} Pipers mapping, reconnect, limits and gRPC checks.");
