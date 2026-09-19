using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>One agent-to-agent message, split into the parts the transcript wants to show separately.</summary>
/// <param name="Glyph">The wire's leading glyph — 💬 for a peer message, 👑 for a manager work order.</param>
/// <param name="Sender">Who wrote it, as a label ("Claude 2", "Claude agent #1").</param>
/// <param name="Kind">"Peer message" / "Work order" — the wire's own description of itself.</param>
/// <param name="Body">What the sending AGENT actually wrote, with the app's framing removed.</param>
public readonly record struct AgentMessageCard(string Glyph, string Sender, string Kind, string Body);

/// <summary>
/// Turns an app-injected agent message back into its parts so the transcript can render it as a card.
///
/// These prompts are assembled by <see cref="PeerMessagePolicy.Wire"/> and by the manager dispatch loop, and they are
/// mostly NOT prose for the reader: a header naming the sender, the agent's actual message, then a block of protocol
/// instructions addressed to the receiving model ("Reply ONLY if…", "@@MSG agent=2 … @@END"). Rendered as one flat
/// string, the reader gets all three at equal weight, in the same grey as their own prompts, with the agent's
/// markdown showing as source.
///
/// The header and the trailer are the app's own text and are matched exactly, so a parse either recognises a wire
/// this app built or fails and the caller shows the raw text unchanged. Deliberately not a loose heuristic: guessing
/// wrong here would hide part of a message an agent really sent.
/// </summary>
public static class AgentMessagePresentation
{
    /// <summary>The opening line both wires share: glyph, "[FROM …]", and a name for the kind of message.</summary>
    private static readonly Regex WireHeader = new(
        @"^(?<glyph>[^\s\[]+)[ \t]+\[FROM[ \t]+(?:AGENT|MANAGER)(?<sender>[^\]]*)\][ \t]*(?<kind>[^:\r\n]+):[ \t]*\r?\n",
        RegexOptions.Compiled);

    /// <summary>Where the app's instructions to the RECEIVING model begin. Everything from here on is protocol, not
    /// message: reply syntax, hop budget, "do not acknowledge". Kept as literals taken from the builders so a
    /// reworded trailer fails the match and shows in full rather than silently swallowing a line of the message.</summary>
    private static readonly string[] Trailers =
    {
        "— This came from another agent on your bridge",
        "— Work within this order's scope and constraints",
    };

    public static bool TryParse(string? text, out AgentMessageCard card)
    {
        card = default;
        if (string.IsNullOrEmpty(text)) return false;
        var match = WireHeader.Match(text);
        if (!match.Success) return false;

        var rest = text[(match.Index + match.Length)..];
        // The trailer is the last thing in the wire, so search from the end: a message that QUOTES the instructions
        // (agents discussing this channel do) must not truncate at the quote.
        var cut = -1;
        foreach (var trailer in Trailers)
        {
            var at = rest.LastIndexOf(trailer, StringComparison.Ordinal);
            if (at > cut) cut = at;
        }
        var body = (cut >= 0 ? rest[..cut] : rest).Trim();
        if (body.Length == 0) return false;   // nothing but framing: not a card, show it as it came

        return TryBuild(match, body, out card);
    }

    private static bool TryBuild(Match match, string body, out AgentMessageCard card)
    {
        // "#2 — Claude 2" (peer) or "— Claude agent #1" (manager): the readable name is whatever follows the dash.
        var sender = match.Groups["sender"].Value.Trim();
        var dash = sender.IndexOf('—');
        if (dash >= 0) sender = sender[(dash + 1)..].Trim();
        if (sender.Length == 0) sender = "another agent";

        card = new AgentMessageCard(
            match.Groups["glyph"].Value,
            sender,
            match.Groups["kind"].Value.Trim(),
            body);
        return true;
    }
}
