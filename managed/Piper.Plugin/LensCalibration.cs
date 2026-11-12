using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The Cal-E part of the Calibration component: the Pika fisheye
/// lens session (<see cref="LensSession"/>), the worker that sets up the
/// camera runtime or solves a session, and the camera profiles in use.
/// <code>
///   field     written by                                          readers see
///   session   the UI actor (Report, New, Captured, BeginSolve,    one volatile reference to an
///             Reset) and the lens worker (Report, Saved,          immutable LensSession
///             Rejected). Each write is a compare-and-swap from
///             the value its writer read.
///   worker    the UI actor (Run), only while no worker is busy    one volatile reference to a task
///   profiles  the UI actor (profile load, RemoveProfile,          a concurrent map of profiles that
///             ClearProfiles), the lens worker and the camera      are not changed once installed
///             workers (InstallProfile)
/// </code>
/// A transition the session cannot make (a solve with no capture folder)
/// throws and changes nothing.
///
/// Not owned here: the capture folders and the saved profile file (the
/// workflow writes them), and the camera workers (Cal-G).</summary>
internal sealed class LensCalibration
{
    private readonly ConcurrentDictionary<string, JsonObject> profiles = new();
    private LensSession session = LensSession.Initial;
    private Task? worker;

    public LensSession Session => Volatile.Read(ref session);
    public bool WorkerBusy => Volatile.Read(ref worker) is { IsCompleted: false };
    /// <summary>For shutdown: the last worker, which may have faulted.</summary>
    public Task? Worker => Volatile.Read(ref worker);

    private void Change(Func<LensSession, LensSession> change)
    {
        LensSession current;
        do current = Session;
        while (!ReferenceEquals(Interlocked.CompareExchange(ref session, change(current), current), current));
    }

    // ---- the session ----

    public void Report(string status) => Change(current => current.WithStatus(status));
    /// <summary>New capture session. Earlier captures and the active profile are kept.</summary>
    public void New() => Change(current => current.New());
    public void Captured(string folder, long sequence, int count) => Change(current => current.Captured(folder, sequence, count));
    public void BeginSolve() => Change(current => current.Solving());
    public void Saved(double heldOutRmsPixels) => Change(current => current.Saved(heldOutRmsPixels));
    public void Rejected(string reason) => Change(current => current.Rejected(reason));
    /// <summary>A robot profile switch: no session.</summary>
    public void Reset() => Volatile.Write(ref session, LensSession.Initial);

    /// <summary>Records the worker now running. The caller has checked <see cref="WorkerBusy"/>.</summary>
    public void Run(Task work) => Volatile.Write(ref worker, work);

    // ---- camera profiles ----

    public JsonObject? Profile(string camera) => profiles.GetValueOrDefault(camera);
    public void InstallProfile(string camera, JsonObject profile) => profiles[camera] = profile;
    public void RemoveProfile(string camera) => profiles.TryRemove(camera, out _);
    public void ClearProfiles() => profiles.Clear();
}
