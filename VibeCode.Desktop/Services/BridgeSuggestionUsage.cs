using System.Globalization;
using System.Text.Json.Nodes;

namespace VibeCode.Services;

/// <summary>Commits usage from one isolated team-size planning turn, including an unusable answer.</summary>
internal sealed class BridgeSuggestionUsage(string provider, string? selectedModel, string project)
{
    private int _recorded;

    public void Record(JsonNode result, string? sessionId, JsonArray models)
    {
        var usage = result["usage"];
        if (Total(usage) <= 0) usage = result["session_usage"]; // This helper always starts a fresh session.
        if (Total(usage) <= 0 || Interlocked.Exchange(ref _recorded, 1) != 0) return;

        var input = Number(usage, "input_tokens");
        var cacheWrite = Number(usage, "cache_creation_input_tokens");
        var cacheRead = Number(usage, "cache_read_input_tokens");
        var output = Number(usage, "output_tokens");
        var model = Text(result["model"]);
        if (string.IsNullOrWhiteSpace(model) && result["modelUsage"] is JsonObject { Count: 1 } byModel)
            model = byModel.First().Key;
        if (string.IsNullOrWhiteSpace(model)) model = selectedModel;
        if (string.IsNullOrWhiteSpace(model) || model == "default")
            model = models.OfType<JsonObject>().Where(m => Text(m["value"]) == "default").Select(m => Text(m["resolvedModel"]))
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m) && m != "default");
        var providerCost = UsagePricing.Number(result["total_cost_usd"]);
        var reported = providerCost is > 0 || (provider == "grok" && providerCost is not null);
        var cost = reported ? Number(result, "total_cost_usd")
            : provider == "grok" ? 0 : UsagePricing.Estimate(model, usage, UsagePricing.Tier(result));
        UsageLog.Instance.Record(provider, model, input, cacheWrite, cacheRead, output,
            cost, reported, sessionId, project);
    }

    private static double Total(JsonNode? usage) => Number(usage, "input_tokens")
        + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "output_tokens");

    private static string? Text(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;

    private static double Number(JsonNode? node, string field) =>
        node?[field] is JsonValue value && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? Math.Max(0, number) : 0;
}
