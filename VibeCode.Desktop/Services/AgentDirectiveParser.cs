using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>One directive lifted out of an agent's reply, before it is sorted into a work order or a peer message.</summary>
/// <param name="Verb">The canonical verb — <c>DISPATCH</c> (manager work order) or <c>MSG</c> (peer message).</param>
/// <param name="Target">The raw target text: a roster number, a comma list, <c>all</c>, or <c>manager</c>.</param>
/// <param name="Attributes">Free-form text after the target (e.g. <c>depends=2,3</c>).</param>
/// <param name="Body">The complete prompt to hand the recipient, verbatim.</param>
public readonly record struct DirectiveBlock(string Verb, string Target, string Attributes, string Body);

/// <summary>A parsed target expression. Kept as a shape rather than a list of panes so the grammar can be tested
/// without a roster.</summary>
public readonly record struct DirectiveTargets(bool All, bool Manager, IReadOnlyList<int> Numbers)
{
    public static DirectiveTargets Parse(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new(false, false, Array.Empty<int>());
        var t = target.Trim();
        if (t.Equals("all", StringComparison.OrdinalIgnoreCase)) return new(true, false, Array.Empty<int>());
        if (t.Equals("manager", StringComparison.OrdinalIgnoreCase)) return new(false, true, Array.Empty<int>());
        var numbers = new List<int>();
        foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out var n) && n > 0 && !numbers.Contains(n)) numbers.Add(n);
        return new(false, false, numbers);
    }
}

/// <summary>
/// The one scanner for every <c>@@</c> directive an agent can write. It exists as a single pass on purpose: dispatch
/// blocks and peer messages share a reply, and a block's body ends at the next directive of EITHER kind. Parsing them
/// with two independent regexes meant a manager that dispatched a lane and then messaged a peer had the whole peer
/// message swallowed into the end of the work order — the worker got instructions addressed to somebody else, and the
/// peer got nothing at all.
///
/// <c>@@END</c> stays OPTIONAL for the same reason it is optional for dispatch: a directive that is otherwise perfectly
/// well formed must never be lost to a missing sentinel. A block ends at its terminator, at the next header, or at the
/// end of the reply, whichever comes first.
/// </summary>
public static class AgentDirectiveParser
{
    public const string DispatchVerb = "DISPATCH";
    public const string MessageVerb = "MSG";
    public const string ReadVerb = "READ";
    public const string AnsweredVerb = "ANSWERED";

    /// <summary>Every accepted spelling. <c>@@MSG</c> is the documented one; the aliases exist because a model that
    /// reaches for the obvious synonym under pressure would otherwise have its message silently dropped, which is the
    /// exact failure this channel is supposed to make impossible.</summary>
    private static readonly Regex Header = new(
        @"^[ \t]*@@(?<verb>DISPATCH|MESSAGE|MSG|PEER|ASK|TO|READ|ANSWERED)[ \t]+agent[ \t]*=[ \t]*" +
        @"(?<target>all|manager|\d+(?:[ \t]*,[ \t]*\d+)*)(?<attrs>[^\r\n]*)\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The optional terminator, shared by both verbs.</summary>
    private static readonly Regex End = new(
        @"^[ \t]*@@END[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A directive verb written anywhere in a reply, whether or not it parsed. Used to tell "said nothing"
    /// apart from "tried to say something and got the syntax wrong" — very different things to report back.</summary>
    private static readonly Regex AnyMention = new(
        @"@@(?<verb>DISPATCH|MESSAGE|MSG|PEER|ASK|TO|READ|ANSWERED)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every directive in a reply, in the order written.</summary>
    public static List<DirectiveBlock> Scan(string? reply)
    {
        var blocks = new List<DirectiveBlock>();
        if (string.IsNullOrEmpty(reply)) return blocks;

        var headers = Header.Matches(reply);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var bodyStart = header.Index + header.Length;
            if (bodyStart >= reply.Length) continue;
            // The body stops at whichever comes first: this block's terminator, or the next block's header.
            var bodyEnd = i + 1 < headers.Count ? headers[i + 1].Index : reply.Length;
            if (End.Match(reply, bodyStart) is { Success: true } end && end.Index < bodyEnd) bodyEnd = end.Index;
            var body = reply[bodyStart..bodyEnd].Trim();
            if (body.Length > 0)
                blocks.Add(new DirectiveBlock(
                    Canonical(header.Groups["verb"].Value),
                    Normalize(header.Groups["target"].Value),
                    header.Groups["attrs"].Value,
                    body));
        }
        return blocks;
    }

    /// <summary>Directives of one verb only.</summary>
    public static List<DirectiveBlock> Scan(string? reply, string verb) =>
        Scan(reply).Where(b => b.Verb == verb).ToList();

    /// <summary>
    /// The reply with every directive block removed — header, body and its optional <c>@@END</c>.
    ///
    /// A block is a COMMAND to the app, not prose: once it has been routed, the recipient has it in their own
    /// transcript and the sender's pane already carries a "messaged agent #3" divider, so leaving the raw block on
    /// screen shows the same message twice, the second time as protocol source in the middle of the agent's report.
    /// Only ever used for DISPLAY — the routers, the manager relay and Copy all keep reading the original text,
    /// because what the agent actually wrote is what those have to reason about.
    /// </summary>
    /// <param name="verbs">Only these verbs are removed; null removes every directive. Scoped because a block is
    /// only noise once it has been ACTED ON — a <c>@@DISPATCH</c> written by an agent that is not the manager is
    /// routed by nobody, and hiding it would delete the only evidence that it was ever written.</param>
    public static string Strip(string? reply, IReadOnlyCollection<string>? verbs = null)
    {
        if (string.IsNullOrEmpty(reply)) return reply ?? "";
        var headers = Header.Matches(reply);
        if (headers.Count == 0) return reply;

        var kept = new System.Text.StringBuilder();
        var cursor = 0;
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (header.Index < cursor) continue;          // already inside a span we removed
            if (verbs is not null && !verbs.Contains(Canonical(header.Groups["verb"].Value))) continue;
            var blockEnd = i + 1 < headers.Count ? headers[i + 1].Index : reply.Length;
            // Take the terminator with the block; without this the reply keeps a bare "@@END" line, which is the
            // most visible half of the noise this removes.
            if (End.Match(reply, header.Index + header.Length) is { Success: true } end && end.Index < blockEnd)
                blockEnd = Math.Min(end.Index + end.Length, reply.Length);

            kept.Append(reply, cursor, header.Index - cursor);
            cursor = blockEnd;
        }
        kept.Append(reply, cursor, reply.Length - cursor);

        // Removing a block from between two paragraphs leaves the blank lines that surrounded it. Collapse runs of
        // three or more newlines back to a paragraph break so the prose closes up instead of gaining a hole.
        return BlankRun.Replace(kept.ToString(), "\n\n").Trim();
    }

    private static readonly Regex BlankRun = new(@"(?:[ \t]*\r?\n){3,}", RegexOptions.Compiled);

    /// <summary>True when a reply names <paramref name="verb"/> at all, parsed or not.</summary>
    public static bool Mentions(string? reply, string verb)
    {
        if (string.IsNullOrEmpty(reply)) return false;
        foreach (Match m in AnyMention.Matches(reply))
            if (Canonical(m.Groups["verb"].Value) == verb) return true;
        return false;
    }

    /// <summary>Fold every accepted spelling onto the one verb the routers switch on.</summary>
    private static string Canonical(string verb) => verb.ToUpperInvariant() switch
    {
        DispatchVerb => DispatchVerb,
        ReadVerb => ReadVerb,
        AnsweredVerb => AnsweredVerb,
        _ => MessageVerb,
    };

    /// <summary>Strip the whitespace a model sprinkles through a comma list so "2, 3" and "2,3" are one target.</summary>
    private static string Normalize(string target) =>
        target.Replace(" ", "").Replace("\t", "");
}
