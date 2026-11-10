using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>B1: one pending operator question, "Has the controller reset or power
/// cycle completed?", pinned to the robot connection and permission epoch that
/// raised it. Only <see cref="PipersPlugin.ConfirmResetCompletion"/> consumes it, and
/// only a consumed prompt lets <c>verify-reset</c> carry <c>reset_completed</c>.
/// No recovery runner attests a reset on the operator's behalf.</summary>
internal sealed record ResetCompletionPrompt(RobotCommandScope Scope, long Epoch);

public sealed partial class PipersPlugin
{
    internal const string ResetCompletionQuestion = "Has the controller reset or power cycle completed?";
    internal ResetCompletionPrompt? resetCompletionPrompt;
    private ResetCompletionPrompt? PendingResetCompletion => Volatile.Read(ref resetCompletionPrompt);

    /// <summary>True while the prompt still belongs to this backend, robot and
    /// permission epoch. STOP or a connection change makes it inert and hidden.</summary>
    private bool ResetCompletionCurrent(ResetCompletionPrompt prompt) =>
        prompt.Epoch == CommandAuthority.Epoch && prompt.Scope.Backend == token && prompt.Scope.ProfileId == ActiveProfile.Id;

    private Widget ResetCompletionConfirmation() => Widget.Row("recovery.reset",
        Widget.Label("recovery.reset.text", ResetCompletionQuestion),
        Widget.Button("recovery.reset.confirm", "Confirm") with { Appearance = "primary",
            Hint = "Confirm only after the controller reset or power cycle has finished, with the arm supported and stationary." },
        Widget.Button("recovery.reset.cancel", "Cancel")) with
        { Visible = PendingResetCompletion is { } prompt && ResetCompletionCurrent(prompt), Padding = 12, Appearance = "panel" };

    private void AddResetCompletionPatches(Dictionary<string, JsonObject> patches) =>
        patches["recovery.reset"] = new() { ["visible"] = PendingResetCompletion is { } prompt && ResetCompletionCurrent(prompt) };

    /// <summary>Stops at the operator question instead of verifying the reset.</summary>
    internal void RequestResetCompletion(RobotCommandScope scope, long epoch)
    {
        if (!CommandAuthority.TryInEpoch(epoch, () => Volatile.Write(ref resetCompletionPrompt, new ResetCompletionPrompt(scope, epoch))))
            return; // STOP already retired the recovery that asked.
        message = "Controller reset pending · " + ResetCompletionQuestion + " Confirm or Cancel above.";
    }

    /// <summary>Consumes the prompt exactly once and sends the one <c>verify-reset</c>
    /// that carries the operator's reset attestation.</summary>
    internal async Task ConfirmResetCompletion(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref resetCompletionPrompt, null) is not { } prompt)
            throw new InvalidOperationException("No reset confirmation is pending. Use the recovery card for the current step.");
        if (!ResetCompletionCurrent(prompt))
            throw new InvalidOperationException("STOP or a connection change retired the reset confirmation. Nothing was sent; use the recovery card again.");
        // The answer attests one specific pending reset. The controller clears any
        // latch on an attested verification, so a latch that is no longer a pending
        // reset (verified elsewhere, or replaced by a new STOP) must not receive it.
        if (Device(prompt.Scope)["reset_pending"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("The controller no longer reports a pending reset. Nothing was sent; use the recovery card for the current step.");
        await Command("verify-reset", null, cancellation, CommandPurpose.Recovery, prompt.Scope, prompt.Epoch, resetCompletion: prompt);
        const string requested = "Controller reset verification requested. Use the recovery card for the next step; nothing moves automatically.";
        SetRecoveryGuideMessage(requested);
        message = requested;
    }

    /// <summary>Cancel closes the question and sends nothing.</summary>
    internal void CancelResetCompletion()
    {
        if (Interlocked.Exchange(ref resetCompletionPrompt, null) is not null)
            message = "Reset confirmation cancelled. No recovery command was sent.";
    }
}
