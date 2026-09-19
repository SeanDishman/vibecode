using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>Peer messages lifted out of any agent's reply — the sideways channel, as opposed to the manager's
/// top-down <see cref="DispatchBlockParser"/> work orders.</summary>
public static class PeerMessageParser
{
    public static bool MentionsMessage(string? reply) =>
        AgentDirectiveParser.Mentions(reply, AgentDirectiveParser.MessageVerb);

    public static List<DirectiveBlock> Parse(string? reply) =>
        AgentDirectiveParser.Scan(reply, AgentDirectiveParser.MessageVerb);

    /// <summary>
    /// The reply CLAIMS it already messaged a peer — "I've asked agent #4…", "Message sent to agent #3" — without
    /// writing any block at all.
    ///
    /// Measured, not guessed: over real Haiku trials this was the single most common way the channel failed. The model
    /// does not refuse to message; it narrates the message in past tense and then waits for an answer nobody was ever
    /// asked for. <see cref="MentionsMessage"/> cannot catch it because there is no <c>@@</c> marker anywhere in the
    /// reply, so the router used to score the turn "nothing to do" and the agent deadlocked in silence — the exact
    /// outcome this whole channel exists to make impossible.
    /// </summary>
    public static bool ClaimsMessageSent(string? reply)
    {
        if (string.IsNullOrEmpty(reply)) return false;
        var prose = Quoted.Replace(reply, " ");
        return SentClaim.IsMatch(prose) || SentPassive.IsMatch(prose) || SentSomething.IsMatch(prose)
               || SentToPronoun.IsMatch(prose) || SubjectlessClaim.IsMatch(prose);
    }

    /// <summary>
    /// Quoted spans are stripped before any pattern runs, because QUOTING a claim is not making one. An agent writing
    /// about this channel — explaining the failure mode, reporting what a peer's reply said, documenting the rule —
    /// puts the phrase in quotes or backticks, and every pattern below matches it happily. That was not theoretical:
    /// a reply explaining that <c>"I've messaged them"</c> is a phrasing the detector used to miss tripped the
    /// detector on that very sentence, and the sender was told it had claimed something it plainly had not.
    ///
    /// Single quotes are deliberately absent. An apostrophe is the same character, so <c>'[^']*'</c> would treat the
    /// gap between "I've" and the next apostrophe as quoted and eat the claim it was meant to find.
    /// </summary>
    private static readonly Regex Quoted = new(
        "\"[^\"\n]*\"|“[^”\n]*”|`[^`\n]*`",
        RegexOptions.Compiled);

    /// <summary>
    /// A subordinating conjunction immediately before the claim means the agent is REASONING about a message rather
    /// than reporting one — "If I sent a message to agent #3 now it would just interrupt them" is a decision NOT to
    /// send, and the tense is past only because English puts conditionals there. Telling that agent it claimed to
    /// message a peer is simply false, and it costs the agent its next turn arguing with the app. The optional
    /// article covers the passive spelling, where the subject is the noun: "unless a message was sent to agent #3".
    /// </summary>
    private const string NotConditional =
        @"(?<!\b(?:if|unless|whether|when|once|though|although|before|until)\s(?:(?:a|an|the|any)\s)?)";

    // First person and COMPLETED, aimed at a peer. Future and conditional forms ("I'll ask agent #3", "I will ask",
    // "I am asking") deliberately do not match: those are an agent saying what it intends to do next, which is not a
    // claim that anything was sent and must not draw a correction.
    private static readonly Regex SentClaim = new(
        NotConditional +
        @"\b(?:i|we)\s*(?:'ve|’ve|\s+have)?\s*(?:just\s+|already\s+)*" +
        @"(?:sent(?:\s+(?:a|the))?(?:\s+\w+)?\s+(?:message|note|question|request)?\s*(?:to)?|" +
        @"messaged|pinged|notified|contacted|asked|queried|" +
        @"reached\s+out\s+to|escalated\s+(?:this\s+|it\s+)?to|handed\s+(?:this\s+|it\s+)?(?:off\s+)?to)\s+" +
        @"(?:the\s+)?(?:manager|agent\b|#\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The passive spelling models actually produced: "Message sent to agent #4 requesting clarification."
    private static readonly Regex SentPassive = new(
        NotConditional +
        @"\b(?:message|msg|request|question|note|escalation|ping)\s+(?:has\s+been\s+|was\s+)?" +
        @"(?:sent|escalated|raised|passed)\s+to\s+(?:the\s+)?(?:manager|agent\b|#\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Waiting for agent #4 to clarify… I sent a message asking what a, b and c represent." The recipient is named
    // BEFORE the verb here, so the two patterns above (which want the target straight after it) both miss. Inside a
    // bridge agent's reply "I sent a message" has no other plausible meaning, so no target is required — and this is
    // the phrasing the fixed prompt still lost one trial in three to.
    //
    // The subject and the verb are allowed to drift apart, because they do: "I've established the blocker and sent a
    // coordination message to agent #3" came back verbatim from a trial and every earlier pattern missed it, purely
    // because six words sat between "I've" and "sent". The gap is tempered rather than open — it may not cross a
    // sentence, may not contain a negation or a conditional, and may not name another actor, since in "I saw that
    // agent #4 sent a message" the sender is somebody else and correcting this agent would be a lie.
    private static readonly Regex SentSomething = new(
        NotConditional + @"\b(?:i|we)\b" +
        @"(?:(?!\b(?:not|never|unable|cannot|if|will|would|could|should|agent|manager|peer|user|you|they|he|she)\b|n't)" +
        @"[^.!?\n]){0,60}?" +
        // Up to two words between the article and the noun: "a peer message", "a quick coordination note".
        @"\bsent\s+(?:a|the|an)\s+(?:\w+\s+){0,2}(?:message|msg|note|question|request|prompt|ping|escalation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The terse status voice, with the subject dropped entirely: "Done. Messaged Agent #3 requesting the column."
    /// / "Sent a request for them to add users.last_seen_at." Every other pattern here anchors on "I" or "we", so a
    /// reply written as a bulleted status report slips through all of them — and this turned out to be the single
    /// biggest hole left. In one scenario measured over five trials the agent believed it had handed work to a peer
    /// four times and actually sent nothing four times, undetected, because it wrote like a changelog.
    ///
    /// A recipient is required, and it has to be a peer rather than anything at all: "Sent a request to the
    /// endpoint and got a 401" is ordinary API work, and an agent in the API lane writes that sentence constantly.
    /// </summary>
    private static readonly Regex SubjectlessClaim = new(
        @"(?:^|[.!?]\s+|[-*•]\s+)\s*(?:just\s+|already\s+)*" +
        @"(?:(?:messaged|pinged|notified|contacted|alerted|escalated\s+to)\s+(?:the\s+)?(?:manager|agent\b|#\d)" +
        @"|sent\s+(?:a|an|the)\s+(?:\w+\s+){0,2}(?:message|msg|note|question|request|ping|escalation)\s+" +
        @"(?:to|for)\s+(?:the\s+)?(?:manager|agent\b|#\d|them\b))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    // "I've messaged them to clarify what a, b and c represent." Same deadlock, and every pattern above misses it for
    // one reason: the target is a PRONOUN, and they all require the recipient to be spelled "agent #N" or "manager".
    // Kept deliberately narrow — only verbs that cannot mean anything except contacting somebody. "I asked them" is
    // excluded on purpose: on a turn that also addresses the user, "them" is genuinely ambiguous, and a false
    // correction costs an innocent agent a whole turn arguing with the app.
    private static readonly Regex SentToPronoun = new(
        @"\b(?:i|we)\s*(?:'ve|’ve|\s+have)?\s*(?:just\s+|already\s+)*" +
        @"(?:messaged|pinged|notified|contacted|alerted)\s+" +
        @"(?:them|him|her|both|the\s+others?|the\s+other\s+agents?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

/// <summary>Why a peer message was not delivered. Every non-<see cref="Deliver"/> value is reported back to the
/// sender — a message that vanishes with no explanation is how an agent ends up waiting forever for an answer.</summary>
public enum PeerMessageVerdict
{
    Deliver,
    /// <summary>Addressed to itself. Always a mistake, never worth a turn.</summary>
    SelfAddressed,
    /// <summary>The chain of peer messages that led here is already as long as it is allowed to get.</summary>
    HopLimit,
    /// <summary>This reply already spent its budget of outbound messages.</summary>
    TurnLimit,
    /// <summary>This sender has sent too many peer messages too recently.</summary>
    RateLimit,
    /// <summary>The same sender already sent the same text to the same recipient inside the window.</summary>
    Duplicate,
}

/// <summary>
/// The bounds on agent-to-agent messaging.
///
/// A sideways channel between autonomous agents has one catastrophic failure mode and it is not "a message gets
/// lost": it is two agents politely acknowledging each other forever. Each message costs a full provider turn on the
/// receiving side, so an unbounded ping-pong burns real money at machine speed with nothing to show for it and no
/// natural stopping point — neither agent is doing anything wrong, and neither can see the loop from inside it.
/// These numbers are what makes the channel safe to leave switched on.
/// </summary>
public readonly record struct PeerMessageLimits(int MaxHops, int MaxBlocksPerTurn, int MaxPerWindow, TimeSpan Window)
{
    /// <summary>Hops bound one conversation: A→B→A→B and then the chain is cut. Four is enough for a real
    /// question-and-answer plus a clarification, and far short of anything that reads as a loop.</summary>
    public static PeerMessageLimits Default => new(
        MaxHops: 4,
        MaxBlocksPerTurn: 3,
        MaxPerWindow: 20,
        Window: TimeSpan.FromMinutes(5));
}

/// <summary>
/// The running record of who has messaged whom, and the gate every peer message passes through. Kept free of any UI
/// type so the rules can be exercised directly.
/// </summary>
public sealed class PeerTrafficLedger
{
    private readonly record struct Entry(int From, int To, string Fingerprint, DateTime At);

    private readonly List<Entry> _sent = new();
    private int _turnBlocks;

    public PeerTrafficLedger(PeerMessageLimits? limits = null) => Limits = limits ?? PeerMessageLimits.Default;

    public PeerMessageLimits Limits { get; }

    /// <summary>Called once when a reply starts being routed. The per-turn budget is what stops one reply from
    /// fanning out to the whole roster over and over.</summary>
    public void BeginTurn() => _turnBlocks = 0;

    /// <summary>Claim one of this turn's message slots. False once the reply has spent its budget.</summary>
    public bool AdmitBlock()
    {
        if (_turnBlocks >= Limits.MaxBlocksPerTurn) return false;
        _turnBlocks++;
        return true;
    }

    /// <summary>Decide one delivery. Recording happens here too, so a caller cannot admit a message and forget to
    /// count it against the window.</summary>
    public PeerMessageVerdict Admit(int from, int to, string body, int hop, DateTime now)
    {
        if (from == to) return PeerMessageVerdict.SelfAddressed;
        if (hop > Limits.MaxHops) return PeerMessageVerdict.HopLimit;

        Prune(now);
        var fingerprint = Fingerprint(body);
        if (_sent.Any(e => e.From == from && e.To == to && e.Fingerprint == fingerprint))
            return PeerMessageVerdict.Duplicate;
        if (_sent.Count(e => e.From == from) >= Limits.MaxPerWindow)
            return PeerMessageVerdict.RateLimit;

        _sent.Add(new Entry(from, to, fingerprint, now));
        return PeerMessageVerdict.Deliver;
    }

    /// <summary>Forget everything. Used when a roster is torn down, so a fresh team starts on a fresh budget.</summary>
    public void Clear()
    {
        _sent.Clear();
        _turnBlocks = 0;
    }

    private void Prune(DateTime now) => _sent.RemoveAll(e => now - e.At > Limits.Window);

    /// <summary>Whitespace-insensitive, case-insensitive identity of a message body. Two agents relaying the same
    /// text back and forth is the cheapest loop to detect, and the one models fall into most.</summary>
    public static string Fingerprint(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var collapsed = Regex.Replace(body.Trim(), @"\s+", " ");
        return collapsed.Length <= 400 ? collapsed.ToLowerInvariant() : collapsed[..400].ToLowerInvariant();
    }
}

/// <summary>One message the router decided to deliver.</summary>
public readonly record struct PeerDelivery(int To, string Body, int Hop);

/// <summary>What a single reply's worth of <c>@@MSG</c> blocks turned into.</summary>
public sealed class PeerRoutingPlan
{
    public List<PeerDelivery> Deliveries { get; } = new();
    /// <summary>Human-readable reasons for everything that did NOT get delivered, in the order written.</summary>
    public List<string> Problems { get; } = new();
    /// <summary>The reply named the verb but produced no parsable block at all.</summary>
    public bool Malformed { get; init; }

    /// <summary>The reply claimed a peer had already been messaged, but wrote no block. Distinguished from
    /// <see cref="Malformed"/> because the correction differs: this agent did not fumble the syntax, it never
    /// reached for it — it described the message instead of sending one.</summary>
    public bool ClaimedWithoutSending { get; init; }
}

/// <summary>
/// Turns a reply into a delivery plan. Deliberately pure — it knows roster NUMBERS, not panes, and touches no UI
/// type — because every rule that matters here is a rule about loops and budgets, and rules that can only be
/// exercised by spawning real provider sessions are rules that never get exercised at all.
/// </summary>
public static class PeerMessageRouter
{
    /// <param name="from">The sending agent's roster number.</param>
    /// <param name="roster">Every live agent number, including the sender.</param>
    /// <param name="managerNumber">The crowned agent's number, or 0 on a flat roster.</param>
    /// <param name="hop">Depth of the chain this reply continues (1 when the sender was not answering a peer).</param>
    public static PeerRoutingPlan Plan(int from, string? reply, IReadOnlyList<int> roster, int managerNumber,
        int hop, PeerTrafficLedger ledger, DateTime now)
    {
        var blocks = PeerMessageParser.Parse(reply);
        if (blocks.Count == 0)
        {
            // Fumbled syntax and "I already asked them" are both an agent that believes it has a question in flight.
            // Malformed takes precedence: if it wrote the verb, telling it about the block layout is the useful note.
            var mentioned = PeerMessageParser.MentionsMessage(reply);
            return new PeerRoutingPlan
            {
                Malformed = mentioned,
                ClaimedWithoutSending = !mentioned && PeerMessageParser.ClaimsMessageSent(reply),
            };
        }

        var others = roster.Where(n => n != from && n > 0).Distinct().ToList();
        var plan = new PeerRoutingPlan();
        ledger.BeginTurn();

        foreach (var block in blocks)
        {
            if (!ledger.AdmitBlock())
            {
                plan.Problems.Add(PeerMessagePolicy.Explain(PeerMessageVerdict.TurnLimit, $"agent={block.Target}"));
                continue;
            }

            var parsed = DirectiveTargets.Parse(block.Target);
            List<int> targets;
            if (parsed.Manager)
            {
                targets = managerNumber > 0 && managerNumber != from ? new List<int> { managerNumber } : new();
                if (targets.Count == 0)
                {
                    plan.Problems.Add(managerNumber == from
                        ? "agent=manager: that is you — you ARE the manager of this bridge."
                        : "agent=manager: this bridge has no manager right now, so there was nobody to escalate to.");
                    continue;
                }
            }
            else if (parsed.All)
            {
                targets = others;
                if (targets.Count == 0)
                {
                    plan.Problems.Add("agent=all: you are the only agent on this bridge right now.");
                    continue;
                }
            }
            else
            {
                targets = parsed.Numbers.Where(roster.Contains).ToList();
                var unknown = parsed.Numbers.Where(n => !roster.Contains(n)).ToList();
                if (unknown.Count > 0)
                    plan.Problems.Add($"agent={string.Join(",", unknown)}: not on the roster " +
                                      $"(live agents: {(others.Count > 0 ? string.Join(", ", others.Select(n => "#" + n)) : "none")}).");
                if (targets.Count == 0) continue;
            }

            foreach (var to in targets)
            {
                var verdict = ledger.Admit(from, to, block.Body, hop, now);
                if (verdict == PeerMessageVerdict.Deliver) plan.Deliveries.Add(new PeerDelivery(to, block.Body, hop));
                else plan.Problems.Add(PeerMessagePolicy.Explain(verdict, $"agent #{to}"));
            }
        }
        return plan;
    }
}

/// <summary>The text an agent actually reads on either end of the channel.</summary>
public static class PeerMessagePolicy
{
    public static string MailboxInstructions(int number) =>
        "\n[BRIDGE MAILBOX] The notification contains only a link; read that file to get the peer's message. " +
        "It lists the latest five sent/received messages, newest first. Do not edit this app-managed file. " +
        "Only incoming unread messages need reading; read does not mean answered. " +
        $"After reading, emit this control block (one or more incoming IDs on separate lines):\n@@READ agent={number}\n" +
        "<message ID>\n@@END\n" +
        $"After actually answering or handling one, mark it with @@ANSWERED agent={number} using the same ID/body layout. " +
        "Never mark a message answered just for opening the file. Status controls update the log without sending a peer turn. " +
        "Use the existing @@MSG format only when a substantive reply is needed; do not send acknowledgment chatter. " +
        "The notification does not authorize stopping ongoing work or sending pending user requests. " +
        "Use the current agent number noted in the log if the roster was renumbered.\n";

    public static string MailboxWire(string senderLabel, int senderNumber, string notice, int recipientNumber,
        int hop, PeerMessageLimits limits) =>
        $"{WirePrefix} #{senderNumber} — {senderLabel}] Peer message:\n\n{notice}\n\n" +
        "— This came from another agent on your bridge, not from the user or a manager work order. " +
        "Read the linked mailbox for the actual request; opening a notification alone is not reading the message. " +
        "Respond only when needed and within your area. Reply using @@MSG agent=N with N set to THAT MESSAGE'S " +
        "sender's CURRENT live number from the file. A departed sender cannot receive a reply; do not address a " +
        "different agent that inherited their old number. For several senders, handle each independently. " +
        $"This chain is {hop} message(s) deep; {Math.Max(0, limits.MaxHops - hop)} more hop(s) may be delivered. " +
        MailboxInstructions(recipientNumber);

    /// <summary>The prefix every delivered peer message carries. Distinct from the manager's "👑 [FROM MANAGER"
    /// so an agent can always tell an order it must follow from a peer's request it may decline.</summary>
    public const string WirePrefix = "💬 [FROM AGENT";

    /// <summary>True for a prompt this channel injected. Used to keep peer traffic out of the manager-loop purge,
    /// which would otherwise drop peer messages the moment a crown changed hands.</summary>
    public static bool IsPeerMessage(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.AsSpan().TrimStart().StartsWith(WirePrefix, StringComparison.Ordinal);

    /// <summary>What lands in the recipient's session.</summary>
    public static string Wire(string senderLabel, int senderNumber, string body, int hop, PeerMessageLimits limits)
    {
        var remaining = Math.Max(0, limits.MaxHops - hop);
        var budget = remaining == 0
            ? "This chain has reached its hop limit: a reply addressed back to this agent will NOT be delivered, so " +
              "act on what you have or report the gap to the manager/user instead."
            : $"This chain is {hop} message(s) deep; {remaining} more hop(s) will be delivered before the channel is cut.";

        return $"{WirePrefix} #{senderNumber} — {senderLabel}] Peer message:\n\n" +
               $"{body}\n\n" +
               "— This came from another agent on your bridge, not from the user and not from the manager. It is a " +
               "request, not an order: if it is genuinely in your area, do it and answer; if it is NOT your area, say " +
               "so in one line rather than doing it anyway.\n" +
               $"Reply ONLY if you were actually asked something. To reply, put it in a block:\n" +
               $"@@MSG agent={senderNumber}\n<your answer>\n@@END\n" +
               "Do NOT send an acknowledgement, a thank-you, or a restatement — a reply that carries no new " +
               $"information is what turns this channel into an infinite loop. {budget}";
    }

    /// <summary>The one-line explanation the sender gets for a message that did not land.</summary>
    public static string Explain(PeerMessageVerdict verdict, string target) => verdict switch
    {
        PeerMessageVerdict.SelfAddressed => $"{target}: that is you — an agent cannot message itself.",
        PeerMessageVerdict.HopLimit => $"{target}: the peer-message chain hit its hop limit. Finish the work with " +
                                       "what you already know, or raise it with the manager/user instead.",
        PeerMessageVerdict.TurnLimit => $"{target}: one reply may only send a limited number of peer messages. " +
                                        "Send the rest on a later turn, or combine them.",
        PeerMessageVerdict.RateLimit => $"{target}: you have sent too many peer messages recently. Do the work you " +
                                        "can do alone for a while.",
        PeerMessageVerdict.Duplicate => $"{target}: you already sent that exact message to that agent. It was not " +
                                        "sent twice — wait for the answer instead of re-asking.",
        _ => $"{target}: not delivered.",
    };

    /// <summary>Staged on the sender's <c>Prelude</c> rather than sent as its own turn: telling an agent that its
    /// message bounced must not itself cost a provider turn, or the failure path becomes more expensive than the
    /// feature. It rides along with whatever the agent is asked next.</summary>
    public static string SenderNotice(IReadOnlyList<string> problems) =>
        "[BRIDGE] Some of the peer messages in your last reply were NOT delivered:\n- " +
        string.Join("\n- ", problems) +
        "\nNobody received those, so do not wait on an answer to them.";

    /// <summary>The nudge for a reply that wrote the verb but produced no parsable block. Same reasoning as the
    /// manager's malformed-dispatch nudge: an agent that believes it asked a peer a question and never did will
    /// otherwise sit waiting for a reply that cannot come.</summary>
    public static string MalformedNotice(string roster) =>
        "[BRIDGE] Your last reply mentioned a peer message but nothing was delivered — no agent received anything. " +
        "The header must be on its own line and name a real agent:\n" +
        "@@MSG agent=<number>\n<your message>\n@@END\n" +
        $"Agents you can reach right now: {(roster.Length > 0 ? roster : "none")}.";

    /// <summary>The nudge for a reply that SAID it had messaged someone without writing a block. Deliberately worded
    /// as a correction of fact rather than an accusation: the agent's next turn has to stop waiting and either send
    /// the block or carry on, and a scolding tone just spends the turn on apology.</summary>
    public static string UnsentClaimNotice(string roster) =>
        "[BRIDGE] Your last reply said you had messaged another agent, but no message was sent and nobody received " +
        "anything — describing a message does not deliver it. Do not wait for a reply; there is no question in " +
        "flight. If you still need one, put the block itself in your next reply:\n" +
        "@@MSG agent=<number>\n<your message>\n@@END\n" +
        $"Agents you can reach right now: {(roster.Length > 0 ? roster : "none")}.";

    /// <summary>The channel's description in every bridge agent's system prompt. Written as rules about WHEN to use
    /// it, not just syntax: the syntax is the easy half, and an agent that messages a peer for everything is worse
    /// than one that never messages at all.</summary>
    public static string BridgeClause(int index) =>
        "\n[PEER MESSAGES] You can send a prompt directly to another agent on this bridge. Put the block in your " +
        "reply. When YOUR TURN ENDS the app saves it to the recipient's agentNmessages.md log and queues a short " +
        "file-link notification for their next natural idle boundary. It never interrupts their current turn or " +
        "flushes pending user prompts. They do not see the rest of your reply. Each roster has a separate directory " +
        "under .vibecode/bridge-messages; use the absolute path in the notice. The log retains the latest five " +
        "sent/received messages and is deleted when that agent leaves.\n" +
        "@@MSG agent=<number, or `all`, or `manager`>\n" +
        "<the complete, self-contained message: what you need, why, and what you already tried>\n" +
        "@@END\n" +
        // Measured failure, not a hypothetical: the most common way real models lose a peer message is to write
        // "I've asked agent #4…" and then wait. Saying it is not doing it, and nothing else in this clause said so.
        "- WRITING THE BLOCK IS THE ONLY WAY TO SEND. Saying \"I asked agent #3\" or \"message sent to agent #4\" " +
        "sends nothing — no one receives it and no answer can arrive. If you need something from a peer, emit the " +
        "block in THIS reply; do not report it as already done, and do not wait for a reply you have not sent.\n" +
        // "Not your area" was the old trigger and it is a TOPIC test, not a necessity test: on a bridge, almost
        // everything is adjacent to somebody's lane, so it fires constantly. Trials leaked exactly there — an agent
        // messaged a peer for a parameter list that was sitting in a JSDoc block it could have read, and another
        // asked the manager to settle a coin-flip inside its own file. Both prongs must hold now, and the second one
        // names the two cases that genuinely do qualify so the strictness does not eat them.
        "- Sending nothing is the normal outcome of a turn. Before you send, BOTH of these must be true. (1) You " +
        "cannot finish what you are doing without an answer. (2) You have looked, and looking did not answer it — " +
        "it is not in the files, not on the board, and not yours to decide. Reading the code and finding only a name " +
        "or a signature does NOT count as an answer: if the file leaves the meaning of an argument, the contract, or " +
        "the consequences of getting it wrong ambiguous, that is exactly the kind of fact only its owner holds, and " +
        "guessing it is worse than asking. What the rule forbids is asking for something the file plainly stated.\n" +
        "- Blocked in THAT sense is the trigger, and then sending the block IS your action for this turn — it is not " +
        "an interruption and it does not need permission.\n" +
        // The one send that is not a question. Without this, tightening prong (1) silently kills the warnings —
        // "I cannot finish without an answer" is false when the person about to lose work is somebody else.
        "- The exception, and the only one: a WARNING. If something you know, or are about to do, will break or " +
        "destroy a peer's in-flight work and they cannot find out in time any other way, tell them now even though " +
        "nothing is blocking you. The test is whether they lose work by not hearing it — not whether it is polite " +
        "to mention.\n" +
        "- None of these are reasons to send, however natural each feels: telling someone what you finished or are " +
        "about to start; a heads-up about work that blocks nobody; asking for status or an ETA; acknowledging, " +
        "thanking or confirming; asking permission you do not need; asking which of two reasonable options to pick " +
        "when both sit inside your own lane and neither affects anyone else; or anything they would read and have " +
        "nothing to do about. If the message would not change what they do next, it is noise — leave it out, or put " +
        "it on the board. None of this applies when the choice IS the interface between your lane and theirs: a " +
        "contract neither of you can settle alone is the case this channel exists for.\n" +
        // Guard measured into existence: making the channel more actionable pushed a MANAGER into handing work out
        // with @@MSG instead of @@DISPATCH. Asking and ordering are different channels and must stay that way.
        "- Assigning work is never a peer message. If you are running this bridge, give work out with @@DISPATCH; " +
        "@@MSG is for asking a peer something, not for telling anyone what to build.\n" +
        // "report something that changes the plan" read as "report something", and the manager became the default
        // dumping ground: most stray messages in the trials went upward, not sideways.
        "- `manager` reaches whoever is running this bridge (nobody, if it has no manager). Escalate only what " +
        "changes the plan: a blocker you cannot route around, or a discovery that makes the current approach wrong. " +
        "Progress, completions and findings that leave the plan intact go in your reply and on the board, not up " +
        "this channel.\n" +
        "- The recipient may decline: this is a request between peers, not an order. Only the manager gives orders.\n" +
        "- A message costs the recipient a full turn, so it must carry NEW information or a real question. Never " +
        "acknowledge, thank, or restate — those replies are what turn the channel into a loop, and the app cuts a " +
        "chain off after a few hops whether or not the agents noticed.\n" +
        // The "don't narrate progress" line that used to sit here is gone: the not-a-reason bullet above says it
        // more precisely, and this clause earns its length back by not saying anything twice.
        $"- Messages beginning \"{WirePrefix} #\" are peers writing to you, agent #{index}.\n" +
        "- Only when you are explaining the feature rather than using it, name it inline (\"the @@MSG block\") so it " +
        "does not fire. Never let that stop you from sending a real one.";
}
