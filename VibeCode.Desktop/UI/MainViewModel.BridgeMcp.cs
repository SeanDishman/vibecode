using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private readonly Dictionary<ChatViewModel, int> _bridgeMcpSends = new();

    // Runs on the UI dispatcher. Roster membership is resolved on EVERY call, including parked rosters.
    // A tool argument can neither choose its sender nor reach another bridge in the same workspace.
    private JsonObject InvokeBridgeTool(ChatViewModel caller, string tool, JsonObject args)
    {
        if (tool == "chat_set_title") return SetChatTitleFromMcp(caller, args);
        if (!TryGetLiveBridge(caller, out var bridge) || caller.Status == "closed")
        {
            if (tool == "bridge_list_agents") return new JsonObject { ["joined"] = false, ["agents"] = new JsonArray() };
            throw new StatusValidationException("This session is not in a live bridge. Start or reopen its bridge first.");
        }
        ReconcileBridgeGroups(bridge.Panes);

        IReadOnlyList<BridgeEditEntry>? rosterEdits = null;
        if (tool == "bridge_list_agents") return new JsonObject
        {
            ["joined"] = true, ["self_id"] = caller.BridgeAgentId,
            ["messaging_enabled"] = PeerMessagingEnabled,
            ["plan_version"] = WorkFor(bridge.Panes).PlanVersion,
            ["coordination_mode"] = WorkFor(bridge.Panes).CentralPlan is null ? "peer_agreement" : "central_assignment",
            ["central_orchestrator"] = CentralPlanJson(WorkFor(bridge.Panes)),
            ["orchestrator_communication_locked"] = WorkFor(bridge.Panes).DispatchStarted,
            ["coordination_ready"] = CoordinationReady(bridge.Panes),
            ["all_scopes_reviewed"] = bridge.Panes.Any(p => p.IsBridgeManager) && bridge.Panes.Where(p => p.IsBridgeManager).All(p => p.BridgeReviewComplete),
            ["agents"] = new JsonArray(bridge.Panes.Select(p => (JsonNode?)new JsonObject
            {
                ["agent_id"] = p.BridgeAgentId, ["number"] = BridgeNumberOf(p), ["label"] = p.BridgeLabel,
                ["message_recipient"] = BridgeMessageRecipient(p, bridge.Panes),
                ["provider"] = p.Provider, ["status"] = p.Status, ["summary"] = p.BridgeActivitySummary,
                ["model"] = p.Model, ["effort"] = p.Effort,
                ["review_level"] = p.BridgeReviewLevel,
                ["review_pass_limit"] = BridgeReviewPolicy.Choice(p.BridgeReviewLevel).PassLimit,
                ["task_name"] = p.BridgeTaskName,
                ["task_state"] = p.BridgeTaskState,
                ["file_activity"] = p.BridgeFileActivity,
                ["recent_edits"] = RecentEditsJson(p.BridgeAgentId, rosterEdits ??= BridgeEditsFor(bridge), DateTimeOffset.UtcNow),
                ["can_receive_messages"] = p.AcceptsPeerNotifications,
                ["can_message"] = !ReferenceEquals(p, caller) && PeerMessagingEnabled && BridgeMessageRestriction(caller, p, WorkFor(bridge.Panes)) is null,
                ["message_blocked_reason"] = ReferenceEquals(p, caller) ? "This is you."
                    : !PeerMessagingEnabled ? "Peer messaging is paused in Bridge settings."
                    : BridgeMessageRestriction(caller, p, WorkFor(bridge.Panes)),
                ["peer_message_delivery"] = p.CanWakeForPeerMessages ? "notify_after_turn" : "mailbox_only",
                ["unread_messages"] = p.UnreadPeerMessageCount,
                ["review_state"] = p.IsBridgeManager ? p.BridgeReviewState : null,
                ["review_summary"] = p.IsBridgeManager ? p.BridgeReviewSummary : null,
                ["manager"] = p.IsBridgeManager, ["self"] = ReferenceEquals(p, caller),
                ["role"] = p.IsBridgeManager ? "orchestrator" : p.BridgeCoordinatorAgentId is not null ? "worker" : "agent",
                ["coordinator_id"] = p.BridgeCoordinatorAgentId,
                ["scope"] = p.IsBridgeManager ? p.BridgeOrchestrationScope : null,
                ["scope_confirmed"] = p.IsBridgeManager && p.BridgeScopeConfirmed,
                ["worker_ids"] = new JsonArray(bridge.Panes.Where(w => w.BridgeCoordinatorAgentId == p.BridgeAgentId)
                    .Select(w => (JsonNode?)JsonValue.Create(w.BridgeAgentId)).ToArray()),
            }).ToArray()),
        };
        if (tool == "bridge_report_activity")
        {
            caller.BridgeActivitySummary = args["summary"]!.GetValue<string>().Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (args["task_name"] is not null)
                caller.BridgeTaskName = string.Join(" ", args["task_name"]!.GetValue<string>().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            RequestSave();
            return new JsonObject { ["agent_id"] = caller.BridgeAgentId, ["task_name"] = caller.BridgeTaskName, ["summary"] = caller.BridgeActivitySummary };
        }
        if (tool == "bridge_set_task_title") return SetBridgeTaskTitle(caller, args);
        if (tool == "bridge_dispatch_task") return DispatchDurableTask(caller, bridge, args);
        if (tool == "bridge_list_tasks") return ListBridgeTasks(bridge);
        if (tool == "bridge_file_edits") return ListBridgeFileEdits(caller, bridge, args);
        if (tool == "bridge_agent_status") return BridgeAgentStatus(caller, bridge, args);
        if (tool == "bridge_set_plan") return SetBridgePlan(caller, bridge, args);
        if (tool == "bridge_update_task") return UpdateBridgeTask(caller, bridge, args, false);
        if (tool == "bridge_retry_task") return UpdateBridgeTask(caller, bridge, args, true);
        if (tool == "bridge_agree_scope") return AgreeBridgeScope(caller, bridge, args);
        if (tool == "bridge_review_scope") return ReviewBridgeScope(caller, bridge, args);
        if (tool == "bridge_send_message") return SendBridgeMcpMessage(caller, bridge, args);
        if (tool == "bridge_broadcast") return SendBridgeMcpMessage(caller, bridge, args, broadcast: true);

        var mailbox = MailboxFor(caller, bridge);
        if (tool == "bridge_read_messages")
        {
            var messages = new JsonArray();
            var page = mailbox.Store.ReadPage(mailbox, args["before"]?.GetValue<string>(), args["limit"]?.GetValue<int>() ?? BridgeMailboxStore.PageSize, DateTimeOffset.UtcNow, out var hasMore);
            var readSetup = false;
            foreach (var message in page)
            {
                var sender = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == message.From.AgentId);
                var recipient = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == message.To.AgentId);
                if (caller.IsBridgeManager && recipient?.IsBridgeManager == true && ReferenceEquals(message.From, mailbox))
                    readSetup |= WorkFor(bridge.Panes).SetupMessages.Add(BridgeWorkState.PeerKey(caller.BridgeAgentId, recipient.BridgeAgentId));
                if (caller.IsBridgeManager && sender?.IsBridgeManager == true && ReferenceEquals(message.To, mailbox))
                {
                    caller.BridgeScopeReadFrom.Add(sender.BridgeAgentId);
                    readSetup |= WorkFor(bridge.Panes).SetupReads.Add(BridgeWorkState.PeerKey(caller.BridgeAgentId, sender.BridgeAgentId));
                }
                messages.Add(new JsonObject
                {
                    ["message_id"] = message.Id, ["sender_id"] = sender?.BridgeAgentId, ["recipient_id"] = recipient?.BridgeAgentId,
                    ["from"] = message.From.Label, ["to"] = message.To.Label,
                    ["direction"] = ReferenceEquals(message.To, mailbox) ? "incoming" : "outgoing",
                    ["message"] = message.Body, ["status"] = message.Status, ["sent_at"] = message.SentAt.ToString("O"),
                    ["sender_departed"] = message.From.Closed,
                });
            }
            caller.RaisePeerInbox();
            if (readSetup) SaveWork(bridge);
            return new JsonObject { ["messages"] = messages, ["capacity"] = BridgeMailboxStore.Capacity,
                ["has_more"] = hasMore, ["next_cursor"] = hasMore ? page.Last().Cursor : null };
        }
        if (tool == "bridge_mark_message")
        {
            var id = args["message_id"]!.GetValue<string>();
            if (!mailbox.Store.Mark(mailbox, id, true, DateTimeOffset.UtcNow))
                throw new StatusValidationException("That ID is not a retained incoming message in your mailbox.");
            caller.RaisePeerInbox();
            return new JsonObject { ["message_id"] = id, ["status"] = "answered" };
        }
        throw new StatusValidationException("Unknown bridge tool.");
    }

    /// <summary>Regular bridges: the agent's own short title in its pane header. It follows the overall task, so an
    /// unchanged title is a no-op and one turn may change it at most <see cref="BridgeTaskTitlePolicy.MaxChangesPerTurn"/>
    /// times. Refusals return applied=false with a reason rather than an error, so a model stops instead of retrying.</summary>
    private JsonObject SetBridgeTaskTitle(ChatViewModel caller, JsonObject args)
    {
        var title = BridgeTaskTitlePolicy.Normalize(args["title"]!.GetValue<string>());
        JsonObject Declined(string reason) => new() { ["applied"] = false, ["title"] = caller.BridgeHeaderTaskTitle, ["reason"] = reason };
        if (!caller.UsesBridgeTaskTitle)
            return Declined("Advanced Bridge orchestrators and workers are titled by their assignments. Do not call this tool.");
        if (BridgeTaskTitlePolicy.IsPlaceholder(title))
            return Declined("Name the actual task in one to four words, e.g. Fix login redirect.");
        if (string.Equals(title, caller.BridgeHeaderTaskTitle, StringComparison.OrdinalIgnoreCase))
            return Declined("That is already your title. Keep working; call this again only when you start a different overall task.");
        if (caller.BridgeTaskTitleChangesThisTurn >= BridgeTaskTitlePolicy.MaxChangesPerTurn)
            return Declined($"Your title already changed {BridgeTaskTitlePolicy.MaxChangesPerTurn} times this turn. Keep the current title until a new request starts a different overall task.");
        caller.BridgeTaskName = title;
        caller.BridgeTaskTitleChangesThisTurn++;
        RequestSave();
        return new JsonObject
        {
            ["applied"] = true, ["title"] = title,
            ["guidance"] = BridgeTaskTitlePolicy.AppliedGuidance,
        };
    }

    private JsonObject SendBridgeMcpMessage(ChatViewModel sender, LiveBridge bridge, JsonObject args, bool broadcast = false)
    {
        if (!PeerMessagingEnabled) throw new StatusValidationException("Peer messaging is paused in Bridge settings.");
        var recipient = broadcast ? "all" : args["recipient"]!.GetValue<string>();
        if (!broadcast && recipient.Equals("all", StringComparison.OrdinalIgnoreCase))
            throw new StatusValidationException("Prefer bridge_send_message to one specific peer (twice for two peers). For important information needed by at least three other agents, use bridge_broadcast with affected_agent_ids and reason.");
        var body = args["message"]!.GetValue<string>();
        var work = WorkFor(bridge.Panes);
        var affectedIds = new List<string>();
        if (broadcast)
        {
            var affected = args["affected_agent_ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToArray();
            if (affected.Distinct(StringComparer.Ordinal).Count() < VibeCode.AgentStatus.Mcp.Bridge.BridgeMcpTools.MinimumBroadcastRecipients)
                throw new StatusValidationException("Broadcast requires at least three distinct affected peers; use direct messages for one or two.");
            foreach (var id in affected)
            {
                var peer = bridge.Panes.FirstOrDefault(p => (p.BridgeAgentId == id || BridgeMessageRecipient(p, bridge.Panes) == id) && !ReferenceEquals(p, sender));
                if (peer is null)
                    throw new StatusValidationException($"affected_agent_ids contains '{id}', which is not another live peer in this bridge. Copy message_recipient exactly from bridge_list_agents; do not truncate it or include yourself. Nothing was delivered.");
                if (BridgeMessageRestriction(sender, peer, work) is { } restriction)
                    throw new StatusValidationException($"Affected peer '{id}' cannot receive this broadcast: {restriction} Do not count unavailable or restricted peers toward the three-peer minimum. Nothing was delivered.");
                affectedIds.Add(peer.BridgeAgentId);
            }
            if (affectedIds.Distinct(StringComparer.Ordinal).Count() != affectedIds.Count)
                throw new StatusValidationException("Each affected peer may appear only once. An address and full agent_id for the same peer do not count as two agents. Nothing was delivered.");
        }
        var targets = bridge.Panes.Where(p => !ReferenceEquals(p, sender)
            && (recipient == "all" || p.BridgeAgentId == recipient || BridgeMessageRecipient(p, bridge.Panes) == recipient)).ToList();
        if (targets.Count == 0)
            throw new StatusValidationException("Recipient is not a live peer in your bridge. Call bridge_list_agents for current agent_id values.");
        var count = _bridgeMcpSends.GetValueOrDefault(sender);
        if (count >= bridge.Peers.Limits.MaxBlocksPerTurn)
            throw new StatusValidationException("This turn's message limit was reached. Continue your task; avoid acknowledgment loops.");
        var from = BridgeNumberOf(sender);
        var hop = _peerHop.GetValueOrDefault(sender) + 1;
        var delivered = new JsonArray();
        var failures = new JsonArray();
        var skipped = new JsonArray();
        foreach (var target in targets)
        {
            var setupKey = BridgeWorkState.PeerKey(sender.BridgeAgentId, target.BridgeAgentId);
            if (BridgeMessageRestriction(sender, target, work) is { } restriction)
            {
                (broadcast ? skipped : failures).Add(new JsonObject { ["agent_id"] = target.BridgeAgentId, ["reason"] = restriction });
                continue;
            }
            var to = BridgeNumberOf(target);
            var admittedAt = DateTime.Now;
            var verdict = bridge.Peers.Admit(from, to, body, hop, admittedAt);
            if (verdict != PeerMessageVerdict.Deliver)
            {
                failures.Add(new JsonObject { ["agent_id"] = target.BridgeAgentId, ["reason"] = PeerMessagePolicy.Explain(verdict, target.BridgeLabel) });
                continue;
            }
            try
            {
                var fromBox = MailboxFor(sender, bridge);
                var toBox = MailboxFor(target, bridge);
                var message = fromBox.Store.Deliver(fromBox, toBox, body, hop, DateTimeOffset.UtcNow, sender.IsBridgeManager && target.IsBridgeManager);
                target.QueuePeerNotification(message, bridge.Peers.Limits, depth => _peerHop[target] = depth);
                if (sender.IsBridgeManager && target.IsBridgeManager)
                {
                    sender.BridgeScopeSentTo.Add(target.BridgeAgentId);
                    work.SetupMessages.Add(setupKey);
                    SaveWork(bridge);
                }
                delivered.Add(new JsonObject { ["agent_id"] = target.BridgeAgentId, ["message_id"] = message.Id, ["status"] = "queued",
                    ["notification"] = target.CanWakeForPeerMessages ? "notify_after_turn" : "mailbox_only" });
                target.Items.Add(new DividerItem { Label = $"Message from {sender.BridgeLabel}" });
                NoteBridgeActivity(BridgePanes.Contains(sender) ? null : bridge);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                bridge.Peers.RollbackDelivery(from, to, body, admittedAt);
                failures.Add(new JsonObject { ["agent_id"] = target.BridgeAgentId, ["reason"] = ex is InvalidOperationException
                    ? ex.Message : "Could not save or queue the message. No delivery slot was consumed; retry when the mailbox is available." });
            }
        }
        if (delivered.Count == 0)
            throw new StatusValidationException("No messages delivered. " + string.Join(" ", failures.Select(f => f?["reason"]?.ToString())));
        _bridgeMcpSends[sender] = count + 1;
        sender.Items.Add(new DividerItem { Label = $"Message queued for {delivered.Count} bridge agent(s)" });
        var receipt = new JsonObject { ["delivered"] = delivered, ["failures"] = failures };
        if (broadcast)
        {
            receipt["affected_agent_ids"] = new JsonArray(affectedIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            receipt["reason"] = args["reason"]!.GetValue<string>();
            receipt["skipped"] = skipped;
            receipt["delivery_complete"] = failures.Count == 0;
            receipt["guidance"] = failures.Count == 0
                ? "Delivered to all eligible peers. No follow-up sends are needed. Skipped peers are intentionally excluded; do not retry or ask a worker to relay to them."
                : "Do not repeat successful deliveries. Skipped peers are intentionally excluded; do not retry or relay to them. Only actual transient failures may need a later direct retry.";
        }
        return receipt;
    }

    private static string? BridgeMessageRestriction(ChatViewModel sender, ChatViewModel target, BridgeWorkState work)
    {
        if (sender.IsBridgeManager && target.IsBridgeManager)
        {
            if (work.DispatchStarted) return "Orchestrator communication is permanently closed after worker dispatch. Workers may still coordinate across groups.";
            if (work.SetupMessages.Contains(BridgeWorkState.PeerKey(sender.BridgeAgentId, target.BridgeAgentId)))
                return "Your one setup message to this orchestrator was already sent. Read the shared plan and confirm its version; do not send more messages.";
        }
        return target.AcceptsPeerNotifications ? null : "Session is not accepting input.";
    }

    private static string BridgeMessageRecipient(ChatViewModel peer, IReadOnlyList<ChatViewModel> roster)
    {
        // Stable identity prefixes avoid copying 32-character IDs. Never guess if a prefix collides:
        // advertise full IDs for both peers and reject the now-ambiguous short address.
        if (peer.BridgeAgentId.Length <= 12) return peer.BridgeAgentId;
        var prefix = peer.BridgeAgentId[..12];
        return roster.Count(p => p.BridgeAgentId.StartsWith(prefix, StringComparison.Ordinal)) == 1
            ? "peer:" + prefix : peer.BridgeAgentId;
    }

}
