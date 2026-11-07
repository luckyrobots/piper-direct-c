namespace LuckyRobots.Piper.Plugin;

/// <summary>One bounded return attempt following a locally observed, approved
/// hand-guiding session. Loading an ended state never grants this authority.</summary>
public sealed class TeachingReturnGate
{
    private readonly object sync = new();
    private string identity = "";
    private bool guiding, inGuiding, authorized, attempted;

    public void Invalidate()
    {
        lock (sync) { identity = ""; guiding = inGuiding = authorized = attempted = false; }
    }

    public bool Observe(TeachingState state, bool approvedBeforeTeaching, bool controlsPage, string connectionIdentity)
    {
        lock (sync) return ObserveLocked(state, approvedBeforeTeaching, controlsPage, connectionIdentity);
    }

    private bool ObserveLocked(TeachingState state, bool approvedBeforeTeaching, bool controlsPage, string connectionIdentity)
    {
        if (identity != connectionIdentity)
        {
            identity = connectionIdentity;
            guiding = inGuiding = authorized = attempted = false;
        }
        if (!controlsPage || state.Phase is "unknown" or "off" or "playback" or "playback-paused")
        {
            guiding = inGuiding = authorized = false;
            return false;
        }
        if (state.HandGuiding)
        {
            if (!inGuiding) { authorized = approvedBeforeTeaching; attempted = false; }
            guiding = inGuiding = true;
            return false;
        }
        inGuiding = false;
        if (!guiding || !authorized || attempted || !state.SamePoseReturn || state.Phase != "ended" || !state.CanReturn)
            return false;
        attempted = true; // A rejected request is not retried on the next poll.
        return true;
    }
}
