using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class MeasuredTrackingPolicy
{
    // Existing assignments opt into feedback only. Explicit Unlink is durable.
    public static bool Enabled(RobotProfile profile) => profile.RememberMeasuredLink != false &&
        profile.RobotId.Length > 0 && profile.CanDeviceId.Length > 0;
}

public sealed partial class PipersPlugin
{
    // StartAsync sets this only for the running application, never test fixtures.
    private bool passiveMeasuredTrackingStarted;
    private long measuredTrackingAttemptAt;
    private bool measuredTrackingSceneUnverified;

    private void RememberMeasuredTracking(bool enabled)
    {
        var profile = ActiveProfile with { RememberMeasuredLink = enabled };
        preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == profile.Id ? profile : p).ToArray() };
    }

    private async Task RestoreMeasuredTracking(CancellationToken cancellation)
    {
        if (SimulationFollowingActive || !passiveMeasuredTrackingStarted || !MeasuredTrackingPolicy.Enabled(ActiveProfile) || switchingProfile || teachingResetBusy ||
            teleoperating || followRequest != 0 || state?["motion"]?["active"]?.GetValue<bool>() == true ||
            measuredTrackingAttemptAt != 0 && Stopwatch.GetElapsedTime(measuredTrackingAttemptAt).TotalSeconds < 1) return;
        // Healthy tracking already receives a verified robot.feedback reply.
        // Never stall live gestures by polling discovery while holding their gate.
        if (!measuredTrackingSceneUnverified && link is { } linked && linkBackendToken == token &&
            linked.EntityId == preferences.RobotId && linked.JointPrefix == ActiveProfile.RobotJointPrefix && linked.DeviceId == ActiveProfile.CanDeviceId &&
            telemetry.Latest is { } receipt && receipt.IsFresh(Stopwatch.GetTimestamp()) &&
            TelemetryScope.Read(receipt.State, ActiveProfile.CanDeviceId) == new TelemetryScope(linkBackendToken, linked.DeviceId, linked.Generation)) return;
        measuredTrackingAttemptAt = Stopwatch.GetTimestamp();
        // Never queue behind an operator action or block telemetry ingestion.
        if (!await commands.WaitAsync(0, cancellation)) return;
        try
        {
            if (!MeasuredTrackingPolicy.Enabled(ActiveProfile) || switchingProfile || teachingResetBusy) return;
            await StartMeasuredLink(automatic: true, cancellation);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            // Keep the last same-identity binding through a temporary editor
            // delay. The normal feedback publisher still enforces freshness.
            sceneTrackingStatus = "Measured link waiting: " + error.Message;
        }
        finally { commands.Release(); }
    }

    private async Task StartMeasuredLink(bool automatic, CancellationToken cancellation)
    {
        var profile = ActiveProfile;
        string expectedToken = token, expectedRobot = preferences.RobotId;
        long permissionEpoch = Interlocked.Read(ref targetPermissionEpoch);
        var initial = Device(expectedToken: expectedToken);
        string deviceId = initial["id"]!.GetValue<string>();
        long generation = initial["generation"]!.GetValue<long>();

        JsonObject CheckScope()
        {
            var current = ActiveProfile;
            if (current.Id != profile.Id || current.CanDeviceId != profile.CanDeviceId ||
                current.PikaDevice != profile.PikaDevice || current.WslDistribution != profile.WslDistribution ||
                current.RobotJointPrefix != profile.RobotJointPrefix ||
                preferences.RobotId != expectedRobot || permissionEpoch != Interlocked.Read(ref targetPermissionEpoch) ||
                automatic && (!passiveMeasuredTrackingStarted || !MeasuredTrackingPolicy.Enabled(current)))
                throw new InvalidOperationException("Robot assignment or permission changed while linking measured feedback.");
            var device = Device(deviceId, generation, expectedToken);
            if (device["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true || generation <= 0)
                throw new InvalidOperationException("The assigned CAN adapter is not online with a verified connection identity.");
            return device;
        }

        CheckScope();
        var discovered = await ReadScene("scene.robots");
        CheckScope();
        if (!automatic && discovered["robots"] is JsonArray { Count: 1 } available && expectedRobot.Length == 0)
        {
            expectedRobot = available[0]!["entityId"]!.GetValue<string>();
            preferences = preferences with { RobotId = expectedRobot };
        }
        string sceneId = discovered["sceneId"]?.GetValue<string>() ?? "";
        var robot = discovered["robots"]?.AsArray().OfType<JsonObject>().SingleOrDefault(r => r["entityId"]?.GetValue<string>() == expectedRobot);
        if (sceneId.Length == 0 || expectedRobot.Length == 0 || robot is null)
        {
            if (link is not null) { PauseSceneFollowing(); link = null; }
            throw new InvalidOperationException("This scene does not contain the robot assigned to this profile. Select its scene robot in Settings.");
        }
        scene = discovered;
        if (link is { } previous)
        {
            PauseSceneFollowing();
            // A delayed editor acknowledgement must not tear down a verified
            // same-identity measured binding. A changed scene/device mapping
            // still invalidates it before any feedback can be published.
            if (previous.SceneId != sceneId || previous.EntityId != expectedRobot ||
                previous.JointPrefix != profile.RobotJointPrefix || previous.DeviceId != deviceId || previous.Generation != generation ||
                linkBackendToken != expectedToken) link = null;
        }
        var next = new RobotLink(sceneId, expectedRobot, robot["joints"]!.AsArray(), CheckScope(), profile.RobotJointPrefix);
        var requested = next.Feedback(CheckScope(), state?["pika"] as JsonObject, PikaMatchesProfile(),
            PikaDisplayReferenceAngle(state?["pika"] as JsonObject));
        var applied = await ReadScene("robot.feedback", requested);
        CheckScope();
        if (applied["applied"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Finish the active viewport joint edit so measured tracking can resume.");
        CheckScenePose(applied["pose"] as JsonObject);
        double error = next.FeedbackError(requested, applied);
        CheckScope();
        var subscribed = await ReadScene("robot.subscribe", new JsonObject { ["sceneId"] = next.SceneId, ["entityId"] = next.EntityId });
        var currentDevice = CheckScope();
        CheckScenePose(subscribed);
        // Passive restoration never restores target permission, motor power,
        // approval, a previous trajectory, recovery or teleoperation.
        linkCommandsEnabled = !automatic && currentDevice["recovery_required"]?.GetValue<bool>() != true;
        linkErrorDegrees = error; linkBackendToken = expectedToken; link = next;
        measuredTrackingSceneUnverified = false;
        sceneTrackingStatus = automatic ? "Measured scene tracking restored · read-only" : "Measured scene link established";
        RetryLinkedPikaMonitor();
        if (!automatic) await Save(cancellation);

        async Task<JsonObject> ReadScene(string method, JsonObject? body = null)
        {
            // The editor serializes each RPC with rendering. Give each bounded
            // round trip its own budget instead of timing out the three-step
            // handshake after an otherwise healthy first reply.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(400));
            if (primaryActionBusy) await WritePrimaryActionReceipt("link-scene." + method);
            return JsonNode.Parse((await engine.RequestAsync(method, body, deadline.Token)).GetRawText())!.AsObject();
        }

        void CheckScenePose(JsonObject? pose)
        {
            if (pose?["sceneId"]?.GetValue<string>() != sceneId || pose["entityId"]?.GetValue<string>() != expectedRobot)
                throw new InvalidOperationException("The editor acknowledged a different scene or robot; measured link was not restored.");
        }
    }
}
