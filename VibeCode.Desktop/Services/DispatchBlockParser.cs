using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>One work order lifted out of a manager/orchestrator reply.</summary>
/// <param name="Target">The roster number(s) to deliver to, or "all".</param>
/// <param name="Attributes">Free-form text after the agent number (currently only <c>depends=2,3</c>).</param>
/// <param name="Body">The complete prompt to hand the worker, verbatim.</param>
public readonly record struct DispatchBlock(string Target, string Attributes, string Body);

/// <summary>
/// Lifts <c>@@DISPATCH</c> work orders out of a manager's reply. The scanning itself lives in
/// <see cref="AgentDirectiveParser"/>, which reads dispatches and peer messages in ONE pass so a reply containing both
/// splits at the right boundaries; this type is the dispatch-shaped view of that result.
///
/// <c>@@END</c> is deliberately OPTIONAL. The original grammar required it, and a manager that wrote a perfectly good
/// work order but let its reply run off the end without the terminator produced ZERO matches: the worker was never
/// told anything, the manager believed it had dispatched and sat waiting for a report, and the lane died in silence
/// with nobody able to tell that it had. Losing a whole assignment to a missing sentinel is the worst failure this
/// code can have, so the sentinel is now a hint — a block ends at its terminator, at the next header, or at the end
/// of the reply, whichever comes first.
/// </summary>
public static class DispatchBlockParser
{
    /// <summary>Attributes on the header line. Lenient: an orchestrator that writes none keeps working.</summary>
    private static readonly Regex DependsAttribute = new(
        @"depends[ \t]*=[ \t]*(?<list>[\d ,]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when a reply is even claiming to dispatch. Used to tell "no work orders" apart from "work orders
    /// that failed to parse", which are very different things to report.</summary>
    public static bool MentionsDispatch(string? reply) =>
        AgentDirectiveParser.Mentions(reply, AgentDirectiveParser.DispatchVerb);

    public static List<DispatchBlock> Parse(string? reply) =>
        AgentDirectiveParser.Scan(reply, AgentDirectiveParser.DispatchVerb)
            .Select(b => new DispatchBlock(b.Target, b.Attributes, b.Body))
            .ToList();

    /// <summary>Roster numbers a work order was declared to depend on.</summary>
    public static List<int> ParseDependencies(string? attributes)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(attributes)) return result;
        if (DependsAttribute.Match(attributes) is not { Success: true } m) return result;
        foreach (var part in m.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out var n) && n > 0) result.Add(n);
        return result;
    }
}
