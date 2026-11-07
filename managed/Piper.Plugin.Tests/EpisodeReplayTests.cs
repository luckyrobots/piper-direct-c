using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class EpisodeReplayTests
{
    internal static EpisodeReplayArtifact ValidArtifact() => EpisodeReplayReader.Parse(Fixture().ToJsonString());

    public static async Task RunFiles(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "piper-replay-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "episode.json");
        try
        {
            await File.WriteAllTextAsync(file, Fixture().ToJsonString());
            var loaded = await PipersPlugin.ReadReplayFile(file, CancellationToken.None);
            check(loaded.ArtifactId == "episode-a" && loaded.Samples.Count == 2 && loaded.TubeStart.LeftMm == 700,
                "Native replay preparation could not load the versioned export and object placement");
            await File.WriteAllTextAsync(file, Fixture().ToJsonString().Replace(EpisodeReplayReader.Kind, "lucky.piper.episode-start", StringComparison.Ordinal));
            bool rejected = false;
            try { await PipersPlugin.ReadReplayFile(file, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Replay preparation accepted an unfinished start manifest");
            using (var oversized = new FileStream(file, FileMode.Open, FileAccess.Write))
                oversized.SetLength(EpisodeReplayReader.MaximumBytes + 1L);
            rejected = false;
            try { await PipersPlugin.ReadReplayFile(file, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "Replay preparation loaded a file beyond the bounded JSON input size");
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }

    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string description)
        {
            try { action(); }
            catch (InvalidOperationException) { check(true, description); return; }
            check(false, description);
        }
        EpisodeReplayArtifact Read(JsonObject value) => EpisodeReplayReader.Parse(value.ToJsonString());
        var artifact = Read(Fixture());
        var repeatedSync = Fixture();
        repeatedSync["samples"]![1]!["syncIndex"] = 12;
        check(Read(repeatedSync).Samples[1].SyncIndex == 12 && artifact.NominalCaptureHz == 50,
            "50Hz Validation samples were incorrectly restricted to unique 10Hz sync-point indices");
        check(artifact.Samples[0].JointDegrees[5] == 60 && artifact.Samples[1].JointDegrees[0] == 20,
            "Replay reader converted explicit degree samples to radians or changed their order");
        check(artifact.TubeStart.LeftMm == 700 && Math.Abs(artifact.TubeStart.WorldPositionMetres.Z + .15) < 1e-9 &&
            Math.Abs(artifact.TubeStart.WorldPositionMetres.Y - .8465) < 1e-9 && artifact.TubeDimensions.MassSource == "estimated" &&
            artifact.Audit.Seed == 15093 && artifact.NativeRecording.EpisodeOrdinal is null,
            "Replay lost initial object placement, tabletop-relative height or mass provenance");

        foreach (string failure in new[] { "version", "mode", "convention", "names", "source", "angles", "first-time", "duplicate-time", "backward-time", "grip", "world-units", "left-units", "raw-actions", "command-source", "command-angles", "command-units", "command-clock", "actual-opening", "actuator-proof" })
        {
            var bad = Fixture();
            switch (failure)
            {
                case "version": bad["version"] = 3; break;
                case "mode": bad["mode"] = "real"; break;
                case "convention": bad["robot"]!["jointConvention"] = "radians"; break;
                case "names": bad["robot"]!["jointNames"]![0] = "joint6"; break;
                case "source": bad["samples"]![1]!["source"] = "torque-state"; break;
                case "angles": bad["samples"]![1]!["jointDegrees"]!.AsArray().RemoveAt(5); break;
                case "first-time": bad["samples"]![0]!["timeSeconds"] = .1; break;
                case "duplicate-time": bad["samples"]![1]!["timeSeconds"] = 0; break;
                case "backward-time": bad["samples"]![1]!["timeSeconds"] = -1; break;
                case "grip": bad["samples"]![1]!["gripperOpeningMm"] = 95; break;
                case "world-units": bad["tubeStart"]!["worldPositionMetres"]!["z"] = -150; break;
                case "left-units": bad["tubeStart"]!["leftMm"] = .7; break;
                case "raw-actions": bad["samples"]![1]!["torques"] = new JsonArray(1, 2, 3, 4, 5, 6); break;
                case "command-source": bad["samples"]![1]!["commandSource"] = "raw-torque-ctrl"; break;
                case "command-angles": bad["samples"]![1]!["commandedJointDegrees"]!.AsArray().RemoveAt(5); break;
                case "command-units": bad["robot"]!["commandConvention"] = "radians-and-raw-finger-slides"; break;
                case "command-clock": bad["commandTimestampConvention"] = "interpolated-without-declaration"; break;
                case "actual-opening": bad["robot"]!["gripperMaximumOpeningMm"] = 94.044; break;
                case "actuator-proof": bad["robot"]!["positionActuatorProvenance"]!["sourceSha256"] = "unverified"; break;
            }
            Reject(() => Read(bad), "Replay accepted malformed/ambiguous " + failure);
        }
        string json = Fixture().ToJsonString();
        Reject(() => EpisodeReplayReader.Parse(json.Replace("\"timeSeconds\":1", "\"timeSeconds\":1e400", StringComparison.Ordinal)),
            "Replay accepted an infinite timestamp");
        Reject(() => EpisodeReplayReader.Parse(json.Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal)),
            "Replay accepted duplicate JSON fields");
        Reject(() => EpisodeReplayReader.Parse(new string(' ', EpisodeReplayReader.MaximumBytes + 1)),
            "Replay accepted an unbounded input file");

        var hardware = Hardware();
        var legacy = Read(Fixture(1));
        check(!legacy.HasPositionCommands && legacy.Samples[0].CommandedJointDegrees is null,
            "Legacy measured-only samples were promoted to executable commands");
        Reject(() => EpisodeReplayPlanner.Build(legacy, hardware), "Version1 measured-only export was treated as an actuator replay");
        var oldestLegacy = Fixture(1);
        oldestLegacy["nativeRecording"]!.AsObject().Remove("nativeOrdinalSource");
        check(!Read(oldestLegacy).HasPositionCommands, "Earlier version1 files lost review support after provenance was introduced");
        var plan = EpisodeReplayPlanner.Build(artifact, hardware);
        check(plan.ReadyToStart && !plan.TimingChanged && plan.DurationSeconds == 1 && plan.Samples.Count == 2 &&
            plan.Samples[0].JointDegrees.SequenceEqual(artifact.Samples[0].JointDegrees) && plan.Artifact.Audit.InitialSimulationState.MujocoQposForAudit[0] == 1234,
            "Replay inserted a zero/home/audit-qpos target or changed valid timing");
        var invalidCapture = Fixture();
        invalidCapture["outcome"] = "invalid-capture: lost sample";
        Reject(() => Read(invalidCapture), "Replay accepted a capture explicitly exported as invalid");
        var drifted = hardware with { JointDegrees = new double[] { 11, 20, -30, 40, 50, 60 }, GripperOpeningMm = 2 };
        var blocked = EpisodeReplayPlanner.Build(artifact, drifted);
        check(!blocked.ReadyToStart && blocked.StartDifferences.Count == 2 && blocked.StartDifferences[0] is { Axis: "J1", Actual: 11, Required: 10, Unit: "deg" } &&
            blocked.Samples.Count == artifact.Samples.Count && blocked.Samples[0].JointDegrees[0] == 10,
            "Initial pose drift silently produced an alignment move or ignored the physical gripper");
        check(EpisodeReplayPlanner.CheckStart(blocked, hardware).Count == 0,
            "Fresh realignment could not clear the start mismatch without rebuilding targets");
        Reject(() => EpisodeReplayPlanner.CheckStart(plan, hardware with { Generation = 2 }), "Replay survived changed adapter generation");
        Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with { FeedbackFresh = false }), "Replay accepted stale joint feedback");
        Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with { LimitsFresh = false }), "Replay substituted fallback joint limits");
        Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with { JointLimits = hardware.JointLimits.Select(limit => limit with { AccelerationFresh = false }).ToArray() }),
            "Replay accepted stale acceleration reports");
        foreach (double invalidAcceleration in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with
                { JointLimits = hardware.JointLimits.Select(limit => limit with { MaximumDegreesPerSecondSquared = invalidAcceleration }).ToArray() }),
                "Replay invented a positive acceleration cap for an invalid report");
        Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with { GripperLimitsVerified = false }), "Replay invented a Pika speed limit");
        Reject(() => EpisodeReplayPlanner.Build(artifact, hardware with { GripperConvention = "raw-motor-angle" }), "Replay accepted raw motor angle as jaw opening");
        var outOfRange = Fixture();
        outOfRange["samples"]![1]!["commandedJointDegrees"]![0] = 151;
        Reject(() => EpisodeReplayPlanner.Build(Read(outOfRange), hardware, new(AllowTimeScaling: true)),
            "Replay time scaling silently clipped a joint target outside reported limits");
        var wideGrip = Fixture();
        wideGrip["samples"]![1]!["commandedGripperOpeningMm"] = 92;
        Reject(() => EpisodeReplayPlanner.Build(Read(wideGrip), hardware, new(AllowTimeScaling: true)),
            "A wider simulated jaw range bypassed the physical Pika opening limit");

        var fastJson = Fixture();
        fastJson["samples"]![1]!["timeSeconds"] = .1;
        var fast = Read(fastJson);
        Reject(() => EpisodeReplayPlanner.Build(fast, hardware), "Replay silently stretched excessive sample rates without opt-in");
        var scaled = EpisodeReplayPlanner.Build(fast, hardware, new(AllowTimeScaling: true));
        check(scaled.TimingChanged && Math.Abs(scaled.TimeScale - 5) < 1e-9 && Math.Abs(scaled.DurationSeconds - .5) < 1e-9 &&
            scaled.Samples[1].JointDegrees.SequenceEqual(fast.Samples[1].JointDegrees) && scaled.Samples[1].GripperOpeningMm == 20 &&
            scaled.Notes[0].Contains("stretched", StringComparison.Ordinal),
            "Explicit replay time scaling did not preserve every angle/opening or account for gripper rate");
        Reject(() => EpisodeReplayPlanner.Build(fast, hardware, new(AllowTimeScaling: true, MaximumTimeScale: 4)),
            "Replay exceeded the explicit maximum time-scale setting");
        var slowerLimits = hardware.JointLimits.Select(limit => limit with { MaximumDegreesPerSecond = 5 }).ToArray();
        Reject(() => EpisodeReplayPlanner.CheckStart(plan, hardware with { JointLimits = slowerLimits }),
            "Replay ignored new slower reported limits between planning and starting");

        var reversingJson = Fixture();
        var third = reversingJson["samples"]![1]!.DeepClone();
        third["timeSeconds"] = 2;
        third["syncIndex"] = 32;
        third["commandedJointDegrees"]![0] = 10;
        reversingJson["samples"]!.AsArray().Add(third);
        var reversing = Read(reversingJson);
        var accelerationLimits = hardware.JointLimits.Select(limit => limit with { MaximumDegreesPerSecondSquared = 5 }).ToArray();
        var accelerationHardware = hardware with { JointLimits = accelerationLimits };
        var timing = EpisodeReplayPlanner.AssessJointTiming(reversing, accelerationLimits);
        check(timing.SpeedTimeScale == 1 && Math.Abs(timing.AccelerationTimeScale - 2) < 1e-12 && timing.RequiredTimeScale == 2,
            "Low-velocity direction reversal bypassed the acceleration cap or omitted square-root time scaling");
        Reject(() => EpisodeReplayPlanner.Build(reversing, accelerationHardware),
            "Acceleration-limited replay silently slowed down without explicit consent");
        var accelerationPlan = EpisodeReplayPlanner.Build(reversing, accelerationHardware, new(AllowTimeScaling: true));
        check(accelerationPlan.TimeScale == 2 && accelerationPlan.DurationSeconds == 4 &&
            accelerationPlan.Samples.Select(sample => sample.JointDegrees[0]).SequenceEqual(new[] { 10.0, 20.0, 10.0 }) &&
            accelerationPlan.Samples.Select(sample => sample.GripperOpeningMm).SequenceEqual(reversing.Samples.Select(sample => sample.CommandedGripperOpeningMm!.Value)),
            "Acceleration planning smoothed, clipped or replaced zero-order-held position targets");
        check(EpisodeReplayPlanner.CheckStart(accelerationPlan, accelerationHardware).Count == 0,
            "Uniform acceleration-aware timing did not satisfy the unchanged reported caps");
        Reject(() => EpisodeReplayPlanner.CheckStart(accelerationPlan, accelerationHardware with
            { JointLimits = accelerationLimits.Select(limit => limit with { MaximumDegreesPerSecondSquared = 2.5 }).ToArray() }),
            "Replay start ignored an acceleration-cap reduction after planning");
        reversingJson["samples"]![2]!["timeSeconds"] = 3;
        var irregularTiming = EpisodeReplayPlanner.AssessJointTiming(Read(reversingJson), accelerationLimits.Select(limit => limit with { MaximumDegreesPerSecondSquared = 2.5 }).ToArray());
        check(Math.Abs(irregularTiming.AccelerationTimeScale - 2) < 1e-12,
            "Acceleration used nominal sample indices instead of midpoint spacing for irregular timestamps");

        var reported = new JsonArray(hardware.JointLimits.Select(limit => (JsonNode)new JsonObject
        {
            ["min_deg"] = limit.MinimumDegrees, ["max_deg"] = limit.MaximumDegrees,
            ["max_speed_rad_s"] = limit.MaximumDegreesPerSecond * Math.PI / 180,
            ["max_acceleration_rad_s2"] = .5, ["limits_fresh"] = true, ["acceleration_fresh"] = true
        }).ToArray());
        var readLimits = EpisodeReplayReportedLimits.Read(reported);
        check(readLimits.All(limit => limit.AccelerationFresh && Math.Abs(limit.MaximumDegreesPerSecondSquared - .5 * 180 / Math.PI) < 1e-12),
            "Replay UI did not preserve the backend's reported rad/s² acceleration units");
        foreach (string key in new[] { "acceleration_fresh", "max_acceleration_rad_s2" })
        {
            var missing = reported.DeepClone().AsArray();
            missing[3]!.AsObject().Remove(key);
            Reject(() => EpisodeReplayReportedLimits.Read(missing), "Replay UI substituted a default for missing " + key);
        }
        reported[1]!["acceleration_fresh"] = false;
        Reject(() => EpisodeReplayReportedLimits.Read(reported), "Replay UI accepted one stale acceleration report");
        var measuredDifferent = Fixture();
        measuredDifferent["samples"]![1]!["jointDegrees"]![0] = 15;
        measuredDifferent["samples"]![1]!["gripperOpeningMm"] = 17;
        var commandPlan = EpisodeReplayPlanner.Build(Read(measuredDifferent), hardware);
        check(commandPlan.Samples[1].JointDegrees[0] == 20 && commandPlan.Samples[1].GripperOpeningMm == 20 &&
            commandPlan.Artifact.Samples[1].JointDegrees[0] == 15 && commandPlan.Artifact.Samples[1].GripperOpeningMm == 17 &&
            commandPlan.Samples[1].Source == EpisodeReplayReader.CommandSource,
            "Physical planner substituted measured observations for position-controller actions");
        var initialJump = Fixture();
        initialJump["samples"]![0]!["commandedJointDegrees"]![0] = 12;
        var jump = EpisodeReplayPlanner.Build(Read(initialJump), hardware);
        check(!jump.ReadyToStart && jump.StartDifferences.Count == 0 && jump.InitialCommandDifferences.Count == 1 &&
            jump.InitialCommandDifferences[0] is { Actual: 10, Required: 12 },
            "First command jump was confused with physical alignment to the measured source start");
        Reject(() => EpisodeReplayPlanner.CheckStart(jump, hardware), "Start recheck bypassed the initial command jump block");
        initialJump["samples"]![0]!["commandedJointDegrees"]![0] = 10.4;
        var smallJump = EpisodeReplayPlanner.Build(Read(initialJump), hardware);
        check(EpisodeReplayPlanner.CheckStart(smallJump, hardware with { JointDegrees = new double[] { 10.8, 20, -30, 40, 50, 60 } }).Count == 1,
            "Physical start was checked against the first command instead of the measured source pose");
        foreach (string missing in new[] { "commandedJointDegrees", "commandedGripperOpeningMm", "commandSource" })
        {
            var bad = Fixture();
            bad["samples"]![1]!.AsObject().Remove(missing);
            Reject(() => Read(bad), "Version2 silently substituted observations for missing " + missing);
        }

        const double gap = 94.04438247823715;
        var mapping = new EpisodeReplayGripperMapping(-gap / 2000, 0, 0, gap / 2000);
        check(Math.Abs(mapping.OpeningMm(0, 0) - gap) < 1e-9 && mapping.OpeningMm(-gap / 2000, gap / 2000) == 0 &&
            Math.Abs(mapping.OpeningMm(-(gap - 40) / 2000, (gap - 40) / 2000) - 40) < 1e-9,
            "Replay Pika inverse mapping confused open-at-zero slides with a closed jaw");
        Reject(() => new EpisodeReplayGripperMapping(0, .04, 0, .04), "Replay accepted same-direction finger slide ranges");
        Reject(() => new EpisodeReplayGripperMapping(-.04, 0, 0, .03), "Replay accepted unequal finger travels");
        Reject(() => mapping.OpeningMm(-.01, .02), "Replay accepted asymmetric measured finger positions");
        Reject(() => mapping.OpeningMm(-.05, .05), "Replay silently clipped a physical-sized finger overshoot");
        var capped = new EpisodeReplayGripperMapping(-gap / 2000, -(gap - 90) / 2000, (gap - 90) / 2000, gap / 2000);
        check(Math.Abs(capped.GeometricZeroOpeningMm - gap) < 1e-9 && Math.Abs(capped.MaximumOpeningMm - 90) < 1e-9 &&
            Math.Abs(capped.OpeningMm(-.020, .02044) - (gap - 40.44)) < 1e-9,
            "Replay lost the geometric jaw baseline or rounded away an allowed contact-induced finger asymmetry");
        Reject(() => capped.OpeningMm(0, 0), "Task-model opening cap was bypassed using its out-of-range geometric zero");
        var zUp = Fixture();
        zUp["tubeStart"]!["worldQuaternionXyzw"] = new JsonArray(-.6830127018922193, .1830127018922193, .1830127018922193, .6830127018922193);
        check(Math.Abs(Read(zUp).TubeStart.YawDeg - 30) < 1e-9, "Native Z-up tube yaw was interpreted as an engine Y-up Euler yaw");
    }

    private static JsonObject Fixture(int version = 2)
    {
        var value = JsonNode.Parse("""
        {
          "kind":"lucky.piper.joint-trajectory","version":1,"mode":"simulation","artifactId":"episode-a","sessionId":"session-a",
          "createdUtc":"2026-11-03T00:00:00Z","seed":15093,"episodeOrdinal":0,
          "robot":{"model":"agilex.piper","jointConvention":"piper-j1-j6-degrees-v1","jointNames":["joint1","joint2","joint3","joint4","joint5","joint6"],
            "gripperConvention":"pika-opening-mm-v1","gripperMapping":"pika-opposed-slides-open-at-zero-v1","gripperMaximumOpeningMm":94.04438247823715,
            "actionSource":"not included; replay samples are measured qpos observations, not controller targets"},
          "desk":{"widthMm":1400,"depthMm":700,"surfaceYMetres":0.75,"frame":"engine-world-y-up-centred-xz-back-negative-z"},
          "tubeStart":{"leftMm":700,"backMm":200,"yawDeg":30,"worldPositionMetres":{"x":0,"y":0.8465,"z":-0.15},
            "worldQuaternionXyzw":[0,0.2588190451,0,0.9659258263],"source":"settled simulation body immediately before episode begin"},
          "tubeDimensions":{"diameterMm":43,"heightMm":193,"massKg":0.18,"massSource":"estimated"},
          "goal":{"leftMm":903,"backMm":350,"placementToleranceMm":25},
          "nativeRecording":{"requested":false,"sessionLabel":"","episodeOrdinal":null,
            "sessionLabelSource":"operator supplied; not discovered from native Recorder","taskIndex":0,"taskSessionId":"session-a",
            "nativeOrdinalSource":"task_owned_session_counter_not_engine_reported"},
          "initialSimulationState":{"mujocoQpos":[1234],
            "qposConvention":"native MuJoCo model order: hinges radians, slides/free translations metres, free quaternions wxyz; not a hardware command",
            "worldEntityId":"1","syncIndex":12},
          "outcome":"success","outcomeSource":"operator-confirmed; native recorder finalization may independently reject an episode",
          "samples":[
            {"timeSeconds":0,"jointDegrees":[10,20,-30,40,50,60],"gripperOpeningMm":0,"syncIndex":12,"source":"simulation-measured-qpos"},
            {"timeSeconds":1,"jointDegrees":[20,20,-30,40,50,60],"gripperOpeningMm":20,"syncIndex":22,"source":"simulation-measured-qpos"}
          ]
        }
        """)!.AsObject();
        if (version == 1)
            return value;
        value["version"] = 2;
        value["commandTimestampConvention"] = EpisodeReplayReader.CommandTimestampConvention;
        value["nominalCaptureHz"] = 50;
        value["outcomeSource"] = EpisodeReplayReader.PositionOutcomeSource;
        var robot = value["robot"]!.AsObject();
        robot["gripperMaximumOpeningMm"] = 90;
        robot["gripperReferenceGapMm"] = 94.04438247823715;
        robot["actionSource"] = EpisodeReplayReader.PositionActionSource;
        robot["commandConvention"] = EpisodeReplayReader.CommandConvention;
        robot["positionActuatorProvenance"] = new JsonObject
        {
            ["sourceSha256"] = "5b666db802b090ffcbdad4655d43397a176533a934d0a7748e46aba9581f483c",
            ["validation"] = EpisodeReplayReader.ActuatorValidation,
            ["runtimeChecks"] = EpisodeReplayReader.ActuatorRuntimeChecks,
            ["runtimeTuning"] = EpisodeReplayReader.ActuatorRuntimeTuning
        };
        foreach (var sample in value["samples"]!.AsArray())
        {
            sample!["commandedJointDegrees"] = sample["jointDegrees"]!.DeepClone();
            sample["commandedGripperOpeningMm"] = sample["gripperOpeningMm"]!.DeepClone();
            sample["commandSource"] = EpisodeReplayReader.CommandSource;
        }
        return value;
    }

    private static EpisodeReplayHardware Hardware() => new("can-a", 1, "agilex.piper", EpisodeReplayReader.JointConvention,
        EpisodeReplayReader.GripperConvention, true, true, true, true,
        [new(1, -150, 150, 30, 1000, true), new(2, 0, 180, 30, 1000, true), new(3, -170, 0, 30, 1000, true), new(4, -100, 100, 30, 1000, true), new(5, -70, 70, 30, 1000, true), new(6, -180, 180, 30, 1000, true)],
        [10, 20, -30, 40, 50, 60], 0, 90, 40);
}
