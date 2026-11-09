using System.Text.Json;
using LuckyRobots.Piper.Pika;

namespace LuckyRobots.Piper.Host;

internal static class CoordinatedStopRequest
{
    internal const string Route = "/api/stop/coordinated";

    internal static Task<CoordinatedStopResult> ExecuteAsync(ControllerService controller, PikaSessionRegistry pika,
        JsonElement input)
    {
        // Clone the payload: request cancellation/disposal must not terminate the
        // second stop attempt. Existing owner/device/generation guards still apply.
        JsonElement command = input.Clone();
        string body = command.GetRawText();
        return CoordinatedStop.RunAsync(
            _ =>
            {
                var arm = controller.Execute("/api/stop", body);
                return Task.FromResult(new StopChannelResult(arm.Status is >= 200 and < 300, arm.Json));
            },
            async cancellation =>
            {
                bool hasArm = command.TryGetProperty("device_id", out var arm);
                if (hasArm && arm.ValueKind != JsonValueKind.String)
                    return new(false, "Pika hold unverified: device_id must be an explicit adapter identity.");
                bool scoped = hasArm && arm.GetString()!.Length > 0;
                if (!scoped && (command.TryGetProperty("generation", out _) || command.TryGetProperty("request_id", out _)))
                    return new(false, "A scoped Pika hold requires its explicit arm and gripper identities.");
                if (scoped && (!command.TryGetProperty("pika_device", out var gripper) ||
                    gripper.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(gripper.GetString())))
                    return new(false, "A scoped STOP requires an explicit pika_device to verify the gripper hold.");
                // SessionOwnerGuard admits a missing owner on an unowned host.
                // An unscoped hold still needs one to find the owned grippers.
                string? owner = command.TryGetProperty("owner", out var ownerValue) && ownerValue.ValueKind == JsonValueKind.String
                    ? ownerValue.GetString() : null;
                if (!scoped && !Guid.TryParse(owner, out _))
                    return new(false, "Pika hold unverified: an unscoped STOP requires the dashboard owner ID.");
                var result = scoped ? await pika.ExecuteAsync("hold", command, cancellation) :
                    await pika.HoldOwnedAsync(owner!, cancellation);
                return new(result["ok"]?.GetValue<bool>() == true,
                    result["message"]?.GetValue<string>() ?? "Pika hold command acknowledged.");
            });
    }
}
