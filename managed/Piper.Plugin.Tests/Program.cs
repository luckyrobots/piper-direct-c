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
    foreach (string route in new[] { "/api/command", "/api/stop", "/api/reconnect", "/api/pika/connect", "/api/shutdown" })
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
