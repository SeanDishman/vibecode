using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private string? _costModelForTurn;
    private string? _costTierForTurn;
    private double? _liveCostEstimate;
    private readonly Dictionary<string, MessagePricing> _messagePricing = new(StringComparer.Ordinal);
    private sealed record MessagePricing(string? Model, string? Tier, double CacheWrite1h);

    private string? CostModel => _costModelForTurn ?? CurrentModel?.ResolvedModel ?? _model;

    // The provider's actual model can differ from a saved alias or dispatch fallback. Prefer observed identity
    // when every request agrees; mixed-model turns retain the dispatched model until a complete breakdown exists.
    private string? UsageModel
    {
        get
        {
            var models = _messagePricing.Where(pair => _liveUsageByMessage.TryGetValue(pair.Key, out var usage) && usage.HasTokens)
                .Select(pair => pair.Value.Model).OfType<string>().Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(ModelPricing.CanonicalId).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return models.Length == 1 ? models[0] : CostModel;
        }
    }

    // Capture at dispatch: changing the picker while a turn runs affects the NEXT turn's fallback price.
    private void BeginTurnPricing()
    {
        _unfinishedUsageRecorded = false;
        _costModelForTurn = CurrentModel?.ResolvedModel ?? _model;
        _costTierForTurn = EffectiveFastMode && (IsClaude || IsCodex) ? "fast" : "standard";
    }

    private double LiveEstimatedCost => IsGrok ? 0 : _liveCostEstimate ?? ModelPricing.TurnCost(CostModel,
        _liveTurnUsage.Input, _liveTurnUsage.CacheWrite, _liveTurnUsage.CacheRead, _liveTurnUsage.Output, _costTierForTurn);

    private void CaptureMessagePricing(string id, JsonNode? message, JsonNode? usage)
    {
        _messagePricing.TryGetValue(id, out var old);
        _messagePricing[id] = new(UsagePricing.Text(message?["model"]) ?? old?.Model ?? CostModel,
            UsagePricing.Tier(usage) ?? old?.Tier ?? _costTierForTurn,
            UsagePricing.Number(usage?["cache_creation"]?["ephemeral_1h_input_tokens"]) ?? old?.CacheWrite1h ?? 0);
    }

    private double MessageCost(string id, LiveUsage usage)
    {
        _messagePricing.TryGetValue(id, out var pricing);
        return ModelPricing.TurnCost(pricing?.Model ?? CostModel, usage.Input, usage.CacheWrite, usage.CacheRead,
            usage.Output, pricing?.Tier ?? _costTierForTurn, usage.TotalIn, pricing?.CacheWrite1h ?? 0);
    }

    private double CompletedEstimate(JsonNode usage)
    {
        if (UsagePricing.Number(usage["estimated_cost_usd"]) is { } normalized) return normalized;
        // Claude message IDs de-duplicate streamed/final copies; per-message pricing retains mixed models,
        // actual speed and cache TTL. Use it only when it accounts for the authoritative final token total.
        if (IsClaude && _liveCostEstimate is { } cost && _liveTurnUsage == UsageOf(usage)) return cost;
        return UsagePricing.Estimate(CostModel, usage, _costTierForTurn);
    }
}
