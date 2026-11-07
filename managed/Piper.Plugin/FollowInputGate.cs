using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Rejects queued, reordered and completed viewport gestures before they reach hardware.</summary>
public sealed class FollowInputGate
{
    private ulong lastGesture;
    private long sequence;
    public string? Active { get; private set; }

    public void Accept(JsonObject input, long nowMilliseconds)
    {
        string phase = input["phase"]?.GetValue<string>() ?? "";
        string gesture = input["gesture"]?.GetValue<string>() ?? "";
        long next = input["sequence"]?.GetValue<long>() ?? 0;
        if (!ulong.TryParse(gesture, out ulong number) || number == 0 || next <= 0)
            throw new InvalidOperationException("Invalid viewport gesture.");
        if (phase == "end" && Active == gesture)
        {
            End(); // A delayed release must still stop following.
            return;
        }
        long timestamp = input["timestampUnixMilliseconds"]?.GetValue<long>() ?? 0;
        if (timestamp < nowMilliseconds - 150 || timestamp > nowMilliseconds + 50)
            throw new InvalidOperationException("Viewport input delayed; following paused. Start a new drag.");
        if (phase == "begin" && Active is null && number > lastGesture)
        {
            lastGesture = number; Active = gesture; sequence = next;
            return;
        }
        if (phase is not ("update" or "commit") || Active != gesture || next <= sequence)
            throw new InvalidOperationException("Discarded an old or inactive viewport gesture.");
        sequence = next;
        if (phase == "commit") End();
    }

    public void End() => Active = null;
}
