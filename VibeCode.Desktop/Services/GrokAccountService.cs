using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeCode.Services;

public enum GrokAccountOutcome
{
    Ok,
    Missing,
    NeedsRelogin,
    IsActive,
    SaveFailed,
}

public sealed class GrokAccountInfo : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Plan { get; init; } = "";
    public string AuthMode { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool Usable { get; init; }
    public DateTime SavedAt { get; init; }
    public int? UsagePercent { get; init; }
    public string UsagePeriod { get; init; } = "";
    public DateTimeOffset? UsageResetsAt { get; init; }
    public long? PrepaidBalanceCents { get; init; }
    public DateTimeOffset? UsageUpdatedAt { get; init; }

    public bool NeedsRelogin => !Usable;
    public bool HasUsage => UsagePercent is not null;
    public bool HasReset => UsageResetsAt is not null;
    public bool HasCredit => PrepaidBalanceCents is not null;
    public bool AtLimit => UsagePercent >= 100;
    public bool IsVibeCodeSelected => IsCurrent
                                      && string.Equals(AppSettings.Current.DefaultProvider, "grok",
                                          StringComparison.OrdinalIgnoreCase);
    public string Label => !string.IsNullOrWhiteSpace(Name) && !LooksLikeEmail(Name)
        ? Name.Trim()
        : $"Grok account · {StableSuffix(Id)}";
    public string Sub => ProviderLine;
    public string ProviderLine => Usable
        ? $"Grok · {FormatPlan(Plan)}"
        : "Grok · Sign in again";
    public string PlanDisplay => FormatPlan(Plan);
    public string UsageLabel => string.IsNullOrWhiteSpace(UsagePeriod)
        ? "Account usage"
        : $"{char.ToUpperInvariant(UsagePeriod.Trim()[0])}{UsagePeriod.Trim()[1..]} usage";
    public string UsageSummary => NeedsRelogin
        ? "sign in again"
        : UsagePercent is { } percent
            ? $"{percent}%{(string.IsNullOrWhiteSpace(UsagePeriod) ? "" : $" {UsagePeriod.Trim().ToLowerInvariant()}")}"
            : UsageUpdatedAt is null ? "checking…" : "usage unavailable";
    public string ResetDisplay => UsageResetsAt is { } reset
        ? $"Resets {reset.ToLocalTime():MMM d, h:mm tt}"
        : "";
    public string UpdatedDisplay => UsageUpdatedAt is { } updated
        ? $"Updated {updated.ToLocalTime():MMM d, h:mm tt}"
        : "";
    public string UsageDisplay
    {
        get
        {
            if (NeedsRelogin) return "";
            var parts = new List<string>();
            if (UsagePercent is { } percent)
                parts.Add($"{percent}% {(string.IsNullOrWhiteSpace(UsagePeriod) ? "usage" : UsagePeriod.Trim().ToLowerInvariant())}");
            if (HasReset)
                parts.Add(ResetDisplay.ToLowerInvariant());
            if (parts.Count == 0)
                return UsageUpdatedAt is null ? "checking…" : "usage unavailable";
            return string.Join(" · ", parts) + (AtLimit ? " · at limit" : "");
        }
    }
    public string CreditDisplay => PrepaidBalanceCents is { } cents
        ? $"Prepaid balance · {(cents / 100m).ToString("C", CultureInfo.CurrentCulture)}"
        : "";
    public string Initial => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "G";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshProviderSelection()
    {
        foreach (var property in new[]
                 {
                     nameof(IsVibeCodeSelected), nameof(Label), nameof(Sub), nameof(ProviderLine),
                     nameof(Initial), nameof(UsageDisplay),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static string FormatPlan(string? plan)
    {
        var value = plan?.Trim().Replace('_', ' ');
        return string.IsNullOrWhiteSpace(value)
            ? "Account"
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
/// Persists each Grok login in its own auth.json and selects it per subprocess through GROK_AUTH_PATH. The original
/// ~/.grok/auth.json is adopted in place, so upgrading does not discard the login the user already had.
/// </summary>
public sealed class GrokAccountService
{
    public static GrokAccountService Instance { get; } = new();
    public static event Action? AccountsChanged;

    private sealed class SavedAccount
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Plan { get; set; } = "";
        public string AuthMode { get; set; } = "";
        public bool UsesLegacyAuth { get; set; }
        public string AuthFileName { get; set; } = "auth.json";
        public DateTime SavedAt { get; set; }
        public int? UsagePercent { get; set; }
        public string UsagePeriod { get; set; } = "";
        public DateTimeOffset? UsageResetsAt { get; set; }
        public long? PrepaidBalanceCents { get; set; }
        public DateTimeOffset? UsageUpdatedAt { get; set; }
    }

    private sealed record StoredAccount(SavedAccount Metadata, string DirectoryPath);
    private sealed class ActiveSelection { public string? AccountId { get; set; } }

    private readonly object _fileGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    public string? LastError { get; private set; }

    private static string StoreRoot =>
        Environment.GetEnvironmentVariable("VIBECODE_GROK_ACCOUNT_STORE") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppSettings.Dir, "grok-accounts");
    private static string LegacyAuthPath =>
        Environment.GetEnvironmentVariable("VIBECODE_GROK_LEGACY_AUTH_PATH") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(GrokAccountLoginService.DefaultHome, "auth.json");
    private static string ActivePath => Path.Combine(StoreRoot, "active.json");

    public string? ActiveId
    {
        get
        {
            lock (_fileGate)
            {
                EnsureLegacyCaptured();
                return ResolveActiveId(LoadAccounts());
            }
        }
    }

    public List<GrokAccountInfo> List()
    {
        lock (_fileGate)
        {
            EnsureLegacyCaptured();
            return BuildList(LoadAccounts());
        }
    }

    public string AuthPathFor(string? accountId)
    {
        lock (_fileGate)
        {
            EnsureLegacyCaptured();
            var accounts = LoadAccounts();
            accountId ??= ResolveActiveId(accounts);
            var stored = accounts.FirstOrDefault(x => x.Metadata.Id == accountId);
            if (stored is not null) return AuthPathOf(stored);
            if (string.IsNullOrWhiteSpace(accountId)) return LegacyAuthPath;
            var quarantine = Path.Combine(StoreRoot, "unavailable", DirectoryKey(accountId), "auth.json");
            Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
            return quarantine;
        }
    }

    public GrokAccountOutcome Select(string id)
    {
        var changed = false;
        lock (_fileGate)
        {
            LastError = null;
            try
            {
                EnsureLegacyCaptured();
                var account = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == id);
                if (account is null) return GrokAccountOutcome.Missing;
                if (!IsUsable(account)) return GrokAccountOutcome.NeedsRelogin;
                WriteActive(id);
                changed = true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return GrokAccountOutcome.SaveFailed;
            }
        }
        if (changed) AccountsChanged?.Invoke();
        return GrokAccountOutcome.Ok;
    }

    public GrokAccountOutcome Forget(string id)
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
                if (account is null) return GrokAccountOutcome.Missing;
                // Allow removing the active/main account: fall back to another usable login, or nothing.
                if (ResolveActiveId(accounts) == id)
                {
                    var next = accounts.FirstOrDefault(x => x.Metadata.Id != id && IsUsable(x));
                    WriteActive(next?.Metadata.Id);
                }
                if (account.Metadata.UsesLegacyAuth)
                {
                    if (File.Exists(LegacyAuthPath)) File.Delete(LegacyAuthPath);
                }
                DeleteAccountDirectory(account.DirectoryPath);
                changed = true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return GrokAccountOutcome.SaveFailed;
            }
        }
        if (changed) AccountsChanged?.Invoke();
        return GrokAccountOutcome.Ok;
    }

    public async Task<GrokLoginResult> AddAsync(GrokLoginMode mode, Action<string> openBrowser,
        Action<string>? statusChanged, CancellationToken cancellationToken)
    {
        var gateHeld = false;
        string? pendingDirectory = null;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            lock (_fileGate) EnsureLegacyCaptured();
            Directory.CreateDirectory(StoreRoot);
            pendingDirectory = Path.Combine(StoreRoot, ".pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pendingDirectory);
            var pendingAuth = Path.Combine(pendingDirectory, "auth.json");
            var result = await GrokAccountLoginService.LoginAsync(
                pendingAuth, mode, openBrowser, statusChanged, cancellationToken);
            if (!result.Success) return result;
            if (string.IsNullOrWhiteSpace(result.AccountId))
                return new GrokLoginResult
                {
                    Error = "Grok sign-in completed, but the CLI did not expose a stable account id. Existing accounts were left unchanged.",
                };

            lock (_fileGate)
            {
                var existing = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == result.AccountId);
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
                        SavedAt = DateTime.Now,
                    };
                }
                else
                {
                    accountDirectory = existing.DirectoryPath;
                    metadata = existing.Metadata;
                    CopyFileAtomically(pendingAuth, AuthPathOf(existing));
                    metadata.SavedAt = DateTime.Now;
                }
                ApplyResult(metadata, result);
                WriteMetadata(accountDirectory, metadata);
                WriteActive(metadata.Id);
            }
            AccountsChanged?.Invoke();
            return result;
        }
        catch (OperationCanceledException)
        {
            return new GrokLoginResult { Error = "Grok sign-in cancelled." };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new GrokLoginResult { Error = ex.Message };
        }
        finally
        {
            if (pendingDirectory is not null)
                try { Directory.Delete(pendingDirectory, recursive: true); } catch { }
            if (gateHeld) _operationGate.Release();
        }
    }

    public async Task<List<GrokAccountInfo>> RefreshAllAsync(CancellationToken cancellationToken = default)
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
                var state = await GrokAccountLoginService.ReadAsync(AuthPathOf(stored), true, cancellationToken);
                if (!state.IsSignedIn || !string.IsNullOrWhiteSpace(state.Error)) continue;
                if (!string.IsNullOrWhiteSpace(state.AccountId) && state.AccountId != stored.Metadata.Id) continue;
                lock (_fileGate)
                {
                    ApplyState(stored.Metadata, state);
                    WriteMetadata(stored.DirectoryPath, stored.Metadata);
                }
            }
            List<GrokAccountInfo> refreshed;
            lock (_fileGate) refreshed = BuildList(LoadAccounts());
            AccountsChanged?.Invoke();
            return refreshed;
        }
        finally
        {
            if (gateHeld) _operationGate.Release();
        }
    }

    public async Task<AccountTestResult> TestAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        StoredAccount? stored;
        lock (_fileGate)
        {
            EnsureLegacyCaptured();
            stored = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == id);
        }
        if (stored is null)
            return new AccountTestResult
            {
                Ok = false,
                Verdict = "No saved login",
                Detail = "VibeCode has no stored Grok account with this id.",
            };
        var state = await GrokAccountLoginService.ReadAsync(AuthPathOf(stored), true, cancellationToken);
        if (!state.IsSignedIn)
            return new AccountTestResult
            {
                Ok = false,
                Verdict = "Credentials invalid",
                Detail = state.Error ?? "This Grok account is signed out. Add it again to refresh the login.",
                Raw = state.Error ?? "",
            };
        if (!string.IsNullOrWhiteSpace(state.AccountId) && state.AccountId != id)
            return new AccountTestResult
            {
                Ok = false,
                Verdict = "Wrong account on disk",
                Detail = "This Grok profile contains a different account.",
            };
        lock (_fileGate)
        {
            ApplyState(stored.Metadata, state);
            WriteMetadata(stored.DirectoryPath, stored.Metadata);
        }
        AccountsChanged?.Invoke();
        var usage = state.UsagePercent is { } percent
            ? $"{percent}% {state.UsagePeriod ?? "usage"}"
            : "account usage unavailable";
        return new AccountTestResult
        {
            Ok = true,
            Verdict = state.UsagePercent >= 100 ? "Valid — at usage limit" : "Valid & working",
            Detail = $"Login works. Usage: {usage}.",
            Raw = state.UsageError ?? "",
        };
    }

    public async Task<GrokLogoutResult> LogoutAsync(string id, CancellationToken cancellationToken = default)
    {
        var gateHeld = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken);
            gateHeld = true;
            StoredAccount? stored;
            lock (_fileGate)
            {
                EnsureLegacyCaptured();
                stored = LoadAccounts().FirstOrDefault(x => x.Metadata.Id == id);
            }
            if (stored is null) return new GrokLogoutResult { Error = "This Grok account is no longer saved." };
            var result = await GrokAccountLoginService.LogoutAsync(AuthPathOf(stored), cancellationToken);
            if (!result.Success) return result;
            lock (_fileGate)
            {
                var accounts = LoadAccounts();
                if (ReadActive() == id)
                {
                    var next = accounts.FirstOrDefault(x => x.Metadata.Id != id && IsUsable(x));
                    WriteActive(next?.Metadata.Id);
                }
            }
            AccountsChanged?.Invoke();
            return result;
        }
        finally
        {
            if (gateHeld) _operationGate.Release();
        }
    }

    private static void ApplyResult(SavedAccount metadata, GrokLoginResult result)
    {
        metadata.Name = result.Name ?? metadata.Name;
        metadata.Email = result.Email ?? metadata.Email;
        metadata.Plan = result.Plan ?? metadata.Plan;
        metadata.AuthMode = result.AuthMode ?? metadata.AuthMode;
        metadata.UsagePercent = result.UsagePercent;
        metadata.UsagePeriod = result.UsagePeriod ?? metadata.UsagePeriod;
        metadata.UsageResetsAt = result.UsageResetsAt;
        metadata.PrepaidBalanceCents = result.PrepaidBalanceCents;
        metadata.UsageUpdatedAt = DateTimeOffset.Now;
    }

    private static void ApplyState(SavedAccount metadata, GrokAccountState state)
    {
        metadata.Name = state.Name ?? metadata.Name;
        metadata.Email = state.Email ?? metadata.Email;
        metadata.Plan = state.Plan ?? metadata.Plan;
        metadata.AuthMode = state.AuthMode ?? metadata.AuthMode;
        if (state.UsageReadSucceeded)
        {
            metadata.UsagePercent = state.UsagePercent;
            metadata.UsagePeriod = state.UsagePeriod ?? "";
            metadata.UsageResetsAt = state.UsageResetsAt;
            metadata.PrepaidBalanceCents = state.PrepaidBalanceCents;
            metadata.UsageUpdatedAt = DateTimeOffset.Now;
        }
    }

    private static void EnsureLegacyCaptured()
    {
        Directory.CreateDirectory(StoreRoot);
        var identity = GrokAccountLoginService.InspectStoredAccount(LegacyAuthPath);
        if (string.IsNullOrWhiteSpace(identity.AccountId)) return;
        var accounts = LoadAccounts();
        var existing = accounts.FirstOrDefault(x => x.Metadata.Id == identity.AccountId);
        if (existing is null)
        {
            var directory = AvailableAccountDirectory(identity.AccountId);
            var metadata = new SavedAccount
            {
                Id = identity.AccountId,
                Name = identity.Name ?? "",
                Email = identity.Email ?? "",
                AuthMode = identity.AuthMode ?? "",
                UsesLegacyAuth = true,
                SavedAt = File.GetLastWriteTime(LegacyAuthPath),
            };
            WriteMetadata(directory, metadata);
            accounts.Add(new StoredAccount(metadata, directory));
        }
        else if (existing.Metadata.UsesLegacyAuth)
        {
            existing.Metadata.Name = identity.Name ?? existing.Metadata.Name;
            existing.Metadata.Email = identity.Email ?? existing.Metadata.Email;
            existing.Metadata.AuthMode = identity.AuthMode ?? existing.Metadata.AuthMode;
            WriteMetadata(existing.DirectoryPath, existing.Metadata);
        }
        if (ReadActive() is not { Length: > 0 } active || accounts.All(x => x.Metadata.Id != active))
            WriteActive(identity.AccountId);
    }

    private static List<StoredAccount> LoadAccounts()
    {
        var result = new List<StoredAccount>();
        if (!Directory.Exists(StoreRoot)) return result;
        foreach (var directory in Directory.EnumerateDirectories(StoreRoot))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal)
                || name.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(directory, "account.json");
            if (!File.Exists(path)) continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<SavedAccount>(File.ReadAllText(path));
                if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Id))
                    result.Add(new StoredAccount(metadata, directory));
            }
            catch { }
        }
        return result;
    }

    private static List<GrokAccountInfo> BuildList(List<StoredAccount> accounts)
    {
        var active = ResolveActiveId(accounts);
        return accounts.Select(x => new GrokAccountInfo
            {
                Id = x.Metadata.Id,
                Name = x.Metadata.Name,
                Email = x.Metadata.Email,
                Plan = x.Metadata.Plan,
                AuthMode = x.Metadata.AuthMode,
                IsCurrent = x.Metadata.Id == active,
                Usable = IsUsable(x),
                SavedAt = x.Metadata.SavedAt,
                UsagePercent = x.Metadata.UsagePercent,
                UsagePeriod = x.Metadata.UsagePeriod,
                UsageResetsAt = x.Metadata.UsageResetsAt,
                PrepaidBalanceCents = x.Metadata.PrepaidBalanceCents,
                UsageUpdatedAt = x.Metadata.UsageUpdatedAt,
            })
            .OrderByDescending(x => x.IsCurrent)
            .ThenByDescending(x => x.SavedAt)
            .ToList();
    }

    private static string? ResolveActiveId(List<StoredAccount> accounts)
    {
        var selected = ReadActive();
        if (selected is not null && accounts.Any(x => x.Metadata.Id == selected && IsUsable(x))) return selected;
        var fallback = accounts.FirstOrDefault(IsUsable)?.Metadata.Id;
        if (fallback != selected) WriteActive(fallback);
        return fallback;
    }

    private static bool IsUsable(StoredAccount account)
    {
        var identity = GrokAccountLoginService.InspectStoredAccount(AuthPathOf(account));
        return !string.IsNullOrWhiteSpace(identity.AccountId) && identity.AccountId == account.Metadata.Id;
    }

    private static string AuthPathOf(StoredAccount account) => account.Metadata.UsesLegacyAuth
        ? LegacyAuthPath
        : Path.Combine(account.DirectoryPath,
            string.IsNullOrWhiteSpace(account.Metadata.AuthFileName) ? "auth.json" : account.Metadata.AuthFileName);

    private static string? ReadActive()
    {
        try { return JsonSerializer.Deserialize<ActiveSelection>(File.ReadAllText(ActivePath))?.AccountId; }
        catch { return null; }
    }

    private static void WriteActive(string? id)
    {
        Directory.CreateDirectory(StoreRoot);
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
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void CopyFileAtomically(string source, string destination)
    {
        if (!File.Exists(source)) throw new InvalidOperationException("The completed Grok login did not create auth.json.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void DeleteAccountDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(StoreRoot).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refused to remove an account directory outside VibeCode's account store.");
        Directory.Delete(full, recursive: true);
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
