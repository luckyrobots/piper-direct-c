using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Pika;

/// <summary>The agent's periodic <c>state</c> line on stdout. This one schema is
/// written by the Linux agent and read by <see cref="PikaProcessService"/>; the
/// service passes the raw line through to its snapshot and decides only from
/// these typed fields. Booleans are nullable so that a missing field never reads
/// as a verified <c>false</c>. Diagnostic objects stay untyped pass-through.</summary>
public sealed record PikaAgentState
{
    [JsonPropertyName("type")] public string Type { get; init; } = "state";
    [JsonPropertyName("connected")] public bool? Connected { get; init; }
    [JsonPropertyName("fresh")] public bool? Fresh { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
    [JsonPropertyName("armed")] public bool? Armed { get; init; }
    [JsonPropertyName("pending")] public bool? Pending { get; init; }
    [JsonPropertyName("driver_status")] public int? DriverStatus { get; init; }
    [JsonPropertyName("driver_fault_mask")] public int? DriverFaultMask { get; init; }
    [JsonPropertyName("driver_fault")] public bool? DriverFault { get; init; }
    [JsonPropertyName("driver_status_text")] public string? DriverStatusText { get; init; }
    [JsonPropertyName("driver_fault_evidence")] public object? DriverFaultEvidence { get; init; }
    [JsonPropertyName("following")] public bool? Following { get; init; }
    [JsonPropertyName("follow_session")] public string? FollowSession { get; init; }
    [JsonPropertyName("follow_sequence")] public long? FollowSequence { get; init; }
    [JsonPropertyName("follow_opening_mm")] public double? FollowOpeningMm { get; init; }
    [JsonPropertyName("holding")] public bool? Holding { get; init; }
    [JsonPropertyName("follow_audit")] public object? FollowAudit { get; init; }
    [JsonPropertyName("resuming_hold")] public bool? ResumingHold { get; init; }
    [JsonPropertyName("last_measured_hold_resume")] public object? LastMeasuredHoldResume { get; init; }
    [JsonPropertyName("agent_diagnostics")] public object? AgentDiagnostics { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("telemetry")] public object? Telemetry { get; init; }
    [JsonPropertyName("age_s")] public double? AgeSeconds { get; init; }
    [JsonPropertyName("last_motor_request")] public string? LastMotorRequest { get; init; }
    [JsonPropertyName("request_time_s")] public double? RequestTimeSeconds { get; init; }
    [JsonPropertyName("feedback_time_s")] public double? FeedbackTimeSeconds { get; init; }
}
