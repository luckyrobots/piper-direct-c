using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class CalibrationSheetUiTests
{
    public static async Task Run()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, hidden)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, hidden)!.GetValue(plugin)!;
        Task Invoke(string name, params object[] args) => (Task)typeof(PipersPlugin).GetMethod(name, hidden)!.Invoke(plugin, args)!;
        var folder = Path.Combine(Path.GetTempPath(), "piper-sheet-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var prefs = RobotProfileRouting.Normalize(new Preferences(Robots: [new("piper-1", "Fixture", "can-a")], ActiveRobotId: "piper-1"));
        var device = new JsonObject
        {
            ["id"] = "can-a", ["generation"] = 1L, ["fresh"] = true,
            ["status"] = new JsonObject { ["age_s"] = 0.0, ["control"] = 2, ["teach_state"] = 1, ["motion_mode"] = 1, ["arm_status"] = 11, ["error"] = 0, ["stationary_s"] = 1.0 },
            ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
            {
                ["actual_deg"] = 0.0, ["fresh"] = true, ["age_s"] = 0.0, ["enabled"] = true, ["fault"] = 0,
                ["limits_fresh"] = true, ["min_deg"] = -180.0, ["max_deg"] = 180.0, ["max_speed_rad_s"] = .3
            }).ToArray())
        };
        var state = new JsonObject { ["token"] = "fixture-session", ["selected"] = "can-a", ["devices"] = new JsonArray(device), ["motion"] = new JsonObject { ["active"] = false } };
        var joints = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject { ["name"] = "joint" + i, ["unit"] = "rad", ["position"] = 0.0 }).ToArray());
        var pose = new JsonObject
        {
            ["sceneId"] = "scene-a", ["entityId"] = "robot-a", ["metricPlacementSupported"] = true, ["metricFrame"] = "engine-world-y-up",
            ["modelAssetId"] = "asset-a", ["modelRevision"] = "revision-a", ["rootPlacement"] = CalibrationPlacement.At(new(0, .75, 0), 0).Json(),
            ["joints"] = joints.DeepClone(), ["toolSites"] = new JsonArray(new JsonObject { ["name"] = "calibration_jaw_tips", ["unit"] = "m", ["position"] = new JsonArray(0, .75, 0) })
        };
        int views = 0;
        JsonObject? lastPatch = null;
        var requests = new List<string>();
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), hidden, null,
            [new PluginStart("fixture", folder, folder),
                (Func<object, CancellationToken, Task>)((message, _) =>
                {
                    var json = JsonSerializer.SerializeToNode(message, PluginProtocol.Json)!;
                    if (json["type"]?.GetValue<string>() == "view") views++;
                    if (json["type"]?.GetValue<string>() == "patch") lastPatch = json["widgets"]!.AsObject();
                    return Task.CompletedTask;
                }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((method, arguments, _) =>
                {
                    requests.Add(method);
                    var result = method == "robot.read" ? pose.DeepClone() : new JsonObject
                    {
                        ["highlighted"] = true, ["label"] = arguments["label"]?.DeepClone(), ["point"] = arguments["point"]?.DeepClone(),
                        ["rootPlacement"] = arguments["expectedRootPlacement"]?.DeepClone(), ["framed"] = arguments["frame"]?.DeepClone() ?? JsonValue.Create(false),
                        ["markerId"] = arguments["markerId"]?.DeepClone()
                    };
                    return Task.FromResult(JsonSerializer.SerializeToElement(result));
                })], null)!;
        Set("preferences", prefs); Set("engine", context); Set("state", state); Set("token", "fixture-session");
        Set("calibrationProfile", "piper-1"); Set("wizardProfile", "piper-1"); plugin.Navigation = WorkspaceNavigation.Calibration;
        Set("link", new RobotLink("scene-a", "robot-a", joints, device));
        Get<LatestTelemetry>("telemetry").Publish(new(state, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
        var draft = CalibrationWizardFlow.New("piper-1", 10) with { Step = CalibrationWizardStep.DeskBack, ModelAssetId = "asset-a", ModelRevision = "revision-a" };
        foreach (var pair in new Dictionary<string, string> { ["tool"] = "calibration_jaw_tips", ["width"] = "1400", ["depth"] = "700", ["precision"] = "5", ["sceney"] = "750" }) draft.Answers[pair.Key] = pair.Value;
        var identity = new CalibrationIdentity("piper-1", "can-a", 1L, CalibrationCapture.Fingerprint("fixture-session"), "scene-a", "robot-a", "asset-a", "revision-a");
        var root = CalibrationPlacement.At(new(0, .75, 0), 0);
        string proof = WizardReferenceHighlightScope.DeskFingerprint(identity, 1400, 700, 750, root);
        draft = draft with { Setup = new(root.Json().ToJsonString(), proof, proof, SurfaceConfirmed: true) };
        foreach (var pair in new Dictionary<string, string> { ["p0back"] = "200", ["p0left"] = "697", ["p1back"] = "450", ["p1left"] = "800", ["p2back"] = "120", ["p2left"] = "900", ["p3back"] = "450", ["p3left"] = "800" }) draft.Answers[pair.Key] = pair.Value;
        Set("calibrationWizard", draft);
        try
        {
            await Invoke("Render", CancellationToken.None);
            long page = Get<long>("workspaceViewEpoch"), sheet = Get<CalibrationWizardDraft>("calibrationWizard").Epoch;
            void Fresh() => Get<LatestTelemetry>("telemetry").Publish(new(state, "fixture-session", 1, Stopwatch.GetTimestamp(), 0));
            async Task Click(string action)
            {
                Fresh();
                string wire = WorkspaceEventScope.Wrap(page, "piper-1", "wizard." + sheet + ".sheet." + action);
                // The same still-visible page IDs must remain useful after a visual action.
                await plugin.HandleEventAsync(new("luckyrobots.pipers.controls", wire, "click", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
            }
            string Wire(string action) => WorkspaceEventScope.Wrap(page, "piper-1", "wizard." + sheet + ".sheet." + action);
            string Fixed(string id) => WorkspaceEventScope.Wrap(page, "piper-1", id);
            async Task Patches() { Fresh(); await Invoke("Update", CancellationToken.None); }
            await Patches();
            Check(lastPatch![Wire("tool.confirm")]?["enabled"]?.GetValue<bool>() == false,
                "Jaw-tip confirmation was available before its marker was acknowledged");
            int beforeViews = views;
            await Click("tool.show");
            Check(Get<long>("workspaceViewEpoch") == page && Get<CalibrationWizardDraft>("calibrationWizard").Epoch == sheet && views == beforeViews,
                "Showing jaw tips rebuilt the calibration page or invalidated the next visible button ID");
            Check(Get<object>("wizardMarker") is MarkerState.Tool, "Showing jaw tips did not retain its confirmation scope");
            await Invoke("MaintainWizardHighlight", CancellationToken.None);
            Check(Get<object>("wizardMarker") is MarkerState.Tool, "Background maintenance replaced the displayed jaw-tip marker");
            await Click("tool.confirm");
            Check(Get<CalibrationWizardDraft>("calibrationWizard").Setup.JawTipsConfirmed,
                "The visible Marker matches jaw tips click was lost after Show jaw-tip marker");
            await Click("tool.show");
            Check(Get<CalibrationWizardDraft>("calibrationWizard").Setup.JawTipsConfirmed,
                "Showing an unchanged jaw-tip site silently revoked the user's existing confirmation");
            await Patches();
            Check(lastPatch![Fixed("sheet.row.status.3")]?["text"]?.GetValue<string>().Contains("overlaps P2") == true,
                "Duplicate P4/P2 measurements did not show a visible row error before capture");
            Get<CalibrationWizardDraft>("calibrationWizard").Answers["precision"] = "0";
            await Patches();
            Check(lastPatch![Fixed("sheet.next")]?["text"]?.GetValue<string>().Contains("accuracy cannot be 0") == true &&
                lastPatch[Wire("capture.0")]?["enabled"]?.GetValue<bool>() == false,
                "Invalid precision zero did not show its visible blocker and disable Capture P1");
            await Click("capture.0");
            Check(Get<CalibrationIdentity?>("calibrationIdentity") is null,
                "Invalid precision zero reached capture despite the visible capture gate");
            // Error feedback must retain the same page, so correcting an input
            // does not require the operator to discover fresh button IDs.
            Check(Get<long>("workspaceViewEpoch") == page, "A rejected capture rebuilt the page and invalidated visible controls");
            Get<CalibrationWizardDraft>("calibrationWizard").Answers["precision"] = "5";
            Set("calibrationWizard", Get<CalibrationWizardDraft>("calibrationWizard") with { Setup = Get<CalibrationWizardDraft>("calibrationWizard").Setup with { LeftProof = "stale-proof" } });
            await Patches();
            Check(lastPatch![Wire("tool.confirm")]?["enabled"]?.GetValue<bool>() == false &&
                lastPatch[Fixed("sheet.setup")]?["text"]?.GetValue<string>().Contains("LEFT pending") == true,
                "Stale desk-edge proof was presented as confirmed or permitted a dependent jaw-tip confirmation");
            Check(requests.All(method => method is "robot.read" or "robot.highlightToolSite" or "robot.highlightReference"),
                "Passive calibration preview sent an engine motion or hardware operation");
        }
        finally
        {
            Get<Grpc.Net.Client.GrpcChannel>("channel").Dispose();
            Directory.Delete(folder, true);
        }
    }
}
