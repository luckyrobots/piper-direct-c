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
                var result = command.TryGetProperty("device_id", out _) ? await pika.ExecuteAsync("hold", command, cancellation) :
                    await pika.HoldOwnedAsync(command.GetProperty("owner").GetString()!, cancellation);
                return new(result["ok"]?.GetValue<bool>() == true,
                    result["message"]?.GetValue<string>() ?? "Pika hold command acknowledged.");
            });
    }
}
