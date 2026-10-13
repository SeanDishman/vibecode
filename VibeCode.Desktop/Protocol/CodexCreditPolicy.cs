using System.Globalization;
using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>ChatGPT credits extend included Codex usage on the same authenticated account.
/// OpenAI applies the credits; the client must not treat a full subscription window as a spending cap.</summary>
internal static class CodexCreditPolicy
{
    internal static JsonObject? MainBucket(JsonNode? result)
    {
        if (result is not JsonObject response) return null;
        if (response["rateLimitsByLimitId"] is JsonObject buckets && buckets.ContainsKey("codex"))
            return buckets["codex"] is JsonObject bucket && IsMainBucket(bucket) ? bucket : null;
        return response["rateLimits"] is JsonObject single && IsMainBucket(single) ? single : null;
    }

    internal static bool IsBlockingStatus(string? status) => status is not (null or "" or "rate_limit_reached");

    internal static bool HasHardLimit(JsonObject? bucket) => bucket is not null
        && (IsBlockingStatus(Text(bucket["rateLimitReachedType"]))
            || bucket["rateLimitReachedType"] is not null && Text(bucket["rateLimitReachedType"]) is null
            || (bucket["individualLimit"] is JsonObject spend && Number(spend["remainingPercent"]) is <= 0));

    internal static bool CanUseCredits(JsonNode? result)
    {
        var bucket = MainBucket(result);
        if (bucket is null || HasHardLimit(bucket)
            || result is JsonObject response && HasHardLimit(response["rateLimits"] as JsonObject)) return false;
        return bucket["credits"] is JsonObject credits
            && (IsTrue(credits["hasCredits"]) || IsTrue(credits["unlimited"]));
    }

    private static bool IsTrue(JsonNode? value) => value is JsonValue json && json.TryGetValue<bool>(out var flag) && flag;
    private static bool IsMainBucket(JsonObject bucket) => bucket["limitId"] is null || Text(bucket["limitId"]) == "codex";
    private static string? Text(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
    private static double? Number(JsonNode? value) => double.TryParse(value?.ToString(), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
}
