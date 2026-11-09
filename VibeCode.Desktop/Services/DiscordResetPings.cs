using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>One limit window of one account, as that provider's usage check last reported it.</summary>
/// <param name="AccountKey">"provider:accountId" - the key the Discord account list ticks on and off.</param>
/// <param name="Window">Stable id of the window within its account ("session", "week", a Codex bucket…).</param>
/// <param name="WindowLabel">How a ping names it: "session limit", "weekly limit".</param>
/// <param name="SeenAt">When the check ran. A check seen before is not news.</param>
public sealed record UsageWindowReading(string AccountKey, string Provider, string AccountLabel, string Window,
    string WindowLabel, double Percent, DateTimeOffset? ResetsAt, DateTimeOffset SeenAt);

/// <summary>An account the pings can cover, as the tick list shows it.</summary>
public sealed record ResetPingAccount(string Key, string Provider, string Label, string Detail);

/// <summary>What the account sources know right now: every account, and every window a usage check has reported.</summary>
public sealed record ResetPingSnapshot(IReadOnlyList<ResetPingAccount> Accounts, IReadOnlyList<UsageWindowReading> Readings);

/// <summary>A window that reset, and how much of it had been used.</summary>
public sealed record UsageReset(string AccountKey, string Provider, string AccountLabel, string WindowLabel,
    double Percent, DateTimeOffset At);

/// <summary>
/// Turns usage readings into "this window has reset". A window with any usage is remembered with its reset time, and
/// once that time passes it HAS reset - no new check is needed, so the ping is on time even for an account whose usage
/// is read only every half hour. A check whose number collapsed (to 0, or by half and at least 10 points) counts too:
/// that is a window that reports no reset time, or a reset the provider granted early. Pure: the caller supplies the
/// clock, persists <see cref="State"/> and decides who hears about it.
/// </summary>
public sealed class ResetWatch
{
    /// <summary>A reset noticed this late (the app was closed, the PC asleep) is old news and passes quietly.</summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromHours(2);
    // Reset times wobble between checks (Claude prints only minutes), so resets this close are the same one.
    private static readonly TimeSpan SameReset = TimeSpan.FromMinutes(5);
    // Longer than a weekly window, so nothing still running is forgotten.
    private static readonly TimeSpan Forget = TimeSpan.FromDays(9);

    public sealed class Window
    {
        public string AccountKey { get; set; } = "";
        public string Provider { get; set; } = "";
        public string AccountLabel { get; set; } = "";
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>The most this window has been used; what the ping reports.</summary>
        public double Peak { get; set; }
        /// <summary>The latest check, which a collapse is measured against.</summary>
        public double Last { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public DateTimeOffset SeenAt { get; set; }
    }

    public sealed class Memory
    {
        public List<Window> Windows { get; set; } = [];
        /// <summary>The last reset announced for each window, so a late or repeated check of it stays quiet.</summary>
        public Dictionary<string, DateTimeOffset> Announced { get; set; } = new(StringComparer.Ordinal);
    }

    public ResetWatch(Memory? memory = null) => State = memory ?? new Memory();

    public Memory State { get; }

    /// <summary>True when the last <see cref="Step"/> changed <see cref="State"/>, so it is worth saving.</summary>
    public bool Changed { get; private set; }

    public List<UsageReset> Step(IEnumerable<UsageWindowReading> readings, DateTimeOffset now)
    {
        Changed = false;
        var resets = new List<UsageReset>();

        // Windows whose reset time has passed have reset, whether or not anything has checked since.
        foreach (var window in State.Windows.Where(w => w.ResetsAt <= now).ToArray())
        {
            var at = window.ResetsAt!.Value;
            State.Windows.Remove(window);
            if (window.Peak > 0 && !WasAnnounced(KeyOf(window), at) && now - at <= MissedGrace)
                resets.Add(ResetOf(window, at));
            State.Announced[KeyOf(window)] = at;   // even when too old to announce, so a stale check of it stays quiet
            Changed = true;
        }

        foreach (var reading in readings)
        {
            if (!double.IsFinite(reading.Percent) || reading.Percent < 0) continue;
            // A check from before a reset still describes the window that ended; the clock above already handled it.
            if (reading.ResetsAt <= now) continue;
            var key = KeyOf(reading.AccountKey, reading.Window);
            var window = State.Windows.FirstOrDefault(w => KeyOf(w) == key);
            if (window is null)
            {
                // An unused window has nothing to reset.
                if (reading.Percent > 0 && !(reading.ResetsAt is { } next && WasAnnounced(key, next)))
                {
                    State.Windows.Add(Track(reading));
                    Changed = true;
                }
                continue;
            }
            if (reading.SeenAt <= window.SeenAt) continue;   // the same check again, or an older one

            var collapsed = window.Last > 0 && (reading.Percent == 0
                || reading.Percent <= window.Last / 2 && window.Last - reading.Percent >= 10);
            if (collapsed)
            {
                resets.Add(ResetOf(window, reading.SeenAt));
                State.Announced[key] = window.ResetsAt ?? reading.SeenAt;
                State.Windows.Remove(window);
                if (reading.Percent > 0) State.Windows.Add(Track(reading));
            }
            else
            {
                window.Last = reading.Percent;
                window.Peak = Math.Max(window.Peak, reading.Percent);
                window.ResetsAt = reading.ResetsAt ?? window.ResetsAt;
                window.SeenAt = reading.SeenAt;
                window.AccountLabel = reading.AccountLabel;   // a renamed account pings under its new name
                window.Label = reading.WindowLabel;
            }
            Changed = true;
        }

        // An account that was removed, or a window no check reports any more, is eventually let go.
        if (State.Windows.RemoveAll(w => w.ResetsAt is null && now - w.SeenAt > Forget) > 0) Changed = true;
        foreach (var old in State.Announced.Where(pair => now - pair.Value > Forget).Select(pair => pair.Key).ToArray())
        {
            State.Announced.Remove(old);
            Changed = true;
        }
        return resets;
    }

    private bool WasAnnounced(string key, DateTimeOffset at) =>
        State.Announced.TryGetValue(key, out var announced) && (announced - at).Duration() <= SameReset;

    private static Window Track(UsageWindowReading reading) => new()
    {
        AccountKey = reading.AccountKey,
        Provider = reading.Provider,
        AccountLabel = reading.AccountLabel,
        Id = reading.Window,
        Label = reading.WindowLabel,
        Peak = reading.Percent,
        Last = reading.Percent,
        ResetsAt = reading.ResetsAt,
        SeenAt = reading.SeenAt,
    };

    private static UsageReset ResetOf(Window window, DateTimeOffset at) =>
        new(window.AccountKey, window.Provider, window.AccountLabel, window.Label, window.Peak, at);

    private static string KeyOf(Window window) => KeyOf(window.AccountKey, window.Id);
    private static string KeyOf(string accountKey, string window) => accountKey + "\n" + window;
}

/// <summary>
/// Pings a Discord channel, mentioning @everyone, when an AI account's usage limit resets: with a bunch of accounts the
/// user hears the moment one is usable again instead of checking the account manager. Every account is covered by
/// default; the account manager's list unticks the ones the user doesn't care about, stored as EXCLUSIONS so an
/// account added later is covered too.
///

/// GLM with Claude's) and asks no provider anything itself; <see cref="ResetWatch"/> decides when a window reset.
/// The webhook URL is a credential - anyone holding it can post to the channel - so it is sealed with DPAPI in this
/// service's own file and never written to settings.json, the same rule <see cref="GroqSpeechService"/> follows.
/// </summary>
public sealed class DiscordResetPings : Observable
{
    public static DiscordResetPings Instance { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, null);

    /// <summary>How often the readings are looked at. They are in memory already, so this is cheap.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    // A ping Discord could not take (offline, an outage) is retried this long, then dropped as stale.
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibeCode.DiscordResetPings.v1");

    private readonly HttpClient _http;
    private readonly string? _filePath;
    private string FilePath => _filePath ?? Path.Combine(AppSettings.Dir, "discord-reset-pings.json");

    private string _secret = "";   // base64 DPAPI ciphertext of the webhook URL, never the URL itself
    private string _masked = "";
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private ResetWatch _watch = new();
    // Everything here runs on the UI thread (the timer, the buttons), so no locking: only awaits interleave.
    private readonly List<(string Content, DateTimeOffset Queued)> _outbox = [];
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private bool _flushing;
    private string _lastSent = "";
    private Func<ResetPingSnapshot>? _source;
    private DispatcherTimer? _timer;
    private string _status = "";
    private bool _statusIsError;

    /// <summary>Tests pass their own client and file; the app uses <see cref="Instance"/>.</summary>
    public DiscordResetPings(HttpClient http, string? filePath)
    {
        _http = http;
        _filePath = filePath;
        Load();
    }

    public bool HasWebhook => _secret.Length > 0;

    /// <summary>"discord.com/api/webhooks/1234…/…AbCd" - enough to recognise the webhook without revealing it.</summary>
    public string MaskedWebhook => _masked;

    /// <summary>The last thing worth saying: the latest ping, or why Discord refused one.</summary>
    public string StatusText => !HasWebhook ? "Not set up yet. Paste a webhook from your Discord channel."
        : _status.Length > 0 ? _status
        : "Ready. Ticked accounts ping @everyone when their usage resets.";

    public bool StatusIsError => HasWebhook && _statusIsError;

    public bool Includes(string accountKey) => !_excluded.Contains(accountKey);

    public void SetIncluded(string accountKey, bool included)
    {
        if (included ? _excluded.Remove(accountKey) : _excluded.Add(accountKey)) Persist();
    }

    public void SetAllIncluded(IEnumerable<string> accountKeys, bool included)
    {
        var changed = false;
        foreach (var key in accountKeys) changed |= included ? _excluded.Remove(key) : _excluded.Add(key);
        if (changed) Persist();
    }

    /// <summary>Accept only a Discord webhook address, so a pasted channel link or a typo is caught when saving
    /// rather than as a silent failure at the first reset. Returns the canonical URL.</summary>
    public static bool TryParseWebhook(string? text, out string url, out string problem)
    {
        url = "";
        problem = "";
        var trimmed = (text ?? "").Trim().Trim('<', '>', '"', '\'');
        if (trimmed.Length == 0)
        {
            problem = "Paste the webhook URL: in Discord, Edit Channel ▸ Integrations ▸ Webhooks ▸ Copy Webhook URL.";
            return false;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.ToLowerInvariant() is not ("discord.com" or "discordapp.com" or "ptb.discord.com"
                or "canary.discord.com" or "ptb.discordapp.com" or "canary.discordapp.com"))
        {
            problem = "That isn't a Discord webhook URL. It starts with https://discord.com/api/webhooks/.";
            return false;
        }
        var path = Regex.Match(uri.AbsolutePath, @"^/api(?:/v\d+)?/webhooks/(\d{5,25})/([A-Za-z0-9_\-]{20,})/?$");
        if (!path.Success)
        {
            problem = "That Discord link isn't a webhook. Copy it from Edit Channel ▸ Integrations ▸ Webhooks.";
            return false;
        }
        // A webhook in a forum channel posts into one thread; that is the only query part worth keeping.
        var thread = Regex.Match(uri.Query, @"(?:^\?|&)thread_id=(\d{5,25})(?:&|$)");
        url = $"https://discord.com/api/webhooks/{path.Groups[1].Value}/{path.Groups[2].Value}"
              + (thread.Success ? "?thread_id=" + thread.Groups[1].Value : "");
        return true;
    }

    public bool SaveWebhook(string url)
    {
        if (!TryParseWebhook(url, out var canonical, out _) || Protect(canonical) is not { Length: > 0 } sealedUrl) return false;
        _secret = sealedUrl;
        _masked = Mask(canonical);
        _retryAt = DateTimeOffset.MinValue;   // a wait Discord set for the old webhook says nothing about this one
        SetStatus("", error: false);
        Persist();
        RaiseState();
        return true;
    }

    public void RemoveWebhook()
    {
        if (!HasWebhook) return;
        _secret = "";
        _masked = "";
        _outbox.Clear();
        // Nothing is watched without a webhook; a new one starts from that day's readings, not an old backlog.
        _watch = new ResetWatch();
        SetStatus("", error: false);
        Persist();
        RaiseState();
    }

    /// <summary>Post a test ping, so the user sees it land before relying on it.</summary>
    public async Task<(bool Sent, string Message)> SendTestAsync(CancellationToken ct = default)
    {
        if (Webhook() is not { Length: > 0 } url) return (false, "Save a webhook first.");
        var result = await PostAsync(_http, url,
            "@everyone VibeCode test ping. Reset pings are set up: you'll get one like this when an AI account's usage resets.",
            ct);
        var message = result.Outcome == PostOutcome.Sent ? $"Test ping sent {DateTime.Now:h:mm tt}. Check your channel." : result.Problem;
        SetStatus(message, error: result.Outcome != PostOutcome.Sent);
        return (result.Outcome == PostOutcome.Sent, message);
    }

    /// <summary>Begin watching. <paramref name="source"/> is read on the UI thread every <see cref="CheckEvery"/>.
    /// Calling again only swaps the source: a reloaded shell must not start a second timer.</summary>
    public void Start(Func<ResetPingSnapshot> source)
    {
        _source = source;
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckEvery };
        _timer.Tick += async (_, _) =>
        {
            try
            {
                // Without a webhook nothing is watched, so nothing is read either.
                if (HasWebhook && _source?.Invoke() is { } snapshot) await CheckAsync(snapshot, DateTimeOffset.Now);
            }
            catch (Exception ex) { CrashLog.Note("DiscordResetPings", ex.Message); }
        };
        _timer.Start();
    }

    /// <summary>One look at the readings: note any resets, queue their pings, and deliver what is queued.</summary>
    public async Task CheckAsync(ResetPingSnapshot snapshot, DateTimeOffset now)
    {
        if (!HasWebhook) return;
        var resets = _watch.Step(snapshot.Readings, now);
        if (_watch.Changed) Persist();
        var known = snapshot.Accounts.Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
        // A removed account's last window can still come due; it pings no one.
        var wanted = resets.Where(reset => known.Contains(reset.AccountKey) && Includes(reset.AccountKey)).ToList();
        if (wanted.Count > 0)
        {
            _outbox.AddRange(Compose(wanted).Select(content => (content, now)));
            _lastSent = Summary(wanted);
        }
        await FlushAsync(now);
    }

    private async Task FlushAsync(DateTimeOffset now)
    {
        if (_flushing || now < _retryAt || Webhook() is not { Length: > 0 } url) return;
        _flushing = true;
        try
        {
            while (true)
            {
                _outbox.RemoveAll(item => now - item.Queued > GiveUpAfter);
                if (_outbox.Count == 0) return;
                var next = _outbox[0];
                var result = await PostAsync(_http, url, next.Content, CancellationToken.None);
                if (result.Outcome == PostOutcome.Retry)
                {
                    _retryAt = now + result.RetryAfter;
                    SetStatus(result.Problem + " Retrying.", error: true);
                    return;
                }
                _outbox.Remove(next);
                if (result.Outcome == PostOutcome.Rejected)
                {
                    // A deleted webhook or a bad token refuses every ping the same way; holding them changes nothing.
                    _outbox.Clear();
                    SetStatus(result.Problem, error: true);
                    return;
                }
                SetStatus($"Last ping {now.LocalDateTime:h:mm tt}: {_lastSent}.", error: false);
            }
        }
        finally { _flushing = false; }
    }

    /* ── the message ──────────────────────────────────────────────────────── */

    /// <summary>The Discord messages for one batch of resets: one line per account, each window it reset in. Discord
    /// caps a message at 2000 characters, so a long batch is split, and only the first part pings.</summary>
    public static List<string> Compose(IReadOnlyList<UsageReset> resets)
    {
        var accounts = resets.GroupBy(reset => reset.AccountKey).Select(group =>
        {
            var first = group.First();
            var windows = string.Join(", ", group.Select(reset => $"{reset.WindowLabel} was {Percent(reset.Percent)} used"));
            return (Name: $"{ProviderName(first.Provider)} account **{Escape(first.AccountLabel)}**", Windows: windows);
        }).ToList();
        if (accounts.Count == 1)
            return [$"@everyone {accounts[0].Name} has been reset ({accounts[0].Windows})."];

        var messages = new List<string>();
        var current = new StringBuilder($"@everyone {accounts.Count} accounts have been reset:");
        foreach (var (name, windows) in accounts)
        {
            var line = $"\n• {name} ({windows})";
            if (current.Length + line.Length > 1900)
            {
                messages.Add(current.ToString());
                current.Clear().Append("More resets:");
            }
            current.Append(line);
        }
        messages.Add(current.ToString());
        return messages;
    }

    private static string Summary(IReadOnlyList<UsageReset> resets)
    {
        var names = resets.Select(reset => $"{ProviderName(reset.Provider)} {reset.AccountLabel}").Distinct().ToList();
        return names.Count == 1 ? $"{names[0]} reset" : $"{names.Count} accounts reset";
    }

    internal static string ProviderName(string provider) => provider switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "gemini" => "Gemini",
        "glm" => "GLM",
        _ => provider,
    };

    private static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>An account name is the user's own text: keep it from turning into Discord formatting, a masked link
    /// or a mention.</summary>
    internal static string Escape(string text) =>
        Regex.Replace(text, @"[\\*_~`|>\[\]]", @"\$0").Replace("@", "@​");

    /* ── Discord ──────────────────────────────────────────────────────────── */

    internal enum PostOutcome { Sent, Retry, Rejected }

    internal readonly record struct PostResult(PostOutcome Outcome, TimeSpan RetryAfter, string Problem);

    internal static async Task<PostResult> PostAsync(HttpClient http, string url, string content, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["content"] = content,
            ["username"] = "VibeCode",
            // Without this a webhook's @everyone is plain text. Only @everyone is allowed: an account name never pings.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray("everyone") },
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new(PostOutcome.Sent, default, "");
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 429) return new(PostOutcome.Retry, RetryAfter(response, text), "Discord is rate limiting this webhook.");
            if (status >= 500) return new(PostOutcome.Retry, TimeSpan.FromMinutes(1), $"Discord is having trouble (HTTP {status}).");
            return new(PostOutcome.Rejected, default, status switch
            {
                404 => "Discord says this webhook no longer exists. Paste a new one.",
                401 or 403 => "Discord refused this webhook's token. Copy the webhook URL again.",
                _ => $"Discord refused the ping (HTTP {status}{(DiscordMessage(text) is { } why ? ": " + why : "")}).",
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(PostOutcome.Retry, TimeSpan.FromMinutes(1), "Discord didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new(PostOutcome.Retry, TimeSpan.FromMinutes(1), "Couldn't reach Discord: " + ex.Message);
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, string body)
    {
        double seconds = 5;
        try
        {
            if (JsonNode.Parse(body)?["retry_after"] is JsonValue value && value.TryGetValue<double>(out var parsed)) seconds = parsed;
        }
        catch (JsonException) { /* not JSON - the header, or the default */ }
        if (response.Headers.RetryAfter?.Delta is { } delta) seconds = Math.Max(seconds, delta.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600));
    }

    private static string? DiscordMessage(string body)
    {
        try { return JsonNode.Parse(body)?["message"]?.GetValue<string>() is { Length: > 0 } text ? text : null; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /* ── storage ──────────────────────────────────────────────────────────── */

    private void SetStatus(string text, bool error)
    {
        _status = text;
        _statusIsError = error;
        Raise(nameof(StatusText));
        Raise(nameof(StatusIsError));
    }

    private void RaiseState()
    {
        Raise(nameof(HasWebhook));
        Raise(nameof(MaskedWebhook));
        Raise(nameof(StatusText));
        Raise(nameof(StatusIsError));
    }

    /// <summary>The webhook URL, unsealed on demand. Empty when there is none, or when the blob was sealed by another
    /// Windows user or machine.</summary>
    private string Webhook()
    {
        if (_secret.Length == 0) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(_secret), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    private static string Protect(string plain)
    {
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy,
                DataProtectionScope.CurrentUser));
        }
        catch { return ""; }   // never fall back to plaintext for a credential
    }

    private static string Mask(string url)
    {
        var parts = Regex.Match(url, @"/webhooks/(\d+)/([^/?]+)");
        return parts.Success
            ? $"discord.com/api/webhooks/{parts.Groups[1].Value[..Math.Min(4, parts.Groups[1].Value.Length)]}…/…{parts.Groups[2].Value[^4..]}"
            : "discord.com/api/webhooks/…";
    }

    private sealed class Persisted
    {
        public string Secret { get; set; } = "";
        public string Masked { get; set; } = "";
        public List<string> Excluded { get; set; } = [];
        public ResetWatch.Memory? Watch { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath) || JsonSerializer.Deserialize<Persisted>(File.ReadAllText(FilePath)) is not { } saved) return;
            _secret = saved.Secret ?? "";
            _masked = saved.Masked ?? "";
            _excluded.UnionWith(saved.Excluded ?? []);
            _watch = new ResetWatch(saved.Watch);
        }
        catch { /* unreadable: behave as "not set up" rather than failing the app's startup */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(new Persisted
            {
                Secret = _secret,
                Masked = _masked,
                Excluded = _excluded.Order(StringComparer.Ordinal).ToList(),
                Watch = _watch.State,
            }, new JsonSerializerOptions { WriteIndented = true });
            // Written whole and swapped in, so a crash mid-write can't cost the saved webhook.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { /* this run still pings; the next launch starts from whatever was last saved */ }
    }
}
