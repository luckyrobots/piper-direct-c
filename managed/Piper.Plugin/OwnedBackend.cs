using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Plugin-4 restart of the controller backend: Idle → Confirming (the
/// operator opened the gripper-release confirmation) → Requested (the request
/// was sent) → Relaunching (this session's owned process exited for maintenance
/// and is being started again) → Idle. Any failure returns to Idle.</summary>
internal enum BackendRestartPhase { Idle, Confirming, Requested, Relaunching }

/// <summary>A controller process this session started. <see cref="Token"/> is
/// null until that process has answered with its session token.</summary>
internal sealed record OwnedBackendProcess(Process Process, string? Token);

/// <summary>Everything Plugin-4 knows at one moment. <see cref="Owned"/> is null
/// for an external controller, which this session never shuts down.</summary>
internal sealed record BackendStanding(OwnedBackendProcess? Owned, BackendRestartPhase Restart, string Status, string Setup);

/// <summary>The Plugin-4 part of the Shell component: whether this session owns
/// the controller process, how far a restart has come, and the two lines the
/// backend page shows.
/// <code>
///   field     written by                                       readers see
///   standing  the UI actor (every transition below) and        one immutable BackendStanding
///             plugin disposal (Release), each through one      through one volatile reference
///             compare-and-swap
/// </code>
/// Ownership: external (Owned null) → Starting (Token null) → Ready (Token set)
/// → external again by <see cref="Disown"/> or <see cref="Release"/>. Restart:
/// see <see cref="BackendRestartPhase"/>. The two are independent: an external
/// controller can be restarted, and an owned one is relaunched in the
/// Relaunching phase.
///
/// Not owned here: the process launch, the readiness probe, the restart request
/// and the shutdown request (the backend workflows on <c>PipersPlugin</c>), and
/// the backend settings (preferences).</summary>
internal sealed class OwnedBackend
{
    private BackendStanding standing = new(null, BackendRestartPhase.Idle,
        $"Use the existing controller, or start the bundled server when ports {ControllerEndpoint.HttpPort}/{ControllerEndpoint.GrpcPort} are free.",
        "Windows Pika requires WSL 2 + a Linux distribution and usbipd-win. Share only the Pika USB device with WSL. Driver/USB setup is explicit; see plugin instructions.");

    public BackendStanding Standing => Volatile.Read(ref standing);

    /// <summary>Applies one transition and returns the standing it replaced.</summary>
    private BackendStanding Change(Func<BackendStanding, BackendStanding> transition)
    {
        while (true)
        {
            var before = Volatile.Read(ref standing);
            if (ReferenceEquals(Interlocked.CompareExchange(ref standing, transition(before), before), before)) return before;
        }
    }

    public void Report(string status) => Change(before => before with { Status = status });
    public void ReportSetup(string setup) => Change(before => before with { Setup = setup });

    /// <summary>Gives up the owned process, if any, before another is launched.</summary>
    public void Disown() => Change(before => before with { Owned = null }).Owned?.Process.Dispose();

    /// <summary>A launched process that has not answered yet.</summary>
    public void Starting(Process process) => Change(before => before with { Owned = new(process, null) });

    /// <summary>The launched process answered as this session's own.</summary>
    public void Ready(Process process, string token) => Change(before => before with { Owned = new(process, token) });

    /// <summary>Ends ownership of <paramref name="owned"/> and closes its process
    /// handle. A process launched since then stays owned.</summary>
    public void Release(OwnedBackendProcess owned)
    {
        Change(before => ReferenceEquals(before.Owned, owned) ? before with { Owned = null } : before);
        owned.Process.Dispose();
    }

    /// <summary>The operator asked to restart: show the confirmation.</summary>
    public void ConfirmRestart() => Change(before =>
        before.Restart is BackendRestartPhase.Idle or BackendRestartPhase.Confirming
            ? before with { Restart = BackendRestartPhase.Confirming }
            : throw new InvalidOperationException("A backend restart is already in progress."));

    /// <summary>The operator closed the confirmation. A running restart is unaffected.</summary>
    public void CancelRestart() => Change(before =>
        before.Restart == BackendRestartPhase.Confirming ? before with { Restart = BackendRestartPhase.Idle } : before);

    public void RestartRequested() => Change(before => before with { Restart = BackendRestartPhase.Requested });
    public void Relaunching() => Change(before => before with { Restart = BackendRestartPhase.Relaunching });
    /// <summary>The restart finished or failed.</summary>
    public void RestartEnded() => Change(before => before with { Restart = BackendRestartPhase.Idle });
}
