using LuckyRobots.Piper.Plugin;

/// <summary>The Workspace component on its own: Plugin-1 commit and publication,
/// Plugin-5 and Plugin-I as tables, and the two cross-thread guarantees it gives.</summary>
internal static class WorkspaceComponentTests
{
    private static PublishedWorkspaceView View(long epoch, WorkspaceViewContext context) =>
        new(epoch, context, new RobotProfile(context.Profile, context.Profile), null, new("", []));

    /// <summary>Plugin-1: commit, the uncertain window, confirmation and epochs.</summary>
    public static void RunPublication()
    {
        var workspace = new WorkspaceComponent();
        var controls = new WorkspaceViewContext("piper-1", WorkspaceNavigation.Initial);
        Check(workspace.Navigation == WorkspaceNavigation.Initial && workspace.Published is null && workspace.Epoch == 0 &&
            !workspace.PublicationUncertain && workspace.Confirmed(controls) is null,
            "A new workspace did not start on Controls with no published view");

        workspace.BeginPublication();
        long first = workspace.ReserveEpoch();
        Check(first == 1 && workspace.PublicationUncertain && workspace.Epoch == 0 && workspace.Confirmed(controls) is null,
            "A view being sent was already treated as visible");
        var shown = View(first, controls);
        workspace.Confirm(shown);
        Check(!workspace.PublicationUncertain && workspace.Epoch == 1 && ReferenceEquals(workspace.Published, shown) &&
            ReferenceEquals(workspace.Confirmed(controls), shown), "A confirmed view was not the published one");

        // A committed destination is ahead of the published view until its own view is confirmed.
        var settings = WorkspaceNavigation.At(WorkspacePage.Settings, SettingsCategory.Bench);
        workspace.Commit(settings);
        var bench = new WorkspaceViewContext("piper-1", settings);
        Check(workspace.Navigation == settings && bench.Calibration && workspace.Confirmed(bench) is null && workspace.Epoch == 1,
            "Committing a page made the old view count as showing it");
        Check(workspace.Confirmed(new("piper-2", WorkspaceNavigation.Initial)) is null, "A view counted as confirmed for another profile");

        // A failed send burns its epoch: the retry never reuses an identifier the host may have seen.
        workspace.BeginPublication();
        long failed = workspace.ReserveEpoch();
        long retry = workspace.ReserveEpoch();
        Check(failed == 2 && retry == 3 && workspace.PublicationUncertain && workspace.Epoch == 1 && ReferenceEquals(workspace.Published, shown),
            "A failed publication reused its epoch or replaced the last confirmed view");
        workspace.Confirm(View(retry, bench));
        Check(workspace.Epoch == 3 && workspace.Confirmed(bench) is { Epoch: 3 } && workspace.Confirmed(controls) is null,
            "The retried view was not confirmed under its own epoch");

        Check(!workspace.ConnectionDetailsOpen && !workspace.CameraStreamDetailsOpen && !workspace.CameraLensDetailsOpen, "A detail section started open");
        workspace.ToggleConnectionDetails(); workspace.ToggleCameraStreamDetails(); workspace.ToggleCameraLensDetails();
        Check(workspace.ConnectionDetailsOpen && workspace.CameraStreamDetailsOpen && workspace.CameraLensDetailsOpen, "A detail section did not open");
        workspace.ToggleCameraStreamDetails();
        Check(workspace.ConnectionDetailsOpen && !workspace.CameraStreamDetailsOpen && workspace.CameraLensDetailsOpen, "Closing one detail section changed another");
    }

    /// <summary>The telemetry loop and the SDK reader read the page while the UI
    /// actor commits another. They must see one committed page, never the fields
    /// of two. (Navigation was a five-field struct in a plain property, which a
    /// concurrent read may observe half-written.)</summary>
    public static void RunNavigationIsReadWhole()
    {
        var workspace = new WorkspaceComponent();
        var a = WorkspaceNavigation.Initial;
        var b = new WorkspaceNavigation(WorkspacePage.Settings, SettingsCategory.Cameras, "wrist", "gripper", WorkspacePage.Recording);
        const int commits = 200_000;
        int mixed = 0;
        bool done = false;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                var seen = workspace.Navigation;
                if (seen != a && seen != b) Interlocked.Increment(ref mixed);
            }
        });
        reader.Start();
        for (int i = 0; i < commits; i++) workspace.Commit((i & 1) == 0 ? b : a);
        Volatile.Write(ref done, true);
        reader.Join();
        Check(mixed == 0, $"A reader saw a page that was never committed {mixed} times");
        Check(ReferenceEquals(workspace.Navigation, a), "The last committed page was not the one read back");
    }

    /// <summary>Plugin-5: every transition of the primary action.</summary>
    public static void RunPrimaryAction()
    {
        var action = new PrimaryAction();
        Check(action.Current is { Phase: PrimaryActionPhase.Idle, Id: "", Status: "" } && !action.Busy && !action.ResumingControls,
            "A new primary action was not Idle and silent");
        Reject(action.EnterResume, "Resume controls was entered with no primary action running");

        action.Report("left over");
        action.Begin();
        string id = action.Current.Id;
        Check(action.Current is { Phase: PrimaryActionPhase.Running, Status: "left over" } && id.Length == 32 && action.Busy && !action.ResumingControls,
            "Begin did not start a running action with a receipt id, or dropped the visible status early");
        action.Report("checking");
        Check(action.Current is { Phase: PrimaryActionPhase.Running, Status: "checking" } && action.Current.Id == id, "A progress report changed the phase or the id");

        action.EnterResume();
        Check(action.ResumingControls && action.Busy && action.Current.Id == id && action.Current.Status == "checking", "EnterResume did not keep the action's id and status");
        Reject(action.EnterResume, "Resume controls was entered twice");
        action.LeaveResume();
        Check(action.Current.Phase == PrimaryActionPhase.Running, "LeaveResume did not return to Running");
        action.LeaveResume();
        Check(action.Current.Phase == PrimaryActionPhase.Running, "LeaveResume changed an action that was not resuming");

        action.Report("Could not start: reason");
        action.Finish();
        Check(action.Current is { Phase: PrimaryActionPhase.Idle, Status: "Could not start: reason" } && action.Current.Id == id && !action.Busy,
            "Finish did not return to Idle with the last status still visible");
        action.LeaveResume();
        Check(action.Current.Phase == PrimaryActionPhase.Idle, "LeaveResume restarted a finished action");
        action.Begin();
        Check(action.Current.Id != id && action.Current.Status == "Could not start: reason", "A second action reused the first one's receipt id");
    }

    /// <summary>Plugin-I: the session's own transitions.</summary>
    public static void RunReplaySession()
    {
        var replay = new ReplayReviewSession();
        var artifact = EpisodeReplayTests.ValidArtifact();
        var scope = new ReplayCheckScope("token", "device", 1);
        Check(replay.Review is null && replay.Path.Length == 0 && replay.Status.Length > 0 && replay.TimingStatus.Length > 0, "A new replay session was not Unloaded");
        Check(replay.Advance(review => review.Confirmed(scope)) is null && replay.Review is null, "A transition created a review with no episode loaded");

        replay.SetPath("episode.json");
        replay.Load(artifact, "piper-1");
        Check(replay.Path == "episode.json" && replay.Review is { Profile: "piper-1", Scope: null, TubeConfirmed: false }, "Load did not produce a Loaded review");
        var confirmed = replay.Advance(review => review.Confirmed(scope));
        Check(ReferenceEquals(confirmed, replay.Review) && confirmed is { TubeConfirmed: true } && confirmed.Scope == scope, "Confirm did not reach PlacementConfirmed");
        Check(replay.Advance(review => review.Observe("piper-1", scope)) is { TubeConfirmed: true }, "An unchanged connection withdrew the placement");
        Check(replay.Advance(review => review.Observe("piper-1", scope with { Generation = 2 })) is { TubeConfirmed: false } withdrawn && withdrawn.Scope == scope,
            "A new controller connection kept the confirmed placement");
        Check(replay.Advance(review => review.Loaded()) is { Scope: null, TubeConfirmed: false }, "A failed recheck did not return to Loaded");

        replay.Report("status"); replay.ReportTiming("timing");
        Check(replay.Status == "status" && replay.TimingStatus == "timing", "The replay status lines were not recorded");
        replay.SetPath("other.json");
        Check(replay.Review is null && replay.Path == "other.json" && replay.Status == "status", "A path change did not unload the review");
    }

    /// <summary>The UI actor and the telemetry loop both change the review. A
    /// transition computed from a value the other writer has since replaced must
    /// not be written: before this was a compare-and-swap, the telemetry loop
    /// could bring back a review the operator had just unloaded, and a finishing
    /// recheck could restore a placement telemetry had withdrawn.</summary>
    public static void RunReplayWritersDoNotOverwriteEachOther()
    {
        var artifact = EpisodeReplayTests.ValidArtifact();
        var scope = new ReplayCheckScope("token", "device", 1);

        // Telemetry observes a confirmed placement; the operator changes the path meanwhile.
        var replay = new ReplayReviewSession();
        replay.Load(artifact, "piper-1");
        replay.Advance(review => review.Confirmed(scope));
        int calls = 0;
        var result = replay.Advance(review =>
        {
            if (calls++ == 0) replay.SetPath("other.json");
            return review.Observe("piper-2", scope);
        });
        Check(result is null && replay.Review is null && calls == 1, "A telemetry observation brought back a review the operator had unloaded");

        // A recheck finishes; telemetry withdrew the placement while it was running.
        replay.Load(artifact, "piper-1");
        replay.Advance(review => review.Confirmed(scope));
        calls = 0;
        result = replay.Advance(review =>
        {
            if (calls++ == 0) replay.Advance(current => current.Observe("piper-1", null));
            return review.Rechecked(scope);
        });
        Check(calls == 2 && result is { TubeConfirmed: false } && result.Scope == scope && ReferenceEquals(result, replay.Review),
            "A finishing recheck restored a placement that telemetry had withdrawn");

        // The same two writers on real threads: every placement telemetry withdraws stays withdrawn.
        var contended = new ReplayReviewSession();
        contended.Load(artifact, "piper-1");
        const int rounds = 20_000;
        int restored = 0;
        using var confirmedSignal = new SemaphoreSlim(0);
        using var withdrawnSignal = new SemaphoreSlim(0);
        var telemetry = new Thread(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                confirmedSignal.Wait();
                contended.Advance(review => review.Observe("piper-1", null));
                withdrawnSignal.Release();
            }
        });
        telemetry.Start();
        for (int i = 0; i < rounds; i++)
        {
            contended.Advance(review => review.Confirmed(scope));
            confirmedSignal.Release();
            // Races the withdrawal: whichever order they land in, the placement must end withdrawn.
            contended.Advance(review => review.Rechecked(scope));
            withdrawnSignal.Wait();
            if (contended.Review is not { TubeConfirmed: false }) restored++;
        }
        telemetry.Join();
        Check(restored == 0, $"A recheck racing telemetry restored a withdrawn placement {restored} times");
    }
}
