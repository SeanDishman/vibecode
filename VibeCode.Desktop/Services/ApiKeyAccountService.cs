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
    /// <summary>claude | codex | grok | kimi - matches AppSettings.DefaultProvider.</summary>
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
        string.Equals(ApiKeyAccountService.Instance.SelectedId(Provider), Id, StringComparison.Ordinal);

    public void RefreshSelection() { Raise(nameof(IsSelected)); }
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
        _ => provider ?? "",
    };

    /// <summary>The providers that can be signed into with a key, in menu order.</summary>
    public static IReadOnlyList<string> Providers { get; } = new[] { "claude", "codex", "grok", "kimi" };

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
            default:
                return false;
        }
        return true;
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
            _ => (null!, null!),
        };
        if (url is null) return ApiKeyValidation.Bad("Unknown provider.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            apply(req);
            using var res = await Http.SendAsync(req, ct);

            if (res.IsSuccessStatusCode) return ApiKeyValidation.Good();
            return (int)res.StatusCode switch
            {
                401 or 403 => ApiKeyValidation.Bad("The provider rejected that key."),
                429 => ApiKeyValidation.Good("Key is valid (rate limited right now)."),
                _ => ApiKeyValidation.Bad($"Provider returned {(int)res.StatusCode}."),
            };
        }
        catch (TaskCanceledException) { return ApiKeyValidation.Bad("Timed out reaching the provider."); }
        catch (Exception ex) { return ApiKeyValidation.Bad("Couldn't reach the provider: " + ex.Message); }
    }

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
        }
        catch { /* a corrupt file must not stop the app starting */ }
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
