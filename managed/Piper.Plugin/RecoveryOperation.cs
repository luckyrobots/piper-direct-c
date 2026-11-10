using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal enum RecoveryKind { HoldHere, ReturnToZero }

    internal sealed class RecoveryOperation(RecoveryKind kind, RobotCommandScope scope, long epoch, CancellationTokenSource cancellation) : IDisposable
    {
        public RecoveryKind Kind { get; } = kind;
        public RobotCommandScope Scope { get; } = scope;
        public long Epoch { get; } = epoch;
        public CancellationToken Token { get; } = cancellation.Token;
        public Task? Worker;
        public long ResetAt;

        public void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { } // Retirement may have completed concurrently.
        }

        public void Dispose() => cancellation.Dispose();
    }

    internal RecoveryOperation? recoveryOperation;
    private RecoveryOperation? CurrentRecovery => Volatile.Read(ref recoveryOperation);
    private bool SupportedRecoveryBusy => CurrentRecovery?.Kind == RecoveryKind.HoldHere;
    private bool RecoveryHomeBusy => CurrentRecovery?.Kind == RecoveryKind.ReturnToZero;

    internal RecoveryOperation BeginRecovery(RecoveryKind kind, JsonObject device, long expectedEpoch, CancellationToken cancellation)
    {
        if (CurrentRecovery is not null || GroupFollowActive)
            throw new InvalidOperationException("Finish the current recovery or stop following before starting recovery.");
        var scope = new RobotCommandScope(ActiveProfile.Id, token, device["id"]!.GetValue<string>(), device["generation"]!.GetValue<long>());
        // The actor retires queued targets by epoch. Revoke under the original
        // admission epoch so STOP cannot turn this click into a fresh grant.
        long epoch = CommandAuthority.Revoke(forgetApproval: scope.ProfileId,
            revokeLocalApproval: () => { approved = linkCommandsEnabled = false; StopViewportDrag(); },
            expectedEpoch: expectedEpoch);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        if (kind == RecoveryKind.ReturnToZero) lifetime.CancelAfter(TimeSpan.FromSeconds(90));
        var operation = new RecoveryOperation(kind, scope, epoch, lifetime);
        if (!CommandAuthority.TryInEpoch(epoch, () => Volatile.Write(ref recoveryOperation, operation)))
        {
            operation.Dispose();
            throw new InvalidOperationException("STOP cancelled the recovery before it started.");
        }
        StopViewportDrag();
        return operation;
    }

    private bool RecoveryScopeMatches(RecoveryOperation operation)
    {
        var scope = operation.Scope;
        return ReferenceEquals(CurrentRecovery, operation) && operation.Epoch == CommandAuthority.Epoch &&
            scope.Backend == token && scope.ProfileId == ActiveProfile.Id && scope.Device == ActiveProfile.CanDeviceId;
    }

    private bool RecoveryScopeCurrent(RecoveryOperation operation) => !operation.Token.IsCancellationRequested && RecoveryScopeMatches(operation);

    internal bool RecoveryExpectedGap(TelemetryReceipt? receipt)
    {
        if (CurrentRecovery is not { } operation || !RecoveryScopeCurrent(operation) || receipt is null ||
            receipt.BackendToken != operation.Scope.Backend || !receipt.IsFresh(Stopwatch.GetTimestamp())) return false;
        long resetAt = Volatile.Read(ref operation.ResetAt);
        var scope = operation.Scope;
        if (resetAt == 0) return false;
        if (operation.Kind == RecoveryKind.ReturnToZero)
            return TeachingResetAssisted.ExpectedFeedbackGap(receipt.State, scope.Device, scope.Generation);
        if (Stopwatch.GetElapsedTime(resetAt).TotalSeconds > 5 || receipt.State["selected"]?.GetValue<string>() != scope.Device) return false;
        var device = receipt.State["devices"]?.AsArray().OfType<JsonObject>().SingleOrDefault(d => d["id"]?.GetValue<string>() == scope.Device);
        return device?["generation"]?.GetValue<long>() == scope.Generation && device["present"]?.GetValue<bool>() == true &&
            device["up"]?.GetValue<bool>() == true && device["status"]?["error"]?.GetValue<int>() == 0 &&
            device["status"]?["arm_status"]?.GetValue<int>() is 0 or 1 && device["joints"] is JsonArray { Count: 6 } joints &&
            joints.All(j => j?["fault"]?.GetValue<int>() == 0) && !receipt.HasFreshDevice(device, Stopwatch.GetTimestamp());
    }

    internal void GrantRecoveryHolding(RecoveryOperation operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!RecoveryScopeCurrent(operation) || !CommandAuthority.TryInEpoch(operation.Epoch, () => approved = true))
            throw new InvalidOperationException("STOP or a connection change retired the recovery permission.");
    }

    private void CompleteRecoveryHolding(RecoveryOperation operation)
    {
        if (!RecoveryScopeCurrent(operation) || !CommandAuthority.TryInEpoch(operation.Epoch, () =>
        {
            operation.Token.ThrowIfCancellationRequested();
            RememberControlApproval(operation.Epoch, activate: true);
            teachingSession.Release();
        }))
            throw new InvalidOperationException("STOP or a connection change retired the recovery result.");
    }

    internal void RetireRecovery(RecoveryOperation operation)
    {
        Debug.Assert(ReferenceEquals(CurrentRecovery, operation));
        // On an interrupted native step, independently stop renewing the exact
        // captured lease even when the best-effort STOP has no acknowledgement.
        if (CommandAuthority.Current.Lease is { } lease && lease.Scope == operation.Scope)
            CommandAuthority.EndLease(lease);
        Volatile.Write(ref recoveryOperation, null);
        operation.Dispose();
    }
}
