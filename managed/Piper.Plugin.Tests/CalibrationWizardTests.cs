using System.Text.Json.Nodes;
using System.Text.Json;
using LuckyRobots.Piper.Plugin;

public static class CalibrationWizardTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string description)
        {
            try { action(); } catch (InvalidOperationException) { check(true, description); return; }
            check(false, description);
        }
        var draft = CalibrationWizardFlow.New("robot-a");
        check(draft.Step == CalibrationWizardStep.Prepare && draft.Setup == new SheetSetup(), "A new wizard skipped preparation or invented point verification");
        foreach (string missing in new[] { "width", "depth", "sceney", "floory", "tableheight", "left", "back", "height", "square", "column", "row" })
            check(!draft.Answers.ContainsKey(missing), "A new user received a fabricated measurement: " + missing);
        Reject(() => CalibrationWizardFlow.Number(draft, "tableheight", 1, 3000), "Unknown physical tabletop height silently defaulted to750mm");
        var next = draft with { Step = CalibrationWizardStep.Capture,
            Answers = new(draft.Answers) { ["left"] = "200", ["back"] = "300", ["height"] = "0", ["cross"] = "explicit choice" } };
        var point = CalibrationWizardFlow.NextPoint(next, 1, 0);
        check(!CalibrationWizardFlow.Accepts(point, "robot-a", draft.Epoch), "Delayed event from before capture remained authorized");
        check(!CalibrationWizardFlow.Accepts(point, "robot-b", point.Epoch), "A calibration event crossed robot profiles");
        check(CalibrationWizardFlow.Accepts(point, "robot-a", point.Epoch), "A current calibration event was rejected");
        check(point.Step == CalibrationWizardStep.PointIntro && point.Answers["role"] == "training", "The first capture skipped remaining training locations");
        check(!point.Answers.ContainsKey("left") && !point.Answers.ContainsKey("back") && !point.Answers.ContainsKey("height"), "The next point reused the previous location/height as new evidence");
        check(next.Answers["left"] == "200", "Advancing a point mutated the prior saved answers");
        point = CalibrationWizardFlow.NextPoint(next, 4, 0);
        check(point.Answers["role"] == "validation" && point.Step == CalibrationWizardStep.PointIntro, "Four training captures did not require a NEW held-out point");
        point = CalibrationWizardFlow.NextPoint(next, 4, 1);
        check(point.Step == CalibrationWizardStep.Review, "A validation capture did not go to explicit review");
        foreach (string invalid in new[] { "", "NaN", "Infinity", "-1", "3001" })
        {
            var bad = draft with { Answers = new(draft.Answers) { ["tableheight"] = invalid } };
            Reject(() => CalibrationWizardFlow.Number(bad, "tableheight", 1, 3000), "Invalid/unknown physical height was accepted: " + invalid);
        }
        var measured = draft with { Answers = new(draft.Answers) { ["tableheight"] = "755.5" } };
        check(CalibrationWizardFlow.Number(measured, "tableheight", 1, 3000) == 755.5, "Measured physical height was not preserved exactly");
        var genericDesk = draft with { Answers = new(draft.Answers) { ["sceney"] = "800", ["floory"] = "0", ["tableheight"] = "800" } };
        check(CalibrationWizardFlow.SceneSurface(genericDesk, 800) == 800, "A generic800mm scene tabletop required editing private JSON");
        Reject(() => CalibrationWizardFlow.SceneSurface(genericDesk, 750), "A mismatched model base silently floated to a ruler-derived scene height");
        Reject(() => CalibrationWizardFlow.SceneSurface(draft), "An unknown scene surface silently defaulted to750mm");
        var tabletopDraft = draft with { Answers = new(draft.Answers) { ["width"] = "1400", ["depth"] = "700", ["sceney"] = "800", ["precision"] = "10", ["left"] = "400", ["back"] = "300", ["height"] = "0" } };
        var tabletop = CalibrationWizardFlow.TabletopInputs(tabletopDraft);
        check(tabletop.Measurement.World(tabletop.Desk).Distance(new(-.3, .8, -.05)) < 1e-12, "Tabletop capture required a physical floor/table-height measurement or used a wrong height frame");
        var inconsistentOffice = tabletopDraft with { Answers = new(tabletopDraft.Answers) { ["floory"] = "1000", ["tableheight"] = "740" } };
        check(CalibrationWizardFlow.TabletopInputs(inconsistentOffice) == tabletop, "An unrelated optional office-height audit blocked tabletop capture");
        // Old question-by-question step numbers resume at the matching part of the single sheet.
        check(CalibrationWizardFlow.Legacy((CalibrationWizardStep)5) == CalibrationWizardStep.Prepare && CalibrationWizardFlow.Legacy((CalibrationWizardStep)20) == CalibrationWizardStep.PointIntro &&
            CalibrationWizardFlow.Legacy(CalibrationWizardStep.DeskBack) == CalibrationWizardStep.DeskBack, "An old draft still forced irrelevant floor questions on resume/back");
        var abstractLegacy = CalibrationWizardFlow.TabletopDefault(tabletopDraft with { Schema = 1, Step = CalibrationWizardStep.PointIntro,
            Setup = new("root", "old proof", "old proof", JawTipsConfirmed: true) });
        check(abstractLegacy.Schema == 2 && abstractLegacy.Step == CalibrationWizardStep.Prepare && abstractLegacy.Setup == new SheetSetup(JawTipsConfirmed: true) && abstractLegacy.Epoch > tabletopDraft.Epoch,
            "An uncaptured old draft invented confirmation of the new desk-edge pointers");
        var savedLegacy = CalibrationWizardFlow.TabletopDefault(tabletopDraft with { Schema = 1, Step = CalibrationWizardStep.InspectSaved, ReportFile = "saved-calibration.json" });
        check(savedLegacy.Step == CalibrationWizardStep.InspectSaved && savedLegacy.ReportFile == "saved-calibration.json", "Pointer-question migration discarded a saved placement inspection/report");
        check((int)CalibrationWizardStep.DeskFrame == 3 && (int)CalibrationWizardStep.InspectSaved == 24 && (int)CalibrationWizardStep.DeskBack == 25,
            "Appending BACK edge changed the meaning of existing saved numeric step values");
        var relativeSamples = new[] { (300d, 200d), (600d, 200d), (600d, 500d), (300d, 500d), (450d, 350d) }.Select((p, i) =>
        {
            var pointDraft = tabletopDraft with { Answers = new(tabletopDraft.Answers) { ["left"] = p.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture), ["back"] = p.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            var pointInput = CalibrationWizardFlow.TabletopInputs(pointDraft);
            var physical = pointInput.Measurement.World(pointInput.Desk);
            return new CalibrationSample("tabletop" + i, physical, new(physical.X + .1, physical.Y, physical.Z + .05), [0, 0, 0, 0, 0, 0], i, i == 4, pointInput.Measurement, "synthetic-fk");
        }).ToArray();
        var relativeFit = CalibrationSolver.Fit(tabletop.Desk, CalibrationPlacement.At(new(.1, .8, .05), 0), relativeSamples);
        check(relativeFit.CanApply && relativeFit.ProposedPlacement.Position.Distance(new(0, .8, 0)) < 1e-12, "A tabletop-only dataset could not fit/validate without floor measurements");
        var wrongTip = relativeSamples.Select(s => s with { SceneWorld = new(s.SceneWorld.X, s.SceneWorld.Y + .03, s.SceneWorld.Z) }).ToArray();
        var wrongTipFit = CalibrationSolver.Fit(tabletop.Desk, CalibrationPlacement.At(new(.1, .8, .05), 0), wrongTip);
        check(!wrongTipFit.CanApply && wrongTipFit.VerticalRmsMm > 20 && wrongTipFit.ProposedPlacement.Position.Y == .8, "Removing the floor gate hid a wrong TCP vertical residual or floated the base");
        var stored = JsonSerializer.Deserialize<CalibrationWizardFile>(JsonSerializer.Serialize(CalibrationWizardFile.From(measured)))!.ToDraft();
        CalibrationWizardFlow.ValidateDraft(stored, "robot-a");
        check(stored.Answers["tableheight"] == "755.5" && stored.Setup == new SheetSetup(), "Draft persistence invented defaults or promoted model verification");
        // The saved file keeps the keys earlier versions wrote; a file where only
        // the flag was cleared (the old physicalpoint/flag disagreement) restores unconfirmed.
        var confirmed = measured with { Setup = new("root", "proof", "proof", JawTipsConfirmed: true, SurfaceConfirmed: true, ReferencePreviewed: true) };
        var savedFile = CalibrationWizardFile.From(confirmed);
        check(savedFile.OperatorSaysTcpMatches && savedFile.Answers["edgeroot"] == "root" && savedFile.Answers["leftedgeconfirmed"] == "proof" && savedFile.Answers["frame"] == "proof" &&
            savedFile.Answers.ContainsKey("physicalpoint") && savedFile.Answers.ContainsKey("sceneconfirmed") && savedFile.Answers.ContainsKey("sheetreferencepreview") &&
            !confirmed.Answers.ContainsKey("frame"), "Saved sheet confirmations changed the draft file format or leaked into the measurements");
        var reloaded = JsonSerializer.Deserialize<CalibrationWizardFile>(JsonSerializer.Serialize(savedFile))!.ToDraft();
        check(reloaded.Setup == confirmed.Setup && !reloaded.Answers.ContainsKey("frame") && !reloaded.Answers.ContainsKey("physicalpoint"), "Sheet confirmations did not survive a draft save and load");
        check(!(savedFile with { OperatorSaysTcpMatches = false }).ToDraft().Setup.JawTipsConfirmed, "A saved physical point without the operator flag restored jaw-tip confirmation");
        check((savedFile with { Answers = new(savedFile.Answers.Where(p => p.Key != "leftedgeconfirmed")) }).ToDraft().Setup.BackProof.Length == 0,
            "A saved BACK confirmation without LEFT was restored");
        check((savedFile with { Step = (CalibrationWizardStep)19 }).ToDraft().Step == CalibrationWizardStep.PointIntro, "An old question step was not mapped on load");
        Reject(() => CalibrationWizardFlow.ValidateDraft(stored, "robot-b"), "A restored draft crossed robot identities");
        Reject(() => CalibrationWizardFlow.ValidateDraft(stored with { ReportFile = "../another-profile/report.json" }, "robot-a"), "A draft report path escaped its per-robot settings folder");
        Reject(() => CalibrationWizardFlow.ValidateDraft(stored with { ReportFile = "settings.json" }, "robot-a"), "A draft could overwrite settings with a report evidence sidecar");
        Reject(() => CalibrationWizardFlow.ValidateDraft(stored with { Epoch = long.MaxValue }, "robot-a"), "A malicious saved epoch could wrap into a repeated step ID");
        var measurement = new CalibrationMeasurement(LeftMm: 200, BackMm: 300, HeightMm: 0);
        var savedSample = new CalibrationSample("training1", new(0, .75, 0), new(0, .75, 0), [0, 0, 0, 0, 0, 0], 1, false, measurement, "pose");
        check(CalibrationWizardFlow.AlreadyRecorded(next, savedSample), "Interrupted post-capture UI did not recognize its already saved observation");
        check(!CalibrationWizardFlow.AlreadyRecorded(next, savedSample with { HeldOut = true }), "A previous training sample was mistaken for new validation");
        check(!CalibrationWizardFlow.AlreadyRecorded(next, savedSample with { Measurement = measurement with { LeftMm = 250 } }), "A different measured point was silently marked captured on resume");
        var live = new CalibrationIdentity("robot-a", "can-a", 3, "backend-a", "scene-a", "model-root-a", "asset-a", "revision-a");
        var marker = new WizardHighlightScope(live.ProfileId, live.DeviceId, live.Generation, live.BackendSessionFingerprint, live.SceneId, live.RobotId, live.ModelAssetId, live.ModelRevision, "grasp_site");
        check(marker.Matches(live, "grasp_site", "asset-a", "revision-a", true, true), "A matching fresh read-only marker context was rejected");
        foreach (var changed in new[] { live with { ProfileId = "robot-b" }, live with { DeviceId = "can-b" }, live with { Generation = 4 }, live with { BackendSessionFingerprint = "backend-b" }, live with { SceneId = "scene-b" }, live with { RobotId = "root-b" } })
            check(!marker.Matches(changed, "grasp_site", "asset-a", "revision-a", true, true), "A temporary marker crossed robot/backend/subscription identity");
        check(!marker.Matches(live, "grasp_site", "asset-a", "revision-b", true, true), "A marker renewed against changed compiled geometry");
        check(!marker.Matches(live, "another_site", "asset-a", "revision-a", true, true), "A marker silently followed a changed selected site");
        check(!marker.Matches(live, "grasp_site", "asset-a", "revision-a", false, true) && !marker.Matches(live, "grasp_site", "asset-a", "revision-a", true, false), "A marker renewed after leaving calibration or stale robot feedback");
        var clear = marker.Request(false);
        check(clear["entityId"]!.GetValue<string>() == "model-root-a" && clear["sceneId"]!.GetValue<string>() == "scene-a" && clear["expectedModelRevision"]!.GetValue<string>() == "revision-a" && !clear["visible"]!.GetValue<bool>(), "Clearing substituted a newly selected root instead of the pinned marker context");
        check(marker.Request(true)["durationSeconds"]!.GetValue<int>() == 30, "Temporary marker lost its bounded native expiry");
        var edgeDraft = tabletopDraft with { Step = CalibrationWizardStep.DeskFrame, ModelAssetId = live.ModelAssetId, ModelRevision = live.ModelRevision,
            Setup = new(SurfaceConfirmed: true) };
        var edgeRoot = CalibrationPlacement.At(new(.1, .8, -.2), .2);
        var leftEdge = WizardReferenceHighlightScope.Create(live, edgeDraft, edgeRoot);
        check(leftEdge.Point.Distance(new(-.7, .803, 0)) < 1e-12 && leftEdge.Label == "LEFT desk edge", "LEFT pointer did not mark the measured desk's short-edge midpoint just above its confirmed surface");
        var previewDraft = edgeDraft with { Setup = new(ReferencePreviewed: true) };
        check(WizardReferenceHighlightScope.Create(live, previewDraft, edgeRoot).Point == leftEdge.Point && !previewDraft.Setup.SurfaceConfirmed, "A visual edge preview required or invented physical tabletop confirmation");
        var backDraft = edgeDraft with { Step = CalibrationWizardStep.DeskBack, Epoch = edgeDraft.Epoch + 1 };
        var backEdge = WizardReferenceHighlightScope.Create(live, backDraft, edgeRoot);
        check(backEdge.Point.Distance(new(0, .803, -.35)) < 1e-12 && backEdge.Label == "BACK desk edge", "BACK pointer did not mark the measured far long edge");
        check(WizardReferenceHighlightScope.RefreshDue(leftEdge, backDraft, TimeSpan.Zero), "The BACK question inherited the previous LEFT pointer's ten-second delay");
        check(WizardReferenceHighlightScope.RefreshDue(null, edgeDraft, TimeSpan.Zero), "A first/resumed LEFT pointer waited for a recent tool-marker renewal");
        check(WizardReferenceHighlightScope.RefreshDue(leftEdge, edgeDraft with { Epoch = edgeDraft.Epoch + 1 }, TimeSpan.Zero), "Returning to a desk-edge question did not show its pointer immediately");
        check(!WizardReferenceHighlightScope.RefreshDue(leftEdge, edgeDraft, TimeSpan.FromSeconds(9.9)) && WizardReferenceHighlightScope.RefreshDue(leftEdge, edgeDraft, TimeSpan.FromSeconds(10)),
            "Same-question renewals lost the ten-second limit or repeatedly stole viewport navigation");
        check(leftEdge.DeskFingerprint() == backEdge.DeskFingerprint(), "Sequential LEFT/BACK confirmations did not refer to one fixed robot/desk reference");
        check(leftEdge.Matches(live, edgeDraft, edgeRoot, true, true) && !leftEdge.Matches(live, backDraft, edgeRoot, true, true), "A LEFT pointer acknowledgement authorized the BACK question");
        check(!leftEdge.Matches(live, edgeDraft with { Epoch = edgeDraft.Epoch + 1 }, edgeRoot, true, true), "A stale pointer survived returning to an earlier question");
        foreach (var changed in new[] { live with { ProfileId = "robot-b" }, live with { DeviceId = "can-b" }, live with { Generation = 4 }, live with { BackendSessionFingerprint = "backend-b" }, live with { SceneId = "scene-b" }, live with { RobotId = "root-b" }, live with { ModelRevision = "revision-b" } })
            check(!leftEdge.Matches(changed, edgeDraft, edgeRoot, true, true), "A desk pointer acknowledgement crossed its robot/backend/model identity");
        foreach (string changedKey in new[] { "width", "depth", "sceney" })
            check(!leftEdge.Matches(live, edgeDraft with { Answers = new(edgeDraft.Answers) { [changedKey] = changedKey == "sceney" ? "900" : "1000" } }, edgeRoot, true, true), "A desk pointer survived changed measurements: " + changedKey);
        check(!leftEdge.Matches(live, edgeDraft, edgeRoot, false, true) && !leftEdge.Matches(live, edgeDraft, edgeRoot, true, false), "A pointer remained confirmable after leaving the page or losing fresh feedback");
        Reject(() => WizardReferenceHighlightScope.Create(live, edgeDraft with { Answers = new(edgeDraft.Answers.Where(p => p.Key != "width")) }, edgeRoot), "Pointer guessed an unmeasured desk width");
        Reject(() => WizardReferenceHighlightScope.Create(live, edgeDraft with { Setup = new() }, edgeRoot), "Pointer assumed an unconfirmed simulated tabletop");
        var edgeReply = JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.7, .803, 0 }, rootPlacement = edgeRoot.Json(), framed = false });
        check(leftEdge.Acknowledged(edgeReply), "Exact native desk-pointer echo was rejected");
        check(!backEdge.Acknowledged(edgeReply), "A returned LEFT pointer was accepted for the BACK question");
        check(!leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.6, .803, 0 }, rootPlacement = edgeRoot.Json(), framed = false })), "A wrong pointer position was accepted as shown");
        check(!leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true })) && !leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = false })), "An absent/incomplete marker acknowledgement enabled edge confirmation");
        var edgeRequest = leftEdge.Request(true);
        check(edgeRequest["point"]!.AsArray().Count == 3 && edgeRequest["siteName"] is null && edgeRequest["durationSeconds"]!.GetValue<int>() == 30 &&
            edgeRequest["expectedModelRevision"]!.GetValue<string>() == live.ModelRevision, "Desk pointer request gained motion/site commands or lost revision/expiry scope");
        foreach (string error in new[] { "Unsupported robot API", "Unsupported or unauthorized editor API: robot.highlightReference", "Unknown request: robot.highlightReference" })
            check(WizardReferenceHighlightScope.FailureMessage(new InvalidOperationException(error)).StartsWith("Update Lucky Engine to show desk markers", StringComparison.Ordinal),
                "An older engine exposed a raw unsupported-request error instead of update guidance");
        check(!WizardReferenceHighlightScope.FailureMessage(new InvalidOperationException("Missing capability: scene.robots")).Contains("Update Lucky Engine", StringComparison.Ordinal),
            "A real permission problem was mislabeled as an old engine");
        check(WizardReferenceHighlightScope.FailureMessage(new OperationCanceledException()).Contains("Show edge to retry", StringComparison.Ordinal),
            "A marker timeout did not explain the next action or kept confirming without a pointer");
        check(WizardReferenceHighlightScope.DeskFingerprint(live, 1400, 700, 800, edgeRoot) == leftEdge.DeskFingerprint() &&
            WizardReferenceHighlightScope.DeskFingerprint(live with { ModelRevision = "changed" }, 1400, 700, 800, edgeRoot) != leftEdge.DeskFingerprint(),
            "Capture could reuse an old LEFT/BACK confirmation after the robot model revision changed");
        foreach (var changedRoot in new[] { CalibrationPlacement.At(new(.2, .8, -.2), .2), CalibrationPlacement.At(new(.1, .8, -.2), .3) })
        {
            check(!leftEdge.Matches(live, edgeDraft, changedRoot, true, true), "A reference pointer silently repinned a moved/rotated simulated robot");
            check(!leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.7, .803, 0 }, rootPlacement = changedRoot.Json(), framed = false })),
                "A pointer acknowledgement for a changed root placement was accepted");
            check(WizardReferenceHighlightScope.DeskFingerprint(live, 1400, 700, 800, changedRoot) != leftEdge.DeskFingerprint(), "LEFT/BACK proof was not bound to the simulated robot placement");
        }
        check(leftEdge.Matches(live, edgeDraft, new(edgeRoot.Position, edgeRoot.Rotation.Select(x => -x).ToArray()), true, true), "Sign-equivalent quaternion changed a pinned pointer's orientation");
        check(leftEdge.Request(true, frame: true)["frame"]!.GetValue<bool>() && !leftEdge.Request(true)["frame"]!.GetValue<bool>() &&
            CalibrationPlacement.Read(leftEdge.Request(true)["expectedRootPlacement"]).Same(edgeRoot), "First/manual framing or non-framing renewals lost their exact expected root pose");
        check(leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.7, .803, 0 }, rootPlacement = edgeRoot.Json(), framed = true }), true),
            "A successfully framed pointer was rejected");
        check(!leftEdge.Acknowledged(edgeReply, frameRequested: true), "Manual Show accepted an unframed marker while the reference remained out of view");
        check(!leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.7, .803, 0 }, rootPlacement = edgeRoot.Json(), framed = true })),
            "A renewal unexpectedly framed the viewport instead of preserving user navigation");
        check(!leftEdge.Acknowledged(JsonSerializer.SerializeToElement(new { highlighted = true, label = leftEdge.Label, point = new[] { -.7, .803, 0 }, framed = false })),
            "A pointer without an echoed root placement enabled confirmation");
        check(WizardReferenceHighlightScope.FailureMessage(new InvalidOperationException("Viewport overlays are hidden. Enable viewport overlays, then retry.")).Contains("Enable viewport overlays", StringComparison.Ordinal),
            "Hidden-overlay guidance was lost behind a generic marker failure");
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var copyPlugin = new PipersPlugin();
        var sheetDraft = draft with { Answers = new(draft.Answers) { ["left"] = "697", ["back"] = "200", ["height"] = "75" } };
        typeof(PipersPlugin).GetField("calibrationWizard", hidden)!.SetValue(copyPlugin, sheetDraft);
        typeof(PipersPlugin).GetMethod("LoadSheetRows", hidden)!.Invoke(copyPlugin, null);
        check(sheetDraft.Answers["p0left"] == "697" && sheetDraft.Answers["p0back"] == "200" && !sheetDraft.Answers.ContainsKey("p0height"), "Sheet migration lost current XY measurements or carried hover height into desk-contact rows");
        foreach (int row in Enumerable.Range(0, 5)) foreach (string axis in new[] { "left", "back" }) sheetDraft.Answers[$"p{row}{axis}"] = (100 + row * 100).ToString();
        CalibrationWizardFlow.ValidateDraft(sheetDraft, "robot-a");
        var roundTrip = JsonSerializer.Deserialize<CalibrationWizardDraft>(JsonSerializer.Serialize(sheetDraft))!;
        check(Enumerable.Range(0, 5).All(row => new[] { "left", "back" }.All(axis => roundTrip.Answers[$"p{row}{axis}"] == sheetDraft.Answers[$"p{row}{axis}"])), "Batch point inputs did not survive draft serialization");
    }
}
