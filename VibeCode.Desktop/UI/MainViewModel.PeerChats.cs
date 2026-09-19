using System.Runtime.CompilerServices;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    // The ledger follows a roster when it is parked or moved to the second display. Pane numbers and the
    // shared default status-board filename cannot identify a roster: both are reused by unrelated bridges.
    private readonly ConditionalWeakTable<PeerTrafficLedger, BridgeChatStore> _peerChats = new();

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
