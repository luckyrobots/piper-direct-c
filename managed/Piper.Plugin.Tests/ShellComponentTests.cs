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

    /// <summary>Plugin-6: one actor for the plugin's lifetime.</summary>
    public static async Task RunUiActor()
    {
        var shell = new ShellComponent();
        Check(shell.Events is null, "A new shell already had a UI actor");
        await using var first = new UiEventDispatch((_, _) => Task.CompletedTask, () => 0, _ => { });
        await using var second = new UiEventDispatch((_, _) => Task.CompletedTask, () => 0, _ => { });
        shell.Open(first);
        Check(ReferenceEquals(shell.Events, first), "The installed actor was not the one readers see");
        Check(Throws(() => shell.Open(second)) && ReferenceEquals(shell.Events, first), "A second actor replaced the first");
    }

    /// <summary>A loop that faults or returns early is counted, reported and
    /// started again; shutdown ends it without a report.</summary>
    public static async Task RunLoopSupervision()
    {
        var loops = new LoopSupervision { RestartDelay = TimeSpan.FromMilliseconds(5) };
        Check(loops.Faults == 0 && loops.Worker is null, "New supervision already had faults or a worker");
        using var lifetime = new CancellationTokenSource();
        var reports = new List<string>();
        int runs = 0;
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task supervised = loops.Supervise("Loop", async cancellation =>
        {
            int run = Interlocked.Increment(ref runs);
            if (run == 1) throw new InvalidOperationException("broken");
            if (run == 2) return;
            third.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
        }, (name, cause) => { lock (reports) reports.Add(name + " " + cause); }, lifetime.Token);
        loops.Started(supervised);
        Check(ReferenceEquals(loops.Worker, supervised) && Throws(() => loops.Started(Task.CompletedTask)) && ReferenceEquals(loops.Worker, supervised),
            "The running loops were replaced");
        await third.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (reports) Check(loops.Faults == 2 && reports.SequenceEqual(["Loop failed: broken", "Loop ended unexpectedly"]),
            "A fault and an early return were not each counted and reported once: " + string.Join("; ", reports));
        lifetime.Cancel();
        await supervised.WaitAsync(TimeSpan.FromSeconds(5));
        lock (reports) Check(loops.Faults == 2 && reports.Count == 2 && Volatile.Read(ref runs) == 3, "Shutdown was counted as a fault or restarted the loop");
    }

    /// <summary>At most one follow-up pass runs and one waits; the waiting pass
    /// covers every STOP that arrived before it started.</summary>
    public static async Task RunStopFollowUpQueue()
    {
        var queue = new StopFollowUpQueue();
        Check(queue.Idle.IsCompleted, "A new queue was not idle");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = new List<bool>();
        async Task Pass(bool group)
        {
            lock (passes) passes.Add(group);
            entered.TrySetResult();
            await release.Task;
        }
        queue.Queue(false, Pass);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task running = queue.Idle;
        Check(!running.IsCompleted, "The queue was idle while a pass ran");
        // Three more STOPs while the first pass runs: one waiting pass covers them.
        queue.Queue(false, Pass);
        Task waiting = queue.Idle;
        queue.Queue(true, Pass); queue.Queue(false, Pass);
        Check(!ReferenceEquals(waiting, running) && ReferenceEquals(queue.Idle, waiting), "Repeated STOPs queued more than one waiting pass");
        lock (passes) Check(passes.SequenceEqual([false]), "A waiting pass started before the running one ended");
        release.SetResult();
        await queue.Idle.WaitAsync(TimeSpan.FromSeconds(5));
        lock (passes) Check(passes.SequenceEqual([false, true]), "The waiting pass did not run once, covering the group STOP");
        queue.Queue(false, Pass);
        await queue.Idle.WaitAsync(TimeSpan.FromSeconds(5));
        lock (passes) Check(passes.SequenceEqual([false, true, false]), "A later STOP did not get its own pass, or inherited the group flag");
    }
}
