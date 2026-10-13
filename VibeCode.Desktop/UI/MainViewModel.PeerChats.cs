using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    // The ledger follows a roster when it is parked or moved to the second display. Pane numbers and the
    // shared default status-board filename cannot identify a roster: both are reused by unrelated bridges.
    private readonly ConditionalWeakTable<PeerTrafficLedger, BridgeChatStore> _peerChats = new();
    private readonly ConditionalWeakTable<PeerTrafficLedger, ConditionalWeakTable<ChatViewModel, BridgeChatReader>> _peerChatReaders = new();

    private JsonObject ReadBridgeChat(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        var requested = args["agent"]!.GetValue<string>();
        string id;
        try { id = EditAgentId(caller, bridge, requested); }
        catch (StatusValidationException) { throw ChatNotFound(); }
        // Edit history accepts departed IDs; transcript access must still resolve to this live roster on every page.
        var target = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == id && p.Status != "closed")
            ?? throw ChatNotFound();
        if (target.IsPeerChatHistoryLoading)
            throw new StatusValidationException("This chat is still loading its earlier conversation. Read it after loading finishes to get the full transcript.");
        var reader = _peerChatReaders.GetValue(bridge.Peers, _ => new()).GetValue(caller, _ => new());
        return reader.Read(id, args["cursor"]?.GetValue<string>(),
            () => BridgeChatMirror.Capture(target, BridgeNumberOf(target), active: true) with { ChatId = id }, DateTimeOffset.UtcNow);

        static StatusValidationException ChatNotFound() => new(
            "That agent is not a live member of your Bridge. Use an agent_id or message_recipient from bridge_list_agents.");
    }

    private void RefreshBridgeChatAccess(ChatViewModel requestingPane)
    {
        if (!requestingPane.IsBridgeAgent || !TryGetLiveBridge(requestingPane, out var bridge)) return;
        try
        {
            if (_peerChats.TryGetValue(bridge.Peers, out var retired)
                && !bridge.Panes.Any(p => ReferenceEquals(p.PeerChatMirror?.Store, retired)))
                _peerChats.Remove(bridge.Peers);
            var store = _peerChats.GetValue(bridge.Peers, _ => new BridgeChatStore(bridge.Panes[0].Cwd));
            foreach (var pane in bridge.Panes)
            {
                if (ReferenceEquals(pane.PeerChatMirror?.Store, store)) continue;
                pane.ClearPeerChatAccess();
                pane.PeerChatMirror = new BridgeChatMirror(pane, store, () => BridgeNumberOf(pane));
                // Running hosts have a frozen system prompt; this notice reaches their next ordinary turn.
                // New peers also receive the skill through their startup system prompt.
                if (pane.HasRunningPeerChatSession)
                    StagePeerNotice(pane, store.Instructions);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            if (requestingPane.PeerChatSetupWarningShown) return;
            requestingPane.PeerChatSetupWarningShown = true;
            requestingPane.Items.Add(new BannerItem { Level = "warning", Text = "Peer chat context is unavailable: " + ex.Message });
        }
    }
}
