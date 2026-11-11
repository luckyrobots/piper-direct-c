using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

/// <summary>The Calibration component on its own: each part's transitions, the
/// transitions that must not happen, and the cross-thread guarantees it gives.</summary>
internal static class CalibrationComponentTests
{
    private static readonly CalibrationIdentity Identity = new("piper-1", "can-one", 1, "backend", "scene", "robot");
    private static readonly CalibrationPlacement Root = new(new(0, 0, 0), [1, 0, 0, 0]);
    private static readonly CalibrationPlacement Proposal = new(new(0.01, 0, 0.02), [1, 0, 0, 0]);
    private static readonly CalibrationFit Fit = new(true, "", Proposal, 0, 0, 0, 0, 0, []);

    private static CalibrationSample Sample(string name) => new(name, new(0, 0, 0), new(0, 0, 0), [], 1, false, new(), name);

    private static HeightObservation Observation(string pose) =>
        new(Identity, new(), new(new(0, 0.1, 0), Root, [], pose, "asset", "revision"), DateTimeOffset.UnixEpoch, "report");

    private static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }

    /// <summary>Cal-A: the phase follows the transitions, and the session is one value.</summary>
    public static void RunDeskSession()
    {
        var desk = new CalibrationComponent().Desk;
        Check(ReferenceEquals(desk.Session, DeskSession.Unloaded) && desk.Session.Phase == CalibrationPhase.None && desk.Session.Fault is null,
            "A new desk calibration was not unloaded");

        int reads = 0;
        desk.LoadProfile("piper-1", () => { reads++; return DeskSession.Unloaded with { Path = "report.json", Profile = "ignored" }; });
        desk.LoadProfile("piper-1", () => { reads++; return DeskSession.Unloaded; });
        Check(reads == 1 && desk.Session is { Profile: "piper-1", Path: "report.json" }, "A profile was not loaded exactly once under its own name");

        var first = Sample("p1"); var second = Sample("p2");
        desk.Begin(Identity, Root);
        Check(desk.Session.Phase == CalibrationPhase.Capturing && ReferenceEquals(desk.Session.Original, Root), "Start did not enter Capturing");
        desk.AddSample(first); desk.AddSample(second);
        var captured = desk.Session;
        desk.Fitted(Fit);
        Check(desk.Session.Phase == CalibrationPhase.Fitted && captured.Fit is null && captured.Samples.Count == 2, "A fit changed a session value a reader already held");

        var corrected = first with { Name = "p1 corrected" };
        desk.Correct(first, corrected);
        Check(desk.Session.Phase == CalibrationPhase.Capturing && ReferenceEquals(desk.Session.Samples[0], corrected) && ReferenceEquals(desk.Session.Samples[1], second),
            "A correction did not replace its own sample and clear the fit");
        Check(ReferenceEquals(captured.Samples[0], first), "A correction changed a sample list a reader already held");

        desk.Fitted(Fit); desk.MarkWritePending();
        Check(desk.Session.Phase == CalibrationPhase.WritePending, "A sent write was not pending");
        desk.MarkWriteNotSent();
        Check(desk.Session.Phase == CalibrationPhase.Fitted, "A write that was never sent stayed pending");
        desk.MarkWritePending(); desk.PlacementVerified(Proposal);
        Check(desk.Session.Phase == CalibrationPhase.Applied && ReferenceEquals(desk.Session.Applied, Proposal), "A verified apply was not Applied");
        desk.MarkWritePending(); desk.PlacementVerified(null);
        Check(desk.Session.Phase == CalibrationPhase.Fitted, "A verified rollback did not return to Fitted");

        desk.AskArchive();
        Check(desk.Session.ArchiveConfirmation, "The archive question was not asked");
        desk.CancelArchive();
        Check(!desk.Session.ArchiveConfirmation, "The archive question was not withdrawn");

        var before = desk.Session;
        desk.Reevaluate(Identity with { ModelRevision = "new" }, [second], new("old", "new", "audit.json", DateTimeOffset.UnixEpoch));
        Check(desk.Session is { Fit: null, Reevaluation: not null, Samples.Count: 1, Identity.ModelRevision: "new" }, "A re-evaluation did not replace the samples and clear the fit");
        desk.Reinstate(before);
        Check(ReferenceEquals(desk.Session, before), "A failed save did not get its earlier session back");

        desk.UseDesk(desk.Session.Desk with { SurfaceY = 0.5 }); desk.SaveTo("next.json"); desk.AskArchive();
        desk.Clear();
        Check(desk.Session is { Phase: CalibrationPhase.None, Fault: null, Samples.Count: 0, ArchiveConfirmation: false, Path: "next.json", Profile: "piper-1" } &&
            desk.Session.Desk.SurfaceY == 0.5, "Clear did not leave only the profile, desk and report path");

        Check((DeskSession.Unloaded with { Original = Root }).Fault is not null, "A placement without an identity was not a fault");
        Check((DeskSession.Unloaded with { Identity = Identity, Applied = Proposal }).Fault is not null, "An applied placement without its fit was not a fault");
    }

    /// <summary>Cal-B: a published draft never changes, and a change that changes nothing publishes nothing.</summary>
    public static void RunSheetWizard()
    {
        var wizard = new CalibrationComponent().Wizard;
        Check(ReferenceEquals(wizard.Session, WizardSession.Closed) && wizard.Session.Draft is null && wizard.Session.EditingRow == -1, "A new sheet was not closed");
        Check(Throws(() => _ = wizard.Draft) && Throws(() => wizard.Answer("a", "1")) && Throws(() => wizard.ChangeSetup(setup => setup)) &&
            ReferenceEquals(wizard.Session, WizardSession.Closed), "A closed sheet accepted a draft change");

        wizard.Report("old"); wizard.Discovered(["site"]); wizard.EditRow(2); wizard.DescribeModelPoint("point", "details");
        var draft = CalibrationWizardFlow.New("piper-1");
        wizard.Open("piper-1", draft);
        Check(wizard.Session is { Profile: "piper-1", Message: "", EditingRow: -1, Sites.Length: 0, ModelPoint: WizardSession.ModelPointPrompt, ModelPointDetails: "details" } &&
            ReferenceEquals(wizard.Draft, draft), "Open did not start from the draft with no message, sites or row");

        wizard.Answer("p1left", "800");
        var answered = wizard.Draft;
        Check(!ReferenceEquals(answered, draft) && !ReferenceEquals(answered.Answers, draft.Answers) && !draft.Answers.ContainsKey("p1left") &&
            answered.Answers["p1left"] == "800", "An answer changed a draft a reader already held");
        var session = wizard.Session;
        wizard.Answer("p1left", "800"); wizard.Edit(same => same); wizard.ChangeSetup(setup => setup with { });
        Check(ReferenceEquals(wizard.Session.Draft, answered), "A change that changed nothing published a new draft");
        wizard.Answer(("p1left", "801"), ("p1back", "499"));
        Check(wizard.Draft.Answers is { Count: var count } && count == answered.Answers.Count + 1 && wizard.Draft.Answers["p1left"] == "801" &&
            answered.Answers["p1left"] == "800" && ReferenceEquals(session.Draft, answered), "Answers given together were not recorded together in a new map");

        wizard.ChangeSetup(setup => setup with { LeftProof = "proof" });
        Check(wizard.Draft.Setup.LeftProof == "proof" && answered.Setup.LeftProof == "", "A setup confirmation was not recorded in a new draft");
        wizard.EditRow(1); wizard.ReportSave("saved"); wizard.Report("message");
        Check(wizard.Session is { EditingRow: 1, SaveStatus: "saved", Message: "message" }, "The row being corrected or a text was not recorded");
        wizard.StopEditing();
        Check(wizard.Session.EditingRow == -1 && wizard.Draft.Setup.LeftProof == "proof", "Closing the row lost it or the draft");
    }

    /// <summary>Cal-B across threads: the render path, marker maintenance and the UI actor may all answer at once.</summary>
    public static void RunSheetWizardConcurrentAnswers()
    {
        var wizard = new CalibrationComponent().Wizard;
        wizard.Open("piper-1", CalibrationWizardFlow.New("piper-1"));
        int before = wizard.Draft.Answers.Count;
        const int writers = 6, each = 200;
        bool readerFaulted = false;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            try { while (!stop.IsCancellationRequested) foreach (var _ in wizard.Draft.Answers) { } }
            catch (InvalidOperationException) { readerFaulted = true; }
        });
        Task.WaitAll([.. Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (int index = 0; index < each; index++)
            {
                wizard.Answer($"w{writer}-{index}", "1");
                if (index % 20 == 0) wizard.Report($"w{writer}");
            }
        }))]);
        stop.Cancel(); reader.Wait();
        Check(!readerFaulted, "A reader saw an answer map change under it");
        Check(wizard.Draft.Answers.Count == before + writers * each, "Concurrent answers lost a write");
    }

    /// <summary>Cal-D: the phase agrees with the pinned pose, and an epoch change retires what the loops wrote.</summary>
    public static void RunHeightCheck()
    {
        var height = new CalibrationComponent().Height;
        Check(ReferenceEquals(height.Session, HeightSession.Unloaded) && !height.Active && height.Epoch == 0 && ReferenceEquals(height.Reading, HeightReading.None) &&
            height.GuideProblem == "" && !height.GuideShown && height.MaintenanceIdle.IsCompletedSuccessfully, "A new height check was not idle");

        var pose = Observation("pose-a");
        int reads = 0;
        height.LoadProfile("piper-1", () => { reads++; return HeightSession.For("other") with { Phase = HeightCheckPhase.Live }; });
        height.LoadProfile("piper-1", () => { reads++; return HeightSession.Unloaded; });
        Check(reads == 1 && height.Session is { Profile: "piper-1", Phase: HeightCheckPhase.Inactive } && height.Epoch == 1, "A profile load did not close the check once under its own name");

        height.Start("started");
        Check(height.Session is { Phase: HeightCheckPhase.Live, Status: "started" } && height.Epoch == 2, "Check jaw height did not start Live in a new epoch");
        height.Observe(height.Epoch, null, "no telemetry"); height.ReportGuide(height.Epoch, "no line");
        Check(height.Reading.Issue == "no telemetry" && height.GuideProblem == "no line", "A reading or guide problem of the current epoch was not shown");

        height.Pin(pose, "pinned"); height.EnterTape("120");
        Check(height.Session is { Phase: HeightCheckPhase.Pinned, Status: "pinned", MeasuredText: "120" } && ReferenceEquals(height.Session.Frozen, pose) && height.Epoch == 2,
            "Measure this pose did not pin it");
        Check(!height.Session.Allows(HeightCheckPhase.Saved) && !height.Session.Allows(HeightCheckPhase.Live), "A pinned, unsaved pose allowed Saved or Live");
        height.ReplaceChecks([new(pose, 120, DateTimeOffset.UnixEpoch)]);
        Check(height.Enter(HeightCheckPhase.Saved) && height.Session is { Phase: HeightCheckPhase.Saved, FrozenSaved: true }, "A saved pose did not enter Saved");

        height.Stop();
        Check(height.Session is { Phase: HeightCheckPhase.Inactive, ResumePhase: HeightCheckPhase.Saved, ActionError: "" } && ReferenceEquals(height.Session.Frozen, pose) && height.Epoch == 3,
            "Stop did not keep the pinned pose in a new epoch");
        Check(ReferenceEquals(height.Reading, HeightReading.None) && height.GuideProblem == "", "A reading or guide problem outlived its epoch");
        height.Observe(2, null, "late"); height.ReportGuide(2, "late");
        Check(ReferenceEquals(height.Reading, HeightReading.None) && height.GuideProblem == "", "A late write from an older epoch was shown");
        height.Observe(3, null, "current"); height.ReportGuide(3, "current");
        height.Observe(2, null, "late"); height.ReportGuide(2, "late");
        Check(height.Reading.Issue == "current" && height.GuideProblem == "current", "A late write from an older epoch erased the current reading or guide problem");

        height.Start("again");
        Check(height.Session.Phase == HeightCheckPhase.Saved, "Check jaw height did not resume at the saved pose");
        height.Unpin("another");
        Check(height.Session is { Phase: HeightCheckPhase.Live, Frozen: null, MeasuredText: "", Status: "another" }, "Check another pose kept the pin or its tape entry");

        Check(!height.Enter(HeightCheckPhase.Pinned) && height.Session is { Phase: HeightCheckPhase.Inactive, ActionError.Length: > 0 }, "A phase without its pinned pose did not close the check and say so");
        height.ReportActionError("");
        Check(height.Session.ActionError == "", "The action error was not cleared");

        height.MarkGuideAttempt(77); height.MarkGuideShown();
        Check(height.GuideAttemptedAt == 77 && height.GuideShown && height.TakeGuideShown() && !height.TakeGuideShown() && !height.GuideShown, "The height line was not taken exactly once");

        var running = new TaskCompletionSource();
        int passes = 0;
        height.QueueMaintenance(() => { passes++; return running.Task; });
        height.QueueMaintenance(() => { passes++; return Task.CompletedTask; });
        Check(passes == 1 && !height.MaintenanceIdle.IsCompleted, "A second maintenance pass started while one was running");
        running.SetResult();
        height.QueueMaintenance(() => { passes++; return Task.CompletedTask; });
        Check(passes == 2 && height.MaintenanceIdle.IsCompletedSuccessfully, "Maintenance did not run again once idle");
    }

    /// <summary>Cal-C: one marker at a time, and a conditional move loses to a newer marker.</summary>
    public static void RunMarkers()
    {
        var markers = new CalibrationComponent().Markers;
        Check(ReferenceEquals(markers.Marker, MarkerState.None) && markers.Tool is null && markers.Edge is null && markers.SheetDirty && !markers.SheetPublished &&
            !markers.RetryRequired && markers.Gate.CurrentCount == 1, "New markers were not hidden, due and free");

        var tool = new MarkerState.Tool(new("piper-1", "can-one", 1, "backend", "scene", "robot", "asset", "revision", "site"));
        var edgeScope = new WizardReferenceHighlightScope(Identity, 1, CalibrationWizardStep.DeskFrame, 100, 100, 0, new(0, 0, 0), "edge", Root);
        var edge = new MarkerState.Edge(edgeScope, 0);
        markers.Show(tool, 5);
        Check(ReferenceEquals(markers.Marker, tool) && markers.Tool is not null && markers.Edge is null && markers.AttemptedAt == 5, "Show did not record the marker and its attempt together");
        markers.UnacknowledgeEdge();
        Check(ReferenceEquals(markers.Marker, tool), "A tool marker was treated as an acknowledged edge");

        markers.Show(edge, 6);
        var acknowledged = edge with { AcknowledgedAt = 9 };
        Check(markers.Move(edge, acknowledged) && ReferenceEquals(markers.Marker, acknowledged), "An acknowledgement was not recorded");
        Check(!markers.Move(edge, tool) && ReferenceEquals(markers.Marker, acknowledged), "A move from a replaced marker overwrote the newer one");
        markers.UnacknowledgeEdge();
        Check(markers.Marker is MarkerState.Edge { AcknowledgedAt: 0 } && ReferenceEquals(markers.Edge, edgeScope), "An acknowledged edge kept its acknowledgement");
        markers.MarkAttempt(8);
        Check(markers.AttemptedAt == 8 && markers.Take() is MarkerState.Edge && ReferenceEquals(markers.Marker, MarkerState.None) &&
            ReferenceEquals(markers.Take(), MarkerState.None), "Take did not hand the marker to exactly one leaver");
        markers.Show(tool, 9); markers.Hide();
        Check(ReferenceEquals(markers.Marker, MarkerState.None), "Hide left a marker");

        markers.BeginSheetPublish(11); markers.MarkSheetPublished();
        Check(!markers.SheetDirty && markers.SheetAttemptedAt == 11 && markers.SheetPublished, "A sheet publish was not recorded");
        markers.InvalidateSheet();
        Check(markers.SheetDirty && markers.SheetPublished, "An edited measurement did not make the marks due");
        Check(markers.TakeSheetPublished() && !markers.TakeSheetPublished(), "The published marks were not taken exactly once");
        markers.BeginSheetPublish(12); markers.MarkSheetPublished(); markers.ResetSheet();
        Check(markers.SheetDirty && !markers.SheetPublished, "A model change did not reset the marks");

        markers.RequireRetry();
        Check(markers.RetryRequired, "The retry latch was not set");
        markers.ClearRetry();
        Check(!markers.RetryRequired, "The retry latch was not cleared");
    }

    /// <summary>Cal-E: a step the session cannot make throws and changes nothing.</summary>
    public static void RunLens()
    {
        var lens = new CalibrationComponent().Lens;
        Check(ReferenceEquals(lens.Session, LensSession.Initial) && !lens.WorkerBusy && lens.Worker is null && lens.Profile("left") is null, "A new lens calibration was not idle");

        Check(Throws(lens.BeginSolve) && Throws(() => lens.Rejected("no")) && ReferenceEquals(lens.Session, LensSession.Initial), "A solve without a capture folder changed the session");
        lens.Captured("folder", 3, 4);
        Check(lens.Session is { Phase: LensPhase.Capturing, Folder: "folder", LastSequence: 3, Open: true }, "A capture did not open a session");
        lens.Report("status");
        Check(lens.Session is { Phase: LensPhase.Capturing, Folder: "folder", Status: "status" }, "A status changed more than the status");
        lens.BeginSolve(); lens.Rejected("blurred");
        Check(lens.Session is { Phase: LensPhase.Rejected, Folder: "folder", Open: true } && lens.Session.Status.Contains("blurred"), "A rejected solve did not stay open");
        lens.BeginSolve(); lens.Saved(0.25);
        Check(lens.Session is { Phase: LensPhase.Saved, Folder: null, LastSequence: 3, Open: false }, "A saved solve did not close the session");
        lens.Captured("next", 4, 1); lens.New();
        Check(lens.Session is { Phase: LensPhase.NoSession, Folder: null, LastSequence: 0 }, "New session kept the old one");
        lens.Captured("next", 4, 1); lens.Reset();
        Check(ReferenceEquals(lens.Session, LensSession.Initial), "A profile switch kept the lens session");

        var work = new TaskCompletionSource();
        lens.Run(work.Task);
        Check(lens.WorkerBusy && ReferenceEquals(lens.Worker, work.Task), "A running worker was not busy");
        work.SetException(new InvalidOperationException("solver"));
        Check(!lens.WorkerBusy && lens.Worker is { IsFaulted: true }, "A faulted worker stayed busy or was forgotten");
        _ = work.Task.Exception;

        var left = new JsonObject(); var right = new JsonObject();
        lens.InstallProfile("left", left); lens.InstallProfile("right", right);
        Check(ReferenceEquals(lens.Profile("left"), left) && ReferenceEquals(lens.Profile("right"), right), "An installed profile was not returned");
        lens.RemoveProfile("left");
        Check(lens.Profile("left") is null && lens.Profile("right") is not null, "Removing one profile did not remove exactly it");
        lens.ClearProfiles();
        Check(lens.Profile("right") is null, "Clearing left a profile");
    }

    /// <summary>Cal-F: a refresh supersedes periodic reads, and the shutdown token outlives retirement.</summary>
    public static async Task RunCameraDiscovery()
    {
        var cameras = new CalibrationComponent().Cameras;
        Check(cameras.SimulationCameras is null && cameras.Epoch == 0 && cameras.ColorChoices.Length == 0 && cameras.DepthChoices.Length == 0 &&
            !cameras.Retired && !cameras.Shutdown.IsCancellationRequested, "New camera discovery had found something");

        var scene = new JsonObject();
        cameras.ApplySimulation(scene);
        Check(ReferenceEquals(cameras.SimulationCameras, scene), "The scene's cameras were not recorded");

        cameras.SimulationProbe.Queue(() => Task.FromResult<SimulationCameraDiscovery?>(new(cameras.Epoch, new())));
        Check(cameras.SimulationProbe.HasPending, "A finished read left nothing pending");
        cameras.Supersede();
        Check(cameras.Epoch == 1 && !cameras.SimulationProbe.HasPending, "A refresh did not drop the pending periodic read");

        var inventory = new CameraDeviceInventory([new("Camera", "path-1")], [new("D405", "serial-1")]);
        var none = cameras.ColorChoices;
        Check(cameras.ApplyInventory(inventory) && cameras.ColorChoices.Length == 1 && cameras.DepthChoices.Length == 1 && none.Length == 0,
            "A first device read did not change the choices, or changed a list a reader held");
        string status = cameras.Status;
        var color = cameras.ColorChoices; var depth = cameras.DepthChoices;
        Check(!cameras.ApplyInventory(inventory), "The same device read counted as a change");
        Check(!cameras.ApplyInventory(new(null, null)) && cameras.ColorChoices.Length == color.Length && cameras.DepthChoices.Length == depth.Length &&
            cameras.Status != status && cameras.Status.Contains("temporarily unavailable"), "A read that could not list devices changed the choices or said nothing");
        cameras.Report("probe failed");
        Check(cameras.Status == "probe failed", "A probe report was not shown");

        var token = cameras.Shutdown;
        cameras.DeviceProbe.Queue(() => Task.FromResult<CameraDeviceInventory?>(inventory));
        await cameras.Retire();
        Check(cameras.Retired && token.IsCancellationRequested && cameras.Shutdown.IsCancellationRequested && !cameras.DeviceProbe.HasPending,
            "Retiring did not cancel the token and drop the unapplied read");
    }

    /// <summary>Cal-G: one worker per camera, its phases, and no start after retirement.</summary>
    public static async Task RunCaptureWorkers()
    {
        var capture = new CalibrationComponent().Capture;
        Check(capture.Phase("bench") == CaptureWorkerPhase.NotStarted && capture.LatestFrame("bench") is null && capture.PublishedFrame("bench") is null &&
            capture.Status("bench") is null && capture.Size("bench") is null, "A camera with no worker had a phase, a frame, a status or a size");

        // A preview that is switched off runs no process, so no camera is needed.
        var off = new CameraPreferences("none", Enabled: false);
        CameraCapture? first = null, second = null;
        await capture.Restart("bench", () => first = new CameraCapture(null!, "bench", off, capture.Report, dimensions: capture.Sized));
        Check(first is { Phase: CaptureWorkerPhase.Running } && capture.Phase("bench") == CaptureWorkerPhase.Running, "Restart did not start the worker it built");
        Check(Throws(first!.Start), "A started worker started twice");

        capture.Sized("bench", 640, 480);
        var size = capture.Size("bench");
        capture.Sized("bench", 1920, 1080);
        Check(size is { Width: 640, Height: 480 } && capture.Size("bench") is { Width: 1920, Height: 1080 }, "A new frame size changed the size a reader held");
        capture.Connecting("bench", "Connecting…");
        Check(capture.Size("bench") is null && capture.Status("bench") == "Connecting…", "A reconnecting camera kept its old frame size or status");
        capture.Report("bench", "live");
        Check(capture.Status("bench") == "live" && capture.Status("depth") is null, "A camera's report was lost or reached another camera");

        await capture.Restart("bench", () =>
        {
            Check(first.Phase == CaptureWorkerPhase.Disposed, "A replacement was built before the earlier worker had ended");
            return second = new CameraCapture(null!, "bench", off, capture.Report);
        });
        Check(second is { Phase: CaptureWorkerPhase.Running } && Throws(first.Start), "The replacement did not run, or the replaced worker could start again");
        await capture.Stop("bench");
        Check(second!.Phase == CaptureWorkerPhase.Disposed && capture.Phase("bench") == CaptureWorkerPhase.NotStarted, "Stop left a worker in the slot");

        // A worker whose camera runtime is missing retries; stopping it ends the wait.
        string empty = Path.Combine(Path.GetTempPath(), "piper-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var engine = Fixtures.Context(new LuckyEngine.PluginSdk.PluginStart("fixture", empty, empty), (_, _) => Task.CompletedTask,
                (_, _, _) => Task.FromResult(default(System.Text.Json.JsonElement)));
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await capture.Restart("gripper", () => new CameraCapture(engine, "gripper", new CameraPreferences("none"),
                (camera, text) => { capture.Report(camera, text); failed.TrySetResult(); }));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(capture.Phase("gripper") == CaptureWorkerPhase.Retrying && capture.Status("gripper")!.EndsWith(" · retrying", StringComparison.Ordinal),
                "A worker that could not launch was not retrying: " + capture.Phase("gripper") + " / " + capture.Status("gripper"));

            await capture.Restart("depth", () => new CameraCapture(null!, "depth", off, capture.Report));
            await capture.Retire().WaitAsync(TimeSpan.FromSeconds(5));
            int built = 0;
            await capture.Restart("bench", () => { built++; return new CameraCapture(null!, "bench", off, capture.Report); });
            Check(built == 0 && capture.Phase("gripper") == CaptureWorkerPhase.NotStarted && capture.Phase("depth") == CaptureWorkerPhase.NotStarted &&
                capture.Phase("bench") == CaptureWorkerPhase.NotStarted, "Retired camera workers kept a worker or started another");
            await capture.Retire();
        }
        finally { Directory.Delete(empty, recursive: true); }
    }
}
