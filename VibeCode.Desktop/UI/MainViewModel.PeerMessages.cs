using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Agent-to-agent messaging inside a bridge — the sideways channel.
///
/// Dispatch is top-down and it is the only channel a bridge had: the crowned manager writes work orders, the app
/// delivers them, workers report back up. Everything sideways had to go through the status board, which is a file
/// nobody is obliged to re-read, so an agent that hit something outside its lane had exactly two options — do the work
/// anyway in someone else's area, or drop it. Both are how a bridge quietly produces conflicting edits.
///
/// This is the third option. Any agent can address any other agent (or the manager, or the whole roster) with
/// the built-in bridge MCP tools, and the message is queued as a control turn. It works on a flat roster
/// with no manager at all, which is the case the board was worst at.
///
/// The whole risk here is the loop: every message costs the recipient a real provider turn, so two agents thanking
/// each other is an unbounded spend that neither of them can see from the inside. <see cref="PeerTrafficLedger"/>
/// holds the bounds; this file's job is to enforce them at the only place a message can enter a session, and to make
/// sure a message that is refused is always explained to the agent that sent it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The PRIMARY surface's roster budget. Every other live roster carries its own on its
    /// <c>LiveBridge</c>, and this field is swapped out with the roster when one is parked or restored — the ledger's
    /// keys are bare agent numbers, so one roster's #1→#2 is indistinguishable from another's unless they are kept
    /// apart. Not readonly for exactly that reason.</summary>
    private PeerTrafficLedger _peerTraffic = new();

    /// <summary>How deep in a peer-message chain each pane's CURRENT turn is. Set when a message is delivered and
    /// consumed at that pane's next turn end, so a chain only continues through the turn it actually caused — an
    /// agent that gets a message, ignores it, and messages somebody else three turns later starts from zero.</summary>
    private readonly Dictionary<ChatViewModel, int> _peerHop = new();

    /// <summary>Panes whose PREVIOUS turn actually delivered a peer message. An agent that really did message someone
    /// and then refers to it ("I asked #3, still waiting") must not be told nothing was sent — that correction would
    /// be false, and acting on it means sending the same question twice, which costs a peer a real turn. One turn of
    /// grace: a second consecutive claim with nothing delivered is a genuine stall and does get the notice.</summary>
    private readonly HashSet<ChatViewModel> _peerSentLastTurn = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PeerTrafficLedger, BridgeMailboxStore>
        _peerMailboxes = new();

    private BridgeMailboxStore.Mailbox MailboxFor(ChatViewModel pane, LiveBridge bridge)
    {
        var workspace = bridge.Panes.First().Cwd;
        if (!AppSettings.Current.BridgeMailboxWorkspaces.Contains(workspace, StringComparer.OrdinalIgnoreCase))
        {
            AppSettings.Current.BridgeMailboxWorkspaces.Add(workspace);
            RequestSave();
        }
        // A primary surface can reuse its cleared traffic ledger after CloseBridge. Do not reuse a retired
        // team's filenames (or a failed store) for a new team, even if cleanup left a locked file behind.
        if (_peerMailboxes.TryGetValue(bridge.Peers, out var retired)
            && !bridge.Panes.Any(p => p.PeerMailbox is { Closed: false } b && ReferenceEquals(b.Store, retired)))
            _peerMailboxes.Remove(bridge.Peers);
        var store = _peerMailboxes.GetValue(bridge.Peers,
            _ => new BridgeMailboxStore(bridge.Panes.First().Cwd, WorkFor(bridge.Panes).RunId));
        if (pane.PeerMailbox is { Closed: false } existing && ReferenceEquals(existing.Store, store)) return existing;
        pane.ClearPeerMailbox();
        var box = pane.PeerMailbox = store.Register(BridgeNumberOf(pane), pane.AgentDisplay, pane.BridgeAgentId);
        StagePeerNotice(pane, "[BRIDGE] Read your inbox with bridge_read_messages; use bridge_mark_message after handling an incoming message.");
        return box;
    }

    private static bool PeerMessagingEnabled => AppSettings.Current.BridgePeerMessaging;

    // MCP calls deliver immediately. End-of-turn text is never executed as a message command.
    private void RoutePeerMessages(ChatViewModel sender, LiveBridge bridge)
    {
        _peerHop.Remove(sender);
        _bridgeMcpSends.Remove(sender);
    }

    /// <summary>
    /// Drop the <c>@@</c> blocks of a finished turn from what that pane DISPLAYS, now that the app has acted on them.
    ///
    /// Called for every pane whose turn just ended, dispatcher or peer, because both kinds of block have the same
    /// problem on screen: the app delivers the message and adds a divider saying so, and the raw block underneath is
    /// then the same text a second time — trailing "@@END" and all — wedged into the middle of the agent's report.
    /// The item keeps its original text, so nothing that re-reads the turn is affected.
    /// </summary>
    private static void HideRoutedDirectives(ChatViewModel pane, string verb)
    {
        for (var i = pane.Items.Count - 1; i >= 0; i--)
        {
            if (pane.Items[i] is UserItem) break;   // the start of this turn
            if (pane.Items[i] is TextItem { HasText: true } t) t.HideRoutedDirective(verb);
        }
    }

    private static void StagePeerNotice(ChatViewModel pane, string notice) =>
        pane.Prelude = string.IsNullOrEmpty(pane.Prelude) ? notice : pane.Prelude + "\n" + notice;

    /// <summary>A manager work order starts a brand new chain of thought, so it must not inherit the hop depth of
    /// whatever peer conversation the worker was in.</summary>
    private void ResetPeerChain(ChatViewModel pane) => _peerHop.Remove(pane);

    /// <summary>Forget a pane's chain state when it leaves a roster.</summary>
    private void ForgetPeerChain(ChatViewModel pane) => _peerHop.Remove(pane);

    /// <summary>Everything this file remembers about one pane, dropped because it has left the roster for good.
    /// Both maps are keyed by pane, so a chat that leaves a bridge and later joins another one would otherwise
    /// start that bridge carrying the last one's chain depth and "already messaged someone" grace.</summary>
    private void ForgetPeerState(ChatViewModel pane)
    {
        pane.ClearPeerChatAccess();
        _peerHop.Remove(pane);
        _peerSentLastTurn.Remove(pane);
        _bridgeMcpSends.Remove(pane);
        pane.ClearPeerMailbox();
    }
}
