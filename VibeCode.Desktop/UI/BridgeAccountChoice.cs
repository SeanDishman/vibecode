using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Account identity only. Credentials stay in the provider's account service.</summary>
public sealed record BridgeAccountChoice(string Provider, string AccountId, string Label, string Detail,
    bool IsUsable = true, bool IsDefault = false)
{
    public string AccessibleLabel => $"{Label}, {Detail}";

    public static IReadOnlyList<BridgeAccountChoice> Load(bool includeKimi = false)
    {
        var keys = ApiKeyAccountService.Instance;
        var choices = new List<BridgeAccountChoice>();
        choices.AddRange(AccountService.Instance.List().Select(a => new BridgeAccountChoice(
            "claude", a.Id, a.Label, a.ProviderLine, a.Usable, a.IsCurrent && keys.SelectedFor("claude") is null)));
        choices.AddRange(CodexAccountService.Instance.List().Select(a => new BridgeAccountChoice(
            "codex", a.Id, a.Label, a.ProviderLine, a.Usable, a.IsCurrent && keys.SelectedFor("codex") is null)));
        if (includeKimi)
            choices.Add(new("kimi", "", "Kimi account", "Kimi Code · Shared CLI login",
                IsDefault: keys.SelectedFor("kimi") is null));
        if (GeminiRuntime.HasLogin)
            choices.Add(new("gemini", "", GeminiRuntime.AccountEmail ?? "Google account", "Gemini · Google sign-in", GeminiRuntime.IsInstalled, true));
        foreach (var provider in ApiKeyAccountService.Providers)
        {
            var providerLabel = BridgeSetupPanel.Providers.First(p => p.Id == provider).Label;
            choices.AddRange(keys.For(provider).Select(a => new BridgeAccountChoice(provider, a.Id, a.Title,
                $"{providerLabel}{(provider == "glm" ? " · " + a.ConnectionDisplay : "")} · API key",
                !string.IsNullOrWhiteSpace(a.Secret), a.IsPreferred)));
        }
        // Identical display names/plans still need to be distinguishable without exposing email addresses.
        var duplicateNames = choices.GroupBy(a => (a.Provider, a.Label, a.Detail)).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet();
        return choices.Select(a => duplicateNames.Contains((a.Provider, a.Label, a.Detail))
                ? a with { Label = $"{a.Label} · {a.AccountId[^Math.Min(6, a.AccountId.Length)..]}" } : a)
            .OrderBy(a => Array.IndexOf(new[] { "claude", "codex", "kimi", "gemini", "glm" }, a.Provider))
            .ThenByDescending(a => a.IsDefault).ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>One provider's accounts, in picker order.</summary>
    public static IReadOnlyList<BridgeAccountChoice> For(string provider, bool includeKimi = false) =>
        Load(includeKimi).Where(a => a.Provider == provider).ToArray();

    /// <summary>The account to use without asking: the only signed-in one. Null when there is a real choice
    /// (two or more) or nothing usable, which leaves the provider's own default in charge.</summary>
    public static BridgeAccountChoice? OnlyUsable(IEnumerable<BridgeAccountChoice> accounts) =>
        accounts.Where(a => a.IsUsable).Take(2).ToArray() is [var only] ? only : null;
}

/// <summary>A provider row in the regular bridge picker. Its accounts are listed only after it is chosen.</summary>
public sealed record BridgeProviderChoice(string Id, string Label, string Detail, bool IsUsable)
{
    public string AccessibleLabel => $"{Label}, {Detail}";

    public static IReadOnlyList<BridgeProviderChoice> From(IReadOnlyList<BridgeAccountChoice> accounts) =>
        BridgeSetupPanel.Providers.Select(p =>
        {
            var saved = accounts.Count(a => a.Provider == p.Id);
            var usable = accounts.Count(a => a.Provider == p.Id && a.IsUsable);
            var detail = saved == 0 ? "No account added" : usable == 0 ? "Sign in again in AI accounts"
                : $"{saved} account{(saved == 1 ? "" : "s")}";
            return new BridgeProviderChoice(p.Id, p.Label, detail, usable > 0);
        }).ToArray();
}
