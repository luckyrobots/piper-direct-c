using System.Globalization;
using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

public sealed partial class PipersPlugin
{
    private sealed class PreparedFollowRun(PipersPlugin plugin, GroupSession group)
    {
        private const string Read = "robot.runtime.read";
        private const string Control = "robot.runtime.control";
        private const string Frames = "robot.runtime.frames";
        private const int MaximumReadFrames = 128;
        private double originalScale;

        public async Task ArmAsync(CancellationToken cancellation)
        {
            var edit = await SendAsync(GroupFollowPhase.Arming, Read, null, cancellation);
            if (edit["mode"]?.GetValue<string>() != "edit")
                throw new InvalidOperationException("Stop Play before arming followers.");
            if (edit["timeMode"]?.GetValue<string>() is not ("deterministic-realtime" or "realtime"))
                throw new InvalidOperationException("Following requires Deterministic real-time or Real-time scene mode. Select one in scene settings before following the real robot.");

            var armed = await SendAsync(GroupFollowPhase.Arming, Control, "arm-next-play", cancellation,
                new() { ["activationHandshake"] = true });
            if (armed["mode"]?.GetValue<string>() != "edit" || armed["startArmedForNextPlay"]?.GetValue<bool>() != true ||
                armed["prepareId"]?.GetValue<string>() != group.PrepareId)
                throw new InvalidOperationException("The engine did not acknowledge an unchanged Edit-mode arm-next-play latch.");
        }

        public async Task<bool> TryBindPlayAsync(CancellationToken cancellation)
        {
            var reply = await SendAsync(GroupFollowPhase.Armed, Read, null, cancellation);
            string? mode = reply["mode"]?.GetValue<string>();
            if (mode is "edit" or "preparing" or "loading") return false;
            if (mode != "pause" || reply["ready"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The armed scene did not pause at its prepared first frame.");
            if (reply["runId"]?.GetValue<string>() is not { Length: > 0 } run ||
                reply["prepareId"]?.GetValue<string>() != group.PrepareId || reply["startRequested"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The user-requested Play run did not match this grouped preparation.");
            if (reply["timeScale"] is not JsonValue scale || !scale.TryGetValue<double>(out double timeScale) ||
                !double.IsFinite(timeScale))
                throw new InvalidOperationException("The prepared Play run has no valid time scale.");
            originalScale = timeScale;
            group.RunId = run;
            return true;
        }

        public async Task<JsonObject> PauseForMatchingAsync(CancellationToken cancellation)
        {
            await SendAsync(GroupFollowPhase.Matching, Control, "pause", cancellation, new() { ["timeScale"] = .2 });
            return await ReadMatchingFrameAsync(cancellation);
        }

        public async Task<JsonObject> ReadMatchingFrameAsync(CancellationToken cancellation)
        {
            var reply = await SendAsync(GroupFollowPhase.Matching, Read, null, cancellation);
            if (reply["mode"]?.GetValue<string>() != "pause")
                throw new InvalidOperationException("The prepared simulation must remain paused.");
            return RequirePreparedReply(reply);
        }

        public async Task<JsonObject> OpenFramesAsync(JsonObject matchedFrame, CancellationToken cancellation)
        {
            await SendAsync(GroupFollowPhase.Matching, Control, "claim-episode-follower", cancellation,
                new() { ["episodeId"] = matchedFrame["episodeId"]?.GetValue<string>() ?? "" });
            var mappings = new JsonArray(group.Lanes.Select(lane =>
            {
                var mapping = lane.Robot.OrderedCaptureMapping(matchedFrame);
                mapping["id"] = lane.Profile.Id;
                return (JsonNode)mapping;
            }).ToArray());
            return RequirePreparedReply(await SendAsync(GroupFollowPhase.Matching, Frames, "start", cancellation,
                new() { ["tracks"] = mappings, ["afterFrameCursor"] = "0", ["maxFrames"] = MaximumReadFrames }));
        }

        public Task MarkReadyAsync(CancellationToken cancellation) =>
            SendAsync(GroupFollowPhase.Matching, Control, "mark-start-ready", cancellation);

        public async Task<JsonObject> ActivateAsync(CancellationToken cancellation)
        {
            var reply = await SendAsync(GroupFollowPhase.Activating, Control, "activate-start", cancellation,
                new() { ["timeScale"] = originalScale });
            if (reply["startActivated"]?.GetValue<bool>() != true ||
                reply["prepareId"]?.GetValue<string>() != group.PrepareId || reply["runId"]?.GetValue<string>() != group.RunId)
                throw new InvalidOperationException("The shared Play activation acknowledgement changed its request.");
            return reply;
        }

        public async Task<JsonObject> ReadFramesAsync(ulong afterCursor, int availableSpace, CancellationToken cancellation) =>
            RequirePreparedReply(await SendAsync(GroupFollowPhase.Following, Frames, null, cancellation, new()
            {
                ["afterFrameCursor"] = afterCursor.ToString(CultureInfo.InvariantCulture),
                ["maxFrames"] = Math.Min(availableSpace, MaximumReadFrames)
            }));

        public Task SetSourcePausedAsync(bool paused, CancellationToken cancellation) =>
            SendAsync(GroupFollowPhase.Following, Control, paused ? "pause-follower-source" : "resume-follower-source", cancellation);

        public Task AcknowledgeEpisodeAsync(OrderedSimulationFrame finalFrame, CancellationToken cancellation) =>
            SendAsync(GroupFollowPhase.Following, Control, "ack-episode", cancellation, new()
            {
                ["episodeId"] = finalFrame.EpisodeId,
                ["episodeOrdinal"] = finalFrame.EpisodeOrdinal,
                ["finalFrameCursor"] = finalFrame.Cursor.ToString(CultureInfo.InvariantCulture)
            });

        public async Task RetireAsync()
        {
            try { await SendAsync(GroupFollowPhase.Retiring, Control, "cancel-start", CancellationToken.None); }
            catch (Exception error) { plugin.FailGroup(group, error); }
            if (group.RunId.Length == 0) return;
            try
            {
                await SendAsync(GroupFollowPhase.Retiring, Control, "pause", CancellationToken.None,
                    new() { ["timeScale"] = originalScale });
            }
            catch (Exception error) { plugin.FailGroup(group, error); }
        }

        private JsonObject RequirePreparedReply(JsonObject reply)
        {
            if (reply["prepareId"]?.GetValue<string>() != group.PrepareId || reply["runId"]?.GetValue<string>() != group.RunId)
                throw new InvalidOperationException("The grouped tape preparation or Play run changed.");
            return reply;
        }

        private async Task<JsonObject> SendAsync(GroupFollowPhase phase, string method, string? action,
            CancellationToken cancellation, JsonObject? payload = null)
        {
            cancellation.ThrowIfCancellationRequested();
            group.Require(phase);
            bool retiring = phase == GroupFollowPhase.Retiring;
            if (!retiring && !plugin.GroupScopeCurrent(group)) throw new InvalidOperationException("Grouped following was disarmed.");

            var request = payload ?? new();
            request["sceneId"] = group.Scene;
            request["entityId"] = group.Entity;
            request["expectedModelAssetId"] = group.Asset;
            request["expectedModelRevision"] = group.Revision;
            request["prepareId"] = group.PrepareId;
            if (group.RunId.Length > 0) request["runId"] = group.RunId;
            if (action is not null) request["action"] = action;

            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(method == Frames && action is null ? 150 : 1000), group.Clock);
            using var lifetime = retiring ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellation, group.Stop.Token, deadline.Token);
            var requestCancellation = lifetime?.Token ?? deadline.Token;
            var reply = await plugin.engine.RequestAsync(method, request, requestCancellation);
            if (!retiring && !plugin.GroupScopeCurrent(group))
                throw new OperationCanceledException("Following was canceled while the editor request retired.");
            requestCancellation.ThrowIfCancellationRequested();
            var result = JsonNode.Parse(reply.GetRawText())!.AsObject();
            if (method == Control && result["applied"]?.GetValue<bool>() != true)
                throw new InvalidOperationException("The engine did not apply the prepared run action " + action + ".");
            return result;
        }
    }
}
