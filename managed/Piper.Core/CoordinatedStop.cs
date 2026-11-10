namespace LuckyRobots.Piper;

public sealed record StopChannelResult(bool Ok, string Message);
public sealed record CoordinatedStopResult(StopChannelResult Arm, StopChannelResult Pika)
{
    public bool Ok => Arm.Ok && Pika.Ok;
}

/// <summary>Independent stop attempts: a failed, cancelled, or blocked channel cannot suppress the
/// other attempt. Caller disconnection does not cancel an already requested stop.</summary>
public static class CoordinatedStop
{
    public const string Route = PiperApi.Paths.CoordinatedStop;

    public static async Task<CoordinatedStopResult> RunAsync(
        Func<CancellationToken, Task<StopChannelResult>> stopArm,
        Func<CancellationToken, Task<StopChannelResult>> holdPika,
        TimeSpan? timeout = null)
    {
        TimeSpan bound = timeout ?? TimeSpan.FromSeconds(3);
        if (bound <= TimeSpan.Zero || bound > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        // Task.Run also isolates delegates which throw/block before returning a Task.
        Task<StopChannelResult> arm = Attempt(stopArm, bound), pika = Attempt(holdPika, bound);
        await Task.WhenAll(arm, pika);
        return new(await arm, await pika);
    }

    private static async Task<StopChannelResult> Attempt(Func<CancellationToken, Task<StopChannelResult>> action, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            var task = Task.Run(() => action(cancellation.Token), CancellationToken.None);
            try { return await task.WaitAsync(timeout); }
            finally
            {
                // A native call may not be interruptible. Observe a later fault; never report
                // timeout as a successful stop or start another movement automatically.
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) { return new(false, error is OperationCanceledException or TimeoutException ? "Stop attempt timed out; hardware state is unverified." : error.Message); }
    }
}
