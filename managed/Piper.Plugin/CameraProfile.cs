using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public static class CameraProfile
{
    public static bool MatchesD405(JsonObject profile, string device, int width, int height, string streamKind)
    {
        try
        {
            // The current engine pinhole view does not apply Brown coefficients.
            // Color is still shown, but distorted factory lenses stay unverified.
            return streamKind is "color" or "depth" && profile["streamKind"]?.GetValue<string>() == streamKind &&
                Matches(profile, device, width, height, true) &&
                (streamKind == "color" || profile["depthScale"]?.GetValue<double>() is double scale && double.IsFinite(scale) && scale is >= 1e-6 and <= .01);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException) { return false; }
    }

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
