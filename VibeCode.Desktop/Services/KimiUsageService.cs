using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>One Kimi quota window: the subscription pool, or a rolling rate window such as the 5h one.</summary>
public sealed class KimiUsageLimit
{
    public required string Label { get; init; }
    public required int Percent { get; init; }
    /// <summary>"99 / 100" — Kimi does not name the unit, so neither do we.</summary>
    public required string Detail { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }

    public bool HasReset => ResetsAt is not null;
    public string ResetDisplay
    {
        get
        {
            if (ResetsAt is not { } reset) return "";
            var span = reset - DateTimeOffset.Now;
            if (span <= TimeSpan.Zero) return "soon";
            if (span.TotalHours < 1) return $"in {Math.Max(1, (int)Math.Round(span.TotalMinutes))}m";
            if (span.TotalHours < 48) return $"in {(int)Math.Round(span.TotalHours)}h";
            return $"in {(int)Math.Round(span.TotalDays)}d";
        }
    }
}

/// <summary>
/// Kimi for Coding quota for the bridge's usage pill. Kimi Code's <c>/usage</c> only reports session tokens, and the
/// Moonshot platform's balance endpoint covers a different (pay-as-you-go) account system, so neither can answer
/// "how much of my plan is left". The CLI's own quota view calls <c>GET {base}/usages</c>, which is what this reads.
///
/// That endpoint is undocumented — it exists only in the Kimi CLI's source — so every field is parsed defensively:
/// counts arrive as JSON strings, <c>used</c> can be missing, reset stamps come with nanosecond precision, and the
/// labelling keys have drifted between releases.
///
/// The CLI's credential file is treated as strictly READ-ONLY. VibeCode never spends the refresh token: Kimi rotates
/// it, so racing the CLI's own refresh would sign the user out of their terminal. Access tokens only live ~15
/// minutes, so when the token on disk has expired the last good numbers are kept on screen with an honest
/// "as of …" stamp rather than being replaced by an error — a Kimi agent working on the bridge refreshes the file
/// on its own, and the next poll picks it up.
/// </summary>
public sealed class KimiUsageService : Observable
{
    public static KimiUsageService Instance { get; } = new();

    private const string DefaultBaseUrl = "https://api.kimi.com/coding/v1";
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly object _gate = new();
    private DateTime _lastFetch = DateTime.MinValue;
    private bool _fetching;
    private bool _pendingForce;

    private string _summary = "";
    private string _detail = "Loading Kimi quota…";
    private string _plan = "";
    private string _updatedText = "";
    private DateTime? _lastSuccess;

    /// <summary>Compact "99% quota · 75% 5h" for the bridge header pill.</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    /// <summary>Plan badge, e.g. "Basic".</summary>
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }

    public ObservableCollection<KimiUsageLimit> Limits { get; } = new();
    public bool HasData => Limits.Count > 0;
    /// <summary>Same question as <see cref="HasData"/>, but answerable off the UI thread: the fetch runs on the
    /// thread pool and must not walk a collection the dispatcher owns.</summary>
    private bool HasReading => _lastSuccess is not null;
    /// <summary>Why the card is empty, or why the numbers it shows are stale. Blank when everything is current.</summary>
    public string Status { get => _detail; private set => Set(ref _detail, value); }
    public bool HasStatus => !string.IsNullOrWhiteSpace(_detail);
    public bool AtLimit => Limits.Any(limit => limit.Percent >= 100);

    public void Refresh(bool force = false)
    {
        lock (_gate)
        {
            if (_fetching) { if (force) _pendingForce = true; return; }
            if (!force && DateTime.UtcNow - _lastFetch < MinInterval) return;
            _fetching = true;
        }
        Application.Current?.Dispatcher.BeginInvoke(() => UpdatedText = "refreshing…");
        _ = Task.Run(FetchAsync);
    }

    private async Task FetchAsync()
    {
        try
        {
            if (ReadAccessToken() is not { } token)
            {
                Post(null, HasReading
                    ? StaleNote("Kimi's saved login is gone — sign in again from the account menu.")
                    : "No Kimi login found. Sign in to Kimi Code from the account menu.");
                return;
            }
            if (token.Expired)
            {
                // Expected whenever no Kimi agent has run for a quarter of an hour. Not an error worth shouting about.
                Post(null, HasReading
                    ? StaleNote("Kimi's access token has expired; it refreshes when a Kimi agent next runs.")
                    : "Kimi's access token has expired — start a Kimi agent, or sign in again, to read the quota.");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl().TrimEnd('/')}/usages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Post(null, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Kimi rejected this login — sign in again from the account menu.",
                    HttpStatusCode.NotFound => "This account has no Kimi for Coding subscription, so it reports no quota.",
                    _ => $"Kimi returned {(int)response.StatusCode} for the quota check — try Refresh.",
                });
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            var parsed = Parse(body);
            Post(parsed, parsed.Limits.Count == 0
                ? "Kimi answered the quota check but reported no limits for this account."
                : "");
        }
        catch (Exception ex)
        {
            Post(null, HasData ? StaleNote($"Couldn't reach Kimi: {ex.Message}") : $"Couldn't load Kimi quota: {ex.Message}");
        }
        finally
        {
            bool rerun;
            lock (_gate)
            {
                _lastFetch = DateTime.UtcNow;
                _fetching = false;
                rerun = _pendingForce;
                _pendingForce = false;
            }
            if (rerun) Refresh(force: true);
        }
    }

    private string StaleNote(string reason) => _lastSuccess is { } at
        ? $"{reason} Showing the numbers from {at:h:mm tt}."
        : reason;

    // ---- credentials -------------------------------------------------------------------------------------------

    private readonly record struct AccessToken(string Value, bool Expired);

    /// <summary>Kimi Code's credential file, then the legacy Python CLI's. Both honour their own home override.</summary>
    internal static IEnumerable<string> CredentialPaths()
    {
        var home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
        yield return Path.Combine(home.Trim('"'), "credentials", "kimi-code.json");

        var legacy = Environment.GetEnvironmentVariable("KIMI_SHARE_DIR");
        if (string.IsNullOrWhiteSpace(legacy))
            legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi");
        yield return Path.Combine(legacy.Trim('"'), "credentials", "kimi-code.json");
    }

    private static string BaseUrl() =>
        Environment.GetEnvironmentVariable("KIMI_CODE_BASE_URL") is { Length: > 0 } configured
            ? configured.Trim('"')
            : DefaultBaseUrl;

    private static AccessToken? ReadAccessToken()
    {
        foreach (var path in CredentialPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject saved) continue;
                if (saved["access_token"]?.GetValue<string>() is not { Length: > 0 } value) continue;
                // expires_at is unix seconds (a float in the legacy CLI). A minute of slack keeps us from firing a
                // request that is certain to 401 by the time it lands.
                var expiresAt = Number(saved["expires_at"]);
                var expired = expiresAt > 0 && DateTimeOffset.FromUnixTimeSeconds(expiresAt) <= DateTimeOffset.UtcNow.AddSeconds(60);
                return new AccessToken(value, expired);
            }
            catch (Exception) { /* unreadable or half-written by the CLI; try the next location */ }
        }
        return null;
    }

    // ---- parsing -----------------------------------------------------------------------------------------------

    internal sealed class Parsed
    {
        public string Summary = "";
        public string Plan = "";
        public List<KimiUsageLimit> Limits = new();
    }

    /// <summary>Reads the /usages payload. Internal so the shape can be exercised against captured responses.</summary>
    internal static Parsed Parse(string body)
    {
        var parsed = new Parsed();
        if (JsonNode.Parse(body) is not JsonObject root) return parsed;

        if (Limit(root["usage"], "Quota") is { } pool) parsed.Limits.Add(pool);
        if (root["limits"] is JsonArray windows)
            foreach (var window in windows)
                if (Limit(window, "Rate limit") is { } rate)
                    parsed.Limits.Add(rate);

        parsed.Plan = Titlecase(Text(root["user"]?["membership"]?["level"]), "LEVEL_");
        parsed.Summary = string.Join(" · ", parsed.Limits.Select(l => $"{l.Percent}% {l.Label.ToLowerInvariant()}"));
        return parsed;
    }

    private static KimiUsageLimit? Limit(JsonNode? node, string fallbackLabel)
    {
        if (node is not JsonObject entry) return null;
        // A rate window nests its counters under "detail"; the subscription pool carries them directly.
        var counters = entry["detail"] as JsonObject ?? entry;
        var limit = Number(counters["limit"]);
        if (limit <= 0) return null;
        var used = counters["used"] is { } u ? Number(u) : limit - Number(counters["remaining"]);
        used = Math.Clamp(used, 0, limit);

        return new KimiUsageLimit
        {
            Label = Label(entry, fallbackLabel),
            Percent = (int)Math.Round(used * 100.0 / limit),
            Detail = $"{used} / {limit}",
            ResetsAt = Reset(counters) ?? Reset(entry),
        };
    }

    /// <summary>Kimi has spelled this several ways across releases; a 5h window usually carries no name at all and
    /// has to be described from its own duration.</summary>
    private static string Label(JsonObject entry, string fallback)
    {
        foreach (var key in new[] { "name", "title", "scope" })
            if (Text(entry[key]) is { Length: > 0 } named)
                return named;

        if (entry["window"] is JsonObject window)
        {
            var duration = Number(window["duration"]);
            var unit = Text(window["timeUnit"]) ?? Text(window["time_unit"]) ?? "";
            var minutes = unit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase) ? duration
                : unit.Contains("HOUR", StringComparison.OrdinalIgnoreCase) ? duration * 60
                : unit.Contains("SECOND", StringComparison.OrdinalIgnoreCase) ? duration / 60
                : 0;
            if (minutes >= 1440 && minutes % 1440 == 0) return $"{minutes / 1440}d";
            if (minutes >= 60 && minutes % 60 == 0) return $"{minutes / 60}h";
            if (minutes > 0) return $"{minutes}m";
        }
        return fallback;
    }

    private static DateTimeOffset? Reset(JsonNode? node)
    {
        if (node is not JsonObject entry) return null;
        foreach (var key in new[] { "resetTime", "reset_time", "resetAt", "reset_at" })
            if (Text(entry[key]) is { Length: > 0 } stamp)
            {
                // Kimi stamps these with nanosecond precision, which DateTimeOffset will not parse. Trim the
                // fraction to 7 digits (the most .NET keeps) and let the rest go.
                var trimmed = System.Text.RegularExpressions.Regex.Replace(
                    stamp, @"(\.\d{7})\d+", "$1");
                if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var reset))
                    return reset;
            }
        foreach (var key in new[] { "resetIn", "reset_in", "ttl" })
        {
            var seconds = Number(entry[key]);
            if (seconds > 0) return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        return null;
    }

    /// <summary>Counts come back as JSON strings on this endpoint, but have appeared as numbers too.</summary>
    private static long Number(JsonNode? node)
    {
        if (node is null) return 0;
        try
        {
            if (node.GetValueKind() == JsonValueKind.Number) return (long)node.GetValue<double>();
            if (node.GetValueKind() == JsonValueKind.String)
                return long.TryParse(node.GetValue<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                    ? value : 0;
        }
        catch (Exception) { /* wrong node kind for this key */ }
        return 0;
    }

    private static string? Text(JsonNode? node)
    {
        try { return node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>"LEVEL_BASIC" -> "Basic".</summary>
    private static string Titlecase(string? value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
        trimmed = trimmed.Replace('_', ' ').Trim();
        return trimmed.Length == 0 ? "" : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private void Post(Parsed? parsed, string status) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (parsed is not null)
        {
            Summary = parsed.Summary;
            Plan = parsed.Plan;
            Limits.Clear();
            foreach (var limit in parsed.Limits) Limits.Add(limit);
            _lastSuccess = DateTime.Now;
            UpdatedText = $"updated {_lastSuccess:h:mm tt}";
        }
        else
        {
            // Keep the last good numbers rather than blanking the pill; the status line explains the staleness.
            if (Limits.Count == 0) Summary = "";
            UpdatedText = _lastSuccess is { } at ? $"as of {at:h:mm tt}" : "not read yet";
        }
        Status = status;
        Raise(nameof(HasData));
        Raise(nameof(HasStatus));
        Raise(nameof(AtLimit));
    });
}
