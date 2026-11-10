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
    // The panel refresh waits on the publication gate, which navigation can hold
    // for a long time. It is bounded so follow-up passes always end.
    internal static readonly TimeSpan StopViewBudget = TimeSpan.FromSeconds(4);
    private readonly object stopFollowUpGate = new();
    private Task stopFollowUp = Task.CompletedTask;
    private bool stopFollowUpGroup, stopFollowUpQueued;

    // This path deliberately does not wait for the ordinary UI command gate or
    // for editor requests. The backend attempts the two channels independently.
    private async Task StopImmediately(RobotProfile profile, string expectedToken, bool groupActive)
    {
        follow.CancelInput("Operator STOP · follower group discarded");
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
        message = "STOP requested · previous targets discarded · verifying hardware";
        using (var timeout = new CancellationTokenSource(StopAcknowledgementBudget))
        try
        {
            if (!groupActive && deviceId.Length == 0) throw new InvalidOperationException("Assign this robot's CAN adapter before sending its STOP.");
            // A group STOP names no arm or gripper: the backend stops and holds everything.
            var armStop = groupActive ? PiperApi.Requests.Stop(owner) : PiperApi.Requests.Stop(owner, deviceId);
            var first = !ownPika ? armStop : groupActive ? PiperApi.Requests.CoordinatedStop(owner)
                : PiperApi.Requests.CoordinatedStop(owner, deviceId, pikaDevice: profile.PikaDevice, pikaDistribution: profile.PikaDistribution);
            ControlReply reply;
            bool legacyCoordinator = false;
            try
            {
                reply = await client.ExecuteAsync(new ControlRequest { Route = first.Route.Path, Json = first.Json },
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
                reply = await client.ExecuteAsync(new ControlRequest { Route = armStop.Route.Path, Json = armStop.Json },
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
        // The handler returns as soon as the hardware answered (or its budget ran
        // out): saving observations and updating the editor run detached, so a
        // held publication gate or slow disk can never delay the next STOP.
        QueueStopFollowUp(groupActive);
    }

    /// <summary>The detached post-STOP work: completes when every queued pass has ended.
    /// It never faults; failures are reported to the operator by <see cref="RunStopFollowUp"/>.</summary>
    internal Task StopFollowUp { get { lock (stopFollowUpGate) return stopFollowUp; } }

    // At most one pass runs and one waits. A STOP that arrives while a pass is
    // waiting is covered by that pass, so repeated STOP clicks cannot pile up work.
    private void QueueStopFollowUp(bool groupActive)
    {
        lock (stopFollowUpGate)
        {
            stopFollowUpGroup |= groupActive;
            if (stopFollowUpQueued) return;
            stopFollowUpQueued = true;
            Task previous = stopFollowUp;
            stopFollowUp = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                bool group;
                lock (stopFollowUpGate) { group = stopFollowUpGroup; stopFollowUpGroup = stopFollowUpQueued = false; }
                await RunStopFollowUp(group).ConfigureAwait(false);
            });
        }
    }

    private async Task RunStopFollowUp(bool groupActive)
    {
        var failures = new List<string>();
        async Task Step(string label, Func<CancellationToken, Task> step, TimeSpan budget)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                deadline.CancelAfter(budget);
                await step(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { } // The plugin is closing.
            catch (OperationCanceledException) { failures.Add(label + " did not finish within " + budget.TotalSeconds.ToString("0") + " s"); }
            catch (Exception error) { failures.Add(label + " failed: " + error.Message); }
        }
        if (groupActive) await Step("Follower group retirement", ct => StopGroupSimulationFollowing("Operator STOP · follower group discarded", ct), StopCleanupBudget);
        await Step("Teaching save", ct => FinishPikaTeaching("Operator STOP", ct), StopCleanupBudget);
        await Step("Panel refresh", Update, StopViewBudget);
        if (failures.Count == 0) return;
        string report = "STOP was sent, but its follow-up is incomplete: " + string.Join("; ", failures) +
            ". Motion permission stays cleared; inspect the robot before continuing.";
        System.Diagnostics.Trace.TraceError("{0}", report);
        uiActionError = report;
    }
}
