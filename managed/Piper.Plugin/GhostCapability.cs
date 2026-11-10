using System.Diagnostics;

namespace LuckyRobots.Piper.Plugin;

/// <summary>What one probe of the engine host recorded, for one backend instance.</summary>
internal sealed record GhostCapabilities(string Backend, bool Ghosts, bool Visibility);

/// <summary>Plugin-H for the current backend instance.
/// <code>
///   Unknown --TryBeginProbe (backoff elapsed)--> Probing
///   Probing --EndProbe(result)--> Supported | Unsupported
///   Probing --EndProbe(null): failed, or the backend changed meanwhile--> Unknown
///   Supported | Unsupported --a different backend instance is current--> Unknown
/// </code></summary>
internal enum GhostCapabilityKind { Unknown, Probing, Supported, Unsupported }

/// <summary>The whole of Plugin-H at one instant.</summary>
internal sealed record GhostCapabilityState(bool Probing, GhostCapabilities? Recorded, long AttemptAt, bool? PublishedVisibility)
{
    public static readonly GhostCapabilityState Initial = new(false, null, 0, null);

    /// <summary>The recorded capability, unless it was probed under another backend instance.</summary>
    public GhostCapabilities? Known(string backend) => Recorded is { } recorded && recorded.Backend == backend ? recorded : null;

    public GhostCapabilityKind Kind(string backend) =>
        Known(backend) is { } known ? known.Ghosts ? GhostCapabilityKind.Supported : GhostCapabilityKind.Unsupported
        : Probing ? GhostCapabilityKind.Probing : GhostCapabilityKind.Unknown;
}

/// <summary>Plugin-H's single writer: one immutable <see cref="GhostCapabilityState"/>
/// behind one reference, changed only by compare-and-swap, so no caller locks or waits.
/// <code>
///   caller          thread                                        transition
///   TryBeginProbe   telemetry processing (ghost publication),     Unknown to Probing; one probe at a
///                   UI actor (the visibility toggle)              time and one backoff for both callers
///   EndProbe        whichever of those began the probe            Probing to the result, or to Unknown
///   PublishVisibility  the same two                               what the engine host was last told
/// </code>
/// Visibility is only ever read from a recorded capability, so it cannot be known
/// while ghost support is Unknown or Probing.</summary>
internal sealed class GhostCapability(TimeSpan backoff)
{
    private GhostCapabilityState state = GhostCapabilityState.Initial;

    public GhostCapabilityState Current => Volatile.Read(ref state);
    public GhostCapabilities? Known(string backend) => Current.Known(backend);
    public GhostCapabilityKind Kind(string backend) => Current.Kind(backend);
    /// <summary>The visibility last acknowledged by the engine host; null until one was.</summary>
    public bool? PublishedVisibility => Current.PublishedVisibility;

    private bool Swap(GhostCapabilityState seen, GhostCapabilityState next) => ReferenceEquals(Interlocked.CompareExchange(ref state, next, seen), seen);

    /// <summary>Claims the one probe. False while another probe is in flight or the
    /// backoff since the previous attempt has not elapsed at <paramref name="nowTicks"/>.</summary>
    public bool TryBeginProbe(long nowTicks)
    {
        while (true)
        {
            var seen = Current;
            if (seen.Probing || seen.AttemptAt != 0 && Stopwatch.GetElapsedTime(seen.AttemptAt, nowTicks) < backoff) return false;
            if (Swap(seen, seen with { Probing = true, Recorded = null, AttemptAt = nowTicks })) return true;
        }
    }

    /// <summary>Ends the claimed probe with what it recorded, or with nothing.</summary>
    public void EndProbe(GhostCapabilities? recorded)
    {
        GhostCapabilityState seen;
        do seen = Current; while (!Swap(seen, seen with { Probing = false, Recorded = recorded }));
    }

    public void PublishVisibility(bool visible)
    {
        GhostCapabilityState seen;
        do seen = Current; while (!Swap(seen, seen with { PublishedVisibility = visible }));
    }

    /// <summary>Lets the next <see cref="TryBeginProbe"/> run without waiting out the backoff.</summary>
    internal void ExpireBackoff()
    {
        GhostCapabilityState seen;
        do seen = Current; while (!Swap(seen, seen with { AttemptAt = 0 }));
    }
}
