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
                Matches(profile, device, width, height, "pinhole", 5, v => Math.Abs(v) < 1e-8) &&
                (streamKind == "color" || profile["depthScale"]?.GetValue<double>() is double scale && double.IsFinite(scale) && scale is >= 1e-6 and <= .01);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException) { return false; }
    }

    /// <summary>A solved Kannala-Brandt fisheye profile for this camera and resolution.</summary>
    public static bool MatchesFisheye(JsonObject profile, string device, int width, int height)
    {
        try { return Matches(profile, device, width, height, "kannala-brandt", 4, v => Math.Abs(v) < 10); }
        catch (Exception error) when (error is InvalidOperationException or FormatException) { return false; }
    }

    private static bool Matches(JsonObject profile, string device, int width, int height, string model, int coefficientCount, Func<double, bool> coefficientValid)
    {
        bool Finite(string key, double min, double max) => profile[key]?.GetValue<double>() is double v && double.IsFinite(v) && v >= min && v <= max;
        var coefficients = profile["coefficients"]?.AsArray();
        return profile["schema"]?.GetValue<int>() == 1 && profile["device"]?.GetValue<string>() == device
            && profile["width"]?.GetValue<int>() == width && profile["height"]?.GetValue<int>() == height
            && profile["model"]?.GetValue<string>() == model
            && Finite("fx", 1, width * 20) && Finite("fy", 1, height * 20)
            && Finite("cx", 0, width - 1) && Finite("cy", 0, height - 1)
            && coefficients?.Count == coefficientCount
            && coefficients.All(c => c?.GetValue<double>() is double v && double.IsFinite(v) && coefficientValid(v));
    }
}
