using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public static class EpisodeReplayAvailability
{
    public const string Reason = "Physical playback is unavailable: verified Pika opening-speed limits and a coordinated replay session are not implemented.";
}

/// <summary>Uses the controller's connection-scoped reported limits, never motion defaults.</summary>
public static class EpisodeReplayReportedLimits
{
    public static IReadOnlyList<EpisodeReplayJointLimit> Read(JsonArray joints)
    {
        if (joints.Count != 6)
            throw new InvalidOperationException("Waiting for all six fresh reported joint limits.");
        var limits = new EpisodeReplayJointLimit[6];
        for (int i = 0; i < limits.Length; i++)
        {
            var joint = joints[i];
            if (joint?["limits_fresh"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"Waiting for J{i + 1}'s fresh reported range and speed.");
            if (joint?["acceleration_fresh"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"Waiting for J{i + 1}'s fresh reported acceleration limit; no fallback acceleration is assumed.");
            double minimum = Number(joint, "min_deg"), maximum = Number(joint, "max_deg"),
                speed = Number(joint, "max_speed_rad_s") * 180 / Math.PI,
                acceleration = Number(joint, "max_acceleration_rad_s2") * 180 / Math.PI;
            if (minimum >= maximum || speed <= 0 || acceleration <= 0 || !double.IsFinite(speed) || !double.IsFinite(acceleration))
                throw new InvalidOperationException($"J{i + 1} reported range, speed or acceleration is invalid.");
            limits[i] = new(i + 1, minimum, maximum, speed, acceleration, true);
        }
        return Array.AsReadOnly(limits);
    }

    internal static double Number(JsonNode? node, string key) => node?[key] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number)
        ? number : throw new InvalidOperationException("Missing finite replay evidence: " + key);
}

/// <summary>The controller connection a replay check or placement belongs to.</summary>
internal sealed record ReplayCheckScope(string Token, string Device, long Generation);

/// <summary>Replay review machine. No review (null) is Unloaded.
/// <code>
///   Unloaded --load--> Loaded --recheck/confirm--> Rechecked(scope) | PlacementConfirmed(scope)
///   Rechecked(scope) --confirm--> PlacementConfirmed(current scope)
///   PlacementConfirmed(scope) --recheck on another scope / profile or scope change--> Rechecked
///   any --recheck failure--> Loaded;  any --path change--> Unloaded
/// </code>
/// Loaded has no scope; Rechecked and PlacementConfirmed always carry one.</summary>
internal sealed record ReplayReview(EpisodeReplayArtifact Artifact, string Profile, ReplayCheckScope? Scope = null, bool TubeConfirmed = false)
{
    public ReplayReview Loaded() => this with { Scope = null, TubeConfirmed = false };
    public ReplayReview Rechecked(ReplayCheckScope scope) => this with { Scope = scope, TubeConfirmed = TubeConfirmed && Scope == scope };
    public ReplayReview Confirmed(ReplayCheckScope scope) => this with { Scope = scope, TubeConfirmed = true };

    /// <summary>A placement belongs to one profile and controller connection.</summary>
    public ReplayReview Observe(string profile, ReplayCheckScope? current) =>
        TubeConfirmed && (profile != Profile || current != Scope) ? this with { TubeConfirmed = false } : this;
}

/// <summary>Owns the Plugin-I state. Part of <see cref="WorkspaceComponent"/>.
/// <code>
///   field              written by                                      readers see
///   review             the UI actor (Unload, Load, SetPath, Advance)    one volatile reference to an
///                      and the telemetry loop (Advance only)            immutable ReplayReview
///   path, status,      the UI actor                                    volatile strings
///   timing status
/// </code>
/// The review has two writers on two threads: the UI actor loads, confirms and
/// rechecks; the telemetry loop withdraws a placement whose controller
/// connection or profile changed. Every change that depends on the present
/// value therefore goes through <see cref="Advance"/>, a compare-and-swap that
/// re-applies the transition when the other writer got in first. So the
/// telemetry loop cannot bring back a review the operator just unloaded, and a
/// finishing recheck cannot restore a placement that telemetry withdrew.
/// The UI actor is whoever holds <c>PipersPlugin.commands</c>.</summary>
internal sealed class ReplayReviewSession
{
    private ReplayReview? review;
    private volatile string path = "", status = "Load a finished simulation episode to review its physical start.",
        timingStatus = "Recheck the arm's fresh reported speed and acceleration limits. No fallback acceleration is used.";

    /// <summary>The loaded review; null is Unloaded.</summary>
    public ReplayReview? Review => Volatile.Read(ref review);
    public string Path => path;
    public string Status => status;
    public string TimingStatus => timingStatus;

    /// <summary>The operator typed another file: whatever was loaded no longer describes it.</summary>
    public void SetPath(string next)
    {
        path = next;
        Unload();
    }

    public void Unload() => Volatile.Write(ref review, null);

    /// <summary>A freshly read episode replaces whatever was there.</summary>
    public void Load(EpisodeReplayArtifact artifact, string profile) => Volatile.Write(ref review, new(artifact, profile));

    /// <summary>Applies a pure transition to the loaded review and returns the
    /// result, or null when nothing is loaded. The transition may run more than once.</summary>
    public ReplayReview? Advance(Func<ReplayReview, ReplayReview> transition)
    {
        while (true)
        {
            var seen = Volatile.Read(ref review);
            if (seen is null) return null;
            var next = transition(seen);
            if (ReferenceEquals(Interlocked.CompareExchange(ref review, next, seen), seen)) return next;
        }
    }

    public void Report(string text) => status = text;
    public void ReportTiming(string text) => timingStatus = text;
}

public sealed partial class PipersPlugin
{
    private string replayPath => workspace.Replay.Path;
    private string replayStatus => workspace.Replay.Status;
    private ReplayReview? replayReview => workspace.Replay.Review;
    private string replayTimingStatus => workspace.Replay.TimingStatus;

    private Widget ReplayPage()
    {
        var items = new List<Widget>
        {
            Widget.Row("replay.heading", Widget.Label("replay.title", "Physical replay · review") with { Appearance = "heading" },
                Widget.Button("workspace.recording@replay", "Back to recording") with { Width = 160 }),
            Widget.Label("replay.help", "Review an exported simulation trajectory before running it on the real arm. This page sends no motion, power or recovery commands.") with { Height = 44 },
            Field("replay.path", "Finished episode JSON file", new Widget("input", "replay.path") { Text = replayPath, Hint = "Paste the full path to the finished .json file, not its .start.json manifest." }),
            Widget.Row("replay.load.row", Widget.Button("replay.load", "Load trajectory") with { Width = 150 },
                Widget.Label("replay.load.status", replayStatus) with { Height = 56, Hint = replayStatus })
        };
        if (replayReview is { Artifact: var artifact } review)
        {
            var start = artifact.TubeStart;
            items.Add(Widget.Label("replay.command.schema", artifact.HasPositionCommands
                ? "Version 2 · position-controller commands and measured observations are separate. Command samples are held until the next recorded command."
                : "Version 1 · measured observations only. This file is review-only and cannot be planned as physical actuator replay.") with { Height = 46, Appearance = "muted" });
            items.Add(Widget.Label("replay.episode", $"Episode {artifact.Audit.EpisodeOrdinal + 1} · {artifact.Samples.Count} measured samples · {artifact.Samples[^1].TimeSeconds:F2} s · outcome: {artifact.Audit.Outcome}") with
                { Hint = $"Artifact {artifact.ArtifactId}; seed {artifact.Audit.Seed}. Outcome is operator-confirmed; native recording has independent integrity checks." });
            items.Add(Widget.Column("replay.placement",
                Widget.Label("replay.placement.title", "1. Place the real tube at the saved start") with { Appearance = "heading" },
                Widget.Label("replay.placement.measurements", $"LEFT {start.LeftMm:F1} mm · BACK {start.BackMm:F1} mm · yaw {start.YawDeg:F1}°") with { Appearance = "numeric", Height = 32 },
                Widget.Label("replay.placement.object", $"Upright tube: {artifact.TubeDimensions.HeightMm:F1} mm high × {artifact.TubeDimensions.DiameterMm:F1} mm diameter. Goal: LEFT {artifact.Audit.Goal.LeftMm:F1} / BACK {artifact.Audit.Goal.BackMm:F1} mm.") with { Height = 44 },
                Widget.Button("replay.placement.confirm", review.TubeConfirmed ? "Tube placement confirmed" : "Tube is at this start") with { Width = 225 },
                Info("replay.placement.info", "Use the same fixed LEFT and BACK edges as desk calibration. Coordinates refer to the tube centre. This is your placement confirmation; the camera does not verify it.")) with { Padding = 12, Spacing = 10, Appearance = "panel" });
            items.Add(Widget.Label("replay.pose.title", "2. Compare the first pose with the real arm") with { Appearance = "heading" });
            items.Add(Widget.Row("replay.pose.headings", Widget.Label("replay.axis.heading", "Axis") with { Width = 60 },
                Widget.Label("replay.target.heading", "Start measured") with { Width = 130 }, Widget.Label("replay.command.heading", "First command") with { Width = 130 },
                Widget.Label("replay.actual.heading", "Real measurement") with { Width = 145 },
                Widget.Label("replay.difference.heading", "Difference") with { Width = 130 }));
            for (int i = 0; i < 6; i++)
                items.Add(Widget.Row("replay.joint." + i, Widget.Label($"replay.joint.{i}.name", "J" + (i + 1)) with { Width = 60 },
                    Widget.Label($"replay.joint.{i}.target", $"{artifact.Samples[0].JointDegrees[i]:F3}°") with { Width = 130, Appearance = "numeric" },
                    Widget.Label($"replay.joint.{i}.command", artifact.HasPositionCommands ? $"{artifact.Samples[0].CommandedJointDegrees![i]:F3}°" : "Not recorded") with { Width = 130, Appearance = "numeric" },
                    Widget.Label($"replay.joint.{i}.actual", "—") with { Width = 145, Appearance = "numeric" },
                    Widget.Label($"replay.joint.{i}.difference", "—") with { Width = 130, Appearance = "numeric" }));
            items.Add(Widget.Row("replay.gripper", Widget.Label("replay.gripper.name", "Pika") with { Width = 60 },
                Widget.Label("replay.gripper.target", $"{artifact.Samples[0].GripperOpeningMm:F2} mm") with { Width = 130, Appearance = "numeric" },
                Widget.Label("replay.gripper.command", artifact.HasPositionCommands ? $"{artifact.Samples[0].CommandedGripperOpeningMm:F2} mm" : "Not recorded") with { Width = 130, Appearance = "numeric" },
                Widget.Label("replay.gripper.actual", "—") with { Width = 145, Appearance = "numeric" },
                Widget.Label("replay.gripper.difference", "—") with { Width = 130, Appearance = "numeric" }));
            items.Add(Widget.Row("replay.check.actions", Widget.Button("replay.recheck", "Recheck pose & limits") with { Width = 200 },
                Widget.Button("workspace.controls@replay", "Open robot controls") with { Width = 180 }));
            items.Add(Widget.Label("replay.timing", replayTimingStatus) with { Height = 72, Hint = replayTimingStatus });
            items.Add(Widget.Label("replay.pose.note", "No approach or zero-position move is generated. Align the first pose separately using robot controls, then return here to recheck.") with { Height = 44, Appearance = "muted" });
        }
        items.Add(Widget.Button("replay.start", "Start physical replay · unavailable") with { Enabled = false, Width = 300, Hint = EpisodeReplayAvailability.Reason });
        items.Add(Widget.Label("replay.unavailable", EpisodeReplayAvailability.Reason) with { Height = 76, Appearance = "muted" });
        return Widget.Column("workspace.replay.body", items.ToArray()) with { Padding = 14, Spacing = 12 };
    }

    private async Task ReplayEvent(string action, JsonElement value, CancellationToken cancellation)
    {
        if (Navigation.Page != WorkspacePage.Replay)
            throw new InvalidOperationException("Open Physical replay review before editing its preparation.");
        if (action == "path")
        {
            string path = value.GetString() ?? "";
            if (path.Length > 4096 || path.Any(char.IsControl))
                throw new InvalidOperationException("Enter a valid bounded local JSON path.");
            workspace.Replay.SetPath(path);
            workspace.Replay.ReportTiming("Load and recheck fresh reported speed and acceleration limits.");
            workspace.Replay.Report("Path changed; load the trajectory before reviewing it.");
        }
        else if (action == "load")
        {
            workspace.Replay.Unload();
            workspace.Replay.ReportTiming("Recheck fresh reported speed and acceleration limits; no timing changes have been applied.");
            try
            {
                var artifact = await ReadReplayFile(replayPath, cancellation);
                workspace.Replay.Load(artifact, ActiveProfile.Id);
                workspace.Replay.Report(artifact.HasPositionCommands
                    ? "Version 2 position commands loaded. Place the real tube at the saved start, then recheck the measured start pose."
                    : "Version 1 observations loaded for review only; physical replay requires a version 2 command export.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                workspace.Replay.Report("Could not load trajectory: " + error.Message);
            }
        }
        else if (action == "placement.confirm")
        {
            if (replayReview?.Profile != ActiveProfile.Id)
                throw new InvalidOperationException("Load the episode for this robot profile before confirming tube placement.");
            var device = Device();
            var scope = new ReplayCheckScope(token, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>());
            workspace.Replay.Advance(loaded => loaded.Confirmed(scope));
            workspace.Replay.Report("Tube placement confirmed by you. Recheck the real arm against the first recorded pose.");
        }
        else if (action == "recheck")
        {
            try
            {
                await RecheckReplay(cancellation);
            }
            catch (Exception error) when (error is InvalidOperationException or PluginRequestException)
            {
                workspace.Replay.Advance(loaded => loaded.Loaded());
                workspace.Replay.Report("Recheck incomplete: " + error.Message);
            }
        }
        else if (action == "start")
            throw new InvalidOperationException(EpisodeReplayAvailability.Reason);
        else
            throw new InvalidOperationException("Unknown replay preparation action.");
        await Render(cancellation);
    }

    private async Task RecheckReplay(CancellationToken cancellation)
    {
        workspace.Replay.ReportTiming("Waiting for fresh reported speed and acceleration limits; no timing changes have been applied.");
        // A review loaded for another robot starts over for this one.
        string profile = ActiveProfile.Id;
        var review = workspace.Replay.Advance(loaded => loaded.Profile == profile ? loaded : (loaded with { Profile = profile }).Loaded())
            ?? throw new InvalidOperationException("Load a finished simulation episode first.");
        var artifact = review.Artifact;
        string expectedToken = token;
        var initialDevice = Device(expectedToken: expectedToken);
        string deviceId = initialDevice["id"]!.GetValue<string>();
        long generation = initialDevice["generation"]!.GetValue<long>();
        var discovered = JsonNode.Parse((await engine.RequestAsync("scene.robots", cancellation: cancellation)).GetRawText())!.AsObject();
        string sceneId = discovered["sceneId"]?.GetValue<string>() ?? throw new InvalidOperationException("Current Edit scene is unavailable; leave Play mode and refresh the scene robot selection.");
        var pose = JsonNode.Parse((await engine.RequestAsync("robot.read", new JsonObject { ["sceneId"] = sceneId, ["entityId"] = preferences.RobotId }, cancellation)).GetRawText())!.AsObject();
        var device = Device(deviceId, generation, expectedToken);
        _ = new RobotLink(sceneId, preferences.RobotId, pose["joints"] as JsonArray ?? throw new InvalidOperationException("The selected Edit robot has no joint model."), device);
        var joints = device["joints"]!.AsArray();
        var limits = EpisodeReplayReportedLimits.Read(joints);
        var timing = artifact.HasPositionCommands ? EpisodeReplayPlanner.AssessJointTiming(artifact, limits) : null;
        double requiredScale = timing?.RequiredTimeScale ?? 1;
        workspace.Replay.ReportTiming(timing is null ? "Version 1 has no position commands; physical timing cannot be assessed."
            : $"Last joint timing check: speed requires {timing.SpeedTimeScale:F3}×; sampled acceleration requires {timing.AccelerationTimeScale:F3}×. " +
              (requiredScale > 1 ? $"Replay needs at least {requiredScale:F3}× slower timing. No scaling applied. " : "Command intervals fit fresh reported joint caps. ") +
              "Gripper timing and instantaneous controller response are not certified; playback remains disabled.");
        bool poseMatches = true;
        bool firstCommandMatches = true;
        for (int i = 0; i < 6; i++)
        {
            double actual = EpisodeReplayReportedLimits.Number(joints[i], "actual_deg");
            poseMatches &= Math.Abs(actual - artifact.Samples[0].JointDegrees[i]) <= .5;
            if (artifact.HasPositionCommands)
                firstCommandMatches &= Math.Abs(artifact.Samples[0].CommandedJointDegrees![i] - artifact.Samples[0].JointDegrees[i]) <= .5;
        }
        // Version 2 commands were range-checked by AssessJointTiming; version 1 has only measurements.
        if (!artifact.HasPositionCommands) EpisodeReplayPlanner.ValidateJointRanges(artifact.Samples.Select(sample => sample.JointDegrees).ToArray(), limits);
        if (!PikaMatchesProfile() || !PikaDisplayOpening(state?["pika"] as JsonObject, out double opening))
            throw new InvalidOperationException("Fresh measured Pika opening for this robot is required to compare the first pose.");
        if (artifact.Samples.Any(sample => (artifact.HasPositionCommands ? sample.CommandedGripperOpeningMm!.Value : sample.GripperOpeningMm) > PikaProtocol.MaximumOpeningMm))
            throw new InvalidOperationException($"A simulated gripper sample exceeds the physical Pika {PikaProtocol.MaximumOpeningMm} mm opening limit; no clipping is available.");
        poseMatches &= Math.Abs(opening - artifact.Samples[0].GripperOpeningMm) <= 1;
        if (artifact.HasPositionCommands)
            firstCommandMatches &= Math.Abs(artifact.Samples[0].CommandedGripperOpeningMm!.Value - artifact.Samples[0].GripperOpeningMm) <= 1;
        var checkedScope = new ReplayCheckScope(expectedToken, deviceId, generation);
        workspace.Replay.Advance(loaded => loaded.Rechecked(checkedScope));
        workspace.Replay.Report("Last recheck: Edit context and reported joint ranges checked. " + (poseMatches ? "First pose matched within 0.5° / 1 mm. " : "First pose differed; align it separately. ") +
            (!artifact.HasPositionCommands ? "Version 1 is observations-only and cannot be replayed as actions. " : requiredScale > 1 ? $"Command speed/acceleration requires at least {requiredScale:F3}× slower playback; no scaling applied. " : "Command interval speed/acceleration fits reported joint limits. ") +
            (!firstCommandMatches ? "The first command differs from the measured source start; physical planning is blocked at default tolerances. " : "") +
            "Physical playback remains unavailable; no commands sent.");
    }

    /// <summary>A confirmed tube placement belongs to the profile and controller
    /// connection it was confirmed on. Called from telemetry maintenance, never render.</summary>
    private void ObserveReplayScope(TelemetryReceipt? receipt)
    {
        if (replayReview is not { TubeConfirmed: true }) return;
        ReplayCheckScope? current = null;
        if (receipt is not null)
            try
            {
                var device = Device(sourceReceipt: receipt);
                current = new(token, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>());
            }
            catch (InvalidOperationException) { }
        string profile = ActiveProfile.Id;
        workspace.Replay.Advance(loaded => loaded.Observe(profile, current));
    }

    private void AddReplayPatches(Dictionary<string, JsonObject> patches, JsonObject? device)
    {
        if (Navigation.Page != WorkspacePage.Replay || replayReview is not { Artifact: var artifact } review)
            return;
        patches["replay.placement.confirm"] = new() { ["text"] = review.TubeConfirmed ? "Tube placement confirmed" : "Tube is at this start" };
        bool fresh = device is not null && telemetry.Latest is { } receipt && receipt.BackendToken == token &&
            receipt.HasFreshDevice(device, System.Diagnostics.Stopwatch.GetTimestamp());
        for (int i = 0; i < 6; i++)
        {
            bool valid = fresh && device?["joints"] is JsonArray { Count: 6 } joints && joints[i]?["actual_deg"] is JsonValue number &&
                number.TryGetValue<double>(out double value) && double.IsFinite(value);
            double actual = valid ? device!["joints"]![i]!["actual_deg"]!.GetValue<double>() : double.NaN;
            patches[$"replay.joint.{i}.actual"] = new() { ["text"] = valid ? $"{actual:F3}°" : "—" };
            patches[$"replay.joint.{i}.difference"] = new() { ["text"] = valid ? $"{actual - artifact.Samples[0].JointDegrees[i]:+0.000;-0.000;0.000}°" : "—" };
        }
        bool grip = fresh && PikaMatchesProfile() && PikaDisplayOpening(state?["pika"] as JsonObject, out _);
        double actualOpening = grip && PikaDisplayOpening(state?["pika"] as JsonObject, out double measured) ? measured : double.NaN;
        patches["replay.gripper.actual"] = new() { ["text"] = grip ? $"{actualOpening:F2} mm" : "—" };
        patches["replay.gripper.difference"] = new() { ["text"] = grip ? $"{actualOpening - artifact.Samples[0].GripperOpeningMm:+0.00;-0.00;0.00} mm" : "—" };
    }

    public static async Task<EpisodeReplayArtifact> ReadReplayFile(string path, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Paste the finished episode JSON file path.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > EpisodeReplayReader.MaximumBytes)
            throw new InvalidOperationException("Trajectory exceeds the 16 MiB file limit.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await file.ReadAsync(buffer, cancellation);
            if (count == 0)
                break;
            if (bytes.Length + count > EpisodeReplayReader.MaximumBytes)
                throw new InvalidOperationException("Trajectory grew beyond its bounded input size.");
            bytes.Write(buffer, 0, count);
        }
        return EpisodeReplayReader.Parse(bytes.ToArray());
    }
}
