using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private volatile bool teachingResetBusy;
    private bool teachingResetConfirmation;
    private string teachingResetDevice = "", teachingResetProgress = "";
    private long teachingResetGeneration;
    private string teachingResetBackendToken = "", teachingResetProfile = "";
    private CancellationTokenSource? teachingResetCancellation;
    private Task? teachingResetWorker;

    private TeachingResetSnapshot ResetSnapshot(string? expectedToken = null)
    {
        if (expectedToken is not null && token != expectedToken) throw new InvalidOperationException("The backend session changed; confirm support again before any new reset-assisted action.");
        var snapshot = state ?? throw new InvalidOperationException("Controller state is unavailable.");
        var device = RobotProfileRouting.Device(preferences, snapshot);
        if (Stopwatch.GetElapsedTime(Interlocked.Read(ref stateAt)).TotalMilliseconds > 500)
        {
            // Keep identity available for a generation-guarded best-effort STOP,
            // but never treat an old transport snapshot as fresh motor evidence.
            device = (JsonObject)device.DeepClone(); device["fresh"] = false;
        }
        return new(device, snapshot["motion"]?["active"]?.GetValue<bool>() == true);
    }

    private Widget ResetAssistedControls() => Widget.Column("teaching.reset",
        Widget.Button("teaching.reset.begin", "Reset-assisted return") with { Visible = teaching.Phase == "ended" },
        Widget.Label("teaching.reset.requirements", "Vendor supported reset posture: |J2| < 10°, |J3| < 10°, 12° < J5 < 45°; teach light OFF. This alternative briefly removes holding torque and explicitly re-enables six arm motors, without a position target.") with { Height = 68, Visible = teaching.Phase == "ended" },
        Widget.Column("teaching.reset.confirmation", Widget.Label("teaching.reset.warning", "Keep the arm physically supported throughout STOP → reset → verified motors off → enable. The arm will briefly lose holding torque. Confirm the teach light is OFF, the displayed supported posture is correct, and the workspace is clear. No homing, position target or recorded trajectory will be sent. Keep supporting until all six motors are verified enabled.") with { Height = 100 },
            Widget.Row("teaching.reset.confirmation.actions", Widget.Button("teaching.reset.confirm", "Supported & clear — reset and enable"), Widget.Button("teaching.reset.cancel", "Cancel"))) with { Visible = teachingResetConfirmation },
        Widget.Row("teaching.reset.running", Widget.Label("teaching.reset.progress", teachingResetProgress) with { Height = 52 },
            Widget.Button("teaching.reset.abort", "Cancel / STOP") with { Width = 130 }) with { Visible = teachingResetBusy });

    private async Task ResetAssistedEvent(string action, CancellationToken cancellation)
    {
        if (action == "cancel") { teachingResetConfirmation = false; await Render(cancellation); return; }
        var current = ResetSnapshot();
        string? reason = TeachingResetAssisted.Blocker(current, true);
        if (reason is not null) throw new InvalidOperationException(reason);
        if (action == "begin")
        {
            teachingResetDevice = current.Device["id"]!.GetValue<string>();
            teachingResetGeneration = current.Device["generation"]!.GetValue<long>();
            teachingResetBackendToken = token; teachingResetProfile = ActiveProfile.Id;
            teachingResetConfirmation = true;
            await Render(cancellation); return;
        }
        if (action != "confirm" || !teachingResetConfirmation) return;
        if (current.Device["id"]?.GetValue<string>() != teachingResetDevice || current.Device["generation"]?.GetValue<long>() != teachingResetGeneration ||
            token != teachingResetBackendToken || ActiveProfile.Id != teachingResetProfile)
        {
            teachingResetConfirmation = false;
            throw new InvalidOperationException("The arm connection changed after the confirmation was shown. Check support and confirm the new connection again.");
        }
        teachingResetConfirmation = false;
        teachingResetBusy = true;
        approved = linkCommandsEnabled = recoveryInProgress = false;
        teachingResetProgress = "Preparing supported reset-assisted return";
        teachingResetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        teachingResetCancellation.CancelAfter(TimeSpan.FromSeconds(35));
        var operationCancellation = teachingResetCancellation;
        string deviceId = teachingResetDevice; long generation = teachingResetGeneration;
        string backendToken = teachingResetBackendToken;
        // Return to the SDK event loop while the operation holds the command
        // semaphore. STOP can then cancel promptly instead of queuing behind reset.
        teachingResetWorker = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await commands.WaitAsync(operationCancellation.Token); entered = true;
                await FinishAlignment("Reset-assisted teaching return", operationCancellation.Token);
                teleoperating = false; followRequest = lease = lastCommand = 0; followInput.End();
                await TeachingResetAssisted.Run(true, deviceId, generation, () => ResetSnapshot(backendToken),
                    async (step, stepCancellation) =>
                    {
                        // STOP must remain possible during a feedback gap. Other
                        // steps require fresh data; all payloads retain the exact
                        // generation/token covered by the operator confirmation.
                        var pinned = step == "stop" ? ResetSnapshot(backendToken).Device : Device(deviceId, generation, backendToken);
                        if (pinned["id"]?.GetValue<string>() != deviceId || pinned["generation"]?.GetValue<long>() != generation)
                            throw new InvalidOperationException("The arm connection changed; no further reset-assisted action is allowed.");
                        if (step == "stop")
                        {
                            approved = linkCommandsEnabled = false;
                            await Call("/api/stop", new JsonObject { ["device_id"] = deviceId, ["generation"] = generation, ["owner"] = owner }, stepCancellation, backendToken);
                            lease = lastCommand = 0;
                        }
                        else if (step == "enable")
                        {
                            // The single confirmation explicitly covers this arm
                            // power transition; independent Pika power is untouched.
                            approved = true;
                            await Enable(stepCancellation, includePika: false, expectedDeviceId: deviceId, expectedGeneration: generation, expectedToken: backendToken);
                        }
                        else await Command(step, null, stepCancellation, recoveryAcknowledged: true,
                            expectedDeviceId: deviceId, expectedGeneration: generation, expectedToken: backendToken);
                    }, stage => { teachingResetProgress = stage; message = stage; }, operationCancellation.Token);
                approved = true; linkCommandsEnabled = link is not null;
                teachingSeen = approvalBeforeTeaching = false;
                message = teachingResetProgress + ". Inspect any remaining range warning before moving.";
            }
            catch (Exception error)
            {
                approved = linkCommandsEnabled = recoveryInProgress = false;
                lease = lastCommand = followRequest = 0; followInput.End();
                teachingResetProgress = error is OperationCanceledException ? "Reset-assisted return cancelled · keep the arm supported"
                    : "Reset-assisted return failed: " + error.Message;
                message = teachingResetProgress + ". No step will retry automatically. Inspect live motor state and use the displayed recovery step.";
            }
            finally
            {
                teachingResetBusy = false;
                operationCancellation.Dispose();
                if (ReferenceEquals(teachingResetCancellation, operationCancellation)) teachingResetCancellation = null;
                if (entered) commands.Release();
                try { await Render(shutdown.Token); await Update(shutdown.Token); }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Reset-assisted final view: {0}", error.Message); }
            }
        }, CancellationToken.None);
        await Render(cancellation);
    }

    private void CancelResetAssisted()
    {
        try { teachingResetCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        message = "Cancelling reset-assisted return · keep supporting the arm until motor state is verified";
    }

    private void AddResetAssistedPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        string? reason = device is null ? "Waiting for fresh device feedback." : TeachingResetAssisted.Blocker(new(device, state?["motion"]?["active"]?.GetValue<bool>() == true), true);
        bool ended = teaching.Phase == "ended";
        patches["teaching.reset.begin"] = new() { ["visible"] = ended && !teachingResetBusy, ["enabled"] = reason is null && !teachingResetBusy };
        patches["teaching.reset.requirements"] = new() { ["visible"] = ended && !teachingResetBusy, ["text"] = reason ?? "Reset-assisted return is available in the supported vendor posture. It briefly removes holding torque, verifies all six motors off, then explicitly enables them under one support/clearance confirmation. No position target or homing is sent." };
        patches["teaching.reset.confirmation"] = new() { ["visible"] = teachingResetConfirmation && !teachingResetBusy };
        patches["teaching.reset.confirm"] = new() { ["enabled"] = reason is null && !teachingResetBusy };
        patches["teaching.reset.running"] = new() { ["visible"] = teachingResetBusy };
        patches["teaching.reset.progress"] = new() { ["text"] = teachingResetProgress };
        if (!teachingResetBusy) return;
        // Disable mutation controls while the background operation owns commands.
        // The explicit Cancel/STOP and global STOP remain responsive.
        foreach (string id in new[] { "enable", "disable", "link", "robots.refresh", "robots.select", "teleoperate.toggle", "speed", "step", "tolerance", "pika.target", "zero.all", "recovery.run", "teaching.return", "profiles.add", "profiles.remove", "profiles.name", "profiles.can", "profiles.pika", "profiles.wsl", "profiles.activate", "backend.start", "backend.check", "backend.wsl", "backend.device", "alignment.start", "alignment.capture", "alignment.finish" })
            patches[id] = new() { ["enabled"] = false };
        foreach (var profile in preferences.Robots!) patches["profiles.select." + profile.Id] = new() { ["enabled"] = false };
        for (int i = 1; i <= 6; i++) foreach (string suffix in new[] { "minus", "plus", "target", "zero" }) patches[$"joint.{i}.{suffix}"] = new() { ["enabled"] = false };
    }

    private async Task StopResetAssistedOnDispose()
    {
        if (teachingResetWorker is not { IsCompleted: false } worker) return;
        CancelResetAssisted();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Reset-assisted shutdown: {0}", error.Message); }
    }
}
