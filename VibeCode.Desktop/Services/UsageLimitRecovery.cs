using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeCode.Services;

/// <summary>Only fresh, account-matched readings can positively establish available allowance.</summary>
public sealed record UsageRecoverySnapshot(bool? Available, DateTimeOffset? ResetsAt = null)
{
    public static UsageRecoverySnapshot Unknown { get; } = new((bool?)null);

    public static UsageRecoverySnapshot FromWindows(IEnumerable<(double Percent, DateTimeOffset? Reset)> windows)
    {
        var all = windows.ToArray();
        if (all.Length == 0 || all.Any(w => !double.IsFinite(w.Percent) || w.Percent < 0)) return Unknown;
        var exhausted = all.Where(w => w.Percent >= 100).ToArray();
        // Both a session and a weekly cap can block a request; the first reset alone is insufficient.
        return new(exhausted.Length == 0,
            exhausted.Length == 0 || exhausted.Any(w => w.Reset is null) ? null : exhausted.Max(w => w.Reset));
    }
}

public static partial class UsageLimitRecovery
{
    public static bool IsLimitError(JsonNode result)
    {
        if (result["is_error"]?.ToString() != "true") return false;
        if (result["provider_error"] is JsonObject provider && (provider["status"]?.ToString() == "429"
            || provider["usage_limit"]?.ToString() == "true")) return true;
        var text = string.Join(" ", new[] { "subtype", "result", "errors", "provider_error" }
            .Select(key => result[key]?.ToString())).ToLowerInvariant();
        // Do not confuse a context limit, invalid credentials, or arbitrary tool failures with account quota.
        return text.Contains("usagelimitexceeded") || text.Contains("rate_limit") || text.Contains("rate limit")
            || text.Contains("rate-limit") || text.Contains("usage limit") || text.Contains("usage cap")
            || text.Contains("usage exhausted") || text.Contains("out of usage") || text.Contains("hit your limit")
            // Claude Code 2.1 names the window: "You've hit your session limit · resets 5:40pm (America/Chicago)",
            // and likewise weekly, Opus, Sonnet, Fable, usage credit and org monthly spend limits.
            || Regex.IsMatch(text, @"\bhit your\b(?! context)[^.\r\n\xB7]{0,40}?\blimit\b")
            || text.Contains("shared budget")
            || text.Contains("quota") || text.Contains("resource_exhausted")
            || text.Contains("resource exhausted") || text.Contains("too many requests")
            || text.Contains("credits depleted") || text.Contains("credit limit")
            || text.Contains("concurrent request limit") || text.Contains("billing cycle")
            || Regex.IsMatch(text, @"(?:http|status|code)\D{0,8}429\b");
    }

    public static DateTimeOffset? RetryAt(JsonNode result, DateTimeOffset now)
    {
        var error = result["provider_error"] as JsonObject;
        foreach (var node in new[] { result, error, error?["data"] })
        {
            if (node is not JsonObject obj) continue;
            foreach (var key in new[] { "retry_after_seconds", "retryAfterSeconds", "retry_after" })
                if (Number(obj[key]) is > 0 and var seconds && seconds < 366 * 86400)
                    return now.AddSeconds(seconds);
            foreach (var key in new[] { "resetsAt", "reset_at", "retry_at" })
                if (ParseReset(obj[key]?.ToString(), now) is { } reset && reset > now) return reset;
        }
        var text = result["result"]?.ToString() ?? "";
        var delay = Regex.Match(text, @"(?:retry|try again)\s+(?:after|in)\s+(\d+(?:\.\d+)?)\s*(seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\b", RegexOptions.IgnoreCase);
        if (delay.Success && double.TryParse(delay.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n < 1_000_000)
        {
            var unit = delay.Groups[2].Value.ToLowerInvariant()[0];
            return now.AddSeconds(n * (unit == 'h' ? 3600 : unit == 'm' ? 60 : 1));
        }
        // Stop at the next " · " part so "resets 5:40pm (America/Chicago) · progress saved" still parses.
        var stamp = Regex.Match(text, @"\bresets?\s+([^\r\n\xB7]+)", RegexOptions.IgnoreCase);
        return stamp.Success ? ParseReset(stamp.Groups[1].Value.Trim().TrimEnd('.'), now) : null;
    }

    public static DateTimeOffset? ParseReset(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (long.TryParse(raw, out var unix) && unix is > 1_000_000_000 and < 100_000_000_000)
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        var text = Regex.Replace(raw.Trim(), @"\s*\([^)]*\)\s*$", "").Trim();
        string[] formats = ["MMM d, h:mmtt yyyy", "MMM d, h:mm tt yyyy", "MMM d, htt yyyy", "MMM d, h tt yyyy",
            "MMMM d, h:mmtt yyyy", "MMMM d, h:mm tt yyyy", "MMMM d, htt yyyy", "MMMM d, h tt yyyy"];
        DateTime local;
        if (DateTime.TryParseExact(text + " " + now.LocalDateTime.Year, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out local))
        {
            if (local.Month == 1 && now.LocalDateTime.Month == 12) local = local.AddYears(1);
            return new DateTimeOffset(local);
        }
        if (DateTime.TryParseExact(text, ["htt", "h tt", "h:mmtt", "h:mm tt", "H:mm"], CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out local))
        {
            local = now.LocalDateTime.Date + local.TimeOfDay;
            if (local < now.LocalDateTime) local = local.AddDays(1);
            return new DateTimeOffset(local);
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date : null;
    }

    public static UsageRecoverySnapshot CodexSnapshot(JsonNode? result, string? model)
    {
        if (result is not JsonObject) return UsageRecoverySnapshot.Unknown;
        var id = model == "gpt-reserve" ? "base_model_inference" : "codex";
        var map = result["rateLimitsByLimitId"] as JsonObject;
        var main = result["rateLimits"] as JsonObject;
        var bucket = map?[id] as JsonObject ?? (main?["limitId"]?.ToString() == id
            || main?["limitId"] is null && id == "codex" ? main : null);
        if (bucket is null) return UsageRecoverySnapshot.Unknown;
        var windows = new[] { bucket["primary"], bucket["secondary"] }.OfType<JsonObject>()
            .Where(w => Number(w["usedPercent"]) is not null)
            .Select(w => (Number(w["usedPercent"])!.Value, ParseReset(w["resetsAt"]?.ToString(), DateTimeOffset.Now)));
        var snapshot = UsageRecoverySnapshot.FromWindows(windows);
        if (!string.IsNullOrEmpty(bucket["rateLimitReachedType"]?.ToString())
            || Number((bucket["individualLimit"] as JsonObject)?["remainingPercent"]) is <= 0
            || main?["rateLimitReachedType"]?.ToString() is "workspace_owner_credits_depleted" or "workspace_member_credits_depleted"
                or "workspace_owner_usage_limit_reached" or "workspace_member_usage_limit_reached")
            return snapshot with { Available = false };
        return snapshot;
    }

    private static double? Number(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}
