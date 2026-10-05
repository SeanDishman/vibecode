using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.Protocol;
using VibeCode.UI;

namespace VibeCode.Services;

public sealed record GlmUsageLimit(string Label, string ShortLabel, double Percent, DateTimeOffset? ResetsAt)
{
    public string PercentDisplay => $"{Percent:0.#}% used";
    public string ResetDisplay => ResetsAt is { } reset
        ? $"Resets {reset.ToLocalTime():ddd, MMM d 'at' h:mm tt}"
        : "Reset time not reported";
}

public sealed record GlmUsageSnapshot(IReadOnlyList<GlmUsageLimit> Limits, string Plan);

/// <summary>Per-account quota state. Never persisted with the encrypted credential.</summary>
public sealed class GlmAccountUsage(string backend) : Observable
{
    private readonly object _gate = new();
    private DateTimeOffset _lastAttempt;
    private bool _refreshing;
    public bool IsCodingPlan { get; } = backend == GlmPreset.ZaiCodingPlan;
    public IReadOnlyList<GlmUsageLimit> Limits { get; private set; } = [];
    public string Plan { get; private set; } = "";
    public DateTimeOffset? UpdatedAt { get; private set; }
    public bool IsStale { get; private set; }
    public bool IsRefreshing => _refreshing;
    public bool CanRefresh => IsCodingPlan && !IsRefreshing;
    public bool HasData => Limits.Count > 0;
    public string Status { get; private set; } = backend == GlmPreset.ZaiCodingPlan
        ? "Usage has not loaded yet. Choose Refresh to check."
        : "Pay-per-token account. Token usage is shown in each chat.";
    public string Summary => HasData
        ? string.Join(" · ", Limits.Select(l => $"{l.ShortLabel} {l.Percent:0.#}%")) + (IsStale ? " · stale" : "")
        : IsRefreshing ? "loading usage…" : IsCodingPlan ? "usage unavailable" : "pay per token";
    public string UpdatedDisplay => IsRefreshing ? "Refreshing…"
        : UpdatedAt is { } at ? $"{(IsStale ? "Last received" : "Updated")} {at.ToLocalTime():h:mm tt}" : "";
    public string Detail => string.Join("\n", Limits.Select(l => $"{l.Label}: {l.PercentDisplay}. {l.ResetDisplay}."))
        + (Status.Length > 0 ? (HasData ? "\n" : "") + Status : "");

    internal bool TryBegin(bool force)
    {
        lock (_gate)
        {
            if (!IsCodingPlan || _refreshing || (!force && DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromMinutes(1)))
                return false;
            _refreshing = true;
            _lastAttempt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    internal void Notify() { Raise(nameof(Limits)); Raise(nameof(Plan)); Raise(nameof(Summary)); Raise(nameof(Status));
        Raise(nameof(Detail)); Raise(nameof(HasData)); Raise(nameof(IsStale)); Raise(nameof(IsRefreshing));
        Raise(nameof(CanRefresh)); Raise(nameof(UpdatedDisplay)); }

    internal void Complete(GlmUsageSnapshot? snapshot, string error)
    {
        if (snapshot is not null)
        {
            Limits = snapshot.Limits;
            Plan = snapshot.Plan;
            UpdatedAt = DateTimeOffset.UtcNow;
            IsStale = false;
        }
        else IsStale = HasData;
        Status = error;
        lock (_gate) _refreshing = false;
        Notify();
    }
}

/// <summary>
/// Reads Z.ai's own monitor endpoint, also used by zai-org/zai-coding-plugins/glm-plan-usage.
/// Its Authorization header is the raw account key, unlike chat completions' Bearer scheme.
/// Both legacy TOKENS_LIMIT and current CREDIT_LIMIT windows are supported.
/// </summary>
public sealed class GlmUsageService
{
    public const string QuotaUrl = "https://api.z.ai/api/monitor/usage/quota/limit";
    public static GlmUsageService Instance { get; } = new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) });
    private readonly HttpClient _http;
    public GlmUsageService(HttpClient http) => _http = http;

    public async Task RefreshAsync(ApiKeyAccount account, bool force = false)
    {
        if (!GlmPreset.Is(account.Provider) || !account.GlmUsage.TryBegin(force)) return;
        await OnUiAsync(account.GlmUsage.Notify);
        GlmUsageSnapshot? snapshot = null;
        string error;
        try
        {
            var key = ApiKeyAccountService.Reveal(account);
            if (string.IsNullOrWhiteSpace(key)) error = "The saved key is unavailable. Reconnect this Z.ai account.";
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, QuotaUrl);
                request.Headers.Add("Authorization", key);
                request.Headers.Add("Accept-Language", "en-US,en");
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await _http.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    error = response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Z.ai rejected this usage check. Reconnect the Coding Plan account.",
                        HttpStatusCode.TooManyRequests => "Z.ai rate limited the usage check. Try Refresh in a moment.",
                        _ => $"Could not load Z.ai usage (HTTP {(int)response.StatusCode}). Try Refresh.",
                    };
                else
                {
                    snapshot = Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    error = snapshot.Limits.Count == 0 ? "Z.ai reported no quota windows for this account." : "";
                }
            }
        }
        catch (OperationCanceledException) { error = "The Z.ai usage check timed out. Try Refresh."; }
        catch (HttpRequestException) { error = "Could not reach Z.ai. Check your connection and try Refresh."; }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { error = "Z.ai returned an unrecognized usage response. Try Refresh or check your account on z.ai."; }
        // No provider bodies or credentials are logged or exposed in transport errors.
        await OnUiAsync(() => account.GlmUsage.Complete(snapshot, error));
    }

    private static async Task OnUiAsync(Action action)
    {
        var ui = Application.Current?.Dispatcher;
        if (ui is null || ui.CheckAccess()) action();
        else if (!ui.HasShutdownStarted) await ui.InvokeAsync(action);
    }

    public static GlmUsageSnapshot Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Expected quota object.");
        if (root["success"]?.ToString().Equals("false", StringComparison.OrdinalIgnoreCase) == true
            || root["code"] is { } code && code.ToString() is not ("0" or "200"))
            throw new FormatException("Provider rejected the quota query.");
        var data = root["data"] as JsonObject ?? root;
        var entries = data["limits"] as JsonArray ?? throw new FormatException("Missing quota limits.");
        var limits = new List<GlmUsageLimit>();
        foreach (var item in entries.OfType<JsonObject>())
        {
            var type = item["type"]?.ToString();
            if (type is not ("TOKENS_LIMIT" or "CREDIT_LIMIT" or "TIME_LIMIT")) continue;
            var percent = Number(item["percentage"]);
            if (percent is null && Number(item["usage"]) is > 0 and var total
                && Number(item["currentValue"]) is >= 0 and var used)
                percent = used / total * 100;
            if (percent is null or < 0 or > 100) throw new FormatException("Missing or invalid quota percentage.");
            var unit = Number(item["unit"]);
            var count = Number(item["number"]);
            var (label, shortLabel) = unit switch
            {
                3 when count is > 0 => ($"{count:0}-hour allowance", $"{count:0}h"),
                6 => ("Weekly allowance", "week"),
                5 => ("Monthly allowance", "month"),
                _ when type == "TOKENS_LIMIT" => ("5-hour allowance", "5h"),
                _ when type == "TIME_LIMIT" => ("Monthly tool allowance", "tools"),
                _ => ("Plan allowance", "plan"),
            };
            if (type == "TIME_LIMIT") { label = "Tool allowance"; shortLabel = "tools"; }
            limits.Add(new(label, shortLabel, percent.Value, ResetTime(item["nextResetTime"])));
        }
        if (entries.Count > 0 && limits.Count == 0) throw new FormatException("No recognized quota windows.");
        var plan = data["level"] is JsonValue level && level.TryGetValue<string>(out var value) ? value : "";
        return new(limits, plan);
    }

    private static double? Number(JsonNode? value) => double.TryParse(value?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static DateTimeOffset? ResetTime(JsonNode? value)
    {
        if (Number(value) is > 0 and var timestamp)
        {
            try { return timestamp > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp)
                : DateTimeOffset.FromUnixTimeSeconds((long)timestamp); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return DateTimeOffset.TryParse(value?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
