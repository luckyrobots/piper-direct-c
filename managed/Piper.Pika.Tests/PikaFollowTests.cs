using LuckyRobots.Piper;
using LuckyRobots.Piper.Pika;

internal static class PikaFollowTests
{
    private static void RejectInput(Action action, string message) =>
        Reject<Exception>(action, message, error => error is InvalidOperationException or ArgumentException);

    internal static async Task RunAsync()
    {
        var writes = new List<double>();
        var follow = new PikaFollowSession(writes.Add, .05);
        string first = follow.Start(0);
        Check(Guid.TryParse(first, out _) && writes.Count == 0, "Starting follow moved the jaws.");
        follow.Update(first, 1, 10, .001);
        follow.Update(first, 2, 40, .002);
        follow.Tick(.003, PikaProtocol.Angle(20), true);
        Check(writes.Count == 1 && Math.Abs(PikaProtocol.Width(writes[0]) - 40) < .00001, "Latest target did not replace pending opening.");
        RejectInput(() => follow.Update(first, 2, 60, .004), "Duplicate sequence accepted.");
        RejectInput(() => follow.Update(first, 1, 60, .005), "Old sequence accepted.");
        RejectInput(() => follow.Update(Guid.NewGuid().ToString(), 3, 60, .006), "Foreign session accepted.");
        RejectInput(() => follow.Update(first, 3, double.NaN, .007), "Nonfinite opening accepted.");
        RejectInput(() => follow.Update(first, 3, 91, .008), "Out-of-range opening clipped/accepted.");
        follow.Update(first, 3, 55, .009);
        follow.Tick(.025, PikaProtocol.Angle(20), true);
        Check(writes.Count == 1, "Configured command period was bypassed.");
        follow.Tick(.054, PikaProtocol.Angle(20), true);
        Check(writes.Count == 2 && Math.Abs(PikaProtocol.Width(writes[^1]) - 55) < .00001, "Bounded tick did not send the latest opening.");
        follow.Hold(PikaProtocol.Angle(23));
        Check(!follow.Active && follow.Holding && follow.OpeningMm is null && Math.Abs(PikaProtocol.Width(writes[^1]) - 23) < .00001,
            "Hold kept a pending target or held the commanded rather than measured opening.");
        int heldWrites = writes.Count;
        follow.Tick(.4, null, false);
        Check(writes.Count == heldWrites, "Retired follow resumed after hold.");
        RejectInput(() => follow.Update(first, 4, 90, .41), "Held session accepted another target.");
        string second = follow.Start(.42);
        Check(second != first && follow.Sequence == 0 && follow.OpeningMm is null, "New session inherited a pending target/identity.");
        RejectInput(() => follow.Update(first, 5, 90, .43), "Previous session crossed a new session boundary.");
        follow.Update(second, 1, 80, .44);
        follow.Tick(.70, PikaProtocol.Angle(24), true);
        Check(!follow.Active && follow.Holding && Math.Abs(PikaProtocol.Width(writes[^1]) - 24) < .00001,
            "Input deadline continued moving instead of holding the fresh measurement.");
        string third = follow.Start(.71);
        follow.Update(third, 1, 80, .72);
        RejectInput(() => follow.Tick(.73, null, true), "Stale feedback kept following enabled.");
        Check(!follow.Active && follow.OpeningMm is null && !follow.Holding, "Unavailable feedback invented successful holding.");
        int beforeStale = writes.Count;
        RejectInput(() => follow.Hold(null), "Hold fabricated a stale measured target.");
        Check(writes.Count == beforeStale, "Stale hold wrote a target.");
        RejectInput(() => follow.Start(.6), "Backward clock accepted.");
        RejectInput(() => new PikaFollowSession(writes.Add, .001), "Unbounded command rate accepted.");

        var broken = new PikaFollowSession(_ => throw new IOException("fixture write failed"));
        string brokenId = broken.Start(0); broken.Update(brokenId, 1, 20, .01);
        try { broken.Tick(.02, PikaProtocol.Angle(10), true); throw new Exception("Write failure hidden."); }
        catch (IOException) { }
        Check(!broken.Active && broken.OpeningMm is null, "Failed write retained an active session.");
        try { broken.Hold(PikaProtocol.Angle(10)); throw new Exception("Hold write failure hidden."); }
        catch (IOException) { }
        Check(!broken.Holding, "Failed hold was reported as successful.");

        var evidence = new PikaFollowSession(_ => { });
        string recorded = evidence.Start(0);
        for (int i = 1; i <= 40; i++)
        {
            double now = i * .01;
            evidence.ObserveFeedback(now, .5 + i * .0001, 0, true, "0x40");
            evidence.Update(recorded, i, 30, now);
        }
        evidence.Tick(.651, .504, true);
        var expired = evidence.LastRetirement!;
        Check(!evidence.Active && evidence.Holding && expired.Cause == "input-expired" && expired.LastInputSequence == 40 &&
            Math.Abs(expired.LastInputTimeSeconds - .4) < 1e-12 && expired.InputAgeSeconds >= .25 && expired.Enabled == true,
            "Input expiry lost its exact deadline, accepted sequence or enabled measurement evidence.");
        Check(expired.RecentEvents.Count == 16 && expired.RecentFeedback.Count == 4 && expired.RecentFeedback[^1].MotorStatus == "0x40",
            "Pika retirement did not retain its bounded16 events/four raw feedback observations.");
        evidence.Hold(.504, now: .66); evidence.Cancel("cleanup", .67);
        evidence.ObserveFeedback(.68, .6, 0, true, "0x40");
        Check(ReferenceEquals(expired, evidence.LastRetirement) && expired.RecentFeedback[^1].PositionRadians == .504 &&
            evidence.Audit.RecentEvents.Count <= 16 && evidence.Audit.RecentFeedback.Count == 4,
            "Hold/cancel/later telemetry overwrote or mutated the first terminal evidence.");
        evidence.Tick(.69, .6, true);
        Check(!evidence.Active, "Diagnostic retirement automatically restarted a dead follow stream.");
        recorded = evidence.Start(.7); evidence.Update(recorded, 1, 30, .71);
        evidence.ObserveFeedback(.72, .6, 0, false, "0x00");
        RejectInput(() => evidence.Tick(.72, .6, false), "A disabled feedback sample was hidden by diagnostics.");
        var disabled = evidence.LastRetirement!;
        Check(disabled.Cause == "feedback-unavailable" && disabled.Enabled == false && disabled.InputAgeSeconds < .25 &&
            disabled.RecentFeedback[^1].MotorStatus == "0x00" && disabled.LastInputSequence == 1,
            "Transient disabled feedback cannot be distinguished from a host input deadline.");
        evidence.Hold(.6, now: .73);
        Check(ReferenceEquals(disabled, evidence.LastRetirement), "Cleanup lost transient disabled feedback proof.");
        recorded = evidence.Start(.74); evidence.Update(recorded, 1, 30, .75);
        evidence.ObserveFeedback(.76, 100, 0, true, "0x40");
        RejectInput(() => evidence.Tick(.76, 100, true), "An out-of-range finite Pika angle was accepted.");
        Check(evidence.LastRetirement!.MeasuredAngleRadians == 100 && evidence.LastRetirement.Enabled == true,
            "An invalid angle was indistinguishable from disabled feedback after retirement.");
        recorded = evidence.Start(.77); evidence.Update(recorded, 1, 30, .78); evidence.Hold(.6, now: .79);
        Check(evidence.LastRetirement!.Cause == "operator-hold" && evidence.LastRetirement.SessionOrdinal == 4,
            "An explicit Stop did not retain its own session's distinct terminal cause.");
        string serialized = System.Text.Json.JsonSerializer.Serialize(evidence.Audit);
        Check(serialized.Length < 16384 && !serialized.Contains(recorded), "Bounded follow diagnostics leaked its opaque session identifier or grew without a bound.");
        var rejection = new PikaFollowRejectedException("No matching active Pika following session.", evidence.Audit);
        var reconstructed = new PikaFollowRejectedException(rejection.Message, evidence.Audit);
        Check(reconstructed.Message == rejection.Message && rejection.Message.Contains("Previous stream ended:"),
            "Structured retirement errors lost the actual reason or duplicated it across transport layers.");
        await using (var retained = new PikaProcessService("/dev/pika-fixture", "unused"))
        {
            var method = typeof(PikaProcessService).GetMethod("RetainFollowAudit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using var ack = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { follow_audit = evidence.Audit }));
            method.Invoke(retained, [ack.RootElement]);
            var snapshot = retained.Snapshot();
            Check(snapshot["follow_audit"]?["LastRetirement"]?["Cause"]?.GetValue<string>() == "operator-hold",
                "An immediate rejected ACK audit was lost before the periodic state snapshot.");
            using var malformed = System.Text.Json.JsonDocument.Parse("{\"follow_audit\":{\"RecentEvents\":null,\"RecentFeedback\":null}}");
            method.Invoke(retained, [malformed.RootElement]);
            Check(retained.Snapshot()["follow_audit"]?["LastRetirement"]?["Cause"]?.GetValue<string>() == "operator-hold",
                "Malformed diagnostics erased valid terminal evidence or changed stream control.");
        }

        // One delegate can throw synchronously before returning a Task. Both
        // channels still run and preserve their independent acknowledgement.
        int armAttempts = 0, pikaAttempts = 0;
        var failedArm = await CoordinatedStop.RunAsync(_ => { Interlocked.Increment(ref armAttempts); throw new IOException("arm failed"); },
            _ => { Interlocked.Increment(ref pikaAttempts); return Task.FromResult(new StopChannelResult(true, "held")); });
        Check(armAttempts == 1 && pikaAttempts == 1 && !failedArm.Ok && !failedArm.Arm.Ok && failedArm.Pika.Ok, "Arm failure suppressed/obscured Pika hold.");
        var failedPika = await CoordinatedStop.RunAsync(_ => Task.FromResult(new StopChannelResult(true, "stopped")),
            _ => throw new OperationCanceledException());
        Check(failedPika.Arm.Ok && !failedPika.Pika.Ok && !failedPika.Ok, "Pika cancellation suppressed/obscured arm stop.");
        var never = new TaskCompletionSource<StopChannelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = await CoordinatedStop.RunAsync(_ => never.Task,
            _ => { Interlocked.Increment(ref pikaAttempts); return Task.FromResult(new StopChannelResult(true, "held")); }, TimeSpan.FromMilliseconds(30));
        Check(!timedOut.Arm.Ok && timedOut.Pika.Ok && pikaAttempts == 2, "Blocked arm prevented bounded Pika stop reporting.");
        never.TrySetResult(new(false, "late stop"));
        var success = await CoordinatedStop.RunAsync(_ => Task.FromResult(new StopChannelResult(true, "stopped")),
            _ => Task.FromResult(new StopChannelResult(true, "held")));
        Check(success.Ok, "Successful coordinated stop was not acknowledged.");
        Console.WriteLine("Pika follow foundations passed: latest-target replacement, session/sequence guards, cadence, deadline, measured hold, stale/write failure retirement, and independent dual-stop outcomes.");
    }
}
