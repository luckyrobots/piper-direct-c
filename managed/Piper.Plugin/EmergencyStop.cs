using System.Text.Json.Nodes;
using Grpc.Core;
using LuckyRobots.Piper.Grpc;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private static readonly TimeSpan StopAcknowledgementBudget = TimeSpan.FromSeconds(4);
    // Post-STOP cleanup runs after the hardware request, with its own budget, so
    // a slow STOP RPC never starts group retirement or the teaching save cancelled.
    private static readonly TimeSpan StopCleanupBudget = TimeSpan.FromSeconds(4);

    // This path deliberately does not wait for the ordinary UI command gate or
    // for editor requests. The backend attempts the two channels independently.
    private async Task StopImmediately(RobotProfile profile, string expectedToken, bool groupActive)
    {
        CancelGroupSimulationFollowingInput("Operator STOP · follower group discarded");
        string deviceId = profile.CanDeviceId;
        bool ownPika = groupActive;
        if (!ownPika && state is { } snapshot)
        {
            try { ownPika = ProfilePikaRouting.Find(snapshot, profile)?["owner"]?.GetValue<string>() == owner; }
            catch (InvalidOperationException) { } // Ambiguous Pika evidence cannot block arm STOP.
        }
        DisarmTelemetryTargets("Operator STOP; previous actions discarded", clearLink: false, forgetApproval: profile.Id, forgetAllApprovals: groupActive);
        CancelHandGuideRecovery();
        uiEvents?.InvalidateControlEvents();
        recoveryInProgress = false;
        message = "STOP requested · previous targets discarded · verifying hardware";
        using (var timeout = new CancellationTokenSource(StopAcknowledgementBudget))
        try
        {
            if (!groupActive && deviceId.Length == 0) throw new InvalidOperationException("Assign this robot's CAN adapter before sending its STOP.");
            var body = new JsonObject { ["owner"] = owner, ["device_id"] = deviceId,
                ["pika_device"] = profile.PikaDevice, ["pika_distribution"] = profile.PikaDistribution };
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
        using var cleanup = new CancellationTokenSource(StopCleanupBudget);
        if (groupActive)
        {
            try { await StopGroupSimulationFollowing("Operator STOP · follower group discarded", cleanup.Token); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Group STOP: {0}", error.Message); }
        }
        try { await FinishPikaTeaching("Operator STOP", cleanup.Token); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Teaching stop save: {0}", error.Message); }
        try { await Update(shutdown.Token); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("STOP view: {0}", error.Message); }
    }
}
