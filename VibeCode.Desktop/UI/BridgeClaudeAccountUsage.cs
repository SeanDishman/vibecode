using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One subscription display per distinct Claude login in this window's Bridge.</summary>
public sealed record BridgeClaudeAccountUsage(string Label, UsageService Usage)
{
    public static IReadOnlyList<BridgeClaudeAccountUsage> From(IEnumerable<ChatViewModel> panes)
    {
        var accounts = panes.Where(pane => pane.IsClaude)
            .GroupBy(pane => pane.ClaudeUsage.AccountId ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => new BridgeClaudeAccountUsage(group.First().AccountLabel, group.First().ClaudeUsage))
            .ToArray();
        var duplicates = accounts.GroupBy(account => account.Label, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return accounts.Select(account => duplicates.Contains(account.Label)
            ? account with { Label = $"{account.Label} · {Suffix(account.Usage.AccountId)}" } : account).ToArray();
    }

    private static string Suffix(string? id) => string.IsNullOrEmpty(id) ? "shared" : id[^Math.Min(6, id.Length)..];
}
