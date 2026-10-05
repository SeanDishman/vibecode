using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    public void SetBridgeReviewLevel(ChatViewModel pane, string level)
    {
        if (!BridgeReviewPolicy.IsKnown(level)) throw new ArgumentException("Choose None, Low, Normal, or High.", nameof(level));
        if (!TryGetLiveBridge(pane, out var bridge)) return;
        level = BridgeReviewPolicy.Normalize(level);
        if (pane.BridgeReviewLevel == level) return;
        pane.BridgeReviewLevel = level;
        if (pane.IsBridgeManager && pane.BridgeReviewState == "skipped" && level != "none")
        {
            pane.BridgeReviewState = "pending";
            pane.BridgeReviewSummary = "";
            if (pane.BridgeTaskState == "completed") pane.BridgeTaskState = "waiting";
        }
        pane.AppendSystemPrompt = BridgePrompt(bridge.Board, pane, BridgeNumberOf(pane),
            bridge.Panes.Select(BridgeNumberOf).ToArray(), ManagerNumberIn(bridge.Panes));
        if (!pane.IsBridgeManager && CoordinatorFor(pane, bridge.Panes) is { } coordinator)
            StagePeerNotice(coordinator, $"[BRIDGE REVIEW SETTINGS]\n{pane.BridgeTerminalIdentity}'s user-selected review level is {level}. " +
                BridgeReviewPolicy.Choice(level).Description + " Respect this limit when assigning optional reviews.");
        SaveBridge(bridge.Panes, bridge.Board);
        RaiseRosterUi(BridgePanes.Contains(pane) ? null : bridge);
        RequestSave();
    }

    private bool ShouldWakeBridgePeer(ChatViewModel pane)
    {
        if (!TryGetLiveBridge(pane, out var bridge)) return false;
        // Worker messages remain available in the inbox without buying another model turn.
        if (!pane.IsBridgeManager) return false;
        return !pane.BridgeReviewComplete || !CoordinationReady(bridge.Panes) ||
            pane.IsWorking || bridge.Panes.Any(p => p.BridgeCoordinatorAgentId == pane.BridgeAgentId && (p.IsWorking || p.HasQueued));
    }

    private void InvalidateBridgeReview(ChatViewModel pane)
    {
        if (!TryGetLiveBridge(pane, out var bridge)) return;
        var coordinator = pane.IsBridgeManager ? pane : CoordinatorFor(pane, bridge.Panes);
        if (coordinator is null) return;
        coordinator.BridgeReviewState = "pending";
        coordinator.BridgeReviewSummary = "";
    }

    private JsonObject ReviewBridgeScope(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        if (!caller.IsBridgeManager || !caller.BridgeCoordinatesOnly)
            throw new StatusValidationException("Only a regular bridge orchestrator can record the final review. Workers return findings to their own orchestrator.");
        var verdict = args["verdict"]!.GetValue<string>();
        if (verdict is not ("approved" or "changes_requested"))
            throw new StatusValidationException("verdict must be approved or changes_requested.");
        var workers = bridge.Panes.Where(p => p.BridgeCoordinatorAgentId == caller.BridgeAgentId).ToArray();
        if (verdict == "approved")
        {
            if (WorkFor(bridge.Panes).Tasks.Any(t => t.CoordinatorId == caller.BridgeAgentId && t.State != "completed"))
                throw new StatusValidationException("Your plan still has unfinished, blocked or interrupted tasks. Complete or recover them before approving the scope.");
            if (!CoordinationReady(bridge.Panes))
                throw new StatusValidationException("Agree orchestrator scopes before approving the final review.");
            if (caller.HasQueued)
                throw new StatusValidationException("Updates are queued for your next turn. Finish your turn so the app can deliver them, then review the latest reports. Reading the inbox cannot consume these queued updates; do not poll.");
            if (workers.Any(p => p.IsWorking || p.HasQueued))
                throw new StatusValidationException("Your workers still have active or queued assignments. Finish your turn; their reports arrive automatically. Review only after those assignments finish, without polling.");
            if (workers.Any(p => p.Status is "error" or "closed" || p.BridgeTaskState is "failed" or "interrupted"))
                throw new StatusValidationException("Resolve failed or interrupted work before approving. Record changes_requested and assign a correction or recovery task to your own workers.");
        }
        var summary = args["summary"]!.GetValue<string>().Trim();
        var wasApproved = caller.BridgeReviewComplete;
        var skipped = verdict == "approved" && caller.BridgeReviewLevel == "none";
        caller.BridgeReviewState = skipped ? "skipped" : verdict;
        caller.BridgeReviewSummary = summary;
        caller.BridgeTaskState = verdict == "approved" ? "completed" : "waiting";
        caller.Items.Add(new DividerItem { Label = skipped ? "Scope complete · extra review disabled"
            : verdict == "approved" ? "Final review passed" : "Final review requested changes" });
        var allReviewed = bridge.Panes.Where(p => p.IsBridgeManager).All(p => p.BridgeReviewComplete);
        SaveBridge(bridge.Panes, bridge.Board);
        RaiseRosterUi(BridgePanes.Contains(caller) ? null : bridge);
        RequestSave();
        return new JsonObject { ["agent_id"] = caller.BridgeAgentId, ["review_state"] = caller.BridgeReviewState,
            ["review_level"] = caller.BridgeReviewLevel, ["all_scopes_reviewed"] = allReviewed,
            ["next_step"] = verdict == "changes_requested" ? "Assign concrete corrections to your own workers. Respect each agent's configured review budget."
                : allReviewed ? "Report the outcome and any review limitations to the user and finish. Do not send acknowledgments or invent more work."
                : "Report your group's outcome and finish. Other groups finish independently; the UI tracks their progress. Do not poll or send messages to other orchestrators." };
    }
}
