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
/// <c>@@MSG agent=N</c>, and the message is delivered into that session as its next turn. It works on a flat roster
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
        // A primary surface can reuse its cleared traffic ledger after CloseBridge. Do not reuse a retired
        // team's filenames (or a failed store) for a new team, even if cleanup left a locked file behind.
        if (_peerMailboxes.TryGetValue(bridge.Peers, out var retired)
            && !bridge.Panes.Any(p => p.PeerMailbox is { Closed: false } b && ReferenceEquals(b.Store, retired)))
            _peerMailboxes.Remove(bridge.Peers);
        var store = _peerMailboxes.GetValue(bridge.Peers,
            _ => new BridgeMailboxStore(bridge.Panes.First().Cwd));
        if (pane.PeerMailbox is { Closed: false } existing && ReferenceEquals(existing.Store, store)) return existing;
        pane.ClearPeerMailbox();
        var box = pane.PeerMailbox = store.Register(BridgeNumberOf(pane), pane.AgentDisplay);
        StagePeerNotice(pane, $"[BRIDGE] Your sent/received peer log is {box.FilePath}. It is app-managed; " +
            "use READ/ANSWERED controls as described in the file instead of editing it.");
        return box;
    }

    private static void HandleMailboxReceipts(ChatViewModel sender, string reply, bool answered = false)
    {
        var verb = answered ? AgentDirectiveParser.AnsweredVerb : AgentDirectiveParser.ReadVerb;
        var controls = AgentDirectiveParser.Scan(reply, verb);
        foreach (var block in controls)
        {
            HideRoutedDirectives(sender, block.Verb);
            if (sender.PeerMailbox is not { Closed: false } box || block.Target != box.Number.ToString())
            {
                StagePeerNotice(sender, "[BRIDGE] Mailbox status was not changed: acknowledge only your own agent number.");
                continue;
            }
            foreach (var id in block.Body.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (!box.Store.Mark(box, id, block.Verb == AgentDirectiveParser.AnsweredVerb, DateTimeOffset.UtcNow))
                        StagePeerNotice(sender, "[BRIDGE] Mailbox status was not changed: the ID is not a retained incoming message.");
                }
                catch (Exception ex) { StagePeerNotice(sender, "[BRIDGE] Could not save mailbox status: " + ex.Message); }
            }
        }
    }

    private static bool PeerMessagingEnabled => AppSettings.Current.BridgePeerMessaging;

    /// <summary>Route every <c>@@MSG</c> block in a pane's finished reply. Called for EVERY pane in a live roster,
    /// manager or not, crowned roster or not — that is the whole difference between this and dispatch.</summary>
    private void RoutePeerMessages(ChatViewModel sender, LiveBridge bridge)
    {
        var panes = bridge.Panes;
        var traffic = bridge.Peers;
        // The inbound hop is consumed whatever happens: if this turn did not relay, the chain ended here.
        _peerHop.Remove(sender, out var inboundHop);
        if (!PeerMessagingEnabled) return;

        var reply = sender.LastTurnReplyText();
        HandleMailboxReceipts(sender, reply);
        // A reply that only CLAIMS to have messaged someone has to get through this gate too. It carries no @@ marker,
        // so the cheap check says "nothing to do" — and that silence is precisely how an agent ends up waiting on an
        // answer it never asked for. Measured as the most common real failure of this channel.
        if (!PeerMessageParser.MentionsMessage(reply) && !PeerMessageParser.ClaimsMessageSent(reply))
        {
            HandleMailboxReceipts(sender, reply, answered: true);
            return;
        }

        var from = BridgeNumberOf(sender);
        if (from <= 0) return;   // an unlabeled pane has no identity to send from

        // Whatever the router decides below, this turn's @@MSG blocks have now been handled — delivered, or refused
        // with an explanation staged for the sender. Either way the raw block has done its job and comes off screen.
        HideRoutedDirectives(sender, AgentDirectiveParser.MessageVerb);

        // Every decision — targets, hop budget, duplicate suppression, rate limiting — is made in the pure planner.
        // This method only maps numbers back onto panes and does the sending.
        var plan = PeerMessageRouter.Plan(from, reply, panes.Select(BridgeNumberOf).Where(n => n > 0).ToList(),
            ManagerNumberIn(panes), inboundHop + 1, traffic, DateTime.Now);

        var problems = new List<string>(plan.Problems);
        var delivered = new List<string>();

        foreach (var (to, body, hop) in plan.Deliveries)
        {
            if (panes.FirstOrDefault(p => BridgeNumberOf(p) == to) is not { } target) continue;

            // A Demon worker may still be waiting in the staggered launch queue; Send is a no-op on a pane with no
            // session yet, and this loop would read that as "not delivered" and drop the message in silence.
            StartQueuedDemonWorker(target);
            if (!target.AcceptsPeerNotifications)
            {
                problems.Add($"agent #{to}: its session is not accepting input right now.");
                continue;
            }

            try
            {
                var senderBox = MailboxFor(sender, bridge);
                var targetBox = MailboxFor(target, bridge);
                var message = senderBox.Store.Deliver(senderBox, targetBox, body, hop, DateTimeOffset.UtcNow);
                target.QueuePeerNotification(message, traffic.Limits, depth => _peerHop[target] = depth);
            }
            catch (Exception ex)
            {
                problems.Add($"agent #{to}: could not save/deliver its mailbox: {ex.Message}");
                continue;
            }
            delivered.Add("#" + to);
            target.Items.Add(new DividerItem { Label = $"💬 Agent {from} sent you a message; view it in agent{to}messages.md" });
            SupervisionLog.Write(sender.BridgeLabel, "PEER-MSG",
                $"→ #{to} (hop {hop}/{traffic.Limits.MaxHops}): {AgentSupervisionPolicy.Excerpt(body, 140)}");
            NoteBridgeActivity();
        }

        if (delivered.Count > 0)
            sender.Items.Add(new DividerItem { Label = $"💬 messaged agent {string.Join(", ", delivered.Distinct())} · logged in agent{from}messages.md" });

        if (problems.Count == 0 && !plan.Malformed)
            HandleMailboxReceipts(sender, reply, answered: true);
        else if (AgentDirectiveParser.Mentions(reply, AgentDirectiveParser.AnsweredVerb))
        {
            HideRoutedDirectives(sender, AgentDirectiveParser.AnsweredVerb);
            StagePeerNotice(sender, "[BRIDGE] ANSWERED status was not saved because an outgoing reply was not delivered. " +
                "Retry the reply or handle the request before marking it answered.");
        }

        // Record whether THIS turn actually reached anyone before deciding what to tell the sender.
        var sentThisTurn = delivered.Count > 0;
        var sentLastTurn = _peerSentLastTurn.Contains(sender);
        if (sentThisTurn) _peerSentLastTurn.Add(sender); else _peerSentLastTurn.Remove(sender);

        // A claim right after a real delivery is the agent referring back to it, not a stall. Only correct it when
        // nothing has actually gone out.
        var stalledOnAClaim = plan.ClaimedWithoutSending && !sentThisTurn && !sentLastTurn;

        // An agent that wrote the verb and reached nobody must be told, or it waits forever for an answer that was
        // never asked for. Staged on the Prelude, never Sent: explaining a bounced message must not cost a turn of
        // its own, which would make the failure path more expensive than the feature.
        if (problems.Count > 0)
        {
            StagePeerNotice(sender, PeerMessagePolicy.SenderNotice(problems));
            sender.Items.Add(new DividerItem { Label = $"💬 {problems.Count} peer message(s) not delivered" });
            foreach (var p in problems) SupervisionLog.Write(sender.BridgeLabel, "PEER-MSG-BLOCKED", p);
        }
        else if (plan.Malformed || stalledOnAClaim)
        {
            var roster = string.Join(", ", panes.Where(p => !ReferenceEquals(p, sender))
                .Select(BridgeNumberOf).Where(n => n > 0).Select(n => "#" + n));
            StagePeerNotice(sender, stalledOnAClaim
                ? PeerMessagePolicy.UnsentClaimNotice(roster)
                : PeerMessagePolicy.MalformedNotice(roster));
            sender.Items.Add(new DividerItem
            {
                Label = stalledOnAClaim
                    ? "💬 peer message not delivered — the reply described one instead of sending it"
                    : "💬 peer message not delivered — nothing parsed in that reply",
            });
            SupervisionLog.Write(sender.BridgeLabel, "PEER-MSG-FAILED", stalledOnAClaim
                ? "reply claimed a peer had been messaged but wrote no block"
                : "reply named @@MSG but matched no agent");
        }
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
        pane.ClearPeerMailbox();
    }
}
