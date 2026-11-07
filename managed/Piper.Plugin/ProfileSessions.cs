using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class ProfilePikaRouting
{
    public static bool CanReleaseAssignment(JsonObject gripper, string owner, bool telemetryFresh) => telemetryFresh &&
        gripper["fresh"]?.GetValue<bool>() == true && gripper["enabled"]?.GetValue<bool>() == false &&
        gripper["armed"]?.GetValue<bool>() == false && gripper["pending"]?.GetValue<bool>() == false &&
        gripper["following"]?.GetValue<bool>() == false && gripper["resuming_hold"]?.GetValue<bool>() != true &&
        gripper["power_uncertain"]?.GetValue<bool>() == false &&
        (gripper["power_requested"]?.GetValue<bool>() != true || Pika.PikaDisabledEvidence.Confirmed(gripper, owner));

    public static JsonObject? Find(JsonObject snapshot, RobotProfile profile)
    {
        bool Match(JsonObject pika) => (pika["device"]?.GetValue<string>() == profile.PikaDevice ||
            pika["aliases"]?.AsArray().Any(a => a?.GetValue<string>() == profile.PikaDevice) == true) &&
            (!OperatingSystem.IsWindows() || pika["distribution"]?.GetValue<string>() == profile.WslDistribution);
        if (profile.PikaDevice.Length == 0) return null;
        if (snapshot["pikas"] is JsonArray sessions) return sessions.OfType<JsonObject>().SingleOrDefault(Match);
        return snapshot["pika"] is JsonObject legacy && Match(legacy) ? legacy : null;
    }
}

public sealed partial class PipersPlugin
{
    private IReadOnlyDictionary<string, FollowControlApproval> controlApprovals => CommandAuthority.Current.Approvals;

    private void ProjectProfilePika(JsonObject snapshot)
    {
        var profile = ActiveProfile;
        var selected = ProfilePikaRouting.Find(snapshot, profile);
        if (snapshot["pikas"] is JsonArray || selected is not null)
            snapshot["pika"] = selected?.DeepClone() ?? new JsonObject { ["device"] = profile.PikaDevice,
                ["distribution"] = profile.WslDistribution, ["configured"] = false, ["connected"] = false, ["fresh"] = false };
    }

    private JsonObject[] OwnedPikaSessions(JsonObject snapshot) => (snapshot["pikas"] is JsonArray sessions
        ? sessions.OfType<JsonObject>() : snapshot["pika"] is JsonObject single ? new[] { single } : [])
        .Where(p => p["owner"]?.GetValue<string>() == owner && p["connected"]?.GetValue<bool>() == true && p["device"] is not null).ToArray();

    private void RememberControlApproval(long epoch, bool activate = false)
    {
        var profile = ActiveProfile;
        string backend = token;
        var device = Device(expectedToken: backend);
        var approval = new FollowControlApproval(backend, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>(),
            profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution);
        if (ActiveProfile.Id != profile.Id || !CommandAuthority.TrySaveApproval(epoch, profile.Id, approval,
            activate ? () => { approved = true; linkCommandsEnabled = link is not null; } : null))
            throw new InvalidOperationException("Control permission changed before the robot connection could be confirmed.");
    }

    private void ForgetControlApproval() => CommandAuthority.ForgetApproval(ActiveProfile.Id);

    private void RestoreControlApproval()
    {
        var permission = CommandAuthority.Current;
        var profile = ActiveProfile;
        if (switchingProfile || calibrationOpen || !HasFreshControlTelemetry(out var receipt) || !permission.Approvals.TryGetValue(profile.Id, out var saved)) return;
        var d = Device(sourceReceipt: receipt);
        if (saved != new FollowControlApproval(receipt!.BackendToken, d["id"]!.GetValue<string>(), d["generation"]!.GetValue<long>(),
            profile.RobotId, profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution))
        { CommandAuthority.ForgetApproval(profile.Id, saved); return; }
        var teaching = TeachingState.Inspect(d);
        // A previously approved operator can use the physical button to recover
        // from an obstruction directly. Actual movement remains blocked until
        // the native controller verifies the new same-pose handoff.
        if (d["recovery_required"]?.GetValue<bool>() == true && teaching.HandGuiding && handGuideRecovery is null)
            BeginHandGuideRecovery(d);
        if (d["recovery_required"]?.GetValue<bool>() == true || d["reset_pending"]?.GetValue<bool>() == true || teaching.BlocksTargets ||
            !RobotPowerState.Inspect(d).AllEnabled || !string.IsNullOrEmpty(d["blocker"]?.GetValue<string>())) return;
        var pika = receipt.State["pika"] as JsonObject;
        bool monitoring = PikaMatchesProfile(profile, pika) && pika?["owner"]?.GetValue<string>() == owner;
        bool owns = monitoring && pika?["armed"]?.GetValue<bool>() == true;
        CommandAuthority.TryRestoreApproval(permission.Epoch, profile.Id, saved, () =>
        {
            approved = true;
            linkCommandsEnabled = link is not null;
            pikaOwned = owns;
            pikaMonitoring = monitoring;
        });
    }

    private async Task MonitorAssignedPikas(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            if (!passiveMeasuredTrackingStarted || !TelemetryIsFresh() || switchingProfile || SimulationFollowingActive || state?["motion"]?["active"]?.GetValue<bool>() == true) continue;
            foreach (var profile in preferences.Robots ?? [])
            {
                if (profile.CanDeviceId.Length == 0 || profile.PikaDevice.Length == 0 || !await commands.WaitAsync(0, cancellation)) continue;
                try
                {
                    if (switchingProfile || !preferences.Robots!.Any(p => p.Id == profile.Id && p.PikaDevice == profile.PikaDevice && p.WslDistribution == profile.WslDistribution)) continue;
                    var gripper = ProfilePikaRouting.Find(state!, profile);
                    var connection = PikaMonitorConnectionPolicy.Inspect(gripper);
                    if (connection == PikaMonitorConnection.Skip) continue;
                    // Read-only discovery owns no motor targets. Exact selectors
                    // and the normal command gate prevent assignment races.
                    await Call("/api/pika/connect", new() { ["owner"] = owner, ["take_over"] = connection == PikaMonitorConnection.AdoptReadOnly,
                        ["read_only_adopt"] = connection == PikaMonitorConnection.AdoptReadOnly,
                        ["pika_device"] = profile.PikaDevice, ["pika_distribution"] = profile.WslDistribution }, cancellation);
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                { System.Diagnostics.Trace.TraceWarning("{0} gripper monitor: {1}", profile.Name, error.Message); }
                finally { commands.Release(); }
            }
        } while (await timer.WaitForNextTickAsync(cancellation));
    }

    private async Task ReleaseOtherPikas(CancellationToken cancellation)
    {
        foreach (var session in OwnedPikaSessions(state ?? new()))
        {
            string device = session["device"]!.GetValue<string>(), distribution = session["distribution"]?.GetValue<string>() ?? "";
            if (device == ActiveProfile.PikaDevice && distribution == ActiveProfile.WslDistribution) continue;
            JsonObject Body() => new() { ["owner"] = owner, ["pika_device"] = device, ["pika_distribution"] = distribution };
            if (session["power_requested"]?.GetValue<bool>() == true || session["armed"]?.GetValue<bool>() == true)
            {
                await Call("/api/pika/disable", Body(), cancellation);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var current = state?["pikas"]?.AsArray().OfType<JsonObject>().SingleOrDefault(p => p["device"]?.GetValue<string>() == device && p["distribution"]?.GetValue<string>() == distribution);
                    if (current?["fresh"]?.GetValue<bool>() == true && current["enabled"]?.GetValue<bool>() == false) break;
                    await Task.Delay(25, cancellation);
                }
            }
            await Call("/api/pika/disconnect", Body(), cancellation);
        }
    }
}
