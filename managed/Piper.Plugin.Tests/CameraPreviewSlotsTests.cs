using LuckyRobots.Piper.Plugin;

internal static class CameraPreviewSlotsTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var slots = new CameraPreviewSlots<ControlledCapture>();
        var previous = new ControlledCapture(paused: true);
        var survivor = new ControlledCapture();
        await slots.ReplaceAsync("bench", () => previous);
        await slots.ReplaceAsync("depth", () => survivor);
        check(ReferenceEquals(slots.Get("bench"), previous), "Camera slot did not expose its active capture");

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
            check(!retiring.IsCompleted && survivor.Disposals == 0, "Camera shutdown took a capture snapshot before joining the admitted restart");
            check(slots.Get("bench") is null && replacementStarts == 0, "A retiring capture or premature replacement was still visible");
        }
        finally { previous.Release.TrySetResult(); }

        await Task.WhenAll(replacing, retiring, lateRestart).WaitAsync(TimeSpan.FromSeconds(5));
        await slots.DisposeAsync();
        check(replacementStarts == 0, "A camera replacement started after shutdown closed admission");
        check(previous.Disposals == 1 && survivor.Disposals == 1, "Camera shutdown missed or repeated capture disposal");
        check(slots.Get("bench") is null && slots.Get("depth") is null && slots.Get("gripper") is null,
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
