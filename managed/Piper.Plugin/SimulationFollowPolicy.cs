using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record SimulationFollowIdentity(string SourceSceneId, string RunId, string EntityId, string ModelAssetId, string ModelRevision);
public sealed record SimulationFollowHardware(string DeviceId, long Generation, bool FeedbackFresh, bool LimitsFresh, bool GripperFresh,
    IReadOnlyList<double> JointDegrees, IReadOnlyList<EpisodeReplayJointLimit> JointLimits, double GripperOpeningMm,
    SimulationFollowNativeReference? NativeReference = null);
public sealed record SimulationFollowTarget(IReadOnlyList<double> JointDegrees, double GripperOpeningMm,
    IReadOnlyList<double> MeasuredJointDegrees, double MeasuredGripperOpeningMm, double SimulationTimeSeconds);
public sealed class SimulationFollowAlignmentException(string message) : InvalidOperationException(message);

/// <summary>Validated native runtime observation. Actuator controls and measured qpos remain distinct.</summary>
public sealed class SimulationFollowSample
{
    private SimulationFollowSample() { }
    public SimulationFollowIdentity Identity { get; private init; } = new("", "", "", "", "");
    public string SourceSceneId => Identity.SourceSceneId;
    public string RunId => Identity.RunId;
    public string EntityId => Identity.EntityId;
    public string ModelAssetId => Identity.ModelAssetId;
    public string ModelRevision => Identity.ModelRevision;
    public string SceneId { get; private init; } = "";
    public string Mode { get; private init; } = "";
    public bool Ready { get; private init; }
    public double TimeScale { get; private init; }
    public double SimTimeSeconds { get; private init; }
    public long SimulationTimeNs { get; private init; }
    public long CapturedMonotonicNs { get; private init; }
    public long CapturedAtUnixMicroseconds { get; private init; }
    public long Sequence { get; private init; }
    public IReadOnlyList<double> CommandDegrees { get; private init; } = Array.Empty<double>();
    public IReadOnlyList<double> MeasuredDegrees { get; private init; } = Array.Empty<double>();
    public double CommandOpeningMm { get; private init; }
    public double MeasuredOpeningMm { get; private init; }
    internal string MappingFingerprint { get; private init; } = "";
    public bool BindsTo(SimulationFollowSample other) => Identity == other.Identity && MappingFingerprint == other.MappingFingerprint;

    public static SimulationFollowSample Read(JsonObject pose, string jointPrefix = "")
    {
        SceneRobotBindings.ValidatePrefix(jointPrefix);
        string mode = Text(pose, "mode");
        if (!Flag(pose, "ready")) throw new InvalidOperationException($"Simulation is {mode}; its runtime pose is not ready.");
        var identity = new SimulationFollowIdentity(Text(pose, "sourceSceneId"), OptionalText(pose, "runId"),
            Text(pose, "entityId"), Text(pose, "modelAssetId"), Text(pose, "modelRevision"));
        string sceneId = Text(pose, "sceneId");
        if (Text(pose, "clockDomain") != "host-utc" || Text(pose, "monotonicClockDomain") != "native-steady-clock")
            throw new InvalidOperationException("Unsupported runtime sample clock domain.");
        long mono = IntegerText(pose, "capturedMonotonicNs"), utc = IntegerText(pose, "capturedAtUnixMicroseconds"), simNs = IntegerText(pose, "simulationTimeNs");
        long sequence = pose["sequence"] is JsonValue sequenceValue && sequenceValue.TryGetValue<long>(out long number) && number > 0
            ? number : throw new InvalidOperationException("Runtime sample sequence is missing or invalid.");
        double simTime = Number(pose, "mujocoTimeSeconds"), scale = Number(pose, "timeScale");
        if (simTime < 0 || scale <= 0) throw new InvalidOperationException("Invalid runtime simulation clock or time scale.");
        /* A MuJoCo instance may own several arms and task objects. Selection is
           by exact names inside the pinned scene/entity/model revision, never
           by array order or by the first eight world joints. */
        var joints = Named(pose, "joints", 8, 256);
        var actuators = Named(pose, "actuators", 7, 256);
        var selectedJoints = Enumerable.Range(1, 8).Select(i => jointPrefix + "joint" + i).ToHashSet(StringComparer.Ordinal);
        var selectedActuators = Enumerable.Range(1, 6).Select(i => jointPrefix + "joint" + i).Append(jointPrefix + "gripper").ToHashSet(StringComparer.Ordinal);
        foreach (var (name, actuator) in actuators)
            if (!selectedActuators.Contains(name) && selectedJoints.Contains(OptionalText(actuator, "targetJoint")))
                throw new InvalidOperationException("An additional runtime actuator targets the selected arm's joints; its mapping is ambiguous.");
        double[] measured = new double[6], command = new double[6];
        var signature = new StringBuilder();
        for (int i = 0; i < 6; i++)
        {
            string name = jointPrefix + "joint" + (i + 1);
            var joint = Required(joints, name);
            Range(joint, "hinge", "rad", out double minimum, out double maximum);
            double qpos = Number(joint, "position");
            if (qpos < minimum - Math.PI / 180 || qpos > maximum + Math.PI / 180)
                throw new InvalidOperationException($"Simulated {name} measurement is outside its range.");
            measured[i] = qpos * 180 / Math.PI;
            var actuator = Required(actuators, name);
            double ctrl = PositionControl(actuator, name, "rad", out double ctrlMin, out double ctrlMax);
            if (ctrlMin < minimum - 1e-6 || ctrlMax > maximum + 1e-6)
                throw new InvalidOperationException($"Simulated {name} control range exceeds its joint range.");
            command[i] = ctrl * 180 / Math.PI;
            Signature(signature, name, minimum, maximum, ctrlMin, ctrlMax, NumberAt(actuator, "gain", 0), NumberAt(actuator, "bias", 2));
        }
        var first = Required(joints, jointPrefix + "joint7");
        var second = Required(joints, jointPrefix + "joint8");
        Range(first, "slide", "m", out double firstMin, out double firstMax);
        Range(second, "slide", "m", out double secondMin, out double secondMax);
        if (firstMin >= 0 || firstMax > 1e-9 || secondMin < -1e-9 || secondMax <= 0 || firstMin < -.050001 || secondMax > .050001 ||
            Math.Abs(firstMin + secondMax) > 1e-6 || Math.Abs(firstMax + secondMin) > 1e-6)
            throw new InvalidOperationException("Simulated Pika requires mirrored limited joint7/joint8 slide ranges.");
        double q7 = Number(first, "position"), q8 = Number(second, "position");
        if (q7 < firstMin - .00005 || q7 > firstMax + .00005 || q8 < secondMin - .00005 || q8 > secondMax + .00005 || Math.Abs(q7 + q8) > .001)
            throw new InvalidOperationException("Simulated Pika slide measurements are asymmetric or outside their ranges.");
        var grip = Required(actuators, jointPrefix + "gripper");
        double gripCtrl = PositionControl(grip, jointPrefix + "joint7", "m", out double gripMin, out double gripMax);
        if (Math.Abs(gripMin - firstMin) > 1e-6 || Math.Abs(gripMax - firstMax) > 1e-6)
            throw new InvalidOperationException("Simulated Pika actuator range does not match joint7.");
        double gap = 2000 * secondMax;
        double maximumOpening = Math.Min(90, gap + 1000 * (firstMax - secondMin));
        double opening = gap + 1000 * (q7 - q8), commandOpening = gap + 2000 * gripCtrl;
        if (maximumOpening <= 0 || opening < -.25 || opening > maximumOpening + .25 || commandOpening < -.0001 || commandOpening > maximumOpening + .0001)
            throw new InvalidOperationException("Simulated Pika opening is outside its 0–90 mm model range.");
        Signature(signature, jointPrefix + "gripper", firstMin, firstMax, secondMin, secondMax, gripMin, gripMax, NumberAt(grip, "gain", 0), NumberAt(grip, "bias", 2));
        return new SimulationFollowSample
        {
            Identity = identity, SceneId = sceneId, Mode = mode, Ready = true, TimeScale = scale, SimTimeSeconds = simTime,
            SimulationTimeNs = simNs, CapturedMonotonicNs = mono, CapturedAtUnixMicroseconds = utc, Sequence = sequence,
            MeasuredDegrees = Array.AsReadOnly(measured), CommandDegrees = Array.AsReadOnly(command),
            MeasuredOpeningMm = Math.Clamp(opening, 0, maximumOpening), CommandOpeningMm = Math.Clamp(commandOpening, 0, maximumOpening),
            MappingFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())))
        };
    }

    private static void Range(JsonObject joint, string type, string unit, out double minimum, out double maximum)
    {
        if (Text(joint, "type") != type || Text(joint, "unit") != unit || !Flag(joint, "limited"))
            throw new InvalidOperationException("Runtime joint type, units or limits do not match the Piper/Pika model.");
        minimum = Number(joint, "min"); maximum = Number(joint, "max");
        if (minimum >= maximum) throw new InvalidOperationException("Invalid runtime joint range.");
        _ = Number(joint, "velocity");
    }

    private static double PositionControl(JsonObject actuator, string target, string unit, out double minimum, out double maximum)
    {
        if (!Flag(actuator, "positionControl") || Text(actuator, "targetJoint") != target || Text(actuator, "controlUnit") != unit ||
            Text(actuator, "transmission") is not ("joint" or "joint-in-parent") || !Flag(actuator, "limited"))
            throw new InvalidOperationException("Runtime actuator is not the expected limited unit-gear position servo.");
        if (actuator["gear"] is not JsonArray gear || gear.Count != 6 || actuator["gain"] is not JsonArray gain || gain.Count != 10 ||
            actuator["bias"] is not JsonArray bias || bias.Count != 10)
            throw new InvalidOperationException("Runtime actuator law metadata is incomplete.");
        for (int i = 0; i < 6; i++)
            if (Math.Abs(NumberAt(actuator, "gear", i) - (i == 0 ? 1 : 0)) > 1e-9)
                throw new InvalidOperationException("Runtime position control requires unit joint transmission gear.");
        for (int i = 0; i < 10; i++) { _ = NumberAt(actuator, "gain", i); _ = NumberAt(actuator, "bias", i); }
        double kp = NumberAt(actuator, "gain", 0);
        if (kp <= 0 || Math.Abs(NumberAt(actuator, "bias", 0)) > 1e-9 || Math.Abs(NumberAt(actuator, "bias", 1) + kp) > Math.Max(1e-9, kp * 1e-9) || NumberAt(actuator, "bias", 2) > 0)
            throw new InvalidOperationException("Runtime actuator law is not a stable position servo.");
        minimum = Number(actuator, "min"); maximum = Number(actuator, "max");
        double value = Number(actuator, "control");
        if (minimum >= maximum || value < minimum || value > maximum) throw new InvalidOperationException("Runtime position command is outside its actuator range.");
        return value;
    }

    private static Dictionary<string, JsonObject> Named(JsonObject root, string key, int minimum, int maximum)
    {
        if (root[key] is not JsonArray array || array.Count < minimum || array.Count > maximum)
            throw new InvalidOperationException($"Runtime {key} must contain {minimum}–{maximum} named entries, including the selected arm's exact mapping.");
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is not JsonObject item || !result.TryAdd(Text(item, "name"), item)) throw new InvalidOperationException($"Runtime {key} names are missing or duplicated.");
        }
        return result;
    }
    private static JsonObject Required(Dictionary<string, JsonObject> values, string key) => values.TryGetValue(key, out var value) ? value : throw new InvalidOperationException("Missing runtime joint/actuator " + key + ".");
    private static string OptionalText(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null ? text : "";
    private static string Text(JsonObject root, string key) => OptionalText(root, key) is string text && text.Length is > 0 and <= 256 ? text : throw new InvalidOperationException("Missing runtime text field: " + key + ".");
    private static bool Flag(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) ? flag : throw new InvalidOperationException("Missing runtime flag: " + key + ".");
    private static double Number(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : throw new InvalidOperationException("Invalid runtime number: " + key + ".");
    private static double NumberAt(JsonObject root, string key, int index) => root[key] is JsonArray array && array[index] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : throw new InvalidOperationException("Invalid runtime actuator coefficient.");
    private static long IntegerText(JsonObject root, string key) => long.TryParse(Text(root, key), NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value >= 0 ? value : throw new InvalidOperationException("Invalid runtime timestamp: " + key + ".");
    private static void Signature(StringBuilder target, string name, params double[] values) => target.Append(name).Append(':').AppendJoin(',', values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))).Append(';');
}

/// <summary>One explicit follow session; any failed runtime validation permanently stops this instance.</summary>
public sealed class SimulationFollowPolicy
{
    public const double MaximumTimeScale = .2;
    public const double MaximumSourceAgeMilliseconds = 250;
    public const double MaximumRequestMilliseconds = 150;
    public const double MaximumGripperFeedbackAgeMilliseconds = Pika.PikaFollowSession.InputDeadlineSeconds * 1000;
    public const double StartJointToleranceDegrees = 1;
    public const double StartGripperToleranceMm = 2;
    public const double TrackingJointToleranceDegrees = 5;
    public const double TrackingGripperToleranceMm = 10;
    private SimulationFollowSample previous;
    private readonly string deviceId;
    private readonly long generation;
    private readonly string hardwareLimits;
    private double[]? previousVelocity;
    private double previousInterval;
    private double previousSimulationInterval;
    private long lastObservedSequence, lastObservedMonotonicNs, lastObservedSceneTimeNs;
    private bool pauseTracking;
    private SimulationFollowSample? pausedReference;
    private SimulationFollowSample? latestPaused;
    public bool Stopped { get; private set; }
    public string StopReason { get; private set; } = "";
    public bool IsPauseTracking => pauseTracking;

    public static bool ReadyObservationFresh(SimulationFollowSample sample, long now, double requestDuration) =>
        sample.Ready && double.IsFinite(requestDuration) && requestDuration is >= 0 and <= MaximumRequestMilliseconds &&
        now - sample.CapturedAtUnixMicroseconds / 1000d is >= -50 and <= MaximumSourceAgeMilliseconds;

    private SimulationFollowPolicy(SimulationFollowSample sample, SimulationFollowHardware hardware)
    {
        previous = sample; deviceId = hardware.DeviceId; generation = hardware.Generation; hardwareLimits = LimitSignature(hardware);
        Observe(sample);
    }

    public static SimulationFollowPolicy Start(SimulationFollowSample sample, SimulationFollowIdentity expectedIdentity, SimulationFollowHardware hardware,
        long receivedAtUnixMilliseconds, double requestDurationMilliseconds)
    {
        ValidateCandidate(sample, expectedIdentity, hardware, receivedAtUnixMilliseconds, requestDurationMilliseconds);
        ValidatePose(sample, hardware, start: true);
        return new SimulationFollowPolicy(sample, hardware);
    }

    /// <summary>Checks a possible Match target without authorizing or performing that physical move.</summary>
    public static void ValidateCandidate(SimulationFollowSample sample, SimulationFollowIdentity expectedIdentity, SimulationFollowHardware hardware,
        long receivedAtUnixMilliseconds, double requestDurationMilliseconds)
    {
        ValidateFresh(sample, receivedAtUnixMilliseconds, requestDurationMilliseconds);
        if (sample.Identity != expectedIdentity || new[] { expectedIdentity.SourceSceneId, expectedIdentity.RunId, expectedIdentity.EntityId, expectedIdentity.ModelAssetId, expectedIdentity.ModelRevision }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Simulation follow identity changed. Select the intended runtime robot again.");
        if (sample.Mode is not ("play" or "pause") || sample.SceneId != sample.RunId)
            throw new InvalidOperationException("Start Follow simulation only in the selected Play session.");
        ValidateHardware(hardware);
        ValidateCommandRanges(sample, hardware);
    }

    public static void ValidatePausedStart(SimulationFollowSample sample, SimulationFollowSample reference)
    {
        if (sample.Mode != "pause") throw new InvalidOperationException("The prepared simulation must remain paused.");
        ValidateStartFrame(sample, reference);
    }

    private static void ValidateStartFrame(SimulationFollowSample sample, SimulationFollowSample reference)
    {
        if (!sample.BindsTo(reference) || sample.SceneId != sample.RunId ||
            sample.SimulationTimeNs != reference.SimulationTimeNs || sample.SimTimeSeconds != reference.SimTimeSeconds ||
            !sample.CommandDegrees.SequenceEqual(reference.CommandDegrees) || !sample.MeasuredDegrees.SequenceEqual(reference.MeasuredDegrees) ||
            sample.CommandOpeningMm != reference.CommandOpeningMm || sample.MeasuredOpeningMm != reference.MeasuredOpeningMm)
            throw new InvalidOperationException("The prepared first frame changed. Stop following and prepare a new start.");
        if (sample.CommandDegrees.Zip(sample.MeasuredDegrees, (target, measured) => Math.Abs(target - measured)).Any(error => error > StartJointToleranceDegrees) ||
            Math.Abs(sample.CommandOpeningMm - sample.MeasuredOpeningMm) > StartGripperToleranceMm)
            throw new InvalidOperationException("The simulation's first-frame measurements do not match its actuator targets. Fix the simulated start pose before moving the real robot.");
    }

    /// <summary>The activation ACK is captured on the native action thread before
    /// its first physics step. Paused admission latency is not active trajectory
    /// time; require that exact unchanged frame before starting the active clock.</summary>
    public void ActivatePreparedStart(SimulationFollowSample sample, SimulationFollowHardware hardware, long now, double requestDuration, bool orderedSource = false)
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        try
        {
            ValidateFresh(sample, now, requestDuration, maximumRequestMilliseconds: 400, maximumTimeScale: orderedSource ? 1 : MaximumTimeScale);
            ValidateHardware(hardware);
            if (sample.Mode != "play" || hardware.DeviceId != deviceId || hardware.Generation != generation || LimitSignature(hardware) != hardwareLimits ||
                sample.Sequence <= lastObservedSequence || sample.CapturedMonotonicNs <= lastObservedMonotonicNs)
                throw new InvalidOperationException("The prepared activation acknowledgement changed its scope or order.");
            ValidateStartFrame(sample, previous);
            ValidatePose(sample, hardware, start: true);
            previous = sample; previousVelocity = null; previousInterval = previousSimulationInterval = 0;
            Observe(sample);
        }
        catch (InvalidOperationException error) { Stop(error.Message); throw; }
    }

    /// <summary>Keep an unchanged prepared frame ready indefinitely, rebasing its
    /// wall-clock baseline without advancing simulation or relaxing pose checks.</summary>
    public void ValidateReady(SimulationFollowSample sample, SimulationFollowHardware hardware, long now, double requestDuration)
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        try
        {
            ValidateFresh(sample, now, requestDuration);
            ValidateHardware(hardware);
            if (hardware.DeviceId != deviceId || hardware.Generation != generation || LimitSignature(hardware) != hardwareLimits ||
                sample.Sequence <= lastObservedSequence || sample.CapturedMonotonicNs <= lastObservedMonotonicNs)
                throw new InvalidOperationException("Prepared start feedback or hardware scope changed.");
            ValidatePausedStart(sample, previous);
            ValidatePose(sample, hardware, start: true);
            previous = sample; previousVelocity = null; previousInterval = previousSimulationInterval = 0;
            Observe(sample);
        }
        catch (InvalidOperationException error) { Stop(error.Message); throw; }
    }

    public SimulationFollowTarget Advance(SimulationFollowSample sample, SimulationFollowHardware hardware, long receivedAtUnixMilliseconds,
        double requestDurationMilliseconds)
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        try
        {
            if (pauseTracking) throw new InvalidOperationException("Following is waiting for its explicitly owned simulation pause to finish.");
            ValidateFresh(sample, receivedAtUnixMilliseconds, requestDurationMilliseconds);
            ValidateHardware(hardware);
            if (hardware.DeviceId != deviceId || hardware.Generation != generation || LimitSignature(hardware) != hardwareLimits)
                throw new InvalidOperationException("The physical robot connection or reported limits changed.");
            if (!sample.BindsTo(previous) || sample.SceneId != sample.RunId || sample.Mode != "play")
                throw new InvalidOperationException("The simulation session/model changed, paused or stopped. Start a new follow session explicitly.");
            if (sample.Sequence <= lastObservedSequence) throw ClockFailure("observation sequence did not advance", sample);
            if (sample.CapturedMonotonicNs <= lastObservedMonotonicNs) throw ClockFailure("host capture clock did not advance", sample);
            if (sample.SimulationTimeNs < lastObservedSceneTimeNs) throw ClockFailure("scene clock regressed", sample);
            if (sample.SimTimeSeconds <= previous.SimTimeSeconds) throw ClockFailure("MuJoCo physics clock did not advance", sample);
            double interval = (sample.CapturedMonotonicNs - previous.CapturedMonotonicNs) / 1e9;
            double simInterval = sample.SimTimeSeconds - previous.SimTimeSeconds;
            if (interval > .25 || simInterval > interval * MaximumTimeScale + .025)
                throw new InvalidOperationException("Simulation timing advanced too quickly or its follow stream was interrupted.");
            ValidatePose(sample, hardware, start: false);
            var velocity = new double[6];
            for (int i = 0; i < 6; i++)
            {
                var limit = hardware.JointLimits.Single(l => l.JointNumber == i + 1);
                velocity[i] = (sample.CommandDegrees[i] - previous.CommandDegrees[i]) / interval;
                double acceleration = Math.Abs(velocity[i] - (previousVelocity?[i] ?? 0)) / (previousVelocity is null ? interval : (interval + previousInterval) * .5);
                if (Math.Abs(velocity[i]) > limit.MaximumDegreesPerSecond + 1e-6 || acceleration > limit.MaximumDegreesPerSecondSquared + 1e-5)
                {
                    double phaseVelocity = (sample.CommandDegrees[i] - previous.CommandDegrees[i]) / simInterval;
                    double previousPhaseVelocity = previousVelocity is not null && previousSimulationInterval > 0
                        ? previousVelocity[i] * previousInterval / previousSimulationInterval : 0;
                    double phaseAcceleration = Math.Abs(phaseVelocity - previousPhaseVelocity) /
                        (previousVelocity is null ? simInterval : (simInterval + previousSimulationInterval) * .5);
                    throw new InvalidOperationException(FormattableString.Invariant($"""
                        Simulation J{i + 1} target exceeds the robot's reported speed or acceleration limit.
                        Observed v={velocity[i]:R} deg/s (limit {limit.MaximumDegreesPerSecond:R}), a={acceleration:R} deg/s² (limit {limit.MaximumDegreesPerSecondSquared:R});
                        q {previous.CommandDegrees[i]:R} → {sample.CommandDegrees[i]:R} deg; host interval {interval:R}s, previous {previousInterval:R}s;
                        MuJoCo {previous.SimTimeSeconds:R} → {sample.SimTimeSeconds:R}s, phase interval {simInterval:R}s, previous {previousSimulationInterval:R}s;
                        previous observed v={previousVelocity?[i] ?? 0:R}; fixed-scale phase estimate v={phaseVelocity * sample.TimeScale:R}, a={phaseAcceleration * sample.TimeScale * sample.TimeScale:R};
                        scale={sample.TimeScale:R}, host ns {previous.CapturedMonotonicNs} → {sample.CapturedMonotonicNs}.
                        """));
                }
            }
            previous = sample; previousVelocity = velocity; previousInterval = interval; previousSimulationInterval = simInterval;
            Observe(sample);
            return new SimulationFollowTarget(sample.CommandDegrees, sample.CommandOpeningMm, sample.MeasuredDegrees, sample.MeasuredOpeningMm, sample.SimTimeSeconds);
        }
        catch (InvalidOperationException error) { Stop(error.Message); throw; }
    }

    /// <summary>A fresh poll can observe the same native physics step. It may renew only
    /// the identical target, without resetting the derivative or stalled-step deadline.</summary>
    public bool TryValidateUnchanged(SimulationFollowSample sample, SimulationFollowHardware hardware, long receivedAtUnixMilliseconds,
        double requestDurationMilliseconds)
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        // Scene time advances for camera/other runner events between Robot
        // physics ticks. Only mjData.time identifies a new physical state.
        bool sameTime = sample.SimTimeSeconds == previous.SimTimeSeconds;
        if (!sameTime) return false;
        try
        {
            if (pauseTracking) throw new InvalidOperationException("Simulation follow is awaiting explicit catch-up resume.");
            ValidateFresh(sample, receivedAtUnixMilliseconds, requestDurationMilliseconds);
            ValidateHardware(hardware);
            if (hardware.DeviceId != deviceId || hardware.Generation != generation || LimitSignature(hardware) != hardwareLimits ||
                !sample.BindsTo(previous) || sample.SceneId != sample.RunId || sample.Mode != "play")
                throw new InvalidOperationException("The unchanged runtime sample has a different scene, model, mode or physical connection.");
            if (sample.Sequence <= lastObservedSequence) throw ClockFailure("unchanged physics observation sequence did not advance", sample);
            if (sample.CapturedMonotonicNs <= lastObservedMonotonicNs) throw ClockFailure("unchanged physics host capture clock did not advance", sample);
            if (sample.SimulationTimeNs < lastObservedSceneTimeNs)
                throw ClockFailure("scene clock regressed between unchanged physics observations", sample);
            if (sample.CapturedMonotonicNs - previous.CapturedMonotonicNs > 250_000_000L)
                throw new InvalidOperationException("Simulation physics did not advance within 250 ms.");
            if (!sample.CommandDegrees.SequenceEqual(previous.CommandDegrees) || !sample.MeasuredDegrees.SequenceEqual(previous.MeasuredDegrees) ||
                sample.CommandOpeningMm != previous.CommandOpeningMm || sample.MeasuredOpeningMm != previous.MeasuredOpeningMm)
                throw new InvalidOperationException("Simulation controls or position changed without advancing physics time.");
            ValidatePose(sample, hardware, start: false);
            Observe(sample); // Deliberately do not replace previous: it anchors the physics stall and derivatives.
            return true;
        }
        catch (InvalidOperationException error) { Stop(error.Message); throw; }
    }

    /// <summary>Called only for the plugin's explicit catch-up pause, never an arbitrary user pause.</summary>
    public void PauseTracking()
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        if (pauseTracking) return;
        pauseTracking = true; pausedReference = null; latestPaused = null;
    }

    public void ValidatePaused(SimulationFollowSample sample, SimulationFollowHardware hardware, long receivedAtUnixMilliseconds, double requestDurationMilliseconds)
    {
        if (Stopped) throw new InvalidOperationException("Simulation following is stopped: " + StopReason);
        try
        {
            if (!pauseTracking) throw new InvalidOperationException("This simulation pause does not belong to the follow session.");
            ValidateFresh(sample, receivedAtUnixMilliseconds, requestDurationMilliseconds);
            ValidateHardware(hardware);
            if (hardware.DeviceId != deviceId || hardware.Generation != generation || LimitSignature(hardware) != hardwareLimits ||
                !sample.BindsTo(previous) || sample.SceneId != sample.RunId || sample.Mode != "pause")
                throw new InvalidOperationException("Simulation catch-up scope, connection or paused runtime changed.");
            var last = latestPaused ?? previous;
            if (sample.Sequence < Math.Max(last.Sequence, lastObservedSequence) || sample.CapturedMonotonicNs < Math.Max(last.CapturedMonotonicNs, lastObservedMonotonicNs) ||
                sample.SimulationTimeNs < Math.Max(last.SimulationTimeNs, lastObservedSceneTimeNs) || sample.SimTimeSeconds < last.SimTimeSeconds)
                throw new InvalidOperationException("Simulation catch-up sample went backwards.");
            if (pausedReference is not null && (sample.SimulationTimeNs != pausedReference.SimulationTimeNs || sample.SimTimeSeconds != pausedReference.SimTimeSeconds ||
                !sample.CommandDegrees.SequenceEqual(pausedReference.CommandDegrees) || !sample.MeasuredDegrees.SequenceEqual(pausedReference.MeasuredDegrees) ||
                sample.CommandOpeningMm != pausedReference.CommandOpeningMm || sample.MeasuredOpeningMm != pausedReference.MeasuredOpeningMm))
                throw new InvalidOperationException("The paused simulation pose or command changed during catch-up.");
            if (pausedReference is not null && sample.CapturedMonotonicNs - pausedReference.CapturedMonotonicNs > 3_000_000_000L)
                throw new InvalidOperationException("The real robot did not catch up within three seconds.");
            ValidatePose(sample, hardware, start: false);
            pausedReference ??= sample;
            latestPaused = sample;
            Observe(sample);
        }
        catch (InvalidOperationException error) { Stop(error.Message); throw; }
    }

    /// <summary>Reseeds only timing validation after catch-up; issues no robot or engine command.</summary>
    public void ResumeAfterPause(SimulationFollowSample sample, SimulationFollowHardware hardware, long receivedAtUnixMilliseconds, double requestDurationMilliseconds)
    {
        ValidatePaused(sample, hardware, receivedAtUnixMilliseconds, requestDurationMilliseconds);
        previous = sample; previousVelocity = null; previousInterval = previousSimulationInterval = 0;
        pauseTracking = false; pausedReference = null; latestPaused = null;
    }

    public void Stop(string reason)
    {
        Stopped = true; pauseTracking = false; pausedReference = null; latestPaused = null;
        StopReason = string.IsNullOrWhiteSpace(reason) ? "Stopped by user." : reason;
    }

    private void Observe(SimulationFollowSample sample)
    {
        lastObservedSequence = sample.Sequence; lastObservedMonotonicNs = sample.CapturedMonotonicNs;
        lastObservedSceneTimeNs = sample.SimulationTimeNs;
    }

    private InvalidOperationException ClockFailure(string reason, SimulationFollowSample sample) => new(
        $"Simulation timing invalid: {reason}; sequence {lastObservedSequence} → {sample.Sequence}, " +
        $"host ns {lastObservedMonotonicNs} → {sample.CapturedMonotonicNs}, scene ns {lastObservedSceneTimeNs} → {sample.SimulationTimeNs}, " +
        $"MuJoCo seconds {previous.SimTimeSeconds.ToString("R", CultureInfo.InvariantCulture)} → {sample.SimTimeSeconds.ToString("R", CultureInfo.InvariantCulture)}.");

    private static void ValidateFresh(SimulationFollowSample sample, long now, double requestDuration, double maximumRequestMilliseconds = MaximumRequestMilliseconds, double maximumTimeScale = MaximumTimeScale)
    {
        double age = now - sample.CapturedAtUnixMicroseconds / 1000d;
        if (!sample.Ready || !double.IsFinite(requestDuration) || requestDuration < 0 || requestDuration > maximumRequestMilliseconds || age < -50 || age > MaximumSourceAgeMilliseconds)
            throw new InvalidOperationException("Runtime sample is stale, delayed or has an invalid source timestamp.");
        // Native time scale is stored as float; 0.2f widens to 0.20000000298.
        if (sample.TimeScale <= 0 || sample.TimeScale > maximumTimeScale + 1e-6)
            throw new InvalidOperationException("Follow simulation requires Play at 20% speed or slower.");
    }

    private static void ValidateHardware(SimulationFollowHardware hardware)
    {
        if (string.IsNullOrWhiteSpace(hardware.DeviceId) || hardware.Generation <= 0 || !hardware.FeedbackFresh || !hardware.LimitsFresh || !hardware.GripperFresh ||
            hardware.JointDegrees.Count != 6 || hardware.JointDegrees.Any(v => !double.IsFinite(v)) || !double.IsFinite(hardware.GripperOpeningMm) || hardware.GripperOpeningMm < 0 || hardware.GripperOpeningMm > 90 || hardware.JointLimits.Count != 6)
            throw new InvalidOperationException("Fresh physical arm, reported limits and assigned Pika measurements are required.");
        for (int i = 1; i <= 6; i++)
        {
            var limits = hardware.JointLimits.Where(l => l.JointNumber == i).ToArray();
            if (limits.Length != 1 || !double.IsFinite(limits[0].MinimumDegrees) || !double.IsFinite(limits[0].MaximumDegrees) || limits[0].MinimumDegrees >= limits[0].MaximumDegrees ||
                !double.IsFinite(limits[0].MaximumDegreesPerSecond) || limits[0].MaximumDegreesPerSecond <= 0 ||
                !double.IsFinite(limits[0].MaximumDegreesPerSecondSquared) || limits[0].MaximumDegreesPerSecondSquared <= 0 || !limits[0].AccelerationFresh)
                throw new InvalidOperationException("Each physical joint needs fresh reported range, speed and acceleration limits.");
        }
    }

    private static void ValidatePose(SimulationFollowSample sample, SimulationFollowHardware hardware, bool start)
    {
        ValidateCommandRanges(sample, hardware);
        double jointTolerance = start ? StartJointToleranceDegrees : TrackingJointToleranceDegrees;
        double gripperTolerance = start ? StartGripperToleranceMm : TrackingGripperToleranceMm;
        for (int i = 0; i < 6; i++)
        {
            if (Math.Abs(sample.CommandDegrees[i] - hardware.JointDegrees[i]) > jointTolerance || Math.Abs(sample.MeasuredDegrees[i] - hardware.JointDegrees[i]) > jointTolerance)
            {
                if (start) throw new SimulationFollowAlignmentException($"J{i + 1} does not match the simulation start pose within 1°. Match the poses before following.");
                throw new InvalidOperationException($"Real J{i + 1} is more than 5° from the simulation. Following stopped.");
            }
        }
        if (Math.Abs(sample.CommandOpeningMm - hardware.GripperOpeningMm) > gripperTolerance || Math.Abs(sample.MeasuredOpeningMm - hardware.GripperOpeningMm) > gripperTolerance)
        {
            if (start) throw new SimulationFollowAlignmentException("Pika does not match the simulation start opening within 2 mm. Match it before following.");
            throw new InvalidOperationException("Real Pika is more than 10 mm from the simulation. Following stopped.");
        }
    }

    private static void ValidateCommandRanges(SimulationFollowSample sample, SimulationFollowHardware hardware)
    {
        for (int i = 0; i < 6; i++)
        {
            var limit = hardware.JointLimits.Single(l => l.JointNumber == i + 1);
            if (sample.CommandDegrees[i] < limit.MinimumDegrees || sample.CommandDegrees[i] > limit.MaximumDegrees)
                throw new InvalidOperationException($"Simulation J{i + 1} target is outside the robot's reported range.");
        }
    }

    private static string LimitSignature(SimulationFollowHardware hardware) => string.Join(";", hardware.JointLimits.OrderBy(l => l.JointNumber)
        .Select(l => string.Join(",", l.JointNumber, l.MinimumDegrees.ToString("R", CultureInfo.InvariantCulture), l.MaximumDegrees.ToString("R", CultureInfo.InvariantCulture),
            l.MaximumDegreesPerSecond.ToString("R", CultureInfo.InvariantCulture), l.MaximumDegreesPerSecondSquared.ToString("R", CultureInfo.InvariantCulture))));
}
