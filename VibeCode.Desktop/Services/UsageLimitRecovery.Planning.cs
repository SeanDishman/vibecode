using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

public static partial class UsageLimitRecovery
{
    internal static async Task<UsageRecoverySnapshot> ProbeAsync(string provider, string? model, string? accountId, ICodingSession session)
    {
        if (session is GlmSession glm) accountId = glm.ActiveAccountId ?? accountId;
        if (provider == "claude")
        {
            var text = accountId is { Length: > 0 }
                ? await AccountService.Instance.ProbeUsageTextAsync(accountId)
                : await UsageService.ProbeSharedLoginAsync();
            return text is null ? UsageRecoverySnapshot.Unknown : UsageService.RecoverySnapshot(text, model);
        }
        if (provider == "codex" && session is CodexSession codex) return await codex.ReadRecoveryUsageAsync();
        if (provider == "kimi") return await KimiUsageService.Instance.ReadRecoveryUsageAsync();
        if (provider == "glm")
        {
            var account = ApiKeyAccountService.Instance.For(GlmPreset.ProviderId).FirstOrDefault(a => a.Id == accountId);
            if (account is null || !account.GlmUsage.IsCodingPlan) return UsageRecoverySnapshot.Unknown;
            await GlmUsageService.Instance.RefreshAsync(account, force: true);
            var usage = account.GlmUsage;
            return usage.IsStale || usage.IsRefreshing ? UsageRecoverySnapshot.Unknown
                : UsageRecoverySnapshot.FromWindows(usage.Limits.Where(l => l.ShortLabel != "tools")
                    .Select(l => (l.Percent, l.ResetsAt)));
        }
        if (provider == "grok" && accountId is { Length: > 0 })
        {
            var started = DateTimeOffset.Now;
            var test = await GrokAccountService.Instance.TestAccountAsync(accountId);
            var account = GrokAccountService.Instance.List().FirstOrDefault(a => a.Id == accountId);
            return test.Ok && account?.UsageUpdatedAt >= started && account.UsagePercent is { } percent
                ? UsageRecoverySnapshot.FromWindows([(percent, account.UsageResetsAt)]) : UsageRecoverySnapshot.Unknown;
        }
        return UsageRecoverySnapshot.Unknown;
    }

    // Planning sessions share the global preference and provider checks. They retain their session while waiting,
    // and the caller pauses its normal response timeout until allowance is available again.
    internal static async Task<bool> WaitForResetAsync(JsonNode result, string provider, string? model,
        string? accountId, ICodingSession session, int attempt, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var hint = RetryAt(result, now);
        var retryAt = hint ?? now.AddMinutes(Math.Min(30, 3 * Math.Pow(2, Math.Min(attempt, 4))));
        var earliest = now.AddSeconds(attempt == 0 ? 5 : Math.Min(300, 30 * Math.Pow(2, Math.Min(attempt, 4))));
        var nextCheck = earliest;
        while (AppSettings.Current.ContinueAfterLimitResets && !session.HasExited)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            if (DateTimeOffset.Now < nextCheck) continue;
            UsageRecoverySnapshot snapshot;
            try { snapshot = await ProbeAsync(provider, model, accountId, session).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { snapshot = UsageRecoverySnapshot.Unknown; }
            cancellationToken.ThrowIfCancellationRequested();
            if (!AppSettings.Current.ContinueAfterLimitResets || session.HasExited) return false;
            now = DateTimeOffset.Now;
            if ((hint is null || now >= hint) && (snapshot.Available == true || snapshot.Available is null && now >= retryAt)) return true;
            if (snapshot.Available == false && snapshot.ResetsAt is { } reset && reset > now) retryAt = reset;
            nextCheck = now.AddMinutes(1);
            var resetAt = snapshot.ResetsAt ?? retryAt;
            if (resetAt > now && resetAt.AddSeconds(2) < nextCheck) nextCheck = resetAt.AddSeconds(2);
        }
        return false;
    }
}
