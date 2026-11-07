using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private volatile bool recoveryHomeBusy, recoveryHomeResetting;
    private string recoveryHomeDevice = "", recoveryHomeToken = "", recoveryHomeProfile = "", recoveryHomeProgress = "";
    private long recoveryHomeGeneration, recoveryHomeEpoch;
    private CancellationTokenSource? recoveryHomeCancellation;
    private Task? recoveryHomeWorker;

    private RecoveryHomeSnapshot HomeSnapshot(bool enforceScope = true)
    {
        if (enforceScope && (token != recoveryHomeToken || ActiveProfile.Id != recoveryHomeProfile ||
            recoveryHomeEpoch != Interlocked.Read(ref targetPermissionEpoch)))
            throw new InvalidOperationException("Recovery was cancelled by STOP, a control-permission change or a different robot/backend. No remaining stage will run.");
        var receipt = telemetry.Latest ?? throw new InvalidOperationException("Waiting for controller feedback.");
        var device = RobotProfileRouting.Device(preferences, receipt.State);
        if (receipt.BackendToken != token || !receipt.HasFreshDevice(device, Stopwatch.GetTimestamp()))
        { device = (JsonObject)device.DeepClone(); device["fresh"] = false; }
        long captured = receipt.ReceivedTicks - (long)(receipt.SourceAgeMilliseconds * Stopwatch.Frequency / 1000);
        return new(device, receipt.State["motion"]?.AsObject() ?? new(), receipt.State["phase"]?.GetValue<string>() ?? "",
            receipt.State["message"]?.GetValue<string>() ?? "Controller state unavailable", receipt.Sequence, captured);
    }

    private bool RecoveryHomeExpectedGap(TelemetryReceipt? receipt) => recoveryHomeBusy && recoveryHomeResetting && receipt is not null &&
        receipt.BackendToken == recoveryHomeToken && receipt.IsFresh(Stopwatch.GetTimestamp()) &&
        TeachingResetAssisted.ExpectedFeedbackGap(true, receipt.State, recoveryHomeDevice, recoveryHomeGeneration);

    private async Task StartRecoveryHome(CancellationToken cancellation)
    {
        if (workspacePage != "controls" || calibrationOpen) throw new InvalidOperationException("Return to Controls before recovering to zero.");
        var current = HomeSnapshot(enforceScope: false);
        string? blocked = RecoveryHome.Blocker(current);
        if (blocked is not null) throw new InvalidOperationException(blocked);
        recoveryHomeProfile = ActiveProfile.Id; recoveryHomeToken = token;
        recoveryHomeDevice = current.Device["id"]!.GetValue<string>(); recoveryHomeGeneration = current.Device["generation"]!.GetValue<long>();
        // This labelled action acknowledges a securely resting arm and a clear
        // zero path. It does not infer support from joint angles or power bits.
        CancelGroupSimulationFollowingInput("Recovery to zero requested · previous follower targets discarded");
        recoveryHomeEpoch = Interlocked.Increment(ref targetPermissionEpoch);
        // The actor drops previously queued controls by epoch at dequeue. Do
        // not cancel its currently executing event: it starts this operation.
        approved = linkCommandsEnabled = teleoperating = recoveryInProgress = false;
        lease = lastCommand = followRequest = 0; followInput.End();
        recoveryHomeBusy = true; recoveryHomeProgress = "Preparing recovery to zero · previous movement discarded";
        recoveryHomeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        recoveryHomeCancellation.CancelAfter(TimeSpan.FromSeconds(90));
        var operation = recoveryHomeCancellation;
        string runId = Guid.NewGuid().ToString("N"), profile = recoveryHomeProfile, deviceId = recoveryHomeDevice, backend = recoveryHomeToken;
        long generation = recoveryHomeGeneration, epoch = recoveryHomeEpoch;
        string report = Path.Combine(engine.Start.SettingsDirectory, "recovery", profile, "last-recovery-to-zero.json");
        DateTimeOffset started = DateTimeOffset.UtcNow;
        async Task SaveProgress(string stage, string? error = null)
        {
            recoveryHomeProgress = stage; message = stage;
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            string temporary = report + "." + runId + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new {
                schema = 1, runId, robotProfile = profile, deviceId, generation, startedUtc = started,
                updatedUtc = DateTimeOffset.UtcNow, stage, error, goal = "all-joints-zero", speedPercent = RecoveryHome.SpeedPercent,
                supportAcknowledgement = "arm-already-resting-securely-on-surface", previousTargetDiscarded = true, automaticResume = false
            }, PluginProtocol.Json));
            File.Move(temporary, report, overwrite: true);
        }
        recoveryHomeWorker = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await commands.WaitAsync(operation.Token); entered = true;
                await SaveProgress(recoveryHomeProgress);
                await FinishAlignment("Recovery to zero", operation.Token);
                await RecoveryHome.Run(true, deviceId, generation, owner, () => HomeSnapshot(),
                    async (step, stepCancellation) =>
                    {
                        var before = HomeSnapshot();
                        recoveryHomeResetting = step is "reset" or "verify-reset";
                        if (step is "enable" or "move-to-zero") approved = true;
                        var reply = await Command(step, null, stepCancellation, recoveryAcknowledged: step is "reset" or "verify-reset" or "verify-current",
                            expectedDeviceId: deviceId, expectedGeneration: generation, expectedToken: backend,
                            expectedPermissionEpoch: epoch, speedOverridePercent: RecoveryHome.SpeedPercent);
                        var receipt = telemetry.Latest;
                        long request = reply["request_id"]?.GetValue<long>() ?? before.Motion["request_id"]?.GetValue<long>() ?? 0;
                        return new(request, receipt?.Sequence ?? 0, Stopwatch.GetTimestamp(), reply["request_id"] is not null);
                    }, async (request, stopping) =>
                    {
                        var currentScope = HomeSnapshot();
                        if (currentScope.Motion["request_id"]?.GetValue<long>() != request) return;
                        await Call("/api/stop", new JsonObject { ["device_id"] = deviceId, ["generation"] = generation,
                            ["request_id"] = request, ["owner"] = owner }, stopping, backend);
                    }, SaveStage, operation.Token);
                HomeSnapshot();
                approved = true; linkCommandsEnabled = link is not null;
                teachingSeen = approvalBeforeTeaching = false;
                async Task SaveStage(string stage) { await SaveProgress(stage); }
            }
            catch (Exception error)
            {
                approved = linkCommandsEnabled = false;
                string stage = error is OperationCanceledException ? "Recovery to zero cancelled · previous movement remains discarded"
                    : "Recovery to zero stopped: " + error.Message;
                try { await SaveProgress(stage, error.Message); }
                catch (Exception storage) { message = stage + ". Recovery report could not be saved: " + storage.Message; }
            }
            finally
            {
                recoveryHomeBusy = recoveryHomeResetting = false;
                lease = lastCommand = followRequest = 0;
                operation.Dispose();
                if (ReferenceEquals(recoveryHomeCancellation, operation)) recoveryHomeCancellation = null;
                if (entered) commands.Release();
                try { await Update(shutdown.Token); } catch (Exception error) { Trace.TraceWarning("Recovery view: {0}", error.Message); }
            }
        }, CancellationToken.None);
        await Update(cancellation);
    }

    private void CancelRecoveryHome()
    {
        try { recoveryHomeCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void AddRecoveryHomePatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        string? blocked;
        try { blocked = RecoveryHome.Blocker(HomeSnapshot(enforceScope: false)); }
        catch (InvalidOperationException error) { blocked = error.Message; }
        patches["recovery.home"] = new() { ["text"] = recoveryHomeBusy ? "Recovering to zero…" : "Resting & clear — recover to zero",
            ["enabled"] = !recoveryHomeBusy && !teachingResetBusy && !recoveryInProgress &&
            workspacePage == "controls" && !calibrationOpen && !SimulationFollowingActive && blocked is null, ["hint"] = blocked ??
            "Use only while the arm rests securely on a support and its route to zero is clear. Healthy enabled motors keep holding; healthy disabled motors enable at their measured pose. A stopped controller resets and verifies off first. Then move to zero at 10%. No prior motion or Pika action resumes." };
        patches["recovery.home.incident"] = (JsonObject)patches["recovery.home"].DeepClone();
        if (patches["recovery.run"].TryGetPropertyValue("text", out var recoveryText))
            patches["recovery.run"]["text"] = recoveryText!.GetValue<string>() + " (controller only)";
        if (!recoveryHomeBusy && patches["recovery.home"]["enabled"]?.GetValue<bool>() == true)
            patches["recovery.next"] = new() { ["text"] = "Let the arm rest securely on the desk or a support, with its route to zero clear. Recover to zero handles the remaining steps; you do not need to lift it." };
        if (!recoveryHomeBusy) return;
        patches["workspace.incident"] = new() { ["visible"] = true };
        patches["recovery"] = new() { ["visible"] = true };
        patches["recovery.next"] = new() { ["text"] = recoveryHomeProgress };
        patches["recovery.home"]!["text"] = "Recovering to zero…";
        patches["status"] = new() { ["text"] = recoveryHomeProgress };
        foreach (string id in patches.Keys.Where(id => id != "stop" && id is not ("recovery.home" or "recovery.home.incident") &&
            (WorkspaceCommandPolicy.RequiresControls(id) || id is "disable" or "link" or "recovery.run" or "motion.hold" ||
            id.StartsWith("profiles.", StringComparison.Ordinal) || id.StartsWith("teaching.", StringComparison.Ordinal) ||
            id.StartsWith("backend.", StringComparison.Ordinal))).ToArray())
            patches[id]["enabled"] = false;
    }
}
