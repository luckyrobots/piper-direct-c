using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record RecoveryHomeSnapshot(JsonObject Device, JsonObject Motion, string Phase, string Message,
    long Sequence, long CapturedTicks);
public sealed record RecoveryHomeAcknowledgement(long RequestId, long Sequence, long Ticks, bool Stateful = true);

/// <summary>One explicit resting-arm recovery goal. Never retries a stage or restores an interrupted target.</summary>
public static class RecoveryHome
{
    public const double SpeedPercent = 10;
    public static string? Blocker(RecoveryHomeSnapshot snapshot)
    {
        var device = snapshot.Device;
        var status = device["status"];
        if (snapshot.Motion["active"]?.GetValue<bool>() != false || device["fresh"]?.GetValue<bool>() != true ||
            device["firmware_supported"]?.GetValue<bool>() != true || status is null || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0 || j["enabled"] is not JsonValue power || !power.TryGetValue<bool>(out _)))
            return "Wait for an idle supported controller and fresh healthy feedback from all six motors.";
        int arm = status["arm_status"]?.GetValue<int>() ?? -1, teach = status["teach_state"]?.GetValue<int>() ?? -1;
        int control = status["control"]?.GetValue<int>() ?? -1;
        bool allOff = joints.All(j => j!["enabled"]!.GetValue<bool>() == false);
        bool residualTeach = arm == 1 && allOff && teach == 1;
        if (status["error"]?.GetValue<int>() != 0 || arm is not (0 or 1) || control is < 0 or > 2 ||
            status["motion_mode"]?.GetValue<int>() is not (0 or 1) || teach is not (0 or 2 or 6) && !residualTeach)
            return "End active teaching/playback and inspect controller or driver faults before recovering to zero.";
        if (status["stationary_s"]?.GetValue<double>() is not double stationary || !double.IsFinite(stationary) || stationary < .4)
            return "Let the supported arm remain stationary for 0.4 seconds before recovering to zero.";
        if (device["reset_pending"]?.GetValue<bool>() == true &&
            !(allOff && (arm == 1 || arm == 0 && control <= 1 && teach is 0 or 2 or 6)))
            return "A reset is still pending. Wait for verified all-off healthy completion or a retired stationary STOP before requesting another recovery.";
        for (int i = 0; i < 6; ++i)
        {
            var joint = joints[i];
            double actual = joint?["actual_deg"]?.GetValue<double>() ?? double.NaN;
            double minimum = joint?["min_deg"]?.GetValue<double>() ?? double.NaN;
            double maximum = joint?["max_deg"]?.GetValue<double>() ?? double.NaN;
            if (joint?["limits_fresh"]?.GetValue<bool>() != true || !double.IsFinite(actual) || !double.IsFinite(minimum) ||
                !double.IsFinite(maximum) || minimum >= maximum || minimum > 0 || maximum < 0)
                return $"J{i + 1} needs valid reported limits containing zero before this recovery can start.";
            double margin = Math.Min(.5, (maximum - minimum) / 2);
            double correction = actual < minimum - .1 ? minimum + margin - actual : actual > maximum + .1 ? actual - maximum + margin : 0;
            if (correction > 3)
                return $"J{i + 1} needs more than the allowed 3° range correction, including the inward margin. This recovery cannot bring that pose to zero automatically.";
        }
        return null;
    }

    private static bool SameDevice(RecoveryHomeSnapshot snapshot, string id, long generation) =>
        snapshot.Device["id"]?.GetValue<string>() == id && snapshot.Device["generation"]?.GetValue<long>() == generation;

    private static bool HealthyOffOrOn(RecoveryHomeSnapshot snapshot, bool enabled, bool requireClearedRecovery = true)
    {
        var device = snapshot.Device; var status = device["status"];
        return device["fresh"]?.GetValue<bool>() == true && status?["arm_status"]?.GetValue<int>() == 0 &&
            status["error"]?.GetValue<int>() == 0 && status["control"]?.GetValue<int>() is 0 or 1 &&
            status["teach_state"]?.GetValue<int>() is 0 or 2 or 6 &&
            status["stationary_s"]?.GetValue<double>() is double stationary && double.IsFinite(stationary) && stationary >= .4 &&
            (!requireClearedRecovery || device["reset_pending"]?.GetValue<bool>() != true && device["recovery_required"]?.GetValue<bool>() != true) &&
            device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true &&
                j["fault"]?.GetValue<int>() == 0 && j["enabled"]?.GetValue<bool>() == enabled);
    }

    public static async Task Run(bool restingAndClearAcknowledged, string deviceId, long generation, string owner,
        Func<RecoveryHomeSnapshot> snapshot,
        Func<string, CancellationToken, Task<RecoveryHomeAcknowledgement>> action,
        Func<long, CancellationToken, Task> stopOwnedRequest, Func<string, Task> progress,
        CancellationToken cancellation, int resetTimeoutMilliseconds = 6000, int enableTimeoutMilliseconds = 10000,
        int homeTimeoutMilliseconds = 60000)
    {
        if (!restingAndClearAcknowledged)
            throw new InvalidOperationException("The arm must already rest securely on the desk or another support, with its route to zero clear. Motor flags cannot establish physical support.");
        RecoveryHomeSnapshot Current()
        {
            cancellation.ThrowIfCancellationRequested();
            var current = snapshot();
            if (!SameDevice(current, deviceId, generation))
                throw new InvalidOperationException("The robot or CAN connection changed. Recovery to zero was cancelled.");
            return current;
        }
        var initial = Current();
        string? blocker = Blocker(initial);
        if (blocker is not null) throw new InvalidOperationException(blocker);
        long ownedRequest = 0;
        long uncertainRequest = 0;
        async Task Wait(RecoveryHomeAcknowledgement acknowledged, Func<RecoveryHomeSnapshot, bool> ready, int timeout, bool allowResetTransitions = false)
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                var current = Current();
                if (current.Device["fresh"]?.GetValue<bool>() == true && current.Sequence > acknowledged.Sequence && current.CapturedTicks > acknowledged.Ticks)
                {
                    long reported = current.Motion["request_id"]?.GetValue<long>() ?? 0;
                    if (reported > acknowledged.RequestId)
                        throw new InvalidOperationException("Another controller action replaced recovery to zero. Its action will not be changed.");
                    bool active = current.Motion["active"]?.GetValue<bool>() == true;
                    bool ownReset = allowResetTransitions && reported == acknowledged.RequestId && active &&
                        current.Motion["owner"]?.GetValue<string>() == owner;
                    if (reported == acknowledged.RequestId && current.Phase is "blocked" or "stopped" or "teaching")
                        throw new InvalidOperationException("Recovery stopped: " + current.Message);
                    var status = current.Device["status"];
                    int arm = status?["arm_status"]?.GetValue<int>() ?? -1;
                    int teach = status?["teach_state"]?.GetValue<int>() ?? -1;
                    bool residualTeach = arm is 0 or 1 && teach == 1;
                    if (!ownReset && (status?["error"]?.GetValue<int>() != 0 || arm is not (0 or 1) ||
                        teach is not (0 or 2 or 6) && !residualTeach ||
                        current.Device["joints"] is not JsonArray { Count: 6 } joints ||
                        joints.Any(j => j?["fault"]?.GetValue<int>() != 0)))
                        throw new InvalidOperationException("Teaching/playback or a controller/driver fault interrupted recovery. No later stage will run.");
                    if (reported == acknowledged.RequestId)
                    {
                        if (current.Motion["active"]?.GetValue<bool>() == true)
                        {
                            if (current.Motion["owner"]?.GetValue<string>() != owner)
                                throw new InvalidOperationException("Another controller owns this action. Recovery to zero was cancelled.");
                        }
                        else
                        {
                            if (current.Phase != "complete" && acknowledged.Stateful)
                                throw new InvalidOperationException("Recovery stopped: " + current.Message);
                            if (ready(current)) return;
                        }
                    }
                }
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > timeout)
                    throw new TimeoutException("Recovery feedback was not verified before its deadline. No stage will retry automatically.");
                await Task.Delay(25, cancellation);
            }
        }
        async Task<RecoveryHomeAcknowledgement> Send(string step)
        {
            var before = Current();
            uncertainRequest = (before.Motion["request_id"]?.GetValue<long>() ?? 0) + 1;
            var acknowledged = await action(step, cancellation);
            uncertainRequest = 0;
            ownedRequest = acknowledged.RequestId;
            return acknowledged;
        }
        try
        {
            bool healthyOn = HealthyOffOrOn(initial, true, requireClearedRecovery: false);
            bool healthyOff = HealthyOffOrOn(initial, false, requireClearedRecovery: false);
            if (initial.Device["reset_pending"]?.GetValue<bool>() == true && healthyOff)
            {
                await progress("Verifying the completed reset · arm stays on its support");
                var verified = await Send("verify-reset");
                await Wait(verified, s => HealthyOffOrOn(s, false), resetTimeoutMilliseconds);
            }
            else if (initial.Device["reset_pending"]?.GetValue<bool>() != true && (healthyOn || healthyOff))
            {
                if (initial.Device["recovery_required"]?.GetValue<bool>() == true)
                {
                    await progress("Verifying the healthy controller · existing motor power is preserved");
                    var verified = await Send("verify-current");
                    await Wait(verified, s => HealthyOffOrOn(s, healthyOn), resetTimeoutMilliseconds);
                }
            }
            else
            {
                await progress("Resetting controller · keep the arm resting on its support");
                var reset = await Send("reset");
                await Wait(reset, s => HealthyOffOrOn(s, false), resetTimeoutMilliseconds, allowResetTransitions: true);
            }
            if (!HealthyOffOrOn(Current(), true))
            {
                await progress("All six motors verified off · enabling at the current resting pose");
                blocker = Blocker(Current());
                if (blocker is not null) throw new InvalidOperationException(blocker);
                var enable = await Send("enable");
                await Wait(enable, s => HealthyOffOrOn(s, true), enableTimeoutMilliseconds);
            }
            await progress("Motors verified on · correcting small range overshoot, then moving all joints to zero at 10%");
            blocker = Blocker(Current());
            if (blocker is not null) throw new InvalidOperationException(blocker);
            var home = await Send("move-to-zero");
            await Wait(home, s => HealthyOffOrOn(s, true) && s.Device["joints"]!.AsArray().All(j =>
                j?["actual_deg"]?.GetValue<double>() is double value && double.IsFinite(value) && Math.Abs(value) <= 1), homeTimeoutMilliseconds);
            await progress("Recovery complete · zero target reached within 1° · previous movement discarded");
        }
        catch
        {
            // A scoped stop can retire only our still-active native request.
            // It must never stop a replacement action or another connection.
            try
            {
                var current = snapshot();
                long request = current.Motion["request_id"]?.GetValue<long>() ?? 0;
                if (SameDevice(current, deviceId, generation) &&
                    current.Motion["active"]?.GetValue<bool>() == true && current.Motion["owner"]?.GetValue<string>() == owner &&
                    request > 0 && (request == ownedRequest || request == uncertainRequest))
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await stopOwnedRequest(request, stop.Token);
                }
            }
            catch (Exception) { /* Preserve the original failure; native leases still expire independently. */ }
            throw;
        }
    }
}
