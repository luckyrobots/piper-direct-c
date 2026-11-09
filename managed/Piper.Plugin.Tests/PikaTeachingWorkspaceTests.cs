using LuckyRobots.Piper.Plugin;

internal static class PikaTeachingWorkspaceTests
{
    public static void Run()
    {
        var plugin = new PipersPlugin();
        bool Visible() => plugin.PikaTeachingControlsVisible;
        void RejectSwap(Action<RobotProfile> swap, RobotProfile profile) =>
            Reject(() => swap(profile), "A pending teaching operation allowed profile state to be swapped: " + swap.Method.Name);
        var a = new RobotProfile("a", "First", "CAN-A", PikaDevice: "/dev/pika/by-host/11-4");
        var b = new RobotProfile("b", "Second", "CAN-B", PikaDevice: "/dev/pika/by-host/15-4");
        var identity = PikaTeachingIdentity.Create(a.Id, a.CanDeviceId, 7, a.PikaDevice, a.PikaDistribution, "test-backend-token");
        var report = new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch).Snapshot(DateTimeOffset.UnixEpoch);
        try
        {
            plugin.preferences = RobotProfileRouting.Normalize(new(Robots: [a, b], ActiveRobotId: a.Id));
            Check(!Visible(), "A fresh profile displayed unrelated teaching work.");
            plugin.pikaTeaching = plugin.pikaTeaching with { DisableRequest = "free-A" };
            plugin.pikaTeaching = plugin.pikaTeaching with { Status = "A remains deliberately free" };
            Check(Visible(), "A completed arm-teach session hid its free-jaw recovery controls.");
            plugin.pikaTeaching = plugin.pikaTeaching with { Unsaved = report };
            plugin.pikaTeaching = plugin.pikaTeaching with { Path = "A/prior-report.json" };
            plugin.pikaTeaching = plugin.pikaTeaching with { ConfirmationIdentity = "pending-A-confirmation" };
            plugin.StorePikaTeachingWorkspace(a);
            plugin.RestorePikaTeachingWorkspace(b);
            Check(plugin.pikaTeaching.DisableRequest == "" && plugin.pikaTeaching.Path == "" &&
                plugin.pikaTeaching.Unsaved is null && !Visible(),
                "Second-profile enable inherited the first gripper's free-jaw request, save or display state.");
            Check(plugin.pikaTeaching.ConfirmationIdentity is null,
                "A pending motor-release gesture survived a profile switch.");

            plugin.pikaTeaching = plugin.pikaTeaching with { DisableRequest = "free-B" };
            plugin.pikaTeaching = plugin.pikaTeaching with { Status = "B remains deliberately free" };
            plugin.StorePikaTeachingWorkspace(b);
            plugin.RestorePikaTeachingWorkspace(a with { Name = "First renamed" });
            Check(plugin.pikaTeaching.DisableRequest == "free-A" && plugin.pikaTeaching.Path == "A/prior-report.json" &&
                plugin.pikaTeaching.Status == "A remains deliberately free" && Visible(),
                "Returning to the exact first assignment lost its resume provenance or saved status.");
            Check(plugin.pikaTeaching.Unsaved!.Identity == identity &&
                plugin.pikaTeaching.Unsaved!.DisabledMotorRequestId == "free-A",
                "An unsaved report was relabelled as another arm/Pika/backend session.");
            plugin.StorePikaTeachingWorkspace(a);
            foreach (var reassigned in new[] { a with { CanDeviceId = "CAN-other" }, a with { PikaDevice = "/dev/pika/by-host/20-1" }, a with { Id = "new-profile" } })
            {
                plugin.RestorePikaTeachingWorkspace(reassigned);
                Check(plugin.pikaTeaching.DisableRequest == "" && plugin.pikaTeaching.Unsaved is null,
                    "A changed hardware/profile assignment inherited old motor-release authority.");
            }
            if (OperatingSystem.IsWindows())
            {
                plugin.RestorePikaTeachingWorkspace(a with { WslDistribution = "Debian" });
                Check(plugin.pikaTeaching.DisableRequest == "", "Another WSL distribution inherited a release nonce.");
            }
            plugin.RestorePikaTeachingWorkspace(b);
            Check(plugin.pikaTeaching.DisableRequest == "free-B" && plugin.pikaTeaching.Status == "B remains deliberately free",
                "Restoring first-profile state overwrote the independent second-profile state.");

            // Actual profile switching awaits this task before stashing any
            // state. The helpers reject a premature swap, so a delayed save
            // cannot write A's completion into B's fields.
            var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            plugin.pikaTeaching = plugin.pikaTeaching with { SaveWorker = save.Task };
            Check(Visible(), "An outstanding teaching save became invisible.");
            RejectSwap(plugin.StorePikaTeachingWorkspace, b);
            RejectSwap(plugin.RestorePikaTeachingWorkspace, a);
            Check(plugin.pikaTeaching.DisableRequest == "free-B", "Rejected pending-save switch mutated current provenance.");
            save.SetResult();
            plugin.StorePikaTeachingWorkspace(b);
            plugin.RestorePikaTeachingWorkspace(a);
            Check(plugin.pikaTeaching.SaveWorker is null && plugin.pikaTeaching.DisableRequest == "free-A",
                "An old completed save worker was attached to the restored profile.");

            plugin.pikaTeaching = plugin.pikaTeaching with { Transition = true };
            RejectSwap(plugin.StorePikaTeachingWorkspace, a); RejectSwap(plugin.RestorePikaTeachingWorkspace, b);
            plugin.pikaTeaching = plugin.pikaTeaching with { Transition = false };
            plugin.pikaTeaching = plugin.pikaTeaching with { Recorder = new PikaTeachingRecorder(identity, "free-A", null, 1000, DateTimeOffset.UnixEpoch) };
            RejectSwap(plugin.StorePikaTeachingWorkspace, a); RejectSwap(plugin.RestorePikaTeachingWorkspace, b);
            plugin.pikaTeaching = plugin.pikaTeaching with { Recorder = null };
            Check(plugin.pikaTeaching.Unsaved!.Identity == identity,
                "Transition/active-recorder rejection erased the unsaved first-profile artifact.");
        }
        finally { plugin.channel.Dispose(); }
    }
}
