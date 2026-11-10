using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Pika;
using LuckyRobots.Piper.Plugin;

internal static class MeasuredGhostPublicationTests
{
    public static async Task Run()
    {
        using (var fixture = new Fixture())
        {
            // Plugin-H: a capability probed under an earlier backend is Unknown after a restart.
            fixture.Plugin.telemetry.Ghosts.Record(new("previous-backend", Ghosts: false, Visibility: false));
            await fixture.Publish();
            Check(fixture.HostProbes == 1 && fixture.Plugin.telemetry.Ghosts.Kind("backend") == GhostCapabilityKind.Supported && fixture.Ghosts.Count == 2,
                "A backend restart kept the previous backend's ghost capability instead of re-probing it");
            await fixture.Publish();
            Check(fixture.HostProbes == 1 && fixture.Ghosts.Count == 4, "Ghost capability was re-probed for an unchanged backend");
        }
        using (var fixture = new Fixture())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.AfterRead = async count =>
            {
                if (count != 1) return;
                entered.TrySetResult();
                await released.Task;
            };
            var publishing = fixture.Publish();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            fixture.Fresh(first: 11, second: 22);
            released.TrySetResult();
            await publishing;
            Check(fixture.Ghosts.Select(request => request["ghostId"]!.GetValue<string>()).SequenceEqual(new[] { "a", "b" }),
                "Independent profiles sharing one physics root did not both publish measured ghosts");
            Check(Math.Abs(fixture.Ghosts[0]["joints"]!["joint1"]!.GetValue<double>() - 11 * Math.PI / 180) < 1e-10 &&
                Math.Abs(fixture.Ghosts[1]["joints"]!["piper3_joint1"]!.GetValue<double>() - 22 * Math.PI / 180) < 1e-10,
                "Runtime discovery reused an older physical pose instead of the newest same-identity evidence");
            Check(fixture.Ghosts.All(request => request["joints"]!.AsObject().Count == 8) &&
                fixture.Ghosts[0]["joints"]!.AsObject().All(joint => !joint.Key.StartsWith("piper3_", StringComparison.Ordinal)) &&
                fixture.Ghosts[1]["joints"]!.AsObject().All(joint => joint.Key.StartsWith("piper3_", StringComparison.Ordinal)),
                "A measured ghost borrowed the other arm's joints from the shared leader");
            await fixture.Publish();
            Check(fixture.Ghosts.Skip(2).Select(request => request["ghostId"]!.GetValue<string>()).SequenceEqual(new[] { "b", "a" }),
                "Repeated ghost publication permanently prioritizes one profile");
        }
        using (var fixture = new Fixture())
        {
            fixture.AfterRead = count => count == 1 ? Task.FromException(new TimeoutException("Slow first profile")) : Task.CompletedTask;
            await fixture.Publish();
            Check(fixture.Ghosts.Count == 1 && fixture.Ghosts[0]["ghostId"]!.GetValue<string>() == "b",
                "One unavailable robot suppressed another robot's healthy measured ghost");
        }
        foreach (string changed in new[] { "stale-pika", "generation", "backend", "profile", "hidden", "disposing" })
        {
            using var fixture = new Fixture();
            fixture.AfterRead = count =>
            {
                if (count != 1) return Task.CompletedTask;
                if (changed == "stale-pika") fixture.Fresh(firstPikaAge: .4);
                if (changed == "generation") fixture.Fresh(firstGeneration: 2);
                if (changed == "backend") fixture.Fresh(backend: "replacement");
                if (changed == "profile") fixture.SetPreferences(fixture.Preferences with {
                    Robots = fixture.Preferences.Robots!.Select(profile => profile.Id == "a" ? profile with { PikaDevice = "/dev/changed" } : profile).ToArray() });
                if (changed == "hidden") fixture.SetPreferences(fixture.Preferences with { ShowMeasuredGhosts = false });
                if (changed == "disposing") fixture.Plugin.passiveMeasuredTrackingStarted = false;
                return Task.CompletedTask;
            };
            await fixture.Publish();
            Check(fixture.Ghosts.All(request => request["ghostId"]!.GetValue<string>() != "a"),
                "Delayed runtime lookup published a ghost after changed " + changed + " evidence");
            if (changed is "stale-pika" or "generation" or "profile")
                Check(fixture.Ghosts.Count == 1 && fixture.Ghosts[0]["ghostId"]!.GetValue<string>() == "b",
                    "One robot's invalid " + changed + " evidence prevented the other measured ghost");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PipersPlugin plugin = new();
        public PipersPlugin Plugin => plugin;
        private readonly string folder = Path.Combine(Path.GetTempPath(), "piper-ghost-" + Guid.NewGuid().ToString("N"));
        private int reads;
        public Preferences Preferences { get; private set; }
        public List<JsonObject> Ghosts { get; } = [];
        public Func<int, Task>? AfterRead;
        public int HostProbes;

        public Fixture()
        {
            Preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [
                new("a", "First", "can-a", RobotId: "compound", PikaDevice: "/dev/a"),
                new("b", "Second", "can-b", RobotId: "compound", PikaDevice: "/dev/b", RobotJointPrefix: "piper3_")], ActiveRobotId: "a"));
            SetPreferences(Preferences);
            plugin.passiveMeasuredTrackingStarted = true;
            plugin.telemetry.Ghosts.Record(new("backend", Ghosts: true, Visibility: true));
            plugin.telemetry.Ghosts.PublishVisibility(true);
            plugin.telemetry.AdoptBackend("backend");
            var joints = new JsonArray();
            foreach (string prefix in new[] { "", "piper3_" })
            {
                for (int joint = 1; joint <= 6; joint++) joints.Add(new JsonObject { ["name"] = prefix + "joint" + joint, ["unit"] = "rad" });
                joints.Add(new JsonObject { ["name"] = prefix + "joint7", ["unit"] = "m", ["limited"] = true, ["min"] = -.047, ["max"] = 0.0 });
                joints.Add(new JsonObject { ["name"] = prefix + "joint8", ["unit"] = "m", ["limited"] = true, ["min"] = 0.0, ["max"] = .047 });
            }
            plugin.scene = new JsonObject { ["sceneId"] = "source", ["robots"] = new JsonArray(new JsonObject {
                ["entityId"] = "compound", ["modelAssetId"] = "asset", ["modelRevision"] = "revision", ["joints"] = joints }) };
            var context = Fixtures.Context(new PluginStart("fixture", folder, folder), (Func<object, CancellationToken, Task>)((_, _) => Task.CompletedTask),
                    (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)(async (method, arguments, _) =>
                    {
                        if (method == "robot.runtime.read")
                        {
                            if (AfterRead is not null) await AfterRead(++reads);
                            return JsonSerializer.SerializeToElement(new { mode = "play", runId = "run" });
                        }
                        if (method == "host.info")
                        {
                            ++HostProbes;
                            return JsonSerializer.SerializeToElement(new { robotFeedbackGhostSupported = true, robotGhostVisibilitySupported = true });
                        }
                        if (method != "robot.feedback") throw new InvalidOperationException("Unexpected ghost request: " + method);
                        Ghosts.Add(arguments.DeepClone().AsObject());
                        return JsonSerializer.SerializeToElement(new { applied = false, renderMode = "ghost" });
                    }));
            plugin.engine = context;
            Fresh();
        }

        public void SetPreferences(Preferences value) { Preferences = value; plugin.preferences = value; }
        public Task Publish() => plugin.PublishMeasuredGhosts(CancellationToken.None);

        public void Fresh(double first = 1, double second = 2, double firstPikaAge = 0, long firstGeneration = 1, string backend = "backend")
        {
            JsonObject Arm(string id, double angle, long generation) => new() { ["id"] = id, ["generation"] = generation,
                ["fresh"] = true, ["status"] = new JsonObject { ["age_s"] = 0.0 },
                ["joints"] = new JsonArray(Enumerable.Range(1, 6).Select(_ => (JsonNode)new JsonObject {
                    ["actual_deg"] = angle, ["age_s"] = 0.0, ["fresh"] = true }).ToArray()) };
            JsonObject Pika(string device, double age) => new() { ["device"] = device, ["distribution"] = PikaDistribution.ForCurrentPlatform(null), ["connected"] = true,
                ["fresh"] = true, ["age_s"] = age, ["telemetry"] = new JsonObject { ["motor"] = new JsonObject { ["Position"] = 0.0 } } };
            var snapshot = new JsonObject { ["token"] = backend, ["selected"] = "can-a", ["devices"] = new JsonArray(Arm("can-a", first, firstGeneration), Arm("can-b", second, 1)),
                ["pikas"] = new JsonArray(Pika("/dev/a", firstPikaAge), Pika("/dev/b", 0)) };
            var latest = plugin.telemetry;
            latest.Publish(new(snapshot, backend, 1, Stopwatch.GetTimestamp(), 0));
        }

        public void Dispose()
        {
            plugin.channel.Dispose(); plugin.shutdown.Dispose(); plugin.commands.Dispose(); plugin.cameraDiscoveryShutdown.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
