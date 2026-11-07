using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private readonly SemaphoreSlim workspacePublicationGate = new(1, 1);
    private long nextWorkspaceViewEpoch;
    private int workspacePublicationUncertain;
    private sealed record PublishedWorkspaceView(long Epoch, WorkspaceViewContext Context);
    private readonly record struct WorkspaceViewContext(string Profile, string Page, string SettingsCategory,
        string SettingsCamera, string CameraDetail, bool Calibration);
    private PublishedWorkspaceView? publishedWorkspaceView;
    private WorkspaceViewContext CurrentWorkspaceViewContext => new(ActiveProfile.Id, workspacePage, settingsCategory, settingsCamera, cameraDetail, calibrationOpen);
    private long PublishedWorkspaceEpoch => Volatile.Read(ref publishedWorkspaceView)?.Epoch ?? Interlocked.Read(ref workspaceViewEpoch);

    private string UnscopePublishedWorkspaceEvent(string wireId)
    {
        var published = Volatile.Read(ref publishedWorkspaceView);
        string id = WorkspaceEventScope.Unwrap(published?.Epoch ?? Interlocked.Read(ref workspaceViewEpoch), ActiveProfile.Id, wireId);
        if (id.Length == 0) return "";
        // Persistent power-release/STOP/cancel actions retain their profile
        // identity. Native engine targets still require a confirmed current view.
        bool viewBound = wireId.StartsWith("view.", StringComparison.Ordinal) || WorkspaceCommandPolicy.RequiresControls(id);
        if (viewBound && (Volatile.Read(ref workspacePublicationUncertain) != 0 ||
            published is not null && published.Context != CurrentWorkspaceViewContext)) return "";
        return id;
    }

    private async Task PublishWorkspaceView(CancellationToken cancellation)
    {
        await workspacePublicationGate.WaitAsync(cancellation);
        try
        {
            Volatile.Write(ref workspacePublicationUncertain, 1);
            try { await Publish(cancellation); }
            catch (Exception first) when (!shutdown.IsCancellationRequested)
            {
                // A failed/cancelled write may already have reached the host.
                // Publish one complete new epoch independently of the cancelled
                // action; never guess which earlier tree is currently visible.
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                recovery.CancelAfter(TimeSpan.FromSeconds(2));
                try { await Publish(recovery.Token); }
                catch (Exception retry)
                {
                    uiActionError = message = "Panel refresh failed. Reopen Pipers to reconnect its controls. STOP, Disable and recovery Cancel remain available for the selected robot.";
                    Trace.TraceWarning("Workspace publication and bounded retry failed: {0}; {1}", first.Message, retry.Message);
                    throw new InvalidOperationException(uiActionError, retry);
                }
                cancellation.ThrowIfCancellationRequested();
            }
        }
        finally { workspacePublicationGate.Release(); }

        async Task Publish(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var context = CurrentWorkspaceViewContext;
            long candidate = Math.Max(nextWorkspaceViewEpoch, Interlocked.Read(ref workspaceViewEpoch)) + 1;
            nextWorkspaceViewEpoch = candidate;
            var tree = ScopeRobotWidget(BuildWorkspaceView(), candidate, context.Profile);
            await engine.PublishViewAsync(Panel, tree, ct);
            if (context != CurrentWorkspaceViewContext)
                throw new InvalidOperationException("The workspace changed while publishing its controls; refreshing the current view.");
            Volatile.Write(ref publishedWorkspaceView, new(candidate, context));
            Interlocked.Exchange(ref workspaceViewEpoch, candidate);
            Volatile.Write(ref workspacePublicationUncertain, 0);
        }
    }
}
