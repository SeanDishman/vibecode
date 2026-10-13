using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.UI;

namespace VibeCode.Services;

public sealed record GeminiQuotaWindow(string Model, double Percent, long? Remaining, DateTimeOffset? ResetsAt)
{
    public string Detail => $"{Percent:0.#}% used" + (Remaining is { } count ? $" · {count:N0} requests remaining" : "")
        + (ResetsAt is { } reset ? $" · resets {reset.ToLocalTime():g}" : "");
}

/// <summary>Reads the same Code Assist quota used by Gemini CLI. Never refreshes or writes OAuth credentials.</summary>
public sealed class GeminiUsageService : Observable
{
    public static GeminiUsageService Instance { get; } = new();
    private readonly HttpClient _http;
    private string? _accountKey;
    private GeminiUsageService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }) { }
    internal GeminiUsageService(HttpClient http) => _http = http;
    private int _fetching;
    private DateTimeOffset _lastAttempt;
    private string _plan = "Google account", _status = "Sign in with Google to view your Gemini quota.", _updated = "";
    public string Plan { get => _plan; private set => Set(ref _plan, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string UpdatedText { get => _updated; private set => Set(ref _updated, value); }
    public ObservableCollection<GeminiQuotaWindow> Limits { get; } = new();
    public bool HasData => Limits.Count > 0;
    public string Summary => HasData ? $"{Limits.Max(q => q.Percent):0.#}% used" : "Quota unavailable";
    public string Detail => string.Join("\n", new[] { Plan, Status, UpdatedText }.Where(s => s.Length > 0)
        .Concat(Limits.Select(q => $"{q.Model}: {q.Detail}")));

    public void Refresh(bool force = false) => _ = Task.Run(() => RefreshAsync(force));

    internal async Task RefreshAsync(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromMinutes(3)) return;
        if (Interlocked.Exchange(ref _fetching, 1) != 0) return;
        _lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var accountKey = GeminiRuntime.Home + "|" + GeminiRuntime.AccountEmail;
            if (_accountKey != accountKey)
            {
                _accountKey = accountKey;
                Publish("Google account", Array.Empty<GeminiQuotaWindow>(), "Refreshing Gemini quota…");
            }
            if (GeminiRuntime.UsesEncryptedCredentials)
            { Publish(null, Array.Empty<GeminiQuotaWindow>(), "Plan quota is unavailable with encrypted CLI credentials. Chat token usage is still tracked."); return; }
            var credentialsPath = Path.Combine(GeminiRuntime.ConfigDirectory, "oauth_creds.json");
            if (!File.Exists(credentialsPath)) { Publish("Google account", Array.Empty<GeminiQuotaWindow>(), "Sign in with Google to view your Gemini quota."); return; }
            var credentials = JsonNode.Parse(await File.ReadAllTextAsync(credentialsPath));
            if (credentials?["access_token"]?.GetValue<string>() is not { Length: > 0 } token)
            { Publish(null, null, "Quota unavailable: the CLI has not saved an access token."); return; }
            if (long.TryParse(credentials["expiry_date"]?.ToString(), out var expiry) && DateTimeOffset.FromUnixTimeMilliseconds(expiry) <= DateTimeOffset.UtcNow)
            { Publish(null, null, "Open a Gemini chat to refresh the Google login. Previous quota may be out of date."); return; }
            var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");
            var profile = await Post("loadCodeAssist", token, new JsonObject
            {
                ["cloudaicompanionProject"] = project,
                ["metadata"] = new JsonObject { ["ideType"] = "IDE_UNSPECIFIED", ["platform"] = "PLATFORM_UNSPECIFIED", ["pluginType"] = "GEMINI" },
            });
            project ??= profile?["cloudaicompanionProject"] is JsonObject p ? p["id"]?.ToString() : profile?["cloudaicompanionProject"]?.ToString();
            var plan = (profile?["paidTier"] ?? profile?["currentTier"])?["name"]?.ToString() ?? "Google account";
            if (string.IsNullOrWhiteSpace(project)) { Publish(plan, null, "Google has not returned a quota project. Open a Gemini chat, then refresh."); return; }
            var quota = await Post("retrieveUserQuota", token, new JsonObject { ["project"] = project });
            var windows = Parse(quota);
            Publish(plan, windows, windows.Count > 0 ? "" : "Google did not return quota limits for this account.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        { Publish(null, null, "Gemini quota is temporarily unavailable. Previous readings may be out of date."); }
        finally { Interlocked.Exchange(ref _fetching, 0); }
    }

    private async Task<JsonNode?> Post(string method, string token, JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://cloudcode-pa.googleapis.com/v1internal:{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    internal static IReadOnlyList<GeminiQuotaWindow> Parse(JsonNode? quota)
    {
        var windows = new List<GeminiQuotaWindow>();
        foreach (var bucket in (quota?["buckets"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (bucket["modelId"]?.ToString() is not { Length: > 0 } model
                || !double.TryParse(bucket["remainingFraction"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
                || !double.IsFinite(fraction)) continue;
            long? remaining = long.TryParse(bucket["remainingAmount"]?.ToString(), out var amount) && amount >= 0 ? amount : null;
            DateTimeOffset? reset = DateTimeOffset.TryParse(bucket["resetTime"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
            windows.Add(new(model, Math.Clamp((1 - fraction) * 100, 0, 100), remaining, reset));
        }
        // Model buckets may share a pool. Keep them separate rather than adding them into a fictitious allowance.
        return windows;
    }

    private void Publish(string? plan, IReadOnlyList<GeminiQuotaWindow>? limits, string status)
    {
        void Apply()
        {
            if (plan is not null) Plan = plan;
            if (limits is not null)
            {
                Limits.Clear(); foreach (var limit in limits) Limits.Add(limit);
                UpdatedText = $"As of {DateTime.Now:t}";
            }
            Status = status;
            Raise(nameof(HasData)); Raise(nameof(Summary)); Raise(nameof(Detail));
        }
        if (Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(Apply); else Apply();
    }
}
