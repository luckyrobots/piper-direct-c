using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class CalibrationSheetIntegrationTests
{
    public static async Task Run()
    {
        // Real plugin UI routing, reads, persistence and solver; fake devices and
        // editor callbacks only. StartAsync is never called: no hardware/workers.
        var plugin = new PipersPlugin();
        var folder = Path.Combine(Path.GetTempPath(), "piper-sheet-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a", RobotId: "fixture-robot")], ActiveRobotId: "piper-1"));
        plugin.preferences = preferences;
        var root = CalibrationPlacement.At(new(.1, .75, -.2), 0);
        (double Left, double Back)[] marks = [(697.0, 200.0), (800.0, 450.0), (900.0, 120.0), (800.0, 440.0), (903.0, 350.0)];
        int point = 0;
        bool sharedRoot = false;
        var device = new JsonObject
        {
            ["id"] = "can-a", ["generation"] = 7L, ["fresh"] = true,
            ["status"] = new JsonObject { ["age_s"] = .001, ["control"] = 2, ["teach_state"] = 1, ["arm_status"] = 11, ["error"] = 0, ["stationary_s"] = 1.0 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
            {
                ["actual_deg"] = 0.0, ["fresh"] = true, ["age_s"] = .001, ["enabled"] = false, ["fault"] = 0,
                ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
            }).ToArray())
        };
        var snapshot = new JsonObject { ["token"] = "fixture-session", ["selected"] = "can-a", ["devices"] = new JsonArray(device), ["motion"] = new JsonObject { ["active"] = false } };
        void Fresh() => plugin.telemetry.Publish(new(snapshot, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
        JsonObject Pose()
        {
            var m = marks[point];
            // Model is displaced 20 mm in X and -10 mm in Z from measured marks.
            var tcp = new CalibrationPoint(m.Left / 1000 - .7 - .02, .75, m.Back / 1000 - .35 + .01);
            return new JsonObject
            {
                ["sceneId"] = "fixture-scene", ["entityId"] = "fixture-robot", ["modelAssetId"] = "fixture-model", ["modelRevision"] = "fixture-revision",
                ["metricPlacementSupported"] = true, ["metricFrame"] = "engine-world-y-up", ["rootPlacement"] = root.Json(),
                ["joints"] = new JsonArray((sharedRoot ? new[] { "", "opposite_" } : [""]).SelectMany(prefix =>
                    Enumerable.Range(1, sharedRoot ? 8 : 6).Select(i => (JsonNode)new JsonObject
                    { ["name"] = prefix + "joint" + i, ["unit"] = i <= 6 ? "rad" : "m", ["position"] = 0.0 })).ToArray()),
                ["toolSites"] = new JsonArray(new[] { "grasp_site", "calibration_jaw_tips" }.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["unit"] = "m", ["position"] = tcp.Json() }).ToArray())
            };
        }
        JsonObject ApplyPlacement(JsonObject arguments)
        {
            Check(root.Same(CalibrationPlacement.Read(arguments["expectedRootPlacement"])), "Apply failed to pin the expected original root placement");
            Check(arguments["expectedModelAssetId"]?.GetValue<string>() == "fixture-model" && arguments["expectedModelRevision"]?.GetValue<string>() == "fixture-revision", "Apply failed to pin the model identity");
            var xyz = arguments["position"]!.AsArray();
            root = CalibrationPlacement.At(new(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz[2]!.GetValue<double>()), arguments["yawRadians"]!.GetValue<double>());
            return new() { ["applied"] = true, ["pose"] = Pose() };
        }
        JsonObject? view = null, patches = null;
        var calls = new ConcurrentQueue<string>();
        var deskMarkers = new ConcurrentQueue<(string Id, string Label, bool Framed)>();
        var deskMarkerReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeskMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pauseDeskMarker = 0;
        var context = Fixtures.Context(new PluginStart("fixture", folder, folder),
                (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!.AsObject();
                    if (json["type"]?.GetValue<string>() == "view") view = json["root"]!.AsObject();
                    if (json["type"]?.GetValue<string>() == "patch") patches = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, arguments, ct) =>
                {
                    calls.Enqueue(method); Fresh();
                    if (method == "robot.highlightReference" && arguments["markerId"]?.GetValue<string>() is { } markerId)
                    {
                        deskMarkers.Enqueue((markerId, arguments["label"]?.GetValue<string>() ?? "", arguments["frame"]?.GetValue<bool>() == true));
                        if (Interlocked.Exchange(ref pauseDeskMarker, 0) != 0)
                        {
                            deskMarkerReached.TrySetResult();
                            await releaseDeskMarker.Task.WaitAsync(ct);
                        }
                    }
                    JsonObject response = method switch
                    {
                        "robot.read" => Pose(),
                        "robot.placement" => ApplyPlacement(arguments),
                        "robot.highlightReference" => new() { ["highlighted"] = true, ["point"] = arguments["point"]?.DeepClone(), ["label"] = arguments["label"]?.DeepClone(), ["rootPlacement"] = root.Json(), ["framed"] = arguments["frame"]?.DeepClone() ?? JsonValue.Create(false), ["markerId"] = arguments["markerId"]?.DeepClone() },
                        "robot.highlightToolSite" => new() { ["highlighted"] = true },
                        "robot.teleoperate" when arguments["enabled"]?.GetValue<bool>() == false => new() { ["enabled"] = false },
                        _ => throw new InvalidOperationException("Unexpected engine mutation: " + method)
                    };
                    return JsonSerializer.SerializeToElement(response);
                }));
        plugin.engine = context; plugin.telemetry.AdoptBackend("fixture-session"); plugin.telemetry.PublishState(snapshot); Fresh();
        plugin.link = new RobotLink("fixture-scene", "fixture-robot", Pose()["joints"]!.AsArray(), device);
        plugin.linkBackendToken = "fixture-session"; plugin.workspace.Commit(WorkspaceNavigation.Calibration);
        IEnumerable<JsonObject> Walk(JsonObject node)
        {
            yield return node;
            foreach (var child in node["children"]?.AsArray().OfType<JsonObject>() ?? []) foreach (var nested in Walk(child)) yield return nested;
        }
        async Task Event(string action, string? value = null)
        {
            Fresh();
            string suffix = ".sheet." + action;
            string id = Walk(view!).Single(w => w["id"]!.GetValue<string>().EndsWith(suffix, StringComparison.Ordinal))["id"]!.GetValue<string>();
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", id, value is null ? "click" : "change", JsonSerializer.SerializeToElement(value)), CancellationToken.None);
        }
        try
        {
            await plugin.Render(CancellationToken.None); await plugin.Update(CancellationToken.None);
            var initialDraft = plugin.calibrationWizard!;
            string retiredNext = WorkspaceEventScope.Wrap(plugin.workspace.Epoch, "piper-1", $"wizard.{initialDraft.Epoch}.next");
            await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", retiredNext, "click", JsonSerializer.SerializeToElement(true)), CancellationToken.None);
            Check(ReferenceEquals(initialDraft, plugin.calibrationWizard) && calls.Count == 0,
                "A retired wizard Next event still advanced a persisted draft or called the editor");
            foreach (var (field, value) in new[] { ("width", "1400"), ("depth", "700"), ("precision", "5") }) await Event("field." + field, value);
            for (int i = 0; i < marks.Length; i++)
            {
                await Event($"field.p{i}left", marks[i].Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Event($"field.p{i}back", marks[i].Back.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Volatile.Write(ref pauseDeskMarker, 1);
            Task renewal = plugin.MaintainWizardHighlight(CancellationToken.None);
            Task? selection = null;
            try
            {
                await deskMarkerReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
                selection = Event("move.1");
                Check(!selection.IsCompleted && deskMarkers.Count == 1 && !plugin.calibrationWizard!.Answers.ContainsKey("sheetselected"),
                    "A manual selection changed state or published markers while the old background batch was still in flight");
            }
            finally { Fresh(); releaseDeskMarker.TrySetResult(); }
            await Task.WhenAll(renewal, selection!).WaitAsync(TimeSpan.FromSeconds(3));
            var publishedMarks = deskMarkers.ToArray();
            string[] markerIds = Enumerable.Range(0, 5).Select(row => "desk-point-" + row).ToArray();
            Check(publishedMarks.Select(mark => mark.Id).SequenceEqual(markerIds.Concat(markerIds)) &&
                publishedMarks.Take(5).Where(mark => mark.Label.EndsWith(" - SELECTED", StringComparison.Ordinal)).Select(mark => mark.Id).SequenceEqual(new[] { "desk-point-0" }) &&
                publishedMarks.Skip(5).Where(mark => mark.Label.EndsWith(" - SELECTED", StringComparison.Ordinal) && mark.Framed).Select(mark => mark.Id).SequenceEqual(new[] { "desk-point-1" }) &&
                plugin.calibrationWizard!.Answers["sheetselected"] == "1",
                "An old marker batch overtook or mixed with the newer selected cross");
            await Event("move.0");
            await Event("left.show"); await Event("left.confirm"); await Event("back.show"); await Event("back.confirm");
            await Event("tool.show");
            await plugin.MaintainWizardHighlight(CancellationToken.None);
            Check(plugin.wizardMarker is MarkerState.Tool, "Background edge renewal erased the jaw-tip confirmation context");
            await Event("tool.confirm"); await Event("surface");
            Check(plugin.calibrationWizard!.Setup.JawTipsConfirmed, "The actual confirmation event did not persist jaw-tip approval");
            string WidgetId(string suffix) => Walk(view!).Single(w => w["id"]!.GetValue<string>().EndsWith(suffix, StringComparison.Ordinal))["id"]!.GetValue<string>();
            bool CaptureEnabled(int row) => patches?[WidgetId(".sheet.capture." + row)]?["enabled"]?.GetValue<bool>() == true;
            string Status(string suffix) => patches?[WidgetId(suffix)]?["text"]?.GetValue<string>() ?? "";
            await Event("field.precision", "0");
            Check(!CaptureEnabled(0) && Status(".sheet.next").Contains("cannot be 0"), "Precision zero silently disabled Capture instead of showing its actionable repair");
            await Event("precision.default");
            Check(plugin.calibrationWizard.Answers["precision"] == "5" && CaptureEnabled(0), "Use ±5 mm did not repair the accuracy gate immediately");
            await Event("field.p3back", "450"); await Event("field.p3left", "800");
            Check(Status(".sheet.row.status.3").Contains("overlaps P2"), "Duplicate reference measurement was not explained beside its row before hand guiding");
            await Event("field.p3back", "440"); await Event("field.p3left", "800");
            Check(!Status(".sheet.row.status.3").Contains("overlaps"), "P4 at BACK 440 / LEFT 800 was rejected merely because P2 is BACK 450 / LEFT 800");
            var validDraft = plugin.calibrationWizard;
            plugin.calibrationWizard = validDraft with { ModelRevision = "changed-revision" };
            Fresh(); await plugin.Update(CancellationToken.None);
            Check(!CaptureEnabled(0) && Status(".sheet.next").Contains("Reconfirm"), "Stale identity-bound desk confirmations still enabled capture");
            Check(patches?[WidgetId(".sheet.left.confirm")]?["enabled"]?.GetValue<bool>() == true, "Stale LEFT confirmation left the repair button disabled");
            plugin.calibrationWizard = validDraft;
            device["status"]!["teach_state"] = 0; device["status"]!["control"] = 1;
            Fresh(); await plugin.Update(CancellationToken.None);
            Check(!CaptureEnabled(0) && Status(".sheet.row.status.0").Contains("solid green"), "Inactive hand guiding silently blocked Capture");
            device["status"]!["teach_state"] = 1; device["status"]!["control"] = 2; device["status"]!["stationary_s"] = .1;
            Fresh(); await plugin.Update(CancellationToken.None);
            Check(!CaptureEnabled(0) && Status(".sheet.row.status.0").Contains("0.4 seconds"), "Moving feedback enabled Capture or failed to explain settling");
            device["status"]!["stationary_s"] = 1.0;
            for (point = 0; point < marks.Length; point++)
            {
                await Event("move." + point);
                int before = plugin.calibrationSamples.Count;
                string captureId = Walk(view!).Single(w => w["id"]!.GetValue<string>().EndsWith(".sheet.capture." + point, StringComparison.Ordinal))["id"]!.GetValue<string>();
                Check(patches?[captureId]?["enabled"]?.GetValue<bool>() == true, $"Actual UI never enabled capture for valid point {point + 1}");
                if (point == 3) Check(CaptureEnabled(3), "The real UI event flow could not capture a distinct reference 10 mm from an earlier reference");
                await Event("capture." + point);
                Check(plugin.calibrationSamples.Count == before + 1, $"Real row event failed to save point {point + 1}");
            }
            await Event("fit");
            var fit = plugin.calibrationFit!;
            Check(fit.CanApply && fit.TrainingRmsMm < .001 && fit.ValidationMaxMm < .001, "Four actual row captures plus held-out check did not reach a passing alignment");
            Check(plugin.calibrationSamples.Count(s => s.HeldOut) == 1, "Final check was not kept independent of reference points");
            string report = plugin.calibrationPath;
            Check(File.Exists(report) && JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!.Samples.Length == 5, "Complete calibration samples did not survive report persistence");
            // Correct a ruler entry without reacquiring or rewriting physical evidence.
            var originalP2 = plugin.calibrationSamples[1];
            string reportBeforeBlockedCorrection = File.ReadAllText(report);
            foreach (var (row, left, back) in new[] { (1, "903", "375"), (4, "800", "425") })
            {
                await Event("move." + row);
                await Event($"field.p{row}left", left); await Event($"field.p{row}back", back);
                Check(!CaptureEnabled(row) && Status(".sheet.row.status." + row).Contains("50 mm", StringComparison.Ordinal),
                    "Editing either side of the check/reference pair within 50 mm did not block Save entry with the spacing reason");
                await Event("capture." + row);
                Check(File.ReadAllText(report) == reportBeforeBlockedCorrection && plugin.calibrationFit == fit &&
                    plugin.sheetEditingRow == row,
                    "A stale Save entry click committed a correction inside the 50 mm check spacing");
                await Event("move." + row);
            }
            foreach (var (row, other) in new[] { (1, 3), (3, 1) })
            {
                await Event("move." + row);
                await Event($"field.p{row}left", marks[other].Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Event($"field.p{row}back", marks[other].Back.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check(!CaptureEnabled(row) && Status(".sheet.row.status." + row).Contains($"overlaps P{other + 1}", StringComparison.Ordinal),
                    "Editing a reference to overlap an earlier or later captured reference did not disable Save entry with the overlap reason");
                await Event("capture." + row);
                Check(File.ReadAllText(report) == reportBeforeBlockedCorrection && plugin.calibrationFit == fit &&
                    plugin.sheetEditingRow == row,
                    "A stale Save entry click committed a duplicate reference correction");
                await Event("move." + row);
            }
            long editPage = plugin.workspace.Epoch;
            await Event("move.1"); await Event("field.p1left", "780");
            Check(plugin.calibrationSamples[1] == originalP2 &&
                plugin.calibrationFit == fit, "Typing a correction changed a captured observation or fit before explicit Save entry");
            await Event("move.1");
            Check(plugin.calibrationWizard.Answers["p1left"] == "800" &&
                plugin.calibrationSamples[1] == originalP2, "Cancel edit failed to restore the captured coordinate");
            Check(plugin.workspace.Epoch == editPage, "Edit/cancel rebuilt the sheet and invalidated its visible controls");
            await Event("move.1"); await Event("field.p1left", "-1"); await Event("capture.1");
            Check(plugin.calibrationSamples[1] == originalP2 && plugin.calibrationFit == fit,
                "An out-of-desk correction replaced the captured observation");
            await Event("field.p1back", "450"); await Event("field.p1left", "780"); await Event("capture.1");
            var correctedP2 = plugin.calibrationSamples[1];
            Check(correctedP2.Measurement.LeftMm == 780 && correctedP2.Measurement.BackMm == 450 &&
                Math.Abs(correctedP2.MeasuredWorld.X - (780.0 / 1000 - .7)) < 1e-12,
                "Save entry failed to update the entered ruler measurement and corresponding world point");
            Check(correctedP2.SceneWorld == originalP2.SceneWorld && correctedP2.JointDegrees.SequenceEqual(originalP2.JointDegrees) &&
                correctedP2.HostUnixMilliseconds == originalP2.HostUnixMilliseconds && correctedP2.PoseFingerprint == originalP2.PoseFingerprint &&
                correctedP2.Name == originalP2.Name && correctedP2.HeldOut == originalP2.HeldOut,
                "Correcting a ruler entry changed measured pose, acquisition time, identity, fingerprint or held-out role");
            Check(plugin.calibrationFit is null && !patches![WidgetId(".sheet.apply")]!["enabled"]!.GetValue<bool>(),
                "A stale fit or Apply permission survived a measurement correction");
            var correctedReport = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!;
            Check(correctedReport.Samples[1].Measurement.LeftMm == 780 && correctedReport.Fit is null,
                "Corrected measurement and fit invalidation did not survive report persistence");
            // Simulate a crash after corrected report commit but before the draft
            // commit: restored report observations must own captured coordinates.
            var resumedDraft = plugin.calibrationWizard;
            resumedDraft.Answers["p1left"] = "800"; resumedDraft.Answers["p1back"] = "499";
            plugin.LoadSheetRows();
            var resumedPoint = plugin.SheetPoint(1);
            Check(resumedDraft.Answers["p1left"] == "780" && resumedDraft.Answers["p1back"] == "450" &&
                resumedPoint.HasValue && Math.Abs(resumedPoint.Value.X - correctedP2.MeasuredWorld.X) < 1e-12 &&
                Math.Abs(resumedPoint.Value.Z - correctedP2.MeasuredWorld.Z) < 1e-12,
                "A stale wizard draft overrode corrected report coordinates or published its old viewport marker after interruption");
            var auditFiles = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Where(path =>
                path.Contains("measurement-corrections", StringComparison.Ordinal)).ToArray();
            Check(auditFiles.Length == 1, "Measurement correction did not retain an audit copy of its prior report");
            var correctionAudit = JsonNode.Parse(File.ReadAllText(auditFiles.Single()))!;
            Check(correctionAudit["originalSample"]!["measurement"]!["leftMm"]!.GetValue<double>() == 800 &&
                correctionAudit["correctedMeasurement"]!["leftMm"]!.GetValue<double>() == 780 &&
                correctionAudit["previousReport"]!["samples"]![1]!["measurement"]!["leftMm"]!.GetValue<double>() == 800,
                "Correction audit lost the old observation/report or the explicit new entered value");
            await Event("move.1"); await Event("field.p1left", "800"); await Event("capture.1");
            await Event("fit"); fit = plugin.calibrationFit!;
            Check(fit.CanApply && fit.TrainingRmsMm < .001 && fit.ValidationMaxMm < .001 &&
                plugin.calibrationSamples.Count == 5,
                "Correcting an entered coordinate back to its measured value required recapture or failed to recover the fit");
            var savedIdentity = plugin.calibrationIdentity!;
            plugin.calibrationIdentity = savedIdentity with { Generation = 999 };
            await Event("move.1");
            Check(plugin.sheetEditingRow == -1, "A changed robot connection permitted editing an old captured report");
            plugin.calibrationIdentity = savedIdentity; plugin.calibrationPlacementUnverified = true;
            await Event("move.1");
            Check(plugin.sheetEditingRow == -1, "An unverified scene placement permitted captured-measurement editing");
            plugin.calibrationPlacementUnverified = false;
            Check(calls.All(c => c is "robot.read" or "robot.highlightReference" or "robot.highlightToolSite" or "robot.teleoperate"), "Calibration attempted a robot/scene mutation before Apply");
            Check(root.Same(CalibrationPlacement.At(new(.1, .75, -.2), 0)), "Fit changed the scene before explicit Apply");
            point = marks.Length - 1;
            sharedRoot = true;
            plugin.scene = new JsonObject { ["sceneId"] = "fixture-scene", ["robots"] = new JsonArray(Pose()) };
            await plugin.Render(CancellationToken.None);
            Check(Walk(view!).Any(w => w["id"]!.GetValue<string>().EndsWith(".calibration.branch.explanation", StringComparison.Ordinal)),
                "The unprefixed arm of a compound entity presented an independent calibration sheet");
            plugin.scene = null;
            await plugin.Render(CancellationToken.None);
            await Event("apply");
            Check(!calls.Contains("robot.placement") && !plugin.calibrationPlacementUnverified &&
                plugin.uiActionError.Contains("move every arm", StringComparison.Ordinal),
                "A compound model discovered in the live pose was allowed to write its shared root");
            sharedRoot = false;
            await Event("apply");
            Check(root.Same(fit.ProposedPlacement) && plugin.calibrationApplied!.Same(root), "Actual Apply event did not install and verify the proposed scene root");
            Check(!plugin.calibrationPlacementUnverified && calls.Count(c => c == "robot.placement") == 1, "Apply remained unverified or repeated its scene write");
            var appliedReport = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!;
            Check(appliedReport.AppliedPlacement!.Same(root) && !appliedReport.PlacementUnverified && appliedReport.Samples.Length == 5, "Verified placement and all observations did not survive final report persistence");
            await Event("move.1");
            Check(plugin.sheetEditingRow == -1 && plugin.calibrationSamples[1].Measurement.LeftMm == 800,
                "An applied alignment permitted changing its captured measurement without rollback");
            await Event("inspect");
            Check(plugin.calibrationApplied!.Same(root) && calls.Count(c => c == "robot.placement") == 1, "Checking an applied placement repeated a scene write");
            // Jaw-height phases (Cal-D), reachable from the sheet through the
            // workspace navigation fix (B3).
            plugin.workspace.Commit(WorkspaceNavigation.Calibration);
            async Task Height(string action, string text = "") { Fresh(); await plugin.HeightCheckEvent(action, JsonSerializer.SerializeToElement(text), CancellationToken.None); }
            string HeightPhase() => plugin.heightPhase.ToString()!;
            await Height("toggle");
            Check(HeightPhase() == "Live" && plugin.heightActionError == "", "Check jaw height did not start live: " + plugin.heightActionError);
            await Height("pin");
            Check(HeightPhase() == "Pinned", "Measure this pose did not pin the pose: " + plugin.heightActionError);
            await Height("measured", "250");
            await Height("save");
            Check(HeightPhase() == "Saved", "Save check did not record the pinned pose: " + plugin.heightActionError);
            await Height("another");
            Check(HeightPhase() == "Live" && plugin.heightFrozen is null, "Check another pose kept the previous pinned pose");
            await Height("toggle");
            Check(HeightPhase() == "Inactive", "Stop height check left the check active");
            plugin.workspace.Commit(WorkspaceNavigation.Calibration); await plugin.Render(CancellationToken.None);
            // Reopening the same compiled model must not tell users to redo an
            // already applied alignment just to perform independent height checks.
            var reopenedPose = Pose(); reopenedPose["sceneId"] = "reopened-scene";
            plugin.link = new RobotLink("reopened-scene", savedIdentity.RobotId, reopenedPose["joints"]!.AsArray(), device);
            plugin.acknowledgedScenePose = new JsonObject { ["pose"] = reopenedPose };
            Fresh(); await plugin.Update(CancellationToken.None);
            Check(Status(".sheet.next").Contains("Saved alignment loaded") && Status(".sheet.next").Contains("Check jaw height"),
                "Reopening an applied model gave a misleading re-evaluation prompt instead of the independent height-check guidance");
            Check(!patches![WidgetId(".sheet.fit")]!["enabled"]!.GetValue<bool>() &&
                !patches[WidgetId(".sheet.apply")]!["enabled"]!.GetValue<bool>() && plugin.calibrationIdentity == savedIdentity,
                "The loaded-alignment message relaxed fit/apply scope or rebound saved observations to a new scene session");
            reopenedPose["modelRevision"] = "actually-changed-model";
            Fresh(); await plugin.Update(CancellationToken.None);
            Check(Status(".sheet.next").Contains("model or scene session changed") && !Status(".sheet.next").Contains("Saved alignment loaded"),
                "The loaded-alignment message hid a real compiled model revision change");
        }
        finally { plugin.channel.Dispose(); Directory.Delete(folder, true); }
    }
}
