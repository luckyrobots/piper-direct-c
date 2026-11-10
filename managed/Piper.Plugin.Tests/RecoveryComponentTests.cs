using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>The Recovery component on its own: the recovery slot, Plugin-3 as a
/// table, the B1 question, and the cross-thread guarantees it gives.</summary>
internal static class RecoveryComponentTests
{
    private static readonly RobotCommandScope Scope = new("piper-1", "backend", "can-one", 1);

    private static RecoveryComponent NewRecovery() => new(new PikaTeachingSession(() => "settings"));

    private static RecoveryOperation Operation(RecoveryKind kind, long epoch, out CancellationToken token)
    {
        var operation = new RecoveryOperation(kind, Scope, epoch, new CancellationTokenSource());
        token = operation.Token;
        return operation;
    }

    /// <summary>One recovery at a time, and only its owner frees the slot.</summary>
    public static void RunOperationSlot()
    {
        var recovery = NewRecovery();
        Check(recovery.Operation is null && recovery.Guide is null && recovery.ResetPrompt is null && recovery.HomeProgress.Length == 0 &&
            !recovery.Teaching.Current.Owned && !recovery.ObservedTeaching.BlocksTargets, "A new recovery component was not idle");

        using var first = Operation(RecoveryKind.HoldHere, 4, out var firstToken);
        using var second = Operation(RecoveryKind.ReturnToZero, 4, out var secondToken);
        Check(first.Worker is null && first.ResetAt == 0 && first.Epoch == 4 && first.Scope == Scope, "A new operation carried a worker or a reset mark");
        Check(recovery.TryAdmit(first) && ReferenceEquals(recovery.Operation, first), "A free slot refused a recovery");
        Check(!recovery.TryAdmit(second) && ReferenceEquals(recovery.Operation, first), "A second recovery replaced the running one");
        Check(!recovery.TryRetire(second) && ReferenceEquals(recovery.Operation, first), "A recovery that does not hold the slot freed it");

        var worker = new TaskCompletionSource();
        first.Attach(worker.Task);
        first.BeginStep(123);
        Check(ReferenceEquals(first.Worker, worker.Task) && first.ResetAt == 123, "The worker or the reset mark was not recorded");
        first.BeginStep(0);
        Check(first.ResetAt == 0, "A step that is not a reset kept the reset mark");

        recovery.OpenGuide("scope");
        recovery.Cancel();
        Check(firstToken.IsCancellationRequested && !secondToken.IsCancellationRequested && recovery.Guide is null,
            "Cancel did not cancel the running recovery and close the guide");
        Check(ReferenceEquals(recovery.Operation, first), "Cancel freed the slot before the recovery's worker retired it");
        Check(recovery.TryRetire(first) && recovery.Operation is null, "The owner could not free the slot");
        Check(!recovery.TryRetire(first), "A recovery was retired twice");
        recovery.Cancel();
        Check(recovery.Operation is null && recovery.Guide is null, "Cancel with nothing running changed state");
        Check(recovery.TryAdmit(second) && recovery.TryRetire(second), "The slot was not reusable after retirement");

        recovery.ReportHomeProgress("stage");
        var guiding = TeachingState.Inspect(null);
        recovery.ObserveTeaching(guiding);
        Check(recovery.HomeProgress == "stage" && ReferenceEquals(recovery.ObservedTeaching, guiding), "Progress or the observed teaching mode was not recorded");
    }

    /// <summary>Plugin-3: every transition of the guide, including the ones that must not happen.</summary>
    public static void RunGuide()
    {
        var recovery = NewRecovery();
        var gate = new HandGuideRecoveryGate("stop-1");
        var seen = gate with { SawGuiding = true };

        // Closed stays closed for every transition except Open and Arm.
        recovery.ShowGuideMessage("late"); recovery.MarkCommandSent("late"); recovery.MarkClaimedReturnSent("late");
        recovery.DisarmGuide("late"); recovery.ObserveGate(gate, seen); recovery.RecordSupportedStop(new JsonObject(), "late");
        Check(recovery.Guide is null, "An update reopened a closed guide");
        Check(!recovery.TryClaimReturn(new RecoveryGuide.Armed("a", gate)) && recovery.Guide is null, "A return was claimed on a closed guide");

        recovery.OpenGuide("a");
        var open = recovery.Guide;
        Check(open is RecoveryGuide.Open { Scope: "a", Message: "", SupportedStop: null }, "Open did not produce an empty Open guide");
        recovery.ShowGuideMessage("step");
        recovery.OpenGuide("a");
        Check(recovery.Guide is RecoveryGuide.Open { Message: "step" }, "Opening the guide for the same connection discarded its message");
        recovery.OpenGuide("b");
        Check(recovery.Guide is RecoveryGuide.Open { Scope: "b", Message: "" }, "Another connection inherited the guide");

        var stopped = new JsonObject { ["reason"] = "obstruction" };
        recovery.RecordSupportedStop(stopped, "starting");
        Check(recovery.Guide is RecoveryGuide.Open { Message: "starting" } recorded && ReferenceEquals(recorded.SupportedStop, stopped),
            "The supported stop record was not kept on the guide");
        recovery.ObserveGate(gate, seen); recovery.DisarmGuide("unchanged"); recovery.MarkClaimedReturnSent("unclaimed");
        Check(recovery.Guide is RecoveryGuide.Open { Message: "starting" }, "A gate or claim transition changed a guide that is not armed");
        recovery.MarkCommandSent("sent");
        Check(recovery.Guide is RecoveryGuide.CommandSent { Scope: "b", Message: "sent" } sent && ReferenceEquals(sent.SupportedStop, stopped),
            "The card's own return did not reach CommandSent with the stop record");

        recovery.ArmGuide("a", gate, "guide");
        Check(recovery.Guide is RecoveryGuide.Armed { Scope: "a", Message: "guide", SupportedStop: null } first && first.Gate == gate,
            "Arming did not replace the guide with an Armed one");
        recovery.ObserveGate(new HandGuideRecoveryGate("other"), seen);
        Check(recovery.Guide!.HandGuideGate == gate, "An observation of another stop's gate was recorded");
        recovery.ObserveGate(gate, seen);
        Check(recovery.Guide is RecoveryGuide.Armed { Message: "guide" } && recovery.Guide.HandGuideGate == seen, "The seen hand-guiding cycle was not recorded");

        var stale = new RecoveryGuide.Armed("a", seen, "guide");
        Check(!recovery.TryClaimReturn(stale) && recovery.Guide is RecoveryGuide.Armed, "An equal but different guide value claimed the return");
        var armed = (RecoveryGuide.Armed)recovery.Guide!;
        Check(recovery.TryClaimReturn(armed) && recovery.Guide is RecoveryGuide.Attempted { Scope: "a", Message: "guide" } claimed && claimed.Gate == seen,
            "The armed guide's return could not be claimed");
        Check(!recovery.TryClaimReturn(armed), "The single return was claimed twice");
        recovery.ObserveGate(seen, gate);
        Check(recovery.Guide.HandGuideGate == seen, "A claimed return's gate was rewritten");
        recovery.MarkClaimedReturnSent("restoring");
        Check(recovery.Guide is RecoveryGuide.CommandSent { Message: "restoring" } && recovery.Guide.HandGuideGate is null, "The claimed return did not reach CommandSent");

        recovery.ArmGuide("a", seen, "guide");
        recovery.DisarmGuide("record changed");
        Check(recovery.Guide is RecoveryGuide.Open { Scope: "a", Message: "record changed" }, "A changed stop record did not retire the proof and keep the guide open");
        recovery.CloseGuide();
        Check(recovery.Guide is null, "The guide did not close");
    }

    /// <summary>B1: the question is asked once and answered once.</summary>
    public static void RunResetQuestion()
    {
        var recovery = NewRecovery();
        Check(recovery.TakeResetCompletion() is null, "A question was answered before it was asked");
        var prompt = new ResetCompletionPrompt(Scope, 7);
        recovery.AskResetCompletion(prompt);
        Check(ReferenceEquals(recovery.ResetPrompt, prompt), "The pending question was not visible");
        Check(ReferenceEquals(recovery.TakeResetCompletion(), prompt) && recovery.ResetPrompt is null, "Taking the question did not remove it");
        Check(recovery.TakeResetCompletion() is null, "One question was answered twice");
    }

    /// <summary>The automatic return and a click can both try to claim the armed
    /// guide's single return. Exactly one of them may send it.</summary>
    public static void RunOneReturnIsClaimed()
    {
        var recovery = NewRecovery();
        const int rounds = 2_000, claimants = 4;
        for (int round = 0; round < rounds; round++)
        {
            recovery.ArmGuide("a", new HandGuideRecoveryGate("stop", true), "");
            var armed = (RecoveryGuide.Armed)recovery.Guide!;
            int won = 0;
            using var start = new Barrier(claimants);
            var threads = Enumerable.Range(0, claimants).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                if (recovery.TryClaimReturn(armed)) Interlocked.Increment(ref won);
            })).ToArray();
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();
            if (won != 1 || recovery.Guide is not RecoveryGuide.Attempted)
            { Check(false, $"Round {round}: {won} callers claimed the single return"); return; }
        }
        Check(true, "");
    }

    /// <summary>A recovery worker reports progress while STOP closes the guide. Once
    /// the close has returned, no report that was already in flight may reopen it.</summary>
    public static void RunClosedGuideStaysClosed()
    {
        var recovery = NewRecovery();
        const int rounds = 20_000;
        bool done = false;
        long reports = 0;
        var worker = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                recovery.ShowGuideMessage("progress");
                recovery.MarkCommandSent("sent");
                Interlocked.Increment(ref reports);
            }
        });
        worker.Start();
        int reopened = 0;
        for (int round = 0; round < rounds; round++)
        {
            recovery.OpenGuide("a");
            recovery.Cancel();
            if (recovery.Guide is not null) reopened++;
            if (round % 100 != 0) continue;
            // Let every report that began before the close finish, then look again.
            long before = Interlocked.Read(ref reports);
            while (Interlocked.Read(ref reports) < before + 2) Thread.Yield();
            if (recovery.Guide is not null) reopened++;
        }
        Volatile.Write(ref done, true);
        worker.Join();
        Check(reopened == 0, $"A late progress report reopened a guide STOP had closed {reopened} times");
    }

    /// <summary>The relation to the command authority: a recovery is admitted inside
    /// the authority's epoch gate. STOP revokes the epoch and then cancels whatever
    /// recovery it finds. Whatever the interleaving, no admitted recovery is left
    /// running past the STOP, and none is admitted under a revoked epoch.</summary>
    public static void RunStopNeverLeavesAnAdmittedRecoveryRunning()
    {
        const int rounds = 5_000;
        for (int round = 0; round < rounds; round++)
        {
            var authority = new RobotCommandAuthority();
            var recovery = NewRecovery();
            long epoch = authority.Epoch;
            using var operation = Operation(RecoveryKind.HoldHere, epoch, out var token);
            bool admitted = false, inEpoch = false;
            using var start = new Barrier(2);
            var click = new Thread(() =>
            {
                start.SignalAndWait();
                inEpoch = authority.TryInEpoch(epoch, () => admitted = recovery.TryAdmit(operation));
            });
            var stop = new Thread(() =>
            {
                start.SignalAndWait();
                authority.Revoke();
                recovery.Cancel();
            });
            click.Start(); stop.Start(); click.Join(); stop.Join();
            bool running = recovery.Operation is not null && !token.IsCancellationRequested;
            if (inEpoch != admitted || admitted != ReferenceEquals(recovery.Operation, operation) || running)
            { Check(false, $"Round {round}: in epoch {inEpoch}, admitted {admitted}, still running {running}"); return; }
        }
        Check(true, "");

        // The same holds for the B1 question: none is raised under a revoked epoch.
        var revoked = new RobotCommandAuthority();
        var late = NewRecovery();
        long asked = revoked.Epoch;
        revoked.Revoke();
        Check(!revoked.TryInEpoch(asked, () => late.AskResetCompletion(new(Scope, asked))) && late.ResetPrompt is null,
            "A recovery retired by STOP still raised its reset question");
    }
}
