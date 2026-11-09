using System.IO;
using VibeCode.Services;

namespace VibeCode.UI;

public enum RecapPeriod { Today, Month, Year }

/// <summary>One project's line on the notice board.</summary>
public sealed record RecapProject(string Name, string Directory, int Turns, double Tokens, double Cost, int Files, int Added, int Removed);

/// <summary>
/// The Town Crier's notice: the day's (or month's, or year's) numbers across every provider - turns, tokens and the
/// estimated cost from the usage log, files and lines changed from each project's Bridge edit ledger, the busiest
/// building, the biggest and the longest turn, and how many times agents came to the door for you. Built off the UI
/// thread from history VibeCode already keeps; nothing new is recorded for it, and nothing is saved unless you ask for
/// a postcard.
/// </summary>
public sealed record WorkflowRecap(RecapPeriod Period, DateTime FromLocal, DateTime ToLocal, int Turns, double Tokens, double Cost,
    int Files, int Added, int Removed, RecapProject? Busiest, string? BiggestTurn, string? LongestTurn, int DoorVisits,
    IReadOnlyList<RecapProject> Projects)
{
    /// <summary>The edit ledger keeps a week, so longer recaps can only count that week's edits.</summary>
    public bool EditsPartial => ToLocal - FromLocal > BridgeEditLedger.Retention;

    public string Title => Period switch
    {
        RecapPeriod.Today => "Today, " + FromLocal.ToString("dddd d MMMM"),
        RecapPeriod.Month => FromLocal.ToString("MMMM yyyy"),
        _ => $"Your {FromLocal.Year} at the tavern",
    };

    public static (DateTime From, DateTime To) Range(RecapPeriod period, DateTime nowLocal) => period switch
    {
        RecapPeriod.Month => (new DateTime(nowLocal.Year, nowLocal.Month, 1), nowLocal),
        RecapPeriod.Year => (new DateTime(nowLocal.Year, 1, 1), nowLocal),
        _ => (nowLocal.Date, nowLocal),
    };

    /// <summary>Gather the notice from <paramref name="usage"/> (the turn log) and <paramref name="edits"/> (a project
    /// directory's Bridge edit ledger). <paramref name="doorVisits"/> and <paramref name="longest"/> come from the wall,
    /// which only knows what happened while it was open.</summary>
    public static WorkflowRecap Build(RecapPeriod period, DateTime nowLocal, IEnumerable<UsageEntry> usage,
        Func<string, IEnumerable<BridgeEditEntry>> edits, int doorVisits, (string Who, TimeSpan Length)? longest,
        IEnumerable<string>? alsoRead = null)
    {
        var (from, to) = Range(period, nowLocal);
        var turns = usage.Where(e => e.At.LocalDateTime >= from && e.At.LocalDateTime <= to).ToList();
        var groups = turns.GroupBy(e => e.Project is { Length: > 0 } p ? WorkflowTown.Key(p) : "", StringComparer.OrdinalIgnoreCase)
            .Select(g => (Directory: g.Key, Turns: g.Count(), Tokens: g.Sum(e => e.Total), Cost: g.Sum(e => e.CostUsd)))
            .OrderByDescending(g => g.Turns).ToList();
        var directories = groups.Where(g => g.Directory.Length > 0).Select(g => g.Directory).Take(12)
            .Concat(alsoRead ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var projects = new List<RecapProject>();
        int files = 0, added = 0, removed = 0;
        foreach (var directory in directories)
        {
            var changed = edits(directory).Where(e => e.At.LocalDateTime >= from && e.At.LocalDateTime <= to).ToList();
            var usageRow = groups.FirstOrDefault(g => string.Equals(g.Directory, directory, StringComparison.OrdinalIgnoreCase));
            if (usageRow.Turns == 0 && changed.Count == 0) continue;
            var row = new RecapProject(Name(directory), directory, usageRow.Turns, usageRow.Tokens, usageRow.Cost,
                changed.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(), changed.Sum(e => e.Added), changed.Sum(e => e.Removed));
            projects.Add(row);
            files += row.Files; added += row.Added; removed += row.Removed;
        }
        if (groups.FirstOrDefault(g => g.Directory.Length == 0) is { Turns: > 0 } loose)
            projects.Add(new RecapProject("Without a project", "", loose.Turns, loose.Tokens, loose.Cost, 0, 0, 0));
        projects = projects.OrderByDescending(p => p.Turns).ThenByDescending(p => p.Added + p.Removed).ToList();
        var biggest = turns.OrderByDescending(e => e.Total).FirstOrDefault();
        return new WorkflowRecap(period, from, to, turns.Count, turns.Sum(e => e.Total), turns.Sum(e => e.CostUsd), files, added, removed,
            projects.FirstOrDefault(p => p.Turns > 0 && p.Directory.Length > 0),
            biggest is null ? null : $"{UsageAnalytics.Tokens(biggest.Total)} tokens · {UsagePalette.DisplayName(biggest.Model)}"
                + (biggest.Project is { Length: > 0 } where ? " · " + Name(where) : ""),
            longest is { } turn ? $"{WorkflowAgentCard.Duration(turn.Length, seconds: true)} · {turn.Who}" : null,
            doorVisits, projects);
    }

    private static string Name(string directory) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) is { Length: > 0 } name ? name : directory;
}
