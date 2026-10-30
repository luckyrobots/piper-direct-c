using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class CameraProfile
{
    public static bool Matches(JsonObject profile, string device, int width, int height, bool depth)
    {
        try
        {
            bool Finite(string key, double min, double max) => profile[key]?.GetValue<double>() is double v && double.IsFinite(v) && v >= min && v <= max;
            var coefficients = profile["coefficients"]?.AsArray();
            return profile["schema"]?.GetValue<int>() == 1 && profile["device"]?.GetValue<string>() == device
                && profile["width"]?.GetValue<int>() == width && profile["height"]?.GetValue<int>() == height
                && profile["model"]?.GetValue<string>() == (depth ? "pinhole" : "kannala-brandt")
                && Finite("fx", 1, width * 20) && Finite("fy", 1, height * 20)
                && Finite("cx", 0, width - 1) && Finite("cy", 0, height - 1)
                && coefficients?.Count == (depth ? 5 : 4)
                && coefficients.All(c => c?.GetValue<double>() is double v && double.IsFinite(v) && (depth ? Math.Abs(v) < 1e-8 : Math.Abs(v) < 10));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
    }
}
