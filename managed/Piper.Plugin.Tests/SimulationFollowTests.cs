using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class SimulationFollowTests
{
    public static void Run()
    {
        const long now = 1_000_000;
        var initial = SimulationFollowSample.Read(Pose());
        var hardware = Hardware(initial);
        Check(initial.CommandDegrees.Count == 6 && initial.MeasuredDegrees.Count == 6 && Math.Abs(initial.CommandOpeningMm - 20) < 1e-8,
            "Runtime follower did not read six position controls and the actual offset Pika slide mapping");
        Check(Math.Abs(initial.MeasuredOpeningMm - 20) < 1e-8 && initial.BindsTo(SimulationFollowSample.Read(Pose())), "Runtime measured gripper or identity binding differs");
        Check(initial.CommandDegrees is not double[] && initial.MeasuredDegrees is not double[], "Caller can mutate retained runtime target arrays");

        var paused = Pose(); paused["mode"] = "pause";
        SimulationFollowPolicy.ValidateStart(SimulationFollowSample.Read(paused), initial.Identity, hardware, now, 20);
        Check(true, "Paused initial runtime pose cannot be checked before explicit resume");

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
            ("gripper-initial 94mm", p => { Joint(p, 7)["position"] = 0; Joint(p, 8)["position"] = 0; }),
            ("gripper-command 94mm", p => Actuator(p, 7)["control"] = 0),
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
            Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, invalid.Change(hardware), now, 20), "Follow start accepted invalid physical " + invalid.Name);

        foreach (var identity in new[]
        {
            initial.Identity with { SourceSceneId = "other" }, initial.Identity with { RunId = "new-run" }, initial.Identity with { EntityId = "other" },
            initial.Identity with { ModelAssetId = "other" }, initial.Identity with { ModelRevision = "new-model" }, initial.Identity with { RunId = "" }
        })
            Reject(() => SimulationFollowPolicy.ValidateStart(initial, identity, hardware, now, 20), "Follow started with a changed/missing pinned identity");
        Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, hardware, now + 251, 20), "Follow accepted an old source sample");
        Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, hardware, now - 51, 20), "Follow accepted a future source sample");
        Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, hardware, now, 151), "Follow accepted delayed IPC request");
        Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, hardware, now, double.NaN), "Follow accepted unknown IPC latency");
        var tooFast = Pose(); tooFast["timeScale"] = .21;
        Reject(() => SimulationFollowPolicy.ValidateStart(SimulationFollowSample.Read(tooFast), initial.Identity, hardware, now, 20), "Follow started above 20% simulation speed");
        var nativeFloatScale = Pose(); nativeFloatScale["timeScale"] = (double).2f;
        SimulationFollowPolicy.ValidateStart(SimulationFollowSample.Read(nativeFloatScale), initial.Identity, hardware, now, 20);
        Check(true, "Native float time-scale round trip rejected the intended 20% setting");
        var initialRuntime = Pose(time: 0); initialRuntime["mode"] = "pause";
        SimulationFollowPolicy.ValidateStart(SimulationFollowSample.Read(initialRuntime), initial.Identity, hardware, now, 20);
        Check(true, "Pause-on-play rejected its correct zero-time initial runtime snapshot");
        var edit = Pose(); edit["mode"] = "edit";
        Reject(() => SimulationFollowPolicy.ValidateStart(SimulationFollowSample.Read(edit), initial.Identity, hardware, now, 20), "Follow started from saved Edit model");
        var differentStart = hardware with { JointDegrees = hardware.JointDegrees.Select((v, i) => v + (i == 0 ? 2 : 0)).ToArray() };
        SimulationFollowPolicy.ValidateCandidate(initial, initial.Identity, differentStart, now, 20);
        Reject(() => SimulationFollowPolicy.ValidateStart(initial, initial.Identity, differentStart, now, 20),
            "A valid but mismatched start was admitted without matching the poses first");
        Reject(() => SimulationFollowPolicy.ValidateCandidate(initial, initial.Identity, hardware, now + 251, 20), "Match target validation accepted stale source data");

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
