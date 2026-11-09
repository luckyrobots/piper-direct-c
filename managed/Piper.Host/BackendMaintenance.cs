using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LuckyRobots.Piper.Host;

public sealed record BackendRestartResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("backend_process_id")] int BackendProcessId,
    [property: JsonPropertyName("backend_instance_id")] string BackendInstanceId,
    [property: JsonPropertyName("restart_mode")] string RestartMode,
    [property: JsonPropertyName("supervisor_process_id")] int? SupervisorProcessId,
    [property: JsonPropertyName("supervisor_instance_id")] string? SupervisorInstanceId,
    [property: JsonPropertyName("releases_gripper_torque")] bool ReleasesGripperTorque,
    [property: JsonPropertyName("recovery_preserved")] bool RecoveryPreserved);

/// <summary>Freezes new host control admission before checking process-exit preconditions.
/// This service never invokes a robot command. Normal agent disposal releases
/// gripper power; native persisted STOP/recovery records are deliberately unchanged.</summary>
public sealed class BackendMaintenance
{
    public const string RestartRoute = HostRoutes.Restart;
    public const int RestartExitCode = 75;
    public const string PendingMessage = "Backend restart is in progress. Wait for the new server session; no commands will be replayed.";
    private const string ShutdownPending = "Backend shutdown is in progress. New controls are closed; STOP remains available.";
    private enum Operation { None, Restart, Shutdown }
    private readonly object gate = new();
    private readonly Func<JsonObject> armSnapshot;
    private readonly Func<JsonArray> pikaSnapshots;
    private readonly Action restart;
    private readonly TimeSpan responseDrain;
    private int controls;
    private Operation pending;
    private bool committed;
    private string preparedOwner = "", lastFailure = "";
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; }
    public int? SupervisorProcessId { get; }
    public string? SupervisorInstanceId { get; }
    public string? PluginOwner { get; }
    public bool Supervised => SupervisorProcessId.HasValue && SupervisorInstanceId is not null;
    public bool Supported => Supervised || PluginOwner is not null;
    public string Mode => Supervised ? "supervisor" : PluginOwner is not null ? "plugin" : "external";

    public BackendMaintenance(Func<JsonObject> armSnapshot, Func<JsonArray> pikaSnapshots, Action restart,
        int processId, int? supervisorProcessId = null, string? supervisorInstanceId = null, TimeSpan? responseDrain = null,
        string? pluginOwner = null)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (supervisorProcessId.HasValue != (supervisorInstanceId is not null) ||
            supervisorProcessId is <= 0 || supervisorInstanceId is not null && !Guid.TryParse(supervisorInstanceId, out _))
            throw new ArgumentException("Restart supervision needs its exact process and instance identity.");
        if (pluginOwner is not null && !Guid.TryParse(pluginOwner, out _)) throw new ArgumentException("Invalid plugin owner identity.");
        this.armSnapshot = armSnapshot; this.pikaSnapshots = pikaSnapshots; this.restart = restart;
        ProcessId = processId; SupervisorProcessId = supervisorProcessId; SupervisorInstanceId = supervisorInstanceId;
        PluginOwner = pluginOwner;
        this.responseDrain = responseDrain ?? TimeSpan.FromMilliseconds(150);
        if (this.responseDrain < TimeSpan.Zero || this.responseDrain > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(responseDrain));
    }

    public JsonObject Snapshot()
    {
        lock (gate) return new JsonObject {
            ["supported"] = Supported, ["mode"] = Mode, ["plugin_owner"] = PluginOwner,
            ["endpoint"] = RestartRoute, ["pending"] = pending != Operation.None,
            ["supervisor_process_id"] = SupervisorProcessId, ["supervisor_instance_id"] = SupervisorInstanceId,
            ["releases_gripper_torque"] = true, ["preserves_arm_recovery"] = true,
            ["last_failure"] = lastFailure,
            ["message"] = lastFailure.Length > 0 ? lastFailure : pending == Operation.Shutdown ? ShutdownPending :
                Supported ? "Restart releases Pika holding torque. Arm STOP/recovery remains latched." :
                "This backend has no restart supervisor. Restart it with its original launcher; no shutdown was requested."
        };
    }

    internal bool TryEnterControl(string route, out IDisposable? admission, out string? error)
    {
        lock (gate)
        {
            // Emergency STOP remains available, including after admission is
            // frozen. It cannot start motion or clear a persisted recovery latch.
            if (pending != Operation.None && !HostRoutes.IsEmergencyStop(route))
            {
                admission = null;
                error = pending == Operation.Shutdown ? ShutdownPending : PendingMessage;
                return false;
            }
            ++controls; admission = new Admission(this); error = null; return true;
        }
    }

    public BackendRestartResult Prepare(JsonElement input)
    {
        if (!Supported) return Result(false, "This backend has no restart supervisor or owning plugin. Use its original launcher or Restart-WindowsDashboard.ps1; no shutdown was requested.");
        if (!input.TryGetProperty("owner", out var ownerValue) || ownerValue.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(ownerValue.GetString(), out _)) return Result(false, "Restart requires the current plugin owner identity.");
        if (PluginOwner is not null && ownerValue.GetString() != PluginOwner)
            return Result(false, "Only the plugin session that owns this backend may restart it.");
        if (!input.TryGetProperty("backend_process_id", out var process) || process.ValueKind != JsonValueKind.Number || !process.TryGetInt32(out int pid) || pid != ProcessId ||
            !input.TryGetProperty("backend_instance_id", out var instance) || instance.ValueKind != JsonValueKind.String || instance.GetString() != InstanceId)
            return Result(false, "The backend session changed. Read fresh state before requesting restart.");
        if (!input.TryGetProperty("release_grippers", out var release) || release.ValueKind != JsonValueKind.True)
            return Result(false, "Confirm that all grippers are empty: restarting releases Pika holding torque.");
        string owner = ownerValue.GetString()!;
        if (PrepareExit(Operation.Restart, () => Check(armSnapshot(), pikaSnapshots(), owner)) is { } blocker)
            return Result(false, blocker);
        preparedOwner = owner;
        return Result(true, "Restart accepted. Pika holding torque will be released; arm power is unchanged and existing STOP/recovery remains latched. " +
            (Supervised ? "The supervisor restarts in monitoring mode without replaying targets." :
                "The owning plugin must wait for this exact process to exit75, then start one new monitoring-only backend. No targets are replayed."));
    }

    public void Commit()
    {
        if (!BeginCommit(Operation.Restart)) return;
        // Both HTTP and gRPC receive the acknowledgement before Kestrel drains.
        // Client disconnect cannot leave an accepted restart half committed.
        _ = Task.Run(async () => {
            await Task.Delay(responseDrain);
            RecheckAndExit("Restart", () => Check(armSnapshot(), pikaSnapshots(), preparedOwner), restart);
        });
    }

    public string? PrepareShutdown() => PrepareExit(Operation.Shutdown, CheckShutdown);

    public void CommitShutdown(Action shutdown)
    {
        if (BeginCommit(Operation.Shutdown)) RecheckAndExit("Shutdown", CheckShutdown, shutdown);
    }

    private string? PrepareExit(Operation operation, Func<string?> check)
    {
        lock (gate)
        {
            if (pending != Operation.None) return pending == Operation.Shutdown ? ShutdownPending : PendingMessage;
            if (controls != 0) return operation == Operation.Restart
                ? "A controller request is still in progress. Wait for it to finish before restarting."
                : "A controller request is still in progress. Wait for it to finish before shutting down.";
            pending = operation; lastFailure = "";
        }
        bool accepted = false;
        try
        {
            string? blocker = check();
            accepted = blocker is null;
            return blocker;
        }
        finally { if (!accepted) lock (gate) pending = Operation.None; }
    }

    private bool BeginCommit(Operation operation)
    {
        lock (gate)
        {
            if (pending != operation) throw new InvalidOperationException(operation + " was not admitted.");
            if (committed) return false;
            committed = true;
            return true;
        }
    }

    private void RecheckAndExit(string operation, Func<string?> check, Action exit)
    {
        // A physical teach button or motor can change during the response drain.
        try
        {
            if (check() is { } blocker) CancelCommit(operation + " cancelled: " + blocker);
            else exit();
        }
        catch (Exception error) { CancelCommit(operation + " cancelled: state could not be verified. " + error.Message); }
    }

    private string? CheckShutdown()
    {
        var arms = armSnapshot();
        if (ControllerActivity.AnyMotion(arms) || ControllerActivity.AnyQuery(arms))
            return "Stop active arm actions and finish queries before shutting down the server.";
        if (pikaSnapshots().OfType<JsonObject>().Any(pika => pika["pending"]?.GetValue<bool>() == true ||
            pika["following"]?.GetValue<bool>() == true || pika["resuming_hold"]?.GetValue<bool>() == true ||
            pika["connected"]?.GetValue<bool>() == true && Finite(pika["telemetry"]?["motor"] as JsonObject, "Speed", out double speed) && Math.Abs(speed) > .02))
            return "Finish active Pika gripper movements before shutting down the server.";
        return null;
    }

    public static string? Check(JsonObject state, JsonArray pikas, string owner)
    {
        if (ControllerActivity.AnyMotion(state) || state["motion"]?["active"]?.GetValue<bool>() != false)
            return "Stop following or finish the current arm action before restarting. Restart never interrupts an active movement.";
        if (ControllerActivity.AnyQuery(state) || state["query_active"]?.GetValue<bool>() != false)
            return "Wait for the firmware/limits query to finish before restarting.";
        if (state["storage_failed"]?.GetValue<bool>() != false)
            return "The recovery-state file could not be saved. Repair storage before restarting; STOP state must be preserved.";
        if (state["devices"] is not JsonArray devices || devices.Any(device => device is not JsonObject))
            return "Arm inventory is unavailable; restart cannot verify an idle controller.";
        foreach (var device in devices.OfType<JsonObject>())
        {
            string name = device["name"]?.GetValue<string>() ?? device["id"]?.GetValue<string>() ?? "Robot";
            string Wait(string reason) => name + ": " + reason;
            if (device["present"]?.GetValue<bool>() != true || device["up"]?.GetValue<bool>() != true ||
                device["stable_identity"]?.GetValue<bool>() != true || device["link_verified"]?.GetValue<bool>() != true ||
                device["fresh"]?.GetValue<bool>() != true || device["firmware_supported"]?.GetValue<bool>() != true)
                return Wait("fresh verified arm feedback is required before restart. Reconnect/read its information first.");
            if (device["reset_pending"]?.GetValue<bool>() != false)
                return Wait("finish the existing reset/recovery operation before restarting.");
            if (device["status"] is not JsonObject status ||
                status["error"]?.GetValue<int>() != 0 || status["joint_communication_fault_mask"]?.GetValue<int>() != 0 ||
                status["joint_angle_limit_fault_mask"]?.GetValue<int>() != 0 ||
                status["arm_status"]?.GetValue<int>() is not (0 or 1))
                return Wait("inspect the hardware fault before restarting; transport restart cannot clear it.");
            // A stationary controller STOP is allowed so a stuck UI can reload.
            // It is not treated as teach/standby and is never cleared here.
            bool stopped = status["arm_status"]?.GetValue<int>() == 1;
            if (!stopped && (status["control"]?.GetValue<int>() == 2 ||
                    status["teach_state"]?.GetValue<int>() is not (0 or 2 or 6)) ||
                device["teaching"]?["active"]?.GetValue<bool>() == true ||
                device["teaching"]?["handoff_in_progress"]?.GetValue<bool>() == true)
                return Wait("leave physical teaching/replay and finish the CAN handoff before restarting.");
            if (!Finite(status, "pose_stationary_s", out double stationary) || stationary < .4)
                return Wait("hold the arm still until fresh pose feedback confirms 0.4 seconds without movement.");
            if (device["joints"] is not JsonArray { Count: 6 } joints || joints.OfType<JsonObject>().Count() != 6)
                return Wait("all six joint samples are required before restart.");
            foreach (var joint in joints.OfType<JsonObject>())
                if (joint["fresh"]?.GetValue<bool>() != true || joint["fault"]?.GetValue<int>() != 0 ||
                    !Finite(joint, "actual_deg", out _) || !Finite(joint, "motor_position_raw", out _) ||
                    joint["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out _))
                    return Wait("wait for fresh, fault-free position and motor feedback from all six joints.");
        }
        if (pikas.Any(pika => pika is not JsonObject)) return "Gripper inventory is invalid; restart cannot verify idle Pika sessions.";
        foreach (var pika in pikas.OfType<JsonObject>())
        {
            string name = pika["device"]?.GetValue<string>() ?? "Pika";
            bool connected = pika["connected"]?.GetValue<bool>() == true;
            bool powered = pika["power_requested"]?.GetValue<bool>() == true;
            bool uncertain = pika["power_uncertain"]?.GetValue<bool>() == true;
            if (!connected && !powered && !uncertain) continue; // An unused read-only assignment has no power to release.
            if (powered && pika["owner"]?.GetValue<string>() != owner)
                return name + ": another client owns this powered gripper. Stop it from that client before restarting.";
            if (!connected || pika["fresh"]?.GetValue<bool>() != true ||
                pika["pending"]?.GetValue<bool>() != false || pika["following"]?.GetValue<bool>() != false ||
                pika["resuming_hold"]?.GetValue<bool>() == true || pika["driver_fault"]?.GetValue<bool>() != false ||
                !Finite(pika["telemetry"]?["motor"] as JsonObject, "Speed", out double speed) || Math.Abs(speed) > .02 ||
                (powered || uncertain) && pika["post_request_feedback_verified"]?.GetValue<bool>() != true)
                return name + ": stop gripper following and wait for fresh stationary, acknowledged motor feedback before restarting.";
        }
        return null;
    }

    private static bool Finite(JsonObject? value, string key, out double number)
    {
        number = double.NaN;
        if (value?[key] is not JsonValue element) return false;
        if (element.TryGetValue<double>(out number)) return double.IsFinite(number);
        if (element.TryGetValue<int>(out int integer)) { number = integer; return true; }
        if (element.TryGetValue<long>(out long wide)) { number = wide; return true; }
        return false;
    }
    private void CancelCommit(string reason)
    {
        lock (gate) { lastFailure = reason; pending = Operation.None; committed = false; preparedOwner = ""; }
    }
    private BackendRestartResult Result(bool ok, string message) => new(ok, message, ProcessId, InstanceId, Mode,
        SupervisorProcessId, SupervisorInstanceId, true, true);
    private sealed class Admission(BackendMaintenance owner) : IDisposable
    {
        private BackendMaintenance? current = owner;
        public void Dispose() { var value = Interlocked.Exchange(ref current, null); if (value is not null) lock (value.gate) --value.controls; }
    }
}
