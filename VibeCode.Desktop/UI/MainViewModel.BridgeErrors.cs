using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    // Diagnostic history only, scoped to this live Bridge. Never authorizes delivery or exposes another roster.
    private readonly ConditionalWeakTable<BridgeWorkState, List<string>> _knownBridgeRecipients = new();

    private void RememberBridgeRecipients(LiveBridge bridge)
    {
        var known = _knownBridgeRecipients.GetOrCreateValue(WorkFor(bridge.Panes));
        foreach (var peer in bridge.Panes)
        {
            known.Remove(peer.BridgeAgentId);
            known.Add(peer.BridgeAgentId);
        }
        if (known.Count > 256) known.RemoveRange(0, known.Count - 256);
    }

    private BridgeMcpException MissingBridgeRecipient(ChatViewModel caller, LiveBridge bridge, string recipient)
    {
        var self = recipient == caller.BridgeAgentId || recipient == BridgeMessageRecipient(caller, bridge.Panes);
        var known = _knownBridgeRecipients.GetOrCreateValue(WorkFor(bridge.Panes));
        var departed = !self && known.Count(id => id == recipient || (id.Length > 12 && "peer:" + id[..12] == recipient)) == 1;
        return new BridgeMcpException(self ? "recipient_is_self" : departed ? "recipient_left" : "recipient_not_found",
            self ? "The recipient is your own chat. A peer message must address another agent."
                : departed ? "That agent was in this Bridge but is no longer a member. Its chat may have closed or left the Bridge. Nothing was delivered."
                : "That recipient is not in your current Bridge. The address may be stale, mistyped, or from a different Bridge. Nothing was delivered.",
            "Call bridge_list_agents once and copy an eligible peer's exact message_recipient. Do not guess an agent number or wait for a removed teammate.",
            context: new JsonObject { ["recipient"] = recipient, ["scope"] = "bridge", ["delivery_status"] = "not_delivered" });
    }

    private static string UnavailablePeerReason(ChatViewModel peer) => peer.Status == "closed"
        ? "The recipient chat is closed and cannot receive messages."
        : peer.PeerMailbox?.Store.Available == false ? "The recipient mailbox is unavailable after a storage failure."
        : peer.Status == "error" ? "The recipient session is in an error state and is not accepting messages."
        : "The recipient's provider session is disconnected or has exited and is not accepting messages.";
}
