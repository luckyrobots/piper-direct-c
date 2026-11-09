using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class PikaTeachingTests
{
    public static void Run()
    {
        string afterTeaching = PipersPlugin.MeasuredPikaHoldStatus(false, false);
        Check(afterTeaching.Contains("Arm hand guiding has ended", StringComparison.Ordinal) && afterTeaching.Contains("Recording is stopped", StringComparison.Ordinal) &&
            !afterTeaching.Contains("continue", StringComparison.Ordinal), "Holding after teaching ended incorrectly claimed an active recording session");
        Check(PipersPlugin.MeasuredPikaHoldStatus(true, true).Contains("Hand guiding and observation recording continue", StringComparison.Ordinal) &&
            PipersPlugin.MeasuredPikaHoldStatus(false, true).Contains("observation recording is stopped", StringComparison.Ordinal) &&
            PipersPlugin.MeasuredPikaHoldStatus(true, false).Contains("session is finishing", StringComparison.Ordinal),
            "Measured-hold status ignored the independent hand-guiding and observation-recording states");
        var identity = PikaTeachingIdentity.Create("robot-one", "CAN-one", 1, "/dev/pika-one", new RobotProfile("robot-one", "Robot one").PikaDistribution, "DO-NOT-SAVE-THIS-TOKEN");
        Check(identity.BackendSessionSha256.Length == 64 && !JsonSerializer.Serialize(identity).Contains("DO-NOT-SAVE"), "Teaching report exposed an authorization token");
        var pika = new JsonObject { ["owner"] = "our-owner", ["connected"] = true, ["fresh"] = true, ["enabled"] = false,
            ["armed"] = false, ["pending"] = false, ["following"] = false, ["power_uncertain"] = false,
            ["last_motor_request"] = "disable-one", ["request_time_s"] = 10.0, ["feedback_time_s"] = 10.1, ["age_s"] = .01 };
        Check(PikaTeachingRecorder.DisabledAfterRequest(pika, "our-owner", "disable-one"), "Fresh causally verified disabled Pika was rejected");
        var holding = (JsonObject)pika.DeepClone();
        holding["enabled"] = true; holding["armed"] = true; holding["power_uncertain"] = true;
        Check(PikaTeachingRecorder.MotorModeAfterRequest(holding, "our-owner", "disable-one", true),
            "The conservative powered-session reservation was mistaken for a motor fault during verified holding");
        pika["last_motor_request"] = "hold-two";
        Check(PikaTeachingRecorder.AssessMotor(pika, "our-owner", "disable-one", false, true) == PikaTeachingMotorDisposition.Gap,
            "Reader-before-API-ack race ended a locally requested teaching transition");
        Check(PikaTeachingRecorder.AssessMotor(pika, "our-owner", "disable-one", false, false) == PikaTeachingMotorDisposition.End,
            "An unknown motor request remained accepted after the bounded local transition");
        Check(PikaTeachingRecorder.AssessMotor(pika, "other-owner", "disable-one", false, true) == PikaTeachingMotorDisposition.End,
            "A transition gap hid a foreign gripper owner");
        pika["last_motor_request"] = "disable-one";
        foreach (string field in new[] { "enabled", "armed", "pending", "following", "power_uncertain" })
        {
            pika[field] = true;
            Check(!PikaTeachingRecorder.DisabledAfterRequest(pika, "our-owner", "disable-one"), "Pika teaching ignored " + field);
            pika[field] = false;
        }
        Check(!PikaTeachingRecorder.DisabledAfterRequest(pika, "other-owner", "disable-one"), "Foreign owner granted free-jaw authority");
        Check(!PikaTeachingRecorder.DisabledAfterRequest(pika, "our-owner", "other-request"), "Buffered disabled feedback acknowledged another request");
        pika["feedback_time_s"] = 10.0;
        Check(!PikaTeachingRecorder.DisabledAfterRequest(pika, "our-owner", "disable-one"), "Pre-request feedback granted free-jaw authority");
        pika["feedback_time_s"] = 10.1; pika["age_s"] = .501;
        Check(!PikaTeachingRecorder.DisabledAfterRequest(pika, "our-owner", "disable-one"), "Stale disabled feedback granted free-jaw authority");
        long origin = 100000;
        long Tick(double seconds) => origin + (long)(Stopwatch.Frequency * seconds);
        PikaTeachingObservation Frame(long sequence, double time) => new(identity, sequence, Tick(time), 1234567890000000 + (long)(time * 1e6),
            [1, 2, 3, 4, 5, 6], -.0096, 0, 5, [5, 6, 7, 8, 9, 10], 80, 123.4);
        var recorder = new PikaTeachingRecorder(identity, "disable-one", -.0096, origin, DateTimeOffset.UnixEpoch);
        Check(recorder.Observe(Frame(1, 0)), "Initial measured free-jaw observation was rejected");
        Check(recorder.Observe(Frame(2, .05)), "Repeated Pika feedback time was incorrectly fabricated as a new source timestamp or rejected");
        Check(!recorder.Observe(Frame(2, .06)), "Duplicate source receipt became another teaching frame");
        Check(!recorder.Observe(Frame(3, .1) with { PikaSourceAgeMs = 501 }), "Stale Pika observation became a valid paired frame");
        Check(!recorder.Observe(Frame(3, .11) with { JointDegrees = [0, 0, double.NaN, 0, 0, 0] }), "Nonfinite joint data entered the teaching artifact");
        Check(recorder.Observe(Frame(4, .5)), "Fresh observation after a gap was rejected");
        recorder.RequestMotorMode("hold-two", true, Tick(.6));
        Check(recorder.Observe(Frame(5, .7) with { PikaMotorEnabled = true }), "Verified measured holding could not continue the same teaching session");
        recorder.RequestMotorMode("free-three", false, Tick(.8));
        Check(recorder.Observe(Frame(6, .9)), "A confirmed release could not continue the same teaching session");
        var report = recorder.Snapshot(DateTimeOffset.UtcNow);
        Check(report.GapCount >= 3 && report.Gaps.Any(g => g.Reason.Contains("250")), "Source/host gaps were hidden");
        Check(report.Frames.Length == 5 && report.Frames[1].PikaFeedbackTimeSeconds == report.Frames[0].PikaFeedbackTimeSeconds,
            "Pika sampling cadence was invented from joint/host cadence");
        Check(report.MotorEvents.Select(e => e.Kind).SequenceEqual(new[] { "free-jaws-confirmed", "measured-hold-requested", "measured-hold-confirmed", "free-jaws-requested", "free-jaws-confirmed" }) &&
            report.Frames[3].PikaMotorEnabled && !report.Frames[4].PikaMotorEnabled,
            "Free/grasp/hold/release observations lost their explicit motor events or measured motor state");
        report.Frames[0].JointDegrees[0] = 999;
        Check(recorder.Snapshot(DateTimeOffset.UtcNow).Frames[0].JointDegrees[0] == 1, "A saved report could mutate recorder evidence");
        recorder.Finish("Arm hand guiding ended");
        Check(!recorder.Observe(Frame(5, .6)) && !report.PhysicalReplayAvailable && !JsonSerializer.Serialize(report).Contains("commandedJoint"),
            "Finishing passively recorded teaching granted replay or accepted later frames");
        var changed = new PikaTeachingRecorder(identity, "disable-one", null, origin, DateTimeOffset.UnixEpoch);
        Check(!changed.Observe(Frame(1, 0) with { Identity = identity with { Generation = 2 } }) && changed.Complete,
            "A reconnected robot inherited the previous teaching recording");
        changed = new(identity, "disable-one", null, origin, DateTimeOffset.UnixEpoch);
        Check(!changed.Observe(Frame(1, 180)) && changed.Complete, "Teaching exceeded its three-minute bound");
        changed = new(identity, "disable-one", null, origin, DateTimeOffset.UnixEpoch);
        for (int n = 0; n < PikaTeachingRecorder.MaximumFrames; ++n)
            if (!changed.Observe(Frame(n + 1, n * .001))) throw new Exception("Frame cap fixture unexpectedly failed");
        Check(!changed.Observe(Frame(9001, 9.001)) && changed.Complete, "Teaching exceeded its frame/memory bound");

        // Release confirmation must remain usable during the powered-grasp
        // segment of the same recording, not disappear because it is active.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        typeof(PipersPlugin).GetField("preferences", fields)!.SetValue(plugin, RobotProfileRouting.Normalize(new()));
        changed = new(identity, "disable-one", null, origin, DateTimeOffset.UnixEpoch);
        changed.RequestMotorMode("hold-two", true, Tick(.1));
        var teachingField = typeof(PipersPlugin).GetField("pikaTeaching", fields)!;
        object teaching = teachingField.GetValue(plugin)!;
        teaching = teaching.GetType().GetMethod("<Clone>$")!.Invoke(teaching, null)!;
        teaching.GetType().GetProperty("Recorder")!.SetValue(teaching, changed);
        teaching.GetType().GetProperty("ConfirmationIdentity")!.SetValue(teaching, "pending-confirmation");
        teachingField.SetValue(plugin, teaching);
        var patches = new Dictionary<string, JsonObject>();
        var handGuiding = new JsonObject { ["fresh"] = true, ["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 1 } };
        typeof(PipersPlugin).GetMethod("AddPikaTeachingPatches", fields)!.Invoke(plugin, [patches, handGuiding]);
        Check(patches["teaching.pika.confirmation"]["visible"]?.GetValue<bool>() == true,
            "Active teaching hid the confirmation needed to free a held grasp");
        var widget = (Widget)typeof(PipersPlugin).GetMethod("PikaTeachingControls", fields)!.Invoke(plugin, null)!;
        IEnumerable<Widget> All(Widget root) => new[] { root }.Concat((root.Children ?? []).SelectMany(All));
        Widget confirmation = All(widget).Single(w => w.Id == "teaching.pika.confirmation");
        Check(confirmation.Type == "column" && All(confirmation).Single(w => w.Id == "teaching.pika.confirm").Width >= 190 &&
            All(confirmation).Single(w => w.Id == "teaching.pika.cancel").Width >= 90,
            "Release warning crowded its confirmation buttons into an unclickable row");
        var controls = (Widget)typeof(PipersPlugin).GetMethod("ControlsPage", fields)!.Invoke(plugin, null)!;
        var children = controls.Children!.ToArray();
        int teachingIndex = Array.FindIndex(children, w => w.Id == "teaching");
        Check(teachingIndex >= 0 && teachingIndex < Array.FindIndex(children, w => w.Id == "cameras") && teachingIndex < Array.FindIndex(children, w => w.Id == "joint.1"),
            "Hand-guiding actions were placed below cameras or disabled joints, making release confirmation unreachable without scrolling");
        Check(All(controls).Count(w => w.Id == "teaching.pika.free") == 1 &&
            All(controls).Single(w => w.Id == "teaching.pika.status").Height == 36 &&
            All(controls).Single(w => w.Id == "teaching.pika.saved").Visible == false,
            "Top hand-guiding actions were duplicated, oversized, or left an empty saved-path row visible");
        Check(patches["teaching.pika.saved"]["visible"]?.GetValue<bool>() == false &&
            patches["teaching.pika.status"]["hint"]?.GetValue<string>() == patches["teaching.pika.status"]["text"]?.GetValue<string>(),
            "Compact teaching status discarded its full tooltip or showed an empty saved-path row");
        ((IDisposable)typeof(PipersPlugin).GetField("channel", fields)!.GetValue(plugin)!).Dispose();
    }
}
