using LuckyRobots.Piper.Pika;

internal static class PikaMeasuredHoldResumeTests
{
    public static void Run(Action<bool, string> check)
    {
        double angle = PikaProtocol.Angle(20);
        var writes = new List<(int Command, double Value)>();
        var resume = new PikaMeasuredHoldResume((command, value) => writes.Add((command, value)));
        void Start(double now = 10, double feedback = 10, double? measured = null, double speed = 0, bool enabled = false,
            bool pending = false, bool following = false, double? expected = null, double expectedTime = 10) =>
            resume.Start(now, feedback, measured ?? angle, speed, enabled, pending, following, expected ?? angle, expectedTime, "resume-audit-fixture");
        void Reject(Action operation, string label)
        {
            int count = writes.Count; bool rejected = false;
            try { operation(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && writes.Count == count, "Invalid measured hold emitted a packet: " + label);
        }
        Reject(() => Start(now: 11), "stale source");
        Reject(() => Start(feedback: 10.1), "future source clock");
        Reject(() => Start(expectedTime: 10.1), "expected newer feedback");
        Reject(() => Start(expected: PikaProtocol.Angle(21)), "changed opening");
        Reject(() => Start(speed: .021), "moving jaws");
        Reject(() => Start(enabled: true), "already enabled");
        Reject(() => Start(pending: true), "pending position");
        Reject(() => Start(following: true), "active follow");
        Reject(() => Start(measured: double.NaN), "nonfinite angle");
        Reject(() => Start(measured: PikaProtocol.Angle(90) + .01, expected: PikaProtocol.Angle(90) + .01), "outside 90 mm");
        Start();
        check(writes.Count == 3 && writes[0] == (22, angle) && writes[1] == (11, 0) && writes[2] == (22, angle),
            "Measured resume did not seed → enable → repeat the exact measured target");
        Reject(() => Start(), "second active attempt");
        resume.Tick(10.1, 10, angle, 0, true, true);
        check(resume.Active && !resume.Succeeded, "Buffered pre-request enabled feedback armed the jaws");
        resume.Tick(10.5, 10.5, angle, 0, true, true); // Supported unsolicited 2 Hz feedback.
        check(!resume.Active && resume.Succeeded && writes.Count == 3, "Fresh same-opening enabled feedback failed to arm without extra targets");
        check(resume.Audit is { RequestId: "resume-audit-fixture", RequestedAtSeconds: 10, RequestFeedbackTimeSeconds: 10,
            VerifiedAtSeconds: 10.5, VerifiedFeedbackTimeSeconds: 10.5, VerifiedDriftMm: 0, Outcome: "verified" } &&
            resume.Audit.RequestedRawAngleRad == angle && resume.Audit.ExpectedRawAngleRad == angle && resume.Audit.VerifiedRawAngleRad == angle,
            "Measured hold audit did not retain its actual request-time target and causal verification tuple");

        foreach (double closed in new[] { -.0005, -.0096 })
        {
            writes.Clear(); Start(measured: closed, expected: closed);
            check(writes.Count == 3 && writes[0].Value == closed && writes[2].Value == closed,
                "Near-closed measured raw hold was clipped or replaced with a zero target");
            resume.Tick(10.1, 10.1, closed, 0, true, true);
            check(resume.Succeeded, "Bounded near-closed measured hold was not acknowledged");
        }
        for (int failure = 0; failure < 4; ++failure)
        {
            writes.Clear(); Start();
            if (failure == 0) resume.Tick(11.6, 11.6, angle, 0, false, true);
            if (failure == 1) resume.Tick(10.1, 10.1, PikaProtocol.Angle(22), 0, true, true);
            if (failure == 2) resume.Tick(10.1, 10.1, angle, .03, true, true);
            if (failure == 3) resume.Tick(10.1, 10, angle, 0, false, false);
            check(!resume.Active && !resume.Succeeded && writes.Count == 4 && writes[^1].Command == 10,
                "Failed measured resume did not request one bounded disable");
            resume.Tick(12, 12, angle, 0, true, true);
            check(writes.Count == 4 && !resume.Succeeded, "A failed resume retried or armed from later feedback");
        }
        writes.Clear();
        var failingWriter = new PikaMeasuredHoldResume((command, value) =>
        {
            writes.Add((command, value));
            if (command == 11) throw new IOException("Synthetic enable write failure");
        });
        try { failingWriter.Start(10, 10, angle, 0, false, false, false, angle, 10); }
        catch (IOException) { }
        check(writes.Select(w => w.Command).SequenceEqual(new[] { 22, 11, 10 }) && !failingWriter.Active && !failingWriter.Succeeded,
            "Partial write failure left a measured resume armed or repeated its old target");
    }
}
