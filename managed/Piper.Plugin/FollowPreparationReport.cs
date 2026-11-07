using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed record FollowControlApproval(string Backend, string Device, long Generation, string SceneRobot,
    string JointPrefix, string PikaDevice, string Distribution);

public sealed record FollowPreparationIssue(string ProfileId, string RobotName, string Code, string Action)
{
    public string Text => RobotName.Length == 0 ? Action : RobotName + ": " + Action;
}

/// <summary>Pure preflight shared by the sidebar and group admission. It reads
/// one receipt without selecting a device, projecting telemetry or issuing any
/// command. Native admission and live execution remain authoritative.</summary>
public sealed record FollowPreparationReport(IReadOnlyList<FollowPreparationIssue> Issues, int RobotCount)
{
    public bool Ready => RobotCount > 0 && Issues.Count == 0;
    public string Summary => Issues.Count == 0 ? $"{RobotCount} robot{(RobotCount == 1 ? "" : "s")} ready · press Follow, then Play."
        : string.Join("\n", Issues.Select(i => i.Text));

    public static FollowPreparationReport Inspect(IReadOnlyList<RobotProfile> profiles, TelemetryReceipt? receipt,
        string backend, string owner, IReadOnlyDictionary<string, FollowControlApproval> approvals, JsonObject? scene, long nowTicks)
    {
        var issues = new List<FollowPreparationIssue>();
        void Add(RobotProfile? p, string code, string action) => issues.Add(new(p?.Id ?? "", p?.Name ?? "", code, action));
        if (profiles.Count == 0) Add(null, "selection", "Check the robots you want to follow.");
        if (profiles.Count > RobotProfileRouting.MaximumProfiles) Add(null, "selection", "Select at most eight robots.");
        if (receipt is null || receipt.BackendToken != backend || !receipt.IsFresh(nowTicks))
        {
            if (profiles.Count > 0) Add(null, "feedback", "Wait for fresh controller feedback before following.");
            return new(issues, profiles.Count);
        }
        if (receipt.State["multi_device_lanes_supported"]?.GetValue<bool>() != true)
            Add(null, "backend", "Update the backend to use independent robot control lanes.");
        string? world = profiles.FirstOrDefault()?.RobotId;
        bool sceneKnown = scene?["sceneId"]?.GetValue<string>() is { Length: > 0 } && scene["robots"] is JsonArray;
        var bindings = SceneRobotBindings.Options(scene?["robots"] is JsonArray robots ? robots.OfType<JsonObject>() : []);
        foreach (var p in profiles)
        {
            if (profiles.Count(other => other.Id == p.Id) != 1) Add(p, "profile", "Use a distinct robot profile.");
            if (!double.IsFinite(p.Speed) || p.Speed is <= 0 or > 100) Add(p, "speed", "Choose a speed above 0% and at most 100%.");
            if (p.CanDeviceId.Length == 0) Add(p, "can-assignment", "Assign this robot's CAN adapter in Settings.");
            else if (profiles.Any(other => other.Id != p.Id && other.CanDeviceId == p.CanDeviceId))
                Add(p, "can-assignment", "Assign a different CAN adapter; another selected robot uses this one.");
            if (p.PikaDevice.Length == 0) Add(p, "pika-assignment", "Assign this robot's Pika gripper in Settings.");
            else if (profiles.Any(other => other.Id != p.Id && other.PikaDevice == p.PikaDevice && other.WslDistribution == p.WslDistribution))
                Add(p, "pika-assignment", "Assign a different Pika; another selected robot uses this one.");
            if (p.RobotId.Length == 0) Add(p, "scene-binding", "Assign this robot's simulation arm in Settings.");
            else if (p.RobotId != world) Add(p, "scene-world", "Use an arm in the same simulation world as the other selected robots.");
            else if (profiles.Any(other => other.Id != p.Id && other.RobotId == p.RobotId && other.RobotJointPrefix == p.RobotJointPrefix))
                Add(p, "scene-binding", "Assign a different simulation arm; another selected robot uses this one.");
            else if (!sceneKnown || !bindings.Any(b => b.Matches(p)))
                Add(p, "scene-binding", "Refresh and assign the matching simulation arm in Settings.");
            else if (scene!["robots"]!.AsArray().OfType<JsonObject>().Count(r => r["entityId"]?.GetValue<string>() == p.RobotId) != 1)
                Add(p, "scene-binding", "Refresh this simulation arm; its scene identity is ambiguous.");
            else if (scene!["robots"]!.AsArray().OfType<JsonObject>().First(r => r["entityId"]?.GetValue<string>() == p.RobotId) is { } model &&
                (model["modelAssetId"]?.GetValue<string>() is not { Length: > 0 } || model["modelRevision"]?.GetValue<string>() is not { Length: > 0 }))
                Add(p, "scene-model", "Refresh this simulation arm's model identity in Settings.");

            var devices = receipt.State["devices"] is JsonArray inventory
                ? inventory.OfType<JsonObject>().Where(d => d["id"]?.GetValue<string>() == p.CanDeviceId).ToArray() : [];
            if (devices.Length != 1)
            { if (p.CanDeviceId.Length > 0) Add(p, "can-connection", "Connect the assigned CAN adapter; its identity is unavailable or ambiguous."); continue; }
            var device = devices[0];
            long generation = device["generation"]?.GetValue<long>() ?? 0;
            if (generation <= 0 || !approvals.TryGetValue(p.Id, out var approval) || approval !=
                new FollowControlApproval(backend, p.CanDeviceId, generation, p.RobotId, p.RobotJointPrefix, p.PikaDevice, p.WslDistribution))
                Add(p, "approval", "Use Enable robot to confirm this arm and gripper connection.");
            if (device["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true || !receipt.HasFreshDevice(device, nowTicks))
                Add(p, "arm-feedback", "Wait for fresh feedback from this robot's CAN adapter.");
            else
            {
                var status = device["status"];
                bool recovery = device["recovery_required"]?.GetValue<bool>() == true || device["reset_pending"]?.GetValue<bool>() == true ||
                    status?["arm_status"]?.GetValue<int>() == 1;
                var teaching = TeachingState.Inspect(device);
                if (recovery) Add(p, "recovery", "Open recovery and complete the displayed recovery step.");
                else if (teaching.Active) Add(p, "teaching", "Finish hand guiding or onboard playback, turn the teach light off, then use Resume controls.");
                else if (teaching.ReturnRequired || status?["control"]?.GetValue<int>() == 2)
                    Add(p, "teaching-return", "Use Resume controls; recording has ended but this arm is still in teaching control.");
                else if (status?["control"]?.GetValue<int>() != 1) Add(p, "can-control", "Use Enable robot to restore CAN control.");
                if (!recovery && (status?["arm_status"]?.GetValue<int>() != 0 || status?["error"]?.GetValue<int>() != 0))
                    Add(p, "arm-fault", "Inspect this robot's controller fault before following.");
                var joints = device["joints"]!.AsArray(); // HasFreshDevice established exactly six joint objects.
                if (joints.Any(j => j?["enabled"]?.GetValue<bool>() != true)) Add(p, "arm-power", "Use Enable robot; all six motors must be enabled.");
                if (joints.Any(j => j?["fault"]?.GetValue<int>() != 0)) Add(p, "driver-fault", "Inspect the motor-driver fault before following.");
                if (device["firmware_supported"]?.GetValue<bool>() != true || joints.Any(j =>
                    j?["limits_fresh"]?.GetValue<bool>() != true || j["acceleration_fresh"]?.GetValue<bool>() != true ||
                    !Finite(j["actual_deg"]) || !Finite(j["min_deg"]) || !Finite(j["max_deg"]) ||
                    !Positive(j["max_speed_rad_s"]) || !Positive(j["max_acceleration_rad_s2"]) ||
                    j["min_deg"]!.GetValue<double>() >= j["max_deg"]!.GetValue<double>()))
                    Add(p, "configuration", "Read supported firmware and fresh joint limits in Settings.");
                foreach (var joint in joints.OfType<JsonObject>())
                    if (Finite(joint["actual_deg"]) && Finite(joint["min_deg"]) && Finite(joint["max_deg"]) &&
                        (joint["actual_deg"]!.GetValue<double>() < joint["min_deg"]!.GetValue<double>() - .1 ||
                         joint["actual_deg"]!.GetValue<double>() > joint["max_deg"]!.GetValue<double>() + .1))
                        Add(p, "joint-range", $"J{joint["index"]} is outside its reported range; complete the displayed range recovery first.");
                if (device["motion"] is not JsonObject motion || motion["device_id"]?.GetValue<string>() != p.CanDeviceId ||
                    motion["generation"]?.GetValue<long>() != generation)
                    Add(p, "motion-identity", "Wait for this connection's independent motion feedback.");
                else if (motion["active"]?.GetValue<bool>() == true) Add(p, "motion-active", "Finish the current movement before following.");
                else if (motion["query_active"]?.GetValue<bool>() == true) Add(p, "motion-query", "Wait for this robot's configuration query to finish.");
            }
            JsonObject? pika;
            try { pika = ProfilePikaRouting.Find(receipt.State, p); }
            catch (InvalidOperationException) { pika = null; } // Duplicate identities are an unavailable assignment, never a best guess.
            if (p.PikaDevice.Length == 0) continue;
            if (pika is null || !receipt.HasFreshPika(pika, nowTicks, SimulationFollowPolicy.MaximumGripperFeedbackAgeMilliseconds))
                Add(p, "pika-feedback", "Connect the assigned Pika and wait for fresh gripper feedback.");
            else
            {
                if (pika["owner"]?.GetValue<string>() != owner) Add(p, "pika-owner", "Use Enable robot to connect and confirm control of this Pika.");
                if (pika["enabled"]?.GetValue<bool>() != true || pika["armed"]?.GetValue<bool>() != true)
                    Add(p, "pika-power", "Use Enable robot to enable this robot's Pika gripper.");
                if (pika["driver_fault"]?.GetValue<bool>() == true) Add(p, "pika-fault", "Inspect this Pika's motor-driver fault before following.");
                // A small closed-position offset can have a saved display
                // reference. Runtime admission validates that exact reference.
                if (!GripperLink.TryMeasuredOpening(pika, out _) && !GripperLink.ClosedDisplayReferenceNeeded(pika))
                    Add(p, "pika-position", "Wait for a valid measured Pika jaw position.");
                if (pika["pending"]?.GetValue<bool>() == true || pika["following"]?.GetValue<bool>() == true || pika["resuming_hold"]?.GetValue<bool>() == true)
                    Add(p, "pika-motion", "Wait for this Pika's current movement to finish.");
            }
        }
        return new(issues, profiles.Count);
    }

    private static bool Finite(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number);
    private static bool Positive(JsonNode? node) => Finite(node) && node!.GetValue<double>() > 0;
}
