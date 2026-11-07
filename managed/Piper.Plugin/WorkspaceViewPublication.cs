using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private readonly SemaphoreSlim workspacePublicationGate = new(1, 1);
    private long nextWorkspaceViewEpoch;
    private int workspacePublicationUncertain;
    private sealed record PublishedWorkspaceView(long Epoch, WorkspaceViewContext Context, RobotProfile StopProfile,
        WorkspaceClearConfirmation? Confirmation);
    private readonly record struct WorkspaceViewContext(string Profile, string Page, string SettingsCategory,
        string SettingsCamera, string CameraDetail, bool Calibration);
    private sealed record WorkspacePatchScope(PublishedWorkspaceView? View, long Epoch, WorkspaceViewContext Context, Preferences Preferences);
    private sealed record WorkspacePatchEvidence(TelemetryReceipt? Receipt, JsonObject? State, string Backend, long PermissionEpoch);
    private PublishedWorkspaceView? publishedWorkspaceView;
    private WorkspaceViewContext CurrentWorkspaceViewContext => new(ActiveProfile.Id, workspacePage, settingsCategory, settingsCamera, cameraDetail, calibrationOpen);
    private long PublishedWorkspaceEpoch => Volatile.Read(ref publishedWorkspaceView)?.Epoch ?? Interlocked.Read(ref workspaceViewEpoch);

    private RobotProfile? CaptureStopProfile(string wireId, RobotProfile selected)
    {
        var visible = Volatile.Read(ref publishedWorkspaceView)?.StopProfile;
        if (visible is not null && wireId == WorkspaceEventScope.Wrap(0, visible.Id, "stop")) return visible;
        return wireId == WorkspaceEventScope.Wrap(0, selected.Id, "stop") ? selected : null;
    }

    private WorkspaceClearConfirmation? DisplayedConfirmation(string wireId, string action)
    {
        var view = Volatile.Read(ref publishedWorkspaceView);
        return view is not null && Volatile.Read(ref workspacePublicationUncertain) == 0 &&
            view.Context == CurrentWorkspaceViewContext && wireId == WorkspaceEventScope.Wrap(view.Epoch, view.Context.Profile, action)
            ? view.Confirmation : null;
    }

    private async Task<WorkspaceClearConfirmation> AcceptWorkspaceClearance(string wireId, CancellationToken cancellation)
    {
        await workspacePublicationGate.WaitAsync(cancellation);
        try
        {
            var confirmation = DisplayedConfirmation(wireId, "safety.accept") ??
                throw new InvalidOperationException("Open Enable robot and confirm that the workspace is clear.");
            try
            {
                var profile = ActiveProfile;
                string backend = token;
                var device = Device(expectedToken: backend);
                var readiness = Readiness(device);
                if (!PrimaryActionAvailable || readiness.NextAction != RobotPrimaryAction.Enable)
                    throw new InvalidOperationException("Enable robot is no longer available. " + readiness.Label + ". " + readiness.Detail);
                if (!CommandAuthority.TryConsumeConfirmation(confirmation, profile.Id, ControlScope(profile, backend, device), () => approved = true))
                    throw new InvalidOperationException("The workspace confirmation expired. Click Enable robot to confirm the current connection.");
                return confirmation;
            }
            finally { CommandAuthority.DismissConfirmation(confirmation); }
        }
        finally { workspacePublicationGate.Release(); }
    }

    private WorkspacePatchScope? CaptureWorkspacePatchScope()
    {
        var published = Volatile.Read(ref publishedWorkspaceView);
        var settings = preferences;
        var context = CurrentWorkspaceViewContext;
        if (Volatile.Read(ref workspacePublicationUncertain) != 0 ||
            published is not null && published.Context != context)
            return null;
        return new(published, published?.Epoch ?? Interlocked.Read(ref workspaceViewEpoch), context, settings);
    }

    private async Task PublishWorkspacePatch(WorkspacePatchScope scope, Dictionary<string, JsonObject> patches,
        CancellationToken cancellation, WorkspacePatchEvidence? evidence = null)
    {
        await workspacePublicationGate.WaitAsync(cancellation);
        try
        {
            if (Volatile.Read(ref workspacePublicationUncertain) != 0 ||
                !ReferenceEquals(scope.View, Volatile.Read(ref publishedWorkspaceView)) ||
                scope.Epoch != PublishedWorkspaceEpoch || scope.Context != CurrentWorkspaceViewContext ||
                !ReferenceEquals(scope.Preferences, preferences))
                return;
            if (evidence is not null && (!ReferenceEquals(evidence.Receipt, telemetry.Latest) ||
                !ReferenceEquals(evidence.State, state) || evidence.Backend != token ||
                evidence.PermissionEpoch != CommandAuthority.Epoch))
                return;

            // A patch always carries the epoch that existed when its data was read.
            // Serialize its send with full views so persistent widget IDs also
            // cannot receive an old patch after a replacement tree was sent.
            var scoped = patches.ToDictionary(
                patch => WorkspaceEventScope.Wrap(scope.Epoch, scope.Context.Profile, patch.Key),
                patch => patch.Value);
            await engine.PatchAsync(Panel, scoped, cancellation);
        }
        finally { workspacePublicationGate.Release(); }
    }

    private string UnscopePublishedWorkspaceEvent(string wireId, string? profileId = null)
    {
        var published = Volatile.Read(ref publishedWorkspaceView);
        string id = WorkspaceEventScope.Unwrap(published?.Epoch ?? Interlocked.Read(ref workspaceViewEpoch), profileId ?? ActiveProfile.Id, wireId);
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
            var profile = ActiveProfile;
            var confirmation = CommandAuthority.Current.Confirmation;
            if (confirmation is not null && (context.Page != "controls" || context.Calibration || confirmation.ProfileId != profile.Id))
            {
                CommandAuthority.DismissConfirmation(confirmation);
                confirmation = null;
            }
            long candidate = Math.Max(nextWorkspaceViewEpoch, Interlocked.Read(ref workspaceViewEpoch)) + 1;
            nextWorkspaceViewEpoch = candidate;
            var tree = ScopeRobotWidget(BuildWorkspaceView(confirmation is not null), candidate, context.Profile);
            await engine.PublishViewAsync(Panel, tree, ct);
            var current = ActiveProfile;
            if (context != CurrentWorkspaceViewContext || profile.Id != context.Profile || profile.Id != current.Id ||
                profile.CanDeviceId != current.CanDeviceId || profile.PikaDevice != current.PikaDevice || profile.WslDistribution != current.WslDistribution)
                throw new InvalidOperationException("The workspace changed while publishing its controls; refreshing the current view.");
            Volatile.Write(ref publishedWorkspaceView, new(candidate, context, profile, confirmation));
            Interlocked.Exchange(ref workspaceViewEpoch, candidate);
            Volatile.Write(ref workspacePublicationUncertain, 0);
        }
    }
}
