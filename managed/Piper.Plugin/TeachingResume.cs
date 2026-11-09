using System.Diagnostics;
using System.Text.Json.Nodes;
using Grpc.Core;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Classifies a manual resume without weakening native admission.
/// A transient stillness gate waits; faults and ownership changes do not.</summary>
public static class TeachingResumeCheck
{
    public static void CommitApproval(Action validate, Action grant, Action revoke)
    {
        validate();
        try { grant(); validate(); }
        catch { revoke(); throw; }
    }

    public static bool Ready(JsonObject device)
    {
        if (device["fresh"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Robot feedback is stale. Resume controls again after the connection returns.");
        if (device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true ||
            device["status"]?["arm_status"]?.GetValue<int>() != 0 || device["status"]?["error"]?.GetValue<int>() != 0)
            throw new InvalidOperationException("The robot reports a stop or fault. Use its displayed recovery action before resuming controls.");
        if (device["joints"] is not JsonArray { Count: 6 } joints || joints.Any(j => j?["fresh"]?.GetValue<bool>() != true ||
            j["enabled"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0))
            throw new InvalidOperationException("All six motors must report fresh, enabled and fault-free feedback. No motor-power command was sent.");
        var teaching = TeachingState.Inspect(device);
        if (teaching.Phase != "ended" || teaching.Active || !teaching.ReturnRequired)
            throw new InvalidOperationException("The robot's teaching mode changed. Finish hand guiding or playback, then click Resume controls again.");
        if (device["motion"]?["active"]?.GetValue<bool>() == true || device["motion"]?["query_active"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Another controller action is active. Wait for it to finish before resuming controls.");
        if (teaching.CanReturn) return true;
        string reason = device["teaching"]?["return_reason"]?.GetValue<string>() ?? "";
        if (reason.StartsWith("Hold the arm still until all six motor encoders", StringComparison.Ordinal) ||
            reason.StartsWith("Keep the measured pose stationary", StringComparison.Ordinal) ||
            reason.StartsWith("Joint feedback disagrees with motor encoders. Waiting", StringComparison.Ordinal)) return false;
        throw new InvalidOperationException(reason.Length > 0 ? reason : "The controller has not qualified a supported return. No mode change was sent.");
    }
}

public sealed partial class PipersPlugin
{
    private async Task ResumeControls(CancellationToken cancellation)
    {
        string expectedProfile = ActiveProfile.Id, expectedToken = token;
        long expectedPermission = CommandAuthority.Epoch;
        var first = Device(expectedToken: expectedToken);
        string expectedDevice = first["id"]!.GetValue<string>();
        long expectedGeneration = first["generation"]!.GetValue<long>();
        JsonObject Current()
        {
            cancellation.ThrowIfCancellationRequested();
            if (ActiveProfile.Id != expectedProfile || Navigation.Page != WorkspacePage.Controls ||
                expectedPermission != CommandAuthority.Epoch)
                throw new InvalidOperationException("Control permission or selected robot changed. Click Resume controls again for the current robot.");
            return Device(expectedDevice, expectedGeneration, expectedToken);
        }

        resumeControlsBusy = true;
        approved = linkCommandsEnabled = false;
        teachingReturnGate.Invalidate();
        try
        {
            await PrimaryActionProgress("waiting-for-stillness", "Hold the arm still · reconnecting controls at this pose…", cancellation);
            long began = Stopwatch.GetTimestamp();
            async Task<JsonObject> ReadyFrame()
            {
                JsonObject device;
                while (!TeachingResumeCheck.Ready(device = Current()))
                {
                    if (Stopwatch.GetElapsedTime(began).TotalSeconds >= 8)
                        throw new InvalidOperationException("The arm did not settle long enough to reconnect. Leave the teach light off, hold the arm still, then click Resume controls again. No mode change was sent.");
                    await Task.Delay(40, cancellation);
                }
                return device;
            }
            _ = await ReadyFrame();
            // One admission attempt only. A native rejection is displayed and
            // never retried, reset, enabled or replaced with another action.
            await PrimaryActionProgress("returning-to-controls", "Reconnecting controls · holding this measured pose…", cancellation);
            // Updating the editor can be slow. Re-read admission after that
            // await, without another UI round-trip before the one command.
            JsonObject current = await ReadyFrame();
            var result = await Command("teach-return", TeachingState.Inspect(current).ReturnFlags(), cancellation,
                teachingAcknowledged: true, expectedDeviceId: expectedDevice, expectedGeneration: expectedGeneration,
                expectedToken: expectedToken, expectedPermissionEpoch: expectedPermission);
            long request = result["request_id"]?.GetValue<long>() ?? 0;
            bool Completed(JsonObject device) => TeachingCanResume(device) && (request == 0 ||
                device["motion"]?["request_id"]?.GetValue<long>() == request &&
                device["motion"]?["active"]?.GetValue<bool>() == false &&
                device["motion"]?["phase"]?.GetValue<string>() == "complete");
            began = Stopwatch.GetTimestamp();
            long heartbeatAt = 0;
            while (true)
            {
                current = Current();
                var mode = TeachingState.Inspect(current);
                var motion = current["motion"];
                long observed = motion?["request_id"]?.GetValue<long>() ?? 0;
                if (request > 0 && observed > request)
                    throw new InvalidOperationException("A different controller request replaced this resume. No retry will run.");
                if (Completed(current)) break;
                if (mode.Active || mode.Phase == "stopped" || current["recovery_required"]?.GetValue<bool>() == true ||
                    current["reset_pending"]?.GetValue<bool>() == true || current["status"]?["error"]?.GetValue<int>() != 0)
                    throw new InvalidOperationException("The controller changed mode or reported a fault while reconnecting. No retry will run.");
                if (request > 0 && observed >= request)
                {
                    if (observed != request || motion?["active"]?.GetValue<bool>() != true || motion["owner"]?.GetValue<string>() != owner)
                        throw new InvalidOperationException(motion?["message"]?.GetValue<string>() ?? "The controller did not verify CAN control. No retry will run.");
                    if (Stopwatch.GetElapsedTime(heartbeatAt).TotalMilliseconds >= 180)
                    {
                        try
                        {
                            await Call("/api/heartbeat", new() { ["device_id"] = expectedDevice, ["generation"] = expectedGeneration,
                                ["request_id"] = request, ["owner"] = owner }, cancellation, expectedToken);
                        }
                        catch (Exception error) when (error is InvalidOperationException or RpcException)
                        {
                            // Completion may race the final heartbeat. Only a
                            // fresh result for this same pinned request retires it.
                            var completed = Current();
                            if (Completed(completed)) break;
                            throw;
                        }
                        heartbeatAt = Stopwatch.GetTimestamp();
                    }
                }
                if (Stopwatch.GetElapsedTime(began).TotalSeconds >= 4)
                    throw new InvalidOperationException("The controller did not confirm control at this pose. No reset, power change or retry will run.");
                await Task.Delay(30, cancellation);
            }
            TeachingResumeCheck.CommitApproval(() => { _ = Current(); }, () =>
            {
                teachingSeen = approvalBeforeTeaching = false;
                RememberControlApproval(expectedPermission, activate: true);
            }, () =>
            {
                // Urgent STOP/Disable can revoke permission during the local
                // commit, including inside RememberControlApproval/Device.
                CommandAuthority.ForgetApproval(expectedProfile);
                approved = linkCommandsEnabled = false;
            });
            message = "Controls restored · holding this pose · previous movement discarded";
        }
        finally { resumeControlsBusy = false; }
    }
}
