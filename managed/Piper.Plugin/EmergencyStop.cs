using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // This path deliberately does not wait for the ordinary UI command gate or
    // for editor requests. The backend attempts the two channels independently.
    private async Task StopImmediately(CancellationToken ignoredCallerCancellation)
    {
        bool groupActive = GroupFollowActive;
        CancelGroupSimulationFollowingInput("Operator STOP · follower group discarded");
        string deviceId = ActiveProfile.CanDeviceId, expectedToken = token;
        bool ownPika = groupActive || PikaMatchesProfile() && state?["pika"]?["owner"]?.GetValue<string>() == owner;
        if (teachingResetBusy) CancelResetAssisted();
        CancelRecoveryHome();
        CancelHandGuideRecovery();
        ForgetControlApproval();
        DisarmTelemetryTargets("Operator STOP; previous actions discarded", clearLink: false);
        uiEvents?.InvalidateControlEvents();
        recoveryInProgress = false;
        message = "STOP requested · previous targets discarded · verifying hardware";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (!groupActive && deviceId.Length == 0) throw new InvalidOperationException("Assign this robot's CAN adapter before sending its STOP.");
            var body = new JsonObject { ["owner"] = owner, ["device_id"] = deviceId,
                ["pika_device"] = ActiveProfile.PikaDevice, ["pika_distribution"] = ActiveProfile.WslDistribution };
            if (groupActive) { body.Remove("device_id"); body.Remove("pika_device"); body.Remove("pika_distribution"); }
            ControlReply reply;
            bool legacyCoordinator = false;
            try
            {
                reply = await client.ExecuteAsync(new ControlRequest {
                    Route = ownPika ? "/api/stop/coordinated" : "/api/stop", Json = body.ToJsonString() },
                    new Metadata { { "x-piper-token", expectedToken } },
                    deadline: DateTime.UtcNow.AddSeconds(4), cancellationToken: timeout.Token);
                legacyCoordinator = ownPika && reply.Status == 404;
            }
            catch (RpcException error) when (ownPika && error.StatusCode is StatusCode.InvalidArgument or StatusCode.Unimplemented)
            {
                legacyCoordinator = true;
                reply = new ControlReply();
            }
            if (legacyCoordinator)
            {
                // Older external backends must still receive the arm STOP.
                reply = await client.ExecuteAsync(new ControlRequest { Route = "/api/stop", Json = body.ToJsonString() },
                    new Metadata { { "x-piper-token", expectedToken } },
                    deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: timeout.Token);
                message = reply.Status is >= 200 and < 300
                    ? "Arm STOP acknowledged · external backend cannot coordinate Pika hold; gripper state unverified"
                    : "STOP could not be acknowledged · inspect the controller's hardware state";
            }
            else if (ownPika)
            {
                var result = JsonNode.Parse(reply.Json);
                bool arm = result?["arm"]?["ok"]?.GetValue<bool>() == true;
                bool pika = result?["pika"]?["ok"]?.GetValue<bool>() == true;
                message = (arm ? "Arm STOP acknowledged" : "Arm STOP unverified") + " · " +
                    (pika ? "Pika hold acknowledged" : "Pika hold unverified") + " · inspect live feedback";
            }
            else message = reply.Status is >= 200 and < 300
                ? "Arm STOP acknowledged · inspect live feedback" : "Arm STOP unverified · inspect hardware";
        }
        catch (Exception error)
        {
            message = "STOP acknowledgement unavailable: " + error.Message + ". Verify the robot's hardware stop; no previous action will resume.";
        }
        // Saving observations and updating the editor happen after the stop
        // request, so a slow renderer or disk cannot hold up the hardware path.
        if (groupActive)
        {
            try { await StopGroupSimulationFollowing("Operator STOP · follower group discarded", timeout.Token); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Group STOP: {0}", error.Message); }
        }
        try { await FinishPikaTeaching("Operator STOP", timeout.Token); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Teaching stop save: {0}", error.Message); }
        try { await FinishAlignment("Operator STOP", timeout.Token); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Alignment stop save: {0}", error.Message); }
        try { await Update(shutdown.Token); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("STOP view: {0}", error.Message); }
    }
}
