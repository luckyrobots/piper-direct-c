using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

internal enum RobotPrimaryAction { None, Enable, Resume, OpenRecovery, OpenStoppedRecovery, StopFollowing }

/// <summary>Plugin-5 primary robot action: Idle → Running → Idle. Running reports
/// stages (received → enable → link-scene, or for a resume waiting-for-stillness →
/// returning-to-controls); ResumingControls is Running while ResumeControls owns the
/// arm. Idle keeps the last status (complete or failed) visible.</summary>
internal enum PrimaryActionPhase { Idle, Running, ResumingControls }

/// <summary>Telemetry updates numbers; only a meaningful controller transition changes the primary action.</summary>
public sealed record RobotReadiness(string Label, string Action, bool Enabled, string Detail)
{
    internal RobotPrimaryAction NextAction { get; init; }

    public static RobotReadiness Inspect(JsonObject? device, bool approved, bool gripperReady, bool following, bool moving) =>
        Inspect(device, approved, gripperReady, following, moving, measuredLinkReady: true);

    internal static RobotReadiness Inspect(JsonObject? device, bool approved, bool gripperReady, bool following, bool moving, bool measuredLinkReady)
    {
        static RobotReadiness Step(string label, string text, string detail, RobotPrimaryAction action = RobotPrimaryAction.None) =>
            new(label, text, action != RobotPrimaryAction.None, detail) { NextAction = action };
        if (following) return Step("Following simulation", "Stop following", "The real robot follows the prepared scene. Stop holds the measured pose.", RobotPrimaryAction.StopFollowing);
        if (device?["fresh"]?.GetValue<bool>() != true) return Step("Waiting for feedback", "Connecting…", "Reconnecting automatically; previous movement will not resume.");
        bool recoveryRequired = device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true;
        if (recoveryRequired && device["status"]?["arm_status"]?.GetValue<int>() == 1)
            return Step("Stopped", "Recover robot", "Controller STOP is latched. Support the arm; use Disable, then the supported reset in Diagnostics. The interrupted target will not replay.", RobotPrimaryAction.OpenStoppedRecovery);
        var teaching = TeachingState.Inspect(device);
        if (teaching.HandGuiding) return Step("Hand guiding", "Hand guiding active", "Move the arm to a clear position. Single-click its button again to finish.");
        if (teaching.Active) return Step("Onboard playback", "Playback active", "The physical controller owns this movement.");
        if (recoveryRequired)
            return Step("Stopped", "Recover robot", device["blocker"]?.GetValue<string>() ?? "Move the arm to a safe pose using its physical hand-guide button.", RobotPrimaryAction.OpenRecovery);
        // Stillness is an admission condition, not a rapidly changing click
        // target. The explicit action waits boundedly for native eligibility.
        if (teaching.ReturnRequired) return Step(teaching.Phase == "ended" ? "Hand guiding off" : "Controller needs attention",
            "Resume controls", teaching.NextStep, teaching.Phase == "ended" || teaching.CanReturn ? RobotPrimaryAction.Resume : RobotPrimaryAction.None);
        var power = RobotPowerState.Inspect(device);
        if (!power.EnabledCount.HasValue) return Step("Waiting for motor feedback", "Connecting…", "Waiting for fresh feedback from all six motors.");
        if (moving) return Step("Movement in progress", "Moving…", "Stop movement remains available.");
        bool needsConnection = !power.AllEnabled || !approved || !gripperReady || !measuredLinkReady;
        string blocker = device["blocker"]?.GetValue<string>() ?? "";
        if (blocker.Length > 0)
        {
            bool informationPending = blocker.StartsWith("Firmware has not been read", StringComparison.Ordinal) ||
                blocker.StartsWith("Reading joint limits", StringComparison.Ordinal) || blocker.StartsWith("Reading acceleration limits", StringComparison.Ordinal);
            if (informationPending)
            {
                // The selected backend refreshes this information read-only.
                // Initial Enable must remain reachable; native admission still
                // refuses motor/target commands until its query completes.
                return Step("Reading robot settings", needsConnection ? "Enable robot" : "Reading robot settings…",
                    blocker + " Settings refresh automatically for the selected robot; following waits until they are verified.",
                    needsConnection ? RobotPrimaryAction.Enable : RobotPrimaryAction.None);
            }
            return Step("Controller needs attention", "Controls unavailable", blocker);
        }
        if (needsConnection) return Step(power.AllEnabled ? "Enabled · finish connection" : "Disabled", "Enable robot", "One action connects the assigned arm and gripper and restores holding control.", RobotPrimaryAction.Enable);
        return Step("Ready · arm and gripper enabled", "Enabled", "Select this robot's Follow simulation checkbox, then use Follow simulation in the left sidebar.");
    }
}

public sealed partial class PipersPlugin
{
    internal sealed record PrimaryActionState(PrimaryActionPhase Phase, string Id, string Status);
    private PrimaryActionState primaryAction = new(PrimaryActionPhase.Idle, "", "");
    private bool PrimaryActionBusy => primaryAction.Phase != PrimaryActionPhase.Idle;
    private bool ResumeControlsBusy => primaryAction.Phase == PrimaryActionPhase.ResumingControls;
    private JsonObject[] pikaDevices = [];
    private SceneRobotBindingOption[] settingsRobotOptions = [];
    private string pikaInventoryMessage = "Discover connected grippers";
    private long pikaInventoryAt;

    private RobotReadiness Readiness(JsonObject? device) => RobotReadiness.Inspect(device, approved,
        ActiveProfile.PikaDevice.Length == 0 || PikaMatchesProfile() && pikaOwned && state?["pika"]?["fresh"]?.GetValue<bool>() == true &&
            state["pika"]?["enabled"]?.GetValue<bool>() == true && state["pika"]?["armed"]?.GetValue<bool>() == true,
        GroupFollowIncludes(ActiveProfile.Id), state?["motion"]?["active"]?.GetValue<bool>() == true,
        measuredLinkReady: ActiveProfile.RobotId.Length == 0 || link is not null);

    private bool PrimaryActionAvailable => !switchingProfile && CurrentRecovery is null && !GroupFollowActive;

    private Task PrimaryRobotAction(CancellationToken cancellation) => RunPrimaryAction(RunPrimaryRobotAction, cancellation);

    private async Task RunPrimaryAction(Func<CancellationToken, Task> action, CancellationToken cancellation)
    {
        primaryAction = new(PrimaryActionPhase.Running, Guid.NewGuid().ToString("N"), primaryAction.Status);
        try
        {
            await PrimaryActionProgress("received", "Checking the selected robot and gripper…", cancellation);
            await action(cancellation);
            await PrimaryActionProgress("complete", SimulationFollowingActive || message.StartsWith("Ready ·", StringComparison.Ordinal) ? "" : message, cancellation);
        }
        catch (Exception error)
        {
            // The primary action lives above the camera/joint controls. A
            // failure must stay visible there, rather than only in a footer
            // below the scroll area. Cancellation never resumes the action.
            primaryAction = primaryAction with { Status = error is OperationCanceledException
                ? "Action cancelled. No previous movement will resume."
                : "Could not start: " + error.Message };
            await WritePrimaryActionReceipt("failed", error.Message);
            throw;
        }
        finally
        {
            primaryAction = primaryAction with { Phase = PrimaryActionPhase.Idle };
            try { using var presentation = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await PatchPrimaryAction(presentation.Token); }
            catch (Exception error) { Trace.TraceWarning("Primary action presentation: {0}", error.Message); }
        }
    }

    private async Task RunPrimaryRobotAction(CancellationToken cancellation)
    {
        if (!PrimaryActionAvailable) { message = "Wait for the current robot action to finish."; return; }
        long epoch = CommandAuthority.Epoch;
        var profile = ActiveProfile;
        string backend = token;
        var device = Device(expectedToken: backend);
        var readiness = Readiness(device);
        switch (readiness.NextAction)
        {
            case RobotPrimaryAction.OpenStoppedRecovery:
                BeginHandGuideRecovery(device);
                message = RecoveryCardState(device).NextStep;
                SetRecoveryGuideMessage(message);
                break;
            case RobotPrimaryAction.OpenRecovery:
                BeginHandGuideRecovery(device);
                break;
            case RobotPrimaryAction.Resume:
                await ResumeControls(cancellation);
                break;
            case RobotPrimaryAction.Enable:
                var scope = ControlScope(profile, backend, device);
                if (!approved)
                {
                    if (!CommandAuthority.TryOpenConfirmation(epoch, profile.Id, scope))
                        throw new InvalidOperationException("Control permission changed. Click Enable robot to confirm the current connection.");
                    await Render(cancellation);
                }
                else await CompleteRobotEnable(epoch, scope, cancellation);
                break;
            case RobotPrimaryAction.StopFollowing:
                await StopGroupSimulationFollowing("Operator stopped the follower group", cancellation);
                break;
            default:
                message = readiness.Detail;
                break;
        }
    }

    private async Task CompleteRobotEnable(long expectedPermission, FollowControlApproval scope, CancellationToken cancellation)
    {
        await PrimaryActionProgress("enable", "Verifying arm and gripper control…", cancellation);
        var device = Device(scope.Device, scope.Generation, scope.Backend);
        if (scope != ControlScope(ActiveProfile, scope.Backend, device))
            throw new InvalidOperationException("Robot assignments changed during Enable. Confirm the current connection.");
        await Enable(cancellation, expectedDeviceId: scope.Device, expectedGeneration: scope.Generation,
            expectedToken: scope.Backend, expectedPermissionEpoch: expectedPermission);
        if (link is null)
        {
            await PrimaryActionProgress("link-scene", "Matching measured feedback to the selected scene robot…", cancellation);
            if (expectedPermission != CommandAuthority.Epoch) throw new InvalidOperationException("The previous Enable action was retired.");
            await StartMeasuredLink(automatic: false, cancellation);
        }
        RememberControlApproval(expectedPermission);
        message = "Ready · arm and gripper enabled. Select follower robots, then use Follow simulation in the left sidebar.";
    }

    private async Task PrimaryActionProgress(string stage, string text, CancellationToken cancellation)
    {
        primaryAction = primaryAction with { Status = text };
        await WritePrimaryActionReceipt(stage);
        await PatchPrimaryAction(cancellation);
    }

    private Task PatchPrimaryAction(CancellationToken cancellation) => Patch(new() { ["workspace.primary-action"] = PrimaryActionPatch() }, cancellation);

    private JsonObject PrimaryActionPatch()
    {
        string status = primaryAction.Status;
        return new() { ["text"] = status, ["visible"] = Navigation.Page == WorkspacePage.Controls && status.Length > 0, ["hint"] = status };
    }

    private async Task WritePrimaryActionReceipt(string stage, string? failure = null)
    {
        string? temporary = null;
        try
        {
            string directory = Path.Combine(engine.Start.SettingsDirectory, "diagnostics", "primary-action");
            Directory.CreateDirectory(directory);
            var telemetryReceipt = telemetry.Latest;
            var pika = telemetryReceipt?.State["pika"];
            double pikaAge = telemetryReceipt?.PikaEvidenceAgeMilliseconds(Stopwatch.GetTimestamp()) ?? double.NaN;
            var receipt = new { id = primaryAction.Id, stage, atUtc = DateTimeOffset.UtcNow,
                profileId = ActiveProfile.Id, robotId = ActiveProfile.RobotId, jointPrefix = ActiveProfile.RobotJointPrefix,
                canDeviceId = ActiveProfile.CanDeviceId, approved, pikaOwned, linked = link is not null,
                following = SimulationFollowingActive, followAfterEnable = false, failure,
                pikaFeedbackAgeMilliseconds = double.IsFinite(pikaAge) ? (double?)pikaAge : null,
                pikaDevice = pika?["device"]?.GetValue<string>(), pikaPending = pika?["pending"]?.GetValue<bool>(),
                pikaSpeed = pika?["telemetry"]?["motor"]?["Speed"]?.GetValue<double>() };
            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt, PluginProtocol.Json));
            File.Move(temporary, Path.Combine(directory, "latest.json"), overwrite: true);
        }
        catch (Exception error) { Trace.TraceWarning("Primary action diagnostic: {0}", error.Message); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private void AddUnifiedWorkspacePatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        var readiness = Readiness(device);
        bool controls = Navigation.Page == WorkspacePage.Controls;
        bool controllerStopped = device?["status"]?["arm_status"]?.GetValue<int>() == 1;
        bool canReleasePower = controllerStopped || !TeachingState.Inspect(device).Active;
        bool groupParticipant = GroupFollowIncludes(ActiveProfile.Id);
        patches["workspace.connection"] = new() {
            ["text"] = groupParticipant ? GroupFollowStatus : ResumeControlsBusy ? "Reconnecting controls" : readiness.Label,
            ["hint"] = groupParticipant ? GroupFollowDetail : readiness.Detail };
        patches["enable"] = new() { ["text"] = ResumeControlsBusy ? "Resuming controls…" : readiness.Action, ["visible"] = controls,
            ["enabled"] = controls && readiness.Enabled && !PrimaryActionBusy && PrimaryActionAvailable,
            ["hint"] = readiness.Detail };
        patches["workspace.primary-action"] = PrimaryActionPatch();
        patches["disable"] = new() { ["visible"] = controls && RobotPowerState.Inspect(device).EnabledCount > 0 && canReleasePower,
            ["enabled"] = controls && device?["fresh"]?.GetValue<bool>() == true && canReleasePower && !switchingProfile,
            ["hint"] = "Support the arm. Disable cancels pending plugin actions, stops following, then removes motor holding torque. It does not home or replay." };
        bool currentRecoveryGuide = RecoveryGuideFor(device);
        var guide = currentRecoveryGuide ? CurrentGuide : null;
        string guideMessage = guide?.Message ?? "";
        patches["workspace.incident"] = new() { ["visible"] = controls && RecoveryPanelVisible(device) };
        var recoveryView = RecoveryCardState(device);
        string recoveryCause = guide?.SupportedStop?["reason"]?.GetValue<string>() is { Length: > 0 } original ? "Recovery after: " + original : recoveryView.Cause;
        patches["incident"] = new() { ["text"] = recoveryCause, ["hint"] = recoveryCause };
        patches["recovery.next"] = new() { ["text"] = recoveryView.NextStep, ["hint"] = recoveryView.NextStep };
        patches["recovery.requirements"] = new() { ["text"] = recoveryView.Requirements, ["visible"] = recoveryView.Requirements.Length > 0, ["hint"] = recoveryView.Requirements };
        patches["recovery.feedback"] = new() { ["text"] = guideMessage, ["visible"] = guideMessage.Length > 0, ["hint"] = guideMessage };
        patches["recovery.card.run"] = new() { ["text"] = recoveryView.ActionText, ["enabled"] = RecoveryCardCanRun(recoveryView), ["hint"] = recoveryView.NextStep };
        patches["teaching"] = new() { ["visible"] = !controllerStopped && teaching.BlocksTargets || PikaTeachingControlsVisible };
        patches["teleoperate.toggle"]!["text"] = teleoperating ? "Finish dragging" : "Drag joints";
        patches["profiles.inventory"] = new() { ["text"] = pikaInventoryMessage };
        patches["recovery.guide.cancel"] = new() { ["visible"] = currentRecoveryGuide || CurrentRecovery is not null };
    }

    private Widget SettingsRobotPicker(string target) => Widget.Row("global.context." + target,
        preferences.Robots!.Select(p => Widget.Button("global.context." + target + "." + p.Id, p.Name) with {
            Appearance = p.Id == ActiveProfile.Id ? "selected" : "quiet", Hint = "Configure " + p.Name + " here in shared Settings" }).ToArray()) with { Spacing = 8 };

    private async Task RefreshPikaInventory(CancellationToken cancellation)
    {
        try
        {
            var reply = await Call("/api/pika/devices", new() { ["pika_distribution"] = ActiveProfile.PikaDistribution }, cancellation);
            pikaDevices = reply["devices"]?.AsArray().OfType<JsonObject>().Select(d => (JsonObject)d.DeepClone()).ToArray() ?? [];
            pikaInventoryMessage = pikaDevices.Length == 0 ? "No Pika detected. Connect its USB cable; discovery retries automatically." : $"{pikaDevices.Length} grippers detected · assignments are saved automatically";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { pikaInventoryMessage = "Gripper discovery: " + error.Message; }
        pikaInventoryAt = Stopwatch.GetTimestamp();
    }

    private Widget GlobalRobotsSettings()
    {
        var robots = SceneRobotBindings.Options(scene?["robots"]?.AsArray().OfType<JsonObject>() ?? []);
        settingsRobotOptions = robots;
        settingsRobotChoices = preferences.Robots!.ToDictionary(p => p.Id, p => SceneRobotBindings.AvailableFor(settingsRobotOptions, preferences.Robots!, p), StringComparer.Ordinal);
        var rows = new List<Widget> { Widget.Label("global.robot.assignment.help", "Connect each real robot to one simulated arm. Each arm can be assigned once.") with { Appearance = "muted", Height = 70 } };
        foreach (var profile in preferences.Robots!)
        {
            string id = "global.robot." + profile.Id;
            string[] canChoices = ["Unassigned", ..knownCanIds.Select(CanFriendlyName)];
            var devices = PikaChoices(profile);
            var armChoices = settingsRobotChoices[profile.Id];
            string[] pikaChoices = ["Unassigned", .. devices.Select(device =>
            {
                string bus = device["bus_id"]?.GetValue<string>() ?? "";
                string label = bus.Length > 0 ? "Pika · USB " + bus : device["name"]?.GetValue<string>() ?? "Saved Pika";
                return label + (device["available"]?.GetValue<bool>() == true ? " · connected" : " · setup needed");
            })];
            // Stack labeled fields rather than requiring a wide device grid.
            // With no fixed field width, native columns use the available pane.
            rows.Add(Widget.Column(id + ".row",
                Widget.Column(id + ".name.field", Widget.Label(id + ".name.caption", "Robot name") with { Height = 20 },
                    new Widget("input", id + ".name") { Text = profile.Name, Height = 30 }) with { Spacing = 2 },
                Widget.Column(id + ".can.field", Widget.Label(id + ".can.caption", "Arm connection") with { Height = 20 },
                    new Widget("dropdown", id + ".can") { Height = 30, Options = canChoices, Value = Array.IndexOf(knownCanIds, profile.CanDeviceId) + 1 }) with { Spacing = 2 },
                Widget.Column(id + ".pika.field", Widget.Label(id + ".pika.caption", "Gripper connection") with { Height = 20 },
                    new Widget("dropdown", id + ".pika") { Height = 30, Options = pikaChoices,
                        Value = Array.FindIndex(devices, d => d["device"]?.GetValue<string>() == profile.PikaDevice || d["aliases"]?.AsArray().Any(a => a?.GetValue<string>() == profile.PikaDevice) == true) + 1 }) with { Spacing = 2 },
                Widget.Column(id + ".scene.field", Widget.Label(id + ".scene.caption", "Simulation arm") with { Height = 20 },
                    new Widget("dropdown", id + ".scene") { Height = 30, Options = new[] { "Unassigned" }.Concat(armChoices.Select(r => r.Name)).ToArray(),
                        Value = Array.FindIndex(armChoices, r => r.Matches(profile)) + 1,
                        Hint = "Choose this real robot's simulated counterpart. Arms assigned to another robot are omitted." }) with { Spacing = 2 },
                SceneArmAssignment(profile, robots),
                Widget.Column(id + ".actions",
                    Widget.Button(id + ".setup", "Set up USB gripper") with { Width = 160,
                        Visible = OperatingSystem.IsWindows() && devices.Any(d => d["device"]?.GetValue<string>() == profile.PikaDevice && d["available"]?.GetValue<bool>() != true),
                        Hint = "One-time Windows USB sharing for the selected Pika; no motor commands." },
                    Widget.Button(id + ".remove", "Remove robot") with { Enabled = preferences.Robots.Length > 1, Appearance = "quiet" }) with { Spacing = 4 }) with { Spacing = 8, Padding = 10, Appearance = "panel" });
        }
        rows.Add(Widget.Column("global.robot.actions", Widget.Button("profiles.add", "+ Add robot") with { Enabled = preferences.Robots!.Length < RobotProfileRouting.MaximumProfiles },
            Widget.Button("global.refresh", "Refresh devices"), Widget.Label("profiles.inventory", pikaInventoryMessage) with { Appearance = "muted", Height = 72 }) with { Spacing = 6 });
        return Widget.Column("global.robots", rows.ToArray()) with { Spacing = 12 };
    }

    private string CanFriendlyName(string id)
    {
        var d = state?["devices"]?.AsArray().FirstOrDefault(d => d?["id"]?.GetValue<string>() == id);
        return (d?["name"]?.GetValue<string>() ?? "USB-CAN") + " · …" + id[^Math.Min(3, id.Length)..] + (d?["present"]?.GetValue<bool>() == true ? "" : " (offline)");
    }

    private JsonObject[] PikaChoices(RobotProfile profile)
    {
        if (profile.PikaDevice.Length == 0 || pikaDevices.Any(d => d["device"]?.GetValue<string>() == profile.PikaDevice || pikaDevices.Length == 1 && d["aliases"]?.AsArray().Any(a => a?.GetValue<string>() == profile.PikaDevice) == true)) return pikaDevices;
        return [..pikaDevices, new JsonObject { ["device"] = profile.PikaDevice, ["distribution"] = profile.WslDistribution, ["name"] = "Saved Pika", ["available"] = false }];
    }

    private async Task GlobalSettingsEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "ghosts")
        {
            bool visible = value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0;
            await SetMeasuredGhostVisibility(visible, cancellation);
            preferences = preferences with { ShowMeasuredGhosts = visible };
            await Save(cancellation); return;
        }
        if (action == "refresh") { await RefreshPikaInventory(cancellation); await Render(cancellation); return; }
        if (SimulationFollowingActive && !action.StartsWith("context.", StringComparison.Ordinal)) throw new InvalidOperationException("Stop following before changing robot or camera assignments. Settings can stay open while the episode runs.");
        if (action.StartsWith("context.", StringComparison.Ordinal))
        {
            string[] parts = action.Split('.');
            var profile = preferences.Robots!.Single(p => p.Id == parts[2]);
            if (profile.Id != ActiveProfile.Id) await SwitchProfile(profile, false, cancellation);
            await WorkspaceEvent("settings." + parts[1], cancellation); return;
        }
        string[] path = action.Split('.');
        if (path.Length != 3 || path[0] != "robot") throw new InvalidOperationException("Unknown settings action.");
        var before = preferences.Robots!.Single(p => p.Id == path[1]);
        var updated = before;
        switch (path[2])
        {
            case "setup":
                var usb = pikaDevices.SingleOrDefault(d => d["device"]?.GetValue<string>() == before.PikaDevice)
                    ?? throw new InvalidOperationException("Choose a detected USB gripper first.");
                string instance = usb["windows_instance_id"]?.GetValue<string>() ?? "";
                if (!OperatingSystem.IsWindows() || !System.Text.RegularExpressions.Regex.IsMatch(instance, @"^USB\\VID_1A86&PID_7522\\[^\r\n]+$"))
                    throw new InvalidOperationException("Only a detected Pika USB device can be shared with WSL.");
                string installer = Path.Combine(engine.Start.PackageDirectory, "tools", "Setup-PikaUsb.ps1");
                if (!File.Exists(installer)) throw new InvalidOperationException("Update the plugin package to include its USB setup helper.");
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installer, "-InstanceId", instance, "-Distribution", before.WslDistribution }) start.ArgumentList.Add(argument);
                using (var setup = Process.Start(start) ?? throw new InvalidOperationException("USB setup did not start."))
                { await setup.WaitForExitAsync(cancellation); if (setup.ExitCode != 0) throw new InvalidOperationException("USB setup did not complete. Check Diagnostics and retry setup."); }
                await RefreshPikaInventory(cancellation); await Render(cancellation); return;
            case "name": updated = before with { Name = RobotProfileRouting.CleanName(value.GetString() ?? "") }; break;
            case "can":
                int can = value.GetInt32() - 1;
                if (can < -1 || can >= knownCanIds.Length) throw new InvalidOperationException("Refresh the device list.");
                string canId = can < 0 ? "" : knownCanIds[can];
                RobotProfileRouting.ValidateCanAssignment(preferences, before.Id, canId);
                updated = before with { CanDeviceId = canId }; break;
            case "pika":
                var choices = PikaChoices(before); int selected = value.GetInt32() - 1;
                if (selected < -1 || selected >= choices.Length) throw new InvalidOperationException("Refresh the gripper list.");
                string device = selected < 0 ? "" : choices[selected]["device"]!.GetValue<string>();
                string distribution = selected < 0 ? before.WslDistribution : choices[selected]["distribution"]?.GetValue<string>() ?? before.WslDistribution;
                if (device.Length > 0 && preferences.Robots!.Any(p => p.Id != before.Id && p.PikaDevice == device && p.WslDistribution == distribution))
                    throw new InvalidOperationException("That Pika is already assigned to another robot.");
                updated = before with { PikaDevice = device, WslDistribution = distribution }; break;
            case "scene":
                if (!settingsRobotChoices.TryGetValue(before.Id, out var robots)) throw new InvalidOperationException("Refresh scene robots.");
                int sceneIndex = value.GetInt32() - 1;
                if (sceneIndex < -1 || sceneIndex >= robots.Length) throw new InvalidOperationException("Refresh scene robots.");
                string entityId = sceneIndex < 0 ? "" : robots[sceneIndex].EntityId, jointPrefix = sceneIndex < 0 ? "" : robots[sceneIndex].JointPrefix;
                RobotProfileRouting.ValidateSceneAssignment(preferences, before.Id, entityId, jointPrefix);
                updated = before with { RobotId = entityId, RobotJointPrefix = jointPrefix }; break;
            case "remove":
                if (preferences.Robots!.Length <= 1) throw new InvalidOperationException("Keep at least one robot.");
                await RetireAssignedGripper(before, cancellation);
                if (before.Id == ActiveProfile.Id) await SwitchProfile(preferences.Robots.First(p => p.Id != before.Id), false, cancellation);
                preferences = preferences with { Robots = preferences.Robots!.Where(p => p.Id != before.Id).ToArray() };
                await Save(cancellation); await Render(cancellation); return;
            default: throw new InvalidOperationException("Unknown robot setting.");
        }
        if (before.PikaDevice != updated.PikaDevice || before.WslDistribution != updated.WslDistribution)
            await RetireAssignedGripper(before, cancellation);
        if (before.Id == ActiveProfile.Id && path[2] != "name") await SwitchProfile(updated, false, cancellation);
        else { preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == before.Id ? updated : p).ToArray() }; await Save(cancellation); }
        message = "Settings saved";
        // A text edit must not destroy its own input. Structural changes render
        // once; ordinary names and live device telemetry use patches.
        if (path[2] != "name") await Render(cancellation);
    }

    private async Task RetireAssignedGripper(RobotProfile profile, CancellationToken cancellation)
    {
        if (profile.PikaDevice.Length == 0 || state is not { } snapshot) return;
        var gripper = ProfilePikaRouting.Find(snapshot, profile);
        if (gripper is null || gripper["connected"]?.GetValue<bool>() != true) return;
        if (!ProfilePikaRouting.CanReleaseAssignment(gripper, owner, TelemetryIsFresh()))
            throw new InvalidOperationException("This gripper is still holding or its power is unverified. Disable the robot before changing or removing its gripper assignment.");
        if (gripper["owner"]?.GetValue<string>() is { Length: > 0 } gripperOwner && gripperOwner != owner)
            throw new InvalidOperationException("Another controller owns this gripper. Finish its session before changing the assignment.");
        if (gripper["owner"]?.GetValue<string>() == owner)
            await Call("/api/pika/disconnect", new() { ["owner"] = owner, ["pika_device"] = profile.PikaDevice, ["pika_distribution"] = profile.PikaDistribution }, cancellation);
    }
}
