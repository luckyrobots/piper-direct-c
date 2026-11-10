using LuckyRobots.Piper.Plugin;

internal static class CameraPreviewSlotsTests
{
    public static async Task Run()
    {
        // A paused preview never launches the camera runtime, so the lifecycle
        // guard can be exercised without a camera or engine connection.
        static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var paused = new CameraCapture(null!, "bench", new CameraPreferences("none", Enabled: false), (_, _) => { });
        paused.Start();
        Check(Throws(paused.Start), "A camera capture started a second worker");
        await paused.DisposeAsync();
        await paused.DisposeAsync();
        Check(Throws(paused.Start), "A disposed camera capture started a worker");
        var neverStarted = new CameraCapture(null!, "bench", new CameraPreferences("none", Enabled: false), (_, _) => { });
        await neverStarted.DisposeAsync();
        Check(Throws(neverStarted.Start), "A camera capture disposed before start could still start");
        var probe = new ThrottledProbe<string>(TimeSpan.FromHours(1));
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        probe.Queue(() => { reads++; return gate.Task; });
        probe.Queue(() => { reads++; return Task.FromResult<string?>("second"); }, force: true);
        Check(reads == 1 && !probe.HasPending, "A camera discovery probe ran two reads at once");
        gate.SetResult("first");
        await probe.Reading!;
        Check(probe.Take() == "first" && probe.Take() is null, "A probe result was lost or applied twice");
        probe.Queue(() => { reads++; return Task.FromResult<string?>("throttled"); });
        Check(reads == 1, "A camera discovery probe ignored its interval");
        probe.Queue(() => { reads++; return Task.FromResult<string?>(null); }, force: true);
        await probe.Retire();
        Check(reads == 2 && !probe.HasPending, "A forced refresh was throttled or a failed read left a result");
        var slots = new CameraPreviewSlots<ControlledCapture>();
        var previous = new ControlledCapture(paused: true);
        var survivor = new ControlledCapture();
        await slots.ReplaceAsync("bench", () => previous);
        await slots.ReplaceAsync("depth", () => survivor);
        Check(ReferenceEquals(slots.Get("bench"), previous), "Camera slot did not expose its active capture");

        int replacementStarts = 0;
        ControlledCapture StartReplacement()
        {
            Interlocked.Increment(ref replacementStarts);
            return new();
        }

        Task replacing = slots.ReplaceAsync("bench", StartReplacement);
        await previous.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        slots.BeginShutdown();
        Task retiring = slots.DisposeAsync().AsTask();
        Task lateRestart = slots.ReplaceAsync("gripper", StartReplacement);
        try
        {
            Check(!retiring.IsCompleted && survivor.Disposals == 0, "Camera shutdown took a capture snapshot before joining the admitted restart");
            Check(slots.Get("bench") is null && replacementStarts == 0, "A retiring capture or premature replacement was still visible");
        }
        finally { previous.Release.TrySetResult(); }

        await Task.WhenAll(replacing, retiring, lateRestart).WaitAsync(TimeSpan.FromSeconds(5));
        await slots.DisposeAsync();
        Check(replacementStarts == 0, "A camera replacement started after shutdown closed admission");
        Check(previous.Disposals == 1 && survivor.Disposals == 1, "Camera shutdown missed or repeated capture disposal");
        Check(slots.Get("bench") is null && slots.Get("depth") is null && slots.Get("gripper") is null,
            "A camera slot remained visible after shutdown");
    }

    private sealed class ControlledCapture(bool paused = false) : IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult();
            if (paused) await Release.Task;
        }
    }
}
