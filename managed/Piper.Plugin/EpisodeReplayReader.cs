using System.Text;
using System.Text.Json;
using System.Globalization;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Reads simulation position samples only. Parsing never connects to or commands hardware.</summary>
public static class EpisodeReplayReader
{
    public const string Kind = "lucky.piper.joint-trajectory";
    public const string JointConvention = "piper-j1-j6-degrees-v1";
    public const string GripperConvention = "pika-opening-mm-v1";
    public const string GripperMapping = "pika-opposed-slides-open-at-zero-v1";
    public const string DeskFrame = "engine-world-y-up-centred-xz-back-negative-z";
    public const string SampleSource = "simulation-measured-qpos";
    public const string ActionSource = "not included; replay samples are measured qpos observations, not controller targets";
    public const string PositionActionSource = "measured qpos observations plus verified position-actuator ctrl targets";
    public const string CommandSource = "simulation-position-actuator-ctrl";
    public const string CommandConvention = "piper-position-actuator-targets-degrees-pika-mm-v1";
    public const string CommandTimestampConvention = "postphysics-validation-samples-zero-order-hold-v1";
    public const string ActuatorValidation = "verified authored position tags for pinned task asset; runtime actuator-law type not introspectable";
    public const string ActuatorRuntimeChecks = "exact actuator names, ordered joint transmissions, control ranges and policy ownership";
    public const string ActuatorRuntimeTuning = "native Play may override gains while retaining position-controller semantics";
    public const string TubeStartSource = "settled simulation body immediately before episode begin";
    public const string SessionLabelSource = "operator supplied; not discovered from native Recorder";
    public const string NativeOrdinalSource = "task_owned_session_counter_not_engine_reported";
    public const string QposConvention = "native MuJoCo model order: hinges radians, slides/free translations metres, free quaternions wxyz; not a hardware command";
    public const string OutcomeSource = "operator-confirmed; native recorder finalization may independently reject an episode";
    public const string PositionOutcomeSource = "completion-request-and-simulated-placement-checks; native-recorder-finalization-unverified";
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumSamples = 100_000;
    public const double MaximumDurationSeconds = 3600;

    public static EpisodeReplayArtifact Parse(string json)
    {
        if (json is null || json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw Invalid("File is missing or exceeds the 16 MiB trajectory limit.");
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    public static EpisodeReplayArtifact Parse(ReadOnlyMemory<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumBytes)
            throw Invalid("File is empty or exceeds the 16 MiB trajectory limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            int version = Integer(root, "version");
            if (version is not (1 or 2))
                throw Invalid("Only trajectory versions 1 (review only) and 2 (explicit position commands) are supported.");
            string[] rootFields = ["kind", "version", "mode", "artifactId", "sessionId", "createdUtc", "seed", "episodeOrdinal", "robot", "desk", "tubeStart", "tubeDimensions", "goal", "nativeRecording", "initialSimulationState", "outcome", "outcomeSource", "samples"];
            Properties(root, "trajectory", version == 2 ? [.. rootFields, "commandTimestampConvention", "nominalCaptureHz"] : rootFields);
            Equal(root, "kind", Kind);
            double? captureHz = null;
            if (version == 2)
            {
                Equal(root, "commandTimestampConvention", CommandTimestampConvention);
                captureHz = Number(root, "nominalCaptureHz");
                if (captureHz is <= 0 or > 1000)
                    throw Invalid("nominalCaptureHz must be a positive bounded nominal rate; actual sample timestamps remain authoritative.");
            }
            Equal(root, "mode", "simulation");
            var robot = ReadRobot(Object(root, "robot"), version);
            var deskJson = Object(root, "desk");
            Properties(deskJson, "desk", "widthMm", "depthMm", "surfaceYMetres", "frame");
            Equal(deskJson, "frame", DeskFrame);
            double width = Number(deskJson, "widthMm"), depth = Number(deskJson, "depthMm"), surfaceY = Number(deskJson, "surfaceYMetres");
            if (width is < 100 or > 10_000 || depth is < 100 or > 10_000 || surfaceY is < 0 or > 3)
                throw Invalid("Desk dimensions must be 100–10000 mm, with its surface 0–3 m above world zero.");
            var desk = new EpisodeReplayDesk(width, depth, surfaceY, DeskFrame);

            var dimensionsJson = Object(root, "tubeDimensions");
            Properties(dimensionsJson, "tubeDimensions", "diameterMm", "heightMm", "massKg", "massSource");
            double diameter = Number(dimensionsJson, "diameterMm"), height = Number(dimensionsJson, "heightMm"), mass = Number(dimensionsJson, "massKg");
            string massSource = Text(dimensionsJson, "massSource");
            if (diameter is <= 0 or > 200 || height is <= 0 or > 1000 || mass is <= 0 or > 10 || massSource is not ("estimated" or "measured" or "manufacturer"))
                throw Invalid("Tube dimensions/mass require explicit millimetre/kilogram values and an estimated, measured or manufacturer mass source.");
            var dimensions = new EpisodeReplayTubeDimensions(diameter, height, mass, massSource);
            var start = ReadStart(Object(root, "tubeStart"), desk, dimensions);
            var recording = Object(root, "nativeRecording");
            string[] recordingFields = ["requested", "sessionLabel", "episodeOrdinal", "sessionLabelSource", "taskIndex", "taskSessionId"];
            bool hasOrdinalSource = recording.TryGetProperty("nativeOrdinalSource", out _);
            Properties(recording, "nativeRecording", version == 2 || hasOrdinalSource ? [.. recordingFields, "nativeOrdinalSource"] : recordingFields);
            if (!recording.TryGetProperty("requested", out var requestedValue) || requestedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("requested must be a boolean.");
            bool requested = requestedValue.GetBoolean();
            string label = Text(recording, "sessionLabel", allowEmpty: true);
            int? ordinal = recording.GetProperty("episodeOrdinal").ValueKind == JsonValueKind.Null ? null : Integer(recording, "episodeOrdinal");
            if (ordinal is < 0 or > 1_000_000 || (requested && (!ordinal.HasValue || label.Length == 0)))
                throw Invalid("nativeRecording requires a nonnegative bounded ordinal and a session label when requested.");
            Equal(recording, "sessionLabelSource", SessionLabelSource);
            if (hasOrdinalSource)
                Equal(recording, "nativeOrdinalSource", NativeOrdinalSource);
            string ordinalSource = hasOrdinalSource ? NativeOrdinalSource : "not reported in version 1";
            string sessionId = Text(root, "sessionId");
            int taskIndex = Integer(recording, "taskIndex");
            string taskSessionId = Text(recording, "taskSessionId");
            if (taskIndex < 0 || taskSessionId != sessionId)
                throw Invalid("Native task metadata must identify this trajectory's task session.");

            var samplesJson = Object(root, "samples", JsonValueKind.Array);
            if (samplesJson.GetArrayLength() is < 2 or > MaximumSamples)
                throw Invalid($"A trajectory requires 2–{MaximumSamples} samples.");
            var samples = new List<EpisodeReplaySample>(samplesJson.GetArrayLength());
            foreach (var sample in samplesJson.EnumerateArray())
            {
                string[] sampleFields = ["timeSeconds", "jointDegrees", "gripperOpeningMm", "syncIndex", "source"];
                Properties(sample, "sample", version == 2 ? [.. sampleFields, "commandedJointDegrees", "commandedGripperOpeningMm", "commandSource"] : sampleFields);
                double time = Number(sample, "timeSeconds");
                if (time < 0 || time > MaximumDurationSeconds ||
                    (samples.Count == 0 ? time != 0 : time <= samples[^1].TimeSeconds))
                    throw Invalid($"Sample {samples.Count}: timeSeconds must start at zero and increase strictly, within one hour.");
                var joints = Object(sample, "jointDegrees", JsonValueKind.Array);
                if (joints.GetArrayLength() != 6)
                    throw Invalid($"Sample {samples.Count}: expected exactly six joint angles in degrees.");
                var angles = joints.EnumerateArray().Select(FiniteNumber).ToArray();
                double opening = Number(sample, "gripperOpeningMm");
                if (opening < 0 || opening > robot.GripperMaximumOpeningMm)
                    throw Invalid($"Sample {samples.Count}: gripper opening is outside the exported model's millimetre range.");
                var sync = Object(sample, "syncIndex", JsonValueKind.Number);
                if (!sync.TryGetUInt64(out ulong index) || (samples.Count > 0 && index < samples[^1].SyncIndex))
                    throw Invalid($"Sample {samples.Count}: syncIndex must be an unsigned, nondecreasing integer.");
                Equal(sample, "source", SampleSource);
                IReadOnlyList<double>? commands = null;
                double? commandedOpening = null;
                if (version == 2)
                {
                    Equal(sample, "commandSource", CommandSource);
                    var commanded = Object(sample, "commandedJointDegrees", JsonValueKind.Array);
                    if (commanded.GetArrayLength() != 6)
                        throw Invalid($"Sample {samples.Count}: version 2 requires exactly six commanded position targets in degrees.");
                    commands = Array.AsReadOnly(commanded.EnumerateArray().Select(FiniteNumber).ToArray());
                    commandedOpening = Number(sample, "commandedGripperOpeningMm");
                    if (commandedOpening < 0 || commandedOpening > robot.GripperMaximumOpeningMm)
                        throw Invalid($"Sample {samples.Count}: commanded gripper opening exceeds the actual allowed range.");
                }
                samples.Add(new(time, Array.AsReadOnly(angles), opening, index, SampleSource, commands, commandedOpening, version == 2 ? CommandSource : null));
            }
            var audit = ReadAudit(root, desk, samples[0].SyncIndex, version);
            return new(Text(root, "artifactId"), sessionId, robot, desk, start, dimensions,
                new(requested, label, ordinal, SessionLabelSource, taskIndex, taskSessionId, ordinalSource), samples.AsReadOnly(), audit, version,
                version == 2 ? CommandTimestampConvention : null, captureHz);
        }
        catch (JsonException error)
        {
            throw Invalid("Malformed trajectory JSON: " + error.Message);
        }
    }

    private static EpisodeReplayRobot ReadRobot(JsonElement robot, int version)
    {
        string[] fields = ["model", "jointConvention", "jointNames", "gripperConvention", "gripperMapping", "gripperMaximumOpeningMm", "actionSource"];
        Properties(robot, "robot", version == 2 ? [.. fields, "gripperReferenceGapMm", "commandConvention", "positionActuatorProvenance"] : fields);
        Equal(robot, "model", "agilex.piper");
        Equal(robot, "jointConvention", JointConvention);
        Equal(robot, "gripperConvention", GripperConvention);
        Equal(robot, "gripperMapping", GripperMapping);
        Equal(robot, "actionSource", version == 2 ? PositionActionSource : ActionSource);
        var names = Object(robot, "jointNames", JsonValueKind.Array);
        if (names.GetArrayLength() != 6 || !names.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
            .SequenceEqual(Enumerable.Range(1, 6).Select(i => "joint" + i)))
            throw Invalid("jointNames must be joint1 through joint6 in physical Piper order.");
        double maximum = Number(robot, "gripperMaximumOpeningMm");
        if (maximum <= 0 || maximum > 100)
            throw Invalid("The verified opposed-slide model opening must be greater than zero and at most 100 mm.");
        double? reference = null;
        EpisodeReplayActuatorProvenance? provenance = null;
        if (version == 2)
        {
            Equal(robot, "commandConvention", CommandConvention);
            reference = Number(robot, "gripperReferenceGapMm");
            if (maximum > 90 || reference < maximum || reference > 100)
                throw Invalid("Actual Pika opening must be at most 90 mm and distinct from its declared geometric zero-slide reference gap.");
            var proof = Object(robot, "positionActuatorProvenance");
            Properties(proof, "positionActuatorProvenance", "sourceSha256", "validation", "runtimeChecks", "runtimeTuning");
            string digest = Text(proof, "sourceSha256");
            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
                throw Invalid("Position-actuator provenance requires the pinned authored model's SHA-256.");
            Equal(proof, "validation", ActuatorValidation);
            Equal(proof, "runtimeChecks", ActuatorRuntimeChecks);
            Equal(proof, "runtimeTuning", ActuatorRuntimeTuning);
            provenance = new(digest, ActuatorValidation, ActuatorRuntimeChecks, ActuatorRuntimeTuning);
        }
        return new("agilex.piper", JointConvention, Array.AsReadOnly(Enumerable.Range(1, 6).Select(i => "joint" + i).ToArray()),
            GripperConvention, GripperMapping, maximum, version == 2 ? PositionActionSource : ActionSource, reference,
            version == 2 ? CommandConvention : null, provenance);
    }

    private static EpisodeReplayTubeStart ReadStart(JsonElement start, EpisodeReplayDesk desk, EpisodeReplayTubeDimensions dimensions)
    {
        Properties(start, "tubeStart", "leftMm", "backMm", "yawDeg", "worldPositionMetres", "worldQuaternionXyzw", "source");
        Equal(start, "source", TubeStartSource);
        double left = Number(start, "leftMm"), back = Number(start, "backMm"), yaw = Number(start, "yawDeg");
        var world = Object(start, "worldPositionMetres");
        Properties(world, "worldPositionMetres", "x", "y", "z");
        var position = new EpisodeReplayPosition(Number(world, "x"), Number(world, "y"), Number(world, "z"));
        double radius = dimensions.DiameterMm / 2;
        if (left < radius || left > desk.WidthMm - radius || back < radius || back > desk.DepthMm - radius || Math.Abs(yaw) > 360 ||
            Math.Abs(position.X - (left - desk.WidthMm / 2) / 1000) > .0001 ||
            Math.Abs(position.Z - (back - desk.DepthMm / 2) / 1000) > .0001 ||
            Math.Abs(position.Y - desk.SurfaceYMetres - dimensions.HeightMm / 2000) > .01)
            throw Invalid("Tube start must fit on the desk; LEFT/BACK in mm must match its settled upright centre world position in metres.");
        var quaternion = Object(start, "worldQuaternionXyzw", JsonValueKind.Array);
        if (quaternion.GetArrayLength() != 4)
            throw Invalid("Tube start requires a four-element xyzw world quaternion.");
        var q = quaternion.EnumerateArray().Select(FiniteNumber).ToArray();
        // The tube body is native Z-up. Its horizontal +X direction defines desk yaw.
        double rotationYaw = Math.Atan2(2 * (q[3] * q[1] - q[0] * q[2]), 1 - 2 * (q[1] * q[1] + q[2] * q[2]));
        double difference = rotationYaw - yaw * Math.PI / 180;
        if (Math.Abs(q.Sum(x => x * x) - 1) > 1e-4 || Math.Abs(Math.Atan2(Math.Sin(difference), Math.Cos(difference))) > .01 * Math.PI / 180)
            throw Invalid("Tube start quaternion must be normalized and agree with its placement yaw.");
        return new(left, back, yaw, position, Array.AsReadOnly(q), TubeStartSource);
    }

    private static EpisodeReplayAudit ReadAudit(JsonElement root, EpisodeReplayDesk desk, ulong firstSync, int version)
    {
        string created = Text(root, "createdUtc");
        if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc) || utc.Offset != TimeSpan.Zero)
            throw Invalid("createdUtc must be a UTC timestamp.");
        int seed = Integer(root, "seed"), ordinal = Integer(root, "episodeOrdinal");
        if (ordinal is < 0 or > 1_000_000)
            throw Invalid("episodeOrdinal must identify a bounded project-owned prepared episode.");
        var goal = Object(root, "goal");
        Properties(goal, "goal", "leftMm", "backMm", "placementToleranceMm");
        double left = Number(goal, "leftMm"), back = Number(goal, "backMm"), tolerance = Number(goal, "placementToleranceMm");
        if (left < 0 || left > desk.WidthMm || back < 0 || back > desk.DepthMm || tolerance is <= 0 or > 100)
            throw Invalid("Goal must be inside the desk with a positive placement tolerance of at most 100 mm.");
        var initial = Object(root, "initialSimulationState");
        Properties(initial, "initialSimulationState", "mujocoQpos", "qposConvention", "worldEntityId", "syncIndex");
        Equal(initial, "qposConvention", QposConvention);
        var qpos = Object(initial, "mujocoQpos", JsonValueKind.Array);
        if (qpos.GetArrayLength() is < 1 or > 512)
            throw Invalid("Initial simulation qpos must be a bounded nonempty audit array.");
        var auditQpos = qpos.EnumerateArray().Select(FiniteNumber).ToArray();
        string world = Text(initial, "worldEntityId");
        if (!ulong.TryParse(world, NumberStyles.None, CultureInfo.InvariantCulture, out ulong worldId) || worldId == 0 ||
            !Object(initial, "syncIndex", JsonValueKind.Number).TryGetUInt64(out ulong sync) || sync != firstSync)
            throw Invalid("Initial simulation audit state must identify a world and the first sample's sync index.");
        string outcome = Text(root, "outcome");
        if (outcome is not ("success" or "unsuccessful"))
            throw Invalid("Only a finished successful/unsuccessful trajectory can be planned; incomplete or invalid captures cannot be replayed.");
        string outcomeSource = version == 2 ? PositionOutcomeSource : OutcomeSource;
        Equal(root, "outcomeSource", outcomeSource);
        return new(utc, seed, ordinal, new(left, back, tolerance), new(Array.AsReadOnly(auditQpos), QposConvention, world, sync), outcome, outcomeSource);
    }

    private static void Properties(JsonElement value, string name, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(name + " must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid(name + " contains a duplicate or unsupported field: " + property.Name);
        }
        if (seen.Count != allowed.Length)
            throw Invalid(name + " is missing a required field.");
    }
    private static JsonElement Object(JsonElement parent, string name, JsonValueKind kind = JsonValueKind.Object)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw Invalid(name + " has a missing or incorrect JSON type.");
        return value;
    }
    private static double Number(JsonElement parent, string name) => FiniteNumber(Object(parent, name, JsonValueKind.Number));
    private static double FiniteNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw Invalid("Trajectory numbers must be finite JSON numbers.");
        return number;
    }
    private static int Integer(JsonElement parent, string name)
    {
        if (!Object(parent, name, JsonValueKind.Number).TryGetInt32(out int value))
            throw Invalid(name + " must be an integer.");
        return value;
    }
    private static string Text(JsonElement parent, string name, bool allowEmpty = false)
    {
        string text = Object(parent, name, JsonValueKind.String).GetString()!;
        if (text.Length > 256 || text.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw Invalid(name + " must be bounded text without control characters.");
        return text;
    }
    private static void Equal(JsonElement parent, string name, string expected)
    {
        if (Text(parent, name) != expected)
            throw Invalid(name + " must be " + expected + ". Raw torque/state trajectories and alternate unit mappings are unsupported.");
    }
    private static InvalidOperationException Invalid(string message) => new(message);
}
