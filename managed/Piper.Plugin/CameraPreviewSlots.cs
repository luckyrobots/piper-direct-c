namespace LuckyRobots.Piper.Plugin;

internal sealed class CameraPreviewSlots<TCapture> : IAsyncDisposable where TCapture : class, IAsyncDisposable
{
    private enum Phase { Accepting, Retiring, Retired }

    private readonly object stateGate = new();
    private readonly SemaphoreSlim changes = new(1);
    private readonly Dictionary<string, TCapture> slots = [];
    private Phase phase;

    public TCapture? Get(string id)
    {
        lock (stateGate) return slots.GetValueOrDefault(id);
    }

    public void BeginShutdown()
    {
        lock (stateGate)
            if (phase == Phase.Accepting)
                phase = Phase.Retiring;
    }

    public async Task ReplaceAsync(string id, Func<TCapture>? startReplacement)
    {
        await changes.WaitAsync();
        try
        {
            TCapture? previous;
            lock (stateGate)
            {
                if (phase != Phase.Accepting) return;
                slots.Remove(id, out previous);
            }
            if (previous is not null) await previous.DisposeAsync();

            // Shutdown can win while the previous worker is draining. Starting
            // the replacement and closing admission share this short lock.
            lock (stateGate)
                if (phase == Phase.Accepting && startReplacement is not null)
                    slots.Add(id, startReplacement());
        }
        finally { changes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        await changes.WaitAsync();
        try
        {
            TCapture[] captures;
            lock (stateGate)
            {
                if (phase == Phase.Retired) return;
                captures = slots.Values.ToArray();
                slots.Clear();
            }
            await Task.WhenAll(captures.Select(async capture => await capture.DisposeAsync()));
        }
        finally
        {
            lock (stateGate) phase = Phase.Retired;
            changes.Release();
        }
    }
}
