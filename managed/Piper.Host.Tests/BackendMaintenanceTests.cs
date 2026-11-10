using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyRobots.Piper.Host;

internal static class BackendMaintenanceTests
{
    internal static async Task Run()
    {
        const string owner = "11111111-1111-1111-1111-111111111111";
        var state = Arm(); var pikas = new JsonArray(Pika(owner));
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int restartCalls = 0;
        var service = new BackendMaintenance(() => state, () => pikas, () => {
            Interlocked.Increment(ref restartCalls); restarted.TrySetResult();
        }, 123, 321, "22222222-2222-2222-2222-222222222222", TimeSpan.Zero);
        JsonElement Input(bool release = true, string? instance = null, int pid = 123) => JsonSerializer.SerializeToElement(new {
            owner, backend_process_id = pid, backend_instance_id = instance ?? service.InstanceId, release_grippers = release
        });
        Check(!service.Prepare(Input(false), out _).Ok, "Restart ignored explicit gripper-release acknowledgement");
        Check(!service.Prepare(Input(instance: "previous-session"), out _).Ok, "Restart accepted an obsolete host instance");
        Check(!service.Prepare(Input(pid: 124), out _).Ok, "Restart accepted another process identity");
        Check(!service.Prepare(JsonSerializer.SerializeToElement(new { owner, backend_process_id = "123" }), out _).Ok,
            "Malformed process ID was admitted");
        Check(service.TryEnterControl("/api/heartbeat", out var active, out _), "Normal request admission failed");
        Check(!service.Prepare(Input(), out _).Ok, "Restart raced an in-flight control request");
        active!.Dispose(); active.Dispose(); // release is idempotent
        string before = state.ToJsonString() + pikas.ToJsonString();
        state["motion"]!["active"] = true;
        Check(!service.Prepare(Input(), out _).Ok, "Restart interrupted an active action");
        state["motion"]!["active"] = false;
        Check(service.TryEnterControl("/api/query", out var afterRejected, out _), "Rejected restart left admission frozen");
        afterRejected!.Dispose();
        Check(service.Prepare(Input(), out var commit).Ok, "Fresh stationary enabled Pika with a retained power reservation was rejected");
        Check(state.ToJsonString() + pikas.ToJsonString() == before, "Maintenance changed robot/recovery/power snapshots");
        Check(!service.TryEnterControl("/api/command", out _, out _), "Accepted restart allowed a new target");
        Check(service.TryEnterControl("/api/stop", out var emergency, out _), "Restart blocked emergency arm STOP");
        emergency!.Dispose();
        Check(!service.Prepare(Input(), out _).Ok, "Duplicate restart was admitted");
        commit!(); commit(); // the returned commit is one-shot
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(restartCalls == 1, "Restart callback ran more than once");
        var changed = Arm();
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var raced = new BackendMaintenance(() => {
            if (Interlocked.Increment(ref reads) > 1) { changed["motion"]!["active"] = true; secondRead.TrySetResult(); }
            return changed;
        }, () => [], () => throw new Exception("Moving arm was restarted"), 123, 321,
            "22222222-2222-2222-2222-222222222222", TimeSpan.Zero);
        var racedInput = JsonSerializer.SerializeToElement(new {
            owner, backend_process_id = 123, backend_instance_id = raced.InstanceId, release_grippers = true
        });
        Check(raced.Prepare(racedInput, out var racedCommit).Ok, "Physical-change race fixture did not admit its initially idle state");
        racedCommit!();
        await secondRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Wait for the same callback task to publish its cancellation result.
        for (int i = 0; i < 100 && raced.Snapshot()["pending"]!.GetValue<bool>(); ++i) await Task.Delay(1);
        Check(raced.Snapshot()["pending"]!.GetValue<bool>() == false &&
            raced.Snapshot()["last_failure"]!.GetValue<string>().Contains("Restart cancelled", StringComparison.Ordinal),
            "Physical movement during acknowledgement did not cancel restart and unfreeze admission");
        var manual = new BackendMaintenance(Arm, () => [], () => throw new Exception("External host was stopped"), 123);
        Check(!manual.Prepare(Input(), out _).Ok && manual.Snapshot()["supported"]!.GetValue<bool>() == false,
            "Unsupervised host promised automatic restart");
        var plugin = new BackendMaintenance(Arm, () => [], () => { }, 123, pluginOwner: owner);
        var pluginInput = JsonSerializer.SerializeToElement(new {
            owner, backend_process_id = 123, backend_instance_id = plugin.InstanceId, release_grippers = true
        });
        Check(plugin.Snapshot()["mode"]!.GetValue<string>() == "plugin" && plugin.Prepare(pluginInput, out _).Ok,
            "Bundled session-owned host did not support its owning plugin restart");
        var foreignPlugin = new BackendMaintenance(Arm, () => [], () => { }, 123, pluginOwner: Guid.NewGuid().ToString());
        var foreignInput = JsonSerializer.SerializeToElement(new {
            owner, backend_process_id = 123, backend_instance_id = foreignPlugin.InstanceId, release_grippers = true
        });
        Check(!foreignPlugin.Prepare(foreignInput, out _).Ok, "Another plugin acquired a directly owned backend restart");

        BackendMaintenance? probe = null;
        bool checkingPending = true, checkingAdmitted = true;
        probe = new BackendMaintenance(() => {
            checkingPending = probe!.Snapshot()["pending"]!.GetValue<bool>();
            checkingAdmitted = probe.TryEnterControl("/api/command", out var lease, out _);
            lease?.Dispose();
            var arm = Arm(); arm["motion"]!["active"] = true; return arm;
        }, () => [], () => { }, 123);
        Check(probe.PrepareShutdown(out var rejected) is not null && rejected is null, "Moving fixture was admitted for shutdown");
        Check(!checkingPending && !checkingAdmitted, "Checking reported pending or admitted a new control");
        Check(probe.TryEnterControl("/api/command", out var reopened, out _) && !probe.Snapshot()["pending"]!.GetValue<bool>(),
            "A rejected check did not reopen admission");
        reopened!.Dispose();

        var shutdownArm = Arm();
        var shutdownPika = Pika(owner);
        var shutdownGate = new BackendMaintenance(() => shutdownArm, () => new JsonArray(shutdownPika.DeepClone()),
            () => throw new Exception("Shutdown selected the restart exit action"), 123);
        int shutdownCalls = 0;
        void Shutdown() => ++shutdownCalls;
        Check(shutdownGate.TryEnterControl("/api/pika/position", out var gripperWrite, out _), "Fixture could not admit gripper write");
        Check(shutdownGate.PrepareShutdown(out _) is not null, "Shutdown raced an already admitted gripper command");
        gripperWrite!.Dispose();
        Check(shutdownGate.PrepareShutdown(out var shutdownCommit) is null, "Legacy shutdown rejected an idle backend");
        Check(!shutdownGate.TryEnterControl("/api/command", out _, out _) && !shutdownGate.TryEnterControl("/api/pika/enable", out _, out _),
            "Shutdown admitted a new arm target or gripper power request before response completion");
        Check(shutdownGate.TryEnterControl("/api/stop/coordinated", out var shutdownStop, out _), "Shutdown blocked coordinated STOP");
        shutdownStop!.Dispose();
        shutdownArm["motion"]!["active"] = true;
        shutdownCommit!(Shutdown);
        Check(shutdownCalls == 0 && shutdownGate.Snapshot()["last_failure"]?.GetValue<string>().StartsWith("Shutdown cancelled", StringComparison.Ordinal) == true,
            "Shutdown did not recheck after its response");
        shutdownArm["motion"]!["active"] = false;
        shutdownPika["following"] = true;
        Check(shutdownGate.PrepareShutdown(out _) is not null, "Shutdown interrupted active gripper following");
        shutdownPika["following"] = false;
        Check(shutdownGate.PrepareShutdown(out var retried) is null, "Cancelled shutdown did not unfreeze the next attempt");
        retried!(Shutdown);
        retried(Shutdown);
        Check(shutdownCalls == 1, "Legacy shutdown callback did not run exactly once");

        void Reject(Action<JsonObject, JsonArray> mutate, string label)
        {
            var arm = Arm(); var grippers = new JsonArray(Pika(owner)); mutate(arm, grippers);
            Check(BackendMaintenance.Check(arm, grippers, owner) is not null, label);
        }
        Reject((arm, _) => arm["query_active"] = true, "Restart raced metadata query");
        Reject((arm, _) => arm["any_motion_active"] = true, "Restart ignored nonselected arm movement");
        Reject((arm, _) => arm["any_query_active"] = true, "Restart ignored nonselected metadata query");
        Reject((arm, _) => arm["motions"] = new JsonArray(new JsonObject { ["device_id"] = "other", ["active"] = true }),
            "Restart trusted an idle selected projection over an active device lane");
        Reject((arm, _) => arm["devices"]![0]!["motion"] = new JsonObject { ["active"] = true },
            "Restart ignored device-local motion evidence");
        Reject((arm, _) => arm["storage_failed"] = true, "Restart ignored a recovery persistence failure");
        Reject((arm, _) => arm["devices"]![0]!["fresh"] = false, "Restart admitted stale physical arm feedback");
        Reject((arm, _) => arm["devices"]![0]!["reset_pending"] = true, "Restart interrupted reset");
        Reject((arm, _) => arm["devices"]![0]!["status"]!["teach_state"] = 1, "Restart interrupted physical teaching");
        Reject((arm, _) => arm["devices"]![0]!["status"]!["control"] = 2, "Restart interrupted physical replay");
        Reject((arm, _) => arm["devices"]![0]!["teaching"]!["handoff_in_progress"] = true, "Restart interrupted teach/CAN handoff");
        Reject((arm, _) => arm["devices"]![0]!["status"]!["pose_stationary_s"] = .399, "Restart accepted moving/undwelled arm");
        Reject((arm, _) => arm["devices"]![0]!["joints"]![5]!["motor_position_raw"] = null, "Restart accepted stale motor evidence");
        Reject((arm, _) => arm["devices"]![0]!["joints"]![2]!["fault"] = 1, "Restart ignored a drive fault");
        Reject((_, grippers) => grippers[0]!["owner"] = "another-client", "Restart released another client's powered Pika");
        Reject((_, grippers) => grippers[0]!["fresh"] = false, "Restart ignored stale gripper evidence");
        Reject((_, grippers) => grippers[0]!["connected"] = false, "Restart admitted disconnected uncertain power");
        Reject((_, grippers) => grippers[0]!["post_request_feedback_verified"] = false, "Restart admitted unacknowledged gripper mutation");
        Reject((_, grippers) => grippers[0]!["pending"] = true, "Restart interrupted pending jaw movement");
        Reject((_, grippers) => grippers[0]!["following"] = true, "Restart interrupted jaw following");
        Reject((_, grippers) => grippers[0]!["telemetry"]!["motor"]!["Speed"] = .021, "Restart admitted a moving gripper");
        Reject((_, grippers) => grippers[0]!["driver_fault"] = true, "Restart ignored gripper fault");
        Reject((arm, _) => arm["devices"]!.AsArray().Add(null), "Restart ignored malformed arm inventory");
        Reject((_, grippers) => grippers.Add(null), "Restart ignored malformed gripper inventory");

        var stopped = Arm(); var device = stopped["devices"]![0]!;
        device["recovery_required"] = true; device["stop_reason"] = "Operator stop.";
        device["status"]!["arm_status"] = 1; device["status"]!["control"] = 0;
        device["joints"]![1]!["actual_deg"] = -2.0; // Restart is not range recovery.
        string persisted = stopped.ToJsonString();
        Check(BackendMaintenance.Check(stopped, [], owner) is null && stopped.ToJsonString() == persisted,
            "Restart cleared or unnecessarily blocked an already stationary persisted STOP");
        Check(BackendMaintenance.Check(Arm(), new JsonArray(new JsonObject {
            ["connected"] = false, ["power_requested"] = false, ["power_uncertain"] = false
        }), owner) is null, "Unused read-only gripper assignment blocked maintenance");
        Console.WriteLine($"Passed {Checks} hardware-free backend maintenance checks.");
    }

    private static JsonObject Arm() => new() {
        ["motion"] = new JsonObject { ["active"] = false }, ["query_active"] = false, ["storage_failed"] = false,
        ["devices"] = new JsonArray(new JsonObject {
            ["id"] = "can-a", ["name"] = "Piper A", ["present"] = true, ["up"] = true,
            ["stable_identity"] = true, ["link_verified"] = true, ["fresh"] = true, ["firmware_supported"] = true,
            ["reset_pending"] = false, ["recovery_required"] = false,
            ["status"] = new JsonObject { ["error"] = 0, ["arm_status"] = 0, ["control"] = 1, ["teach_state"] = 0,
                ["joint_communication_fault_mask"] = 0, ["joint_angle_limit_fault_mask"] = 0, ["pose_stationary_s"] = .5 },
            ["teaching"] = new JsonObject { ["active"] = false, ["handoff_in_progress"] = false },
            ["joints"] = new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)new JsonObject {
                ["fresh"] = true, ["fault"] = 0, ["actual_deg"] = 0, ["motor_position_raw"] = 0, ["enabled"] = true
            }).ToArray())
        })
    };
    private static JsonObject Pika(string owner) => new() {
        ["device"] = "/dev/pika/by-host/1-2", ["owner"] = owner, ["connected"] = true, ["fresh"] = true,
        ["power_requested"] = true, ["power_uncertain"] = true, ["post_request_feedback_verified"] = true,
        ["pending"] = false, ["following"] = false, ["resuming_hold"] = false, ["driver_fault"] = false,
        ["enabled"] = true, ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Speed"] = 0 } }
    };
}
