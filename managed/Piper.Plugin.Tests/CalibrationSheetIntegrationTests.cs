using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class CalibrationSheetIntegrationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        // Real plugin UI routing, reads, persistence and solver; fake devices and
        // editor callbacks only. StartAsync is never called: no hardware/workers.
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string key, object? value) => typeof(PipersPlugin).GetField(key, hidden)!.SetValue(plugin, value);
        T Get<T>(string key) => (T)typeof(PipersPlugin).GetField(key, hidden)!.GetValue(plugin)!;
        Task Invoke(string key, params object[] args) => (Task)typeof(PipersPlugin).GetMethod(key, hidden)!.Invoke(plugin, args)!;
        var folder = Path.Combine(Path.GetTempPath(), "piper-sheet-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a", RobotId: "fixture-robot")], ActiveRobotId: "piper-1"));
        Set("preferences", preferences);
        var root = CalibrationPlacement.At(new(.1, .75, -.2), 0);
        (double Left, double Back)[] marks = [(697.0, 200.0), (800.0, 450.0), (900.0, 120.0), (800.0, 440.0), (903.0, 350.0)];
        int point = 0;
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
        void Fresh() => Get<LatestTelemetry>("telemetry").Publish(new(snapshot, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
        JsonObject Pose()
        {
            var m = marks[point];
            // Model is displaced 20 mm in X and -10 mm in Z from measured marks.
            var tcp = new CalibrationPoint(m.Left / 1000 - .7 - .02, .75, m.Back / 1000 - .35 + .01);
            return new JsonObject
            {
                ["sceneId"] = "fixture-scene", ["entityId"] = "fixture-robot", ["modelAssetId"] = "fixture-model", ["modelRevision"] = "fixture-revision",
                ["metricPlacementSupported"] = true, ["metricFrame"] = "engine-world-y-up", ["rootPlacement"] = root.Json(),
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad", ["position"] = 0.0 }).ToArray()),
                ["toolSites"] = new JsonArray(new[] { "grasp_site", "calibration_jaw_tips" }.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["unit"] = "m", ["position"] = tcp.Json() }).ToArray())
            };
        }
        JsonObject ApplyPlacement(JsonObject arguments)
        {
            check(root.Same(CalibrationPlacement.Read(arguments["expectedRootPlacement"])), "Apply failed to pin the expected original root placement");
            check(arguments["expectedModelAssetId"]?.GetValue<string>() == "fixture-model" && arguments["expectedModelRevision"]?.GetValue<string>() == "fixture-revision", "Apply failed to pin the model identity");
            var xyz = arguments["position"]!.AsArray();
            root = CalibrationPlacement.At(new(xyz[0]!.GetValue<double>(), xyz[1]!.GetValue<double>(), xyz[2]!.GetValue<double>()), arguments["yawRadians"]!.GetValue<double>());
            return new() { ["applied"] = true, ["pose"] = Pose() };
        }
        JsonObject? view = null, patches = null;
        var calls = new List<string>();
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), hidden, null,
            [new PluginStart("fixture", folder, folder),
                (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!.AsObject();
                    if (json["type"]?.GetValue<string>() == "view") view = json["root"]!.AsObject();
                    if (json["type"]?.GetValue<string>() == "patch") patches = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((method, arguments, _) =>
                {
                    calls.Add(method); Fresh();
                    JsonObject response = method switch
                    {
                        "robot.read" => Pose(),
                        "robot.placement" => ApplyPlacement(arguments),
                        "robot.highlightReference" => new() { ["highlighted"] = true, ["point"] = arguments["point"]?.DeepClone(), ["label"] = arguments["label"]?.DeepClone(), ["rootPlacement"] = root.Json(), ["framed"] = arguments["frame"]?.DeepClone() ?? JsonValue.Create(false), ["markerId"] = arguments["markerId"]?.DeepClone() },
                        "robot.highlightToolSite" => new() { ["highlighted"] = true },
                        "robot.teleoperate" when arguments["enabled"]?.GetValue<bool>() == false => new() { ["enabled"] = false },
                        _ => throw new InvalidOperationException("Unexpected engine mutation: " + method)
                    };
                    return Task.FromResult(JsonSerializer.SerializeToElement(response));
                })], null)!;
        Set("engine", context); Set("token", "fixture-session"); Set("state", snapshot); Fresh();
        Set("link", new RobotLink("fixture-scene", "fixture-robot", Pose()["joints"]!.AsArray(), device));
        Set("linkBackendToken", "fixture-session"); Set("workspacePage", "calibration"); Set("calibrationOpen", true);
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
            await Invoke("Render", CancellationToken.None); await Invoke("Update", CancellationToken.None);
            foreach (var (field, value) in new[] { ("width", "1400"), ("depth", "700"), ("precision", "5") }) await Event("field." + field, value);
            for (int i = 0; i < marks.Length; i++)
            {
                await Event($"field.p{i}left", marks[i].Left.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Event($"field.p{i}back", marks[i].Back.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            await Event("left.show"); await Event("left.confirm"); await Event("back.show"); await Event("back.confirm");
            await Event("tool.show");
            await Invoke("MaintainWizardHighlight", CancellationToken.None);
            check(Get<object?>("wizardHighlightScope") is not null, "Background edge renewal erased the jaw-tip confirmation context");
            await Event("tool.confirm"); await Event("surface");
            check(Get<CalibrationWizardDraft>("calibrationWizard").OperatorSaysTcpMatches, "The actual confirmation event did not persist jaw-tip approval");
            string WidgetId(string suffix) => Walk(view!).Single(w => w["id"]!.GetValue<string>().EndsWith(suffix, StringComparison.Ordinal))["id"]!.GetValue<string>();
            bool CaptureEnabled(int row) => patches?[WidgetId(".sheet.capture." + row)]?["enabled"]?.GetValue<bool>() == true;
            string Status(string suffix) => patches?[WidgetId(suffix)]?["text"]?.GetValue<string>() ?? "";
            await Event("field.precision", "0");
            check(!CaptureEnabled(0) && Status(".sheet.next").Contains("cannot be 0"), "Precision zero silently disabled Capture instead of showing its actionable repair");
            await Event("precision.default");
            check(Get<CalibrationWizardDraft>("calibrationWizard").Answers["precision"] == "5" && CaptureEnabled(0), "Use ±5 mm did not repair the accuracy gate immediately");
            await Event("field.p3back", "450"); await Event("field.p3left", "800");
            check(Status(".sheet.row.status.3").Contains("overlaps P2"), "Duplicate reference measurement was not explained beside its row before hand guiding");
            await Event("field.p3back", "440"); await Event("field.p3left", "800");
            check(!Status(".sheet.row.status.3").Contains("overlaps"), "P4 at BACK 440 / LEFT 800 was rejected merely because P2 is BACK 450 / LEFT 800");
            var validDraft = Get<CalibrationWizardDraft>("calibrationWizard");
            Set("calibrationWizard", validDraft with { ModelRevision = "changed-revision" });
            Fresh(); await Invoke("Update", CancellationToken.None);
            check(!CaptureEnabled(0) && Status(".sheet.next").Contains("Reconfirm"), "Stale identity-bound desk confirmations still enabled capture");
            check(patches?[WidgetId(".sheet.left.confirm")]?["enabled"]?.GetValue<bool>() == true, "Stale LEFT confirmation left the repair button disabled");
            Set("calibrationWizard", validDraft);
            device["status"]!["teach_state"] = 0; device["status"]!["control"] = 1;
            Fresh(); await Invoke("Update", CancellationToken.None);
            check(!CaptureEnabled(0) && Status(".sheet.row.status.0").Contains("solid green"), "Inactive hand guiding silently blocked Capture");
            device["status"]!["teach_state"] = 1; device["status"]!["control"] = 2; device["status"]!["stationary_s"] = .1;
            Fresh(); await Invoke("Update", CancellationToken.None);
            check(!CaptureEnabled(0) && Status(".sheet.row.status.0").Contains("0.4 seconds"), "Moving feedback enabled Capture or failed to explain settling");
            device["status"]!["stationary_s"] = 1.0;
            for (point = 0; point < marks.Length; point++)
            {
                await Event("move." + point);
                int before = Get<List<CalibrationSample>>("calibrationSamples").Count;
                string captureId = Walk(view!).Single(w => w["id"]!.GetValue<string>().EndsWith(".sheet.capture." + point, StringComparison.Ordinal))["id"]!.GetValue<string>();
                check(patches?[captureId]?["enabled"]?.GetValue<bool>() == true, $"Actual UI never enabled capture for valid point {point + 1}");
                if (point == 3) check(CaptureEnabled(3), "The real UI event flow could not capture a distinct reference 10 mm from an earlier reference");
                await Event("capture." + point);
                check(Get<List<CalibrationSample>>("calibrationSamples").Count == before + 1, $"Real row event failed to save point {point + 1}");
            }
            await Event("fit");
            var fit = Get<CalibrationFit>("calibrationFit");
            check(fit.CanApply && fit.TrainingRmsMm < .001 && fit.ValidationMaxMm < .001, "Four actual row captures plus held-out check did not reach a passing alignment");
            check(Get<List<CalibrationSample>>("calibrationSamples").Count(s => s.HeldOut) == 1, "Final check was not kept independent of reference points");
            string report = Get<string>("calibrationPath");
            check(File.Exists(report) && JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!.Samples.Length == 5, "Complete calibration samples did not survive report persistence");
            // Correct a ruler entry without reacquiring or rewriting physical evidence.
            var originalP2 = Get<List<CalibrationSample>>("calibrationSamples")[1];
            long editPage = Get<long>("workspaceViewEpoch");
            await Event("move.1"); await Event("field.p1left", "780");
            check(Get<List<CalibrationSample>>("calibrationSamples")[1] == originalP2 &&
                Get<CalibrationFit>("calibrationFit") == fit, "Typing a correction changed a captured observation or fit before explicit Save entry");
            await Event("move.1");
            check(Get<CalibrationWizardDraft>("calibrationWizard").Answers["p1left"] == "800" &&
                Get<List<CalibrationSample>>("calibrationSamples")[1] == originalP2, "Cancel edit failed to restore the captured coordinate");
            check(Get<long>("workspaceViewEpoch") == editPage, "Edit/cancel rebuilt the sheet and invalidated its visible controls");
            await Event("move.1"); await Event("field.p1left", "-1"); await Event("capture.1");
            check(Get<List<CalibrationSample>>("calibrationSamples")[1] == originalP2 && Get<CalibrationFit>("calibrationFit") == fit,
                "An out-of-desk correction replaced the captured observation");
            await Event("field.p1left", "800"); await Event("field.p1back", "440"); await Event("capture.1");
            check(Get<List<CalibrationSample>>("calibrationSamples")[1] == originalP2,
                "A correction duplicating an already captured reference was saved");
            await Event("field.p1back", "450"); await Event("field.p1left", "780"); await Event("capture.1");
            var correctedP2 = Get<List<CalibrationSample>>("calibrationSamples")[1];
            check(correctedP2.Measurement.LeftMm == 780 && correctedP2.Measurement.BackMm == 450 &&
                Math.Abs(correctedP2.MeasuredWorld.X - (780.0 / 1000 - .7)) < 1e-12,
                "Save entry failed to update the entered ruler measurement and corresponding world point");
            check(correctedP2.SceneWorld == originalP2.SceneWorld && correctedP2.JointDegrees.SequenceEqual(originalP2.JointDegrees) &&
                correctedP2.HostUnixMilliseconds == originalP2.HostUnixMilliseconds && correctedP2.PoseFingerprint == originalP2.PoseFingerprint &&
                correctedP2.Name == originalP2.Name && correctedP2.HeldOut == originalP2.HeldOut,
                "Correcting a ruler entry changed measured pose, acquisition time, identity, fingerprint or held-out role");
            check(Get<CalibrationFit?>("calibrationFit") is null && !patches![WidgetId(".sheet.apply")]!["enabled"]!.GetValue<bool>(),
                "A stale fit or Apply permission survived a measurement correction");
            var correctedReport = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!;
            check(correctedReport.Samples[1].Measurement.LeftMm == 780 && correctedReport.Fit is null,
                "Corrected measurement and fit invalidation did not survive report persistence");
            var auditFiles = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Where(path =>
                path.Contains("measurement-corrections", StringComparison.Ordinal)).ToArray();
            check(auditFiles.Length == 1, "Measurement correction did not retain an audit copy of its prior report");
            var correctionAudit = JsonNode.Parse(File.ReadAllText(auditFiles.Single()))!;
            check(correctionAudit["originalSample"]!["measurement"]!["leftMm"]!.GetValue<double>() == 800 &&
                correctionAudit["correctedMeasurement"]!["leftMm"]!.GetValue<double>() == 780 &&
                correctionAudit["previousReport"]!["samples"]![1]!["measurement"]!["leftMm"]!.GetValue<double>() == 800,
                "Correction audit lost the old observation/report or the explicit new entered value");
            await Event("move.1"); await Event("field.p1left", "800"); await Event("capture.1");
            await Event("fit"); fit = Get<CalibrationFit>("calibrationFit");
            check(fit.CanApply && fit.TrainingRmsMm < .001 && fit.ValidationMaxMm < .001 &&
                Get<List<CalibrationSample>>("calibrationSamples").Count == 5,
                "Correcting an entered coordinate back to its measured value required recapture or failed to recover the fit");
            var savedIdentity = Get<CalibrationIdentity>("calibrationIdentity");
            Set("calibrationIdentity", savedIdentity with { Generation = 999 });
            await Event("move.1");
            check(Get<int>("sheetEditingRow") == -1, "A changed robot connection permitted editing an old captured report");
            Set("calibrationIdentity", savedIdentity); Set("calibrationPlacementUnverified", true);
            await Event("move.1");
            check(Get<int>("sheetEditingRow") == -1, "An unverified scene placement permitted captured-measurement editing");
            Set("calibrationPlacementUnverified", false);
            check(calls.All(c => c is "robot.read" or "robot.highlightReference" or "robot.highlightToolSite" or "robot.teleoperate"), "Calibration attempted a robot/scene mutation before Apply");
            check(root.Same(CalibrationPlacement.At(new(.1, .75, -.2), 0)), "Fit changed the scene before explicit Apply");
            point = marks.Length - 1;
            await Event("apply");
            check(root.Same(fit.ProposedPlacement) && Get<CalibrationPlacement>("calibrationApplied").Same(root), "Actual Apply event did not install and verify the proposed scene root");
            check(!Get<bool>("calibrationPlacementUnverified") && calls.Count(c => c == "robot.placement") == 1, "Apply remained unverified or repeated its scene write");
            var appliedReport = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(report), PluginProtocol.Json)!;
            check(appliedReport.AppliedPlacement!.Same(root) && !appliedReport.PlacementUnverified && appliedReport.Samples.Length == 5, "Verified placement and all observations did not survive final report persistence");
            await Event("move.1");
            check(Get<int>("sheetEditingRow") == -1 && Get<List<CalibrationSample>>("calibrationSamples")[1].Measurement.LeftMm == 800,
                "An applied alignment permitted changing its captured measurement without rollback");
            await Event("inspect");
            check(Get<CalibrationPlacement>("calibrationApplied").Same(root) && calls.Count(c => c == "robot.placement") == 1, "Checking an applied placement repeated a scene write");
        }
        finally { Get<Grpc.Net.Client.GrpcChannel>("channel").Dispose(); Directory.Delete(folder, true); }
    }
}
