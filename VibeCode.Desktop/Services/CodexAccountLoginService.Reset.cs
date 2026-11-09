using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

public sealed record CodexUsageResetResult(string Outcome, string Message, CodexAccountState? State = null)
{
    public bool Succeeded => Outcome is "reset" or "alreadyRedeemed";
    public bool Uncertain => Outcome == "uncertain";
}

public static partial class CodexAccountLoginService
{
    // https://learn.chatgpt.com/docs/app-server#8-earned-rate-limit-resets-chatgpt
    // A redemption key belongs to one logical attempt, including retries after an interrupted response.
    public static async Task<CodexUsageResetResult> ConsumeUsageResetAsync(string homeDirectory, string expectedAccountId,
        string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAccountId) || !Guid.TryParse(idempotencyKey, out _))
            return new("failed", "The reset request is missing its account or retry key.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var gateHeld = false;
        var sent = false;
        Process? process = null;
        try
        {
            await AccountGate.WaitAsync(token);
            gateHeld = true;
            process = StartServer(homeDirectory);
            await InitializeAsync(process, token);
            await WriteAsync(process, new JsonObject
            {
                ["id"] = 2, ["method"] = "account/read",
                ["params"] = new JsonObject { ["refreshToken"] = true },
            }, token);
            var account = (await ReadResponseAsync(process, 2, token))["result"]?["account"];
            if (account is null) return new("failed", "This account is signed out. Sign in again before using a reset.");
            var identity = ReadStoredIdentity(homeDirectory);
            if (identity.AccountId != expectedAccountId)
                return new("failed", "This saved login now belongs to a different account. Re-add it before resetting usage.");

            // Do not automatically retry a mutation with a new key, even if the response is lost.
            sent = true;
            await WriteAsync(process, new JsonObject
            {
                ["id"] = 3, ["method"] = "account/rateLimitResetCredit/consume",
                ["params"] = new JsonObject { ["idempotencyKey"] = idempotencyKey },
            }, token);
            var outcome = (await ReadResponseAsync(process, 3, token))["result"]?["outcome"]?.GetValue<string>();
            if (outcome is not ("reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit"))
                return new("uncertain", "Codex returned an unrecognized reset result. Choose Reset usage again to check this same attempt.");

            var usage = await TryReadRateLimitsAsync(process, 4, token);
            var state = usage.ReadSucceeded ? new CodexAccountState
            {
                IsSignedIn = true, AccountId = identity.AccountId, Name = identity.Name, Email = identity.Email,
                Plan = account["planType"]?.GetValue<string>() ?? usage.Plan,
                UsageReadSucceeded = true, UsageUpdatedAt = usage.UpdatedAt,
                SessionPercent = usage.SessionPercent, WeekPercent = usage.WeekPercent, UsageLimits = usage.Limits,
                AvailableResetCredits = usage.AvailableResetCredits, HasCredits = usage.HasCredits,
                UnlimitedCredits = usage.UnlimitedCredits, CreditBalance = usage.CreditBalance,
                SpendLimit = usage.SpendLimit, RateLimitStatus = usage.RateLimitStatus,
            } : null;
            var message = outcome switch
            {
                "reset" => "Usage reset. One earned reset was used.",
                "alreadyRedeemed" => "The previous reset completed. No additional reset was used.",
                "nothingToReset" => "No eligible usage window needs a reset. No reset was used.",
                _ => "No earned resets are available for this account.",
            };
            if (state is null) message += " Usage could not be refreshed; reopen the account manager to check it.";
            return new(outcome, message, state);
        }
        catch (Exception ex)
        {
            var detail = ex is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? "The request was cancelled." : "Codex did not respond in time."
                : ex.Message;
            return new(sent ? "uncertain" : "failed", detail + (sent
                ? " The reset may have completed. Choose Reset usage again to check the same attempt without spending another reset."
                : " No reset was sent."));
        }
        finally
        {
            if (process is not null) StopServer(process);
            if (gateHeld) AccountGate.Release();
        }
    }
}
