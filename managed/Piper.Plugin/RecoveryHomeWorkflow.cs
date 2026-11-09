using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private string recoveryHomeProgress = "";

    private RecoveryHomeSnapshot HomeSnapshot(RecoveryOperation? operation = null)
    {
        // The runner checks cancellation before each ordinary read and write.
        // Keep this read available to its exact-request STOP cleanup after Cancel.
        if (operation is not null && !RecoveryScopeMatches(operation))
            throw new InvalidOperationException("Recovery was cancelled by STOP, a control-permission change or a different robot/backend. No remaining stage will run.");
        var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for controller feedback.");
        var device = RobotProfileRouting.Device(preferences, receipt.State);
        if (receipt.BackendToken != token || !receipt.HasFreshDevice(device, Stopwatch.GetTimestamp()))
        { device = (JsonObject)device.DeepClone(); device["fresh"] = false; }
        long captured = receipt.ReceivedTicks - (long)(receipt.SourceAgeMilliseconds * Stopwatch.Frequency / 1000);
        return new(device, receipt.State["motion"]?.AsObject() ?? new(), receipt.State["phase"]?.GetValue<string>() ?? "",
            receipt.State["message"]?.GetValue<string>() ?? "Controller state unavailable", receipt.Sequence, captured);
    }

    private string? RecoveryHomeBlocker(RecoveryHomeSnapshot? snapshot = null)
    {
        if (Navigation.Page != WorkspacePage.Controls) return "Return to Controls before recovering to zero.";
        if (GroupFollowActive) return "Stop following before recovering to zero.";
        if (CurrentRecovery is not null || recoveryInProgress || switchingProfile) return "Wait for the current recovery or connection change.";
        try
        {
            var current = snapshot ?? HomeSnapshot();
            return RecoveryPanelVisible(current.Device) ? RecoveryHome.Blocker(current) : "Open recovery for this robot first.";
        }
        catch (InvalidOperationException error) { return error.Message; }
    }

    private bool RecoveryPanelVisible(JsonObject? device) => CurrentRecovery is not null || RecoveryGuideFor(device) ||
        device?["recovery_required"]?.GetValue<bool>() == true || device?["reset_pending"]?.GetValue<bool>() == true ||
        device?["status"]?["arm_status"]?.GetValue<int>() == 1 || device?["range_recovery"]?["required"]?.GetValue<bool>() == true;

    private async Task StartRecoveryHome(CancellationToken cancellation)
    {
        long permissionEpoch = CommandAuthority.Epoch;
        var current = HomeSnapshot();
        string? blocked = RecoveryHomeBlocker(current);
        if (blocked is not null) throw new InvalidOperationException(blocked);
        // This labelled action acknowledges a securely resting arm and a clear
        // zero path. It does not infer support from joint angles or power bits.
        var operation = BeginRecovery(RecoveryKind.ReturnToZero, current.Device, permissionEpoch, cancellation);
        recoveryHomeProgress = "Preparing recovery to zero · previous movement discarded";
        var (profile, backend, deviceId, generation) = operation.Scope;
        string runId = Guid.NewGuid().ToString("N");
        string report = Path.Combine(engine.Start.SettingsDirectory, "recovery", profile, "last-recovery-to-zero.json");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        async Task SaveProgress(string stage, string? error = null)
        {
            if (operation.Epoch == CommandAuthority.Epoch) recoveryHomeProgress = message = stage;
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            string temporary = report + "." + runId + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new {
                schema = 1, runId, robotProfile = profile, deviceId, generation, startedUtc = started,
                updatedUtc = DateTimeOffset.UtcNow, stage, error, goal = "all-joints-zero", speedPercent = RecoveryHome.SpeedPercent,
                supportAcknowledgement = "arm-already-resting-securely-on-surface", previousTargetDiscarded = true, automaticResume = false
            }, PluginProtocol.Json));
            File.Move(temporary, report, overwrite: true);
        }
        operation.Worker = Task.Run(async () =>
        {
            bool entered = false;
            bool completed = false;
            try
            {
                await commands.WaitAsync(operation.Token); entered = true;
                await SaveProgress(recoveryHomeProgress);
                await RecoveryHome.Run(deviceId, generation, owner, () => HomeSnapshot(operation),
                    async (step, stepCancellation) =>
                    {
                        var before = HomeSnapshot(operation);
                        if (step == "reset") Volatile.Write(ref operation.ResetAt, Stopwatch.GetTimestamp());
                        else if (step != "verify-reset") Volatile.Write(ref operation.ResetAt, 0);
                        if (step is "enable" or "move-to-zero") GrantRecoveryHolding(operation, stepCancellation);
                        var reply = await Command(step, null, stepCancellation, recoveryAcknowledged: step is "reset" or "verify-reset" or "verify-current",
                            expectedDeviceId: deviceId, expectedGeneration: generation, expectedToken: backend,
                            expectedPermissionEpoch: operation.Epoch, speedOverridePercent: RecoveryHome.SpeedPercent);
                        var receipt = telemetry.Latest;
                        long request = reply["request_id"]?.GetValue<long>() ?? before.Motion["request_id"]?.GetValue<long>() ?? 0;
                        return new(request, receipt?.Sequence ?? 0, Stopwatch.GetTimestamp(), reply["request_id"] is not null);
                    }, async (request, stopping) =>
                    {
                        // A timeout within this operation can still stop its own
                        // exact move. Priority STOP revocation already owns the
                        // transport if the epoch changed.
                        if (!RecoveryScopeMatches(operation)) return;
                        var current = HomeSnapshot(operation);
                        if (current.Device["id"]?.GetValue<string>() != deviceId || current.Device["generation"]?.GetValue<long>() != generation ||
                            current.Motion["request_id"]?.GetValue<long>() != request || current.Motion["owner"]?.GetValue<string>() != owner ||
                            current.Motion["active"]?.GetValue<bool>() != true) return;
                        await Call("/api/stop", new JsonObject { ["device_id"] = deviceId, ["generation"] = generation,
                            ["request_id"] = request, ["owner"] = owner }, stopping, backend);
                    }, stage => SaveProgress(stage), operation.Token);
                CompleteRecoveryHolding(operation);
                completed = true;
            }
            catch (Exception error)
            {
                string stage = error is OperationCanceledException ? "Recovery to zero cancelled · previous movement remains discarded"
                    : "Recovery to zero stopped: " + error.Message;
                try { await SaveProgress(stage, error.Message); }
                catch (Exception storage) { message = stage + ". Recovery report could not be saved: " + storage.Message; }
            }
            finally
            {
                if (!completed)
                    CommandAuthority.TryInEpoch(operation.Epoch, () => approved = linkCommandsEnabled = false);
                RetireRecovery(operation);
                if (entered) commands.Release();
                try { await Update(shutdown.Token); } catch (Exception error) { Trace.TraceWarning("Recovery view: {0}", error.Message); }
            }
        }, CancellationToken.None);
        await Update(cancellation);
    }

    private void AddRecoveryHomePatches(Dictionary<string, JsonObject> patches)
    {
        string? blocked = RecoveryHomeBlocker();
        patches["recovery.home"] = new() { ["text"] = RecoveryHomeBusy ? "Recovering to zero…" : "Arm resting, path clear — recover to zero",
            ["enabled"] = blocked is null, ["hint"] = blocked ??
            "Rest the arm securely on a support and clear its path to zero. The controller verifies recovery and moves at 10%." };
        if (!RecoveryHomeBusy) return;
        patches["recovery.next"]["text"] = recoveryHomeProgress;
        patches["status"] = new() { ["text"] = recoveryHomeProgress };
        foreach (string id in patches.Keys.Where(id => id != "recovery.home" &&
            (WorkspaceCommandPolicy.RequiresControls(id) || id is "link" or "recovery.run" or "motion.hold" ||
            id.StartsWith("profiles.", StringComparison.Ordinal) || id.StartsWith("teaching.", StringComparison.Ordinal) ||
            id.StartsWith("backend.", StringComparison.Ordinal))).ToArray())
            patches[id]["enabled"] = false;
    }
}
