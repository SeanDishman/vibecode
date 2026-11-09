using VibeCode.AgentStatus.Mcp.Bridge;

namespace VibeCode.Services;

/// <summary>Compose app-owned instructions without repeating the session's policies on a resumed turn.
/// User text, attachments, history, and team updates are never rewritten by this helper.</summary>
internal static class PromptContext
{
    internal static string Join(IEnumerable<string?> parts)
    {
        var kept = new List<string>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (kept.Any(existing => ContainsBlock(existing, part))) continue;
            kept.Add(part);
        }
        return string.Join("\n\n", kept);
    }

    internal static string MissingSessionInstructions(string? instructions, string? appContext, string provider)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return "";
        if (string.IsNullOrWhiteSpace(appContext)) return instructions;

        // A resumed Codex session prepends its launch appendix to its first message. A Bridge join/recovery
        // prelude often already carries those same rules. Remove only known, exact policy blocks from the
        // appendix; keep the live per-turn context intact (including newer roles, review settings and notices).
        var missing = instructions;
        foreach (var policy in Policies(provider))
        {
            var block = policy.Trim();
            if (!ContainsBlock(appContext, block)) continue;
            for (var at = FindBlock(missing, block); at >= 0; at = FindBlock(missing, block))
                missing = missing.Remove(at, block.Length);
        }
        return string.Join("\n\n", missing.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !ContainsBlock(appContext, part.Trim()))).Trim();
    }

    private static IEnumerable<string> Policies(string provider)
    {
        yield return BridgeMcpTools.Instructions;
        yield return BridgeMcpConnection.RegularBridgeToolGuideFor(provider);
        yield return AgentTeamPolicy.SessionClause(provider);
        yield return AgentTeamPolicy.SessionClause(provider, paneTeam: true);
        foreach (var level in new[] { "none", "low", "normal", "high" })
        {
            yield return BridgeReviewPolicy.Instructions(level, orchestrator: false);
            yield return BridgeReviewPolicy.Instructions(level, orchestrator: true);
        }
    }

    private static bool ContainsBlock(string text, string block) => FindBlock(text, block) >= 0;

    private static int FindBlock(string text, string block)
    {
        if (block.Length == 0) return -1;
        var start = 0;
        while (start <= text.Length - block.Length)
        {
            var at = text.IndexOf(block, start, StringComparison.Ordinal);
            if (at < 0) return -1;
            var end = at + block.Length;
            if ((at == 0 || text[at - 1] == '\n') && (end == text.Length || text[end] is '\r' or '\n'))
                return at;
            start = at + 1;
        }
        return -1;
    }
}
