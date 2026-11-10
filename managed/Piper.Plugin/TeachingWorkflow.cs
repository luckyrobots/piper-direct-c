using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal TeachingState teaching = TeachingState.Inspect(null);
    /// <summary>Plugin-D: teaching ownership, its scope and the automatic return.</summary>
    internal readonly TeachingSession teachingSession = new();
    internal JsonObject? acknowledgedScenePose;

    private async Task ObserveTeaching(CancellationToken cancellation)
    {
        long observedEpoch = CommandAuthority.Epoch;
        JsonObject? device = null;
        try { device = Device(); } catch (InvalidOperationException) { }
        TeachingState next = TeachingState.Inspect(device);
        TeachingScope? scope = device is null ? null
            : new(device["id"]?.GetValue<string>() ?? "", device["generation"]?.GetValue<long>() ?? 0);
        teachingSession.Track(scope);
        if (next.BlocksTargets && scope is { } observed)
        {
            bool ownershipChanged = !teachingSession.Current.Owned || next.Active && next.Phase != teaching.Phase;
            if (!CommandAuthority.TryInEpoch(observedEpoch, () =>
            {
                teachingSession.ObserveHandOver(observed, approved);
                approved = linkCommandsEnabled = false;
            })) return;
            // The controller has already yielded ownership to its physical teaching
            // mode. Discard the old gesture locally; do not inject a hold/STOP into it.
            if (ownershipChanged) { CommandAuthority.ClearTracked(); EndViewportGesture(); }
            if (teleoperating)
            {
                StopViewportDrag();
                if (link is { } current)
                    await engine.RequestAsync("robot.teleoperate", new JsonObject {
                        ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId, ["enabled"] = false }, cancellation);
            }
            if (next.Phase != teaching.Phase) message = next.Label + ". " + next.NextStep;
        }
        else if (!ResumeControlsBusy && teachingSession.Current.Owned && device is not null && TeachingCanResume(device))
        {
            // Restore only a previous UI approval. This sends no enable, reset,
            // replay or position packet, and a new gesture is still required.
            if (!CommandAuthority.TryInEpoch(observedEpoch, () =>
            {
                approved = teachingSession.Current.RestoresApproval;
                linkCommandsEnabled = approved && link is not null;
                teachingSession.Release();
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
                if (!GroupFollowActive && CurrentRecovery is null && teachingSession.ObserveReturn(new(expectedDevice, expectedGeneration),
                    currentTeaching, Navigation.Page == WorkspacePage.Controls && !switchingProfile))
                {
                    attempting = true;
                    await Command("teach-return", currentTeaching.ReturnFlags(), cancellation, CommandPurpose.TeachingReturn,
                        new(ActiveProfile.Id, expectedToken, expectedDevice, expectedGeneration), permissionEpoch);
                    message = "Hand guiding finished · restoring CAN at the measured pose · previous targets discarded";
                }
            }
            catch (InvalidOperationException error)
            {
                if (attempting) message = "Automatic CAN return paused: " + error.Message + " Use Return to CAN when ready.";
                else teachingSession.InvalidateReturn();
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

    internal void AddTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        AddPikaTeachingPatches(patches, device);
        patches["teaching.label"] = new() { ["text"] = teaching.Label };
        patches["teaching.next"] = new() { ["text"] = teaching.NextStep };
    }
}
