namespace LuckyRobots.Piper.Plugin;

/// <summary>One bounded return attempt following a locally observed, approved
/// hand-guiding session. Loading an ended state never grants this authority.</summary>
/// <remarks>Plugin-D auto-return, per connection identity:
/// Idle → Guiding (approval was absent) or GuidingAuthorized (approved before teaching);
/// GuidingAuthorized → Released when the light turns off; Released → Attempted on the one
/// eligible same-pose return. Guiding releases to Idle. Leaving Controls, Off, Playback,
/// Unknown, a new identity and Invalidate return to Idle; HandGuiding restarts a session.</remarks>
public sealed class TeachingReturnGate
{
    private enum Phase { Idle, Guiding, GuidingAuthorized, Released, Attempted }
    private readonly object sync = new();
    private string identity = "";
    private Phase phase;

    public void Invalidate()
    {
        lock (sync) { identity = ""; phase = Phase.Idle; }
    }

    public bool Observe(TeachingState state, bool approvedBeforeTeaching, bool controlsPage, string connectionIdentity)
    {
        lock (sync)
        {
            if (identity != connectionIdentity) { identity = connectionIdentity; phase = Phase.Idle; }
            if (!controlsPage || state.Phase is TeachingPhase.Unknown or TeachingPhase.Off or TeachingPhase.Playback or TeachingPhase.PlaybackPaused)
            {
                phase = Phase.Idle;
                return false;
            }
            if (state.HandGuiding)
            {
                if (phase is not (Phase.Guiding or Phase.GuidingAuthorized))
                    phase = approvedBeforeTeaching ? Phase.GuidingAuthorized : Phase.Guiding;
                return false;
            }
            phase = phase switch { Phase.Guiding => Phase.Idle, Phase.GuidingAuthorized => Phase.Released, var unchanged => unchanged };
            if (phase != Phase.Released || !state.SamePoseReturn || state.Phase != TeachingPhase.Ended || !state.CanReturn)
                return false;
            phase = Phase.Attempted; // A rejected request is not retried on the next poll.
            return true;
        }
    }
}
