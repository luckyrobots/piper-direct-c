using System.Reflection;
using LuckyRobots.Piper.Plugin;

internal static class PikaTeachingWorkspaceTests
{
    public static void Run(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var plugin = new PipersPlugin();
        void Set(string name, object? value) => typeof(PipersPlugin).GetField(name, flags)!.SetValue(plugin, value);
        void Invoke(string name, RobotProfile profile) => typeof(PipersPlugin).GetMethod(name, flags)!.Invoke(plugin, [profile]);
        bool Visible() => (bool)typeof(PipersPlugin).GetProperty("PikaTeachingControlsVisible", flags)!.GetValue(plugin)!;
        void Teach(string property, object? value)
        {
            var field = typeof(PipersPlugin).GetField("pikaTeaching", flags)!;
            object current = field.GetValue(plugin)!;
            object copy = current.GetType().GetMethod("<Clone>$")!.Invoke(current, null)!;
            copy.GetType().GetProperty(property)!.SetValue(copy, value);
            field.SetValue(plugin, copy);
        }
        T Taught<T>(string property)
        {
            object current = typeof(PipersPlugin).GetField("pikaTeaching", flags)!.GetValue(plugin)!;
            return (T)current.GetType().GetProperty(property)!.GetValue(current)!;
        }
        var a = new RobotProfile("a", "First", "CAN-A", PikaDevice: "/dev/pika/by-host/11-4");
        var b = new RobotProfile("b", "Second", "CAN-B", PikaDevice: "/dev/pika/by-host/15-4");
        var identity = PikaTeachingIdentity.Create(a.Id, a.CanDeviceId, 7, a.PikaDevice, a.PikaDistribution, "test-backend-token");
        var report = new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch).Snapshot(DateTimeOffset.UnixEpoch);
        try
        {
            Set("preferences", RobotProfileRouting.Normalize(new(Robots: [a, b], ActiveRobotId: a.Id)));
            check(!Visible(), "A fresh profile displayed unrelated teaching work.");
            Teach("DisableRequest", "free-A");
            Teach("Status", "A remains deliberately free");
            check(Visible(), "A completed arm-teach session hid its free-jaw recovery controls.");
            Teach("Unsaved", report);
            Teach("Path", "A/prior-report.json");
            Teach("ConfirmationIdentity", "pending-A-confirmation");
            Invoke("StorePikaTeachingWorkspace", a);
            Invoke("RestorePikaTeachingWorkspace", b);
            check(Taught<string>("DisableRequest") == "" && Taught<string>("Path") == "" &&
                Taught<PikaTeachingReport?>("Unsaved") is null && !Visible(),
                "Second-profile enable inherited the first gripper's free-jaw request, save or display state.");
            check(Taught<string?>("ConfirmationIdentity") is null,
                "A pending motor-release gesture survived a profile switch.");

            Teach("DisableRequest", "free-B");
            Teach("Status", "B remains deliberately free");
            Invoke("StorePikaTeachingWorkspace", b);
            Invoke("RestorePikaTeachingWorkspace", a with { Name = "First renamed" });
            check(Taught<string>("DisableRequest") == "free-A" && Taught<string>("Path") == "A/prior-report.json" &&
                Taught<string>("Status") == "A remains deliberately free" && Visible(),
                "Returning to the exact first assignment lost its resume provenance or saved status.");
            check(Taught<PikaTeachingReport>("Unsaved").Identity == identity &&
                Taught<PikaTeachingReport>("Unsaved").DisabledMotorRequestId == "free-A",
                "An unsaved report was relabelled as another arm/Pika/backend session.");
            Invoke("StorePikaTeachingWorkspace", a);
            foreach (var reassigned in new[] { a with { CanDeviceId = "CAN-other" }, a with { PikaDevice = "/dev/pika/by-host/20-1" }, a with { Id = "new-profile" } })
            {
                Invoke("RestorePikaTeachingWorkspace", reassigned);
                check(Taught<string>("DisableRequest") == "" && Taught<PikaTeachingReport?>("Unsaved") is null,
                    "A changed hardware/profile assignment inherited old motor-release authority.");
            }
            if (OperatingSystem.IsWindows())
            {
                Invoke("RestorePikaTeachingWorkspace", a with { WslDistribution = "Debian" });
                check(Taught<string>("DisableRequest") == "", "Another WSL distribution inherited a release nonce.");
            }
            Invoke("RestorePikaTeachingWorkspace", b);
            check(Taught<string>("DisableRequest") == "free-B" && Taught<string>("Status") == "B remains deliberately free",
                "Restoring first-profile state overwrote the independent second-profile state.");

            // Actual profile switching awaits this task before stashing any
            // state. The helpers reject a premature swap, so a delayed save
            // cannot write A's completion into B's fields.
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Teach("SaveWorker", save.Task);
            check(Visible(), "An outstanding teaching save became invisible.");
            Reject("StorePikaTeachingWorkspace", b);
            Reject("RestorePikaTeachingWorkspace", a);
            check(Taught<string>("DisableRequest") == "free-B", "Rejected pending-save switch mutated current provenance.");
            save.SetResult();
            Invoke("StorePikaTeachingWorkspace", b);
            Invoke("RestorePikaTeachingWorkspace", a);
            check(Taught<Task?>("SaveWorker") is null && Taught<string>("DisableRequest") == "free-A",
                "An old completed save worker was attached to the restored profile.");

            Teach("Transition", true);
            Reject("StorePikaTeachingWorkspace", a); Reject("RestorePikaTeachingWorkspace", b);
            Teach("Transition", false);
            Teach("Recorder", new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch));
            Reject("StorePikaTeachingWorkspace", a); Reject("RestorePikaTeachingWorkspace", b);
            Teach("Recorder", null);
            check(Taught<PikaTeachingReport>("Unsaved").Identity == identity,
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
