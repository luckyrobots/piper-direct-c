using LuckyRobots.Piper.Pika;

internal static class PikaMeasuredHoldResumeTests
{
    public static void Run()
    {
        double angle = PikaProtocol.Angle(20);
        var writes = new List<(PikaCommand Command, double Value)>();
        var resume = new PikaMeasuredHoldResume((command, value) => writes.Add((command, value)));
        void Start(double now = 10, double feedback = 10, double? measured = null, double speed = 0, bool enabled = false,
            bool pending = false, bool following = false, double? expected = null, double expectedTime = 10) =>
            resume.Start(now, feedback, measured ?? angle, speed, enabled, pending, following, expected ?? angle, expectedTime, "resume-audit-fixture");
        void RejectWithoutWrite(Action operation, string label)
        {
            int count = writes.Count; bool rejected = false;
            try { operation(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && writes.Count == count, "Invalid measured hold emitted a packet: " + label);
        }
        RejectWithoutWrite(() => Start(now: 11), "stale source");
        RejectWithoutWrite(() => Start(feedback: 10.1), "future source clock");
        RejectWithoutWrite(() => Start(expectedTime: 10.1), "expected newer feedback");
        RejectWithoutWrite(() => Start(expected: PikaProtocol.Angle(21)), "changed opening");
        RejectWithoutWrite(() => Start(speed: .021), "moving jaws");
        RejectWithoutWrite(() => Start(enabled: true), "already enabled");
        RejectWithoutWrite(() => Start(pending: true), "pending position");
        RejectWithoutWrite(() => Start(following: true), "active follow");
        RejectWithoutWrite(() => Start(measured: double.NaN), "nonfinite angle");
        RejectWithoutWrite(() => Start(measured: PikaProtocol.Angle(90) + .01, expected: PikaProtocol.Angle(90) + .01), "outside 90 mm");
        Start();
        Check(writes.Count == 3 && writes[0] == (PikaCommand.Position, angle) && writes[1] == (PikaCommand.Enable, 0) && writes[2] == (PikaCommand.Position, angle),
            "Measured resume did not seed → enable → repeat the exact measured target");
        RejectWithoutWrite(() => Start(), "second active attempt");
        resume.Tick(10.1, 10, angle, 0, true, true);
        Check(resume.Active && !resume.Succeeded, "Buffered pre-request enabled feedback armed the jaws");
        resume.Tick(10.5, 10.5, angle, 0, true, true); // Supported unsolicited 2 Hz feedback.
        Check(!resume.Active && resume.Succeeded && writes.Count == 3, "Fresh same-opening enabled feedback failed to arm without extra targets");
        Check(resume.Audit is { RequestId: "resume-audit-fixture", RequestedAtSeconds: 10, RequestFeedbackTimeSeconds: 10,
            VerifiedAtSeconds: 10.5, VerifiedFeedbackTimeSeconds: 10.5, VerifiedDriftMm: 0, Outcome: PikaMeasuredHoldOutcome.Verified } &&
            resume.Audit.RequestedRawAngleRad == angle && resume.Audit.ExpectedRawAngleRad == angle && resume.Audit.VerifiedRawAngleRad == angle,
            "Measured hold audit did not retain its actual request-time target and causal verification tuple");
        Check(System.Text.Json.JsonSerializer.Serialize(resume.Audit).Contains("\"Outcome\":\"verified\"", StringComparison.Ordinal),
            "The measured-hold outcome changed its lowercase string wire form");

        foreach (double closed in new[] { -.0005, -.0096 })
        {
            writes.Clear(); Start(measured: closed, expected: closed);
            Check(writes.Count == 3 && writes[0].Value == closed && writes[2].Value == closed,
                "Near-closed measured raw hold was clipped or replaced with a zero target");
            resume.Tick(10.1, 10.1, closed, 0, true, true);
            Check(resume.Succeeded, "Bounded near-closed measured hold was not acknowledged");
        }
        for (int failure = 0; failure < 4; ++failure)
        {
            writes.Clear(); Start();
            if (failure == 0) resume.Tick(11.6, 11.6, angle, 0, false, true);
            if (failure == 1) resume.Tick(10.1, 10.1, PikaProtocol.Angle(22), 0, true, true);
            if (failure == 2) resume.Tick(10.1, 10.1, angle, .03, true, true);
            if (failure == 3) resume.Tick(10.1, 10, angle, 0, false, false);
            Check(!resume.Active && !resume.Succeeded && writes.Count == 4 && writes[^1].Command == PikaCommand.Disable,
                "Failed measured resume did not request one bounded disable");
            resume.Tick(12, 12, angle, 0, true, true);
            Check(writes.Count == 4 && !resume.Succeeded, "A failed resume retried or armed from later feedback");
        }
        writes.Clear();
        var failingWriter = new PikaMeasuredHoldResume((command, value) =>
        {
            writes.Add((command, value));
            if (command == PikaCommand.Enable) throw new IOException("Synthetic enable write failure");
        });
        try { failingWriter.Start(10, 10, angle, 0, false, false, false, angle, 10); }
        catch (IOException) { }
        Check(writes.Select(w => w.Command).SequenceEqual(new[] { PikaCommand.Position, PikaCommand.Enable, PikaCommand.Disable }) && !failingWriter.Active && !failingWriter.Succeeded,
            "Partial write failure left a measured resume armed or repeated its old target");
    }
}
