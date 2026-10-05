using System.Globalization;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Pricing metadata shared by provider adapters, chat totals, and background planning.</summary>
internal static class UsagePricing
{
    public static string? Tier(JsonNode? node) => Text(node?["speed"] ?? node?["service_tier"] ?? node?["serviceTier"]);
    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    public static double? Number(JsonNode? node) => node is JsonValue value
        && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number >= 0 ? number : null;

    public static double Estimate(string? model, JsonNode? usage, string? fallbackTier = null) =>
        Number(usage?["estimated_cost_usd"]) ?? ModelPricing.TurnCost(model,
            Number(usage?["input_tokens"]) ?? 0, Number(usage?["cache_creation_input_tokens"]) ?? 0,
            Number(usage?["cache_read_input_tokens"]) ?? 0, Number(usage?["output_tokens"]) ?? 0,
            Tier(usage) ?? fallbackTier, Number(usage?["context_input_tokens"]),
            Number(usage?["cache_creation"]?["ephemeral_1h_input_tokens"]) ?? 0);
}
