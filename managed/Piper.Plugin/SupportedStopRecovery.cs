using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SupportedRecoverySnapshot(JsonObject Device, bool Active, long RequestId, long Sequence, long ReceivedTicks, bool ExpectedResetGap = false);
public sealed record SupportedRecoveryAcknowledgement(long RequestId, long Sequence, long AcknowledgedTicks);

/// <summary>One supported STOP acknowledgement; never sends a destination, homes, replays, or retries reset.</summary>
public static class SupportedStopRecovery
{
    public static async Task<bool> Run(Func<SupportedRecoverySnapshot> read,
        Func<string, CancellationToken, Task<SupportedRecoveryAcknowledgement>> send,
        Func<CancellationToken, Task> enableHolding, Action<string> progress, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var first = Read();
        if (first.Active || first.Device["status"]?["arm_status"]?.GetValue<int>() != 1)
            throw new InvalidOperationException("This recovery action requires an idle, latched controller STOP.");
        HealthyDrivers(first.Device);
        if (!AllOff(first.Device))
        {
            progress("Disabling arm motors. Keep the arm supported; the interrupted target has been discarded.");
            var disabled = await send("disable", ct);
            await Wait(disabled, snapshot => AllOff(snapshot.Device), "Waiting for fresh confirmation that all six arm motors are off.");
        }
        await Wait(null, snapshot => AllOff(snapshot.Device) && RecoveryPlan.Inspect(snapshot.Device, snapshot.Active).Action == "reset",
            "All six motors must remain off. Waiting for the controller's strict stationary check before clearing STOP.");
        progress("All six motors are off. Clearing the controller STOP once; keep supporting the arm.");
        var reset = await send("reset", ct);
        var resetState = await Wait(reset, snapshot => AllOff(snapshot.Device) && snapshot.Device["status"]?["arm_status"]?.GetValue<int>() == 0 &&
            snapshot.Device["status"]?["stationary_s"]?.GetValue<double>() is >= .4,
            "Verifying fresh, healthy, stationary feedback with all six motors off after reset.");
        if (resetState.Device["reset_pending"]?.GetValue<bool>() == true || resetState.Device["recovery_required"]?.GetValue<bool>() == true)
        {
            progress("Controller STOP cleared. Verifying recovery without changing motor power or position.");
            var verified = await send(resetState.Device["reset_pending"]?.GetValue<bool>() == true ? "verify-reset" : "verify-current", ct);
            await Wait(verified, snapshot => AllOff(snapshot.Device) && snapshot.Device["recovery_required"]?.GetValue<bool>() != true &&
                snapshot.Device["reset_pending"]?.GetValue<bool>() != true, "Waiting for the controller's recovery verification.");
        }
        var cleared = Read();
        string range = RecoveryWorkspaceState.RangeRequirements(cleared.Device);
        if (range.Length > 0 || !string.IsNullOrEmpty(cleared.Device["blocker"]?.GetValue<string>()))
        {
            progress("STOP is cleared and all motors are off. Use the green hand-guiding button to bring the listed joints into range, turn it off, then use Resume holding here.");
            return false;
        }
        progress("Controller recovery verified. Enabling holding at the current measured pose; no destination or homing.");
        await enableHolding(ct);
        var held = Read();
        if (!RobotPowerState.Inspect(held.Device).AllEnabled || !PipersPlugin.TeachingCanResume(held.Device))
            throw new InvalidOperationException("Holding is not yet verified. Keep supporting the arm and follow the current controller requirement.");
        progress("Recovery complete. Holding the current measured pose; no interrupted movement will resume.");
        return true;

        SupportedRecoverySnapshot Read(bool allowExpectedResetGap = false)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = read();
            if (allowExpectedResetGap && snapshot.ExpectedResetGap) return snapshot;
            if (snapshot.Device["fresh"]?.GetValue<bool>() != true || snapshot.Device["joints"] is not JsonArray { Count: 6 } joints ||
                joints.Any(j => j?["fresh"]?.GetValue<bool>() != true)) throw new InvalidOperationException("Recovery stopped: live feedback from all six joints is required.");
            HealthyDrivers(snapshot.Device);
            return snapshot;
        }
        async Task<SupportedRecoverySnapshot> Wait(SupportedRecoveryAcknowledgement? acknowledgement, Func<SupportedRecoverySnapshot, bool> ready, string waiting)
        {
            progress(waiting);
            while (true)
            {
                var snapshot = Read(allowExpectedResetGap: true);
                if (snapshot.ExpectedResetGap) { await Task.Delay(25, ct); continue; }
                bool afterAcknowledgement = acknowledgement is null || snapshot.Sequence > acknowledgement.Sequence &&
                    snapshot.ReceivedTicks > acknowledgement.AcknowledgedTicks && (acknowledgement.RequestId == 0 || snapshot.RequestId == acknowledgement.RequestId);
                if (!snapshot.Active && afterAcknowledgement && ready(snapshot)) return snapshot;
                await Task.Delay(25, ct);
            }
        }
    }

    private static bool AllOff(JsonObject device) => RobotPowerState.Inspect(device).EnabledCount == 0;
    private static void HealthyDrivers(JsonObject device)
    {
        if (device["status"]?["error"]?.GetValue<int>() != 0 || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fault"]?.GetValue<int>() != 0))
            throw new InvalidOperationException("A controller or motor-driver fault is still present. Inspect that fault before supported STOP recovery.");
    }
}
