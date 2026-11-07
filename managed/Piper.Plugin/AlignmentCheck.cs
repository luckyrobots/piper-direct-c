using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record AlignmentReference(string Name = "Desk cross / reference object", double X = 0, double DeskY = 0, double Z = 0,
    double ObjectHeightMm = 0, string ToolPoint = "grasp_site");
public sealed record AlignmentFrame(long HostUnixMilliseconds, double[] MeasuredDegrees, double[] SceneDegrees, double MaximumJointErrorDegrees);
public sealed record AlignmentObservation(AlignmentFrame Frame, string ReferenceName, double[] ReferenceTopMetres, string ToolPoint,
    bool OperatorSaysAligned, double? EstimatedGapMm);
public sealed record AlignmentReport(int Schema, string ProfileId, string DeviceId, long Generation, string SceneId, string RobotId,
    string Firmware, string[] JointNames, string JointUnit, string ClockDomain, JsonObject CameraProfiles,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Reason, AlignmentReference Reference,
    AlignmentFrame[] Trace, AlignmentObservation[] Observations, bool MetricCalibrationValid = false, bool ExposureSynchronized = false,
    string Evidence = "Joint agreement and operator visual observations only. No independent tool/base/camera transform was solved.");

/// <summary>Bounded read-only observations; this class has no robot command path.</summary>
public sealed class AlignmentCheck(string profileId, string deviceId, long generation, string sceneId, string robotId, AlignmentReference reference,
    string firmware = "unavailable", JsonObject? cameraProfiles = null)
{
    private readonly object gate = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly List<AlignmentFrame> trace = [];
    private readonly List<AlignmentObservation> observations = [];
    private DateTimeOffset? finished;
    private string reason = "In progress";
    public string ProfileId => profileId;
    public int Samples { get { lock (gate) return trace.Count; } }
    public int Observations { get { lock (gate) return observations.Count; } }

    public static void ValidateReference(AlignmentReference value)
    {
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 160 || value.Name.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.ToolPoint) || value.ToolPoint.Length > 80 || value.ToolPoint.Any(char.IsControl) ||
            !double.IsFinite(value.X) || !double.IsFinite(value.DeskY) || !double.IsFinite(value.Z) ||
            Math.Abs(value.X) > 20 || Math.Abs(value.DeskY) > 20 || Math.Abs(value.Z) > 20 ||
            !double.IsFinite(value.ObjectHeightMm) || value.ObjectHeightMm is < 0 or > 1000)
            throw new InvalidOperationException("Name the reference and tool point, and enter finite scene coordinates and an object height of 0–1000 mm.");
    }

    public AlignmentFrame Read(long now, string currentProfile, JsonObject device, RobotLink link, JsonObject acknowledgement, long acknowledgedAt)
    {
        if (currentProfile != profileId || device["id"]?.GetValue<string>() != deviceId || device["generation"]?.GetValue<long>() != generation ||
            link.SceneId != sceneId || link.EntityId != robotId || link.DeviceId != deviceId || link.Generation != generation ||
            device["fresh"]?.GetValue<bool>() != true ||
            now < acknowledgedAt || now - acknowledgedAt > 500)
            throw new InvalidOperationException("Fresh measurements and a matching linked scene are required; start a new alignment check after reconnecting or switching robots.");
        if (device["status"]?["error"]?.GetValue<int>() != 0 || device["joints"] is not JsonArray { Count: 6 } measuredJoints ||
            measuredJoints.Any(j => j?["fresh"]?.GetValue<bool>() != true || j["fault"]?.GetValue<int>() != 0))
            throw new InvalidOperationException("Healthy, fresh feedback from all six joints is required for an alignment observation.");
        var pose = acknowledgement["pose"];
        if (pose?["sceneId"]?.GetValue<string>() != sceneId || pose["entityId"]?.GetValue<string>() != robotId || pose["joints"] is not JsonArray sceneJoints)
            throw new InvalidOperationException("The scene acknowledgement belongs to another robot or scene; wait for the current linked pose.");
        // This is an observation, not a new feedback publication. In particular,
        // do not update/reset the independently verified Pika link state here.
        double[] measured = measuredJoints.Select(j => j!["actual_deg"]!.GetValue<double>()).ToArray();
        double[] simulated = link.JointNames.Select(name => sceneJoints.Single(j => j?["name"]?.GetValue<string>() == name)!["position"]!.GetValue<double>() * 180 / Math.PI).ToArray();
        if (measured.Concat(simulated).Any(angle => !double.IsFinite(angle))) throw new InvalidOperationException("Joint observations must be finite.");
        double error = measured.Zip(simulated).Max(pair => Math.Abs(pair.First - pair.Second));
        if (error > .01) throw new InvalidOperationException("The linked scene has not yet acknowledged the measured arm pose; hold still and capture again.");
        return new(now, measured, simulated, error);
    }

    public void Sample(AlignmentFrame frame)
    {
        lock (gate)
        {
            if (finished.HasValue || trace.Count >= 1800 || (trace.Count > 0 && frame.HostUnixMilliseconds - trace[^1].HostUnixMilliseconds < 100)) return;
            trace.Add(frame);
        }
    }

    public void Observe(AlignmentFrame frame, bool aligned, double? gap)
    {
        if (gap.HasValue && (!double.IsFinite(gap.Value) || gap.Value is < 0 or > 1000)) throw new InvalidOperationException("Gap must be 0–1000 mm, or unmeasured.");
        lock (gate)
        {
            if (finished.HasValue) throw new InvalidOperationException("Start a new alignment check before adding observations.");
            if (observations.Count >= 24) throw new InvalidOperationException("Finish this check before capturing more than 24 observations.");
            if (observations.Count > 0 && frame.MeasuredDegrees.Zip(observations[^1].Frame.MeasuredDegrees).Max(p => Math.Abs(p.First - p.Second)) < 2)
                throw new InvalidOperationException("Use a different approach pose (at least one joint changes by 2°) before capturing another observation.");
            observations.Add(new(frame, reference.Name, [reference.X, reference.DeskY + reference.ObjectHeightMm / 1000, reference.Z], reference.ToolPoint, aligned, gap));
        }
    }

    public AlignmentReport Finish(string why)
    {
        lock (gate) { finished ??= DateTimeOffset.UtcNow; reason = why; return Snapshot(); }
    }
    public AlignmentReport Snapshot()
    {
        lock (gate) return new(1, profileId, deviceId, generation, sceneId, robotId, firmware,
            Enumerable.Range(1, 6).Select(i => "joint" + i).ToArray(), "degrees", "host-utc-receipt", (JsonObject)(cameraProfiles?.DeepClone() ?? new JsonObject()),
            started, finished, reason, reference, trace.ToArray(), observations.ToArray());
    }
}
