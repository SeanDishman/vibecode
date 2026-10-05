using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const string ClaudeSessionLimit = "You've hit your session limit · resets 5:40pm (America/Chicago)";

    /// <summary>The exact terminal envelope Claude Code 2.1.286 sent every Bridge pane when its session limit hit.</summary>
    private static JsonObject ClaudeLimitResult() => new()
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["result"] = ClaudeSessionLimit,
    };

    private static void ClaudeLimitQueueChecks()
    {
        AppSettings.Current.ContinueAfterLimitResets = true;
        Field(UsageService.Instance, "_lastFetch", DateTime.UtcNow);

        var (real, realSession) = NewChat("claude-real-session-limit", "claude");
        real.Status = "running";
        End(real, ClaudeLimitResult());
        Check("real Claude session-limit result schedules recovery", real.WaitingForLimitReset && real.BridgeTaskState == "waiting");
        var banner = real.Items.OfType<BannerItem>().Last().Text;
        Check("limit banner names an error, not success, and promises recovery",
            banner.StartsWith("Turn ended with error: " + ClaudeSessionLimit, StringComparison.Ordinal)
            && banner.Contains("VibeCode will continue"));
        Field(real, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
        Field(real, "_limitRecoveryRetryAt", DateTimeOffset.MinValue);
        SetProbe(real, () => Task.FromResult(new UsageRecoverySnapshot(true)));
        CheckRecovery(real);
        Pump(() => realSession.Sent.Count == 1);
        Check("real Claude session limit continues once allowance returns",
            realSession.Sent[0].Contains("Continue the interrupted task") && !real.WaitingForLimitReset);

        var (streamed, _) = NewChat("claude-structured-limit", "claude");
        streamed.Status = "running";
        var resetsAt = DateTimeOffset.Now.AddMinutes(37).ToUnixTimeSeconds();
        Call(streamed, "IngestSdk", JsonNode.Parse($$"""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":{{resetsAt}},"rateLimitType":"five_hour"},"uuid":"e1","session_id":"fixture-session"}""")!);
        Call(streamed, "IngestSdk", JsonNode.Parse("""{"type":"assistant","message":{"id":"m1","role":"assistant","model":"<synthetic>","content":[{"type":"text","text":"Claude is unavailable right now."}]},"parent_tool_use_id":null,"session_id":"fixture-session","uuid":"a1","error":"rate_limit"}""")!);
        End(streamed, new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["result"] = "Claude is unavailable right now." });
        Check("streamed rate-limit signals schedule recovery when the wording is unknown", streamed.WaitingForLimitReset);
        Check("streamed reset time is used exactly", ReadField<DateTimeOffset>(streamed, "_limitRecoveryRetryAt") == DateTimeOffset.FromUnixTimeSeconds(resetsAt));
        Check("turn rate-limit signals are consumed by the result", !ReadField<bool>(streamed, "_turnRateLimited")
            && ReadField<long?>(streamed, "_turnRateLimitResetsAt") is null);

        var (warned, _) = NewChat("claude-warning-only", "claude");
        warned.Status = "running";
        Call(warned, "IngestSdk", JsonNode.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","utilization":0.9,"rateLimitType":"five_hour"}}""")!);
        End(warned, Error("500 Internal server error"));
        Check("a usage warning does not turn an unrelated failure into a quota wait", !warned.WaitingForLimitReset && warned.Status == "error");

        // The reported Bridge failure: the orchestrator holds an app notice queued mid-turn (the shared terminal hides
        // coordination updates), then its own turn hits the limit. The user's prompt used to sit behind that notice
        // forever, labelled "waiting for shared swarm capacity", because an errored pane never drains its queue.
        var (orchestrator, os) = NewChat("orchestrator-stuck-queue", "claude");
        orchestrator.BridgeLabel = "Agent 1";
        Field(orchestrator, "_bridgeSessionInitialized", true);
        Call(orchestrator, "ApplyBridgeConfiguration", new BridgeAgentConfiguration("claude", null, null));
        orchestrator.IsBridgeManager = true;
        Property(orchestrator, "BridgeCoordinatesOnly", true);
        orchestrator.Status = "running";
        Check("coordination notice queues behind the running turn",
            orchestrator.Send("👑 [MANAGER UPDATE] Claude agent #3 hit an ERROR and stopped mid-work.") && orchestrator.HasQueued);
        End(orchestrator, ClaudeLimitResult());
        Check("orchestrator waits for its limit with the notice still queued",
            orchestrator.WaitingForLimitReset && orchestrator.Status == "error" && orchestrator.HasQueued && os.Sent.Count == 0);
        Check("user prompt is accepted by the errored orchestrator", orchestrator.Send("start them back up"));
        var userCard = orchestrator.Items.OfType<QueuedItem>().Single(q => q.Text == "start them back up");
        Check("queued user prompt is not mislabelled as waiting for swarm capacity",
            !userCard.WaitingForSwarmCapacity && !userCard.QueueStatusText.Contains("swarm"));
        Pump(() => os.Sent.Count == 1);
        Check("sending to an errored pane drains the held queue in FIFO order",
            os.Sent[0].Contains("[MANAGER UPDATE]") && !orchestrator.WaitingForLimitReset && orchestrator.HasQueued);
        End(orchestrator, new JsonObject { ["type"] = "result", ["is_error"] = false });
        Pump(() => os.Sent.Count == 2);
        Check("the user's prompt follows once the notice turn ends", os.Sent[1].Contains("start them back up") && !orchestrator.HasQueued);

        var (failed, fs) = NewChat("send-now-after-error", "claude");
        failed.Status = "running";
        Check("prompt queues behind a turn that will fail", failed.Send("Queued during a failing turn") && failed.HasQueued);
        End(failed, Error("500 Internal server error"));
        Check("an unrelated failure holds the queue", failed.Status == "error" && failed.HasQueued && fs.Sent.Count == 0);
        var head = failed.Items.OfType<QueuedItem>().Single();
        Check("queue head can be sent after a failed turn", failed.CanSendQueuedNow && failed.SendQueuedNow(head));
        Pump(() => fs.Sent.Count == 1);
        Check("send-now dispatches the held prompt", fs.Sent[0].Contains("Queued during a failing turn") && !failed.HasQueued);

        foreach (var chat in new[] { real, streamed, warned, orchestrator, failed }) chat.Close();

        // Sidebar cue: an Advanced Bridge host is flagged when the bridge is snapshotted; a regular one is not.
        var (bridgeHost, _) = NewChat("sidebar-bridge-host", "claude");
        var (bridgePeer, _) = NewChat("sidebar-bridge-peer", "claude");
        var snapshot = typeof(MainViewModel).GetMethod("SnapshotBridge", Flags)!;
        snapshot.Invoke(null, [new List<ChatViewModel> { bridgeHost, bridgePeer }, "", true]);
        Check("a regular bridge keeps the regular sidebar icon", !bridgeHost.IsAdvancedBridgeHost);
        bridgeHost.IsBridgeManager = true;
        Property(bridgeHost, "BridgeCoordinatesOnly", true);
        Property(bridgePeer, "BridgeCoordinatorAgentId", bridgeHost.BridgeAgentId);
        snapshot.Invoke(null, [new List<ChatViewModel> { bridgeHost, bridgePeer }, "", true]);
        Check("an Advanced Bridge host gets the advanced sidebar icon", bridgeHost.IsAdvancedBridgeHost);
    }
}
