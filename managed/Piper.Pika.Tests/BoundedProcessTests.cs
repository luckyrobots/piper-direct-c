using System.Diagnostics;
using System.Globalization;
using LuckyRobots.Piper.Pika;

// BoundedProcess runs usbipd, wslpath and chmod. A helper that hangs past its
// deadline must end as a timeout with its whole process tree gone.
internal static class BoundedProcessTests
{
    internal const string TreeFixture = "--bounded-tree-fixture", TreeChild = "--bounded-tree-child";

    internal static async Task Run()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "pika-bounded-tree-" + Guid.NewGuid().ToString("N"));
        try
        {
            var elapsed = Stopwatch.StartNew();
            bool timedOut = false;
            try
            {
                await BoundedProcess.RunAsync(Environment.ProcessPath!, [TreeFixture, pidFile], "Could not start the fixture.",
                    "Fixture deadline.", default, TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException error) when (error.Message == "Fixture deadline.") { timedOut = true; }
            Check(timedOut && elapsed.Elapsed < TimeSpan.FromSeconds(10), "A hung helper did not end as a bounded timeout");
            Check(File.Exists(pidFile), "The helper fixture did not start its child before the deadline");
            int[] tree = File.ReadAllText(pidFile).Split(' ').Select(id => int.Parse(id, CultureInfo.InvariantCulture)).ToArray();
            // The parent has exited when RunAsync returns; its child dies with the tree kill.
            for (int i = 0; i < 100 && !tree.All(Ended); i++) await Task.Delay(20);
            Check(tree.Length == 2 && tree.All(Ended), "A timed-out helper left part of its process tree running");
        }
        finally { File.Delete(pidFile); }
    }

    // Fixture: a parent that starts a child of its own and never exits.
    internal static void RunTreeFixture(string pidFile)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add(TreeChild);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the fixture child.");
        File.WriteAllText(pidFile + ".tmp", $"{Environment.ProcessId} {child.Id}");
        File.Move(pidFile + ".tmp", pidFile);
        Thread.Sleep(Timeout.Infinite);
    }

    private static bool Ended(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
