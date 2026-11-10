using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class EpisodeReplayTests
{
    internal static EpisodeReplayArtifact ValidArtifact() => EpisodeReplayReader.Parse(Fixture().ToJsonString());

    public static async Task RunFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "piper-replay-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "episode.json");
        try
        {
            await File.WriteAllTextAsync(file, Fixture().ToJsonString());
            var loaded = await PipersPlugin.ReadReplayFile(file, CancellationToken.None);
            Check(loaded.ArtifactId == "episode-a" && loaded.Samples.Count == 2 && loaded.TubeStart.LeftMm == 700,
                "Native replay preparation could not load the versioned export and object placement");
            await File.WriteAllTextAsync(file, Fixture().ToJsonString().Replace(EpisodeReplayReader.Kind, "lucky.piper.episode-start", StringComparison.Ordinal));
            bool rejected = false;
            try { await PipersPlugin.ReadReplayFile(file, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Replay preparation accepted an unfinished start manifest");
            using (var oversized = new FileStream(file, FileMode.Open, FileAccess.Write))
                oversized.SetLength(EpisodeReplayReader.MaximumBytes + 1L);
            rejected = false;
            try { await PipersPlugin.ReadReplayFile(file, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Replay preparation loaded a file beyond the bounded JSON input size");
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }

    public static void Run()
    {
        EpisodeReplayArtifact Read(JsonObject value) => EpisodeReplayReader.Parse(value.ToJsonString());
        var artifact = Read(Fixture());
        var repeatedSync = Fixture();
        repeatedSync["samples"]![1]!["syncIndex"] = 12;
        Check(Read(repeatedSync).Samples[1].SyncIndex == 12 && artifact.NominalCaptureHz == 50,
            "50Hz Validation samples were incorrectly restricted to unique 10Hz sync-point indices");
        Check(artifact.Samples[0].JointDegrees[5] == 60 && artifact.Samples[1].JointDegrees[0] == 20,
            "Replay reader converted explicit degree samples to radians or changed their order");
        Check(artifact.TubeStart.LeftMm == 700 && Math.Abs(artifact.TubeStart.WorldPositionMetres.Z + .15) < 1e-9 &&
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

        var review = new ReplayReview(artifact, "profile-a");
        var scope = new ReplayCheckScope("backend-a", "can-a", 7);
        var confirmed = review.Confirmed(scope);
        Check(confirmed is { TubeConfirmed: true } && confirmed.Observe("profile-a", scope).TubeConfirmed &&
            !confirmed.Observe("profile-b", scope).TubeConfirmed && !confirmed.Observe("profile-a", scope with { Generation = 8 }).TubeConfirmed &&
            !confirmed.Observe("profile-a", null).TubeConfirmed && confirmed.Rechecked(scope).TubeConfirmed &&
            !confirmed.Rechecked(scope with { Device = "can-b" }).TubeConfirmed && confirmed.Loaded() is { TubeConfirmed: false, Scope: null } &&
            !review.Rechecked(scope).TubeConfirmed,
            "Tube placement survived a profile or connection change, or a recheck invented a placement confirmation");
        var limits = Limits();
        var legacy = Read(Fixture(1));
        Check(!legacy.HasPositionCommands && legacy.Samples[0].CommandedJointDegrees is null,
            "Legacy measured-only samples were promoted to executable commands");
        Reject(() => EpisodeReplayPlanner.AssessJointTiming(legacy, limits), "Version 1 measured-only export was treated as an actuator replay");
        var oldestLegacy = Fixture(1);
        oldestLegacy["nativeRecording"]!.AsObject().Remove("nativeOrdinalSource");
        Check(!Read(oldestLegacy).HasPositionCommands, "Earlier version 1 files lost review support after provenance was introduced");
        var invalidCapture = Fixture();
        invalidCapture["outcome"] = "invalid-capture: lost sample";
        Reject(() => Read(invalidCapture), "Replay accepted a capture explicitly exported as invalid");
        Reject(() => EpisodeReplayPlanner.AssessJointTiming(artifact, limits.Select(limit => limit with { AccelerationFresh = false }).ToArray()),
            "Replay accepted stale acceleration reports");
        foreach (double invalidAcceleration in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Reject(() => EpisodeReplayPlanner.AssessJointTiming(artifact, limits.Select(limit => limit with { MaximumDegreesPerSecondSquared = invalidAcceleration }).ToArray()),
                "Replay invented a positive acceleration cap for an invalid report");
        var outOfRange = Fixture();
        outOfRange["samples"]![1]!["commandedJointDegrees"]![0] = 151;
        Reject(() => EpisodeReplayPlanner.AssessJointTiming(Read(outOfRange), limits),
            "Replay review silently clipped a joint target outside reported limits");
        Reject(() => EpisodeReplayPlanner.ValidateJointRanges(Read(outOfRange).Samples.Select(s => s.CommandedJointDegrees!).ToArray(), limits),
            "Replay range review accepted a joint target outside reported limits");

        var reversingJson = Fixture();
        var third = reversingJson["samples"]![1]!.DeepClone();
        third["timeSeconds"] = 2;
        third["syncIndex"] = 32;
        third["commandedJointDegrees"]![0] = 10;
        reversingJson["samples"]!.AsArray().Add(third);
        var reversing = Read(reversingJson);
        var accelerationLimits = limits.Select(limit => limit with { MaximumDegreesPerSecondSquared = 5 }).ToArray();
        var timing = EpisodeReplayPlanner.AssessJointTiming(reversing, accelerationLimits);
        Check(timing.SpeedTimeScale == 1 && Math.Abs(timing.AccelerationTimeScale - 2) < 1e-12 && timing.RequiredTimeScale == 2,
            "Low-velocity direction reversal bypassed the acceleration cap or omitted square-root time scaling");
        reversingJson["samples"]![2]!["timeSeconds"] = 3;
        var irregularTiming = EpisodeReplayPlanner.AssessJointTiming(Read(reversingJson), accelerationLimits.Select(limit => limit with { MaximumDegreesPerSecondSquared = 2.5 }).ToArray());
        Check(Math.Abs(irregularTiming.AccelerationTimeScale - 2) < 1e-12,
            "Acceleration used nominal sample indices instead of midpoint spacing for irregular timestamps");

        var reported = new JsonArray(limits.Select(limit => (JsonNode)new JsonObject
        {
            ["min_deg"] = limit.MinimumDegrees, ["max_deg"] = limit.MaximumDegrees,
            ["max_speed_rad_s"] = limit.MaximumDegreesPerSecond * Math.PI / 180,
            ["max_acceleration_rad_s2"] = .5, ["limits_fresh"] = true, ["acceleration_fresh"] = true
        }).ToArray());
        var readLimits = EpisodeReplayReportedLimits.Read(reported);
        Check(readLimits.All(limit => limit.AccelerationFresh && Math.Abs(limit.MaximumDegreesPerSecondSquared - .5 * 180 / Math.PI) < 1e-12),
            "Replay UI did not preserve the backend's reported rad/s² acceleration units");
        foreach (string key in new[] { "acceleration_fresh", "max_acceleration_rad_s2" })
        {
            var missing = reported.DeepClone().AsArray();
            missing[3]!.AsObject().Remove(key);
            Reject(() => EpisodeReplayReportedLimits.Read(missing), "Replay UI substituted a default for missing " + key);
        }
        reported[1]!["acceleration_fresh"] = false;
        Reject(() => EpisodeReplayReportedLimits.Read(reported), "Replay UI accepted one stale acceleration report");
        foreach (string missing in new[] { "commandedJointDegrees", "commandedGripperOpeningMm", "commandSource" })
        {
            var bad = Fixture();
            bad["samples"]![1]!.AsObject().Remove(missing);
            Reject(() => Read(bad), "Version 2 silently substituted observations for missing " + missing);
        }

        var zUp = Fixture();
        zUp["tubeStart"]!["worldQuaternionXyzw"] = new JsonArray(-.6830127018922193, .1830127018922193, .1830127018922193, .6830127018922193);
        Check(Math.Abs(Read(zUp).TubeStart.YawDeg - 30) < 1e-9, "Native Z-up tube yaw was interpreted as an engine Y-up Euler yaw");
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

    private static EpisodeReplayJointLimit[] Limits() =>
        [new(1, -150, 150, 30, 1000, true), new(2, 0, 180, 30, 1000, true), new(3, -170, 0, 30, 1000, true), new(4, -100, 100, 30, 1000, true), new(5, -70, 70, 30, 1000, true), new(6, -180, 180, 30, 1000, true)];
}
