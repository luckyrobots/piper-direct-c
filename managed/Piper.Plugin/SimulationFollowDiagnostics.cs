using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationFollowFailureScope(string SourceSceneId, string RunId, string EntityId,
    string ModelAssetId, string ModelRevision, string CanDeviceId, long CanGeneration, string PikaDevice, long? BackendProcessId,
    string RobotJointPrefix = "");
public sealed record SimulationFollowFailureRuntime(string Mode, long Sequence, long SimulationTimeNs,
    long CapturedMonotonicNs, double TimeScale, double[] CommandDegrees, double[] MeasuredDegrees,
    double CommandOpeningMm, double MeasuredOpeningMm, double MuJoCoTimeSeconds);
public sealed record SimulationFollowFailureHardware(long TelemetrySequence, double TelemetryAgeMilliseconds,
    string CanDeviceId, long CanGeneration, long MotionRequestId, bool MotionActive, bool RecoveryRequired,
    long AcceptedFollowSequence, bool Following, string ControllerMessage, double[] JointDegrees, double? GripperOpeningMm,
    SimulationFollowFailurePika Pika);
public sealed record SimulationFollowFailurePika(bool AssignmentMatches, bool OwnerMatches, bool LocallyOwned,
    bool Connected, bool FreshFlag, bool Armed, bool Enabled, double EnvelopeSourceAgeMilliseconds,
    double ReceiptElapsedMilliseconds, double? SourceAndTransportAgeMilliseconds, double? CombinedEvidenceAgeMilliseconds,
    double? MaximumDecodeIntervalMilliseconds, double? MaximumStdoutEmitMilliseconds, Pika.PikaFollowAudit? FollowAudit = null);
public sealed record SimulationFollowFailureOrdered(string EpisodeId, long EpisodeOrdinal, string Phase, string Kind,
    ulong FirstCursor, ulong EndCursor, ulong CompletedCursor, int BufferedFrames, double ElapsedSeconds, double DurationSeconds,
    double[] PlannedJointDegrees, double[] EndpointJointDegrees, double[] ActualJointDegrees, double[]? NativeReferenceJointDegrees,
    double[] EndpointJointErrorsDegrees, double? MaximumNativeTrackingErrorDegrees, double CommandOpeningMm, double EndpointCommandOpeningMm,
    double SourceExpectedOpeningMm, double ExpectedOpeningMm, double ActualOpeningMm, double OpeningToleranceMm,
    bool RequiresPhysicalContactCandidate, bool ActualPhysicalContactCandidate, bool ArmStationary, double? GripperSpeed,
    double? GripperFeedbackTime, long? NativeRequestId, long? NativeSequence)
{
    public static SimulationFollowFailureOrdered Capture(OrderedSimulationExecution execution, SimulationFollowHardware hardware,
        bool stationary, double gripperSpeed, double gripperFeedbackTime)
    {
        var phase = execution.ActivePhase;
        var endpoint = phase?.End.CommandDegrees ?? execution.Target.JointDegrees;
        var frame = phase?.End ?? execution.Path.LastSource;
        var reference = hardware.NativeReference;
        return new(frame?.EpisodeId ?? "", frame?.EpisodeOrdinal ?? 0, frame?.Phase ?? "holding", phase?.Kind.ToString() ?? "Holding",
            phase?.Frames[0].Cursor ?? execution.CompletedCursor, phase?.End.Cursor ?? execution.CompletedCursor,
            execution.CompletedCursor, execution.Path.FrameCount, execution.ElapsedSeconds, phase?.DurationSeconds ?? 0,
            execution.Target.JointDegrees.ToArray(), endpoint.ToArray(), hardware.JointDegrees.ToArray(), reference?.JointDegrees.ToArray(),
            endpoint.Zip(hardware.JointDegrees, (target, actual) => Math.Abs(target - actual)).ToArray(),
            reference is null ? null : reference.JointDegrees.Zip(hardware.JointDegrees, (target, actual) => Math.Abs(target - actual)).Max(),
            execution.Target.OpeningMm, phase?.End.CommandOpeningMm ?? execution.Target.OpeningMm,
            phase?.ExpectedMeasuredOpeningMm ?? execution.ExpectedMeasuredOpeningMm, execution.ExpectedMeasuredOpeningMm, hardware.GripperOpeningMm,
            execution.OpeningArrivalToleranceMm, execution.RequiresPhysicalContactCandidate,
            hardware.GripperOpeningMm - (phase?.End.CommandOpeningMm ?? execution.Target.OpeningMm) > 2, stationary,
            double.IsFinite(gripperSpeed) ? gripperSpeed : null, double.IsFinite(gripperFeedbackTime) ? gripperFeedbackTime : null,
            reference?.RequestId, reference?.Sequence);
    }

    public string Describe()
    {
        int joint = Array.IndexOf(EndpointJointErrorsDegrees, EndpointJointErrorsDegrees.Max());
        string F(double value, string format = "F1") => value.ToString(format, CultureInfo.InvariantCulture);
        return $"Queued {Kind.ToLowerInvariant()} phase {Phase}, cursors {FirstCursor}–{EndCursor}: " +
            $"Pika {F(ActualOpeningMm)} mm; expected {F(ExpectedOpeningMm)} ±{F(OpeningToleranceMm)} mm " +
            $"(command {F(EndpointCommandOpeningMm)} mm)" +
            (RequiresPhysicalContactCandidate ? $"; physical contact candidate {(ActualPhysicalContactCandidate ? "present" : "missing")}" : "") +
            $"; largest arm endpoint error J{joint + 1} {F(EndpointJointErrorsDegrees[joint], "F2")}° (allowed 0.25°)" +
            $"; arm stationary {(ArmStationary ? "yes" : "no")}; elapsed {F(ElapsedSeconds, "F2")}/{F(DurationSeconds, "F2")} s.";
    }
}
public sealed record SimulationFollowFailure(DateTimeOffset RecordedUtc, string Phase, string Operation,
    string ErrorType, string Reason, long ArmRequestId, long FollowSequence, SimulationFollowFailureScope Scope,
    SimulationFollowFailureRuntime? Runtime, SimulationFollowFailureHardware? Hardware, SimulationPublishAudit? Publications = null,
    SimulationFollowFailureOrdered? Ordered = null);

/// <summary>Stores one immutable first trigger. Disk and editor logging never sit
/// in front of a hardware hold; cleanup evidence is appended to the same record.</summary>
public sealed class SimulationFollowDiagnostics(string directory, string sessionId, Func<string, Task> report)
{
    private readonly object gate = new();
    private readonly string path = Path.Combine(directory, sessionId + ".json");
    private JsonObject? first;
    private readonly List<string> cleanup = [];
    private Task writer = Task.CompletedTask;
    public string Pathname => path;
    public Task Completion { get { lock (gate) return writer; } }

    public bool Capture(SimulationFollowFailure failure)
    {
        lock (gate)
        {
            if (first is not null) return false;
            if (failure.Ordered is { } ordered) failure = failure with { Reason = failure.Reason + " " + ordered.Describe() };
            first = JsonSerializer.SerializeToNode(failure)!.AsObject();
            Queue();
            return true;
        }
    }

    public void AppendCleanup(IEnumerable<string> failures)
    {
        lock (gate)
        {
            if (first is null) return;
            bool changed = false;
            foreach (string failure in failures)
                if (!cleanup.Contains(failure, StringComparer.Ordinal)) { cleanup.Add(failure); changed = true; }
            if (changed) Queue();
        }
    }

    public string FirstReason { get { lock (gate) return first?[nameof(SimulationFollowFailure.Reason)]?.GetValue<string>() ?? ""; } }

    private void Queue()
    {
        string document = new JsonObject { ["schema"] = 1, ["firstFailure"] = first!.DeepClone(),
            ["cleanupFailures"] = JsonSerializer.SerializeToNode(cleanup) }.ToJsonString();
        string summary = $"Follow failure: {first![nameof(SimulationFollowFailure.Reason)]} · phase {first[nameof(SimulationFollowFailure.Phase)]}" +
            $" · operation {first[nameof(SimulationFollowFailure.Operation)]} · request {first[nameof(SimulationFollowFailure.ArmRequestId)]}" +
            $" · sequence {first[nameof(SimulationFollowFailure.FollowSequence)]}" +
            (cleanup.Count > 0 ? " · cleanup: " + string.Join("; ", cleanup) : "");
        writer = writer.ContinueWith(async _ =>
        {
            string temporary = path + ".tmp";
            string persistenceError = "";
            try
            {
                Directory.CreateDirectory(directory);
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(document);
                    await output.WriteAsync(bytes);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { persistenceError = " · diagnostic file could not be saved: " + error.GetType().Name; }
            try { await report(summary + persistenceError); }
            catch (Exception) { /* A blocked/closed editor must not defeat cleanup or erase the file. */ }
        }, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default).Unwrap();
    }
}
