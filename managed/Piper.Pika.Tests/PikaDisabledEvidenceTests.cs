using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Pika;

internal static class PikaDisabledEvidenceTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string owner = Guid.NewGuid().ToString();
        JsonObject Evidence() => new()
        {
            ["owner"] = owner, ["device"] = "/dev/pika-fixture", ["configured"] = true, ["connected"] = true,
            ["fresh"] = true, ["enabled"] = false, ["armed"] = false, ["pending"] = false, ["following"] = false,
            ["resuming_hold"] = false, ["power_uncertain"] = false, ["last_motor_request"] = "verified-disable",
            ["age_s"] = .01, ["request_time_s"] = 10.0, ["feedback_time_s"] = 10.1
        };
        check(PikaDisabledEvidence.Confirmed(Evidence(), owner), "Fresh same-owner disabled evidence was rejected");
        foreach (string field in new[] { "enabled", "armed", "pending", "following", "resuming_hold", "power_uncertain" })
        {
            var evidence = Evidence(); evidence[field] = true;
            check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Disabled STOP shortcut ignored " + field);
        }
        foreach (string field in new[] { "configured", "connected", "fresh" })
        {
            var evidence = Evidence(); evidence[field] = false;
            check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Disabled STOP shortcut accepted " + field + "=false");
        }
        check(!PikaDisabledEvidence.Confirmed(Evidence(), Guid.NewGuid().ToString()), "Another owner received a disabled STOP acknowledgement");
        foreach (double value in new[] { -.1, .501, double.NaN, double.PositiveInfinity })
        {
            var evidence = Evidence(); evidence["age_s"] = value;
            check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Old/invalid disabled evidence was promoted to a STOP result");
        }
        foreach (double value in new[] { 9.9, 10, double.NaN })
        {
            var evidence = Evidence(); evidence["feedback_time_s"] = value;
            check(!PikaDisabledEvidence.Confirmed(evidence, owner), "A pre-request disabled packet acknowledged STOP");
        }
        var malformed = Evidence(); malformed["enabled"] = "false";
        check(!PikaDisabledEvidence.Confirmed(malformed, owner), "Malformed disabled evidence passed or threw instead of being rejected");
        foreach (string field in new[] { "following", "resuming_hold", "last_motor_request" })
        {
            var evidence = Evidence(); evidence.Remove(field);
            check(!PikaDisabledEvidence.Confirmed(evidence, owner), "Missing " + field + " evidence was treated as a verified idle motor");
        }

        // Exercise the actual service operation lock/shortcut on every OS. The
        // inert Process wrapper has no redirected stdin and never opens serial;
        // any attempted forward would fail this test. Detach it before disposal.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var service = new PikaProcessService("/dev/pika-fixture", "never-launched");
        using var current = Process.GetCurrentProcess();
        void Set(string name, object? value) => typeof(PikaProcessService).GetField(name, fields)!.SetValue(service, value);
        Set("process", current); Set("owner", owner); Set("connected", true); Set("sessionPowerRequested", true);
        Set("motorRequestId", "verified-disable"); Set("motorRequestAcknowledged", true);
        Set("state", Evidence()); Set("stateAt", Stopwatch.GetTimestamp());
        try
        {
            var result = await service.ExecuteAsync("hold", JsonSerializer.SerializeToElement(new { owner }), default);
            var after = service.Snapshot();
            check(result["ok"]?.GetValue<bool>() == true && result["state"]?.GetValue<string>() == "disabled" &&
                after["last_motor_request"]?.GetValue<string>() == "verified-disable" && after["power_uncertain"]?.GetValue<bool>() == false,
                "Already-disabled service hold wrote/reserved new motor power instead of returning its verified state");
        }
        finally
        {
            Set("process", null); Set("owner", null); Set("connected", false); Set("sessionPowerRequested", false); Set("unresolvedOwner", null);
            await service.DisposeAsync();
        }
    }
}
