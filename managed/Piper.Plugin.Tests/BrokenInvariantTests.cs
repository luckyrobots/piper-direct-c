using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Grpc;
using LuckyRobots.Piper.Plugin;

/// <summary>Invariants that used to be Debug.Assert (absent from Release builds).
/// Each broken invariant must now refuse or disarm, and say so.</summary>
internal static class BrokenInvariantTests
{
    private const string Backend = "invariant-session";

    private static PipersPlugin Plugin(EmergencyStopTests.FakeCalls rpc)
    {
        var plugin = new PipersPlugin();
        plugin.client = new PiperControl.PiperControlClient(rpc);
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-one")], ActiveRobotId: "piper-1"));
        plugin.token = Backend;
        var snapshot = Fixtures.Snapshot(Backend, Fixtures.Arm("can-one", generation: 1));
        plugin.state = snapshot;
        plugin.telemetry.Publish(new(snapshot, Backend, 1, Stopwatch.GetTimestamp(), 0));
        plugin.engine = Fixtures.Context(new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
            (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
            (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false }))));
        return plugin;
    }

    private static void Close(PipersPlugin plugin) { plugin.shutdown.Cancel(); plugin.channel.Dispose(); plugin.commands.Dispose(); }

    private static string Refusal(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) { return error.Message; }
        return "";
    }

    private static async Task<string> Refusal(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) { return error.Message; }
        return "";
    }

    public static void RunPureStates()
    {
        var blank = SheetSetup.Restore("", "", "", false, false, false);
        Check(Refusal(() => blank.ConfirmLeft("", "desk-proof")).Contains("Nothing was confirmed", StringComparison.Ordinal),
            "A LEFT edge confirmation without its edge root was accepted");
        Check(Refusal(() => LensSession.Initial.Solving()).Contains("no capture folder", StringComparison.Ordinal),
            "A lens solve was accepted for a session that has no capture folder");
        var trapped = WorkspaceNavigation.Initial with { Page = WorkspacePage.Camera, CameraReturn = WorkspacePage.Camera };
        Check(trapped.Navigate("camera-back").Page == WorkspacePage.Controls,
            "A camera page that returns to itself trapped the operator instead of returning to Controls");
        var snapshot = Fixtures.Snapshot(Backend, Fixtures.Arm("can-one", generation: 1));
        var receipt = new TelemetryReceipt(snapshot, Backend, 1, Stopwatch.GetTimestamp(), 0);
        var profile = new RobotProfile("piper-1", "Fixture", "can-one");
        Check(Refusal(() => _ = new FollowLaneObservation(receipt, profile, 0, null, Stopwatch.Frequency)).Contains("Following was not started", StringComparison.Ordinal),
            "A follower observation was built for an arm with no live connection generation");
        Check(Refusal(() => _ = new FollowLaneObservation(receipt, profile with { CanDeviceId = "" }, 1, null, Stopwatch.Frequency)).Contains("Following was not started", StringComparison.Ordinal),
            "A follower observation was built for a robot with no CAN adapter");
    }

    public static async Task RunPluginStates()
    {
        var rpc = new EmergencyStopTests.FakeCalls();
        var plugin = Plugin(rpc);
        try
        {
            // Retiring a recovery that does not own the slot must not free it
            // under the real owner; everything is cancelled and reported.
            var scope = new RobotCommandScope("piper-1", Backend, "can-one", 1);
            using var ownerCancellation = new CancellationTokenSource();
            using var strayCancellation = new CancellationTokenSource();
            var slotOwner = new PipersPlugin.RecoveryOperation(PipersPlugin.RecoveryKind.HoldHere, scope, plugin.CommandAuthority.Epoch, ownerCancellation);
            var stray = new PipersPlugin.RecoveryOperation(PipersPlugin.RecoveryKind.ReturnToZero, scope, plugin.CommandAuthority.Epoch, strayCancellation);
            plugin.recoveryOperation = slotOwner;
            plugin.approved = true;
            long epoch = plugin.CommandAuthority.Epoch;
            var ownerToken = slotOwner.Token; var strayToken = stray.Token;
            plugin.RetireRecovery(stray);
            Check(ReferenceEquals(plugin.recoveryOperation, slotOwner), "Retiring a stray recovery freed the slot of the recovery that owns it");
            Check(ownerToken.IsCancellationRequested && strayToken.IsCancellationRequested && !plugin.approved && plugin.CommandAuthority.Epoch > epoch,
                "A recovery bookkeeping mismatch left a recovery running or motion permission standing");
            Check(plugin.uiActionError.Contains("Recovery state was inconsistent", StringComparison.Ordinal), "A recovery bookkeeping mismatch was not reported to the operator");
            plugin.recoveryOperation = null; plugin.uiActionError = "";
            // The ordinary path still clears the slot quietly.
            using var ordinaryCancellation = new CancellationTokenSource();
            var ordinary = new PipersPlugin.RecoveryOperation(PipersPlugin.RecoveryKind.HoldHere, scope, plugin.CommandAuthority.Epoch, ordinaryCancellation);
            plugin.recoveryOperation = ordinary;
            plugin.RetireRecovery(ordinary);
            Check(plugin.recoveryOperation is null && plugin.uiActionError.Length == 0, "An ordinary recovery retirement reported a fault or kept the slot");

            string resume = await Refusal(() => plugin.ResumeControls(CancellationToken.None));
            Check(resume.Contains("primary robot action", StringComparison.Ordinal) && rpc.Routes.IsEmpty && plugin.primaryAction.Phase == PrimaryActionPhase.Idle,
                "Resume controls ran outside the primary robot action: " + resume);

            plugin.SetHeightPhase(PipersPlugin.HeightCheckPhase.Live);
            Check(plugin.heightPhase == PipersPlugin.HeightCheckPhase.Live && plugin.heightActionError.Length == 0, "A consistent jaw-height phase was refused");
            plugin.SetHeightPhase(PipersPlugin.HeightCheckPhase.Saved);
            Check(plugin.heightPhase == PipersPlugin.HeightCheckPhase.Inactive && plugin.heightActionError.Contains("Nothing was saved", StringComparison.Ordinal),
                "A jaw-height phase without its pinned pose stayed open or was not reported");

            Check(plugin.CalibrationSessionFault is null, "An empty calibration session was reported as inconsistent");
            plugin.calibrationIdentity = new("piper-1", "can-one", 1, "fingerprint", "scene", "robot");
            plugin.calibrationApplied = new CalibrationPlacement(new CalibrationPoint(0, 0, 0), [1, 0, 0, 0]);
            string save = await Refusal(() => plugin.SaveCalibration(CancellationToken.None));
            Check(plugin.CalibrationSessionFault is not null && save.Contains("Nothing was saved", StringComparison.Ordinal),
                "A calibration session whose applied placement has no fit was saved as a report: " + save);
        }
        finally { Close(plugin); }
    }
}
