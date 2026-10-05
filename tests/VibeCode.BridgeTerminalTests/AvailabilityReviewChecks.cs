using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyAgentAvailabilityAndReview()
    {
        var (vm, team) = Team("availability-review", 3);
        foreach (var agent in team) agent.Status = "idle";
        var manager = team[0]; var worker = team[1]; var peer = team[2];
        Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true);
        Property(worker, "BridgeCoordinatorAgentId", manager.BridgeAgentId);
        vm.SetBridgeTerminalMode(true);
        var agents = Tool(manager, "bridge_list_agents")["agents"]!.AsArray();
        Check("roster exposes readiness and passive worker delivery", agents[1]!["status"]!.ToString() == "idle" &&
            agents[1]!["task_state"]!.ToString() == "ready" && agents[1]!["peer_message_delivery"]!.ToString() == "mailbox_only" &&
            agents[1]!["can_receive_messages"]!.ToString() == "true");
        worker.Draft = "A private draft";
        var receipt = Tool(peer, "bridge_send_message", new() { ["recipient"] = worker.BridgeAgentId, ["message"] = "Useful context for the next assignment." });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("idle-worker delivery retains context without an extra model turn", receipt["delivered"]![0]!["notification"]!.ToString() == "mailbox_only" &&
            worker.UnreadPeerMessageCount == 1 && Session(worker).Sent.Count == 0 && worker.Draft == "A private draft");
        Tool(worker, "bridge_read_messages");
        Check("read receipts update unread counts", worker.UnreadPeerMessageCount == 0 &&
            Tool(manager, "bridge_list_agents")["agents"]![1]!["unread_messages"]!.ToString() == "0");
        var directMark = Tool(peer, "bridge_send_message", new() { ["recipient"] = worker.BridgeAgentId, ["message"] = "Another retained note." });
        var inboxNotified = false;
        worker.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatViewModel.UnreadPeerMessageCount)) inboxNotified = true; };
        Tool(worker, "bridge_mark_message", new() { ["message_id"] = directMark["delivered"]![0]!["message_id"]!.ToString() });
        Check("handling an unread message refreshes the roster badge", worker.UnreadPeerMessageCount == 0 && inboxNotified);
        peer.Status = "error";
        Check("errored recipients are advertised as unavailable", Tool(manager, "bridge_list_agents")["agents"]![2]!["can_receive_messages"]!.ToString() == "false");
        Reject("errored peers are rejected before delivery", () => Tool(manager, "bridge_send_message", new() { ["recipient"] = peer.BridgeAgentId, ["message"] = "Unavailable." }));
        peer.Status = "idle";
        Reject("worker cannot approve final product review", () => Tool(worker, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Not my role." }));
        Reject("unknown review verdict is rejected", () => Tool(manager, "bridge_review_scope", new() { ["verdict"] = "looks_good", ["summary"] = "Invalid." }));
        manager.Status = "running";
        manager.Send("Review one more edge case before approving.");
        try
        {
            Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Premature while a user follow-up is queued." });
            throw new Exception("Queued follow-up was ignored.");
        }
        catch (VibeCode.AgentStatus.Mcp.Contracts.StatusValidationException ex)
        { Check("queued review updates tell the model to yield instead of polling", ex.Message.Contains("Finish your turn") && ex.Message.Contains("Reading the inbox cannot consume")); }
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        PumpUntil(() => Session(manager).Sent.Count == 1);
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Tool(manager, "bridge_dispatch_task", new() { ["recipient"] = worker.BridgeAgentId, ["task_name"] = "Build catalogue", ["message"] = "Implement the catalogue and verify filtering." });
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Reject("busy worker blocks premature approval", () => Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Too soon." }));
        var changes = Tool(manager, "bridge_review_scope", new() { ["verdict"] = "changes_requested", ["summary"] = "Product filtering drops the last item; correct the off-by-one boundary and verify empty lists." });
        Check("review feedback records concrete findings", changes["review_state"]!.ToString() == "changes_requested" && manager.BridgeReviewSummary.Contains("off-by-one") && manager.BridgeReviewLabel == "Changes requested");
        worker.Items.Add(new TextItem { Text = "Corrected the filtering boundary. Empty-list and final-item regression checks passed." });
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        PumpUntil(() => Session(manager).Sent.Count > 1);
        Check("worker completion routes correction evidence to its orchestrator", worker.BridgeTaskState == "completed" &&
            Session(manager).Sent.Any(s => s.Contains("final-item regression checks passed")));
        manager.Items.Add(new TextItem { Text = "Reviewed the filtering result and regression evidence. Ready to record the final review." });
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var passed = Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Reviewed corrected filtering, empty-list handling and final-item regression evidence; all checks passed." });
        Check("passed review marks the group complete", passed["all_scopes_reviewed"]!.ToString() == "true" && manager.BridgeReviewLabel == "Review passed" && manager.BridgeTaskState == "completed");
        var before = Session(manager).Sent.Count;
        var passive = Tool(peer, "bridge_send_message", new() { ["recipient"] = manager.BridgeAgentId, ["message"] = "A useful note to retain for a future goal." });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("completed orchestrator retains messages without waking", passive["delivered"]![0]!["notification"]!.ToString() == "mailbox_only" && Session(manager).Sent.Count == before && manager.UnreadPeerMessageCount == 1);
        Call(vm, "SaveBridge", vm.BridgePanes, ".vibecode-bridge.md");
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == manager.SessionId);
        Check("saved bridge retains review evidence and task completion", saved.HostReviewState == "approved" && saved.HostReviewSummary!.Contains("regression evidence") && saved.Peers[0].TaskState == "completed");
        Tool(manager, "bridge_dispatch_task", new() { ["recipient"] = worker.BridgeAgentId, ["task_name"] = "Check mobile catalogue", ["message"] = "Verify the actual catalogue layout on a narrow viewport." });
        Check("new assignment invalidates the prior review", manager.BridgeReviewState == "pending" && manager.BridgeReviewSummary == "");
        PumpUntil(() => Session(worker).Sent.Count == 2);
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = true });
        Reject("failed assignment blocks final approval", () => Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "No evidence yet." }));

        var (multiVm, groups) = Team("review-two-groups", 4);
        foreach (var agent in groups) agent.Status = "idle";
        foreach (var coordinator in new[] { groups[0], groups[2] })
        { Property(coordinator, "IsBridgeManager", true); Property(coordinator, "BridgeCoordinatesOnly", true); }
        Property(groups[1], "BridgeCoordinatorAgentId", groups[0].BridgeAgentId);
        Property(groups[3], "BridgeCoordinatorAgentId", groups[2].BridgeAgentId);
        multiVm.SetBridgeTerminalMode(true);
        Tool(groups[0], "bridge_send_message", new() { ["recipient"] = groups[2].BridgeAgentId, ["message"] = "Catalogue is mine; search is yours." });
        Tool(groups[2], "bridge_send_message", new() { ["recipient"] = groups[0].BridgeAgentId, ["message"] = "Agreed. Search is my scope." });
        Tool(groups[0], "bridge_read_messages"); Tool(groups[2], "bridge_read_messages");
        Tool(groups[0], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Catalogue" });
        Tool(groups[2], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Search" });
        PumpUntil(() => Session(groups[0]).Sent.Count > 0);
        Call(groups[0], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var first = Tool(groups[0], "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Catalogue and search-link integration inspected; checks passed." });
        Check("one reviewed group cannot declare the whole product finished", first["all_scopes_reviewed"]!.ToString() == "false");
        Check("review findings do not create orchestrator-to-orchestrator chatter", !Session(groups[2]).Sent.Any(s => s.Contains("search-link integration")));
        var final = Tool(groups[2], "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Search results and shared catalogue navigation verified." });
        Check("all group reviews are required for product completion", final["all_scopes_reviewed"]!.ToString() == "true");
        Check("final peer review updates shared status without waking another orchestrator", groups[0].BridgeReviewState == "approved" &&
            !Session(groups[0]).Sent.Any(s => s.Contains("All orchestrators completed their configured reviews")) &&
            Tool(groups[0], "bridge_list_agents")["all_scopes_reviewed"]!.ToString() == "true");
        var pendingBefore = groups[0].Items.OfType<QueuedItem>().Count();
        Tool(groups[2], "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Search results and shared catalogue navigation verified." });
        Check("repeated approval does not create another peer wake-up", groups[0].Items.OfType<QueuedItem>().Count() == pendingBefore);
    }
}
