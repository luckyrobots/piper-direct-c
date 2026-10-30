using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private TeachingState teaching = TeachingState.Inspect(null);
    private string teachingDevice = "";
    private long teachingGeneration;
    private bool teachingSeen, approvalBeforeTeaching, teachingHelpOpen, teachingReturnConfirmation;
    private JsonObject? acknowledgedScenePose;
    private long acknowledgedSceneAt;
    private AlignmentCheck? alignmentCheck;
    private string? alignmentPath;
    private AlignmentFrame? alignmentPending;
    private string alignmentStatus = "No alignment observations. Joint linking does not establish spatial calibration.";
    private bool alignmentOpen;
    private double alignmentGapMm;
    private AlignmentReference AlignmentOptions => ActiveProfile.Alignment ?? new();

    private void LoadAlignmentSummary()
    {
        string directory = Path.Combine(engine.Start.SettingsDirectory, "alignment-checks", ActiveProfile.Id);
        alignmentPath = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        alignmentPending = null; alignmentGapMm = 0;
        alignmentStatus = alignmentPath is null ? "No alignment observations for this robot. Joint linking does not establish spatial calibration."
            : "A saved visual-check report is available for this robot. Metric calibration and exposure synchronization remain unverified.";
    }

    private async Task ObserveTeaching(CancellationToken cancellation)
    {
        JsonObject? device = null;
        try { device = RobotProfileRouting.Device(preferences, state!); } catch (InvalidOperationException) { }
        TeachingState next = TeachingState.Inspect(device);
        string currentDevice = device?["id"]?.GetValue<string>() ?? "";
        long generation = device?["generation"]?.GetValue<long>() ?? 0;
        if (teachingDevice != currentDevice || teachingGeneration != generation)
        {
            teachingSeen = approvalBeforeTeaching = false;
            teachingDevice = currentDevice; teachingGeneration = generation;
            if (alignmentCheck is not null) await FinishAlignment("Connection or selected robot changed", cancellation);
        }
        if (next.BlocksTargets)
        {
            bool ownershipChanged = !teachingSeen || next.Active && next.Phase != teaching.Phase;
            if (!teachingSeen) { approvalBeforeTeaching = approved; teachingSeen = true; }
            approved = linkCommandsEnabled = false;
            // The controller has already yielded ownership to its physical teaching
            // mode. Discard the old gesture locally; do not inject a hold/STOP into it.
            if (ownershipChanged) { lease = lastCommand = followRequest = 0; followInput.End(); }
            if (teleoperating)
            {
                teleoperating = false;
                if (link is { } current)
                    await engine.RequestAsync("robot.teleoperate", new JsonObject {
                        ["sceneId"] = current.SceneId, ["entityId"] = current.EntityId, ["enabled"] = false }, cancellation);
            }
            if (next.Phase != teaching.Phase) message = next.Label + ". " + next.NextStep;
        }
        else if (teachingSeen && device is not null && TeachingCanResume(device))
        {
            // Restore only a previous UI approval. This sends no enable, reset,
            // replay or position packet, and a new gesture is still required.
            approved = approvalBeforeTeaching;
            linkCommandsEnabled = approved && link is not null;
            teachingSeen = approvalBeforeTeaching = false;
            teachingReturnConfirmation = false;
            message = approved ? "CAN control restored · previous targets discarded · measured scene tracking continues"
                : "Teaching ended and CAN control is healthy. Use Enable robot when ready; no movement was resumed.";
        }
        teaching = next;
    }

    public static bool TeachingCanResume(JsonObject device)
    {
        var mode = TeachingState.Inspect(device);
        var status = device["status"];
        return !mode.BlocksTargets && mode.Phase == "off" && device["fresh"]?.GetValue<bool>() == true &&
            device["recovery_required"]?.GetValue<bool>() != true && device["reset_pending"]?.GetValue<bool>() != true &&
            string.IsNullOrEmpty(device["blocker"]?.GetValue<string>()) && status?["arm_status"]?.GetValue<int>() == 0 &&
            status["error"]?.GetValue<int>() == 0 && status["control"]?.GetValue<int>() == 1 &&
            device["joints"] is JsonArray { Count: 6 } joints && joints.All(j => j?["fresh"]?.GetValue<bool>() == true &&
                j["enabled"]?.GetValue<bool>() == true && j["fault"]?.GetValue<int>() == 0);
    }

    private Widget TeachingControls() => Widget.Column("teaching",
        Widget.Row("teaching.header", Widget.Label("teaching.label", teaching.Label) with { Height = 34 },
            Widget.Button("teaching.help", "Hand-guide help") with { Width = 150 }),
        Widget.Label("teaching.next", teaching.NextStep) with { Height = 86, Visible = teaching.BlocksTargets || teachingHelpOpen },
        Widget.Label("teaching.zero", "") with { Height = 34, Visible = teaching.ReturnRequired },
        Widget.Button("teaching.return", "Return to CAN control") with { Visible = teaching.ReturnRequired, Enabled = teaching.CanReturn },
        Widget.Row("teaching.return.confirmation", Widget.Label("teaching.return.warning", "Confirm: the physical arm is at zero, the teach light is OFF and the workspace is clear. Support the arm during the control-mode handoff; holding behavior can change."),
            Widget.Button("teaching.return.confirm", "Confirm return to CAN"), Widget.Button("teaching.return.cancel", "Cancel")) with { Visible = teachingReturnConfirmation },
        ResetAssistedControls(),
        Widget.Label("teaching.help.text", "Single-click the physical teach button: solid green records hand-guided motion; single-click again ends it. Double-click starts the robot's saved playback (flashing green). Starting a new onboard recording replaces its previous trajectory; the robot stores up to three minutes. To return to CAN, first bring the supported arm to its physical zero pose while hand guiding, then turn the light off. The plugin detects the mode and keeps the measured model moving. It never starts onboard playback or writes encoder-zero calibration.") with { Visible = teachingHelpOpen, Height = 118 });

    private async Task TeachingEvent(string action, CancellationToken cancellation)
    {
        if (action.StartsWith("reset.", StringComparison.Ordinal)) { await ResetAssistedEvent(action[6..], cancellation); return; }
        if (action == "help") { teachingHelpOpen = !teachingHelpOpen; await Render(cancellation); return; }
        if (action == "return.cancel") { teachingReturnConfirmation = false; await Render(cancellation); return; }
        if (!TeachingState.Inspect(Device()).CanReturn)
            throw new InvalidOperationException("Finish hand guiding at the physical zero pose, turn the teach light off, and wait for the live zero/stationary checks. No mode change was sent.");
        if (action == "return") { teachingReturnConfirmation = true; await Render(cancellation); return; }
        if (action == "return.confirm")
        {
            teachingReturnConfirmation = false;
            await Command("teach-return", new() { ["button_off"] = true, ["zero_pose_confirmed"] = true }, cancellation, teachingAcknowledged: true);
            await Render(cancellation);
        }
    }

    private void AddTeachingPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        patches["teaching.label"] = new() { ["text"] = teaching.Label };
        patches["teaching.next"] = new() { ["text"] = teaching.NextStep, ["visible"] = teaching.BlocksTargets || teachingHelpOpen };
        string zero = device?["joints"] is JsonArray joints ? string.Join(" · ", joints.Select((j, i) => $"J{i + 1} {j?["actual_deg"]?.GetValue<double>().ToString("F2") ?? "—"}°")) : "Waiting for six fresh joint measurements";
        patches["teaching.zero"] = new() { ["visible"] = teaching.ReturnRequired, ["text"] = zero + " · return guard ±1° (not encoder calibration)" };
        patches["teaching.return"] = new() { ["visible"] = teaching.ReturnRequired, ["enabled"] = teaching.CanReturn };
        patches["teaching.return.confirmation"] = new() { ["visible"] = teachingReturnConfirmation && teaching.CanReturn };
        patches["enable"] = new() { ["enabled"] = device is not null && !switchingProfile && !teaching.BlocksTargets };
    }

    private Widget AlignmentControls() => Widget.Column("alignment",
        Widget.Button("alignment.toggle", "Guided desk alignment check"),
        Widget.Column("alignment.form",
            Widget.Label("alignment.instructions", "Place a distinct object on the measured desk cross, and the corresponding object at the same scene reference. Enter its height above the desk. Define the exact contact/tool point (for example the model's grasp_site), not a camera origin or an unspecified fingertip. Link the robot, single-click its physical teach button and move that point to the object's top. The scene should follow. Capture while stationary, then confirm both views. Repeat with different approach poses. Finish by hand-guiding to zero and turning the teach light off; this wizard sends no homing or playback.") with { Height = 122 },
            new Widget("input", "alignment.name") { Text = AlignmentOptions.Name, Hint = "Physical reference name" },
            new Widget("input", "alignment.tool") { Text = AlignmentOptions.ToolPoint, Hint = "The exact named model TCP/contact point; it is recorded, not inferred" },
            Widget.Row("alignment.reference", Widget.Label("alignment.x.label", "Scene X m"), Widget.Slider("alignment.x", AlignmentOptions.X, -20, 20, .001),
                Widget.Label("alignment.y.label", "Desk Y m"), Widget.Slider("alignment.y", AlignmentOptions.DeskY, -20, 20, .001),
                Widget.Label("alignment.z.label", "Scene Z m"), Widget.Slider("alignment.z", AlignmentOptions.Z, -20, 20, .001)),
            Widget.Row("alignment.height.row", Widget.Label("alignment.height.label", "Measured object height mm (0 = bare desk)"), Widget.Slider("alignment.height", AlignmentOptions.ObjectHeightMm, 0, 1000, 1)),
            Widget.Row("alignment.actions", Widget.Button("alignment.start", "Start local check / trace"), Widget.Button("alignment.capture", "Capture stationary pose"), Widget.Button("alignment.finish", "Finish & save report")),
            Widget.Label("alignment.status", alignmentStatus) with { Height = 68 },
            Widget.Row("alignment.confirmation", Widget.Label("alignment.confirm.text", "Captured. Is the defined physical tool point at the reference, and does the simulated point visibly agree?"),
                Widget.Button("alignment.match", "Both look aligned"), Widget.Button("alignment.mismatch", "Mismatch")) with { Visible = alignmentPending is not null },
            Widget.Row("alignment.gap.row", Widget.Label("alignment.gap.label", "Estimated gap mm (0 = unmeasured)"), Widget.Slider("alignment.gap", alignmentGapMm, 0, 1000, 1)),
            Widget.Button("alignment.open", "Open saved report"),
            Widget.Label("alignment.limits", "This records joint agreement and operator visual observations. It does not solve base/tool/camera transforms, independently measure TCP position, validate contact force or synchronize camera exposures. Metric calibration remains unverified; returning to zero alone does not make a dataset calibrated.") with { Height = 68 }) with { Visible = alignmentOpen });

    private async Task AlignmentEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (action == "toggle") { alignmentOpen = !alignmentOpen; await Render(cancellation); return; }
        if (action is "name" or "tool" or "x" or "y" or "z" or "height")
        {
            if (alignmentCheck is not null) throw new InvalidOperationException("Finish the current check before changing its reference.");
            var reference = action switch {
                "name" => AlignmentOptions with { Name = value.GetString() ?? "" }, "tool" => AlignmentOptions with { ToolPoint = value.GetString() ?? "" },
                "x" => AlignmentOptions with { X = value.GetDouble() }, "y" => AlignmentOptions with { DeskY = value.GetDouble() },
                "z" => AlignmentOptions with { Z = value.GetDouble() }, _ => AlignmentOptions with { ObjectHeightMm = value.GetDouble() } };
            AlignmentCheck.ValidateReference(reference);
            preferences = preferences with { Robots = preferences.Robots!.Select(p => p.Id == ActiveProfile.Id ? p with { Alignment = reference } : p).ToArray() };
            await Save(cancellation); return;
        }
        if (action == "gap") { alignmentGapMm = Math.Clamp(value.GetDouble(), 0, 1000); return; }
        if (action == "start")
        {
            if (alignmentCheck is not null) throw new InvalidOperationException("Finish the current local check first.");
            AlignmentCheck.ValidateReference(AlignmentOptions);
            var device = Device(); var linked = link ?? throw new InvalidOperationException("Link the scene robot first. Linking reads measurements and does not move hardware.");
            var profiles = new JsonObject { ["benchDevice"] = CameraSettings("bench").Device,
                ["gripperDevice"] = CameraSettings("gripper").Device, ["depthSerial"] = preferences.DepthSerial,
                ["profiles"] = new JsonObject(cameraCalibration.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value.DeepClone()))) };
            alignmentCheck = new(ActiveProfile.Id, linked.DeviceId, linked.Generation, linked.SceneId, linked.EntityId, AlignmentOptions,
                device["firmware"]?.GetValue<string>() ?? "unavailable", profiles);
            string directory = Path.Combine(engine.Start.SettingsDirectory, "alignment-checks", ActiveProfile.Id);
            Directory.CreateDirectory(directory);
            alignmentPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
            alignmentPending = null;
            alignmentStatus = "Local check started. Single-click the physical teach button, guide the named tool point to the reference, and hold still before Capture. Onboard playback is separate and is not started.";
            await PersistAlignment(cancellation); await Render(cancellation); return;
        }
        if (action == "open")
        {
            if (alignmentPath is null || !File.Exists(alignmentPath)) throw new InvalidOperationException("Start and save a local check first.");
            Process.Start(new ProcessStartInfo(alignmentPath) { UseShellExecute = true }); return;
        }
        if (action == "finish") { await FinishAlignment("Operator finished", cancellation); await Render(cancellation); return; }
        var session = alignmentCheck ?? throw new InvalidOperationException("Start a local alignment check first.");
        if (action == "capture")
        {
            var device = Device();
            if (!TeachingState.Inspect(device).HandGuiding) throw new InvalidOperationException("Capture this observation while hand guiding with the physical teach light solid green.");
            if (device["status"]?["stationary_s"]?.GetValue<double>() is not >= .4) throw new InvalidOperationException("Hold the tool point still for at least 0.4 seconds before capturing.");
            alignmentPending = ReadAlignmentFrame(session, device);
            alignmentStatus = "Pose captured; confirm the physical tool point and simulated point against the reference. A zero joint-error number alone is not independent spatial evidence.";
            await Render(cancellation); return;
        }
        if (action is "match" or "mismatch")
        {
            var captured = alignmentPending ?? throw new InvalidOperationException("Capture a fresh stationary pose first.");
            var currentDevice = Device();
            if (!TeachingState.Inspect(currentDevice).HandGuiding || currentDevice["joints"]!.AsArray()
                .Select((j, i) => Math.Abs(j!["actual_deg"]!.GetValue<double>() - captured.MeasuredDegrees[i])).Any(error => error > .2))
                throw new InvalidOperationException("The arm moved or left hand guiding after capture. Hold the current reference pose and capture again before confirming it.");
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - captured.HostUnixMilliseconds > 30000)
                throw new InvalidOperationException("That observation is over 30 seconds old. Capture the current stationary pose again.");
            session.Observe(captured, action == "match", alignmentGapMm > 0 ? alignmentGapMm : null);
            alignmentPending = null;
            alignmentStatus = $"Saved {session.Observations} operator observations and {session.Samples} local hand-guided frames. Use a different approach pose for another check. Metric calibration and camera timing remain unverified.";
            await PersistAlignment(cancellation); await Render(cancellation);
        }
    }

    private AlignmentFrame ReadAlignmentFrame(AlignmentCheck session, JsonObject device)
    {
        if (link is null || acknowledgedScenePose is null) throw new InvalidOperationException("Wait for a linked scene acknowledgement.");
        return session.Read(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ActiveProfile.Id, device, link, acknowledgedScenePose, acknowledgedSceneAt);
    }

    private void SampleAlignment(JsonObject device)
    {
        if (alignmentCheck is not { } session || !teaching.HandGuiding) return;
        try { session.Sample(ReadAlignmentFrame(session, device)); }
        catch (InvalidOperationException) { /* Incomplete or delayed observations are omitted, never fabricated. */ }
    }
    private async Task WriteAlignmentReport(AlignmentReport report, CancellationToken cancellation)
    {
        if (alignmentPath is null) return;
        string temporary = alignmentPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, PluginProtocol.Json), cancellation);
        File.Move(temporary, alignmentPath, true);
    }
    private Task PersistAlignment(CancellationToken cancellation) => alignmentCheck is null ? Task.CompletedTask : WriteAlignmentReport(alignmentCheck.Snapshot(), cancellation);
    private async Task FinishAlignment(string reason, CancellationToken cancellation)
    {
        if (alignmentCheck is not { } session) return;
        await WriteAlignmentReport(session.Finish(reason), cancellation);
        alignmentStatus = $"Saved {session.Observations} visual observations and {session.Samples} local frames. Metric calibration remains unverified. {reason}.";
        alignmentCheck = null; alignmentPending = null;
    }
    private void AddAlignmentPatches(Dictionary<string, JsonObject> patches)
    {
        patches["alignment.status"] = new() { ["text"] = alignmentCheck is { } current ? alignmentStatus + $" Local trace: {current.Samples}/1800 frames; {current.Observations}/24 visual observations." : alignmentStatus };
        patches["alignment.capture"] = new() { ["enabled"] = alignmentCheck is not null && teaching.HandGuiding && link is not null };
        patches["alignment.start"] = new() { ["enabled"] = alignmentCheck is null && link is not null };
        patches["alignment.finish"] = new() { ["enabled"] = alignmentCheck is not null };
        patches["alignment.open"] = new() { ["enabled"] = alignmentPath is not null };
        foreach (string field in new[] { "name", "tool", "x", "y", "z", "height" })
            patches["alignment." + field] = new() { ["enabled"] = alignmentCheck is null };
    }
}
