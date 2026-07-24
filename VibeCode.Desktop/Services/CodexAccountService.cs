using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeCode.Protocol;

namespace VibeCode.Services;

public enum CodexAccountOutcome
{
    Ok,
    Missing,
    NeedsRelogin,
    IsActive,
    SaveFailed,
}

/// <summary>One managed ChatGPT login saved in its own Codex home.</summary>
public sealed class CodexAccountInfo : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Plan { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool Usable { get; init; }
    public DateTime SavedAt { get; init; }
    public int? SessionPercent { get; init; }
    public int? WeekPercent { get; init; }
    public IReadOnlyList<CodexUsageLimit> UsageLimits { get; init; } = Array.Empty<CodexUsageLimit>();
    public DateTimeOffset? UsageUpdatedAt { get; init; }
    public int? AvailableResetCredits { get; init; }
    public bool? HasCredits { get; init; }
    public bool? UnlimitedCredits { get; init; }
    public string CreditBalance { get; init; } = "";
    public CodexSpendLimit? SpendLimit { get; init; }
    public string RateLimitStatus { get; init; } = "";

    public bool NeedsRelogin => !Usable;
    public bool AtLimit => SessionPercent >= 100 || WeekPercent >= 100
                           || !string.IsNullOrWhiteSpace(RateLimitStatusDisplay);
    public bool IsVibeCodeSelected => IsCurrent
                                      && string.Equals(AppSettings.Current.DefaultProvider, "codex", StringComparison.OrdinalIgnoreCase);
    public string Label => !string.IsNullOrWhiteSpace(Name) && !LooksLikeEmail(Name)
        ? Name.Trim()
        : $"OpenAI account · {StableSuffix(Id)}";
    public string Sub => ProviderLine;
    public string ProviderLine => Usable
        ? $"OpenAI Codex · {FormatPlan(Plan)}"
        : "OpenAI Codex · Sign in again";
    public string PlanDisplay => FormatPlan(Plan);
    public string UsageDisplay
    {
        get
        {
            var parts = new List<string>();
            if (SessionPercent is { } session) parts.Add($"{session}% session");
            if (WeekPercent is { } week) parts.Add($"{week}% week");
            if (parts.Count == 0)
                return NeedsRelogin ? "" : AtLimit ? "at limit" : UsageUpdatedAt is null ? "checking…" : "usage unavailable";
            return string.Join(" · ", parts) + (AtLimit ? " · at limit" : "");
        }
    }
    public string UsageUpdatedText
    {
        get
        {
            if (UsageUpdatedAt is not { } updatedAt) return "";
            var local = updatedAt.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? $"updated {local:h:mm tt}"
                : $"updated {local:MMM d, h:mm tt}";
        }
    }
    public bool HasResetCredits => AvailableResetCredits is > 0;
    public string ResetCreditsDisplay => AvailableResetCredits is { } count
        ? $"{count} earned limit reset{(count == 1 ? "" : "s")} available"
        : "";
    public bool HasCreditDetails => !string.IsNullOrWhiteSpace(CreditDetailsDisplay);
    public string CreditDetailsDisplay => UnlimitedCredits == true
        ? "Workspace credits · unlimited"
        : !string.IsNullOrWhiteSpace(CreditBalance)
            ? $"Workspace credits · {CreditBalance} remaining"
            : HasCredits == true
                ? "Additional workspace credits available"
                : HasCredits == false ? "Additional workspace credits · none" : "";
    public bool HasSpendLimit => SpendLimit is not null;
    public bool HasRateLimitStatus => !string.IsNullOrWhiteSpace(RateLimitStatusDisplay);
    public string RateLimitStatusDisplay => RateLimitStatus switch
    {
        "rate_limit_reached" => "Rate limit reached",
        "workspace_owner_credits_depleted" or "workspace_member_credits_depleted" => "Workspace credits depleted",
        "workspace_owner_usage_limit_reached" or "workspace_member_usage_limit_reached" => "Workspace usage limit reached",
        _ => "",
    };
    public string Initial => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "O";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshProviderSelection()
    {
        foreach (var property in new[]
                 {
                     nameof(IsVibeCodeSelected), nameof(Label), nameof(Sub), nameof(ProviderLine), nameof(Initial),
                     nameof(UsageDisplay),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static string FormatPlan(string? plan)
    {
        var value = plan?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? "Subscription"
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1;
    }

    private static string StableSuffix(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length == 0) return "ACCOUNT";
        return compact.Length > 4 ? compact[^4..].ToUpperInvariant() : compact.ToUpperInvariant();
    }
}

/// <summary>
/// Keeps every VibeCode OpenAI login in a separate CODEX_HOME. The original %APPDATA%\VibeCode\codex directory is
/// adopted in place as the first profile, preserving its existing threads. New browser logins happen in a fresh home
/// and are committed only after Codex reports a stable account id, so adding an account can never overwrite one that
/// is already saved.
/// </summary>
public sealed class CodexAccountService
{
    public static CodexAccountService Instance { get; } = new();
    public static event Action? AccountsChanged;

    private sealed class SavedAccount
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Plan { get; set; } = "";
        public bool UsesLegacyHome { get; set; }
        public string HomeFolder { get; set; } = "home";
        public DateTime SavedAt { get; set; }
        public int? SessionPercent { get; set; }
        public int? WeekPercent { get; set; }
        public List<CodexUsageLimit> UsageLimits { get; set; } = new();
        public DateTimeOffset? UsageUpdatedAt { get; set; }
        public int? AvailableResetCredits { get; set; }
        public bool? HasCredits { get; set; }
        public bool? UnlimitedCredits { get; set; }
        public string CreditBalance { get; set; } = "";
        public CodexSpendLimit? SpendLimit { get; set; }
        public string RateLimitStatus { get; set; } = "";
    }

    private sealed record StoredAccount(SavedAccount Metadata, string DirectoryPath);
    private sealed class ActiveSelection { public string? AccountId { get; set; } }

    private readonly object _fileGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    public string? LastError { get; private set; }

    private static string StoreRoot => Environment.GetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE") is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : Path.Combine(AppSettings.Dir, "codex-accounts");
    private static string LegacyHome => Environment.GetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME") is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : CodexEnvironment.HomeDirectory;
    private static string ActivePath => Path.Combine(StoreRoot, "active.json");

    public string? ActiveId
    {
        get
        {
            lock (_fileGate)
            {
                EnsureLegacyCaptured();
                var accounts = LoadAccounts();
                return ResolveActiveId(accounts);
            }
        }
    }

    public List<CodexAccountInfo> List()
    {
        lock (_fileGate)
        {
            EnsureLegacyCaptured();
            return BuildList(LoadAccounts());
        }
    }

    /// <summary>Resolve a captured account to its private home. A missing saved id gets an empty quarantine home so a
    /// stale restored chat fails signed-out instead of silently running as whichever account happens to be active.</summary>
    public string HomeFor(string? accountId)
    {
        lock (_fileGate)
        {
            EnsureLegacyCaptured();
            var accounts = LoadAccounts();
            accountId ??= ResolveActiveId(accounts);
            var stored = accounts.FirstOrDefault(x => x.Metadata.Id == accountId);
            if (stored is not null && IsUsable(stored)) return HomeOf(stored);
            if (string.IsNullOrWhiteSpace(accountId)) return LegacyHome;
            var quarantine = Path.Combine(StoreRoot, "unavailable", DirectoryKey(accountId));
            Directory.CreateDirectory(quarantine);
            return quarantine;
        }
    }

    public CodexAccountOutcome Select(string id)
    {
        var changed = false;
        lock (_fileGate)
        {
            LastError = null;
            try
            {
                EnsureLegacyCaptured();
                var account = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == id);
                if (account is null) return CodexAccountOutcome.Missing;
                if (!IsUsable(account)) return CodexAccountOutcome.NeedsRelogin;
                WriteActive(id);
                changed = true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return CodexAccountOutcome.SaveFailed;
            }
        }
        if (changed) AccountsChanged?.Invoke();
        return CodexAccountOutcome.Ok;
    }

    public CodexAccountOutcome Forget(string id)
    {
        var changed = false;
        lock (_fileGate)
        {
            LastError = null;
            try
            {
                EnsureLegacyCaptured();
                var accounts = LoadAccounts();
                var account = accounts.FirstOrDefault(x => x.Metadata.Id == id);
                if (account is null) return CodexAccountOutcome.Missing;
                // Allow removing the active/main account: fall back to another usable login, or nothing.
                if (ResolveActiveId(accounts) == id)
                {
                    var next = accounts.FirstOrDefault(x => x.Metadata.Id != id && IsUsable(x));
                    WriteActive(next?.Metadata.Id);
                }

                if (account.Metadata.UsesLegacyHome)
                {
                    var auth = Path.Combine(LegacyHome, "auth.json");
                    if (File.Exists(auth)) File.Delete(auth);
                    DeleteDirectoryRobust(account.DirectoryPath);
                }
                else
                {
                    var full = Path.GetFullPath(account.DirectoryPath);
                    var root = Path.GetFullPath(StoreRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Refused to remove an account directory outside VibeCode's account store.");
                    DeleteDirectoryRobust(full);
                }
                changed = true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return CodexAccountOutcome.SaveFailed;
            }
        }
        if (changed) AccountsChanged?.Invoke();
        return CodexAccountOutcome.Ok;
    }

    /// <summary>Validate a saved Codex login end-to-end, like Claude's "Test account": force a live read against ITS
    /// OWN private home (any token refresh happens in place, so testing can never strand/corrupt the login) and
    /// translate the result into a debug-friendly verdict: invalid / valid / valid-but-at-usage-limit.</summary>
    public async Task<AccountTestResult> TestAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        StoredAccount? stored;
        lock (_fileGate) { EnsureLegacyCaptured(); stored = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == id); }
        if (stored is null)
            return new AccountTestResult { Ok = false, Verdict = "No saved login", Detail = "VibeCode has no stored Codex account with this id." };

        CodexAccountState state;
        try { state = await CodexAccountLoginService.ReadAsync(HomeOf(stored), forceRefresh: true, cancellationToken); }
        catch (Exception ex)
        {
            return new AccountTestResult { Ok = false, Verdict = "Test failed", Detail = ex.Message };
        }

        var raw = state.Error ?? "";
        if (!state.IsSignedIn || (!string.IsNullOrWhiteSpace(state.Error) && !state.UsageReadSucceeded))
            return new AccountTestResult
            {
                Ok = false, Verdict = "Credentials invalid",
                Detail = string.IsNullOrWhiteSpace(state.Error)
                    ? "This Codex login is signed out. Re-add the account (account menu → Add account) to refresh it."
                    : $"Codex rejected this login: {state.Error}",
                Raw = raw,
            };
        if (!string.IsNullOrWhiteSpace(state.AccountId) && state.AccountId != id)
            return new AccountTestResult
            {
                Ok = false, Verdict = "Wrong account on disk",
                Detail = "This profile's home now holds a different ChatGPT account. Re-add both accounts to untangle them.",
                Raw = raw,
            };

        // Signed in - persist the freshly probed identity/usage so the account rows update immediately.
        lock (_fileGate)
        {
            stored.Metadata.Name = state.Name ?? stored.Metadata.Name;
            stored.Metadata.Email = state.Email ?? stored.Metadata.Email;
            stored.Metadata.Plan = state.Plan ?? stored.Metadata.Plan;
            if (state.UsageReadSucceeded)
            {
                stored.Metadata.SessionPercent = state.SessionPercent;
                stored.Metadata.WeekPercent = state.WeekPercent;
                stored.Metadata.UsageLimits = state.UsageLimits;
                stored.Metadata.UsageUpdatedAt = state.UsageUpdatedAt;
                stored.Metadata.RateLimitStatus = state.RateLimitStatus ?? "";
            }
            WriteMetadata(stored.DirectoryPath, stored.Metadata);
        }
        AccountsChanged?.Invoke();

        var usageBits = new List<string>();
        if (state.SessionPercent is { } s) usageBits.Add($"{s}% session");
        if (state.WeekPercent is { } w) usageBits.Add($"{w}% week");
        var usage = usageBits.Count > 0 ? string.Join(" · ", usageBits) : "usage unavailable";
        var atLimit = state.SessionPercent >= 100 || state.WeekPercent >= 100
                      || string.Equals(state.RateLimitStatus, "rate_limit_reached", StringComparison.OrdinalIgnoreCase);
        return atLimit
            ? new AccountTestResult { Ok = true, Verdict = "Valid — at usage limit", Detail = $"Login works, but this account is maxed out ({usage}). It'll work again after the reset.", Raw = raw }
            : new AccountTestResult { Ok = true, Verdict = "Valid & working", Detail = $"Login works. Usage: {usage}.", Raw = raw };
    }

    /// <summary>Copy a thread's persisted rollout file(s) from one account's home into another's, so the SAME
    /// conversation can resume under a different login (moving a chat off an exhausted account). Copy, never move —
    /// the source account keeps its own history. Returns false when no rollout for the thread was found.</summary>
    public bool MigrateThread(string threadId, string? fromAccountId, string? toAccountId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return false;
        lock (_fileGate)
        {
            try
            {
                // NOT HomeFor(): that quarantines an account whose login is currently broken, which would make this
                // read from an empty dir and silently lose the conversation - migrating AWAY from a broken/exhausted
                // account is precisely when this runs. Resolve the account's REAL home regardless of usability.
                var accounts = LoadAccounts();
                string ResolveHome(string? id)
                {
                    id ??= ResolveActiveId(accounts);
                    var stored = accounts.FirstOrDefault(x => x.Metadata.Id == id);
                    return stored is not null ? HomeOf(stored) : LegacyHome;
                }
                var fromHome = ResolveHome(fromAccountId);
                var toHome = ResolveHome(toAccountId);
                if (string.Equals(fromHome, toHome, StringComparison.OrdinalIgnoreCase)) return true;
                var copied = false;
                // Mirror CodexEnvironment.ImportOwnedSessions: rollouts live under sessions/ AND archived_sessions/,
                // and rollout filenames embed the thread id (rollout-<timestamp>-<threadId>.jsonl).
                foreach (var folder in new[] { "sessions", "archived_sessions" })
                {
                    var sourceRoot = Path.Combine(fromHome, folder);
                    if (!Directory.Exists(sourceRoot)) continue;
                    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.jsonl", SearchOption.AllDirectories))
                    {
                        if (!Path.GetFileName(file).Contains(threadId, StringComparison.OrdinalIgnoreCase)) continue;
                        var dest = Path.Combine(toHome, folder, Path.GetRelativePath(sourceRoot, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(file, dest, overwrite: true);   // source may be newer than an earlier copy - always take it
                        copied = true;
                    }
                }
                // Recent Codex builds FIND rollouts via session_index.jsonl - without the row, thread/resume in the
                // new home can't locate the thread even though the rollout file is right there.
                try
                {
                    var sourceIndex = Path.Combine(fromHome, "session_index.jsonl");
                    if (copied && File.Exists(sourceIndex))
                    {
                        var targetIndex = Path.Combine(toHome, "session_index.jsonl");
                        var existing = File.Exists(targetIndex) ? File.ReadAllText(targetIndex) : "";
                        var rows = File.ReadLines(sourceIndex)
                            .Where(line => line.Contains(threadId, StringComparison.OrdinalIgnoreCase)
                                           && !existing.Contains(line, StringComparison.Ordinal))
                            .ToArray();
                        if (rows.Length > 0) File.AppendAllLines(targetIndex, rows, new UTF8Encoding(false));
                    }
                }
                catch { /* the index is an optimization; the app-server can still scan rollout files */ }
                return copied;
            }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }
    }

    /// <summary>Authenticate in a brand-new home, then atomically publish that home as a saved account.</summary>
    public async Task<CodexLoginResult> AddAsync(Action<string> openBrowser, CancellationToken cancellationToken)
    {
        var gateHeld = false;
        string? pendingDirectory = null;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            lock (_fileGate) EnsureLegacyCaptured(); // snapshot the old identity before opening another OAuth flow
            Directory.CreateDirectory(StoreRoot);
            pendingDirectory = Path.Combine(StoreRoot, ".pending-" + Guid.NewGuid().ToString("N"));
            var pendingHome = Path.Combine(pendingDirectory, "home");
            Directory.CreateDirectory(pendingHome);

            var result = await CodexAccountLoginService.LoginAsync(pendingHome, openBrowser, cancellationToken);
            if (!result.Success) return result;
            if (string.IsNullOrWhiteSpace(result.AccountId))
                return new CodexLoginResult { Error = "OpenAI sign-in completed, but Codex did not expose a stable account id. The existing saved accounts were left unchanged." };

            lock (_fileGate)
            {
                var accounts = LoadAccounts();
                var existing = accounts.FirstOrDefault(x => x.Metadata.Id == result.AccountId);
                SavedAccount metadata;
                string accountDirectory;
                if (existing is null)
                {
                    accountDirectory = AvailableAccountDirectory(result.AccountId);
                    Directory.Move(pendingDirectory, accountDirectory);
                    pendingDirectory = null;
                    metadata = new SavedAccount
                    {
                        Id = result.AccountId,
                        UsesLegacyHome = false,
                        HomeFolder = "home",
                        SavedAt = DateTime.Now,
                    };
                }
                else
                {
                    accountDirectory = existing.DirectoryPath;
                    metadata = existing.Metadata;
                    CopyAuthAtomically(pendingHome, HomeOf(existing));
                    metadata.SavedAt = DateTime.Now;
                }

                metadata.Name = result.Name ?? metadata.Name;
                metadata.Email = result.Email ?? metadata.Email;
                metadata.Plan = result.Plan ?? metadata.Plan;
                WriteMetadata(accountDirectory, metadata);
                WriteActive(metadata.Id);
            }
            AccountsChanged?.Invoke();
            return result;
        }
        catch (OperationCanceledException)
        {
            return new CodexLoginResult { Error = cancellationToken.IsCancellationRequested ? "Sign-in cancelled." : "Sign-in timed out." };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new CodexLoginResult { Error = ex.Message };
        }
        finally
        {
            if (pendingDirectory is not null)
                try { Directory.Delete(pendingDirectory, recursive: true); } catch { /* best-effort pending-login cleanup */ }
            if (gateHeld) _operationGate.Release();
        }
    }

    /// <summary>Refresh identity, plan and rate limits for every saved account without changing the active selection.</summary>
    public async Task<List<CodexAccountInfo>> RefreshAllAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var gateHeld = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            List<StoredAccount> accounts;
            lock (_fileGate)
            {
                EnsureLegacyCaptured();
                accounts = LoadAccounts();
            }

            foreach (var stored in accounts.Where(IsUsable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await CodexAccountLoginService.ReadAsync(HomeOf(stored), forceRefresh, cancellationToken);
                if (!state.IsSignedIn || !string.IsNullOrWhiteSpace(state.Error)) continue;
                if (!string.IsNullOrWhiteSpace(state.AccountId) && state.AccountId != stored.Metadata.Id) continue;
                lock (_fileGate)
                {
                    stored.Metadata.Name = state.Name ?? stored.Metadata.Name;
                    stored.Metadata.Email = state.Email ?? stored.Metadata.Email;
                    stored.Metadata.Plan = state.Plan ?? stored.Metadata.Plan;
                    if (state.UsageReadSucceeded)
                    {
                        stored.Metadata.SessionPercent = state.SessionPercent;
                        stored.Metadata.WeekPercent = state.WeekPercent;
                        stored.Metadata.UsageLimits = state.UsageLimits;
                        stored.Metadata.UsageUpdatedAt = state.UsageUpdatedAt;
                        stored.Metadata.AvailableResetCredits = state.AvailableResetCredits;
                        stored.Metadata.HasCredits = state.HasCredits;
                        stored.Metadata.UnlimitedCredits = state.UnlimitedCredits;
                        stored.Metadata.CreditBalance = state.CreditBalance ?? "";
                        stored.Metadata.SpendLimit = state.SpendLimit;
                        stored.Metadata.RateLimitStatus = state.RateLimitStatus ?? "";
                    }
                    WriteMetadata(stored.DirectoryPath, stored.Metadata);
                }
            }

            lock (_fileGate) return BuildList(LoadAccounts());
        }
        finally { if (gateHeld) _operationGate.Release(); }
    }

    private static void EnsureLegacyCaptured()
    {
        Directory.CreateDirectory(StoreRoot);
        var live = CodexAccountLoginService.InspectStoredAccount(LegacyHome);
        if (!live.IsSignedIn || string.IsNullOrWhiteSpace(live.AccountId)) return;
        var accounts = LoadAccounts();
        var existing = accounts.FirstOrDefault(x => x.Metadata.Id == live.AccountId);
        if (existing is null)
        {
            var directory = AvailableAccountDirectory(live.AccountId);
            var metadata = new SavedAccount
            {
                Id = live.AccountId,
                Name = live.Name ?? "",
                Email = live.Email ?? "",
                UsesLegacyHome = true,
                SavedAt = File.GetLastWriteTime(Path.Combine(LegacyHome, "auth.json")),
            };
            WriteMetadata(directory, metadata);
            accounts.Add(new StoredAccount(metadata, directory));
        }
        else if (existing.Metadata.UsesLegacyHome)
        {
            existing.Metadata.Name = live.Name ?? existing.Metadata.Name;
            existing.Metadata.Email = live.Email ?? existing.Metadata.Email;
            WriteMetadata(existing.DirectoryPath, existing.Metadata);
        }

        if (ReadActive() is not { Length: > 0 } active || accounts.All(x => x.Metadata.Id != active))
            WriteActive(live.AccountId);
    }

    private static List<StoredAccount> LoadAccounts()
    {
        var result = new List<StoredAccount>();
        if (!Directory.Exists(StoreRoot)) return result;
        foreach (var directory in Directory.EnumerateDirectories(StoreRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("unavailable", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(directory, "account.json");
            if (!File.Exists(path)) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<SavedAccount>(File.ReadAllText(path));
                if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Id))
                    result.Add(new StoredAccount(metadata, directory));
            }
            catch { /* one corrupt metadata file must not hide every other account */ }
        }
        return result;
    }

    private static List<CodexAccountInfo> BuildList(List<StoredAccount> accounts)
    {
        var active = ResolveActiveId(accounts);
        return accounts.Select(x => new CodexAccountInfo
            {
                Id = x.Metadata.Id,
                Name = x.Metadata.Name,
                Email = x.Metadata.Email,
                Plan = x.Metadata.Plan,
                IsCurrent = x.Metadata.Id == active,
                Usable = IsUsable(x),
                SavedAt = x.Metadata.SavedAt,
                SessionPercent = x.Metadata.SessionPercent,
                WeekPercent = x.Metadata.WeekPercent,
                UsageLimits = EffectiveUsageLimits(x.Metadata),
                UsageUpdatedAt = x.Metadata.UsageUpdatedAt,
                AvailableResetCredits = x.Metadata.AvailableResetCredits,
                HasCredits = x.Metadata.HasCredits,
                UnlimitedCredits = x.Metadata.UnlimitedCredits,
                CreditBalance = x.Metadata.CreditBalance,
                SpendLimit = x.Metadata.SpendLimit,
                RateLimitStatus = x.Metadata.RateLimitStatus,
            })
            .OrderByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.SavedAt)
            .ToList();
    }

    private static IReadOnlyList<CodexUsageLimit> EffectiveUsageLimits(SavedAccount account)
    {
        if (account.UsageLimits is { Count: > 0 }) return account.UsageLimits;
        var legacy = new List<CodexUsageLimit>(2);
        if (account.SessionPercent is { } session)
            legacy.Add(new CodexUsageLimit { LimitId = "codex", Label = "Session", Percent = Math.Clamp(session, 0, 100) });
        if (account.WeekPercent is { } week)
            legacy.Add(new CodexUsageLimit { LimitId = "codex", Label = "This week", Percent = Math.Clamp(week, 0, 100) });
        return legacy;
    }

    private static string? ResolveActiveId(List<StoredAccount> accounts)
    {
        var selected = ReadActive();
        if (selected is not null && accounts.Any(x => x.Metadata.Id == selected)) return selected;
        var fallback = accounts.FirstOrDefault(IsUsable)?.Metadata.Id ?? accounts.FirstOrDefault()?.Metadata.Id;
        if (fallback is not null) WriteActive(fallback);
        return fallback;
    }

    private static bool IsUsable(StoredAccount account)
    {
        var state = CodexAccountLoginService.InspectStoredAccount(HomeOf(account));
        return state.IsSignedIn && state.AccountId == account.Metadata.Id;
    }

    private static string HomeOf(StoredAccount account) => account.Metadata.UsesLegacyHome
        ? LegacyHome
        : Path.Combine(account.DirectoryPath, string.IsNullOrWhiteSpace(account.Metadata.HomeFolder) ? "home" : account.Metadata.HomeFolder);

    private static string? ReadActive()
    {
        try { return JsonSerializer.Deserialize<ActiveSelection>(File.ReadAllText(ActivePath))?.AccountId; }
        catch { return null; }
    }

    private static void WriteActive(string? id)
    {
        Directory.CreateDirectory(StoreRoot);
        // null = no preferred Codex account (UI shows unsigned / pick one)
        WriteJsonAtomically(ActivePath, new ActiveSelection { AccountId = id });
    }

    private static void WriteMetadata(string directory, SavedAccount metadata)
    {
        Directory.CreateDirectory(directory);
        WriteJsonAtomically(Path.Combine(directory, "account.json"), metadata);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Delete an account directory even when the codex CLI has scaffolded read-only bits inside it (a
    /// "Test account" probe creates plugins/ etc.). Clears attributes and retries once; a live process still holding
    /// the directory surfaces as SaveFailed with the real reason, never a silent half-delete.</summary>
    private static void DeleteDirectoryRobust(string path)
    {
        try { Directory.Delete(path, recursive: true); return; }
        catch (UnauthorizedAccessException) { /* fall through to the attribute-clearing retry */ }
        catch (IOException) { /* fall through to the attribute-clearing retry */ }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(entry, FileAttributes.Normal); } catch { /* best-effort */ }
        System.Threading.Thread.Sleep(250);   // give a just-exited codex probe a beat to release handles
        Directory.Delete(path, recursive: true);
    }

    private static void CopyAuthAtomically(string sourceHome, string destinationHome)
    {
        var source = Path.Combine(sourceHome, "auth.json");
        if (!File.Exists(source)) throw new InvalidOperationException("The completed Codex login did not create auth.json.");
        Directory.CreateDirectory(destinationHome);
        var destination = Path.Combine(destinationHome, "auth.json");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    private static string AvailableAccountDirectory(string id)
    {
        Directory.CreateDirectory(StoreRoot);
        var basePath = Path.Combine(StoreRoot, DirectoryKey(id));
        var candidate = basePath;
        for (var suffix = 2; Directory.Exists(candidate); suffix++) candidate = basePath + "-" + suffix;
        return candidate;
    }

    private static string DirectoryKey(string id)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(digest[..12]).ToLowerInvariant();
    }
}
