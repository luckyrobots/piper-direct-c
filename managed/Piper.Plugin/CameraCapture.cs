using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

public sealed record CameraPreferences(string Device, bool Enabled = true, string VideoSize = "", int FrameRate = 0, string InputCodec = "");

/// <summary>Preview acquisition via the bundled C# camera worker. Timestamps are host receipt times, not exposure triggers.</summary>
public sealed class CameraCapture(PluginContext engine, string imageId,
    CameraPreferences settings, Action<string, string> status, string depthSerial = "",
    Action<string, int, int>? dimensions = null, Action<string, JsonObject>? profile = null, string? outputImageId = null) : IAsyncDisposable
{
    public CameraFrame? LatestFrame => Volatile.Read(ref latestFrame);
    public CameraFrame? PublishedFrame => Volatile.Read(ref publishedFrame);
    private CameraFrame? latestFrame;
    private CameraFrame? publishedFrame;
    private readonly CancellationTokenSource stopping = new();
    private Task? worker;
    private long sequence;
    public void Start() => worker = Task.Run(() => Run(stopping.Token));

    private async Task Run(CancellationToken cancellation)
    {
        if (!settings.Enabled) { status(imageId, "Stopped"); return; }
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                bool depth = imageId == "depth";
                var info = CameraWorker.StartInfo(engine.Start.PackageDirectory);
                string[] arguments = depth
                    ? ["depth", "--serial", depthSerial]
                    : ["color", "--device", settings.Device, "--size", settings.VideoSize,
                        "--fps", settings.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "--codec", settings.InputCodec];
                foreach (string argument in arguments) info.ArgumentList.Add(argument);
                using var process = Process.Start(info) ?? throw new IOException("Could not start camera capture.");
                process.StandardInput.Close();
                using var stop = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                var errors = DrainErrors(process.StandardError, cancellation);
                // Capture must keep draining even if the engine's pipe is busy. There is one
                // replaceable pending preview, never a queue of old camera frames.
                var latest = Channel.CreateBounded<CameraFrame>(new BoundedChannelOptions(1)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
                using var publishing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var publish = PublishLatest(latest.Reader, publishing.Token);
                // A broken output pipe must also release the camera; otherwise capture can
                // run indefinitely after its only consumer has failed.
                var stopCapture = publish.ContinueWith(_ =>
                {
                    try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    try { await publish; } catch (OperationCanceledException) { }
                    await stopCapture;
                    try { await errors; } catch (OperationCanceledException) { }
                }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            {
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
        await stopping.CancelAsync();
        if (worker is not null) { try { await worker; } catch (OperationCanceledException) { } }
        stopping.Dispose();
    }
}
