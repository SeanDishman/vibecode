using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.Protocol;

public sealed partial class CodexSession
{
    private sealed record CostProfile(string? Model, string? Tier);
    private readonly Dictionary<string, CostProfile> _costProfiles = new(StringComparer.Ordinal);
    private double _turnEstimatedCost;

    private void RememberTurnPricing(JsonObject request)
    {
        var thread = SafeString(request["threadId"]) ?? SessionId ?? "root";
        _costProfiles[thread] = new(SafeString(request["model"]) ?? ApiModel(_model), UsagePricing.Tier(request));
    }

    private void ObserveTurnPricing(string? threadId, JsonObject? envelope)
    {
        var thread = threadId ?? SessionId ?? "root";
        var previous = _costProfiles.GetValueOrDefault(thread) ?? _costProfiles.GetValueOrDefault(SessionId ?? "root");
        var details = envelope?["turn"] as JsonObject;
        _costProfiles[thread] = new(SafeString(details?["model"] ?? envelope?["model"]) ?? previous?.Model ?? ApiModel(_model),
            UsagePricing.Tier(details) ?? UsagePricing.Tier(envelope) ?? previous?.Tier
            ?? (_fastMode && KnownFastModel(_model) ? "priority" : "default"));
    }

    private void AddRequestCost(JsonObject? envelope, JsonNode usage, string thread, TokenUsage increment, TokenUsage latest)
    {
        if (!increment.HasTokens) return;
        var profile = _costProfiles.GetValueOrDefault(thread)
            ?? _costProfiles.GetValueOrDefault(SessionId ?? "root")
            ?? new CostProfile(ApiModel(_model), _fastMode && KnownFastModel(_model) ? "priority" : "default");
        var model = SafeString(usage["last"]?["model"] ?? usage["model"] ?? envelope?["model"]) ?? profile.Model;
        // Returned metadata wins over requested speed, including an explicit downgrade to standard.
        var tier = UsagePricing.Tier(usage["last"]) ?? UsagePricing.Tier(usage)
            ?? UsagePricing.Tier(envelope) ?? profile.Tier;
        // Missing intermediate notifications may combine multiple requests in the increment. Only apply a
        // context surcharge when the increment actually describes the one request whose size was reported.
        double? context = increment.Input == latest.Input ? latest.Input : null;
        _turnEstimatedCost += ModelPricing.TurnCost(model, Math.Max(0, increment.Input - increment.CachedInput),
            0, increment.CachedInput, increment.Output, tier, context);
    }
}
