using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-D teaching ownership for the tracked CAN device and generation:
/// None → ObservedWithoutApproval | ObservedAfterApproval when the physical controller
/// takes the arm, recording whether this session's control approval existed then.
/// It returns to None when CAN control is restored, on a connection or device change,
/// and on STOP or a verified recovery.</summary>
internal enum TeachingOwnership { None, ObservedWithoutApproval, ObservedAfterApproval }

public sealed partial class PipersPlugin
{
    private TeachingState teaching = TeachingState.Inspect(null);
    private string teachingDevice = "";
    private long teachingGeneration;
    private TeachingOwnership teachingOwnership;

    /// <summary>Records the hand-over to physical teaching once, keeping the approval that existed then.</summary>
    private void ObserveTeachingOwnership()
    {
        if (teachingOwnership == TeachingOwnership.None)
            teachingOwnership = approved ? TeachingOwnership.ObservedAfterApproval : TeachingOwnership.ObservedWithoutApproval;
    }
    private readonly TeachingReturnGate teachingReturnGate = new();
    private JsonObject? acknowledgedScenePose;

    private async Task ObserveTeaching(CancellationToken cancellation)
    {
        long observedEpoch = CommandAuthority.Epoch;
        JsonObject? device = null;
        try { device = Device(); } catch (InvalidOperationException) { }
        TeachingState next = TeachingState.Inspect(device);
        string currentDevice = device?["id"]?.GetValue<string>() ?? "";
        long generation = device?["generation"]?.GetValue<long>() ?? 0;
        if (teachingDevice != currentDevice || teachingGeneration != generation)
        {
            teachingOwnership = TeachingOwnership.None;
            teachingDevice = currentDevice; teachingGeneration = generation;
        }
        if (next.BlocksTargets)
        {
            bool ownershipChanged = teachingOwnership == TeachingOwnership.None || next.Active && next.Phase != teaching.Phase;
            if (!CommandAuthority.TryInEpoch(observedEpoch, () =>
            {
                ObserveTeachingOwnership();
                approved = linkCommandsEnabled = false;
            })) return;
            // The controller has already yielded ownership to its physical teaching
            // mode. Discard the old gesture locally; do not inject a hold/STOP into it.
            if (ownershipChanged) { CommandAuthority.ClearTracked(); followRequest = 0; followInput.End(); }
            if (teleoperating)
            {
                teleoperating = false;
                if (link is { } current)
                    await engine.RequestAsync("robot.teleoperate", new JsonObject {
                        ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId, ["enabled"] = false }, cancellation);
            }
            if (next.Phase != teaching.Phase) message = next.Label + ". " + next.NextStep;
        }
        else if (!ResumeControlsBusy && teachingOwnership != TeachingOwnership.None && device is not null && TeachingCanResume(device))
        {
            // Restore only a previous UI approval. This sends no enable, reset,
            // replay or position packet, and a new gesture is still required.
            if (!CommandAuthority.TryInEpoch(observedEpoch, () =>
            {
                approved = teachingOwnership == TeachingOwnership.ObservedAfterApproval;
                linkCommandsEnabled = approved && link is not null;
                teachingOwnership = TeachingOwnership.None;
                message = approved ? "CAN control restored · previous targets discarded · measured scene tracking continues"
                    : "Teaching ended and CAN control is healthy. Use Enable robot when ready; no movement was resumed.";
            })) return;
        }
        // Serialize with manual actions before consuming the one-attempt gate.
        // A busy UI action leaves this poll observational; it grants no request.
        if (!ResumeControlsBusy && !GroupFollowActive && CurrentRecovery is null && await commands.WaitAsync(0, cancellation))
        {
            bool attempting = false;
            try
            {
                long permissionEpoch = CommandAuthority.Epoch;
                string expectedToken = token;
                JsonObject current = Device(expectedToken: expectedToken);
                string expectedDevice = current["id"]!.GetValue<string>();
                long expectedGeneration = current["generation"]!.GetValue<long>();
                TeachingState currentTeaching = TeachingState.Inspect(current);
                if (!GroupFollowActive && CurrentRecovery is null && teachingReturnGate.Observe(currentTeaching, teachingOwnership == TeachingOwnership.ObservedAfterApproval,
                    Navigation.Page == WorkspacePage.Controls && !switchingProfile,
                    expectedDevice + ":" + expectedGeneration + ":" + expectedToken))
                {
                    attempting = true;
                    await Command("teach-return", currentTeaching.ReturnFlags(), cancellation, teachingAcknowledged: true,
                        expectedDeviceId: expectedDevice, expectedGeneration: expectedGeneration, expectedToken: expectedToken,
                        expectedPermissionEpoch: permissionEpoch);
                    message = "Hand guiding finished · restoring CAN at the measured pose · previous targets discarded";
                }
            }
            catch (InvalidOperationException error)
            {
                if (attempting) message = "Automatic CAN return paused: " + error.Message + " Use Return to CAN when ready.";
                else teachingReturnGate.Invalidate();
            }
            finally { commands.Release(); }
        }
        teaching = next;
    }

    public static bool TeachingCanResume(JsonObject device)
    {
        var mode = TeachingState.Inspect(device);
        var status = device["status"];
        return !mode.BlocksTargets && mode.Phase == TeachingPhase.Off && device["fresh"]?.GetValue<bool>() == true &&
            device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true &&
            string.IsNullOrEmpty(device["blocker"]?.GetValue<string>()) && status?["arm_status"]?.GetValue<int>() == 0 &&
            status["error"]?.GetValue<int>() == 0 && status["control"]?.GetValue<int>() == 1 &&
            device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true &&
                j["enabled"]?.GetValue<bool>() == true && j["fault"]?.GetValue<int>() == 0);
    }

    private void AddTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        AddPikaTeachingPatches(patches, device);
        patches["teaching.label"] = new() { ["text"] = teaching.Label };
        patches["teaching.next"] = new() { ["text"] = teaching.NextStep };
    }
}
