using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>Outcome of "Test account": whether the saved login authenticates and what state it's in.</summary>
public sealed class AccountTestResult
{
    public bool Ok { get; init; }              // did it authenticate at all
    public required string Verdict { get; init; }  // short headline, e.g. "Credentials invalid", "Valid — at usage limit"
    public string Detail { get; init; } = "";  // one-line explanation
    public string Raw { get; init; } = "";     // raw CLI output, for the debug box
}

public sealed class ClaudeLoginResult
{
    public bool Success { get; init; }
    public string? AccountId { get; init; }
    public string? Label { get; init; }
    public string? Error { get; init; }
}

/// <summary>One Claude Code login VibeCode knows about. Identity fields (email/name/org) come from the non-secret
/// <c>oauthAccount</c> block in ~/.claude.json. The profile's credentials supply only its subscription type for
/// presentation; token values are never exposed to account surfaces.</summary>
public sealed class AccountInfo : Observable
{
    public required string Id { get; init; }          // accountUuid - the profile key
    public string Email { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Org { get; init; } = "";
    /// <summary>Claude subscription tier from this profile's own credentials (pro / max / team / enterprise).</summary>
    public string Plan { get; init; } = "";
    public bool IsCurrent { get; set; }
    public bool IsSaved { get; set; }
    public bool Usable { get; set; } = true;   // false = the saved login is truncated/broken and needs a fresh /login
    public DateTime SavedAt { get; set; }
    public bool NeedsRelogin => !Usable;

    // ---- live subscription usage for this account (polled in the background via a headless `claude /usage`) ----
    private int? _sessionPct, _weekPct;
    private bool _atLimit;
    private string _usageDisplay = "";

    public int? SessionPercent { get => _sessionPct; private set => Set(ref _sessionPct, value); }
    public int? WeekPercent { get => _weekPct; private set => Set(ref _weekPct, value); }
    /// <summary>True when this account is maxed out (session or week at 100% / the CLI reported a limit).</summary>
    public bool AtLimit { get => _atLimit; private set => Set(ref _atLimit, value); }
    /// <summary>Compact "12% session · 46% week" (or "Signed out" / "checking…") shown on the account row.</summary>
    public string UsageDisplay { get => _usageDisplay; private set => Set(ref _usageDisplay, value); }
    public bool HasUsage => !string.IsNullOrEmpty(_usageDisplay);
    /// <summary>Placeholder shown while the first probe for this account is in flight (only if we have nothing yet).</summary>
    public void MarkUsageChecking() { if (!HasUsage) { UsageDisplay = "checking…"; Raise(nameof(HasUsage)); } }

    /// <summary>Push a freshly-probed usage snapshot onto this row.</summary>
    public void ApplyUsage(AccountUsage u)
    {
        SessionPercent = u.SessionPercent;
        WeekPercent = u.WeekPercent;
        AtLimit = u.AtLimit;
        UsageDisplay = FormatUsage(u);
        Raise(nameof(HasUsage));
    }

    private static string FormatUsage(AccountUsage u)
    {
        var parts = new List<string>();
        if (u.SessionPercent is { } s) parts.Add($"{s}% session");
        if (u.WeekPercent is { } w) parts.Add($"{w}% week");
        if (parts.Count == 0) return u.Error ?? (u.Authenticated ? "usage unavailable" : "");
        return string.Join(" · ", parts) + (u.AtLimit ? " · at limit" : "");
    }

    // Account surfaces use identity only for the primary name. Never fall back to an email (or an email-shaped
    // display name): the secondary line is reserved for provider + plan, and generic aliases remain screen-safe.
    public string Label => !string.IsNullOrWhiteSpace(DisplayName) && !LooksLikeEmail(DisplayName)
        ? DisplayName.Trim()
        : $"Claude account · {StableSuffix(Id)}";
    public string PlanDisplay => FormatPlan(Plan);
    public string ProviderLine => NeedsRelogin
        ? "Claude Code · Sign in again"
        : $"Claude Code · {PlanDisplay}";
    public string Initial => Label.Length > 0 ? Label[..1].ToUpperInvariant() : "?";
    /// <summary>The single checkmark in the unified AI-account list belongs to Claude only when Claude is selected.</summary>
    public bool IsVibeCodeSelected => IsCurrent
                                      && string.Equals(AppSettings.Current.DefaultProvider, "claude", StringComparison.OrdinalIgnoreCase);
    public void RefreshProviderSelection() => Raise(nameof(IsVibeCodeSelected));

    private static string FormatPlan(string? plan) => plan?.Trim().ToLowerInvariant() switch
    {
        "pro" => "Pro",
        "max" => "Max",
        "team" => "Team",
        "enterprise" => "Enterprise",
        "free" => "Free",
        { Length: > 0 } value => char.ToUpperInvariant(value[0]) + value[1..],
        _ => "Subscription",
    };

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

/// <summary>One account's parsed subscription usage from a headless <c>claude -p /usage</c> run.</summary>
public sealed class AccountUsage
{
    public int? SessionPercent { get; set; }
    public int? WeekPercent { get; set; }
    public string? SessionReset { get; set; }
    public bool AtLimit { get; set; }
    public bool Authenticated { get; set; }   // did the probe actually authenticate & yield usage
    public string? Error { get; set; }         // short reason when it didn't (e.g. "Signed out")
    public DateTime FetchedAt { get; set; }

    public bool HasNumbers => SessionPercent is not null || WeekPercent is not null;

    /// <summary>Interpret a <c>claude -p /usage --output-format json</c> run (same text the "Test account" probe reads).</summary>
    public static AccountUsage FromProbe(int exit, string stdout, string stderr)
    {
        var result = stdout;
        try { if (JsonNode.Parse(stdout) is { } n) result = n["result"]?.GetValue<string>() ?? stdout; }
        catch { /* not JSON - use the raw text */ }
        var hay = (result + "\n" + stderr).ToLowerInvariant();
        var u = new AccountUsage { FetchedAt = DateTime.Now };

        // Auth failure — the login was rejected / needs a fresh sign-in.
        if (hay.Contains("not logged in") || hay.Contains("/login") || hay.Contains("please log in") || hay.Contains("please sign in")
            || hay.Contains("unauthor") || hay.Contains("invalid_grant") || hay.Contains("401") || hay.Contains("authentication_error"))
        {
            u.Error = "Signed out";
            return u;
        }

        // Same buckets the status pill parses: "Current session: N% used · resets …" / "Current week (all models): N% used …".
        foreach (Match m in Regex.Matches(result,
                     @"Current (?:session|week \((?<bucket>[^)]+)\)):\s*(?<pct>\d+)% used(?:[^\n]*?resets\s*(?<reset>[^\n]+))?",
                     RegexOptions.IgnoreCase))
        {
            var bucket = m.Groups["bucket"].Value.Trim();
            var pct = int.TryParse(m.Groups["pct"].Value, out var v) ? Math.Clamp(v, 0, 100) : 0;
            var reset = m.Groups["reset"].Success ? m.Groups["reset"].Value.Trim() : null;
            if (bucket.Length == 0) { u.SessionPercent = pct; u.SessionReset = reset; }
            else if (string.Equals(bucket, "all models", StringComparison.OrdinalIgnoreCase) || u.WeekPercent is null) u.WeekPercent = pct;
        }

        u.AtLimit = u.SessionPercent >= 100 || u.WeekPercent >= 100
            || hay.Contains("limit reached") || hay.Contains("usage limit") || hay.Contains("rate limit");
        u.Authenticated = u.HasNumbers || u.AtLimit;
        // An unauthenticated `/usage` doesn't say so: it exits 0 and prints the generic run summary
        // ("Total cost: $0.00 …") with no buckets and no error word, which used to leave the row blank.
        if (!u.Authenticated && u.Error is null)
            u.Error = Regex.IsMatch(result, @"^\s*Total cost:", RegexOptions.Multiline) ? "Signed out"
                : exit == 0 ? null : "Couldn't read usage";
        return u;
    }
}

/// <summary>Result of a switch attempt.</summary>
public enum SwitchOutcome
{
    Ok,             // credentials swapped in
    NeedsRelogin,   // the target profile's saved login is truncated/broken - don't switch (would log you out)
    Missing,        // no such saved profile
    SaveFailed,     // the selection couldn't be written to disk - it won't survive a restart, so say so
    IsActive,       // refused: that profile is the one new chats currently run under - NOTHING was deleted
}

/// <summary>
/// Manages multiple Claude Code accounts by snapshotting/swapping the CLI's on-disk login. "Switch account but stay
/// logged in to the old one" works because switching first snapshots the account you're leaving (its refresh token
/// is preserved in the profile store), then swaps the target's credentials in - so switching back needs no re-login.
/// New <c>claude</c> processes pick up the swapped login at startup; already-running sessions keep their account.
/// </summary>
public sealed class AccountService
{
    public static AccountService Instance { get; } = new();

    // Last-known per-account usage (keyed by accountUuid). Survives RefreshAccounts (which rebuilds AccountInfo objects)
    // so freshly-built rows show cached numbers immediately, and change detection always has a previous value to diff.
    private readonly ConcurrentDictionary<string, AccountUsage> _usageCache = new();

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string SharedConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim('"'))
            : Path.Combine(Home, ".claude");
    private static string CredPath => Path.Combine(SharedConfigDir, ".credentials.json");
    private static string ConfigPath =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 }
            ? Path.Combine(SharedConfigDir, ".claude.json")
            : Path.Combine(Home, ".claude.json");
    private static string StoreDir =>
        Environment.GetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim('"'))
            : Path.Combine(Home, ".claude", "vibecode-accounts");

    private static string ProfileRoot(string id) => Path.Combine(StoreDir, id);
    private static string ProfileDescriptorPath(string id) => Path.Combine(ProfileRoot(id), "profile.json");
    private static string PrivateConfigDir(string id) => Path.Combine(ProfileRoot(id), "claude-home");

    private static bool IsSharedProfile(string id)
    {
        try
        {
            return string.Equals(JsonNode.Parse(File.ReadAllText(ProfileDescriptorPath(id)))?["kind"]?.GetValue<string>(),
                "shared", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ResolveConfigDirectory(string id, bool migrateLegacy = true)
    {
        if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id)
            throw new ArgumentException("Invalid Claude account id.", nameof(id));
        if (IsSharedProfile(id)) return SharedConfigDir;

        var target = PrivateConfigDir(id);
        if (!migrateLegacy) return target;
        var root = ProfileRoot(id);
        var legacyCredentials = Path.Combine(root, "credentials.json");
        var targetCredentials = Path.Combine(target, ".credentials.json");
        if (!File.Exists(targetCredentials) && File.Exists(legacyCredentials))
        {
            Directory.CreateDirectory(target);
            AtomicCopy(legacyCredentials, targetCredentials);
            var config = new JsonObject
            {
                ["hasCompletedOnboarding"] = true,
                ["mcpServers"] = new JsonObject(),
            };
            try
            {
                if (JsonNode.Parse(File.ReadAllText(Path.Combine(root, "account.json"))) is JsonObject metadata)
                {
                    if (metadata["oauthAccount"] is { } account) config["oauthAccount"] = account.DeepClone();
                    if (metadata["userID"] is { } userId) config["userID"] = userId.DeepClone();
                }
            }
            catch { }
            AtomicWrite(Path.Combine(target, ".claude.json"), config.ToJsonString());
            AtomicWrite(ProfileDescriptorPath(id), new JsonObject { ["kind"] = "private" }.ToJsonString());
        }
        return target;
    }

    private static string CredentialPath(string id) =>
        Path.Combine(ResolveConfigDirectory(id), ".credentials.json");

    /// <summary>The persistent native Claude home for this account. Claude Code reads and refreshes credentials here.
    /// No browser cookies or copied access-token environment variables are involved.</summary>
    public string? ConfigDirectory(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var directory = ResolveConfigDirectory(id);
            return LooksLikeRealLogin(Path.Combine(directory, ".credentials.json")) ? directory : null;
        }
        catch { return null; }
    }

    /// <summary>The account currently active in ~/.claude.json, or null if not logged in with a subscription.</summary>
    public AccountInfo? Current()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var oa = JsonNode.Parse(File.ReadAllText(ConfigPath))?["oauthAccount"];
            var id = oa?["accountUuid"]?.GetValue<string>();
            if (oa is null || string.IsNullOrEmpty(id)) return null;
            return FromOAuth(id, oa, CredPath);
        }
        catch { return null; }
    }

    /// <summary>Every account VibeCode can switch to (saved profiles + the live one), current first.</summary>
    public List<AccountInfo> List()
    {
        var current = Current();
        var byId = new Dictionary<string, AccountInfo>();
        try
        {
            if (Directory.Exists(StoreDir))
                foreach (var dir in Directory.GetDirectories(StoreDir))
                {
                    var meta = Path.Combine(dir, "account.json");
                    if (!File.Exists(meta)) continue;
                    try
                    {
                        var oa = JsonNode.Parse(File.ReadAllText(meta))?["oauthAccount"];
                        var id = Path.GetFileName(dir);
                        var cred = CredentialPath(id);
                        if (!File.Exists(cred)) continue;
                        var info = oa is not null
                            ? FromOAuth(id, oa, cred)
                            : new AccountInfo { Id = id, Plan = ReadSubscriptionType(cred) };
                        info.IsSaved = true;
                        info.Usable = LooksLikeRealLogin(cred);
                        info.SavedAt = File.GetLastWriteTime(meta);
                        byId[id] = info;
                    }
                    catch { /* skip a corrupt profile */ }
                }
        }
        catch { /* store unreadable */ }

        // The live ~/.claude login may not be a saved profile yet (e.g. right after `/login`) - show it anyway.
        if (current is not null && !byId.ContainsKey(current.Id)) { current.Usable = LooksLikeRealLogin(CredPath); byId[current.Id] = current; }
        // "Current" now means the ACTIVE account (which one new chats run under), not whatever's in the live file -
        // switching no longer swaps the live file, so the active account is a VibeCode selection.
        var activeId = ActiveId;
        foreach (var a in byId.Values) a.IsCurrent = a.Id == activeId;
        var ordered = byId.Values.OrderByDescending(a => a.IsCurrent).ThenByDescending(a => a.SavedAt).ToList();
        foreach (var a in ordered) if (_usageCache.TryGetValue(a.Id, out var u)) a.ApplyUsage(u);   // last-known usage on rebuild
        return ordered;
    }

    /// <summary>Last-known usage for an account, or null if it hasn't been probed yet.</summary>
    public AccountUsage? CachedUsage(string id) => _usageCache.TryGetValue(id, out var u) ? u : null;

    /// <summary>Probe one account's <c>/usage</c> headlessly (isolated from the live login), update the cache, and return
    /// the fresh snapshot.
    /// Non-invasive - runs against a throwaway copy of the account's creds, never the shared <c>~/.claude</c> login.</summary>
    public async Task<AccountUsage?> RefreshUsageAsync(string id)
    {
        string? tmp = null;
        try
        {
            tmp = ConfigDirectory(id);
            AccountUsage fresh;
            if (tmp is null)
                fresh = new AccountUsage { FetchedAt = DateTime.Now, Error = "Signed out" };   // no usable saved login
            else
            {
                var (exit, so, se) = await RunUsageProbeAsync(tmp);
                fresh = AccountUsage.FromProbe(exit, so, se);
            }

            _usageCache[id] = fresh;
            return fresh;
        }
        catch { return null; }
        finally
        {
            // CRITICAL: OAuth refresh tokens ROTATE. If the CLI refreshed the creds inside the throwaway dir, the
            // store's refresh token is now INVALID — deleting the temp without copying back silently kills the saved
            // login (the "need to log in again" corruption). Always persist a fresher credential file back first.
            if (tmp is not null && Path.GetFileName(tmp).StartsWith("vibecode-usage-", StringComparison.Ordinal))
            {
                PersistRefreshedCreds(tmp, id);
                try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup best-effort */ }
            }
        }
    }

    /// <summary>Run <c>/usage</c> AS a specific saved account and hand back the CLI's raw report text (null when the
    /// account has no usable saved login).
    /// Deliberately goes through an isolated CLAUDE_CONFIG_DIR - a real login - because <c>/usage</c> reports NOTHING
    /// under bare CLAUDE_CODE_OAUTH_TOKEN auth: it falls back to the "Total cost: $0.00" run summary, with zero
    /// buckets to parse. Verified on one account, one machine: env-token probe = no usage, config-dir probe = full
    /// usage. Rotation-safe - a credential file the CLI refreshed inside the temp dir is persisted back first.</summary>
    public async Task<string?> ProbeUsageTextAsync(string id)
    {
        string? tmp = null;
        try
        {
            tmp = ConfigDirectory(id);
            if (tmp is null) return null;
            var (_, stdout, _) = await RunUsageProbeAsync(tmp);
            try { return JsonNode.Parse(stdout)?["result"]?.GetValue<string>() ?? stdout; }
            catch { return stdout; }   // not JSON - the raw text still parses fine downstream
        }
        catch { return null; }
        finally
        {
            if (tmp is not null && Path.GetFileName(tmp).StartsWith("vibecode-usage-", StringComparison.Ordinal))
            {
                PersistRefreshedCreds(tmp, id);
                try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup best-effort */ }
            }
        }
    }

    /// <summary>Copy a probe dir's <c>.credentials.json</c> back into the store when it is STRICTLY fresher than the
    /// stored one. The CLI rotates refresh tokens on every refresh, so a refreshed pair left behind in a temp dir means
    /// the stored pair is already dead — losing it is how saved logins kept "corrupting". Never copies a stub or a
    /// stale/equal file back, so a failed probe can't damage the store either.</summary>
    private void PersistRefreshedCreds(string tmpDir, string id)
    {
        try
        {
            var refreshed = Path.Combine(tmpDir, ".credentials.json");
            var stored = CredentialPath(id);
            if (!File.Exists(refreshed) || !LooksLikeRealLogin(refreshed) || FilesEqual(refreshed, stored)) return;
            var newTtl = TokenTtl(refreshed);
            var oldTtl = TokenTtl(stored);
            if (newTtl is null) return;                       // no expiry info = can't prove it's fresher
            if (oldTtl is { } o && newTtl <= o) return;       // not fresher - keep what we have
            AtomicCopy(refreshed, stored);
        }
        catch { /* best-effort - worst case the store keeps its current file */ }
    }

    /// <summary>
    /// Keep a stored account and the shared ~/.claude login on the SAME token generation. OAuth refresh tokens are
    /// single-use: whichever copy refreshes last invalidates the other copy's refresh token, so two diverged copies
    /// of one account's login fight forever — each side's refresh kills the other (this was the "accounts keep
    /// expiring" bug: a probe refresh murdered the user's normal CLI login, and any normal CLI use murdered the
    /// stored login). Converge them instead: the file with the later expiresAt holds the live generation, so copy
    /// it over the staler one — in whichever direction that is. Does nothing when the shared login is a DIFFERENT
    /// account (stored-only accounts have no shared copy to fight) and never lets a broken file overwrite a real one.
    /// </summary>
    private void SyncWithSharedLogin(string id)
    {
        try
        {
            if (Current()?.Id != id) return;                    // the shared login is another account (or signed out)
            if (!LooksLikeRealLogin(CredPath)) return;          // a broken shared file proves nothing
            var stored = CredentialPath(id);
            if (!File.Exists(stored))
            {
                Directory.CreateDirectory(Path.Combine(StoreDir, id));
                AtomicCopy(CredPath, stored);                   // no local copy yet - adopt the shared login
                return;
            }
            if (FilesEqual(CredPath, stored)) return;           // already converged
            var sharedTtl = TokenTtl(CredPath);
            var storedTtl = TokenTtl(stored);
            if (sharedTtl is null || storedTtl is null) return; // can't prove which generation is live - don't guess
            if (sharedTtl > storedTtl)
                AtomicCopy(CredPath, stored);                   // the user's CLI refreshed first - adopt its fresh pair
            else if (storedTtl > sharedTtl)
                AtomicCopy(stored, CredPath);                   // we refreshed first - heal the shared login before it dies
        }
        catch { /* best-effort convergence - a missed sync just leaves more for the next one to fix */ }
    }

    /// <summary>True when this account's stored access token is expired or will expire within <paramref name="margin"/>.
    /// (Also true when there is no stored token at all.)</summary>
    public bool TokenExpiring(string? id, TimeSpan margin)
    {
        if (string.IsNullOrEmpty(id)) return false;   // no per-account auth = shared login, nothing to refresh
        var cred = CredentialPath(id);
        if (!File.Exists(cred)) return false;
        return TokenTtl(cred) is not { } ttl || ttl < margin;
    }

    /// <summary>Build a throwaway CLAUDE_CONFIG_DIR containing ONLY this account's creds + a minimal identity config, so a
    /// headless `claude` authenticates as this account without touching the live login. Null if it has no usable login.
    /// Caller deletes the returned dir.</summary>
    private string? CreateIsolatedConfigDir(string id)
    {
        var dir = ProfileRoot(id);
        var cred = CredentialPath(id);
        if (!LooksLikeRealLogin(cred)) return null;
        var tmp = Path.Combine(Path.GetTempPath(), "vibecode-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.Copy(cred, Path.Combine(tmp, ".credentials.json"), overwrite: true);
        var cfg = new JsonObject { ["hasCompletedOnboarding"] = true, ["mcpServers"] = new JsonObject() };
        try { if (JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "account.json"))) is JsonObject m) { if (m["oauthAccount"] is { } oa) cfg["oauthAccount"] = oa.DeepClone(); if (m["userID"] is { } u) cfg["userID"] = u.DeepClone(); } } catch { /* minimal config still probes fine */ }
        File.WriteAllText(Path.Combine(tmp, ".claude.json"), cfg.ToJsonString());
        return tmp;
    }

    /// <summary>Which saved account new chats run under (the VibeCode selection), falling back to the live login if the
    /// selection is unset or points at an account that no longer exists.</summary>
    public string? ActiveId => ResolveActive().Id;

    /// <summary>True when <see cref="ActiveId"/> came from the live ~/.claude login because no valid selection was
    /// stored - i.e. the shown account is a guess, not the user's choice. Surfaced as a hint in the account popup.
    /// Computed on demand: it used to be a side effect of the ActiveId getter, which background callers (usage polling)
    /// hit constantly, so the popup's hint reflected whoever touched ActiveId last rather than the UI's own question.</summary>
    public bool ActiveIsFallback => ResolveActive().IsFallback;

    // "this account because you picked it" and "this account because your pick evaporated" used to look identical
    // everywhere, which let a lost selection masquerade as correct state. Name the difference - in one pure place.
    private (string? Id, bool IsFallback) ResolveActive()
    {
        var sel = AppSettings.Current.ActiveAccountId;
        if (!string.IsNullOrEmpty(sel) && ProfileUsable(sel)) return (sel, false);
        return (Current()?.Id, true);
    }

    /// <summary>Raised when the saved-account set or the active selection changed, so already-open chats can re-resolve
    /// their "running as …" chip. Subscribers must unsubscribe when they're torn down (see ChatViewModel.Close).</summary>
    public static event Action? AccountsChanged;

    /// <summary>Announce that the account list/selection moved (called after a refresh or a switch).</summary>
    public static void NotifyAccountsChanged() => AccountsChanged?.Invoke();

    /// <summary>The active account's info (for the footer / current-account display).</summary>
    public AccountInfo? Active() => List().FirstOrDefault(a => a.IsCurrent);

    /// <summary>This account's OAuth access token, read from its saved profile. New chats pass it via
    /// CLAUDE_CODE_OAUTH_TOKEN so they authenticate as this account WITHOUT touching the shared credentials file.
    /// Returns null if unavailable (caller then spawns with no override = the CLI's normal shared-file behavior).</summary>
    public string? AccessToken(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var p = CredentialPath(id);
            if (!File.Exists(p)) return null;
            var tok = JsonNode.Parse(File.ReadAllText(p))?["claudeAiOauth"]?["accessToken"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(tok) ? null : tok;
        }
        catch { return null; }
    }

    /// <summary>Select which account new chats use. NO file swap - just a VibeCode preference - so it can never corrupt a
    /// running chat's login (each chat already captured its own token). Refuses a broken/stub profile.</summary>
    public SwitchOutcome SelectAccount(string id)
    {
        var cred = CredentialPath(id);
        if (!File.Exists(cred)) return SwitchOutcome.Missing;
        if (!LooksLikeRealLogin(cred)) return SwitchOutcome.NeedsRelogin;
        // Never report a switch we didn't verify reached disk - "Switched to X." followed by X being gone next launch
        // is exactly the silent revert this whole path exists to prevent.
        var err = AppSettings.Current.SetActiveAccount(id);
        if (err is not null)
        {
            // Memory must not keep a selection disk refused: otherwise the dialog says "couldn't save" while the app
            // already behaves as the new account, and the next ordinary Save() from anywhere persists it regardless.
            // Snap back to whatever disk actually holds, so memory and disk agree either way.
            AppSettings.Current.ActiveAccountId = AppSettings.Current.LastPersistedActiveAccountId;
            LastSelectError = err.Message;
            return SwitchOutcome.SaveFailed;
        }
        LastSelectError = null;
        return SwitchOutcome.Ok;
    }

    /// <summary>Why the last <see cref="SelectAccount"/> returned <see cref="SwitchOutcome.SaveFailed"/>, for the UI to show.</summary>
    public string? LastSelectError { get; private set; }

    /// <summary>Does this credentials file look like a real, usable login? Schema-agnostic: it must parse as JSON and
    /// carry a long token-like string (real OAuth tokens are 80+ chars). Only string LENGTHS are inspected here - never
    /// the values - so the tokens stay opaque. This is what stops a truncated/logged-out stub (e.g. a 238-byte file
    /// with no real token) from being snapshotted or switched into and silently logging the user out.</summary>
    public static bool LooksLikeRealLogin(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 120) return false;   // too small to hold a real token
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node is not null && HasLongString(node);
        }
        catch { return false; }
    }

    private static bool HasLongString(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) if (kv.Value is not null && HasLongString(kv.Value)) return true;
                return false;
            case JsonArray a:
                foreach (var v in a) if (v is not null && HasLongString(v)) return true;
                return false;
            case JsonValue val:
                return val.TryGetValue<string>(out var s) && s is { Length: >= 80 };   // length only, never the value
            default:
                return false;
        }
    }

    /// <summary>Is this saved profile's login usable (not a broken stub)? Used to grey-out / warn before switching.</summary>
    public bool ProfileUsable(string id)
    {
        try { return LooksLikeRealLogin(CredentialPath(id)); }
        catch { return false; }
    }

    /// <summary>Run Claude Code's own OAuth login inside a brand-new persistent account home. The CLI stores the
    /// refreshable credential grant directly in that home; VibeCode never reads browser cookies and never snapshots a
    /// short-lived access token.</summary>
    public async Task<ClaudeLoginResult> LoginNewAccountAsync(CancellationToken cancellationToken = default)
    {
        var pendingRoot = Path.Combine(StoreDir, ".pending-" + Guid.NewGuid().ToString("N"));
        var pendingHome = Path.Combine(pendingRoot, "claude-home");
        try
        {
            Directory.CreateDirectory(pendingHome);
            var psi = new ProcessStartInfo
            {
                FileName = Protocol.ClaudeSession.ResolveCliPath(),
                WorkingDirectory = pendingHome,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--claudeai");
            Protocol.ClaudeSession.StripInheritedEnv(psi);
            psi.Environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
            psi.Environment["CLAUDE_CONFIG_DIR"] = pendingHome;

            using var process = Process.Start(psi);
            if (process is null) return new ClaudeLoginResult { Error = "Could not start Claude Code login." };
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return new ClaudeLoginResult { Error = "Claude sign-in was cancelled." };
            }
            if (process.ExitCode != 0)
                return new ClaudeLoginResult { Error = $"Claude Code login exited with code {process.ExitCode}." };

            var status = await ReadAuthStatusAsync(pendingHome, cancellationToken).ConfigureAwait(false);
            if (!status.LoggedIn)
                return new ClaudeLoginResult { Error = status.Error ?? "Claude Code did not save a usable login." };
            var credentials = Path.Combine(pendingHome, ".credentials.json");
            var configPath = Path.Combine(pendingHome, ".claude.json");
            if (!LooksLikeRealLogin(credentials) || !File.Exists(configPath))
                return new ClaudeLoginResult { Error = "Claude Code finished, but its native credential home is incomplete." };

            var config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            var oauth = config?["oauthAccount"] as JsonObject;
            var id = oauth?["accountUuid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || Path.GetFileName(id) != id)
                return new ClaudeLoginResult { Error = "Claude Code saved the login but did not report its account identity." };

            var profileRoot = ProfileRoot(id);
            Directory.CreateDirectory(profileRoot);
            var targetHome = PrivateConfigDir(id);
            var incoming = targetHome + ".incoming-" + Guid.NewGuid().ToString("N");
            var backup = targetHome + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(pendingHome, incoming);
            var movedOld = false;
            try
            {
                if (Directory.Exists(targetHome))
                {
                    Directory.Move(targetHome, backup);
                    movedOld = true;
                }
                Directory.Move(incoming, targetHome);
                var metadata = new JsonObject
                {
                    ["oauthAccount"] = oauth!.DeepClone(),
                    ["userID"] = config?["userID"]?.DeepClone(),
                };
                AtomicWrite(Path.Combine(profileRoot, "account.json"),
                    metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                AtomicWrite(ProfileDescriptorPath(id), new JsonObject { ["kind"] = "private" }.ToJsonString());
                if (movedOld && Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            }
            catch
            {
                try { if (Directory.Exists(targetHome)) Directory.Delete(targetHome, recursive: true); } catch { }
                try { if (movedOld && Directory.Exists(backup)) Directory.Move(backup, targetHome); } catch { }
                throw;
            }

            var label = oauth["displayName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(label)) label = "Claude account";
            return new ClaudeLoginResult { Success = true, AccountId = id, Label = label };
        }
        catch (Exception ex)
        {
            return new ClaudeLoginResult { Error = ex.Message };
        }
        finally
        {
            try { if (Directory.Exists(pendingRoot)) Directory.Delete(pendingRoot, recursive: true); } catch { }
        }
    }

    private static async Task<(bool LoggedIn, string? Error)> ReadAuthStatusAsync(
        string configDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Protocol.ClaudeSession.ResolveCliPath(),
            WorkingDirectory = configDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        Protocol.ClaudeSession.StripInheritedEnv(psi);
        psi.Environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        psi.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        using var process = Process.Start(psi);
        if (process is null) return (false, "Could not start Claude Code authentication check.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        try
        {
            var loggedIn = JsonNode.Parse(output)?["loggedIn"]?.GetValue<bool>() == true;
            return loggedIn
                ? (true, null)
                : (false, "Claude Code reports that the account is signed out.");
        }
        catch
        {
            var detail = (await stderr.ConfigureAwait(false)).Trim();
            return (false, detail.Length > 0 ? detail : "Claude Code returned an unreadable authentication status.");
        }
    }

    /// <summary>Snapshot the live account (opaque credentials + identity) into the profile store. Non-destructive.
    /// Refuses to capture a broken/stub live login - that's exactly what corrupted a saved account before.</summary>
    public AccountInfo? SaveCurrent()
    {
        try
        {
            var cur = Current();
            if (cur is null || !File.Exists(CredPath)) return null;
            if (!LooksLikeRealLogin(CredPath)) return null;   // never overwrite a saved profile with a broken live file
            var dir = ProfileRoot(cur.Id);
            Directory.CreateDirectory(dir);
            // The account already lives in Claude Code's normal home. Reference that native home instead of copying
            // its rotating refresh token into a second file that will eventually invalidate the first one.
            if (!File.Exists(ProfileDescriptorPath(cur.Id)))
                AtomicWrite(ProfileDescriptorPath(cur.Id), new JsonObject { ["kind"] = "shared" }.ToJsonString());
            var root = JsonNode.Parse(File.ReadAllText(ConfigPath));
            var meta = new JsonObject
            {
                ["oauthAccount"] = root?["oauthAccount"]?.DeepClone(),
                ["userID"] = root?["userID"]?.DeepClone(),
            };
            AtomicWrite(Path.Combine(dir, "account.json"), meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            cur.IsSaved = true;
            return cur;
        }
        catch { return null; }
    }

    // NOTE: the old file-swapping SwitchTo() was removed. Switching accounts now goes through SelectAccount(), which
    // only changes which account NEW chats use and NEVER writes ~/.claude/.credentials.json - so it can't corrupt a
    // running session. Don't reintroduce a shared-file swap here.

    /// <summary>Remove a saved profile (never touches the live ~/.claude login). If this was the main/selected
    /// account, re-points new chats at another usable saved profile, or clears the selection so the app falls back
    /// to nothing / the live CLI login. Returns <see cref="SwitchOutcome.SaveFailed"/> (reason in
    /// <see cref="LastSelectError"/>) if the selection couldn't be updated - in that case NOTHING is deleted.</summary>
    public SwitchOutcome Forget(string id)
    {
        // Re-point selection BEFORE deleting when this is the active/main account. Deleting first and clearing after
        // meant a refused save left memory AND disk naming a profile that no longer exists.
        var isMain = AppSettings.Current.ActiveAccountId == id || ActiveId == id;
        if (isMain)
        {
            var next = FindNextUsableProfileId(exceptId: id);
            // Deliberate selection change (null = no preferred account / fall back to live CLI if present).
            if (AppSettings.Current.SetActiveAccount(next) is { } err)
            {
                AppSettings.Current.ActiveAccountId = AppSettings.Current.LastPersistedActiveAccountId;
                LastSelectError = err.Message;
                return SwitchOutcome.SaveFailed;
            }
        }
        try { var d = Path.Combine(StoreDir, id); if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
        catch { /* best-effort */ }
        LastSelectError = null;
        return SwitchOutcome.Ok;
    }

    /// <summary>Next saved Claude profile to prefer after removing <paramref name="exceptId"/>, or null if none.</summary>
    private string? FindNextUsableProfileId(string exceptId)
    {
        try
        {
            if (!Directory.Exists(StoreDir)) return null;
            foreach (var dir in Directory.EnumerateDirectories(StoreDir)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var otherId = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(otherId) || otherId == exceptId) continue;
                if (ProfileUsable(otherId)) return otherId;
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>Test a saved account's login WITHOUT touching the live one: build a throwaway CLAUDE_CONFIG_DIR that
    /// contains only this account's credentials, run `claude -p /usage` against it, and interpret the result
    /// (invalid / valid / valid-but-at-usage-limit). Returns a debug-friendly verdict + the raw CLI output.</summary>
    public async Task<AccountTestResult> TestAccountAsync(string id)
    {
        string? tempDir = null;
        try
        {
            var cred = CredentialPath(id);
            if (!File.Exists(cred))
                return new AccountTestResult { Ok = false, Verdict = "No saved login", Detail = "VibeCode has no stored credentials for this account." };
            if (!LooksLikeRealLogin(cred))
                return new AccountTestResult { Ok = false, Verdict = "Credentials invalid", Detail = "The saved credentials are empty/truncated (logged out). Re-add this account with Add another account." };

            tempDir = ConfigDirectory(id);
            if (tempDir is null)
                return new AccountTestResult { Ok = false, Verdict = "No saved login", Detail = "VibeCode has no native Claude login for this account." };

            var (exit, stdout, stderr) = await RunUsageProbeAsync(tempDir);
            return Interpret(exit, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new AccountTestResult { Ok = false, Verdict = "Test failed", Detail = ex.Message };
        }
        finally
        {
            // Token rotation: if the CLI refreshed the creds during the test, the store's pair is already dead - copy
            // the fresher file back or "Test account" REPORTS success while silently killing the saved login.
            if (tempDir is not null && Path.GetFileName(tempDir).StartsWith("vibecode-accttest-", StringComparison.Ordinal))
            {
                PersistRefreshedCreds(tempDir, id);
                try { Directory.Delete(tempDir, recursive: true); } catch { /* temp cleanup best-effort */ }
            }
        }
    }

    private static async Task<(int exit, string stdout, string stderr)> RunUsageProbeAsync(string configDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Protocol.ClaudeSession.ResolveCliPath(),
            WorkingDirectory = configDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,    // closed immediately below - see the stdin note after Start
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("/usage");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        Protocol.ClaudeSession.StripInheritedEnv(psi);
        psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;   // use ONLY this account's creds - never the live login

        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "", "Couldn't start the claude CLI.");
        // Close stdin at once. With an open-but-silent stdin the CLI waits ~3s ("no stdin data received in 3s") on
        // EVERY probe before it will answer - pure latency on a call the usage panel blocks on.
        try { proc.StandardInput.Close(); } catch { /* already gone */ }
        var so = proc.StandardOutput.ReadToEndAsync();
        var se = proc.StandardError.ReadToEndAsync();
        // Async wait (never the blocking WaitForExit) so callers on the UI thread don't freeze while claude runs.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { /* gone */ } }
        return (proc.HasExited ? proc.ExitCode : -1, (await so).Trim(), (await se).Trim());
    }

    private static AccountTestResult Interpret(int exit, string stdout, string stderr)
    {
        string result = stdout;
        try { if (JsonNode.Parse(stdout) is { } n) result = n["result"]?.GetValue<string>() ?? stdout; }
        catch { /* not JSON - use the raw text */ }

        var raw = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n[stderr]\n{stderr}";
        var hay = (result + "\n" + stderr).ToLowerInvariant();

        // Auth failure — the CLI asks you to log in / rejects the token.
        if (hay.Contains("not logged in") || hay.Contains("/login") || hay.Contains("please log in") || hay.Contains("please sign in")
            || hay.Contains("unauthor") || hay.Contains("invalid_grant") || hay.Contains("401") || hay.Contains("authentication_error"))
            return new AccountTestResult { Ok = false, Verdict = "Credentials invalid", Detail = "Claude rejected this login — the account needs to sign in again (re-add it).", Raw = raw };

        // Usage percentages present => it authenticated. Detect "maxed out".
        var pcts = Regex.Matches(result, @"Current (?:session|week[^:]*):\s*(\d+)% used", RegexOptions.IgnoreCase)
            .Select(m => int.TryParse(m.Groups[1].Value, out var v) ? v : 0).ToList();
        var limitLang = hay.Contains("limit reached") || hay.Contains("usage limit") || hay.Contains("rate limit")
            || hay.Contains("resets") && hay.Contains("100% used");
        if (pcts.Count > 0)
        {
            var session = Regex.Match(result, @"Current session:\s*(\d+)%", RegexOptions.IgnoreCase);
            var week = Regex.Match(result, @"Current week[^:]*:\s*(\d+)%", RegexOptions.IgnoreCase);
            var parts = new List<string>();
            if (session.Success) parts.Add($"{session.Groups[1].Value}% session");
            if (week.Success) parts.Add($"{week.Groups[1].Value}% week");
            var summary = parts.Count > 0 ? string.Join(" · ", parts) : $"max {pcts.Max()}%";
            if (pcts.Max() >= 100 || limitLang)
                return new AccountTestResult { Ok = true, Verdict = "Valid — at usage limit", Detail = $"Login works, but this account is maxed out ({summary}). It'll work again after the reset.", Raw = raw };
            return new AccountTestResult { Ok = true, Verdict = "Valid & working", Detail = $"Login works. Usage: {summary}.", Raw = raw };
        }

        // No percentages but the CLI mentioned limits => valid but capped.
        if (limitLang)
            return new AccountTestResult { Ok = true, Verdict = "Valid — at usage limit", Detail = "Login works but the account is at its usage limit.", Raw = raw };

        if (exit != 0)
            return new AccountTestResult { Ok = false, Verdict = "Test failed", Detail = $"claude exited with code {exit}. See raw output.", Raw = raw };

        return new AccountTestResult { Ok = true, Verdict = "Inconclusive", Detail = "The CLI ran but the output didn't match a known pattern — see raw output.", Raw = raw };
    }

    /// <summary>Keep every saved account's OAuth access token fresh. Chats authenticate with the STORED token (env
    /// var), which the CLI can't refresh on its own, so we periodically let the CLI refresh a near-expiry token in an
    /// isolated dir and copy the new token back. Best-effort - a still-valid token is left untouched.</summary>
    public async Task RefreshAllTokensAsync()
    {
        if (!Directory.Exists(StoreDir)) return;
        string[] dirs;
        try { dirs = Directory.GetDirectories(StoreDir); } catch { return; }
        foreach (var dir in dirs) await RefreshStoredTokenAsync(Path.GetFileName(dir));
    }

    public async Task RefreshStoredTokenAsync(string id)
    {
        try
        {
            var config = ConfigDirectory(id);
            if (config is null) return;
            var cred = Path.Combine(config, ".credentials.json");
            if (!LooksLikeRealLogin(cred)) return;
            if (TokenTtl(cred) is { } ttl && ttl > TimeSpan.FromHours(2)) return;   // still fresh - don't spawn for nothing
            await RunUsageProbeAsync(config); // Claude Code refreshes and persists OAuth in this same native home.
        }
        catch { /* best-effort; the stored token keeps working until it actually expires */ }
    }

    /// <summary>Remaining lifetime of a credentials file's OAuth access token (from <c>expiresAt</c>), or null.</summary>
    private static TimeSpan? TokenTtl(string credPath)
    {
        try
        {
            var exp = JsonNode.Parse(File.ReadAllText(credPath))?["claudeAiOauth"]?["expiresAt"];
            if (exp is null) return null;
            long ms; try { ms = exp.GetValue<long>(); } catch { ms = (long)exp.GetValue<double>(); }
            return DateTimeOffset.FromUnixTimeMilliseconds(ms > 1_000_000_000_000 ? ms : ms * 1000) - DateTimeOffset.UtcNow;
        }
        catch { return null; }
    }

    private static bool FilesEqual(string a, string b)
    {
        try { return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)); } catch { return false; }
    }

    /// <summary>Read only the non-secret subscription tier used for presentation. Tokens remain internal and are never
    /// copied into an AccountInfo or exposed through bindings.</summary>
    private static string ReadSubscriptionType(string credentialPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(credentialPath))?["claudeAiOauth"]?["subscriptionType"]
                       ?.GetValue<string>()
                   ?? "";
        }
        catch { return ""; }
    }

    private static AccountInfo FromOAuth(string id, JsonNode oa, string credentialPath) => new()
    {
        Id = id,
        Email = oa["emailAddress"]?.GetValue<string>() ?? "",
        DisplayName = oa["displayName"]?.GetValue<string>() ?? "",
        Org = oa["organizationName"]?.GetValue<string>() ?? "",
        Plan = ReadSubscriptionType(credentialPath),
    };

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void AtomicCopy(string src, string dst)
    {
        var tmp = dst + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }
}
