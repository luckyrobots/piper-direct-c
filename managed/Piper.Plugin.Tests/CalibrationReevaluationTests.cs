using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class CalibrationReevaluationTests
{

    public static async Task Run()
    {
        await SuccessfulReevaluation();
        await ReopenedSceneReevaluation("new-model");
        await ReopenedSceneReevaluation("old-model");
        foreach (string unavailable in new[] { "unsupported-reason", "metric-reason" })
        {
            using var fixture = new Fixture { Failure = unavailable };
            string before = File.ReadAllText(fixture.ReportPath);
            await fixture.Plugin.Render(CancellationToken.None);
            await fixture.Event("reevaluate");
            string displayed = fixture.Patches?["sheet.action.feedback"]?["text"]?.GetValue<string>() ?? "missing patch: " + fixture.Plugin.wizardMessage;
            Check(displayed.Contains("fixture capability reason"),
                "The native calibration UI replaced the engine's actionable capability reason with a generic message: " + displayed);
            Check(File.ReadAllText(fixture.ReportPath) == before && fixture.Calls.SequenceEqual(["robot.read"]),
                "An unsupported capability changed saved captures or attempted evaluation/movement");
        }
        foreach (string failure in new[] { "revision", "root", "joint", "dependency", "not-read-only", "connection", "read-changed", "unsupported", "scene-switch", "initial-root", "asset" })
        {
            using var fixture = new Fixture { Failure = failure };
            string before = File.ReadAllText(fixture.ReportPath);
            await RejectAsync<InvalidOperationException>(() => fixture.Reevaluate(), failure + " unexpectedly replaced a saved calibration");
            Check(File.ReadAllText(fixture.ReportPath) == before && fixture.Plugin.calibrationIdentity == fixture.Identity &&
                fixture.Plugin.calibrationSamples.SequenceEqual(fixture.Originals),
                failure + " left partial recomputed geometry or identity in the saved report");
            Check(fixture.Calls.All(c => c is "robot.read" or "robot.evaluate"), failure + " attempted to replay a saved pose into the live scene");
        }
        using (var wrongRobot = new Fixture("reopened-scene", entityId: "other-robot"))
        {
            await RejectAsync<InvalidOperationException>(() => wrongRobot.Reevaluate(), "A reopened scene rebound captures to a different entity UUID");
            Check(wrongRobot.Calls.Count == 0, "A different entity reached model evaluation before its identity was rejected");
        }
        foreach (string blocked in new[] { "calibrationApplied", "calibrationPlacementUnverified", "sheetEditingRow", "stale", "motion" })
        {
            using var fixture = new Fixture();
            if (blocked == "calibrationApplied") fixture.Plugin.calibration.Desk.PlacementVerified(fixture.Root);
            else if (blocked == "calibrationPlacementUnverified") fixture.Plugin.calibration.Desk.MarkWritePending();
            else if (blocked == "sheetEditingRow") fixture.Plugin.calibration.Wizard.EditRow(1);
            else if (blocked == "stale") fixture.State["devices"]![0]!["fresh"] = false;
            else fixture.State["motion"]!["active"] = true;
            await RejectAsync<InvalidOperationException>(() => fixture.Reevaluate(), blocked + " allowed re-evaluation");
            Check(fixture.Calls.Count == 0, blocked + " reached the editor before its local guard rejected re-evaluation");
        }
    }

    private static async Task ReopenedSceneReevaluation(string revision)
    {
        using var f = new Fixture("reopened-scene", revision);
        f.Plugin.acknowledgedScenePose = new JsonObject { ["pose"] = f.Pose() };
        await f.Plugin.Render(CancellationToken.None); await f.Plugin.Update(CancellationToken.None);
        string id = f.WidgetId(".sheet.reevaluate");
        Check(f.Patches?[id]?["enabled"]?.GetValue<bool>() == true && f.Patches[id]?["visible"]?.GetValue<bool>() == true,
            "A reopened scene hid the only explicit way to re-evaluate saved observations");
        await f.Event("reevaluate");
        var report = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(f.ReportPath), PluginProtocol.Json)!;
        Check(report.Identity == (f.Identity with { SceneId = "reopened-scene", ModelRevision = revision }) && report.Fit is null &&
            report.ModelReevaluation is { PreviousSceneId: "scene-a", SceneId: "reopened-scene" },
            "Explicit re-evaluation did not pin and audit the reopened scene while preserving the physical robot identity");
        Check(report.Samples.Where((s, i) => s.PoseFingerprint == f.Originals[i].PoseFingerprint && s.JointDegrees.SequenceEqual(f.Originals[i].JointDegrees) &&
            s.Measurement == f.Originals[i].Measurement && s.HostUnixMilliseconds == f.Originals[i].HostUnixMilliseconds).Count() == 5,
            "Scene-session rebinding rewrote physical capture evidence");
        var audit = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Directory, "calibration", "piper-1", report.ModelReevaluation!.AuditFile)))!;
        Check(audit["previousSceneId"]!.GetValue<string>() == "scene-a" && audit["sceneId"]!.GetValue<string>() == "reopened-scene" &&
            audit["previousReport"]!["identity"]!["sceneId"]!.GetValue<string>() == "scene-a", "Scene rebinding lost its original-scene provenance");
        var draft = f.Plugin.calibrationWizard!;
        f.Plugin.calibration.Wizard.Answer("reevaluatedscene", "scene-a");
        f.Plugin.calibration.Wizard.ChangeSetup(_ => new("old-root", "old-proof", "old-proof", JawTipsConfirmed: true));
        f.Plugin.ReconcileReevaluatedWizard();
        draft = f.Plugin.calibrationWizard!;
        Check(draft.Answers["reevaluatedscene"] == "reopened-scene" && draft.Setup == new SheetSetup(),
            "An interrupted scene-only rebind restored confirmations from the old scene when the model revision was unchanged");
        foreach (string action in new[] { "left.show", "left.confirm", "back.show", "back.confirm", "tool.show", "tool.confirm", "surface", "fit" })
            await f.Event(action);
        Check(f.Plugin.calibrationFit!.CanApply && f.Patches![f.WidgetId(".sheet.apply")]!["enabled"]!.GetValue<bool>(),
            "A deliberately rebound scene could not reconfirm references and calculate normally");
    }

    private static async Task SuccessfulReevaluation()
    {
        using var f = new Fixture();
        f.Plugin.acknowledgedScenePose = new JsonObject { ["pose"] = f.Pose() };
        await f.Plugin.Render(CancellationToken.None); await f.Plugin.Update(CancellationToken.None);
        string id = f.Widgets().Single(w => w["id"]!.GetValue<string>().EndsWith(".sheet.reevaluate", StringComparison.Ordinal))["id"]!.GetValue<string>();
        Check(f.Patches?[id]?["visible"]?.GetValue<bool>() == true && f.Patches[id]?["enabled"]?.GetValue<bool>() == true,
            "A corrected model did not expose the explicit re-evaluation action");
        await f.Plugin.HandleEventAsync(new("luckyrobots.pipers.controls", id, "click", JsonSerializer.SerializeToElement<object?>(null)), CancellationToken.None);
        var current = f.Plugin.calibrationSamples;
        Check(current.Count == 5 && current.Where((s, i) => s.SceneWorld != f.Originals[i].SceneWorld).Count() == 5,
            "The explicit UI action failed to recompute all five saved scene points");
        for (int i = 0; i < current.Count; i++)
        {
            var before = f.Originals[i]; var after = current[i];
            Check(after.JointDegrees.SequenceEqual(before.JointDegrees) && after.Measurement == before.Measurement && after.MeasuredWorld == before.MeasuredWorld &&
                after.HostUnixMilliseconds == before.HostUnixMilliseconds && after.PoseFingerprint == before.PoseFingerprint && after.HeldOut == before.HeldOut,
                "Re-evaluation rewrote physical evidence or the independent-check role for point " + i);
        }
        var report = JsonSerializer.Deserialize<CalibrationReport>(File.ReadAllText(f.ReportPath), PluginProtocol.Json)!;
        Check(report.Identity.ModelRevision == "new-model" && report.Fit is null && report.AppliedPlacement is null && !report.PlacementUnverified,
            "A corrected model kept an old fit, application permission or stale report identity");
        Check(report.ModelReevaluation is { PreviousModelRevision: "old-model", ModelRevision: "new-model" } &&
            report.ModelReevaluation.Evidence.Contains("original capture fingerprints"), "Recomputed scene data did not retain explicit derivation provenance");
        var audit = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Directory, "calibration", "piper-1", report.ModelReevaluation!.AuditFile)))!;
        Check(audit["previousReport"]!["identity"]!["modelRevision"]!.GetValue<string>() == "old-model" && audit["evaluations"]!.AsArray().Count == 5 &&
            audit["previousWizard"]!["operatorSaysTcpMatches"]!.GetValue<bool>(), "Re-evaluation audit omitted original report, physical-point confirmation or per-pose receipts");
        var draft = f.Plugin.calibrationWizard!;
        Check(draft.ModelRevision == "new-model" && draft.Setup == new SheetSetup() &&
            !draft.Answers.ContainsKey("frame") && !draft.Answers.ContainsKey("sceneconfirmed"), "Old model visual confirmations survived re-evaluation");
        Check(draft.Answers["p1left"] == "600" && !f.Patches![f.WidgetId(".sheet.fit")]!["enabled"]!.GetValue<bool>(),
            "Re-evaluation lost ruler coordinates or enabled calculation before confirming the corrected references");
        Check(f.Calls.Count(c => c == "robot.evaluate") == 5 && f.Calls.All(c => c is "robot.read" or "robot.evaluate"),
            "Re-evaluation replayed saved angles into live pose, changed placement or attempted a robot command");
        Check(f.LiveJoint == 2.345 && f.Root.Same(CalibrationPlacement.At(new(.1, .75, -.2), 0)), "Re-evaluation changed the live fixture joint or base");

        // Reconfirmation must be possible without starting over or altering the
        // saved tabletop/root. Exercise the same visible controls as the user.
        foreach (string action in new[] { "left.show", "left.confirm", "back.show", "back.confirm", "tool.show", "tool.confirm", "surface", "fit" })
            await f.Event(action);
        Check(f.Plugin.calibrationWizard!.Setup.Stage(f.Plugin.calibrationWizard.Setup.BackProof) == SheetSetupStage.Ready &&
            f.Plugin.calibrationWizard.Setup.SurfaceConfirmed &&
            f.Plugin.calibrationFit!.CanApply && f.Patches![f.WidgetId(".sheet.apply")]!["enabled"]!.GetValue<bool>(),
            "Re-evaluated saved captures became trapped behind confirmations that could not be completed");
        Check(f.Plugin.calibrationDesk.SurfaceY == .75 && f.Plugin.calibrationSamples.Count == 5 &&
            f.Calls.All(c => c is "robot.read" or "robot.evaluate" or "robot.highlightReference" or "robot.highlightToolSite"),
            "Reconfirming the corrected model changed the fixed tabletop, recaptured poses or wrote placement");

        // Simulate report commit followed by an interrupted wizard-draft write.
        var stale = draft with { ModelRevision = "old-model", Setup = new("old-root", "old-proof", "old-proof", JawTipsConfirmed: true) };
        f.Plugin.calibration.Wizard.Edit(_ => stale);
        f.Plugin.ReconcileReevaluatedWizard();
        draft = f.Plugin.calibrationWizard!;
        Check(draft.ModelRevision == "new-model" && draft.Setup == new SheetSetup(),
            "A stale wizard draft restored the old model and approvals after the recomputed report committed");
    }

    private sealed class Fixture : IDisposable
    {
        public PipersPlugin Plugin { get; } = new();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "piper-reevaluation-" + Guid.NewGuid().ToString("N"));
        public CalibrationIdentity Identity { get; } = new("piper-1", "can-a", 1, CalibrationCapture.Fingerprint("session"), "scene-a", "robot-a", "asset-a", "old-model");
        public CalibrationPlacement Root { get; } = CalibrationPlacement.At(new(.1, .75, -.2), 0);
        public double LiveJoint { get; } = 2.345;
        public CalibrationSample[] Originals { get; }
        public string ReportPath { get; }
        public JsonObject State { get; }
        public List<string> Calls { get; } = [];
        public string Failure { get; init; } = "";
        public JsonObject? View { get; private set; }
        public JsonObject? Patches { get; private set; }
        private int evaluations, reads;
        private readonly string liveScene, liveRevision, liveEntity;

        public Fixture(string sceneId = "scene-a", string revision = "new-model", string entityId = "robot-a")
        {
            liveScene = sceneId; liveRevision = revision; liveEntity = entityId;
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "calibration", "piper-1"));
            ReportPath = Path.Combine(Directory, "calibration", "piper-1", "fixture-calibration.json");
            var desk = new CalibrationDesk(PrecisionMm: 5, ToolPoint: "calibration_jaw_tips");
            (double Left, double Back)[] marks = [(697, 200), (600, 450), (900, 120), (800, 440), (903, 350)];
            Originals = marks.Select((p, i) => new CalibrationSample(i == 4 ? "Check" : "P" + (i + 1), desk.World(p.Left, p.Back, 0),
                desk.World(p.Left, p.Back, 10), [i + 10, i + 20, -i - 30, i + 40, i + 50, -i - 60], 123000 + i, i == 4,
                new(LeftMm: p.Left, BackMm: p.Back), "capture-fingerprint-" + i)).ToArray();
            var originalFit = new CalibrationFit(true, "fixture", Root, 0, 0, 0, 0, 0, []);
            var report = new CalibrationReport(1, Identity, desk, Root, Originals, originalFit, null, false, DateTimeOffset.UtcNow);
            File.WriteAllText(ReportPath, JsonSerializer.Serialize(report, PluginProtocol.Json));
            var device = new JsonObject { ["id"] = "can-a", ["generation"] = 1L, ["fresh"] = true,
                ["status"] = new JsonObject { ["age_s"] = 0.0, ["arm_status"] = 0, ["error"] = 0, ["stationary_s"] = 1.0 },
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject { ["actual_deg"] = 0.0,
                    ["fresh"] = true, ["age_s"] = 0.0, ["fault"] = 0, ["enabled"] = false }).ToArray()) };
            State = new() { ["token"] = "session", ["selected"] = "can-a", ["devices"] = new JsonArray(device), ["motion"] = new JsonObject { ["active"] = false } };
            var context = Fixtures.Context(new PluginStart("fixture", Directory, Directory), (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!.AsObject();
                    if (json["type"]?.GetValue<string>() == "view") View = json["root"]!.AsObject();
                    if (json["type"]?.GetValue<string>() == "patch") Patches = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }), (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((method, arguments, _) =>
                {
                    Calls.Add(method); Fresh();
                    var response = method switch
                    {
                        "robot.read" => Read(), "robot.evaluate" => Evaluate(arguments),
                        "robot.highlightReference" => new JsonObject { ["highlighted"] = true, ["label"] = arguments["label"]?.DeepClone(),
                            ["point"] = arguments["point"]?.DeepClone(), ["rootPlacement"] = Root.Json(),
                            ["framed"] = arguments["frame"]?.DeepClone() ?? JsonValue.Create(false), ["markerId"] = arguments["markerId"]?.DeepClone() },
                        "robot.highlightToolSite" => new JsonObject { ["highlighted"] = true },
                        _ => throw new Exception("Unexpected mutation: " + method)
                    };
                    return Task.FromResult(JsonSerializer.SerializeToElement(response));
                }));
            var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a", RobotId: "robot-a")], ActiveRobotId: "piper-1"));
            Plugin.engine = context; Plugin.preferences = prefs; Plugin.telemetry.PublishState(State); Plugin.telemetry.AdoptBackend("session");
            Plugin.link = new RobotLink(liveScene, liveEntity, Pose()["joints"]!.AsArray(), device);
            Plugin.linkBackendToken = "session"; Plugin.workspace.Commit(WorkspaceNavigation.Calibration);
            Plugin.calibration.Desk.Reinstate(DeskSession.Unloaded with { Profile = "piper-1", Identity = Identity, Original = Root, Fit = originalFit,
                Desk = desk, Path = ReportPath, Samples = [.. Originals] });
            var wizard = CalibrationWizardFlow.New("piper-1") with { ModelAssetId = "asset-a", ModelRevision = "old-model",
                Setup = new(Root.Json().ToJsonString(), "old-left", "old-back", JawTipsConfirmed: true, SurfaceConfirmed: true) };
            foreach (var (key, text) in new[] { ("tool", "calibration_jaw_tips"), ("width", "1400"), ("depth", "700"), ("precision", "5"),
                ("sceney", "750") }) wizard.Answers[key] = text;
            Plugin.calibration.Wizard.Open("piper-1", wizard); Fresh();
        }

        public JsonObject Pose() => new()
        {
            ["sceneId"] = liveScene, ["entityId"] = liveEntity, ["modelAssetId"] = "asset-a", ["modelRevision"] = liveRevision,
            ["metricPlacementSupported"] = true, ["kinematicEvaluationSupported"] = true, ["metricFrame"] = "engine-world-y-up", ["rootPlacement"] = Root.Json(),
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad", ["position"] = LiveJoint }).ToArray())
        };
        private JsonObject Read()
        {
            reads++;
            var pose = Pose();
            if (Failure == "read-changed" && reads > 1) pose["modelRevision"] = "third-model";
            if (Failure == "unsupported") pose["kinematicEvaluationSupported"] = false;
            if (Failure == "unsupported-reason") { pose["kinematicEvaluationSupported"] = false; pose["kinematicEvaluationReason"] = "fixture capability reason: private FK scratch data is unavailable."; }
            if (Failure == "metric-reason") { pose["metricPlacementSupported"] = false; pose["metricPlacementReason"] = "fixture capability reason: the root is tilted."; }
            if (Failure == "initial-root") pose["rootPlacement"] = CalibrationPlacement.At(new(.2, .75, -.2), 0).Json();
            if (Failure == "asset") pose["modelAssetId"] = "other-asset";
            return pose;
        }
        private JsonObject Evaluate(JsonObject args)
        {
            int sample = evaluations++;
            if (args["sceneId"]?.GetValue<string>() != liveScene || args["expectedModelRevision"]?.GetValue<string>() != liveRevision || args["toolSite"]?.GetValue<string>() != "calibration_jaw_tips" ||
                !Root.Same(CalibrationPlacement.Read(args["expectedRootPlacement"]))) throw new Exception("Unpinned evaluation request");
            var result = Pose(); result["evaluationOnly"] = true;
            result["joints"] = new JsonArray(args["joints"]!.AsObject().Select(j => (JsonNode)new JsonObject { ["name"] = j.Key, ["unit"] = "rad", ["position"] = j.Value!.DeepClone() }).ToArray());
            var site = new JsonObject { ["name"] = "calibration_jaw_tips", ["unit"] = "m", ["position"] = Originals[sample].MeasuredWorld.Json(),
                ["requiredJoints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)JsonValue.Create("joint" + i)).ToArray()) };
            result["toolSites"] = new JsonArray(site);
            if (sample == 1)
            {
                if (Failure == "revision") result["modelRevision"] = "other";
                if (Failure == "root") result["rootPlacement"] = CalibrationPlacement.At(new(.2, .75, -.2), 0).Json();
                if (Failure == "joint") result["joints"]![0]!["position"] = 99;
                if (Failure == "dependency") site["requiredJoints"]!.AsArray().Add("joint7");
                if (Failure == "not-read-only") result["evaluationOnly"] = false;
                if (Failure == "connection") Plugin.telemetry.AdoptBackend("changed");
                if (Failure == "scene-switch") Plugin.link = new RobotLink("third-scene", liveEntity, Pose()["joints"]!.AsArray(), State["devices"]![0]!.AsObject());
            }
            return result;
        }
        public Task Reevaluate() => Plugin.ReevaluateCalibrationModel(CancellationToken.None);
        public Task Event(string action)
        {
            Fresh();
            return Plugin.HandleEventAsync(new("luckyrobots.pipers.controls", WidgetId(".sheet." + action), "click", JsonSerializer.SerializeToElement<object?>(null)), CancellationToken.None);
        }
        private void Fresh() => Plugin.telemetry.Publish(new(State, "session", 1, Stopwatch.GetTimestamp(), 0));
        public IEnumerable<JsonObject> Widgets(JsonObject? root = null)
        {
            var node = root ?? View!; yield return node;
            foreach (var child in node["children"]?.AsArray().OfType<JsonObject>() ?? []) foreach (var nested in Widgets(child)) yield return nested;
        }
        public string WidgetId(string suffix) => Widgets().Single(w => w["id"]!.GetValue<string>().EndsWith(suffix, StringComparison.Ordinal))["id"]!.GetValue<string>();
        public void Dispose() { Plugin.channel.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
