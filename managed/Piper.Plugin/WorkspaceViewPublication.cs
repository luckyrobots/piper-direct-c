using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private sealed record WorkspacePatchScope(PublishedWorkspaceView? View, long Epoch, WorkspaceViewContext Context, Preferences Preferences);
    private sealed record WorkspacePatchEvidence(TelemetryReceipt? Receipt, JsonObject? State, string Backend, long PermissionEpoch);
    private WorkspaceViewContext CurrentWorkspaceViewContext => new(ActiveProfile.Id, Navigation);

    private RobotProfile? CaptureStopProfile(string wireId, RobotProfile selected)
    {
        var visible = workspace.Published?.StopProfile;
        if (visible is not null && wireId == WorkspaceEventScope.Wrap(0, visible.Id, "stop")) return visible;
        return wireId == WorkspaceEventScope.Wrap(0, selected.Id, "stop") ? selected : null;
    }

    private WorkspaceClearConfirmation? DisplayedConfirmation(string wireId, string action)
    {
        return workspace.Confirmed(CurrentWorkspaceViewContext) is { } view &&
            wireId == WorkspaceEventScope.Wrap(view.Epoch, view.Context.Profile, action) ? view.Confirmation : null;
    }

    private async Task<WorkspaceClearConfirmation> AcceptWorkspaceClearance(string wireId, CancellationToken cancellation)
    {
        await workspace.PublicationGate.WaitAsync(cancellation);
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
        finally { workspace.PublicationGate.Release(); }
    }

    private WorkspacePatchScope? CaptureWorkspacePatchScope()
    {
        var published = workspace.Published;
        var settings = preferences;
        var context = CurrentWorkspaceViewContext;
        if (workspace.PublicationUncertain || published is not null && published.Context != context)
            return null;
        return new(published, published?.Epoch ?? 0, context, settings);
    }

    private async Task PublishWorkspacePatch(WorkspacePatchScope scope, Dictionary<string, JsonObject> patches,
        CancellationToken cancellation, WorkspacePatchEvidence? evidence = null)
    {
        await workspace.PublicationGate.WaitAsync(cancellation);
        try
        {
            // The same published view carries the same epoch, so one identity check covers both.
            if (workspace.PublicationUncertain || !ReferenceEquals(scope.View, workspace.Published) ||
                scope.Context != CurrentWorkspaceViewContext ||
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
        finally { workspace.PublicationGate.Release(); }
    }

    internal string UnscopePublishedWorkspaceEvent(string wireId, string? profileId = null)
    {
        var published = workspace.Published;
        string id = WorkspaceEventScope.Unwrap(published?.Epoch ?? 0, profileId ?? ActiveProfile.Id, wireId);
        if (id.Length == 0) return "";
        // Persistent power-release/STOP/cancel actions retain their profile
        // identity. Native engine targets still require a confirmed current view.
        bool viewBound = wireId.StartsWith("view.", StringComparison.Ordinal) || WorkspaceCommandPolicy.RequiresControls(id);
        if (viewBound && (workspace.PublicationUncertain ||
            published is not null && published.Context != CurrentWorkspaceViewContext)) return "";
        return id;
    }

    /// <summary>Publishes a full view. <paramref name="destination"/>, when given, is the
    /// Plugin-1 commit: it is adopted inside the publication gate, just before the
    /// view that shows it is sent, so a cancelled wait commits nothing.</summary>
    private async Task PublishWorkspaceView(CancellationToken cancellation, WorkspaceNavigation? destination = null)
    {
        await workspace.PublicationGate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (destination is not null) workspace.Commit(destination);
            workspace.BeginPublication();
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
        finally { workspace.PublicationGate.Release(); }

        async Task Publish(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var context = CurrentWorkspaceViewContext;
            var profile = ActiveProfile;
            var confirmation = CommandAuthority.Current.Confirmation;
            if (confirmation is not null && (context.Page != WorkspacePage.Controls || confirmation.ProfileId != profile.Id))
            {
                CommandAuthority.DismissConfirmation(confirmation);
                confirmation = null;
            }
            long candidate = workspace.ReserveEpoch();
            var inventory = simulationCameras;
            var cameraChoices = new SimulationCameraChoices(inventory?["sceneId"]?.GetValue<string>() ?? "",
                (inventory?["cameras"] as JsonArray)?.OfType<JsonObject>()
                    .Where(camera => camera["entityId"]?.GetValue<string>() is { Length: > 0 })
                    .Select(camera => (camera["entityId"]!.GetValue<string>(), camera["name"]?.GetValue<string>() ?? "Camera")).ToArray() ?? []);
            var tree = ScopeRobotWidget(BuildWorkspaceView(confirmation is not null, cameraChoices), candidate, context.Profile);
            await engine.PublishViewAsync(Panel, tree, ct);
            var current = ActiveProfile;
            if (context != CurrentWorkspaceViewContext || profile.Id != context.Profile || profile.Id != current.Id ||
                profile.CanDeviceId != current.CanDeviceId || profile.PikaDevice != current.PikaDevice || profile.WslDistribution != current.WslDistribution)
                throw new InvalidOperationException("The workspace changed while publishing its controls; refreshing the current view.");
            workspace.Confirm(new(candidate, context, profile, confirmation, cameraChoices));
        }
    }
}
