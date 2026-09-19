using System.Text.Json.Nodes;

namespace VibeCode.Protocol;

/// <summary>
/// GPT Reserve is a model backed by the separate base_model_inference allowance, not a banked reset or
/// workspace credit. Only use it when this account advertises the model and both quota buckets confirm eligibility.
/// </summary>
internal static class CodexReserveFallback
{
    internal const string ModelId = "gpt-reserve";
    internal const string LimitId = "base_model_inference";

    internal static bool IsUsageLimitError(JsonNode? error) => error is JsonObject obj
        && Text(obj["codexErrorInfo"] ?? (obj["data"] as JsonObject)?["codexErrorInfo"]) == "usageLimitExceeded";

    internal static bool CanActivate(JsonNode? result)
    {
        if (result is not JsonObject) return false;
        var main = Bucket(result, "codex");
        var reserve = Bucket(result, LimitId);
        if (main is null || reserve is null) return false;
        var mainStatus = Text(main["rateLimitReachedType"]);
        // Workspace spending caps/credit depletion are not an invitation to change models.
        if (mainStatus is not (null or "" or "rate_limit_reached")
            || (main["rateLimitReachedType"] is not null && mainStatus is null)
            || (reserve["rateLimitReachedType"] is not null && Text(reserve["rateLimitReachedType"]) is null)
            || Number((main["individualLimit"] as JsonObject)?["remainingPercent"]) is <= 0
            || Number((reserve["individualLimit"] as JsonObject)?["remainingPercent"]) is <= 0
            || new[] { main["primary"], main["secondary"], reserve["primary"], reserve["secondary"] }
                .Any(w => w is not (null or JsonObject))
            || !string.IsNullOrEmpty(Text(reserve["rateLimitReachedType"]))) return false;
        var mainFull = mainStatus == "rate_limit_reached"
                       || Windows(main).Any(w => Number(w["usedPercent"]) is >= 100);
        var reserveWindows = Windows(reserve).ToArray();
        return mainFull && reserveWindows.Length > 0
                       && reserveWindows.All(w => Number(w["usedPercent"]) is >= 0 and < 100);
    }

    internal static JsonObject? FindModel(JsonArray? catalog) => catalog?.OfType<JsonObject>()
        .FirstOrDefault(m => Text(m["id"]) == ModelId || Text(m["model"]) == ModelId)?.DeepClone().AsObject();

    internal static string? Effort(JsonObject model, string? requested)
    {
        var supported = (model["supportedReasoningEfforts"] as JsonArray)?.OfType<JsonObject>()
            .Select(e => Text(e["reasoningEffort"])).OfType<string>().ToArray() ?? [];
        if (requested is null || supported.Contains(requested, StringComparer.OrdinalIgnoreCase)) return requested;
        // For example Astra's ultra is not offered by Reserve: step down to its strongest supported effort.
        string[] ladder = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
        var rank = Array.FindIndex(ladder, e => e.Equals(requested, StringComparison.OrdinalIgnoreCase));
        return ladder.Take(Math.Max(0, rank)).LastOrDefault(e => supported.Contains(e, StringComparer.OrdinalIgnoreCase))
               ?? supported.FirstOrDefault(e => e == Text(model["defaultReasoningEffort"]))
               ?? supported.FirstOrDefault();
    }

    private static JsonObject? Bucket(JsonNode? result, string id)
    {
        if (result?["rateLimitsByLimitId"] is JsonObject buckets && buckets[id] is JsonObject found
            && (Text(found["limitId"]) is null || Text(found["limitId"]) == id)) return found;
        return result?["rateLimits"] is JsonObject main && (Text(main["limitId"]) == id
            || (main["limitId"] is null && id == "codex")) ? main : null;
    }

    private static IEnumerable<JsonObject> Windows(JsonObject bucket)
    {
        if (bucket["primary"] is JsonObject primary) yield return primary;
        if (bucket["secondary"] is JsonObject secondary) yield return secondary;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static double? Number(JsonNode? node) => node is JsonValue value
        && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
}
