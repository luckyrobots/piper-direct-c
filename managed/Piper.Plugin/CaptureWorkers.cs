using System.Collections.Concurrent;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>The pixel size of a camera's frames.</summary>
internal sealed record CaptureSize(int Width, int Height);

/// <summary>The Cal-G part of the Calibration component: the camera worker
/// behind each preview, and what each preview last said about itself. One
/// worker per camera at most; its phase is a <see cref="CaptureWorkerPhase"/>.
/// <code>
///   field    written by                                            readers see
///   slots    the UI actor and plugin start (Restart, Stop) and     the worker now in a slot, or none
///            plugin disposal (Retire). Changes are serialized,     while it is being replaced
///            and a replacement starts only after the earlier
///            worker has ended.
///   status   the UI actor (Connecting) and the camera in that      a concurrent map of strings
///            slot (Report)
///   sizes    the camera in that slot (Sized); the UI actor         a concurrent map of immutable
///            (Connecting) removes one                              sizes
/// </code>
/// A camera with no worker has the phase NotStarted. After <see cref="Retire"/>
/// no worker starts again.
///
/// Not owned here: the camera settings (preferences), the worker's arguments
/// and where its frames and lens profile go (the plugin builds the worker),
/// and the lens profiles themselves (Cal-E).</summary>
internal sealed class CaptureWorkers
{
    private readonly CameraPreviewSlots<CameraCapture> slots = new();
    private readonly ConcurrentDictionary<string, string> status = new();
    private readonly ConcurrentDictionary<string, CaptureSize> sizes = new();

    public CaptureWorkerPhase Phase(string camera) => slots.Get(camera)?.Phase ?? CaptureWorkerPhase.NotStarted;
    /// <summary>The newest frame the camera's worker read.</summary>
    public CameraFrame? LatestFrame(string camera) => slots.Get(camera)?.LatestFrame;
    /// <summary>The newest frame the engine accepted.</summary>
    public CameraFrame? PublishedFrame(string camera) => slots.Get(camera)?.PublishedFrame;
    public string? Status(string camera) => status.GetValueOrDefault(camera);
    public CaptureSize? Size(string camera) => sizes.GetValueOrDefault(camera);

    /// <summary>Ends the camera's worker, then builds and starts its
    /// replacement. Nothing is built once the workers are retired.</summary>
    public Task Restart(string camera, Func<CameraCapture> build) => slots.ReplaceAsync(camera, () =>
    {
        var capture = build();
        capture.Start();
        return capture;
    });

    /// <summary>Ends the camera's worker and leaves its slot empty.</summary>
    public Task Stop(string camera) => slots.ReplaceAsync(camera, startReplacement: null);

    /// <summary>A camera whose next frames come from different settings: its
    /// last frame size no longer describes it.</summary>
    public void Connecting(string camera, string text)
    {
        sizes.TryRemove(camera, out _);
        status[camera] = text;
    }

    public void Report(string camera, string text) => status[camera] = text;
    public void Sized(string camera, int width, int height) => sizes[camera] = new(width, height);

    /// <summary>Plugin shutdown: closes admission at once, then ends every worker.</summary>
    public Task Retire() => slots.DisposeAsync().AsTask();
}
