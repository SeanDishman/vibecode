using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>project_read_chat: an ordinary chat views the other chats open in its own directory. The project roster
/// (<see cref="ProjectChatPeers"/>) is resolved again on every page, so another directory, a Bridge member, a closed
/// chat or a no-directory scratch chat is never reachable, and a cursor never selects a file or another caller's snapshot.</summary>
public sealed partial class MainViewModel
{
    private JsonObject ReadProjectChat(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers, JsonObject args)
    {
        var requested = args["agent"]?.GetValue<string>();
        var cursor = args["cursor"]?.GetValue<string>();
        var view = args["view"]?.GetValue<string>();
        if (requested is null)
        {
            if (cursor is not null || view is not null || args["term"] is not null)
                throw new StatusValidationException("cursor, view and term read one chat: pass agent with them, or omit them all to list the chats in this directory.");
            return ListProjectChats(caller, peers);
        }
        var target = ResolveProjectChat(caller, peers, requested);
        if (target.IsPeerChatHistoryLoading)
            throw new StatusValidationException("This chat is still loading its earlier conversation. Read it after loading finishes to get the full transcript.");
        var room = ProjectRoom(caller);
        var number = room.Number(target);
        BridgeChatSnapshot Snapshot() => BridgeChatMirror.Capture(target, number, active: true) with { ChatId = target.BridgeAgentId };
        if (view is null)
        {
            if (args["term"] is not null) throw new StatusValidationException("term is only used with view=query.");
            // Keyed by this directory's room, then by caller, as Bridge snapshots are keyed by their roster's ledger.
            var reader = _peerChatReaders.GetValue(room.Traffic, _ => new()).GetValue(caller, _ => new());
            return reader.Read(target.BridgeAgentId, cursor, Snapshot, DateTimeOffset.UtcNow,
                scope: "project", tool: BridgeMcpTools.ProjectReadChatToolName);
        }
        if (cursor is not null)
            throw new StatusValidationException("cursor pages the full read. Omit it with view, or omit view to keep paging.");
        var page = BridgeChatViews.Build(view, args["term"]?.GetValue<string>(), Snapshot(), target.RecoveryCheckpoint);
        page["scope"] = "project";
        return page;
    }

    private JsonObject ListProjectChats(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["scope"] = "project", ["project"] = ProjectChatRoot(caller), ["self_id"] = caller.BridgeAgentId,
            ["chats"] = new JsonArray(peers.Select(p => (JsonNode?)WithAgentActivity(new JsonObject
            {
                ["agent_id"] = p.BridgeAgentId, ["message_recipient"] = BridgeMessageRecipient(p, peers),
                ["title"] = p.Title, ["provider"] = p.Provider, ["model"] = p.Model,
                ["self"] = ReferenceEquals(p, caller), ["status"] = p.Status,
                ["task_name"] = p.BridgeHeaderTaskTitle is { Length: > 0 } task ? task : p.Title,
                ["summary"] = p.BridgeActivitySummary,
                ["message_count"] = BridgeChatMirror.MessageCount(p),
                ["checkpoint_saved"] = p.RecoveryCheckpoint is not null,
                ["history_loading"] = p.IsPeerChatHistoryLoading,
            }, p, now)).ToArray()),
            ["guidance"] = "Pass one chat's agent_id or message_recipient as agent. view=checkpoint, user_messages, tail or query returns one bounded page; " +
                "without view the full transcript starts, then keep passing next_cursor as cursor until has_more=false. " +
                "Only open ordinary chats in this exact directory are listed. Listing and reading never message or wake anyone.",
        };
    }

    /// <summary>An exact agent_id or message_recipient, me, or a title only one chat in this directory has.</summary>
    private static ChatViewModel ResolveProjectChat(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers, string requested)
    {
        var value = requested.Trim();
        if (value.ToLowerInvariant() is "me" or "self" or "myself") return caller;
        if (peers.FirstOrDefault(p => p.BridgeAgentId == value || BridgeMessageRecipient(p, peers) == value) is { } exact) return exact;
        var titled = peers.Where(p => string.Equals(p.Title.Trim(), value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (titled.Length == 1) return titled[0];
        throw new StatusValidationException(titled.Length > 1
            ? $"{titled.Length} chats in this directory are titled '{value}'. Pass the agent_id or message_recipient from {BridgeMcpTools.ProjectReadChatToolName}'s listing instead."
            : $"That chat is not open in this project directory. It may have closed, joined a Bridge or belong to another directory. Call {BridgeMcpTools.ProjectReadChatToolName} without agent to list the chats you can view.");
    }
}
