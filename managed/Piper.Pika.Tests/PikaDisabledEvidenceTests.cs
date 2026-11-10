using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

internal static class PikaDisabledEvidenceTests
{
    public static async Task Run()
    {
        string owner = Guid.NewGuid().ToString();
        JsonObject Evidence() => new()
        {
            ["owner"] = owner, ["device"] = "/dev/pika-fixture", ["configured"] = true, ["connected"] = true,
            ["fresh"] = true, ["enabled"] = false, ["armed"] = false, ["pending"] = false, ["following"] = false,
            ["resuming_hold"] = false, ["power_uncertain"] = false, ["last_motor_request"] = "verified-disable",
            ["age_s"] = .01, ["request_time_s"] = 10.0, ["feedback_time_s"] = 10.1
        };
        Check(PikaDisabledEvidence.Confirmed(Evidence(), owner), "Fresh same-owner disabled evidence was rejected");
        foreach (string field in new[] { "enabled", "armed", "pending", "following", "resuming_hold", "power_uncertain" })
        {
            var evidence = Evidence(); evidence[field] = true;
            Check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Disabled STOP shortcut ignored " + field);
        }
        foreach (string field in new[] { "configured", "connected", "fresh" })
        {
            var evidence = Evidence(); evidence[field] = false;
            Check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Disabled STOP shortcut accepted " + field + "=false");
        }
        Check(!PikaDisabledEvidence.Confirmed(Evidence(), Guid.NewGuid().ToString()), "Another owner received a disabled STOP acknowledgement");
        foreach (double value in new[] { -.1, .501, double.NaN, double.PositiveInfinity })
        {
            var evidence = Evidence(); evidence["age_s"] = value;
            Check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Old/invalid disabled evidence was promoted to a STOP result");
        }
        foreach (double value in new[] { 9.9, 10, double.NaN })
        {
            var evidence = Evidence(); evidence["feedback_time_s"] = value;
            Check(!PikaDisabledEvidence.Confirmed(evidence, owner), "A pre-request disabled packet acknowledged STOP");
        }
        var malformed = Evidence(); malformed["enabled"] = "false";
        Check(!PikaDisabledEvidence.Confirmed(malformed, owner), "Malformed disabled evidence passed or threw instead of being rejected");
        foreach (string field in new[] { "following", "resuming_hold", "last_motor_request" })
        {
            var evidence = Evidence(); evidence.Remove(field);
            Check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Missing " + field + " evidence was treated as a verified idle motor");
        }

        // Exercise the actual service operation lock/shortcut on every OS. The
        // inert Process wrapper has no redirected stdin and never opens serial;
        // any attempted forward would fail this test. Detach it before disposal.
        var service = new PikaProcessService("/dev/pika-fixture", "never-launched");
        using var current = Process.GetCurrentProcess();
        service.AttachForTest(current, owner, PikaAgentPhase.Ready);
        service.SetEvidenceForTest(PikaPowerEvidence.VerifiedDisabled, owner, "verified-disable", Evidence().ToJsonString());
        try
        {
            var result = await service.ExecuteAsync("hold", JsonSerializer.SerializeToElement(new { owner }), default);
            var after = service.Snapshot();
            Check(result["ok"]?.GetValue<bool>() == true && result["state"]?.GetValue<string>() == "disabled" &&
                after["last_motor_request"]?.GetValue<string>() == "verified-disable" && after["power_uncertain"]?.GetValue<bool>() == false,
                "Already-disabled service hold wrote/reserved new motor power instead of returning its verified state");
            foreach (string value in new[] { "", ",\"value\":null", ",\"value\":\"0.5\"", ",\"value\":1e1000" })
            {
                using var malformedPosition = JsonDocument.Parse("{\"owner\":" + JsonSerializer.Serialize(owner) + value + "}");
                bool rejected = false;
                try { await service.ExecuteAsync("position", malformedPosition.RootElement, default); }
                catch (ArgumentException error) when (error.Message == "Pika position requires a finite numeric value.") { rejected = true; }
                Check(rejected, "Malformed Pika position did not return the actionable input rejection: " + value);
            }
            bool foreignRejected = false;
            try { await service.ExecuteAsync("position", JsonSerializer.SerializeToElement(new { owner = Guid.NewGuid() }), default); }
            catch (InvalidOperationException error) when (error.Message.Contains("does not own", StringComparison.Ordinal)) { foreignRejected = true; }
            Check(foreignRejected && service.Snapshot()["post_request_feedback_verified"]?.GetValue<bool>() == true &&
                service.Snapshot()["power_uncertain"]?.GetValue<bool>() == false,
                "Malformed Pika positions bypassed ownership or changed verified motor evidence");
        }
        finally
        {
            service.DetachForTest();
            await service.DisposeAsync();
        }

        // Reproduce a parsed message reaching publication after its agent was
        // detached. No child process, pipe or serial transport is opened.
        await using var fenced = new PikaProcessService("/dev/pika-fixture", "never-launched");
        using var previous = new Process();
        using var replacement = new Process();
        var requests = (ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>)
            typeof(PikaProcessService).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fenced)!;
        var request = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests["new"] = request;
        var oldReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fenced.AttachForTest(replacement, owner, PikaAgentPhase.Launching);
        try
        {
            bool Publish(Process process, TaskCompletionSource ready, string json)
            {
                using var message = JsonDocument.Parse(json);
                return fenced.PublishAgentMessage(process, ready, message.RootElement);
            }
            Check(!Publish(previous, oldReady, """{"type":"ready"}""") && !oldReady.Task.IsCompleted &&
                !Publish(previous, oldReady, """{"type":"state","fresh":true,"age_s":0,"enabled":true}""") &&
                !Publish(previous, oldReady, """{"type":"ack","id":"new","ok":false}""") && !request.Task.IsCompleted &&
                fenced.Snapshot()["connected"]?.GetValue<bool>() == false && fenced.Snapshot()["enabled"] is null,
                "Detached agent published readiness, stale power feedback, or an acknowledgement into its replacement");
            Check(Publish(replacement, newReady, """{"type":"ready"}""") && newReady.Task.IsCompletedSuccessfully &&
                Publish(replacement, newReady, """{"type":"ack","id":"new","ok":true}""") &&
                request.Task.IsCompletedSuccessfully && request.Task.Result.GetProperty("ok").GetBoolean(),
                "Replacement agent readiness or acknowledgement was dropped");
        }
        finally { fenced.DetachForTest(); }
    }
}
