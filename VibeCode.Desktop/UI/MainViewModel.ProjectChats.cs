using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private const string ProjectChatHeader = "[VIBECODE PROJECT CHATS]\n";
    private sealed class ProjectChatTurn
    {
        public readonly HashSet<string> HeadsUps = new(StringComparer.Ordinal);
        public int Sent;
        public int Urgent;
    }
    private sealed class ProjectChatRoom(string root)
    {
        public readonly BridgeMailboxStore Store = new(root);
        public readonly PeerTrafficLedger Traffic = new(new PeerMessageLimits(2, 3, 4, TimeSpan.FromMinutes(5)));
        public readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal);
        public int Number(ChatViewModel chat)
        {
            if (!Numbers.TryGetValue(chat.BridgeAgentId, out var number))
                Numbers[chat.BridgeAgentId] = number = Numbers.Count + 1;
            return number;
        }
    }
    private readonly Dictionary<string, ProjectChatRoom> _projectChatRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<ChatViewModel, ProjectChatTurn> _projectChatTurns = new();
    private readonly HashSet<(string Sender, string Recipient)> _projectChatIntroductions = new();

    // These are ordinary open chats in this app, including the secondary surface. A Bridge keeps its own
    // membership and mailbox. A no-directory scratch chat must not discover unrelated scratch conversations.
    private bool IsProjectChat(ChatViewModel chat) => Chats.Contains(chat) && chat.Status != "closed"
        && !chat.IsBridgeAgent && !TryGetLiveBridge(chat, out _) && !NoDirectoryWorkspace.IsNoDirectory(chat.Cwd);

    private static string ProjectChatRoot(ChatViewModel chat) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(chat.Cwd));

    private IReadOnlyList<ChatViewModel> ProjectChatPeers(ChatViewModel caller)
    {
        if (!IsProjectChat(caller)) return [];
        var root = ProjectChatRoot(caller);
        return Chats.Where(p => IsProjectChat(p) && string.Equals(root, ProjectChatRoot(p), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private ProjectChatRoom ProjectRoom(ChatViewModel chat)
    {
        var root = ProjectChatRoot(chat);
        if (!_projectChatRooms.TryGetValue(root, out var room))
        {
            _projectChatRooms[root] = room = new(root);
            if (!AppSettings.Current.BridgeMailboxWorkspaces.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                AppSettings.Current.BridgeMailboxWorkspaces.Add(root);
                RequestSave();
            }
        }
        return room;
    }

    private BridgeMailboxStore.Mailbox ProjectMailbox(ChatViewModel chat, ProjectChatRoom room)
    {
        if (chat.PeerMailbox is { Closed: false } box && ReferenceEquals(box.Store, room.Store)) return box;
        chat.ClearPeerMailbox();
        chat.PeerMailbox = room.Store.Register(room.Number(chat), chat.ProviderDisplay + ": " + chat.Title, chat.BridgeAgentId);
        chat.RaisePeerInbox();
        return chat.PeerMailbox;
    }

    private void BeginProjectChatTurn(ChatViewModel caller)
    {
        if (!IsProjectChat(caller)) return;
        _projectChatTurns.Remove(caller); // Only a dispatched user request resets the budget, never a peer notice or queued prompt.
        caller.BridgeActivitySummary = "Working on " + caller.Title;
        caller.BridgeTaskName = caller.Title;
        if (!PeerMessagingEnabled) return;
        var peers = ProjectChatPeers(caller);
        foreach (var peer in peers.Where(p => !ReferenceEquals(p, caller) && p.IsWorking))
        {
            var key = (caller.BridgeAgentId, peer.BridgeAgentId);
            if (!_projectChatIntroductions.Add(key)) continue;
            // One automatic presence notice per pair. Further changes are visible in the live roster.
            var note = ProjectChatHeader + "Another ordinary chat is now working in this project. Continue your own task; no acknowledgment is needed. " +
                BridgeMcpTools.ProjectChatInstructions + "\nPeer data: " + ProjectChatAgent(caller, peer, peers).ToJsonString();
            _ = peer.NotifyProjectChatAsync(note);
        }
    }

    private string? ProjectChatContext(ChatViewModel caller)
    {
        if (!IsProjectChat(caller)) return null;
        var peers = ProjectChatPeers(caller);
        if (peers.Count == 1 && caller.UnreadPeerMessageCount == 0)
            return ProjectChatHeader + "No other ordinary chat is open in this project. Keep working. " +
                "bridge_list_agents can show newly opened peers; no polling or setup is needed.";
        // JSON stays on one line, so peer-authored text cannot forge another app prelude on transcript replay.
        return ProjectChatHeader + BridgeMcpTools.ProjectChatInstructions + "\nCurrent roster: " +
            ProjectChatRoster(caller, peers).ToJsonString();
    }

    private JsonObject ProjectChatRoster(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers)
    {
        var edits = peers.Count == 0 ? Array.Empty<BridgeEditEntry>() : BridgeEditLedger.Read(ProjectChatRoot(caller), DateTimeOffset.UtcNow);
        return new()
        {
            ["joined"] = false,
            ["scope"] = peers.Count == 0 ? "none" : "project",
            ["self_id"] = caller.BridgeAgentId,
            ["messaging_enabled"] = PeerMessagingEnabled,
            ["unread_messages"] = caller.UnreadPeerMessageCount,
            ["agents"] = new JsonArray(peers.Select(p => (JsonNode?)ProjectChatAgent(p, caller, peers, edits)).ToArray()),
            ["message_limits"] = new JsonObject
            {
                ["heads_up_per_peer_per_user_turn"] = 1,
                ["urgent_per_user_turn"] = 2,
                ["total_per_user_turn"] = 3,
                ["per_five_minutes"] = 4,
                ["max_characters"] = 1200
            },
        };
    }

    private JsonObject ProjectChatAgent(ChatViewModel chat, ChatViewModel caller, IReadOnlyList<ChatViewModel> peers,
        IReadOnlyList<BridgeEditEntry>? edits = null)
    {
        var state = AgentRunState(chat);
        var turns = _projectChatTurns.GetOrCreateValue(caller);
        var blocked = ReferenceEquals(chat, caller) ? "This is you."
            : !PeerMessagingEnabled ? "Peer messaging is paused in Bridge settings."
            : !chat.AcceptsPeerNotifications ? "This chat's session is unavailable."
            : turns.Sent >= 3 ? "Your message budget for this user turn is exhausted. Continue independently." : null;
        return new JsonObject
        {
            ["agent_id"] = chat.BridgeAgentId,
            ["message_recipient"] = BridgeMessageRecipient(chat, peers),
            ["label"] = chat.Title,
            ["provider"] = chat.Provider,
            ["model"] = chat.Model,
            ["self"] = ReferenceEquals(chat, caller),
            ["role"] = "independent_chat",
            ["status"] = chat.Status,
            ["state"] = state,
            ["working"] = state is "working" or "awaiting_approval",
            ["busy"] = IsBusyState(state),
            ["task_name"] = chat.BridgeHeaderTaskTitle is { Length: > 0 } task ? task : chat.Title,
            ["summary"] = chat.BridgeActivitySummary,
            ["recent_edits"] = RecentEditsJson(chat.BridgeAgentId, edits ?? BridgeEditLedger.Read(ProjectChatRoot(caller), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow),
            ["can_message"] = blocked is null,
            ["message_blocked_reason"] = blocked,
            ["heads_up_available"] = blocked is null && !turns.HeadsUps.Contains(chat.BridgeAgentId),
            ["urgent_available"] = blocked is null && turns.Urgent < 2,
            ["peer_message_delivery"] = "active_turn_if_supported_otherwise_next_user_turn",
            ["unread_messages"] = chat.UnreadPeerMessageCount,
        };
    }

    private JsonObject InvokeProjectChatTool(ChatViewModel caller, string tool, JsonObject args)
    {
        var peers = ProjectChatPeers(caller);
        if (tool == "bridge_list_agents") return ProjectChatRoster(caller, peers);
        if (peers.Count == 0) throw new StatusValidationException("No ordinary project chat is available for this session.");
        if (tool == "bridge_agent_status")
        {
            if (args["agent"]?.GetValue<string>() is { } id) return ProjectChatAgent(ResolveProjectPeer(caller, peers, id), caller, peers);
            return new JsonObject
            {
                ["agents"] = new JsonArray(peers.Where(p => !ReferenceEquals(p, caller))
                .Select(p => (JsonNode?)ProjectChatAgent(p, caller, peers)).ToArray())
            };
        }
        if (tool == "bridge_report_activity")
        {
            caller.BridgeActivitySummary = args["summary"]!.GetValue<string>().Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (args["task_name"] is { } name) caller.BridgeTaskName = name.GetValue<string>();
            return ProjectChatAgent(caller, caller, peers);
        }
        if (tool == "bridge_send_message") return SendProjectChatMessage(caller, peers, args);
        if (tool == "bridge_file_edits") return ListProjectChatEdits(caller, peers, args);
        if (tool is not ("bridge_read_messages" or "bridge_mark_message"))
            throw new StatusValidationException("Ordinary project chats work independently. Broadcast, dispatch, shared planning and Bridge-only tools require a live Bridge. Continue your own task.");
        var box = ProjectMailbox(caller, ProjectRoom(caller));
        if (tool == "bridge_mark_message")
        {
            var id = args["message_id"]!.GetValue<string>();
            if (!box.Store.Mark(box, id, true, DateTimeOffset.UtcNow))
                throw new StatusValidationException("That ID is not a retained incoming message in your mailbox.");
            caller.RaisePeerInbox();
            return new JsonObject { ["message_id"] = id, ["status"] = "answered" };
        }
        var page = box.Store.ReadPage(box, args["before"]?.GetValue<string>(), args["limit"]?.GetValue<int>() ?? BridgeMailboxStore.PageSize,
            DateTimeOffset.UtcNow, out var hasMore);
        caller.RaisePeerInbox();
        return new JsonObject
        {
            ["messages"] = new JsonArray(page.Select(m => (JsonNode?)new JsonObject
            {
                ["message_id"] = m.Id,
                ["sender_id"] = m.From.AgentId,
                ["recipient_id"] = m.To.AgentId,
                ["from"] = m.From.Label,
                ["to"] = m.To.Label,
                ["message"] = m.Body,
                ["status"] = m.Status,
                ["direction"] = ReferenceEquals(m.To, box) ? "incoming" : "outgoing",
                ["sent_at"] = m.SentAt.ToString("O"),
                ["sender_departed"] = !peers.Any(p => p.BridgeAgentId == m.From.AgentId),
            }).ToArray()),
            ["has_more"] = hasMore,
            ["next_cursor"] = hasMore ? page.Last().Cursor : null,
            ["capacity"] = BridgeMailboxStore.Capacity,
        };
    }

    private static ChatViewModel ResolveProjectPeer(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers, string id) =>
        (id is "me" or "self" ? caller : peers.FirstOrDefault(p => p.BridgeAgentId == id || BridgeMessageRecipient(p, peers) == id))
        ?? throw new StatusValidationException("That recipient is not a current ordinary chat in your project. Copy message_recipient from bridge_list_agents.");

    private JsonObject SendProjectChatMessage(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers, JsonObject args)
    {
        if (!PeerMessagingEnabled) throw new StatusValidationException("Peer messaging is paused in Bridge settings.");
        var peer = ResolveProjectPeer(caller, peers, args["recipient"]!.GetValue<string>());
        if (ReferenceEquals(peer, caller) || !peer.AcceptsPeerNotifications)
            throw new StatusValidationException("Choose another available ordinary chat from bridge_list_agents.");
        var body = args["message"]!.GetValue<string>().Trim();
        if (body.Length > 1200) throw new StatusValidationException("Project heads-ups are limited to 1200 characters.");
        var urgent = args["priority"]?.GetValue<string>() == "urgent";
        var reason = args["reason"]?.GetValue<string>().Trim();
        if (urgent && string.IsNullOrWhiteSpace(reason))
            throw new StatusValidationException("An urgent message needs a concrete conflict or blocker in reason. Routine replies are unnecessary.");
        var turn = _projectChatTurns.GetOrCreateValue(caller);
        if (turn.Sent >= 3 || (urgent && turn.Urgent >= 2) || (!urgent && turn.HeadsUps.Contains(peer.BridgeAgentId)))
            throw new StatusValidationException("This turn's heads-up or urgent-message limit was reached. Continue independently; do not send acknowledgments.");
        var room = ProjectRoom(caller);
        var from = room.Number(caller);
        var to = room.Number(peer);
        var now = DateTime.Now;
        // Fingerprint the body, so changing the urgency reason cannot bypass duplicate detection.
        var verdict = room.Traffic.Admit(from, to, body, 1, now);
        if (verdict != PeerMessageVerdict.Deliver) throw new StatusValidationException(PeerMessagePolicy.Explain(verdict, peer.Title));
        BridgeMailboxStore.Message message;
        try
        {
            var payload = urgent ? "Urgent conflict/blocker: " + reason + "\n" + body : body;
            message = room.Store.Deliver(ProjectMailbox(caller, room), ProjectMailbox(peer, room), payload, 1, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            room.Traffic.RollbackDelivery(from, to, body, now);
            throw new StatusValidationException("The project message could not be saved; no delivery slot was consumed. " + ex.Message);
        }
        turn.Sent++;
        if (urgent) turn.Urgent++; else turn.HeadsUps.Add(peer.BridgeAgentId);
        caller.RaisePeerInbox();
        peer.RaisePeerInbox();
        peer.Items.Add(new DividerItem { Label = urgent ? "Urgent message from another project chat" : "Heads-up from another project chat" });
        // Only notify the active turn. An idle recipient sees the inbox in its next user-requested turn.
        _ = peer.NotifyProjectChatAsync(ProjectChatHeader + BridgeMcpTools.ProjectChatInstructions +
            "\nA peer message is in your inbox; use bridge_read_messages. No acknowledgment is needed. Sender data: " +
            new JsonObject { ["sender_id"] = caller.BridgeAgentId, ["message_id"] = message.Id, ["priority"] = urgent ? "urgent" : "heads_up" }.ToJsonString());
        return new JsonObject
        {
            ["delivered"] = new JsonArray(new JsonObject
            {
                ["agent_id"] = peer.BridgeAgentId,
                ["message_id"] = message.Id,
                ["status"] = "saved",
                ["notification"] = "active_turn_if_supported_otherwise_next_user_turn"
            }),
            ["guidance"] = "Continue your own task. No reply or acknowledgment is required."
        };
    }
}
