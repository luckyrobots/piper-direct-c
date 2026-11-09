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
        if (profile.PikaDevice.Length == 0) return null;
        if (snapshot["pikas"] is JsonArray sessions)
            return sessions.OfType<JsonObject>().SingleOrDefault(pika => Matches(profile, pika));
        return snapshot["pika"] is JsonObject legacy && Matches(profile, legacy) ? legacy : null;
    }

    internal static bool Matches(RobotProfile profile, JsonObject? pika) => profile.PikaDevice.Length > 0 && pika is not null &&
        (pika["device"]?.GetValue<string>() == profile.PikaDevice ||
            pika["aliases"] is JsonArray aliases && aliases.OfType<JsonValue>().Any(a => a.TryGetValue<string>(out var alias) && alias == profile.PikaDevice)) &&
        (!OperatingSystem.IsWindows() || pika["distribution"]?.GetValue<string>() == profile.PikaDistribution);
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
        var approval = ControlScope(profile, backend, device);
        if (ActiveProfile.Id != profile.Id || !CommandAuthority.TrySaveApproval(epoch, profile.Id, approval,
            activate ? () => { approved = true; linkCommandsEnabled = link is not null; } : null))
            throw new InvalidOperationException("Control permission changed before the robot connection could be confirmed.");
    }

    private static FollowControlApproval ControlScope(RobotProfile profile, string backend, JsonObject device) =>
        new(backend, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>(), profile.RobotId,
            profile.RobotJointPrefix, profile.PikaDevice, profile.WslDistribution);

    private void ForgetControlApproval() => CommandAuthority.ForgetApproval(ActiveProfile.Id);

    private void RestoreControlApproval()
    {
        var permission = CommandAuthority.Current;
        var profile = ActiveProfile;
        if (switchingProfile || calibrationOpen || CurrentRecovery is not null || !HasFreshControlTelemetry(out var receipt) ||
            !permission.Approvals.TryGetValue(profile.Id, out var saved)) return;
        var d = Device(sourceReceipt: receipt);
        if (saved != ControlScope(profile, receipt!.BackendToken, d))
        { CommandAuthority.ForgetApproval(profile.Id, saved); return; }
        var teaching = TeachingState.Inspect(d);
        // A previously approved operator can use the physical button to recover
        // from an obstruction directly. Actual movement remains blocked until
        // the native controller verifies the new same-pose handoff.
        if (d["recovery_required"]?.GetValue<bool>() == true && teaching.HandGuiding && CurrentGuide?.Gate is null)
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
                        ["pika_device"] = profile.PikaDevice, ["pika_distribution"] = profile.PikaDistribution }, cancellation);
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                { System.Diagnostics.Trace.TraceWarning("{0} gripper monitor: {1}", profile.Name, error.Message); }
                finally { commands.Release(); }
            }
        } while (await timer.WaitForNextTickAsync(cancellation));
    }

    private async Task ReleaseOwnedPikas(CancellationToken cancellation)
    {
        var receipt = telemetry.Latest;
        if (receipt is null || receipt.BackendToken != token) return;
        string backend = receipt.BackendToken;
        var identities = OwnedPikaSessions(receipt.State)
            .Select(session => (Device: session["device"]!.GetValue<string>(), Distribution: session["distribution"]?.GetValue<string>()))
            .Distinct().ToArray();
        await Task.WhenAll(identities.Select(Release));

        async Task Release((string Device, string? Distribution) identity)
        {
            JsonObject Body() => new() { ["owner"] = owner, ["pika_device"] = identity.Device, ["pika_distribution"] = identity.Distribution };
            (TelemetryReceipt Receipt, JsonObject Gripper)? ReadOwned()
            {
                var latest = telemetry.Latest;
                if (latest is null || latest.BackendToken != backend || token != backend) return null;
                var inventory = latest.State["pikas"] is JsonArray sessions ? sessions.OfType<JsonObject>() :
                    latest.State["pika"] is JsonObject single ? new[] { single } : [];
                var gripper = inventory.SingleOrDefault(p => p["device"]?.GetValue<string>() == identity.Device &&
                    p["distribution"]?.GetValue<string>() == identity.Distribution);
                return gripper is not null && gripper["owner"]?.GetValue<string>() == owner && gripper["connected"]?.GetValue<bool>() == true
                    ? (latest, gripper) : null;
            }

            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (ReadOwned() is not { } original) return;
                var session = original.Gripper;
                if (session["power_requested"]?.GetValue<bool>() == true || session["armed"]?.GetValue<bool>() == true)
                {
                    var reply = await Call("/api/pika/disable", Body(), cancellation, backend);
                    if (reply["motor_request_id"]?.GetValue<string>() is not { Length: > 0 } request)
                        throw new InvalidOperationException("Disable did not acknowledge a motor request.");
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (ReadOwned() is not { } current) return;
                        if (current.Receipt.HasFreshPika(current.Gripper, System.Diagnostics.Stopwatch.GetTimestamp(), 500) &&
                            current.Gripper["last_motor_request"]?.GetValue<string>() == request &&
                            Pika.PikaDisabledEvidence.Confirmed(current.Gripper, owner)) break;
                        await Task.Delay(25, cancellation);
                    }
                }
                else if (session["power_requested"]?.GetValue<bool>() != false || session["armed"]?.GetValue<bool>() != false ||
                    session["power_uncertain"]?.GetValue<bool>() != false || session["pending"]?.GetValue<bool>() == true ||
                    session["following"]?.GetValue<bool>() == true || session["resuming_hold"]?.GetValue<bool>() == true)
                    throw new InvalidOperationException("The gripper's read-only state could not be verified.");

                cancellation.ThrowIfCancellationRequested();
                await Call("/api/pika/disconnect", Body(), cancellation, backend);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Pika shutdown ({0}): {1}", identity.Device, error.Message); }
        }
    }
}
