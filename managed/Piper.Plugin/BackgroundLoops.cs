using System.Diagnostics;
using Grpc.Core;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    internal TimeSpan backgroundLoopRestartDelay = TimeSpan.FromSeconds(1);
    private int backgroundLoopFaults;
    /// <summary>How many times a supervised background loop ended outside shutdown.</summary>
    internal int BackgroundLoopFaults => Volatile.Read(ref backgroundLoopFaults);

    internal Task Watch(CancellationToken cancellation) => Task.WhenAll(
        Supervise("Telemetry reader", ReadTelemetry, cancellation),
        Supervise("Telemetry processing", ProcessTelemetry, cancellation),
        Supervise("Arm heartbeat", ArmHeartbeats, cancellation),
        Supervise("Pika heartbeat", PikaHeartbeats, cancellation),
        Supervise("Pika monitor", MonitorAssignedPikas, cancellation));

    /// <summary>Runs one background loop until <paramref name="cancellation"/>. The loops
    /// carry the feedback watchdog and the lease heartbeats, so none may end unseen:
    /// a fault (or an unexpected return) clears motion permission, is shown to the
    /// operator, and the loop is started again after a short pause.</summary>
    internal async Task Supervise(string name, Func<CancellationToken, Task> loop, CancellationToken cancellation)
    {
        while (true)
        {
            string cause;
            try
            {
                await loop(cancellation).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested) return;
                cause = "ended unexpectedly";
            }
            catch (Exception error) when (cancellation.IsCancellationRequested &&
                error is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled }) { return; }
            catch (Exception error) { cause = "failed: " + error.Message; }
            ReportBackgroundLoopFault(name, cause);
            try { await Task.Delay(backgroundLoopRestartDelay, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ReportBackgroundLoopFault(string name, string cause)
    {
        Interlocked.Increment(ref backgroundLoopFaults);
        string report = name + " " + cause + " · motion permission cleared · restarting. Check the robot before continuing.";
        Trace.TraceError("Background loop: {0}", report);
        try { DisarmTelemetryTargets(name + " " + cause, clearLink: false); }
        catch (Exception error)
        {
            // Reporting must survive a failing disarm step; the permission itself is still withdrawn.
            approved = linkCommandsEnabled = false;
            CommandAuthority.Revoke(forgetAllApprovals: true);
            Trace.TraceError("Background loop disarm: {0}", error);
        }
        uiActionError = message = report;
    }
}
