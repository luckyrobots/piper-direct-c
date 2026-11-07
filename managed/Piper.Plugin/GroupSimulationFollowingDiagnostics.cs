using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    // One coordinator failure, with an independently pinned diagnostic for
    // every participant. Diagnostics cannot select hardware or renew a lease.
    private void FailGroup(GroupSession group, Exception error)
    {
        string Redact(string text)
        {
            foreach (string secret in new[] { group.Backend, owner })
                if (secret.Length > 0) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return text.Length <= 2000 ? text : text[..2000];
        }
        string reason = Redact(error.Message);
        if (Interlocked.CompareExchange(ref group.FirstFailure, reason, "").Length != 0)
        {
            foreach (var lane in group.Lanes) lane.Diagnostics?.AppendCleanup([reason]);
            return;
        }
        if (ReferenceEquals(Volatile.Read(ref groupSimulation), group))
        {
            uiActionError = "Following stopped: " + reason;
            GroupFollowStatus = "Following stopped · " + reason;
            groupFollowFailure = "Following stopped\n" + reason;
            GroupFollowDetail = "All selected targets are discarded. The simulation remains paused; restore feedback or fix the reported issue before arming again.";
        }
        var receipt = telemetry.Latest;
        long now = Stopwatch.GetTimestamp();
        foreach (var lane in group.Lanes)
        {
            try
            {
                var matches = (receipt?.State["devices"] as JsonArray)?.OfType<JsonObject>().Where(d => d["id"]?.GetValue<string>() == lane.Robot.DeviceId).ToArray();
                var device = matches is { Length: 1 } ? matches[0] : null;
                JsonObject? pika = null;
                try { if (receipt is not null) pika = ProfilePikaRouting.Find(receipt.State, lane.Profile); }
                catch (InvalidOperationException) { } // Ambiguous evidence is unavailable, never a best guess.
                var motion = device?["motion"] as JsonObject;
                SimulationFollowFailureHardware? hardware = null;
                static double? Milliseconds(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out double seconds) && double.IsFinite(seconds)
                    ? seconds * 1000 : null;
                if (device is not null && receipt is not null)
                {
                    double age = receipt.PikaEvidenceAgeMilliseconds(pika, now);
                    var angles = device["joints"]?.AsArray().Select(j => j?["actual_deg"]?.GetValue<double>() ?? double.NaN).ToArray() ?? [];
                    if (angles.Any(v => !double.IsFinite(v))) angles = [];
                    hardware = new(receipt.Sequence, receipt.AgeMilliseconds(now), lane.Robot.DeviceId,
                        device["generation"]?.GetValue<long>() ?? 0, motion?["request_id"]?.GetValue<long>() ?? 0,
                        motion?["active"]?.GetValue<bool>() == true, device["recovery_required"]?.GetValue<bool>() == true,
                        motion?["follow_sequence"]?.GetValue<long>() ?? 0, motion?["following"]?.GetValue<bool>() == true,
                        Redact(motion?["message"]?.GetValue<string>() ?? ""), angles,
                        GripperLink.TryMeasuredOpening(pika, out double width, lane.ClosedReference) ? width : null,
                        new(pika is not null, pika?["owner"]?.GetValue<string>() == owner, pika?["owner"]?.GetValue<string>() == owner,
                            pika?["connected"]?.GetValue<bool>() == true, pika?["fresh"]?.GetValue<bool>() == true,
                            pika?["armed"]?.GetValue<bool>() == true, pika?["enabled"]?.GetValue<bool>() == true,
                            receipt.SourceAgeMilliseconds, Math.Max(0, receipt.AgeMilliseconds(now) - receipt.SourceAgeMilliseconds),
                            Milliseconds(pika?["age_s"]), double.IsFinite(age) ? age : null,
                            Milliseconds(pika?["agent_diagnostics"]?["maximum_valid_decode_interval_s"]),
                            Milliseconds(pika?["agent_diagnostics"]?["maximum_stdout_emit_s"]),
                            Volatile.Read(ref lane.RejectedPikaAudit) ?? (error as PikaFollowRejectedException)?.Audit ??
                                pika?["follow_audit"]?.Deserialize<PikaFollowAudit>()));
                }
                var sample = lane.LastSample;
                var runtime = sample is null ? null : new SimulationFollowFailureRuntime(sample.Mode, sample.Sequence,
                    sample.SimulationTimeNs, sample.CapturedMonotonicNs, sample.TimeScale, sample.CommandDegrees.ToArray(),
                    sample.MeasuredDegrees.ToArray(), sample.CommandOpeningMm, sample.MeasuredOpeningMm, sample.SimTimeSeconds);
                SimulationFollowFailureOrdered? ordered = null;
                lock (group.Gate)
                    if (lane.Execution is { } execution && lane.LastHardware is { } observed)
                        ordered = SimulationFollowFailureOrdered.Capture(execution, observed, lane.LastStationary, lane.LastGripperSpeed, lane.LastGripperFeedback);
                string phase = group.Phase.ToString().ToLowerInvariant();
                lane.Diagnostics?.Capture(new(DateTimeOffset.UtcNow, phase, "group:" + phase,
                    error.GetType().Name, reason, lane.ArmRequest, Interlocked.Read(ref lane.Sequence),
                    new(group.Scene, group.RunId, group.Entity, group.Asset, group.Revision, lane.Robot.DeviceId,
                        lane.Robot.Generation, lane.Profile.PikaDevice, receipt?.State["backend_process_id"]?.GetValue<long>(), lane.Robot.JointPrefix),
                    runtime, hardware, lane.Publications.Snapshot(), ordered));
            }
            catch (Exception diagnosticError) { Trace.TraceWarning("Could not capture follower diagnostics: {0}", diagnosticError.GetType().Name); }
        }
        Trace.TraceWarning("Following: {0}", reason);
    }
}
