using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationFollowTests
{
    public static void Run(Action<bool, string> check)
    {
        const long now = 1_000_000;
        var initial = SimulationFollowSample.Read(Pose());
        var hardware = Hardware(initial);
        check(initial.CommandDegrees.Count == 6 && initial.MeasuredDegrees.Count == 6 && Math.Abs(initial.CommandOpeningMm - 20) < 1e-8,
            "Runtime follower did not read six position controls and the actual offset Pika slide mapping");
        check(Math.Abs(initial.MeasuredOpeningMm - 20) < 1e-8 && initial.BindsTo(SimulationFollowSample.Read(Pose())), "Runtime measured gripper or identity binding differs");
        check(initial.CommandDegrees is not double[] && initial.MeasuredDegrees is not double[], "Caller can mutate retained runtime target arrays");

        var policy = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        var nextJson = Pose(time: 1.01, sequence: 2, elapsedMs: 50);
        Joint(nextJson, 1)["position"] = .001;
        Actuator(nextJson, 1)["control"] = .002;
        var next = SimulationFollowSample.Read(nextJson);
        var target = policy.Advance(next, hardware, now + 50, 20);
        check(Math.Abs(target.JointDegrees[0] - .002 * 180 / Math.PI) < 1e-10 && Math.Abs(target.MeasuredJointDegrees[0] - .001 * 180 / Math.PI) < 1e-10,
            "Follow simulation substituted measured qpos for the exported position-actuator command");
        check(target.SimulationTimeSeconds == 1.01 && target.GripperOpeningMm == next.CommandOpeningMm && !policy.Stopped, "Accepted runtime target lost its timing/gripper metadata");
        var paused = Pose(); paused["mode"] = "pause";
        check(!SimulationFollowPolicy.Start(SimulationFollowSample.Read(paused), initial.Identity, hardware, now, 20).Stopped,
            "Paused initial runtime pose cannot be checked before explicit resume");

        foreach (var invalid in new (string Name, Action<JsonObject> Change)[]
        {
            ("ready", p => p["ready"] = false),
            ("missing-joint", p => ((JsonArray)p["joints"]!).RemoveAt(0)),
            ("duplicate-joint", p => Joint(p, 2)["name"] = "joint1"),
            ("radians", p => Joint(p, 1)["unit"] = "deg"),
            ("hinge", p => Joint(p, 1)["type"] = "slide"),
            ("limit", p => Joint(p, 1)["limited"] = false),
            ("joint-range", p => Joint(p, 1)["min"] = 100),
            ("nonfinite", p => Joint(p, 1)["position"] = double.NaN),
            ("missing-velocity", p => Joint(p, 1).Remove("velocity")),
            ("missing-actuator", p => ((JsonArray)p["actuators"]!).RemoveAt(0)),
            ("duplicate-actuator", p => Actuator(p, 2)["name"] = "joint1"),
            ("torque", p => Actuator(p, 1)["positionControl"] = false),
            ("wrong-target", p => Actuator(p, 1)["targetJoint"] = "joint2"),
            ("raw-control", p => Actuator(p, 1)["controlUnit"] = "raw"),
            ("tendon", p => Actuator(p, 1)["transmission"] = "tendon"),
            ("unlimited", p => Actuator(p, 1)["limited"] = false),
            ("gear", p => ((JsonArray)Actuator(p, 1)["gear"]!)[0] = 2),
            ("short-gear", p => ((JsonArray)Actuator(p, 1)["gear"]!).RemoveAt(0)),
            ("negative-gain", p => ((JsonArray)Actuator(p, 1)["gain"]!)[0] = -10),
            ("wrong-bias", p => ((JsonArray)Actuator(p, 1)["bias"]!)[1] = -15),
            ("unstable-damping", p => ((JsonArray)Actuator(p, 1)["bias"]!)[2] = 5),
            ("command-range", p => Actuator(p, 1)["control"] = 30),
            ("gripper-ranges", p => Joint(p, 8)["min"] = .004),
            ("gripper-asymmetry", p => Joint(p, 8)["position"] = .02),
            ("gripper-initial94mm", p => { Joint(p, 7)["position"] = 0; Joint(p, 8)["position"] = 0; }),
            ("gripper-command94mm", p => Actuator(p, 7)["control"] = 0),
            ("gripper-wrong-transmission", p => Actuator(p, 7)["targetJoint"] = "joint8"),
            ("clock-domain", p => p["clockDomain"] = "device"),
            ("monotonic-domain", p => p["monotonicClockDomain"] = "utc"),
            ("missing-sequence", p => p.Remove("sequence")),
            ("invalid-clock", p => p["capturedMonotonicNs"] = "NaN"),
            ("clock-overflow", p => p["simulationTimeNs"] = "9999999999999999999999999")
        })
        {
            var bad = Pose(); invalid.Change(bad);
            Reject(() => SimulationFollowSample.Read(bad), "Runtime parser accepted " + invalid.Name);
        }

        foreach (var invalid in new (string Name, Func<SimulationFollowHardware, SimulationFollowHardware> Change)[]
        {
            ("feedback", h => h with { FeedbackFresh = false }),
            ("limits", h => h with { LimitsFresh = false }),
            ("pika", h => h with { GripperFresh = false }),
            ("device", h => h with { DeviceId = "" }),
            ("generation", h => h with { Generation = 0 }),
            ("joints", h => h with { JointDegrees = [0] }),
            ("nonfinite", h => h with { JointDegrees = [double.NaN, 0, 0, 0, 0, 0] }),
            ("limit-count", h => h with { JointLimits = [h.JointLimits[0]] }),
            ("duplicate-limit", h => h with { JointLimits = Enumerable.Repeat(h.JointLimits[0], 6).ToArray() }),
            ("missing-acceleration", h => h with { JointLimits = h.JointLimits.Select(l => l with { AccelerationFresh = false }).ToArray() }),
            ("zero-speed", h => h with { JointLimits = h.JointLimits.Select(l => l with { MaximumDegreesPerSecond = 0 }).ToArray() }),
            ("unknown-acceleration", h => h with { JointLimits = h.JointLimits.Select(l => l with { MaximumDegreesPerSecondSquared = double.NaN }).ToArray() }),
            ("gripper-width", h => h with { GripperOpeningMm = 91 }),
            ("arm-start-offset", h => h with { JointDegrees = h.JointDegrees.Select((v, i) => v + (i == 0 ? 1.01 : 0)).ToArray() }),
            ("gripper-start-offset", h => h with { GripperOpeningMm = 22.01 })
        })
            Reject(() => SimulationFollowPolicy.Start(initial, initial.Identity, invalid.Change(hardware), now, 20), "Follow start accepted invalid physical " + invalid.Name);

        foreach (var identity in new[]
        {
            initial.Identity with { SourceSceneId = "other" }, initial.Identity with { RunId = "new-run" }, initial.Identity with { EntityId = "other" },
            initial.Identity with { ModelAssetId = "other" }, initial.Identity with { ModelRevision = "new-model" }, initial.Identity with { RunId = "" }
        })
            Reject(() => SimulationFollowPolicy.Start(initial, identity, hardware, now, 20), "Follow started with a changed/missing pinned identity");
        Reject(() => SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now + 251, 20), "Follow accepted an old source sample");
        Reject(() => SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now - 51, 20), "Follow accepted a future source sample");
        Reject(() => SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 151), "Follow accepted delayed IPC request");
        Reject(() => SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, double.NaN), "Follow accepted unknown IPC latency");
        var tooFast = Pose(); tooFast["timeScale"] = .21;
        Reject(() => SimulationFollowPolicy.Start(SimulationFollowSample.Read(tooFast), initial.Identity, hardware, now, 20), "Follow started above20% simulation speed");
        var nativeFloatScale = Pose(); nativeFloatScale["timeScale"] = (double).2f;
        check(!SimulationFollowPolicy.Start(SimulationFollowSample.Read(nativeFloatScale), initial.Identity, hardware, now, 20).Stopped,
            "Native float time-scale round trip rejected the intended20% setting");
        var initialRuntime = Pose(time: 0); initialRuntime["mode"] = "pause";
        check(!SimulationFollowPolicy.Start(SimulationFollowSample.Read(initialRuntime), initial.Identity, hardware, now, 20).Stopped,
            "Pause-on-play rejected its correct zero-time initial runtime snapshot");
        var edit = Pose(); edit["mode"] = "edit";
        Reject(() => SimulationFollowPolicy.Start(SimulationFollowSample.Read(edit), initial.Identity, hardware, now, 20), "Follow started from saved Edit model");

        foreach (var invalid in new (string Name, Action<JsonObject> Change)[]
        {
            ("paused", p => p["mode"] = "pause"), ("stopped", p => p["mode"] = "edit"), ("loading", p => p["mode"] = "loading"),
            ("scene", p => p["sourceSceneId"] = "other"), ("run", p => { p["runId"] = "run2"; p["sceneId"] = "run2"; }),
            ("wrong-runtime-scene", p => p["sceneId"] = "saved-scene"), ("entity", p => p["entityId"] = "other"),
            ("asset", p => p["modelAssetId"] = "other"), ("revision", p => p["modelRevision"] = "new-model"),
            ("sequence", p => p["sequence"] = 1L), ("monotonic-reset", p => p["capturedMonotonicNs"] = "9999999999"),
            ("simtime-reset", p => p["mujocoTimeSeconds"] = .5), ("time-ns-reset", p => p["simulationTimeNs"] = "500000000"),
            ("too-fast", p => p["timeScale"] = 1d), ("time-jump", p => p["mujocoTimeSeconds"] = 2d),
            ("changed-gain", p => { ((JsonArray)Actuator(p, 1)["gain"]!)[0] = 11d; ((JsonArray)Actuator(p, 1)["bias"]!)[1] = -11d; }),
            ("speed", p => Actuator(p, 1)["control"] = .03), ("acceleration", p => Actuator(p, 1)["control"] = .006),
            ("arm-not-following", p => { Actuator(p, 1)["control"] = .09; Joint(p, 1)["position"] = .09; }),
            ("pika-not-following", p => Actuator(p, 7)["control"] = -.025)
        })
        {
            var active = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
            var bad = Pose(time: 1.01, sequence: 2, elapsedMs: 50); invalid.Change(bad);
            Reject(() => active.Advance(SimulationFollowSample.Read(bad), hardware, now + 50, 20), "Active follower accepted " + invalid.Name);
            check(active.Stopped, "Runtime validation failure did not terminally stop session: " + invalid.Name);
            Reject(() => active.Advance(next, hardware, now + 50, 20), "Stopped session automatically resumed after " + invalid.Name);
        }
        foreach (var changedHardware in new[]
        {
            hardware with { DeviceId = "other" }, hardware with { Generation = 8 }, hardware with { FeedbackFresh = false }, hardware with { GripperFresh = false },
            hardware with { JointLimits = hardware.JointLimits.Select(l => l with { MaximumDegreesPerSecond = 19 }).ToArray() }
        })
        {
            var active = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
            Reject(() => active.Advance(next, changedHardware, now + 50, 20), "Runtime follower accepted hardware reconnect/stale feedback/changed limits");
            check(active.Stopped, "Hardware validation failure did not latch stopped session");
        }
        var interrupted = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        Reject(() => interrupted.Advance(SimulationFollowSample.Read(Pose(time: 1.06, sequence: 2, elapsedMs: 300)), hardware, now + 300, 20), "Interrupted follow stream resumed with queued targets");
        var explicitStop = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        explicitStop.Stop("Operator stop");
        Reject(() => explicitStop.Advance(next, hardware, now + 50, 20), "Explicit stop resumed on a later good sample");
        check(explicitStop.StopReason == "Operator stop", "Stop cause was overwritten by a late sample");
        var differentStart = hardware with { JointDegrees = hardware.JointDegrees.Select((v, i) => v + (i == 0 ? 2 : 0)).ToArray() };
        SimulationFollowPolicy.ValidateCandidate(initial, initial.Identity, differentStart, now, 20);
        bool alignmentOnly = false;
        try { SimulationFollowPolicy.Start(initial, initial.Identity, differentStart, now, 20); }
        catch (SimulationFollowAlignmentException) { alignmentOnly = true; }
        check(alignmentOnly, "A valid but mismatched start does not expose a distinct recoverable alignment error");
        Reject(() => SimulationFollowPolicy.ValidateCandidate(initial, initial.Identity, hardware, now + 251, 20), "Match target validation accepted stale source data");
        bool hardwareErrorIsAlignment = false;
        try { SimulationFollowPolicy.Start(initial, initial.Identity, hardware with { FeedbackFresh = false }, now, 20); }
        catch (SimulationFollowAlignmentException) { hardwareErrorIsAlignment = true; }
        catch (InvalidOperationException) { }
        check(!hardwareErrorIsAlignment, "A stale physical state was presented as a repairable pose mismatch");

        var catchUp = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        catchUp.PauseTracking();
        var pausedCatchUp = Pose(time: 1.01, sequence: 2, elapsedMs: 50); pausedCatchUp["mode"] = "pause";
        // A contact grasp legitimately has a smaller commanded opening than its measured width.
        Joint(pausedCatchUp, 7)["position"] = -.0470221912391186 + .043 / 2;
        Joint(pausedCatchUp, 8)["position"] = .0470221912391186 - .043 / 2;
        Actuator(pausedCatchUp, 7)["control"] = -.0470221912391186 + .036 / 2;
        var grippingHardware = hardware with { GripperOpeningMm = 43 };
        catchUp.ValidatePaused(SimulationFollowSample.Read(pausedCatchUp), grippingHardware, now + 50, 20);
        check(catchUp.IsPauseTracking && !catchUp.Stopped, "Owned catch-up pause rejected a bounded seven-mm contact-closing target gap");
        var laterPaused = (JsonObject)pausedCatchUp.DeepClone();
        laterPaused["capturedMonotonicNs"] = "11000000000"; laterPaused["capturedAtUnixMicroseconds"] = "1001000000"; laterPaused["sequence"] = 3L;
        catchUp.ValidatePaused(SimulationFollowSample.Read(laterPaused), grippingHardware, now + 1000, 20);
        catchUp.ResumeAfterPause(SimulationFollowSample.Read(laterPaused), grippingHardware, now + 1000, 20);
        check(!catchUp.IsPauseTracking && !catchUp.Stopped, "Catch-up resume incorrectly reran empty-jaw start alignment");
        var resumed = (JsonObject)laterPaused.DeepClone(); resumed["mode"] = "play";
        resumed["capturedMonotonicNs"] = "11050000000"; resumed["capturedAtUnixMicroseconds"] = "1001050000"; resumed["sequence"] = 4L;
        resumed["mujocoTimeSeconds"] = 1.02; resumed["simulationTimeNs"] = "1020000000";
        var resumedTarget = catchUp.Advance(SimulationFollowSample.Read(resumed), grippingHardware, now + 1050, 20);
        check(!catchUp.Stopped && Math.Abs(resumedTarget.GripperOpeningMm - 36) < 1e-8, "Catch-up pause duration corrupted the resumed derivative/timing baseline");

        var repeatedStep = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        var sameStep = SimulationFollowSample.Read(Pose(time: 1, sequence: 2, elapsedMs: 30));
        check(repeatedStep.TryValidateUnchanged(sameStep, hardware, now + 30, 20) && !repeatedStep.Stopped,
            "Polling faster than20%-speed physics rejected a fresh identical native step");
        var advancedAfterRepeat = Pose(time: 1.01, sequence: 3, elapsedMs: 60);
        Actuator(advancedAfterRepeat, 1)["control"] = .004;
        var advancedSample = SimulationFollowSample.Read(advancedAfterRepeat);
        check(!repeatedStep.TryValidateUnchanged(advancedSample, hardware, now + 60, 20), "An advanced physics step was classified as unchanged");
        check(repeatedStep.Advance(advancedSample, hardware, now + 60, 20).JointDegrees[0] > 0 && !repeatedStep.Stopped,
            "An unchanged poll reset the derivative baseline instead of spanning the actual wall interval");
        var independentClocks = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        foreach (var (elapsed, sceneNs, observation) in new[] { (30L, 1_006_666_667L, 2L), (60L, 1_013_333_333L, 3L), (90L, 1_016_666_667L, 4L) })
        {
            var cameraEvent = Pose(time: 1, sequence: observation, elapsedMs: elapsed);
            cameraEvent["simulationTimeNs"] = sceneNs.ToString();
            check(independentClocks.TryValidateUnchanged(SimulationFollowSample.Read(cameraEvent), hardware, now + elapsed, 20),
                "A camera-only scene event was treated as a new/invalid MuJoCo physics step");
        }
        var actualStep = Pose(time: 1.02, sequence: 5, elapsedMs: 100);
        actualStep["simulationTimeNs"] = "1016666667"; // Physics may advance while the scene clock is unchanged.
        Actuator(actualStep, 1)["control"] = .005;
        var actualSample = SimulationFollowSample.Read(actualStep);
        check(!independentClocks.TryValidateUnchanged(actualSample, hardware, now + 100, 20) &&
            independentClocks.Advance(actualSample, hardware, now + 100, 20).JointDegrees[0] > 0 && !independentClocks.Stopped,
            "Independent scene/physics clocks rebased derivatives at camera polls or rejected a held scene timestamp");
        var cameraStall = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        foreach (long elapsed in new[] { 80L, 160L, 240L })
        {
            var cameraEvent = Pose(time: 1, sequence: elapsed, elapsedMs: elapsed);
            cameraEvent["simulationTimeNs"] = (1_000_000_000L + elapsed * 200_000).ToString();
            check(cameraStall.TryValidateUnchanged(SimulationFollowSample.Read(cameraEvent), hardware, now + elapsed, 20),
                "Fresh unchanged physics did not survive independent scene progress within250ms");
        }
        var stalled = Pose(time: 1, sequence: 300, elapsedMs: 270); stalled["simulationTimeNs"] = "1054000000";
        Reject(() => cameraStall.TryValidateUnchanged(SimulationFollowSample.Read(stalled), hardware, now + 270, 20),
            "Camera progress reset the250ms true-physics stall deadline");
        check(cameraStall.Stopped, "True-physics stall did not terminally retire following");
        foreach (bool advancePhysics in new[] { false, true })
        {
            var clockRegression = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
            var cameraEvent = Pose(time: 1, sequence: 2, elapsedMs: 30); cameraEvent["simulationTimeNs"] = "1016666667";
            check(clockRegression.TryValidateUnchanged(SimulationFollowSample.Read(cameraEvent), hardware, now + 30, 20), "Clock regression setup failed");
            var backwards = Pose(time: advancePhysics ? 1.01 : 1, sequence: 3, elapsedMs: 60);
            backwards["simulationTimeNs"] = "1010000000"; // Above the last physics frame, below the last camera observation.
            Reject(() => { var value = SimulationFollowSample.Read(backwards); if (!clockRegression.TryValidateUnchanged(value, hardware, now + 60, 20)) clockRegression.Advance(value, hardware, now + 60, 20); },
                "A scene clock regression was hidden by the last-physics baseline");
            check(clockRegression.StopReason.Contains("scene clock regressed") && clockRegression.StopReason.Contains("MuJoCo seconds") &&
                clockRegression.StopReason.Contains("sequence"), "Timing failure omitted the actual clock predicate and previous/current evidence");
        }
        foreach (var invalid in new (string Name, Action<JsonObject> Change, long Elapsed)[]
        {
            ("zero-time-command", p => Actuator(p, 1)["control"] = .001, 60),
            ("zero-time-qpos", p => Joint(p, 1)["position"] = .001, 60),
            ("unchanged-pause", p => p["mode"] = "pause", 60),
            ("unchanged-sequence-rollback", p => p["sequence"] = 2L, 60),
            ("stalled-physics", _ => { }, 270)
        })
        {
            var active = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
            check(active.TryValidateUnchanged(sameStep, hardware, now + 30, 20), "Set up an unchanged poll before corruption");
            var bad = Pose(time: 1, sequence: 3, elapsedMs: invalid.Elapsed); invalid.Change(bad);
            Reject(() => active.TryValidateUnchanged(SimulationFollowSample.Read(bad), hardware, now + invalid.Elapsed, 20), "Unchanged sample accepted " + invalid.Name);
            check(active.Stopped, "Unchanged-step validation failure did not latch Stop: " + invalid.Name);
        }

        foreach (var invalid in new (string Name, Action<JsonObject> Change)[]
        {
            ("manual-resume", p => p["mode"] = "play"),
            ("paused-time-change", p => p["mujocoTimeSeconds"] = 1.02),
            ("paused-ns-change", p => p["simulationTimeNs"] = "1020000000"),
            ("paused-target-change", p => Actuator(p, 1)["control"] = .001),
            ("paused-qpos-change", p => Joint(p, 1)["position"] = .001),
            ("paused-new-run", p => { p["runId"] = "other"; p["sceneId"] = "other"; }),
            ("paused-time-rollback", p => p["capturedMonotonicNs"] = "1"),
            ("catch-up-timeout", p => { p["capturedMonotonicNs"] = "14100000000"; p["capturedAtUnixMicroseconds"] = "1004100000"; })
        })
        {
            var active = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20); active.PauseTracking();
            active.ValidatePaused(SimulationFollowSample.Read(pausedCatchUp), grippingHardware, now + 50, 20);
            var bad = (JsonObject)laterPaused.DeepClone(); invalid.Change(bad);
            long receipt = invalid.Name == "catch-up-timeout" ? now + 4100 : now + 1000;
            Reject(() => active.ValidatePaused(SimulationFollowSample.Read(bad), grippingHardware, receipt, 20), "Catch-up accepted " + invalid.Name);
            check(active.Stopped, "Catch-up validation failure did not latch stopped state: " + invalid.Name);
        }
        var unownedPause = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20);
        Reject(() => unownedPause.ValidatePaused(SimulationFollowSample.Read(pausedCatchUp), grippingHardware, now + 50, 20), "An arbitrary manual pause was treated as owned catch-up");
        var earlyResume = SimulationFollowPolicy.Start(initial, initial.Identity, hardware, now, 20); earlyResume.PauseTracking();
        Reject(() => earlyResume.Advance(next, hardware, now + 50, 20), "Play input automatically resumed an explicitly paused follow session");
        Reject(() => earlyResume.PauseTracking(), "A stopped follow session was revived by catch-up pause");

        void Reject(Action action, string reason)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, reason);
        }
    }

    private static SimulationFollowHardware Hardware(SimulationFollowSample sample) => new("can-a", 7, true, true, true, sample.MeasuredDegrees,
        Enumerable.Range(1, 6).Select(i => new EpisodeReplayJointLimit(i, i == 2 ? 0 : -180, i == 3 ? 0 : 180, 20, 100, true)).ToArray(), sample.MeasuredOpeningMm);
    private static JsonObject Joint(JsonObject sample, int number) => ((JsonArray)sample["joints"]!).OfType<JsonObject>().Single(j => j["name"]!.GetValue<string>() == "joint" + number);
    private static JsonObject Actuator(JsonObject sample, int number) => ((JsonArray)sample["actuators"]!).OfType<JsonObject>().Single(j => j["name"]!.GetValue<string>() == (number == 7 ? "gripper" : "joint" + number));
    internal static JsonObject Pose(double time = 1, long sequence = 1, long elapsedMs = 0)
    {
        var joints = new JsonArray(); var actuators = new JsonArray();
        double[] qpos = [0, .2, -.3, .1, .2, -.1, -.0370221912391186, .0370221912391186];
        for (int i = 1; i <= 8; i++)
        {
            double min = i switch { 2 => 0, 7 => -.0470221912391186, 8 => .0020221912391186, _ => -Math.PI };
            double max = i switch { 3 => 0, 7 => -.0020221912391186, 8 => .0470221912391186, _ => Math.PI };
            joints.Add(new JsonObject { ["name"] = "joint" + i, ["type"] = i <= 6 ? "hinge" : "slide", ["unit"] = i <= 6 ? "rad" : "m",
                ["position"] = qpos[i - 1], ["velocity"] = 0d, ["limited"] = true, ["min"] = min, ["max"] = max });
            if (i > 7) continue;
            actuators.Add(new JsonObject { ["name"] = i == 7 ? "gripper" : "joint" + i, ["control"] = qpos[i - 1], ["positionControl"] = true,
                ["controlUnit"] = i <= 6 ? "rad" : "m", ["targetJoint"] = "joint" + i, ["transmission"] = "joint", ["limited"] = true, ["min"] = min, ["max"] = max,
                ["gear"] = new JsonArray(1d, 0d, 0d, 0d, 0d, 0d), ["gain"] = new JsonArray(10d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d),
                ["bias"] = new JsonArray(0d, -10d, -1d, 0d, 0d, 0d, 0d, 0d, 0d, 0d) });
        }
        return new JsonObject { ["mode"] = "play", ["ready"] = true, ["sourceSceneId"] = "source-a", ["sceneId"] = "run-a", ["runId"] = "run-a",
            ["entityId"] = "robot-a", ["modelAssetId"] = "asset-a", ["modelRevision"] = "model-a", ["sequence"] = sequence,
            ["capturedMonotonicNs"] = (10_000_000_000L + elapsedMs * 1_000_000L).ToString(),
            ["simulationTimeNs"] = ((long)Math.Round(time * 1e9)).ToString(), ["capturedAtUnixMicroseconds"] = (1_000_000_000L + elapsedMs * 1000).ToString(),
            ["clockDomain"] = "host-utc", ["monotonicClockDomain"] = "native-steady-clock", ["mujocoTimeSeconds"] = time, ["timeScale"] = .2,
            ["joints"] = joints, ["actuators"] = actuators };
    }
}
