using LuckyRobots.Piper.Plugin;

internal static class OrderedFrameReadRetryTests
{
    public static void Run(Action<bool, string> check)
    {
        check(OrderedFrameReadRetry.ExpectedStop(false, true, true), "A known Stop cancellation before its150ms deadline must not become a false first failure");
        check(!OrderedFrameReadRetry.ExpectedStop(true, true, true), "A real read deadline must remain diagnosable when Stop arrives later");
        check(!OrderedFrameReadRetry.ExpectedStop(false, true, false) && !OrderedFrameReadRetry.ExpectedStop(false, false, true),
            "Only an explicitly ending cancelled session may classify a read as expected Stop");
        using (var stop = new CancellationTokenSource())
        using (var worker = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        using (var deadline = new CancellationTokenSource())
        using (var read = CancellationTokenSource.CreateLinkedTokenSource(worker.Token, stop.Token, deadline.Token))
        {
            bool observedBeforeOuterLink = false;
            using var registration = read.Token.Register(() => observedBeforeOuterLink = !worker.IsCancellationRequested &&
                OrderedFrameReadRetry.ExpectedStop(deadline.IsCancellationRequested, stop.IsCancellationRequested, true));
            stop.Cancel();
            check(observedBeforeOuterLink,
                "The real nested Stop links can cancel a source read before its outer worker token; that ordering must remain an expected Stop");
        }
        check(OrderedFrameReadRetry.Allowed(150, false, true, true), "A read-only150ms timeout should permit same-cursor retry inside its existing250ms physical lease");
        check(OrderedFrameReadRetry.Allowed(249.999, false, true, true), "Retry must preserve the exact remaining validated source lease");
        foreach (double age in new[] { -1d, 250, 251, double.NaN, double.PositiveInfinity })
            check(!OrderedFrameReadRetry.Allowed(age, false, true, true), "Missing, invalid or expired evidence must not renew a queued physical source lease");
        check(!OrderedFrameReadRetry.Allowed(150, true, true, true), "Operator cancellation must not become a camera-stall retry");
        check(!OrderedFrameReadRetry.Allowed(150, false, false, true), "A replaced runtime, robot or permission scope must not retry old source frames");
        check(!OrderedFrameReadRetry.Allowed(150, false, true, false), "A retired publisher must not be restarted by a late source reply");
    }
}
