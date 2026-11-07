using System.Collections.Immutable;

namespace LuckyRobots.Piper.Plugin;

internal sealed record RobotCommandScope(string ProfileId, string Backend, string Device, long Generation);

internal sealed record TrackedRobotCommand(RobotCommandScope Scope, long Request);

internal sealed record WorkspaceClearConfirmation(long Epoch, string ProfileId, FollowControlApproval Scope);

internal sealed record RobotCommandState(long Epoch, TrackedRobotCommand? Lease, TrackedRobotCommand? LastCommand,
    ImmutableDictionary<string, FollowControlApproval> Approvals, WorkspaceClearConfirmation? Confirmation = null);

// Owns the local permission epoch and acknowledgement bookkeeping. The controller
// independently authorizes commands and owns the physical lease and STOP latch.
internal sealed class RobotCommandAuthority
{
    private readonly object gate = new();
    private RobotCommandState state = new(0, null, null, ImmutableDictionary.Create<string, FollowControlApproval>(StringComparer.Ordinal));
    public RobotCommandState Current => Volatile.Read(ref state);
    public long Epoch => Current.Epoch;

    public long Revoke(string? forgetApproval = null, Action? revokeLocalApproval = null, bool forgetAllApprovals = false)
    {
        lock (gate)
        {
            long epoch = state.Epoch + 1;
            var approvals = forgetAllApprovals ? state.Approvals.Clear() :
                forgetApproval is null ? state.Approvals : state.Approvals.Remove(forgetApproval);
            revokeLocalApproval?.Invoke();
            Volatile.Write(ref state, new(epoch, null, null, approvals));
            return epoch;
        }
    }

    public bool TryAcknowledge(long epoch, RobotCommandScope scope, long? request)
    {
        lock (gate)
        {
            if (epoch != state.Epoch) return false;
            var command = request is > 0 ? new TrackedRobotCommand(scope, request.Value) : null;
            var lease = request.HasValue ? command : state.Lease?.Scope == scope ? state.Lease : null;
            Volatile.Write(ref state, state with { Lease = lease, LastCommand = command });
            return true;
        }
    }

    public bool TryOpenConfirmation(long epoch, string profile, FollowControlApproval scope)
    {
        lock (gate)
        {
            if (epoch != state.Epoch) return false;
            Volatile.Write(ref state, state with { Confirmation = new(epoch, profile, scope) });
            return true;
        }
    }

    public bool TryConsumeConfirmation(WorkspaceClearConfirmation confirmation, string profile, FollowControlApproval scope, Action approve)
    {
        lock (gate)
        {
            if (!ReferenceEquals(state.Confirmation, confirmation) || confirmation.Epoch != state.Epoch ||
                confirmation.ProfileId != profile || confirmation.Scope != scope) return false;
            approve();
            Volatile.Write(ref state, state with { Confirmation = null });
            return true;
        }
    }

    public void DismissConfirmation(WorkspaceClearConfirmation? confirmation)
    {
        lock (gate)
            if (confirmation is not null && ReferenceEquals(state.Confirmation, confirmation))
                Volatile.Write(ref state, state with { Confirmation = null });
    }

    public bool TrySaveApproval(long epoch, string profile, FollowControlApproval approval, Action? activate = null)
    {
        lock (gate)
        {
            if (epoch != state.Epoch) return false;
            activate?.Invoke();
            Volatile.Write(ref state, state with { Approvals = state.Approvals.SetItem(profile, approval) });
            return true;
        }
    }

    public bool TryRestoreApproval(long epoch, string profile, FollowControlApproval expected, Action restore)
    {
        lock (gate)
        {
            if (epoch != state.Epoch || !state.Approvals.TryGetValue(profile, out var approval) || approval != expected) return false;
            restore();
            return true;
        }
    }

    public bool TryInEpoch(long epoch, Action update)
    {
        lock (gate)
        {
            if (epoch != state.Epoch) return false;
            update();
            return true;
        }
    }

    public void ForgetApproval(string profile, FollowControlApproval? expected = null)
    {
        lock (gate)
            if (state.Approvals.TryGetValue(profile, out var approval) && (expected is null || approval == expected))
                Volatile.Write(ref state, state with { Approvals = state.Approvals.Remove(profile) });
    }

    public void ClearTracked()
    {
        lock (gate)
            if (state.Lease is not null || state.LastCommand is not null)
                Volatile.Write(ref state, state with { Lease = null, LastCommand = null });
    }

    public void EndLease(TrackedRobotCommand command)
    {
        lock (gate)
            if (ReferenceEquals(state.Lease, command))
                Volatile.Write(ref state, state with { Lease = null });
    }

    public void EndStatus(TrackedRobotCommand command)
    {
        lock (gate)
            if (ReferenceEquals(state.LastCommand, command))
                Volatile.Write(ref state, state with { LastCommand = null });
    }
}
