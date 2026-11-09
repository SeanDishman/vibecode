using System.IO;
using System.Text.Json;

namespace VibeCode.Services;

public sealed partial class CodexAccountService
{
    private sealed record UsageResetAttempt(string? Key);
    private readonly HashSet<string> _resettingAccounts = new(StringComparer.Ordinal);
    private static string ResetAttemptPath(string directory) => Path.Combine(directory, "usage-reset-attempt.json");
    private static bool HasPendingReset(string directory)
    {
        try { return ReadResetAttempt(directory)?.Key is { Length: > 0 }; }
        catch { return true; } // A damaged receipt must not silently turn a retry into a new redemption.
    }
    private static UsageResetAttempt? ReadResetAttempt(string directory) => File.Exists(ResetAttemptPath(directory))
        ? JsonSerializer.Deserialize<UsageResetAttempt>(File.ReadAllText(ResetAttemptPath(directory)))
          ?? throw new InvalidDataException("The saved reset receipt is invalid. No reset was sent.") : null;

    public bool IsResettingUsage(string id) { lock (_fileGate) return _resettingAccounts.Contains(id); }

    public async Task<CodexUsageResetResult> ResetUsageAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_fileGate)
            if (!_resettingAccounts.Add(id)) return new("busy", "A reset for this account is already in progress.");
        var gateHeld = false;
        CodexUsageResetResult? result = null;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            StoredAccount? stored;
            string key;
            lock (_fileGate)
            {
                stored = LoadAccounts().FirstOrDefault(a => a.Metadata.Id == id);
                if (stored is null) return new("failed", "This account is no longer saved in VibeCode.");
                if (!IsUsable(stored)) return new("failed", "Sign in again before resetting this account's usage.");
                var pending = ReadResetAttempt(stored.DirectoryPath);
                key = pending?.Key ?? Guid.NewGuid().ToString();
                if (!Guid.TryParse(key, out _)) return new("failed", "The saved reset receipt is invalid. No reset was sent.");
                // Persist before sending. A crash or lost response must reuse this exact attempt on the next click.
                WriteJsonAtomically(ResetAttemptPath(stored.DirectoryPath), new UsageResetAttempt(key));
            }
            result = await CodexAccountLoginService.ConsumeUsageResetAsync(HomeOf(stored), id, key, cancellationToken);
            lock (_fileGate)
            {
                // The user may remove or re-add this login while the operation is awaiting a response.
                var current = LoadAccounts().FirstOrDefault(a => a.Metadata.Id == id && a.DirectoryPath == stored.DirectoryPath);
                if (current is not null && result.State is { UsageReadSucceeded: true } state)
                {
                    current.Metadata.Name = state.Name ?? current.Metadata.Name;
                    current.Metadata.Email = state.Email ?? current.Metadata.Email;
                    current.Metadata.Plan = state.Plan ?? current.Metadata.Plan;
                    ApplyResetUsage(current.Metadata, state);
                    WriteMetadata(current.DirectoryPath, current.Metadata);
                }
                // A failed preflight on a retry says nothing about the original redemption.
                // Only a definitive consume response can retire this attempt's key.
                if (current is not null && result.Outcome is "reset" or "alreadyRedeemed" or "nothingToReset" or "noCredit")
                    WriteJsonAtomically(ResetAttemptPath(current.DirectoryPath), new UsageResetAttempt(null));
            }
            return result;
        }
        catch (Exception ex)
        {
            if (result is not null)
                return result with { Message = result.Message + " The local account update could not be saved: " + ex.Message };
            return new("failed", ex is OperationCanceledException ? "Reset cancelled before it was sent." : ex.Message);
        }
        finally
        {
            if (gateHeld) _operationGate.Release();
            lock (_fileGate) _resettingAccounts.Remove(id);
            AccountsChanged?.Invoke();
        }
    }

    private static void ApplyResetUsage(SavedAccount metadata, CodexAccountState state)
    {
        metadata.SessionPercent = state.SessionPercent;
        metadata.WeekPercent = state.WeekPercent;
        metadata.UsageLimits = state.UsageLimits;
        metadata.UsageUpdatedAt = state.UsageUpdatedAt;
        metadata.AvailableResetCredits = state.AvailableResetCredits;
        metadata.HasCredits = state.HasCredits;
        metadata.UnlimitedCredits = state.UnlimitedCredits;
        metadata.CreditBalance = state.CreditBalance ?? "";
        metadata.SpendLimit = state.SpendLimit;
        metadata.RateLimitStatus = state.RateLimitStatus ?? "";
    }
}
