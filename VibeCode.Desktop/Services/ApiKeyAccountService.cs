using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeCode.UI;   // Observable (INotifyPropertyChanged base), as used by the other account types

namespace VibeCode.Services;

/// <summary>
/// One "sign in with an API key" account. The key itself never lives on this object in plaintext -
/// <see cref="Secret"/> holds a DPAPI blob that only the current Windows user can unseal.
/// </summary>
public sealed class ApiKeyAccount : Observable
{
    public required string Id { get; init; }
    /// <summary>claude | codex | grok | kimi | glm - matches AppSettings.DefaultProvider.</summary>
    public required string Provider { get; init; }
    public string Label { get; set; } = "";
    /// <summary>Base64 DPAPI ciphertext. Never logged, never shown, never written in the clear.</summary>
    public string Secret { get; set; } = "";
    /// <summary>"sk-ant-…7T4a" - enough to recognise a key without revealing it.</summary>
    public string Masked { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string ProviderDisplay => ApiKeyAccountService.ProviderName(Provider);
    [JsonIgnore] public string Initial => ProviderDisplay.Length > 0 ? ProviderDisplay[..1] : "?";
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Label) ? $"{ProviderDisplay} API key" : Label;
    [JsonIgnore] public string Subtitle => $"{ProviderDisplay} · {Masked}";
    /// <summary>True when this key is the account VibeCode will actually use for its provider.</summary>
    [JsonIgnore]
    public bool IsSelected =>
        string.Equals(AppSettings.Current.DefaultProvider, Provider, StringComparison.OrdinalIgnoreCase) &&
        IsPreferred;

    /// <summary>
    /// True when this is the key chosen for its own provider, whether or not that provider is the active one.
    ///
    /// Distinct from <see cref="IsSelected"/> on purpose. That one answers "is this the credential the next chat
    /// will run on", which is false for every Grok key while Claude is the active provider - so a per-provider
    /// key list using it would show no key as chosen at all, and offer to switch to the one already in use.
    /// </summary>
    [JsonIgnore]
    public bool IsPreferred =>
        string.Equals(ApiKeyAccountService.Instance.SelectedId(Provider), Id, StringComparison.Ordinal);

    public void RefreshSelection() { Raise(nameof(IsSelected)); Raise(nameof(IsPreferred)); }
}

public sealed class ApiKeyValidation
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public static ApiKeyValidation Good(string m = "Key accepted") => new() { Ok = true, Message = m };
    public static ApiKeyValidation Bad(string m) => new() { Ok = false, Message = m };
}

/// <summary>
/// API-key sign-in for the four coding CLIs VibeCode drives.
///
/// Every one of them authenticates through an environment variable, which is why this fits the existing
/// design: a session already gets its account by having env pointed at a per-account credential home.
/// The delicate part is that a key SILENTLY OUTRANKS a subscription login in both the Claude and Codex
/// CLIs - a stray ANTHROPIC_API_KEY on this machine is what produced "402 API key budget exhausted"
/// (see ClaudeSession.StripInheritedEnv), and OpenAI has the same trap. So the rule here is strict:
/// the whole environment stays scrubbed as before, and a key is injected ONLY for a session whose
/// selected account for that provider is an API-key account. Subscription runs are never touched, and
/// picking a key account is therefore the only way to start spending per-token.
/// </summary>
public sealed class ApiKeyAccountService : Observable
{
    public static ApiKeyAccountService Instance { get; } = new();

    private readonly List<ApiKeyAccount> _accounts = new();
    private readonly Dictionary<string, string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibeCode.ApiKeyAccount.v1");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private ApiKeyAccountService() { Load(); }

    private static string FilePath => Path.Combine(AppSettings.Dir, "apikeys.json");

    public IReadOnlyList<ApiKeyAccount> Accounts => _accounts;
    public IEnumerable<ApiKeyAccount> For(string provider) =>
        _accounts.Where(a => string.Equals(a.Provider, provider, StringComparison.OrdinalIgnoreCase));

    public static string ProviderName(string provider) => provider?.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "codex" => "ChatGPT",
        "grok" => "Grok",
        "kimi" => "Kimi",
        VibeCode.Protocol.GlmPreset.ProviderId => VibeCode.Protocol.GlmPreset.DisplayName,
        _ => provider ?? "",
    };

    /// <summary>
    /// The provider list, held in a nested type on purpose.
    ///
    /// <see cref="Instance"/> is a static initializer that runs <see cref="Load"/>, and static initializers run in
    /// DECLARATION order - so anything Load touches that is declared below it is still null when Load runs. A
    /// nested type's statics initialize on first touch of that type instead, which is order-independent. This bit
    /// once already: the retired-provider purge read the list from here, got null, threw, and was swallowed whole
    /// by Load's catch - leaving the purge looking like it simply did nothing.
    /// </summary>
    private static class Known
    {
        /// <summary>Providers that can be signed into with a key, in menu order.</summary>
        /// <remarks>GLM is last because it is the only entry here that is ONLY a key - the other four are
        /// subscription CLIs for which a key is an alternative. Its id must stay in this list: it is also what
        /// <see cref="DropRetiredProviders"/> checks, so omitting it would silently delete every saved GLM key
        /// on the next launch.</remarks>
        internal static readonly string[] InMenuOrder =
            ["claude", "codex", "grok", "kimi", VibeCode.Protocol.GlmPreset.ProviderId];
    }

    /// <summary>The providers that can be signed into with a key, in menu order.</summary>
    public static IReadOnlyList<string> Providers => Known.InMenuOrder;

    public string? SelectedId(string provider) =>
        _selected.TryGetValue(provider, out var id) ? id : null;

    /// <summary>The key account this provider should run under, or null to use the subscription login.</summary>
    public ApiKeyAccount? SelectedFor(string provider)
    {
        var id = SelectedId(provider);
        return id is null ? null : _accounts.FirstOrDefault(a => a.Id == id);
    }

    /* ── mutation ─────────────────────────────────────────────────────────── */

    public ApiKeyAccount Add(string provider, string key, string? label = null)
    {
        key = (key ?? "").Trim();
        var account = new ApiKeyAccount
        {
            Id = Guid.NewGuid().ToString("n"),
            Provider = provider.ToLowerInvariant(),
            Label = label?.Trim() ?? "",
            Secret = Protect(key),
            Masked = Mask(key),
        };
        _accounts.Add(account);
        _selected[account.Provider] = account.Id;   // adding a key selects it, which is the point
        Save();
        RaiseAll();
        return account;
    }

    public void Remove(string id)
    {
        var account = _accounts.FirstOrDefault(a => a.Id == id);
        if (account is null) return;
        _accounts.Remove(account);
        if (SelectedId(account.Provider) == id) _selected.Remove(account.Provider);
        Save();
        RaiseAll();
    }

    /// <summary>Select a key account, or pass null to fall back to the subscription login.</summary>
    public void Select(string provider, string? id)
    {
        provider = provider.ToLowerInvariant();
        if (id is null) _selected.Remove(provider);
        else _selected[provider] = id;
        Save();
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var a in _accounts) a.RefreshSelection();
        Raise(nameof(Accounts));
        Raise(nameof(HasAny));
    }

    public bool HasAny => _accounts.Count > 0;

    /* ── the actual point: putting the key in front of the CLI ────────────── */

    /// <summary>
    /// Inject this provider's key into a CLI launch, if (and only if) the selected account for that
    /// provider is a key account. Call AFTER any env scrubbing so the deliberate value survives.
    /// </summary>
    public bool ApplyTo(System.Diagnostics.ProcessStartInfo psi, string provider)
    {
        var account = SelectedFor(provider);
        if (account is null) return false;

        var key = Reveal(account);
        if (string.IsNullOrWhiteSpace(key)) return false;

        switch (provider.ToLowerInvariant())
        {
            case "claude":
                psi.Environment["ANTHROPIC_API_KEY"] = key;
                break;
            case "codex":
                psi.Environment["OPENAI_API_KEY"] = key;
                break;
            case "grok":
                // The Grok CLI reads either name; set both so it works whichever build is installed.
                psi.Environment["XAI_API_KEY"] = key;
                psi.Environment["GROK_API_KEY"] = key;
                break;
            case "kimi":
                // Moonshot speaks the Anthropic wire format, so a Kimi key drives an Anthropic-shaped
                // client by repointing the base URL rather than needing its own transport.
                psi.Environment["MOONSHOT_API_KEY"] = key;
                psi.Environment["ANTHROPIC_BASE_URL"] = "https://api.moonshot.ai/anthropic";
                psi.Environment["ANTHROPIC_AUTH_TOKEN"] = key;
                break;
            case VibeCode.Protocol.GlmPreset.ProviderId:
                // Nothing to inject: this provider has no CLI to launch. GlmSession is spoken in-process and
                // reads its keys through KeysFor below.
                return false;
            default:
                return false;
        }
        return true;
    }

    /// <summary>
    /// The plaintext key for a provider whose session is spoken in-process rather than launched as a CLI, or ""
    /// when no key account is selected. The CLI providers must keep using <see cref="ApplyTo"/> - handing a key
    /// back to arbitrary callers is only safe because this one never leaves the process.
    /// </summary>
    public string KeyFor(string provider)
    {
        var account = SelectedFor(provider);
        return account is null ? "" : Reveal(account);
    }

    /// <summary>
    /// Every usable key for an in-process provider, the selected one first.
    ///
    /// Exists for failover: a free tier can rate-limit after a couple of requests, so a session that holds
    /// several keys can move to the next one instead of failing the turn. Order matters - the account the
    /// user picked is always tried first, and the rest are a fallback rather than a pool to round-robin, so
    /// normal use stays on one key and stays predictable.
    /// </summary>
    public IReadOnlyList<string> KeysFor(string provider)
    {
        var selected = SelectedFor(provider);
        var ordered = new List<string>();
        if (selected is not null && Reveal(selected) is { Length: > 0 } first) ordered.Add(first);
        foreach (var account in For(provider))
        {
            if (selected is not null && account.Id == selected.Id) continue;
            if (Reveal(account) is { Length: > 0 } key && !ordered.Contains(key, StringComparer.Ordinal))
                ordered.Add(key);
        }
        return ordered;
    }

    /* ── validation ───────────────────────────────────────────────────────── */

    /// <summary>
    /// Confirm a key works before saving it. Every one of these vendors exposes a free GET /v1/models,
    /// so this proves the credential without generating a single billable token.
    /// </summary>
    public static async Task<ApiKeyValidation> ValidateAsync(string provider, string key, CancellationToken ct = default)
    {
        key = (key ?? "").Trim();
        if (key.Length < 8) return ApiKeyValidation.Bad("That key looks too short.");

        var (url, apply) = provider.ToLowerInvariant() switch
        {
            "claude" => ("https://api.anthropic.com/v1/models",
                (Action<HttpRequestMessage>)(r =>
                {
                    r.Headers.TryAddWithoutValidation("x-api-key", key);
                    r.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                })),
            "codex" => ("https://api.openai.com/v1/models",
                r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)),
            "grok" => ("https://api.x.ai/v1/models",
                r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)),
            "kimi" => ("https://api.moonshot.ai/v1/models",
                r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key)),
            // Baseten. Its own scheme rather than Bearer (both work, this is the documented one). A bad key here
            // answers 403 rather than 401, which the status mapping below already treats as a rejection.
            VibeCode.Protocol.GlmPreset.ProviderId =>
                ($"{VibeCode.Protocol.GlmPreset.DefaultBaseUrl.TrimEnd('/')}/models",
                r => r.Headers.Authorization =
                    new AuthenticationHeaderValue(VibeCode.Protocol.GlmPreset.AuthScheme, key)),
            _ => (null!, null!),
        };
        if (url is null) return ApiKeyValidation.Bad("Unknown provider.");

        // A gateway blip must not be reported as a bad key. Measured against Baseten: a wrong key answers 403
        // about five times in six and an occasional 502, while a good key answers 200 every time - so a single
        // 5xx says nothing about the credential. Without a retry the damaging case is the good one: paste a
        // working key, land on the blip, and be told the provider rejected it.
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                apply(req);
                // ResponseHeadersRead because only the STATUS is inspected below - never the body.
                // Not a micro-optimisation: Baseten answers a bad key with a 403 that advertises
                // "Content-Length: 135" and then sends zero bytes, so buffering the body (the default) throws
                // "Error while copying content to a stream" before the status is ever looked at. That landed in
                // the catch-all underneath and told the user "Couldn't reach the provider" for what is really a
                // rejected key - sending them to debug a network that was working fine.
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (res.IsSuccessStatusCode) return ApiKeyValidation.Good();
                var status = (int)res.StatusCode;
                if (IsTransientStatus(status) && attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
                    continue;
                }
                return status switch
                {
                    401 or 403 => ApiKeyValidation.Bad("The provider rejected that key."),
                    429 => ApiKeyValidation.Good("Key is valid (rate limited right now)."),
                    // Deliberately not "that key is bad": after the retries above this is the provider being
                    // unwell, and the key may well be fine.
                    _ => ApiKeyValidation.Bad(
                        $"Couldn't check the key - the provider returned {status}. Try again in a moment."),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException) { return ApiKeyValidation.Bad("Timed out reaching the provider."); }
            catch (Exception) when (attempt < attempts)
            {
                // A truncated/reset response is the same class of blip as a 5xx; give it the same second chance.
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct);
            }
            catch (Exception ex) { return ApiKeyValidation.Bad("Couldn't reach the provider: " + ex.Message); }
        }
    }

    /// <summary>A status that says the provider is unwell rather than that the credential is wrong.</summary>
    private static bool IsTransientStatus(int status) => status >= 500 || status == 408;

    /* ── storage ──────────────────────────────────────────────────────────── */

    private static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 10) return new string('•', key.Length);
        return key[..Math.Min(7, key.Length)] + "…" + key[^4..];
    }

    /// <summary>DPAPI, current-user scope: the file is useless on any other account or machine.</summary>
    private static string Protect(string plain)
    {
        try
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch
        {
            // Never silently fall back to plaintext for a credential.
            return "";
        }
    }

    public static string Reveal(ApiKeyAccount account)
    {
        if (string.IsNullOrEmpty(account.Secret)) return "";
        try
        {
            var blob = Convert.FromBase64String(account.Secret);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    private sealed class Persisted
    {
        public List<ApiKeyAccount> Accounts { get; set; } = new();
        public Dictionary<string, string> Selected { get; set; } = new();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var data = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(FilePath));
            if (data is null) return;
            _accounts.AddRange(data.Accounts);
            foreach (var kv in data.Selected) _selected[kv.Key] = kv.Value;
            if (DropRetiredProviders()) Save();
        }
        catch { /* a corrupt file must not stop the app starting */ }
    }

    /// <summary>
    /// Forget keys saved for a provider this build no longer has.
    ///
    /// Not housekeeping for its own sake: <see cref="DisplayName"/> falls back to the raw provider id, so an
    /// orphaned row would keep the removed provider's internal name visible in the account manager - and there is
    /// no panel left to delete it from, because the panel went with the provider.
    /// </summary>
    private bool DropRetiredProviders()
    {
        var live = Known.InMenuOrder;
        var removed = _accounts.RemoveAll(a => !live.Contains(a.Provider ?? "", StringComparer.OrdinalIgnoreCase));
        foreach (var key in _selected.Keys.Where(k => !live.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _selected.Remove(key);
            removed++;
        }
        return removed > 0;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            var json = JsonSerializer.Serialize(
                new Persisted { Accounts = _accounts, Selected = new Dictionary<string, string>(_selected) },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* best effort - the in-memory list still works this session */ }
    }
}
