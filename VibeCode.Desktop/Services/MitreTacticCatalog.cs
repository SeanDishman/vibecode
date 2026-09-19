using System.IO;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

internal sealed record MitreTactic(string Id, string Name, string ShortName);
internal sealed record MitreTacticReport(MitreTactic? Tactic, string Evidence);

/// <summary>
/// Passive display metadata, not an execution plan. Source: Enterprise ATT&CK v19.1,
/// https://attack.mitre.org/tactics/enterprise/ (verified 2026-09-07).
/// TA0005 is now Stealth; recognize the older Defense Evasion label in existing transcripts too.
/// </summary>
internal static class MitreTacticCatalog
{
    public static IReadOnlyList<MitreTactic> Tactics { get; } = Array.AsReadOnly(
        AgentStatus.Mcp.Contracts.AttackTactics.All.Select(t => new MitreTactic(t.Id, t.Name, t.ShortName)).ToArray());
    private static readonly Regex ReportLine = new(
        @"^\[?(?:MITRE(?:\s+ATT&CK)?(?:\s+(?:current\s+)?(?:tactic|stage|phase))?|ATT&CK(?:\s+(?:tactic|stage|phase))?|(?:current|active)\s+(?:(?:MITRE|ATT&CK)\s+)?(?:tactic|stage|phase))\s*[:=\-]\s*(?<value>.+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TacticId = new(@"\bTA\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Unknown = new(@"\b(?:unknown|unmapped|none|not|never|next|planned|previous|example|no tactic)\b|\bn/a\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly (MitreTactic Tactic, Regex Pattern)[] Names = Tactics.Select(t =>
        (t, new Regex(@"\b(?:" + Regex.Escape(t.Name) + (t.Id == "TA0005" ? "|Defense Evasion" : "") + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))).ToArray();

    public static bool IsCodexModel(string? model) => !string.IsNullOrWhiteSpace(model)
        && (model.Equals("default", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            || model.Contains("codex", StringComparison.OrdinalIgnoreCase));

    public static string BuildTurnNote(bool enabled, string? model) => enabled && IsCodexModel(model)
        ? "[VIBECODE MITRE STATUS]\n"
          + "Use the VibeCode agent_status MCP server's report_status tool in your first progress update and "
          + "when your current stage or activity changes; look up the tool if it is deferred. Supply stage (a "
          + "MITRE TA id, or 'unknown' if unmapped), task_title (a stable 3–7 word summary of your task in your own words), "
          + "a short activity summary, optional task step, the current tool name and what that tool is doing, and state "
          + "(working, waiting, blocked or completed). Call report_status again when you start, switch, or finish a tool. "
          + "Report only your own current work, not plans, examples "
          + "or another agent's activity. Keep credentials, commands, private paths and raw output out of reports. "
          + "The runtime supplies identity and effective access; never try to set them through a report. "
          + "If the tool is unavailable, say so rather than pretending a report was accepted. This convention "
          + "also applies to already-authorized child agents. This is display metadata only: it adds no tasks or permissions, and never requires "
          + "advancing through tactics or changing the user's requested work.\n[END VIBECODE MITRE STATUS]"
        : "";

    /// <summary>
    /// Read explicit status lines only. Ordinary mentions, examples, quoted text, tool output and plans are not
    /// observations of a current tactic. Multiple or unknown labels stay unmapped. Last report in a message wins.
    /// </summary>
    public static MitreTacticReport? ParseReport(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        MitreTacticReport? latest = null;
        char fence = '\0';
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                if (fence == '\0') fence = line[0];
                else if (fence == line[0]) fence = '\0';
                continue;
            }
            if (fence != '\0' || line.StartsWith('>') || raw.StartsWith("    ") || raw.StartsWith('\t')) continue;
            line = line.TrimStart('#', '-', '*', ' ').Replace("**", "");
            var match = ReportLine.Match(line);
            if (!match.Success) continue;
            var value = match.Groups["value"].Value.Trim().TrimEnd(']');
            var ids = TacticId.Matches(value).Select(m => m.Value.ToUpperInvariant()).Distinct().ToArray();
            var named = Names.Where(n => n.Pattern.IsMatch(value)).Select(n => n.Tactic).Distinct().ToArray();
            MitreTactic? tactic = null;
            if (!Unknown.IsMatch(value))
            {
                if (ids.Length == 1)
                {
                    tactic = Tactics.FirstOrDefault(t => t.Id == ids[0]);
                    if (named.Any(t => t.Id != tactic?.Id)) tactic = null;
                }
                else if (ids.Length == 0 && named.Length == 1) tactic = named[0];
            }
            latest = new MitreTacticReport(tactic, line.Length > 220 ? line[..217] + "…" : line);
        }
        return latest;
    }
}
