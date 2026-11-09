using System.Text.Json;
using LuckyRobots.Piper.Pika;

internal static class PikaHealthyPositionTests
{
    public static void Run(Action<bool, string> check)
    {
        static JsonElement Packet(double angle, string status = "0x40") => JsonSerializer.SerializeToElement(new
        {
            motor = new { Position = angle, Speed = 0, Current = -60 },
            motorstatus = new { Status = status, Voltage = 23.9, DriverTemp = 41, MotorTemp = 33, BusCurrent = 0 }
        });
        static double FeedbackAngle(double width)
        {
            double low = 0, high = (180 - 43.99) * Math.PI / 180;
            for (int n = 0; n < 60; n++)
            {
                double middle = (low + high) / 2;
                if (PikaProtocol.Width(middle) <= width) low = middle; else high = middle;
            }
            return low;
        }
        foreach (double width in new[] { 90.085, 90.25, 90.251 })
            check(Math.Abs(PikaProtocol.Width(FeedbackAngle(width)) - width) < 1e-9, "Endpoint-noise fixture did not represent its stated physical width");
        foreach (double angle in new[] { -.0096, 0, .7148, PikaProtocol.Angle(90), FeedbackAngle(90.085), FeedbackAngle(90.25) })
            check(PikaProtocol.Telemetry(Packet(angle), out double read, out _, out bool enabled) && read == angle && enabled,
                "Healthy measured-angle validation rejected an established closed offset or measured endpoint noise");
        foreach (double angle in new[] { -2d, -1, -.15, FeedbackAngle(90.251), 2 * Math.PI })
            check(!PikaProtocol.Telemetry(Packet(angle), out double read, out double speed, out bool enabled) && read == 0 && speed == 0 && !enabled &&
                PikaProtocol.ValidateTelemetry(Packet(angle)) == "$.motor.Position",
                "An impossible healthy enabled angle became fresh feedback or was treated as a new motor mode");
        try { PikaProtocol.Angle(90.25); check(false, "Measured endpoint noise widened the command range"); }
        catch (ArgumentOutOfRangeException) { }
        foreach (string status in new[] { "0x00", "0x20", "0x21", "0x60" })
            check(PikaProtocol.Telemetry(Packet(-2, status), out double read, out _, out _) && read == -2,
                "Healthy-position validation discarded a real disabled/fault-bearing encoder report");

        var writes = new List<double>(); var follow = new PikaFollowSession(writes.Add);
        string id = follow.Start(0); double feedbackAt = -100, measured = 0; bool on = false;
        PikaDriverStatus? driver = null;
        bool Receive(JsonElement packet, double now)
        {
            if (!PikaProtocol.Telemetry(packet, out double angle, out _, out bool enabled)) return false;
            measured = angle; on = enabled; feedbackAt = now;
            PikaDriverStatus.TryRead(packet.GetProperty("motorstatus").GetProperty("Status").GetString(), out driver);
            return true;
        }
        void Tick(double now) => follow.Tick(now, now - feedbackAt < PikaFollowSession.InputDeadlineSeconds ? measured : null, on, driver);
        check(Receive(Packet(.7148), .01), "Observed healthy Pika packet was not received");
        follow.Update(id, 1, 36, .02); Tick(.021);
        double confirmedAt = feedbackAt;
        check(!Receive(Packet(-2), .03) && feedbackAt == confirmedAt && measured == .7148 && on,
            "The exact observed−2/status40 packet renewed feedback age or replaced healthy power/position");
        Tick(.031);
        check(follow.Active && follow.SessionId == id && follow.LastRetirement is null,
            "One rejected invalid encoder packet retired/restarted the healthy follower despite current fresh evidence");
        check(Receive(Packet(.7148), .04), "The next intact packet did not recover immediately");
        follow.Update(id, 2, 36, .041); Tick(.05);
        check(follow.Active && follow.SessionId == id && follow.LastRetirement is null && feedbackAt == .04,
            "The next good packet changed stream ownership or required automatic rearming");
        long sequence = 3;
        foreach (double now in new[] { .06, .1, .18, .27, .28 })
        {
            check(!Receive(Packet(-2), now) && feedbackAt == .04, "Persistent invalid samples kept resetting the source clock");
            follow.Update(id, sequence++, 36, now); Tick(now);
        }
        try { Tick(.291); check(false, "Persistent invalid feedback bypassed the original250ms freshness deadline"); }
        catch (InvalidOperationException) { }
        check(!follow.Active && follow.LastRetirement?.Cause == "feedback-unavailable" && follow.LastRetirement.InputAgeSeconds < .25,
            "Repeated invalid frames were silently held current or mislabeled as a relaxed input deadline");

        foreach (string status in new[] { "0x20", "0x21", "0x60" })
        {
            var fault = Packet(-2, status);
            check(PikaProtocol.Telemetry(fault, out double angle, out _, out bool enabled), "A genuine fault frame was filtered");
            PikaDriverStatus.TryRead(status, out var faultStatus);
            var guarded = new PikaFollowSession(_ => throw new Exception("Fault handling wrote a movement target"));
            string token = guarded.Start(0); guarded.Update(token, 1, 36, .01);
            try { guarded.Tick(.02, angle, enabled, faultStatus); check(false, "A genuine motor fault was delayed/ignored"); }
            catch (InvalidOperationException) { }
            check(!guarded.Active && guarded.LastRetirement?.Cause == "driver-fault", "A fault-bearing−2 report did not retain immediate fault semantics");
        }
        string good = Packet(.7148).GetRawText(), bad = Packet(-2).GetRawText();
        foreach (string prefix in new[] { "", "{\r\n\"motor\":{\r\n\"Speed\":0.000,\r\n\"C" })
        foreach (int fragment in new[] { 1, 7, 32, 4096 })
        {
            var parser = new PikaJsonStream(); string wire = prefix + good + bad + good;
            var accepted = new List<double>();
            for (int at = 0; at < wire.Length; at += fragment)
                foreach (var row in parser.Push(wire.Substring(at, Math.Min(fragment, wire.Length - at))))
                    if (PikaProtocol.Telemetry(row, out double angle, out _, out _)) accepted.Add(angle);
            check(accepted.SequenceEqual(new[] { .7148, .7148 }), "Framing/recovery bypassed the same healthy-angle gate or lost the next intact sample");
        }
    }
}
