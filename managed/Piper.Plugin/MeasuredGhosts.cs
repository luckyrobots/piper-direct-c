using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Visual feedback never owns the leader's physics or sends a hardware target.</summary>
public sealed partial class PipersPlugin
{
    /// <summary>Plugin-H: Unknown unless probed for the current backend identity. A probe
    /// (at most every 5 s, shared by publication and the visibility toggle) records
    /// what the engine host supports; visibility can therefore never be known while
    /// ghost support is unknown. A backend restart (a new token) makes the recorded
    /// capability Unknown again, so it is re-probed.</summary>
    internal sealed record GhostCapabilities(string Backend, bool Ghosts, bool Visibility);
    internal GhostCapabilities? ghostCapabilities;
    private GhostCapabilities? KnownGhostCapabilities => ghostCapabilities is { } known && known.Backend == token ? known : null;
    private long ghostCapabilityAttempt;
    private const double GhostProbeBackoffSeconds = 5;
    internal bool? publishedGhostVisibility;
    private readonly Dictionary<string, PikaClosedDisplayReference?> ghostDisplayReferences = new();

    private double? GhostDisplayReference(RobotProfile profile, JsonObject pika)
    {
        string key = PikaClosedDisplayReference.Key(profile);
        if (profile.Id == ActiveProfile.Id)
        {
            double? angle = PikaDisplayReferenceAngle(pika);
            ghostDisplayReferences[key] = pikaDisplayReference;
            return angle;
        }
        if (!ghostDisplayReferences.TryGetValue(key, out var saved))
            ghostDisplayReferences[key] = saved = PikaClosedDisplayReference.TryLoad(engine.Start.SettingsDirectory, profile);
        return saved?.AngleFor(profile, pika);
    }

    private async Task<GhostCapabilities?> ProbeGhostCapabilities(CancellationToken cancellation)
    {
        if (KnownGhostCapabilities is { } known) return known;
        if (ghostCapabilityAttempt != 0 && Stopwatch.GetElapsedTime(ghostCapabilityAttempt).TotalSeconds < GhostProbeBackoffSeconds) return null;
        ghostCapabilityAttempt = Stopwatch.GetTimestamp();
        string backend = token;
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        discovery.CancelAfter(400);
        var info = await engine.RequestAsync("host.info", cancellation: discovery.Token);
        if (token != backend) return null; // The backend changed during the probe; probe again for the new one.
        return ghostCapabilities = new(backend, info.TryGetProperty("robotFeedbackGhostSupported", out var supported) && supported.GetBoolean(),
            info.TryGetProperty("robotGhostVisibilitySupported", out var visibility) && visibility.GetBoolean());
    }

    private async Task SetMeasuredGhostVisibility(bool visible, CancellationToken cancellation)
    {
        // Unknown and backing off: leave publishedGhostVisibility unset so it is retried.
        if (await ProbeGhostCapabilities(cancellation) is not { } capabilities) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(500);
        if (capabilities.Visibility)
            await engine.RequestAsync("robot.ghost.visibility", new JsonObject { ["visible"] = visible }, timeout.Token);
        // Older hosts expire an unpublished overlay naturally. No visibility
        // preference changes leader physics, motor state or control ownership.
        publishedGhostVisibility = visible;
    }

    private int measuredGhostProfileCursor;

    private bool TryGhostEvidence(RobotProfile profile, string backend, long? generation,
        out JsonObject device, out JsonObject pika)
    {
        device = pika = null!;
        if (!passiveMeasuredTrackingStarted || switchingProfile || !preferences.ShowMeasuredGhosts || token != backend ||
            !preferences.Robots!.Any(current => current.Id == profile.Id && MeasuredTrackingPolicy.Enabled(current) &&
                current.RobotId == profile.RobotId && current.CanDeviceId == profile.CanDeviceId &&
                current.PikaDevice == profile.PikaDevice && current.WslDistribution == profile.WslDistribution &&
                current.RobotJointPrefix == profile.RobotJointPrefix)) return false;
        var latest = telemetry.Latest;
        long now = Stopwatch.GetTimestamp();
        if (latest is null || latest.BackendToken != backend || !latest.IsFresh(now)) return false;
        var arm = latest.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(current => current["id"]?.GetValue<string>() == profile.CanDeviceId);
        var gripper = ProfilePikaRouting.Find(latest.State, profile);
        if (arm is null || gripper is null || !latest.HasFreshDevice(arm, now) ||
            !latest.HasFreshPika(gripper, now, SimulationFollowPolicy.MaximumGripperFeedbackAgeMilliseconds) ||
            generation is not null && arm["generation"]?.GetValue<long>() != generation) return false;
        device = arm; pika = gripper;
        return true;
    }

    private async Task PublishMeasuredGhost(RobotProfile profile, JsonObject model, string source, CancellationToken cancellation)
    {
        string backend = token;
        if (!TryGhostEvidence(profile, backend, null, out var initialDevice, out _)) return;
        var measured = new RobotLink(source, profile.RobotId, model["joints"]!.AsArray(), initialDevice, profile.RobotJointPrefix);
        var scope = new JsonObject { ["sceneId"] = source, ["entityId"] = profile.RobotId,
            ["expectedModelAssetId"] = model["modelAssetId"]!.DeepClone(), ["expectedModelRevision"] = model["modelRevision"]!.DeepClone() };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(400);
        var runtime = JsonNode.Parse((await engine.RequestAsync("robot.runtime.read", scope, timeout.Token)).GetRawText())!.AsObject();
        string mode = runtime["mode"]?.GetValue<string>() ?? "";
        if (mode is not ("edit" or "play" or "pause") || scene?["sceneId"]?.GetValue<string>() != source ||
            !TryGhostEvidence(profile, backend, measured.Generation, out var device, out var pika)) return;
        // Runtime discovery can wait behind camera rendering. Sample each arm's
        // newest same-identity evidence after that wait, never relabel old poses.
        var request = measured.Feedback(device, pika, true, GhostDisplayReference(profile, pika));
        if (request["joints"] is not JsonObject { Count: 8 }) return;
        request["expectedModelAssetId"] = scope["expectedModelAssetId"]!.DeepClone();
        request["expectedModelRevision"] = scope["expectedModelRevision"]!.DeepClone();
        if (mode is "play" or "pause") request["runId"] = runtime["runId"]!.DeepClone();
        request["renderMode"] = "ghost"; request["opacity"] = .5;
        request["visible"] = true; request["ghostId"] = profile.Id;
        await engine.RequestAsync("robot.feedback", request, timeout.Token);
    }

    internal async Task PublishMeasuredGhosts(CancellationToken cancellation)
    {
        if (!passiveMeasuredTrackingStarted || switchingProfile) return;
        try
        {
            var referenceKeys = (preferences.Robots ?? []).Select(PikaClosedDisplayReference.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string obsolete in ghostDisplayReferences.Keys.Where(key => !referenceKeys.Contains(key)).ToArray())
                ghostDisplayReferences.Remove(obsolete);
            if (await ProbeGhostCapabilities(cancellation) is not { } capabilities) return;
            if (publishedGhostVisibility != preferences.ShowMeasuredGhosts)
                await SetMeasuredGhostVisibility(preferences.ShowMeasuredGhosts, cancellation);
            if (!preferences.ShowMeasuredGhosts || !capabilities.Ghosts || scene?["robots"] is not JsonArray robots) return;
            string source = scene["sceneId"]!.GetValue<string>();
            var profiles = (preferences.Robots ?? []).Where(MeasuredTrackingPolicy.Enabled).ToArray();
            if (profiles.Length == 0) return;
            int first = measuredGhostProfileCursor % profiles.Length;
            measuredGhostProfileCursor = (first + 1) % profiles.Length;
            for (int offset = 0; offset < profiles.Length; offset++)
            {
                cancellation.ThrowIfCancellationRequested();
                var profile = profiles[(first + offset) % profiles.Length];
                try
                {
                    var model = robots.OfType<JsonObject>().SingleOrDefault(item => item["entityId"]?.GetValue<string>() == profile.RobotId);
                    if (model is not null) await PublishMeasuredGhost(profile, model, source, cancellation);
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested)
                {
                    // One missing or delayed robot must not suppress another
                    // robot's independent fresh measured overlay.
                    Trace.TraceWarning("{0} measured ghost unavailable: {1}", profile.Name, error.Message);
                }
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            Trace.TraceWarning("Measured ghost unavailable: {0}", error.Message);
        }
    }
}
