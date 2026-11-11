using System.Diagnostics;
using LuckyRobots.Piper.Plugin;

/// <summary>The Shell component on its own: what each part remembers and the
/// transitions that change it.</summary>
internal static class ShellComponentTests
{
    private static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }

    /// <summary>Plugin-4: ownership of the controller process and the restart
    /// phase change independently, each as one whole replacement.</summary>
    public static void RunOwnedBackend()
    {
        var backend = new OwnedBackend();
        var initial = backend.Standing;
        Check(initial.Owned is null && initial.Restart == BackendRestartPhase.Idle && initial.Status.Length > 0 && initial.Setup.Length > 0,
            "A new session did not start with an external controller and no restart");

        backend.Report("status"); backend.ReportSetup("setup");
        Check(backend.Standing is { Status: "status", Setup: "setup", Owned: null, Restart: BackendRestartPhase.Idle } &&
            initial.Status != "status" && initial.Setup != "setup", "A report changed more than its line, or changed a standing a reader held");

        // A process object that is never started stands in for a launched controller.
        var first = new Process();
        backend.Starting(first);
        var starting = backend.Standing.Owned;
        Check(starting is { Token: null } && ReferenceEquals(starting.Process, first), "A launched process was not owned without a token");
        backend.Ready(first, "token");
        var ready = backend.Standing.Owned;
        Check(ready is { Token: "token" } && ReferenceEquals(ready.Process, first) && starting!.Token is null,
            "The answering process was not owned with its token, or the earlier standing changed");
        Check(backend.Standing is { Status: "status", Setup: "setup" }, "Ownership changed the status lines");

        // Releasing an earlier ownership leaves the current one alone.
        backend.Release(starting!);
        Check(ReferenceEquals(backend.Standing.Owned, ready), "Releasing a replaced ownership ended the current one");
        backend.Release(ready!);
        Check(backend.Standing.Owned is null, "The released process stayed owned");
        backend.Release(ready!);
        Check(backend.Standing.Owned is null, "A second release changed ownership");

        var second = new Process();
        backend.Starting(second);
        backend.Disown();
        Check(backend.Standing.Owned is null, "Disowning kept the process");
        backend.Disown();
        Check(backend.Standing.Owned is null, "Disowning an external controller changed ownership");

        // Restart: Idle → Confirming → Requested → Relaunching → Idle.
        backend.CancelRestart();
        Check(backend.Standing.Restart == BackendRestartPhase.Idle, "Cancelling with no confirmation open changed the phase");
        backend.ConfirmRestart(); backend.ConfirmRestart();
        Check(backend.Standing.Restart == BackendRestartPhase.Confirming, "A repeated restart click did not keep the confirmation open");
        backend.CancelRestart();
        Check(backend.Standing.Restart == BackendRestartPhase.Idle, "Cancel did not close the confirmation");
        backend.ConfirmRestart(); backend.RestartRequested();
        Check(backend.Standing.Restart == BackendRestartPhase.Requested, "A sent restart was not Requested");
        Check(Throws(backend.ConfirmRestart) && backend.Standing.Restart == BackendRestartPhase.Requested, "A restart was confirmed again while one was running");
        backend.CancelRestart();
        Check(backend.Standing.Restart == BackendRestartPhase.Requested, "Cancel ended a running restart");
        backend.Relaunching();
        Check(backend.Standing.Restart == BackendRestartPhase.Relaunching && Throws(backend.ConfirmRestart), "A relaunch was not exclusive");
        backend.RestartEnded();
        Check(backend.Standing is { Restart: BackendRestartPhase.Idle, Status: "status", Setup: "setup", Owned: null }, "An ended restart did not return to Idle alone");
    }
}
