using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Pika;

/// <summary>The Host-owned half of one gripper's snapshot: what the session
/// itself knows (assignment, ownership, power evidence, agent phase, evidence
/// age), as opposed to the agent's own <see cref="PikaAgentState"/> line that is
/// passed through beside it. This is the one definition of those fields. A live
/// <see cref="PikaProcessService"/> fills it from its state under its gate; the
/// registry's placeholder for an identity that has no session is the same
/// record left at its defaults, which describe a session that was never opened
/// and so holds no owner, no power request and no evidence.</summary>
internal sealed record PikaSessionFields(
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("distribution")] string? Distribution,
    // Transport cadence is not a measured/verified physical opening rate.
    [property: JsonPropertyName("follow_minimum_command_period_s")] double CommandPeriodSeconds)
{
    public const string Unassigned = "No Pika assigned.";
    public const string Unopened = "Connect to read this gripper's telemetry.";

    [JsonPropertyName("configured")] public bool Configured => !string.IsNullOrWhiteSpace(Device);
    [JsonPropertyName("transport")] public string Transport =>
        Configured ? PikaDistribution.Transport(Distribution) : OperatingSystem.IsWindows() ? "disabled" : "browser";
    [JsonPropertyName("follow_maximum_command_hz")] public double MaximumCommandHz => 1 / CommandPeriodSeconds;
    [JsonPropertyName("verified_maximum_opening_speed_mm_s")] public double? VerifiedMaximumOpeningSpeed => null;
    [JsonPropertyName("physical_replay_available")] public bool PhysicalReplayAvailable => false;
    [JsonPropertyName("measured_hold_resume_supported")] public bool MeasuredHoldResumeSupported => true;

    [JsonPropertyName("owner")] public string? Owner { get; init; }
    [JsonPropertyName("power_requested")] public bool PowerRequested { get; init; }
    [JsonPropertyName("power_uncertain")] public bool PowerUncertain { get; init; }
    /// <summary>A retained power reservation also exists during a healthy enabled
    /// session. Maintenance distinguishes that from an unresolved request by this
    /// private ACK identity and post-request feedback.</summary>
    [JsonPropertyName("post_request_feedback_verified")] public bool PostRequestFeedbackVerified { get; init; }
    [JsonPropertyName("configuration_blocked")] public bool ConfigurationBlocked { get; init; }
    [JsonIgnore] public PikaAgentPhase Phase { get; init; } = PikaAgentPhase.Idle;
    [JsonPropertyName("agent_phase")] public string AgentPhase => Phase.ToString().ToLowerInvariant();
    [JsonPropertyName("connected")] public bool Connected => Phase == PikaAgentPhase.Ready;
    /// <summary>A client may adopt only a privately verified dead, unpowered
    /// observer. The session rechecks this under its mutation lock.</summary>
    [JsonPropertyName("read_only_adoptable")] public bool ReadOnlyAdoptable { get; init; }
    /// <summary>Null when there is no evidence, or none with a valid source age.</summary>
    [JsonPropertyName("age_s")] public double? AgeSeconds { get; init; }
    [JsonPropertyName("fresh")] public bool Fresh { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = Unopened;

    /// <summary>Writes every field, replacing any agent field of the same name:
    /// the agent cannot speak for the session.</summary>
    public void WriteTo(JsonObject snapshot)
    {
        foreach (var field in JsonSerializer.SerializeToNode(this)!.AsObject())
            snapshot[field.Key] = field.Value?.DeepClone();
    }
}
