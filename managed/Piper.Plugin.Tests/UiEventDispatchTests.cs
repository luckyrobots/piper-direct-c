using System.Collections.Concurrent;
using System.Text.Json;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class UiEventDispatchTests
{
    private static UiEvent Event(string widget, object? value = null) => new("panel", widget, "change", JsonSerializer.SerializeToElement(value));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static async Task Run(Action<bool, string> check)
    {
        long epoch = 0;
        var entered = Signal(); var release = Signal(); var preference = Signal();
        var handled = new ConcurrentQueue<string>(); var dropped = new ConcurrentQueue<string>(); var errors = new ConcurrentQueue<Exception>();
        await using (var dispatch = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "enable") { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); }
            handled.Enqueue(input.WidgetId);
            if (input.WidgetId == "camera.preference") preference.TrySetResult();
        }, () => Interlocked.Read(ref epoch), input => input.WidgetId is "enable" or "joint.target", errors.Enqueue,
            discarded: (input, _) => dropped.Enqueue(input.WidgetId)))
        {
            check(dispatch.Enqueue(Event("enable"), 0), "Runtime actor rejected its first enable event");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // These synchronous calls must finish even while the first handler is blocked.
            check(dispatch.Enqueue(Event("joint.target"), 0) && dispatch.Enqueue(Event("camera.preference"), 0),
                "Blocked handler prevented the SDK-facing enqueue from returning");
            var stopped = Signal();
            Interlocked.Increment(ref epoch);
            dispatch.InvalidateControlEvents();
            // The plugin's immediate STOP path executes outside this actor/semaphore.
            stopped.TrySetResult();
            check(stopped.Task.IsCompletedSuccessfully && !release.Task.IsCompleted,
                "Immediate STOP was forced to wait for the ordinary UI handler");
            await preference.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!handled.Contains("joint.target") && dropped.Contains("joint.target"), "STOP epoch let a queued target resume afterward");
            check(handled.Contains("camera.preference"), "CAN/permission epoch change blocked an independent camera preference");
            check(errors.IsEmpty, "Expected control cancellation was reported as an unhandled action failure");
            release.TrySetResult();
        }

        var boundedEntered = Signal(); var boundedRelease = Signal();
        await using (var bounded = new UiEventDispatch(async (_, cancellation) =>
        { boundedEntered.TrySetResult(); await boundedRelease.Task.WaitAsync(cancellation); }, () => 0, _ => false, errors.Enqueue, capacity: 2))
        {
            check(bounded.Enqueue(Event("blocked"), 0), "Bounded actor rejected initial event");
            await boundedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(bounded.Enqueue(Event("queued1"), 0) && bounded.Enqueue(Event("queued2"), 0) && !bounded.Enqueue(Event("overflow"), 0),
                "UI actor queue did not enforce its configured capacity");
            await bounded.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            check(!bounded.Enqueue(Event("after-stop"), 0), "Stopped actor accepted another target");
            boundedRelease.TrySetResult();
        }

        var payloadEntered = Signal(); var payloadRelease = Signal(); var payloadRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var payloads = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "blocked") { payloadEntered.TrySetResult(); await payloadRelease.Task.WaitAsync(cancellation); }
            else payloadRead.TrySetResult(input.Value.GetProperty("opening").GetInt32());
        }, () => 0, _ => false, errors.Enqueue))
        {
            payloads.Enqueue(Event("blocked"), 0); await payloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (var document = JsonDocument.Parse("{\"opening\":23}"))
                check(payloads.Enqueue(new("panel", "payload", "change", document.RootElement), 0), "Payload event enqueue failed");
            payloadRelease.TrySetResult();
            check(await payloadRead.Task.WaitAsync(TimeSpan.FromSeconds(2)) == 23, "Queued event retained a disposed SDK JSON document");
        }

        var disposeEntered = Signal(); var cancelled = Signal(); var afterCancel = Signal(); bool oldTargetRan = false;
        await using (var drain = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "active")
            {
                disposeEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); await afterCancel.Task; throw; }
            }
            else oldTargetRan = true;
        }, () => 0, _ => true, errors.Enqueue))
        {
            drain.Enqueue(Event("active"), 0); await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); drain.Enqueue(Event("queued"), 0);
            Task stopping = drain.StopAsync(); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!stopping.IsCompleted, "Shutdown released ownership before its active UI handler drained");
            afterCancel.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            check(!oldTargetRan, "Shutdown ran a queued control action while draining");
        }
        check(errors.IsEmpty, "UI actor fixture recorded unexpected errors");

        var next = Signal(); var failures = new ConcurrentQueue<Exception>();
        await using (var resilient = new UiEventDispatch((input, _) =>
        {
            if (input.WidgetId == "failure") throw new IOException("fake transport failed");
            next.TrySetResult(); return Task.CompletedTask;
        }, () => 0, _ => false, failures.Enqueue))
        {
            resilient.Enqueue(Event("failure"), 0); resilient.Enqueue(Event("next"), 0);
            await next.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(failures.Count == 1 && failures.Single().Message == "fake transport failed",
                "A failed action silently killed the UI actor or lost its diagnostic");
        }

        var urgentEntered = Signal(); var disabled = Signal(); var ordinary = Signal();
        var urgentOrder = new ConcurrentQueue<string>(); long urgentEpoch = 0;
        await using (var urgent = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "connecting") { urgentEntered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
            urgentOrder.Enqueue(input.WidgetId);
            if (input.WidgetId == "disable") disabled.TrySetResult();
            if (input.WidgetId == "camera.preference") ordinary.TrySetResult();
        }, () => urgentEpoch, input => input.WidgetId != "camera.preference", errors.Enqueue, capacity: 2))
        {
            urgent.Enqueue(Event("connecting"), urgentEpoch);
            await urgentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            urgent.Enqueue(Event("joint.target"), urgentEpoch);
            urgent.Enqueue(Event("camera.preference"), urgentEpoch);
            Interlocked.Increment(ref urgentEpoch);
            check(urgent.EnqueueUrgent(Event("disable"), urgentEpoch), "Disable could not preempt a full UI queue");
            await disabled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ordinary.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(urgentOrder.SequenceEqual(new[] { "disable", "camera.preference" }),
                "Disable waited behind settings or a retired movement ran after it");
        }
        check(errors.IsEmpty, "Urgent Disable cancellation was reported as a timeout");

        long viewEpoch = 12;
        var renderEntered = Signal(); var persistentDisable = Signal();
        string disableWire = WorkspaceEventScope.Wrap(viewEpoch, "piper-1", "disable");
        await using (var rendering = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "rendering")
            {
                renderEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation); }
                finally { Interlocked.Increment(ref viewEpoch); }
            }
            else if (WorkspaceEventScope.Unwrap(viewEpoch, "piper-1", input.WidgetId) == "disable") persistentDisable.TrySetResult();
        }, () => 0, _ => false, errors.Enqueue))
        {
            rendering.Enqueue(Event("rendering"), 0);
            await renderEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            rendering.EnqueueUrgent(Event(disableWire), 0);
            await persistentDisable.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(WorkspaceEventScope.Unwrap(viewEpoch, "piper-2", disableWire).Length == 0,
                "A Disable surviving a render was redirected to a different physical robot");
            check(errors.IsEmpty, "Cancelled render falsely reported an action timeout during Disable");
        }

        var expired = Signal(); var afterTimeout = Signal();
        await using (var timeBound = new UiEventDispatch(async (input, cancellation) =>
        {
            if (input.WidgetId == "hung") await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            else afterTimeout.TrySetResult();
        }, () => 0, _ => false, error => { if (error is TimeoutException) expired.TrySetResult(); },
            actionTimeout: TimeSpan.FromMilliseconds(50)))
        {
            timeBound.Enqueue(Event("hung"), 0); timeBound.Enqueue(Event("next"), 0);
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await afterTimeout.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(true, "Timed out UI operation did not report a reason and release the serial worker");
        }
    }
}
