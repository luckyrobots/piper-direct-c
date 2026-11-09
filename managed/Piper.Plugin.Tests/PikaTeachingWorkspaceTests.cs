using System.Reflection;
using LuckyRobots.Piper.Plugin;

internal static class PikaTeachingWorkspaceTests
{
    public static void Run(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        T Get<T>(string name) => (T)typeof(PipersPlugin).GetField(name, flags)!.GetValue(plugin)!;
        void Invoke(string name, RobotProfile profile) => typeof(PipersPlugin).GetMethod(name, flags)!.Invoke(plugin, [profile]);
        bool Visible() => (bool)typeof(PipersPlugin).GetProperty("PikaTeachingControlsVisible", flags)!.GetValue(plugin)!;
        var a = new RobotProfile("a", "First", "CAN-A", PikaDevice: "/dev/pika/by-host/11-4");
        var b = new RobotProfile("b", "Second", "CAN-B", PikaDevice: "/dev/pika/by-host/15-4");
        var identity = PikaTeachingIdentity.Create(a.Id, a.CanDeviceId, 7, a.PikaDevice, a.PikaDistribution, "test-backend-token");
        var report = new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch).Snapshot(DateTimeOffset.UnixEpoch);
        try
        {
            Set("preferences", RobotProfileRouting.Normalize(new(Robots: [a, b], ActiveRobotId: a.Id)));
            check(!Visible(), "A fresh profile displayed unrelated teaching work.");
            Set("pikaTeachingDisableRequest", "free-A");
            Set("pikaTeachingStatus", "A remains deliberately free");
            check(Visible(), "A completed arm-teach session hid its free-jaw recovery controls.");
            Set("pikaTeachingUnsaved", report);
            Set("pikaTeachingPath", "A/prior-report.json");
            Set("pikaTeachingConfirmation", true);
            Set("pikaTeachingConfirmationIdentity", "pending-A-confirmation");
            Invoke("StorePikaTeachingWorkspace", a);
            Invoke("RestorePikaTeachingWorkspace", b);
            check(Get<string>("pikaTeachingDisableRequest") == "" && Get<string>("pikaTeachingPath") == "" &&
                Get<PikaTeachingReport?>("pikaTeachingUnsaved") is null && !Visible(),
                "Second-profile enable inherited the first gripper's free-jaw request, save or display state.");
            check(!Get<bool>("pikaTeachingConfirmation") && Get<string>("pikaTeachingConfirmationIdentity") == "",
                "A pending motor-release gesture survived a profile switch.");

            Set("pikaTeachingDisableRequest", "free-B");
            Set("pikaTeachingStatus", "B remains deliberately free");
            Invoke("StorePikaTeachingWorkspace", b);
            Invoke("RestorePikaTeachingWorkspace", a with { Name = "First renamed" });
            check(Get<string>("pikaTeachingDisableRequest") == "free-A" && Get<string>("pikaTeachingPath") == "A/prior-report.json" &&
                Get<string>("pikaTeachingStatus") == "A remains deliberately free" && Visible(),
                "Returning to the exact first assignment lost its resume provenance or saved status.");
            check(Get<PikaTeachingReport>("pikaTeachingUnsaved").Identity == identity &&
                Get<PikaTeachingReport>("pikaTeachingUnsaved").DisabledMotorRequestId == "free-A",
                "An unsaved report was relabelled as another arm/Pika/backend session.");
            Invoke("StorePikaTeachingWorkspace", a);
            foreach (var reassigned in new[] { a with { CanDeviceId = "CAN-other" }, a with { PikaDevice = "/dev/pika/by-host/20-1" }, a with { Id = "new-profile" } })
            {
                Invoke("RestorePikaTeachingWorkspace", reassigned);
                check(Get<string>("pikaTeachingDisableRequest") == "" && Get<PikaTeachingReport?>("pikaTeachingUnsaved") is null,
                    "A changed hardware/profile assignment inherited old motor-release authority.");
            }
            if (OperatingSystem.IsWindows())
            {
                Invoke("RestorePikaTeachingWorkspace", a with { WslDistribution = "Debian" });
                check(Get<string>("pikaTeachingDisableRequest") == "", "Another WSL distribution inherited a release nonce.");
            }
            Invoke("RestorePikaTeachingWorkspace", b);
            check(Get<string>("pikaTeachingDisableRequest") == "free-B" && Get<string>("pikaTeachingStatus") == "B remains deliberately free",
                "Restoring first-profile state overwrote the independent second-profile state.");

            // Actual profile switching awaits this task before stashing any
            // state. The helpers reject a premature swap, so a delayed save
            // cannot write A's completion into B's fields.
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set("pikaTeachingSaveWorker", save.Task);
            check(Visible(), "An outstanding teaching save became invisible.");
            Reject("StorePikaTeachingWorkspace", b);
            Reject("RestorePikaTeachingWorkspace", a);
            check(Get<string>("pikaTeachingDisableRequest") == "free-B", "Rejected pending-save switch mutated current provenance.");
            save.SetResult();
            Invoke("StorePikaTeachingWorkspace", b);
            Invoke("RestorePikaTeachingWorkspace", a);
            check(Get<Task?>("pikaTeachingSaveWorker") is null && Get<string>("pikaTeachingDisableRequest") == "free-A",
                "An old completed save worker was attached to the restored profile.");

            Set("pikaTeachingTransition", true);
            Reject("StorePikaTeachingWorkspace", a); Reject("RestorePikaTeachingWorkspace", b);
            Set("pikaTeachingTransition", false);
            Set("pikaTeachingRecorder", new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch));
            Reject("StorePikaTeachingWorkspace", a); Reject("RestorePikaTeachingWorkspace", b);
            Set("pikaTeachingRecorder", null);
            check(Get<PikaTeachingReport>("pikaTeachingUnsaved").Identity == identity,
                "Transition/active-recorder rejection erased the unsaved first-profile artifact.");
        }
        finally { ((IDisposable)typeof(PipersPlugin).GetField("channel", flags)!.GetValue(plugin)!).Dispose(); }

        void Reject(string method, RobotProfile profile)
        {
            bool rejected = false;
            try { Invoke(method, profile); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
            check(rejected, "A pending teaching operation allowed profile state to be swapped: " + method);
        }
    }
}
