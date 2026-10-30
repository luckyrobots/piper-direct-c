using OpenCvSharp;

namespace LuckyRobots.Piper.Camera;

public static class ColorPreview
{
    public static async Task Run(string device, string size, int fps, string codec, CancellationToken cancellation)
    {
        using var capture = new VideoCapture();
        bool opened;
        if (OperatingSystem.IsWindows())
        {
            var matches = VideoDevices.List().Where(d => d.Path == device || d.Name == device).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Choose one camera by name or unique device path; found " + matches.Length + " matches for " + device);
            opened = capture.Open(matches[0].Index, VideoCaptureAPIs.DSHOW);
            var current = VideoDevices.List().SingleOrDefault(d => d.Index == matches[0].Index);
            if (current is null || current.Name != matches[0].Name || current.Path != matches[0].Path)
                throw new IOException("Camera order changed while connecting; retrying device discovery is required.");
        }
        else opened = capture.Open(device, VideoCaptureAPIs.V4L2);
        if (!opened) throw new IOException("Camera could not be opened: " + device);
        // DirectShow holds one pending sample. Consume it continuously and avoid
        // adding another frame queue; the plugin keeps draining this output pipe.
        if (codec.Equals("mjpeg", StringComparison.OrdinalIgnoreCase)) capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        var dimensions = size.Split('x');
        if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height))
        {
            capture.Set(VideoCaptureProperties.FrameWidth, width);
            capture.Set(VideoCaptureProperties.FrameHeight, height);
        }
        if (fps > 0) capture.Set(VideoCaptureProperties.Fps, fps);
        capture.Set(VideoCaptureProperties.BufferSize, 1);
        using var frame = new Mat();
        using var preview = new Mat();
        using var output = Console.OpenStandardOutput();
        while (!cancellation.IsCancellationRequested)
        {
            if (!capture.Read(frame) || frame.Empty()) throw new IOException("Camera stopped delivering frames: " + device);
            double scale = Math.Min(640.0 / frame.Cols, 480.0 / frame.Rows);
            Cv2.Resize(frame, preview, new Size(Math.Max(1, (int)Math.Round(frame.Cols * scale)), Math.Max(1, (int)Math.Round(frame.Rows * scale))), interpolation: InterpolationFlags.Area);
            Cv2.ImEncode(".jpg", preview, out var jpeg, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)]);
            await output.WriteAsync(jpeg, cancellation);
            await output.FlushAsync(cancellation);
        }
    }
}
