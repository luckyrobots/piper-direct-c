using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Processes;

namespace LuckyRobots.Piper.Plugin;

public sealed record CameraPreferences(string Device, bool Enabled = true, string VideoSize = "", int FrameRate = 0, string InputCodec = "", string ViewMode = "color");

public static class D405View
{
    public static string Mode(CameraPreferences? settings) => settings?.ViewMode == "depth" ? "depth" : "color";
    public static bool MetricDepth(string id, CameraPreferences settings) => id == "depth" && Mode(settings) == "depth";
    public static CameraPreferences Select(CameraPreferences settings, int index) => index is 0 or 1
        ? settings with { ViewMode = index == 0 ? "color" : "depth" }
        : throw new ArgumentOutOfRangeException(nameof(index), "Choose Color or Depth.");
}

/// <summary>Launches the bundled camera worker, the only process that opens
/// cameras: as a bounded tool run, or as a long-lived capture worker whose
/// lifecycle <see cref="CameraCapture"/> owns. Both go through
/// <see cref="BoundedProcess"/>, with stdin a closed pipe of their own so the
/// worker never shares the engine's live plugin-protocol pipe.</summary>
internal static class CameraRuntime
{
    private const string StartFailure = "Could not start the bundled camera runtime.";

    /// <summary>Runs one camera tool to completion under a deadline. Only the
    /// last 1000 characters of its error stream are kept.</summary>
    public static async Task<BoundedProcess.Result> RunAsync(string packageDirectory, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellation)
    {
        var (folder, executable) = Locate(packageDirectory);
        var result = await BoundedProcess.RunAsync(executable, arguments, StartFailure,
            "Camera operation timed out; its worker was stopped.", cancellation, timeout, folder);
        return result with { Error = result.Error.Length > 1000 ? result.Error[^1000..] : result.Error };
    }

    /// <summary>Starts a capture worker. The caller reads both output streams and retires it.</summary>
    public static Process Start(string packageDirectory, IEnumerable<string> arguments)
    {
        var (folder, executable) = Locate(packageDirectory);
        var process = BoundedProcess.Start(BoundedProcess.Redirected(executable, arguments, folder), StartFailure);
        process.StandardInput.Close();
        return process;
    }

    /// <summary>Stops the worker and its children. Safe to call from any path, including after it exited.</summary>
    public static void Kill(Process process) => BoundedProcess.KillTree(process);

    /// <summary>Kills the worker and waits for it, so no camera handle outlives the caller.</summary>
    public static async Task Retire(Process process)
    {
        Kill(process);
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static (string Folder, string Executable) Locate(string packageDirectory)
    {
        string folder = Path.Combine(packageDirectory, "camera-runtime");
        string executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "LuckyRobots.Piper.Camera.exe" : "LuckyRobots.Piper.Camera");
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled camera runtime is missing. Reinstall the Pipers package.", executable);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        return (folder, executable);
    }
}

/// <summary>Cal-G: where one capture worker is in its life.
/// <code>
///   NotStarted -> Running     Start, once; a second Start throws
///   Running -> Retrying       the worker process failed or could not be launched
///   Retrying -> Running       the next launch attempt, three seconds later
///   any -> Stopping           DisposeAsync: the process is being killed and joined
///   Stopping -> Disposed      the worker task has ended; Start throws from here on
/// </code>
/// A capture whose preview is switched off is Running without a process.</summary>
internal enum CaptureWorkerPhase { NotStarted, Running, Retrying, Stopping, Disposed }

/// <summary>Preview acquisition via the bundled C# camera worker. Timestamps are host receipt times, not exposure triggers.
/// <code>
///   field           written by                                       readers see
///   phase           Start and DisposeAsync (their callers), and the  one volatile value
///                   worker task (Retrying, back to Running), all
///                   under phaseGate; the worker never leaves
///                   Stopping or Disposed
///   latestFrame     the worker task                                  one volatile reference to an
///   publishedFrame  the worker's publisher                           immutable frame each
/// </code></summary>
public sealed class CameraCapture(PluginContext engine, string imageId,
    CameraPreferences settings, Action<string, string> status, string depthSerial = "",
    Action<string, int, int>? dimensions = null, Action<string, JsonObject>? profile = null, string? outputImageId = null) : IAsyncDisposable
{
    internal CaptureWorkerPhase Phase => phase;
    public CameraFrame? LatestFrame => Volatile.Read(ref latestFrame);
    public CameraFrame? PublishedFrame => Volatile.Read(ref publishedFrame);
    private CameraFrame? latestFrame;
    private CameraFrame? publishedFrame;
    private readonly CancellationTokenSource stopping = new();
    private readonly object phaseGate = new();
    private volatile CaptureWorkerPhase phase;
    private Task? worker;
    private long sequence;

    public void Start()
    {
        lock (phaseGate)
        {
            if (phase != CaptureWorkerPhase.NotStarted)
                throw new InvalidOperationException(phase is CaptureWorkerPhase.Stopping or CaptureWorkerPhase.Disposed
                    ? "This camera capture was disposed." : "This camera capture is already running.");
            phase = CaptureWorkerPhase.Running;
            worker = Task.Run(() => Run(stopping.Token));
        }
    }

    /// <summary>The worker's own transitions. A capture being stopped keeps its phase.</summary>
    private void Advance(CaptureWorkerPhase from, CaptureWorkerPhase to)
    {
        lock (phaseGate) if (phase == from) phase = to;
    }

    private async Task Run(CancellationToken cancellation)
    {
        if (!settings.Enabled) { status(imageId, "Stopped"); return; }
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                Advance(CaptureWorkerPhase.Retrying, CaptureWorkerPhase.Running);
                string[] arguments = imageId == "depth"
                    ? ["d405", "--serial", depthSerial, "--view", D405View.Mode(settings)]
                    : ["color", "--device", settings.Device, "--size", settings.VideoSize,
                        "--fps", settings.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "--codec", settings.InputCodec];
                using var process = CameraRuntime.Start(engine.Start.PackageDirectory, arguments);
                using var stop = cancellation.Register(() => CameraRuntime.Kill(process));
                var errors = DrainErrors(process.StandardError, cancellation);
                // Capture must keep draining even if the engine's pipe is busy. There is one
                // replaceable pending preview, never a queue of old camera frames.
                var latest = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
                using var publishing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var publish = PublishLatest(latest.Reader, publishing.Token);
                // A broken output pipe must also release the camera; otherwise capture can
                // run indefinitely after its only consumer has failed.
                var stopCapture = publish.ContinueWith(_ => CameraRuntime.Kill(process),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try
                {
                    byte[] block = new byte[16384];
                    using var frame = new MemoryStream();
                    int previous = 0, count;
                    bool inside = false;
                    while ((count = await process.StandardOutput.BaseStream.ReadAsync(block, cancellation)) > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            byte current = block[i];
                            if (!inside && previous == 0xff && current == 0xd8) { frame.SetLength(0); frame.WriteByte(0xff); inside = true; }
                            if (inside) frame.WriteByte(current);
                            if (frame.Length > 4 * 1024 * 1024) throw new InvalidDataException("Camera frame is too large.");
                            if (inside && previous == 0xff && current == 0xd9)
                            {
                                inside = false;
                                long timestamp = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                                var size = JpegDimensions.Read(frame.GetBuffer().AsSpan(0, (int)frame.Length));
                                dimensions?.Invoke(imageId, size.Width, size.Height);
                                var captured = new CameraFrame(outputImageId ?? imageId, size.Width, size.Height, Convert.ToBase64String(frame.GetBuffer(), 0, (int)frame.Length), ++sequence, timestamp, "host-utc-receipt", settings.Device);
                                Volatile.Write(ref latestFrame, captured);
                                latest.Writer.TryWrite(captured);
                            }
                            previous = current;
                        }
                    }
                    await process.WaitForExitAsync(cancellation);
                    throw new IOException("Camera unavailable: " + await errors);
                }
                finally
                {
                    latest.Writer.TryComplete();
                    await publishing.CancelAsync();
                    await CameraRuntime.Retire(process);
                    try { await publish; } catch (OperationCanceledException) { }
                    await stopCapture;
                    try { await errors; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
                Advance(CaptureWorkerPhase.Running, CaptureWorkerPhase.Retrying);
                status(imageId, (error.Message.Length > 120 ? error.Message[..120] + "…" : error.Message) + " · retrying");
                await Task.Delay(3000, cancellation);
            }
        }
    }

    private async Task PublishLatest(ChannelReader<CameraFrame> frames, CancellationToken cancellation)
    {
        long previous = Stopwatch.GetTimestamp(), sent = 0;
        double rate = 0;
        while (await frames.WaitToReadAsync(cancellation))
        {
            if (!frames.TryRead(out var frame)) continue;
            while (frames.TryRead(out var newer)) frame = newer;
            long sendStarted = Stopwatch.GetTimestamp();
            await engine.PublishFrameAsync(frame, cancellation);
            Volatile.Write(ref publishedFrame, frame);
            ++sent;
            double elapsed = Stopwatch.GetElapsedTime(previous).TotalSeconds;
            if (elapsed >= 1) { rate = sent / elapsed; sent = 0; previous = Stopwatch.GetTimestamp(); }
            double age = Math.Max(0, (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10000.0 - frame.TimestampUnixMicroseconds / 1000.0);
            status(imageId, $"{frame.Width}×{frame.Height} · {rate:F0} fps · pipe {age:F0} ms (exposure delay unmeasured)");
            // Bound preview bandwidth without making the capture reader wait.
            // Leave headroom below a 30 fps camera period: Windows' timer granularity
            // can round a 33 ms wait to about 47 ms, unnecessarily reducing preview rate.
            int delay = Math.Max(0, 25 - (int)Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds);
            if (delay > 0) await Task.Delay(delay, cancellation);
        }
    }

    private async Task<string> DrainErrors(StreamReader reader, CancellationToken cancellation)
    {
        var recent = new Queue<string>();
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (line.StartsWith("PIPER_CAMERA_PROFILE ", StringComparison.Ordinal))
            {
                var value = JsonNode.Parse(line[21..])!.AsObject();
                profile?.Invoke(imageId, value);
                continue;
            }
            recent.Enqueue(line.Length > 512 ? line[..512] : line);
            if (recent.Count > 6) recent.Dequeue();
        }
        return string.Join(" ", recent);
    }

    public async ValueTask DisposeAsync()
    {
        lock (phaseGate)
        {
            if (phase is CaptureWorkerPhase.Stopping or CaptureWorkerPhase.Disposed) return;
            phase = CaptureWorkerPhase.Stopping;
        }
        await stopping.CancelAsync();
        try { if (worker is not null) await worker; }
        catch (OperationCanceledException) { }
        // A worker that faulted while ending is still ended; its replacement must start.
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Camera worker {0} faulted while stopping: {1}", imageId, error.Message); }
        finally
        {
            lock (phaseGate) phase = CaptureWorkerPhase.Disposed;
            stopping.Dispose();
        }
    }
}
