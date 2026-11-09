using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Every AI account whose usage limit resets, and every window its usage check has reported, for
    /// <see cref="DiscordResetPings"/>. Read from what the account manager's checks already hold; nothing is fetched.
    /// Pay-per-token keys have no limit to reset and are left out; a Z.ai Coding Plan key is the one that has one.
    /// Labels are the account names the manager shows, which never fall back to an email address.
    /// </summary>
    public ResetPingSnapshot ReadResetPingSnapshot()
    {
        var accounts = new List<ResetPingAccount>();
        var readings = new List<UsageWindowReading>();

        foreach (var account in Accounts)
        {
            var key = "claude:" + account.Id;
            accounts.Add(new(key, "claude", account.Label, account.ProviderLine));
            if (AccountService.Instance.CachedUsage(account.Id) is not { HasNumbers: true } usage) continue;
            var seen = new DateTimeOffset(usage.FetchedAt);
            // The CLI can print a bare clock time for a reset later today; it means that day as of the check, not now.
            if (usage.SessionPercent is { } session)
                readings.Add(new(key, "claude", account.Label, "session", "session limit", session,
                    UsageLimitRecovery.ParseReset(usage.SessionReset, seen), seen));
            if (usage.WeekPercent is { } week)
                readings.Add(new(key, "claude", account.Label, "week", "weekly limit", week,
                    UsageLimitRecovery.ParseReset(usage.WeekReset, seen), seen));
        }

        foreach (var account in CodexAccounts)
        {
            var key = "codex:" + account.Id;
            accounts.Add(new(key, "codex", account.Label, account.ProviderLine));
            if (account.UsageUpdatedAt is not { } seen) continue;
            foreach (var limit in account.UsageLimits)
                readings.Add(new(key, "codex", account.Label, limit.LimitId + ":" + limit.Label, WindowName(limit.Label),
                    limit.Percent, FromUnix(limit.ResetsAtUnixSeconds), seen));
        }



        // The Google login is the Gemini CLI's own single login; an empty id is how a chat names it too.

        foreach (var account in ApiKeyAccountService.Instance.For(GlmPreset.ProviderId).Where(a => a.GlmUsage.IsCodingPlan))
        {
            var key = "glm:" + account.Id;
            accounts.Add(new(key, "glm", account.Title, $"GLM · {account.ConnectionDisplay}"));
            // A stale reading is the last one that arrived before a failed refresh: it says nothing new.
            if (account.GlmUsage is not { UpdatedAt: { } seen, IsStale: false } usage) continue;
            foreach (var limit in usage.Limits)
                readings.Add(new(key, "glm", account.Title, limit.Label, limit.Label.ToLowerInvariant(), limit.Percent,
                    limit.ResetsAt, seen));
        }

        return new(accounts, readings);
    }

    /// <summary>"Session" → "session limit", "This week" → "weekly limit", "Spark · this week" → "Spark weekly limit".</summary>
    private static string WindowName(string label)
    {
        var at = label.LastIndexOf(" · ", StringComparison.Ordinal);
        var (prefix, window) = at < 0 ? ("", label) : (label[..at] + " ", label[(at + 3)..]);
        var name = window.Trim().ToLowerInvariant() switch
        {
            "session" or "this session" => "session",
            "week" or "this week" => "weekly",
            var other => other,
        };
        return $"{prefix}{name} limit";
    }

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is > 0 and < 253_402_300_800 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
}
