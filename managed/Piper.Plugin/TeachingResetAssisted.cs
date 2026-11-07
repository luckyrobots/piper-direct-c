using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record TeachingResetSnapshot(JsonObject Device, bool MotionActive);

/// <summary>Explicit supported reset sequence. No trajectory or joint target is part of this workflow.</summary>
public static class TeachingResetAssisted
{
    public static string? Blocker(TeachingResetSnapshot snapshot, bool requireEndedTeaching, bool afterStop = false)
    {
        var device = snapshot.Device;
        var status = device["status"];
        if (snapshot.MotionActive) return "Finish the current arm action before starting reset-assisted return.";
        if (requireEndedTeaching && device["recovery_required"]?.GetValue<bool>() == true)
            return "A recorded controller stop still requires its displayed recovery procedure; reset-assisted teaching return is not an automatic fault bypass.";
        if (device["fresh"]?.GetValue<bool>() != true || device["firmware_supported"]?.GetValue<bool>() != true ||
            status is null || device["joints"] is not JsonArray { Count: 6 } joints ||
            joints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0 ||
                j["actual_deg"] is null || !double.IsFinite(j["actual_deg"]!.GetValue<double>())))
            return "Fresh, supported controller feedback and six healthy joint measurements are required.";
        if (device["reset_pending"]?.GetValue<bool>() == true) return "A reset is already pending; verify that reset rather than starting another.";
        int arm = status["arm_status"]?.GetValue<int>() ?? -1;
        if (status["error"]?.GetValue<int>() != 0 || arm != 0 && !(afterStop && arm == 1))
            return "A controller/driver fault is present; use the displayed fault recovery procedure.";
        int control = status["control"]?.GetValue<int>() ?? -1, teach = status["teach_state"]?.GetValue<int>() ?? -1;
        if (control is < 0 or > 2 || status["motion_mode"]?.GetValue<int>() is not (0 or 1) || teach is not (0 or 2 or 6) ||
            requireEndedTeaching && (control != 2 || TeachingState.Inspect(device).Phase != "ended"))
            return "End physical teaching/playback and turn the teach light off before reset-assisted return.";
        double stationary = status["stationary_s"]?.GetValue<double>() ?? 0;
        if (!double.IsFinite(stationary) || stationary < .4) return "Support the arm and hold it stationary for at least 0.4 seconds.";
        double j2 = joints[1]!["actual_deg"]!.GetValue<double>(), j3 = joints[2]!["actual_deg"]!.GetValue<double>(), j5 = joints[4]!["actual_deg"]!.GetValue<double>();
        if (Math.Abs(j2) >= 10 || Math.Abs(j3) >= 10 || j5 <= 12 || j5 >= 45)
            return "While hand guiding, support the arm at |J2| < 10°, |J3| < 10° and 12° < J5 < 45°, then turn the teach light off. This is the vendor reset posture, not a homing target.";
        return null;
    }

    public static bool CanResetAssisted(TeachingResetSnapshot snapshot) => Blocker(snapshot, true) is null;

    public static bool ExpectedFeedbackGap(bool confirmedResetInProgress, JsonObject snapshot, string deviceId, long generation)
    {
        if (!confirmedResetInProgress || snapshot["selected"]?.GetValue<string>() != deviceId) return false;
        var device = snapshot["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == deviceId);
        return device?["generation"]?.GetValue<long>() == generation && device["present"]?.GetValue<bool>() == true &&
            device["up"]?.GetValue<bool>() == true && device["fresh"]?.GetValue<bool>() == false;
    }

    private static bool SameDevice(JsonObject device, string id, long generation) => device["id"]?.GetValue<string>() == id && device["generation"]?.GetValue<long>() == generation;
    private static bool HealthyPower(TeachingResetSnapshot snapshot, bool enabled)
    {
        var device = snapshot.Device; var status = device["status"];
        return !snapshot.MotionActive && device["fresh"]?.GetValue<bool>() == true && status?["arm_status"]?.GetValue<int>() == 0 &&
            status["error"]?.GetValue<int>() == 0 && status["control"]?.GetValue<int>() is 0 or 1 &&
            status["teach_state"]?.GetValue<int>() is 0 or 2 or 6 && status["stationary_s"]?.GetValue<double>() >= .4 &&
            device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true &&
                j["fault"]?.GetValue<int>() == 0 && j["enabled"]?.GetValue<bool>() == enabled);
    }

    public static async Task Run(bool supportAndPowerLossConfirmed, string deviceId, long generation,
        Func<TeachingResetSnapshot> snapshot, Func<string, CancellationToken, Task> action, Action<string> progress,
        CancellationToken cancellation, int timeoutMilliseconds = 12000)
    {
        if (!supportAndPowerLossConfirmed) throw new InvalidOperationException("Confirm continuous arm support, a clear workspace and the brief loss of holding torque before starting.");
        TeachingResetSnapshot Current()
        {
            var value = snapshot();
            if (!SameDevice(value.Device, deviceId, generation)) throw new InvalidOperationException("The selected CAN device or connection generation changed; reset-assisted return was cancelled.");
            return value;
        }
        string? initial = Blocker(Current(), true);
        if (initial is not null) throw new InvalidOperationException(initial);
        async Task Wait(Func<TeachingResetSnapshot, bool> ready)
        {
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var value = Current();
                if (value.Device["fresh"]?.GetValue<bool>() == true && (TeachingState.Inspect(value.Device).Active ||
                    value.Device["status"]?["error"]?.GetValue<int>() != 0 || value.Device["joints"]!.AsArray().Any(j => j?["fault"]?.GetValue<int>() != 0)))
                    throw new InvalidOperationException("Teaching/playback restarted or a controller/driver fault appeared. Reset-assisted return was stopped; keep the arm supported.");
                // MCU reset can briefly interrupt telemetry. Wait for fresh
                // evidence under the bounded deadline; never treat silence as off.
                if (value.Device["fresh"]?.GetValue<bool>() == true && ready(value)) return;
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > timeoutMilliseconds) throw new TimeoutException("Timed out waiting for verified controller/motor feedback. Keep the arm supported and inspect the displayed state.");
                await Task.Delay(25, cancellation);
            }
        }
        try
        {
            progress("Stopping controller · keep supporting the arm");
            await action("stop", cancellation);
            await Wait(s => !s.MotionActive && s.Device["status"]?["stationary_s"]?.GetValue<double>() >= .4);
            string? stopped = Blocker(Current(), false, afterStop: true);
            if (stopped is not null) throw new InvalidOperationException("After STOP: " + stopped);
            progress("Resetting controller · holding torque will be removed");
            await action("reset", cancellation);
            await Wait(s => HealthyPower(s, false));
            progress("Verifying all six motors are off · keep supporting the arm");
            await action(Current().Device["reset_pending"]?.GetValue<bool>() == true ? "verify-reset" : "verify-current", cancellation);
            await Wait(s => HealthyPower(s, false) && s.Device["reset_pending"]?.GetValue<bool>() != true && s.Device["recovery_required"]?.GetValue<bool>() != true);
            var verified = Current();
            string? posture = Blocker(verified, false);
            if (posture is not null || !HealthyPower(verified, false) || verified.Device["recovery_required"]?.GetValue<bool>() == true)
                throw new InvalidOperationException(posture ?? "Reset verification did not clear recovery with all six motors off.");
            progress("Enabling six arm motors under your confirmation · no position target");
            await action("enable", cancellation);
            await Wait(s => HealthyPower(s, true) && s.Device["recovery_required"]?.GetValue<bool>() != true && s.Device["reset_pending"]?.GetValue<bool>() != true);
            progress("Reset-assisted return complete · six motors enabled · previous targets discarded");
        }
        catch
        {
            // Failure never retries reset/enable or resumes a target. A best-effort
            // STOP is confined to the exact connection covered by the confirmation.
            try
            {
                if (SameDevice(snapshot().Device, deviceId, generation))
                {
                    using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await action("stop", stopping.Token);
                }
            }
            catch (Exception) { /* Report the original stage failure; core leases still expire independently. */ }
            throw;
        }
    }
}
