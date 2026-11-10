using System.Diagnostics;
using LuckyRobots.Piper.Plugin;

/// <summary>Plugin-H as a table: every transition of the measured-ghost capability.</summary>
internal static class GhostCapabilityTests
{
    public static void Run()
    {
        long second = Stopwatch.Frequency, start = Stopwatch.GetTimestamp();
        var capability = new GhostCapability(TimeSpan.FromSeconds(5));
        Check(capability.Kind("a") == GhostCapabilityKind.Unknown && capability.Known("a") is null && capability.PublishedVisibility is null,
            "A new ghost capability was not Unknown with no published visibility");

        Check(capability.TryBeginProbe(start) && capability.Kind("a") == GhostCapabilityKind.Probing, "The first probe was not claimed");
        Check(!capability.TryBeginProbe(start + 60 * second), "A second caller probed while a probe was in flight");
        var probing = capability.Current;
        capability.EndProbe(null);
        Check(probing.Probing && capability.Kind("a") == GhostCapabilityKind.Unknown, "A failed probe did not return to Unknown, or changed a published snapshot");

        // One backoff for both callers: the UI toggle and ghost publication share this state.
        Check(!capability.TryBeginProbe(start + 4 * second), "A probe ran inside the backoff after a failed attempt");
        Check(capability.TryBeginProbe(start + 5 * second), "A probe was refused once the backoff elapsed");
        capability.EndProbe(new("a", Ghosts: true, Visibility: false));
        Check(capability.Kind("a") == GhostCapabilityKind.Supported && capability.Known("a") is { Visibility: false },
            "A probe that found ghost support did not reach Supported");

        // A change of backend instance makes the recorded capability Unknown again.
        Check(capability.Kind("b") == GhostCapabilityKind.Unknown && capability.Known("b") is null, "Another backend instance inherited a recorded capability");
        Check(!capability.TryBeginProbe(start + 6 * second), "A backend change bypassed the shared probe backoff");
        Check(capability.TryBeginProbe(start + 10 * second) && capability.Kind("a") == GhostCapabilityKind.Probing,
            "A new probe kept the capability recorded by the previous one");
        capability.EndProbe(new("b", Ghosts: false, Visibility: true));
        Check(capability.Kind("b") == GhostCapabilityKind.Unsupported && capability.Known("b") is { Visibility: true } && capability.Kind("a") == GhostCapabilityKind.Unknown,
            "A probe that found no ghost support did not reach Unsupported for its own backend only");

        capability.PublishVisibility(true);
        Check(capability.PublishedVisibility == true && capability.Kind("b") == GhostCapabilityKind.Unsupported, "Publishing visibility changed the capability");
        Check(capability.TryBeginProbe(start + 20 * second) && capability.PublishedVisibility == true, "A probe forgot the published visibility");
        capability.EndProbe(null);
    }

    /// <summary>However many callers race, exactly one claims the probe.</summary>
    public static void RunOneProbeAtATime()
    {
        int worst = 1;
        for (int round = 0; round < 200; round++)
        {
            var capability = new GhostCapability(TimeSpan.FromSeconds(5));
            long now = Stopwatch.GetTimestamp();
            int claimed = 0;
            Parallel.For(0, 8, _ => { if (capability.TryBeginProbe(now)) Interlocked.Increment(ref claimed); });
            if (claimed != 1) worst = claimed;
        }
        Check(worst == 1, $"{worst} callers claimed one ghost capability probe");
    }
}
