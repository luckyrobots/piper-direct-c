using Intel.RealSense;

namespace LuckyRobots.Piper.Camera;

public static class D405Profiles
{
    // Device and profile discovery only: no sensor.Open, pipeline.Start or setting writes.
    public static IReadOnlyList<object> Describe(string selectedSerial)
    {
        using var context = new Context();
        using var devices = context.QueryDevices();
        var result = new List<object>();
        foreach (var device in devices)
        using (device)
        {
            string name = device.Info[CameraInfo.Name], serial = device.Info[CameraInfo.SerialNumber];
            if (!name.Contains("D405", StringComparison.OrdinalIgnoreCase) || selectedSerial.Length > 0 && serial != selectedSerial) continue;
            var profiles = new List<object>();
            foreach (var sensor in device.QuerySensors())
            using (sensor)
            foreach (var profile in sensor.StreamProfiles)
            using (profile)
            {
                if (profile.Stream is not (Intel.RealSense.Stream.Depth or Intel.RealSense.Stream.Color) || !profile.Is(Extension.VideoProfile)) continue;
                using var video = profile.As<VideoStreamProfile>();
                if (video.Width != 640 || video.Height != 480 || profile.Framerate != 15) continue;
                var intr = video.GetIntrinsics();
                profiles.Add(new { stream = profile.Stream.ToString(), format = profile.Format.ToString(), fps = profile.Framerate,
                    width = intr.width, height = intr.height, fx = intr.fx, fy = intr.fy, cx = intr.ppx, cy = intr.ppy,
                    distortion = intr.model.ToString(), coefficients = intr.coeffs });
            }
            result.Add(new { name, serial, profiles });
        }
        return result;
    }
}
