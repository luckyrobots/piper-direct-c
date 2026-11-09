using System.Diagnostics;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class FollowLaneTelemetryTests
{
    public static void Run(Action<bool, string> check)
    {
        var clock = new ManualClock();
        var profile = new RobotProfile("second", "Piper 2", CanDeviceId: "can-b", Speed: 35, Tolerance: .75,
            PikaDevice: "/dev/pika-b-alias", WslDistribution: "Pika-WSL");
        var state = Snapshot();
        long receivedAt = clock.GetTimestamp();
        TelemetryReceipt latest = new(state, "backend", 11, receivedAt, 25);
        var reader = new FollowLaneTelemetry(profile, 7, null, () => latest, clock);
        var observation = reader.Read("backend");
        var hardware = observation.Hardware("owner", receivedAt, 77, 12);
        check(hardware.DeviceId == "can-b" && hardware.Generation == 7 && hardware.JointDegrees[0] == 1 &&
            hardware.NativeReference is { RequestId: 77, Sequence: 12 } native && native.JointDegrees[0] == 21 &&
            observation.PikaSpeed == 0 && observation.PikaFeedbackTime == 4 && observation.PikaIdle && observation.ArmStationary(receivedAt),
            "Typed Follow observation mixed a selected display lane, its measured pose, or its native reference");

        state["devices"]![1]!["joints"]![0]!["actual_deg"] = 42d;
        state["pikas"]![0]!["telemetry"]!["motor"]!["Speed"] = .08d;
        check(ReferenceEquals(reader.Read("backend"), observation) && observation.Hardware("owner", receivedAt).JointDegrees[0] == 1 &&
            observation.PikaSpeed == 0, "Cached lane observation retained mutable joint or Pika telemetry");
        check(observation.AdmissionReady("owner", 77, 12, receivedAt - clock.TimestampFrequency / 25, 10, receivedAt) &&
            !observation.AdmissionReady("owner", 77, 12, receivedAt - clock.TimestampFrequency / 100, 10, receivedAt) &&
            !observation.AdmissionReady("owner", 77, 12, receivedAt - clock.TimestampFrequency / 25, 11, receivedAt),
            "Typed admission confused delayed source evidence or a cached sequence with post-ACK proof");

        var sent = new List<(string Route, JsonObject Body, bool Cancelable, string? Backend)>();
        var controller = new FollowLaneController("backend", "owner", profile, 7, (route, body, cancellation, backend) =>
        {
            sent.Add((route, (JsonObject)body.DeepClone(), cancellation.CanBeCanceled, backend));
            return Task.FromResult(new JsonObject { ["ok"] = true });
        });
        clock.AdvanceMilliseconds(199);
        _ = reader.Read("backend").Hardware("owner", clock.GetTimestamp());
        controller.Arm(FollowOp.Update, new() { ["request_id"] = 77L, ["sequence"] = 13L }).GetAwaiter().GetResult();
        controller.Pika(FollowOp.Hold, null).GetAwaiter().GetResult();
        using var cancellation = new CancellationTokenSource();
        controller.Heartbeat(77, cancellation.Token).GetAwaiter().GetResult();
        check(sent.Count == 3 && sent.All(s => s.Backend == "backend" && s.Body["owner"]?.GetValue<string>() == "owner") &&
            sent[0] is { Route: "/api/command", Cancelable: false } && sent[0].Body["action"]?.GetValue<string>() == "follow-update" &&
            sent[0].Body["device_id"]?.GetValue<string>() == "can-b" && sent[0].Body["generation"]?.GetValue<long>() == 7 &&
            sent[0].Body["sequence"]?.GetValue<long>() == 13 && sent[0].Body["speed_percent"]?.GetValue<double>() == 35 &&
            sent[1] is { Route: "/api/pika/hold", Cancelable: false } && sent[1].Body["pika_device"]?.GetValue<string>() == "/dev/pika-b-alias" &&
            sent[1].Body["pika_distribution"]?.GetValue<string>() == (OperatingSystem.IsWindows() ? "Pika-WSL" : null) &&
            sent[2] is { Route: "/api/heartbeat", Cancelable: true } &&
            sent[2].Body["request_id"]?.GetValue<long>() == 77, "Typed controller changed lane routing, request sequence or cancellation policy");
        check(sent[0].Body["standalone"]?.GetValue<bool>() == true && !sent[0].Body.ContainsKey("clear") &&
            !sent[0].Body.ContainsKey("supported") && !sent[0].Body.ContainsKey("reset_completed"),
            "Follow update carried safety declarations that only other actions read");
        sent.Clear();
        foreach (var action in new[] { FollowOp.Match, FollowOp.Start, FollowOp.Hold })
            controller.Arm(action, null).GetAwaiter().GetResult();
        check(sent.All(s => s.Body["standalone"]?.GetValue<bool>() == true && !s.Body.ContainsKey("supported") &&
                !s.Body.ContainsKey("reset_completed")) &&
            sent[0].Body["clear"]?.GetValue<bool>() == true && sent[1].Body["clear"]?.GetValue<bool>() == true &&
            !sent[2].Body.ContainsKey("clear"), "Follow sent clear, supported or reset_completed beyond the actions that read them");

        clock.AdvanceMilliseconds(2);
        Reject(() => _ = reader.Read("backend").Hardware("owner", clock.GetTimestamp()), "Cached Pika evidence gained a new 250 ms window");
        var previous = latest;
        latest = new(Snapshot(), "backend", 12, clock.GetTimestamp(), 0);
        var replacement = reader.Read("backend");
        check(!ReferenceEquals(replacement, observation) && ReferenceEquals(reader.Read("backend", previous), observation) &&
            ReferenceEquals(reader.Read("backend"), replacement), "Interleaved reads did not reuse the immutable observation for each receipt");
        Reject(() => _ = reader.Read("different-backend"), "Typed observer accepted a replacement backend under the previous scope");
        Reject(() => _ = replacement.Hardware("other-owner", clock.GetTimestamp()), "Typed observation accepted an unrelated motion owner");
        Reject(() => _ = replacement.Hardware("owner", clock.GetTimestamp(), 77, 11), "Typed observation accepted an unpublished native sequence");

        var wrong = Snapshot();
        wrong["devices"]![1]!["generation"] = 8L;
        latest = new(wrong, "backend", 13, clock.GetTimestamp(), 0);
        Reject(() => _ = reader.Read("backend"), "Typed observer accepted a replaced CAN connection");
        var disconnected = previous with { Connected = false };
        Reject(() => _ = reader.Read("backend", disconnected), "Typed observer reused a connected snapshot after disconnect");
        clock.AdvanceMilliseconds(300);
        Reject(() => _ = reader.Read("backend", previous), "Cached controller receipt gained a new 500 ms window");

        var cadence = new SimulationPublishCadence(clock.GetTimestamp(), clock.TimestampFrequency, TimeSpan.FromMilliseconds(30));
        clock.AdvanceMilliseconds(12); // The acknowledgement consumed part of the period.
        Task wait = cadence.WaitAfterPublication(clock, CancellationToken.None);
        clock.AdvanceMilliseconds(16);
        check(!wait.IsCompleted, "Follow cadence published before the remaining deadline");
        clock.AdvanceMilliseconds(2);
        check(wait.IsCompletedSuccessfully, "Follow cadence ignored its injected clock at the deadline");
        clock.AdvanceMilliseconds(85);
        wait = cadence.WaitAfterPublication(clock, CancellationToken.None);
        check(cadence.SkippedDeadlines == 2 && !wait.IsCompleted, "Follow cadence attempted to replay missed publication deadlines");
        clock.AdvanceMilliseconds(5);
        check(wait.IsCompletedSuccessfully, "Follow cadence failed to resume at the next real deadline");

        void Reject(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException) { check(true, reason); return; }
            check(false, reason);
        }
    }

    private static JsonObject Snapshot() => new()
    {
        ["token"] = "backend", ["selected"] = "can-a",
        ["devices"] = new JsonArray(new JsonObject { ["id"] = "can-a", ["generation"] = 3L }, new JsonObject
        {
            ["id"] = "can-b", ["generation"] = 7L, ["present"] = true, ["up"] = true, ["fresh"] = true,
            ["firmware_supported"] = true, ["recovery_required"] = false, ["reset_pending"] = false,
            ["status"] = new JsonObject { ["control"] = 1, ["arm_status"] = 0, ["error"] = 0, ["age_s"] = .01, ["pose_stationary_s"] = .5 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(j => (JsonNode)new JsonObject
            {
                ["actual_deg"] = (double)j, ["command_deg"] = j + 20d, ["age_s"] = .01, ["fresh"] = true,
                ["enabled"] = true, ["fault"] = 0, ["limits_fresh"] = true, ["acceleration_fresh"] = true,
                ["min_deg"] = -90d, ["max_deg"] = 90d, ["max_speed_rad_s"] = 1d, ["max_acceleration_rad_s2"] = 2d
            }).ToArray()),
            ["motion"] = new JsonObject { ["device_id"] = "can-b", ["generation"] = 7L, ["active"] = true, ["following"] = true,
                ["trajectory_follow"] = true, ["owner"] = "owner", ["request_id"] = 77L, ["follow_sequence"] = 12L }
        }),
        ["pikas"] = new JsonArray(new JsonObject
        {
            ["device"] = "/dev/pika-b", ["aliases"] = new JsonArray("/dev/pika-b-alias"), ["distribution"] = "Pika-WSL",
            ["fresh"] = true, ["connected"] = true, ["owner"] = "owner", ["enabled"] = true, ["armed"] = true, ["driver_fault"] = false,
            ["age_s"] = .025, ["feedback_time_s"] = 4d, ["pending"] = false,
            ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = .1, ["Speed"] = 0d } }
        })
    };

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp = Stopwatch.Frequency;
        private ManualTimer? timer;
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timer = new(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void AdvanceMilliseconds(long milliseconds)
        {
            timestamp += milliseconds * TimestampFrequency / 1000;
            timer?.Observe(timestamp);
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long due;
            private bool pending;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new ArgumentException("The fixture supports one-shot timers only.", nameof(period));
                due = clock.GetTimestamp() + (long)Math.Round(dueTime.TotalSeconds * clock.TimestampFrequency);
                pending = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
            public void Observe(long timestamp)
            {
                if (!pending || timestamp < due) return;
                pending = false;
                callback(state);
            }
            public void Dispose() => pending = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
