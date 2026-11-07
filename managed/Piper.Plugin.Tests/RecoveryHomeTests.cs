using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class RecoveryHomeTests
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task<Exception?> Failure(Func<Task> operation)
        { try { await operation(); return null; } catch (Exception error) { return error; } }
        foreach (bool initiallyStopped in new[] { false, true })
        {
            var fixture = new Fixture();
            if (!initiallyStopped)
            {
                fixture.Device["status"]!["arm_status"] = 0; fixture.Device["recovery_required"] = false;
                foreach (var joint in fixture.Device["joints"]!.AsArray()) joint!["enabled"] = false;
            }
            check(RecoveryHome.Blocker(fixture.Snapshot()) is null,
                "Resting bounded range overrun was blocked before reset/enable, stopped=" + initiallyStopped);
            await fixture.Run();
            check(fixture.Actions.SequenceEqual(initiallyStopped ? ["reset", "enable", "move-to-zero"] : ["enable", "move-to-zero"]),
                "Recovery did not use exactly reset → verified-off → power-only enable → zero");
            check(fixture.OffVerifiedBeforeEnable && fixture.OnVerifiedBeforeZero,
                "Recovery skipped verified power stages before enabling or homing");
            check((!initiallyStopped || fixture.Progress[0].Contains("Resetting")) && fixture.Progress[^1].Contains("Recovery complete"),
                "Recovery progress did not identify the requested zero goal and completion");
            check(fixture.Device["joints"]!.AsArray().All(j => j!["actual_deg"]!.GetValue<double>() == 0),
                "Recovery completed before all six zero measurements were observed");
        }
        foreach (bool softwareLatch in new[] { false, true })
        {
            var fixture = new Fixture(); fixture.Device["status"]!["arm_status"] = 0;
            fixture.Device["recovery_required"] = softwareLatch;
            fixture.Device["joints"]![1]!["actual_deg"] = .5; fixture.Device["joints"]![2]!["actual_deg"] = -.5;
            await fixture.Run();
            check(fixture.Actions.SequenceEqual(softwareLatch ? ["verify-current", "move-to-zero"] : ["move-to-zero"]),
                "Healthy enabled homing removed holding torque or sent an unnecessary power/reset command");
        }
        {
            var fixture = new Fixture(); fixture.Device["status"]!["arm_status"] = 0; fixture.Device["joints"]![2]!["enabled"] = false;
            await fixture.Run();
            check(fixture.Actions.SequenceEqual(["reset", "enable", "move-to-zero"]),
                "Known partial motor power bypassed reset/off verification before homing");
        }
        {
            var fixture = new Fixture(); fixture.Device["reset_pending"] = true;
            fixture.Device["status"]!["arm_status"] = 0;
            foreach (var joint in fixture.Device["joints"]!.AsArray()) joint!["enabled"] = false;
            await fixture.Run();
            check(fixture.Actions.SequenceEqual(["verify-reset", "enable", "move-to-zero"]),
                "An already completed reset was resent instead of verified once");
        }
        {
            var fixture = new Fixture(); fixture.Device["reset_pending"] = true;
            fixture.Device["status"]!["teach_state"] = 1;
            foreach (var joint in fixture.Device["joints"]!.AsArray()) joint!["enabled"] = false;
            await fixture.Run();
            check(fixture.Actions.SequenceEqual(["reset", "enable", "move-to-zero"]),
                "Explicit recovery of retired STOP/residual-teach failed or retried a stage");
        }
        {
            var fixture = new Fixture();
            check(await Failure(() => fixture.Run(acknowledged: false)) is InvalidOperationException && fixture.Actions.Count == 0,
                "Recovery inferred physical support instead of requiring the one labelled resting/clear action");
        }
        foreach (var (name, mutate) in new (string, Action<Fixture>)[]
        {
            ("stale arm", f => f.Device["fresh"] = false),
            ("stale joint", f => f.Device["joints"]![2]!["fresh"] = false),
            ("driver fault", f => f.Device["joints"]![2]!["fault"] = 1),
            ("unknown motor power", f => f.Device["joints"]![2]!.AsObject().Remove("enabled")),
            ("controller error", f => f.Device["status"]!["error"] = 1),
            ("moving", f => f.Motion["active"] = true),
            ("not stationary", f => f.Device["status"]!["stationary_s"] = .399),
            ("hand guiding", f => { f.Device["status"]!["arm_status"] = 11; f.Device["status"]!["teach_state"] = 1; }),
            ("playback", f => f.Device["status"]!["teach_state"] = 3),
            ("unsupported firmware", f => f.Device["firmware_supported"] = false),
            ("unread limits", f => f.Device["joints"]![2]!["limits_fresh"] = false),
            ("zero outside limits", f => f.Device["joints"]![2]!["max_deg"] = -1),
            ("overrun greater than 3 degrees", f => f.Device["joints"]![1]!["actual_deg"] = -3.001),
            ("overrun plus inward margin greater than 3 degrees", f => f.Device["joints"]![1]!["actual_deg"] = -2.8),
            ("nonfinite pose", f => f.Device["joints"]![0]!["actual_deg"] = double.NaN)
        })
        {
            var fixture = new Fixture(); mutate(fixture);
            check(await Failure(() => fixture.Run()) is InvalidOperationException && fixture.Actions.Count == 0,
                "Recovery to zero admitted " + name);
        }
        foreach (string stage in new[] { "reset", "enable", "move-to-zero" })
        {
            var fixture = new Fixture();
            fixture.AfterAction = step =>
            {
                if (step != stage) return;
                fixture.Motion["active"] = true;
                throw new IOException("Lost " + step + " acknowledgement");
            };
            check(await Failure(() => fixture.Run()) is IOException,
                "Recovery swallowed a lost acknowledgement at " + stage);
            check(fixture.Actions.Count(a => a == stage) == 1 && fixture.StoppedRequests.SequenceEqual([fixture.Motion["request_id"]!.GetValue<long>()]),
                "A failed stage was retried or its newly admitted request was not stopped: " + stage);
            check(fixture.Actions.Count == Array.IndexOf(new[] { "reset", "enable", "move-to-zero" }, stage) + 1,
                "Recovery continued to a later stage after " + stage + " failed");
        }
        foreach (string stage in new[] { "reset", "enable", "move-to-zero" })
        {
            var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
            fixture.AfterAction = step => { if (step == stage) { fixture.Motion["active"] = true; cancellation.Cancel(); } };
            check(await Failure(() => fixture.Run(cancellation: cancellation.Token)) is OperationCanceledException,
                "Cancellation was ignored after " + stage);
            check(fixture.Actions.Count(a => a == stage) == 1 && fixture.StoppedRequests.Count == 1 &&
                fixture.Actions.Count == Array.IndexOf(new[] { "reset", "enable", "move-to-zero" }, stage) + 1,
                "Cancellation resumed a later stage or omitted the scoped stop after " + stage);
        }
        foreach (bool sameGeneration in new[] { false, true })
        {
            var fixture = new Fixture();
            fixture.AfterAction = step =>
            {
                if (step != "reset") return;
                fixture.Motion["active"] = true;
                if (sameGeneration) { fixture.Motion["request_id"] = 500L; fixture.Motion["owner"] = "another-client"; }
                else fixture.Device["generation"] = 8L;
            };
            check(await Failure(() => fixture.Run()) is InvalidOperationException && fixture.Actions.SequenceEqual(["reset"]) && fixture.StoppedRequests.Count == 0,
                "A replacement controller action or CAN generation inherited recovery or received its cleanup STOP");
        }
        foreach (string stage in new[] { "reset", "enable", "move-to-zero" })
        {
            var fixture = new Fixture();
            fixture.AfterAction = step => { if (step == stage) { fixture.FreezeAfterAcknowledgement = true; fixture.Motion["active"] = true; } };
            check(await Failure(() => fixture.Run(timeout: 35)) is TimeoutException && fixture.Actions.Count(a => a == stage) == 1 && fixture.StoppedRequests.Count == 1,
                "Cached pre-ack telemetry advanced recovery or failed to time out and stop at " + stage);
        }
        {
            var fixture = new Fixture();
            fixture.AfterAction = step => { if (step == "reset") { fixture.Motion["active"] = false; fixture.Phase = "stopped"; } };
            check(await Failure(() => fixture.Run()) is InvalidOperationException && fixture.Actions.SequenceEqual(["reset"]),
                "A native reset timeout/fault triggered an automatic reset retry or enable");
        }
        {
            var fixture = new Fixture(); int transientReads = 0;
            fixture.AfterAction = step =>
            {
                if (step != "reset") return;
                fixture.Motion["active"] = true; fixture.Phase = "resetting";
                fixture.Device["status"]!["arm_status"] = 5; fixture.Device["status"]!["error"] = 63;
                fixture.Device["joints"]![2]!["fault"] = 1;
                fixture.BeforeSnapshot = () =>
                {
                    if (++transientReads < 3) return;
                    fixture.Device["status"]!["arm_status"] = 0; fixture.Device["status"]!["error"] = 0;
                    fixture.Device["joints"]![2]!["fault"] = 0;
                    fixture.Motion["active"] = false; fixture.Phase = "complete";
                };
            };
            await fixture.Run(timeout: 500);
            check(transientReads >= 3 && fixture.Actions.SequenceEqual(["reset", "enable", "move-to-zero"]) && fixture.StoppedRequests.Count == 0,
                "Expected active-owned reset transitions caused a premature client STOP instead of waiting for fresh healthy/all-off verification");
        }
        foreach (string stage in new[] { "enable", "move-to-zero" })
        {
            var fixture = new Fixture(); fixture.AfterAction = step =>
            {
                if (step != stage) return;
                fixture.Motion["active"] = true; fixture.Device["status"]!["error"] = 63;
            };
            check(await Failure(() => fixture.Run()) is InvalidOperationException && fixture.StoppedRequests.Count == 1,
                "Reset's transient-status allowance leaked into " + stage);
        }
        {
            var fixture = new Fixture();
            fixture.AfterAction = step => { if (step == "move-to-zero") fixture.Device["joints"]![5]!["actual_deg"] = 5d; };
            check(await Failure(() => fixture.Run(timeout: 35)) is TimeoutException && !fixture.Progress.Any(p => p.StartsWith("Recovery complete")),
                "Recovery reported success without measured zero-position feedback");
        }
        {
            var fixture = new Fixture(); fixture.ProgressFailure = true;
            check(await Failure(() => fixture.Run()) is IOException && fixture.Actions.Count == 0,
                "Failure to persist the initial recovery stage still admitted a reset");
        }
        check(WorkspaceCommandPolicy.RequiresControls("recovery.home") && CalibrationCommandGuard.Blocks("recovery.home", true),
            "Recovery home bypassed Controls/calibration command policy");
        check(WorkspaceCommandPolicy.RequiresControls("recovery.home.incident") && CalibrationCommandGuard.Blocks("recovery.home.incident", true),
            "The primary incident recovery alias bypassed the same Controls/calibration policy");
        await RecoveryHomeUiTests.Run(check);
    }

    private sealed class Fixture
    {
        public JsonObject Device { get; } = new()
        {
            ["id"] = "resting-arm", ["generation"] = 7L, ["fresh"] = true, ["firmware_supported"] = true,
            ["recovery_required"] = true, ["reset_pending"] = false,
            ["status"] = new JsonObject { ["arm_status"] = 1, ["control"] = 0, ["teach_state"] = 0,
                ["motion_mode"] = 1, ["error"] = 0, ["stationary_s"] = 1d },
            ["joints"] = new JsonArray(new[] { 0d, -2.064, 2.105, 0, 21.346, 0 }.Select((angle, i) => (JsonNode)new JsonObject
            {
                ["actual_deg"] = angle, ["min_deg"] = i == 1 ? 0d : -180d, ["max_deg"] = i == 2 ? 0d : 180d,
                ["fresh"] = true, ["fault"] = 0, ["enabled"] = true, ["limits_fresh"] = true
            }).ToArray())
        };
        public JsonObject Motion { get; } = new() { ["active"] = false, ["request_id"] = 10L, ["owner"] = "recovery-fixture" };
        public string Phase = "stopped";
        public List<string> Actions { get; } = [];
        public List<string> Progress { get; } = [];
        public List<long> StoppedRequests { get; } = [];
        public Action<string>? AfterAction;
        public Action? BeforeSnapshot;
        public bool FreezeAfterAcknowledgement, ProgressFailure, OffVerifiedBeforeEnable, OnVerifiedBeforeZero;
        private long sequence = 1, ticks = 100;
        public RecoveryHomeSnapshot Snapshot()
        {
            BeforeSnapshot?.Invoke();
            if (!FreezeAfterAcknowledgement) { ++sequence; ++ticks; }
            return new(Device, Motion, Phase, "Fixture controller stopped", sequence, ticks);
        }
        public Task Run(bool acknowledged = true, CancellationToken cancellation = default, int timeout = 100) =>
            RecoveryHome.Run(acknowledged, "resting-arm", 7, "recovery-fixture", Snapshot, (step, cancel) =>
            {
                cancel.ThrowIfCancellationRequested(); Actions.Add(step);
                bool stateful = step is not ("verify-reset" or "verify-current");
                if (stateful) Motion["request_id"] = Motion["request_id"]!.GetValue<long>() + 1;
                Motion["active"] = false; Phase = stateful ? "complete" : "monitor";
                if (step == "enable") OffVerifiedBeforeEnable = Device["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>() == false);
                if (step == "move-to-zero") OnVerifiedBeforeZero = Device["joints"]!.AsArray().All(j => j!["enabled"]!.GetValue<bool>());
                Device["status"]!["arm_status"] = 0; Device["status"]!["teach_state"] = 0;
                Device["recovery_required"] = false; Device["reset_pending"] = false;
                foreach (var joint in Device["joints"]!.AsArray())
                {
                    if (step == "reset") joint!["enabled"] = false;
                    if (step is "enable" or "move-to-zero") joint!["enabled"] = true;
                    if (step == "move-to-zero") joint!["actual_deg"] = 0d;
                }
                var result = new RecoveryHomeAcknowledgement(Motion["request_id"]!.GetValue<long>(), sequence, ++ticks, stateful);
                AfterAction?.Invoke(step);
                return Task.FromResult(result);
            }, (request, _) => { StoppedRequests.Add(request); return Task.CompletedTask; }, stage =>
            {
                if (ProgressFailure) throw new IOException("Fixture report storage unavailable");
                Progress.Add(stage); return Task.CompletedTask;
            }, cancellation, timeout, timeout, timeout);
    }
}
