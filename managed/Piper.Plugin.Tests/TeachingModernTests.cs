using System.Text.Json.Nodes;
using LuckyRobots.Piper.Plugin;

internal static class TeachingModernTests
{
    public static void Run()
    {
        JsonObject Device(string strategy = "measured-pose") => new()
        {
            ["fresh"] = true, ["status"] = new JsonObject { ["control"] = 2, ["teach_state"] = 2 },
            ["teaching"] = new JsonObject { ["phase"] = "ended", ["return_required"] = true, ["can_return"] = true,
                ["seamless_supported"] = true, ["return_requires_zero"] = false, ["return_strategy"] = strategy }
        };
        TeachingState modern = TeachingState.Inspect(Device());
        Check(modern.SamePoseReturn && !modern.RequiresZero && modern.CanReturn, "Modern backend capability did not expose measured-pose return");
        Check(modern.ReturnFlags() is { ButtonOff: true, SamePoseConfirmed: true, ZeroPoseConfirmed: null },
            "Modern return mislabeled its request as physical-zero confirmation");
        Check(modern.NextStep.Contains("no need to move to zero") && modern.ReturnConfirmation.Contains("measured pose"), "Modern return still demanded zero");
        var missing = Device(); missing.Remove("teaching");
        Check(TeachingState.Inspect(missing).RequiresZero, "Missing backend capability bypassed the legacy guard");
        Check(TeachingState.Inspect(Device("future-unknown")).RequiresZero, "Unknown handoff strategy bypassed the legacy guard");
        missing = Device(); missing["teaching"]!["seamless_supported"] = false;
        Check(TeachingState.Inspect(missing).RequiresZero, "Unqualified firmware enabled same-pose return");
        var legacy = TeachingState.Inspect(missing);
        Check(legacy.ReturnFlags() is { ButtonOff: true, ZeroPoseConfirmed: true, SamePoseConfirmed: null },
            "Legacy return changed its zero-only API contract");

        TeachingState guiding = modern with { Phase = TeachingPhase.HandGuiding, Active = true, CanReturn = false };
        TeachingScope a = new("can-a", 1), b = new("can-a", 2);
        var session = new TeachingSession();
        session.ObserveHandOver(a, approved: true);
        Check(!session.ObserveReturn(a, modern, true), "Loading an ended controller state issued an automatic return");
        Check(!session.ObserveReturn(a, guiding, true) && session.Current.Phase == TeachingSessionPhase.Guiding, "Entering hand guiding changed controller mode");
        Check(!session.ObserveReturn(a, modern with { CanReturn = false }, true), "Unready handoff triggered a return");
        Check(session.ObserveReturn(a, modern, true) && session.Current.Phase == TeachingSessionPhase.Attempted, "Approved local hand-guiding session did not return when healthy");
        Check(!session.ObserveReturn(a, modern, true), "Failed or accepted return was retried without another session");
        session.Release(); session.ObserveHandOver(a, approved: false);
        session.ObserveReturn(a, guiding, true);
        Check(!session.ObserveReturn(a, modern, true) && session.Current is { Phase: TeachingSessionPhase.Observed, RestoresApproval: false },
            "An unapproved hand-guiding session granted CAN authority");
        session.Release(); session.ObserveHandOver(a, approved: true);
        session.ObserveReturn(a, guiding, true);
        Check(!session.ObserveReturn(b, modern, true) && session.Current is { Scope: var moved, Owned: false } && moved == b,
            "CAN generation change retained automatic authority or the old ownership");
        session.ObserveHandOver(b, approved: true);
        session.ObserveReturn(b, guiding, true);
        Check(!session.ObserveReturn(b, legacy, true), "Legacy firmware returned automatically without its explicit zero flow");
        session.ObserveReturn(b, guiding, true);
        session.ObserveReturn(b, modern with { Phase = TeachingPhase.Playback, Active = true }, true);
        Check(!session.ObserveReturn(b, modern, true), "Onboard playback completion was mistaken for approved hand guiding");
        session.ObserveReturn(b, guiding, true);
        session.ObserveReturn(b, modern, false);
        Check(!session.ObserveReturn(b, modern, true), "Leaving Controls preserved an automatic mode-change intent");
        session.ObserveReturn(b, guiding, true);
        session.ObserveReturn(b, TeachingState.Inspect(null), true);
        Check(!session.ObserveReturn(b, modern, true), "A lost status stream retained automatic mode-change authority");
        session.ObserveReturn(b, guiding, true);
        session.InvalidateReturn(); // Reader-side disconnect may occur between rendered polls.
        Check(!session.ObserveReturn(b, modern, true) && session.Current is { Phase: TeachingSessionPhase.Observed, RestoresApproval: true },
            "A stream reconnect between UI polls retained automatic return authority or dropped ownership");
        Check(!session.ObserveHandOver(b, approved: false) && session.Current.RestoresApproval, "A repeated hand-over observation replaced the approval kept at the first");
        session.WithdrawApproval();
        Check(session.Current is { Owned: true, RestoresApproval: false }, "A read-only workspace kept the approval recorded at the hand-over");
        session.Track(a);
        Check(session.Current is { Owned: false, Phase: TeachingSessionPhase.Idle } && session.Current.Scope == a, "A different CAN device inherited teaching ownership");
    }
}
