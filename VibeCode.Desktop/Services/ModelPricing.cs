namespace VibeCode.Services;

/// <summary>
/// Anthropic and OpenAI API list prices in USD per 1,000,000 tokens (input / output), used to ESTIMATE a chat's
/// equivalent-API cost from its token usage. Claude list prices were verified on October 1, 2026.
/// On a subscription login the CLI reports <c>total_cost_usd = 0</c>, so this estimate is what the UI
/// falls back to — it's the "money this chat is using" number, not a bill.
/// </summary>
public static class ModelPricing
{
    /// <summary>USD per 1M tokens for one model.</summary>
    public readonly record struct Price(double InputPerMTok, double OutputPerMTok);

    // Claude/OpenAI prompt caching uses these defaults. Kimi's automatic context cache has model-specific
    // cache-hit rates and no separate cache-write premium; TurnCost selects those rates by model id below.
    public const double CacheWriteMultiplier = 1.25;
    public const double CacheReadMultiplier = 0.10;

    /// <summary>Cache-read rate for the 5.1 frontier pair: $0.25 per Mtok against a $10 input, i.e. 0.025x rather
    /// than the usual 0.10x. Opus 5.5 has its own 0.05x rate.
    /// https://platform.claude.com/docs/en/about-claude/pricing</summary>
    public const double CheapCacheReadMultiplier = 0.025;

    // input / output USD per 1M tokens. Keyed by the resolved model id with any "[1m]"-style variant tag stripped.
    private static readonly IReadOnlyDictionary<string, Price> Table = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5"]    = new(10.00, 50.00),
        ["claude-mythos-5"]   = new(10.00, 50.00),
        // 5.1 (2026-09): same $10/$50 base as 5, but a quarter the cache-read rate - see CheapCacheReadMultiplier.
        ["claude-fable-5-1"]  = new(10.00, 50.00),
        ["claude-mythos-5-1"] = new(10.00, 50.00),
        // Current Claude lineup: https://platform.claude.com/docs/en/about-claude/pricing
        ["claude-opus-5-5"]   = new(4.00, 20.00),
        ["claude-sonnet-5-5"] = new(2.00, 10.00),
        // Earlier models retain their own rates; Sonnet 5 now shares the $2/$10 tier.
        ["claude-opus-5"]     = new(5.00, 25.00),
        ["claude-opus-4-8"]   = new(5.00, 25.00),
        ["claude-opus-4-7"]   = new(5.00, 25.00),
        ["claude-opus-4-6"]   = new(5.00, 25.00),
        ["claude-opus-4-5"]   = new(5.00, 25.00),
        ["claude-sonnet-5"]   = new(2.00, 10.00),
        ["claude-sonnet-4-6"] = new(3.00, 15.00),
        ["claude-sonnet-4-5"] = new(3.00, 15.00),
        ["claude-haiku-4-5"]  = new(1.00, 5.00),

        // OpenAI standard short-context rates, updated 2026-09-30. Cache-write is 1.25x input.
        // GPT-6.1 Sol's cache reads are 0.05x input; the other listed OpenAI models use 0.10x.
        // https://developers.openai.com/api/docs/pricing
        // GPT-5.6 Sol's $4/$20 is the promotional rate OpenAI says runs at least through 2026-11-21.
        // TurnCost applies the long-context rate only when an individual request's size is known.
        // A whole agentic turn's token total must never be mistaken for one request's context.
        ["gpt-6-astra"]        = new(10.00, 50.00),
        ["gpt-6.1-sol"]        = new(2.00, 10.00),
        ["gpt-6-sol"]          = new(2.00, 10.00),
        ["gpt-6-luna"]         = new(0.10, 0.50),
        ["gpt-5.6-sol"]        = new(4.00, 20.00),
        ["gpt-5.6-terra"]      = new(2.00, 12.00),
        ["gpt-5.6-luna"]       = new(0.20, 1.20),
        ["gpt-5.5"]            = new(5.00, 30.00),

        // Moonshot Kimi API. K3 cache-hit input is 10% of fresh input; K2.7's is 20%.
        ["k3"]                  = new(3.00, 15.00),
        ["kimi-k3"]             = new(3.00, 15.00),
        ["kimi-code/k3"]        = new(3.00, 15.00),
        ["kimi-k2.7-code"]      = new(0.95, 4.00),
        ["kimi-for-coding"]     = new(0.95, 4.00),
        ["kimi-code/kimi-for-coding"] = new(0.95, 4.00),
        ["kimi-k2.7-code-highspeed"] = new(1.90, 8.00),
        ["kimi-for-coding-highspeed"] = new(1.90, 8.00),
        ["kimi-code/kimi-for-coding-highspeed"] = new(1.90, 8.00),

        // GLM through Baseten. Read from the endpoint's own GET /v1/models pricing block rather than a docs page,
        // so these are the rates the account is actually billed at. Listed explicitly rather than left unlisted:
        // the fallback below is the Opus tier, which would have reported the cheapest model here as 33x its real
        // input cost.
        ["zai-org/glm-5.3-flash"] = new(0.15, 0.50),
        ["zai-org/glm-5.2"]       = new(1.40, 4.40),
        ["zai-org/glm-5.2-fast"]  = new(2.10, 6.60),
        ["zai-org/glm-4.7"]       = new(0.60, 2.20),
        // Official Z.ai list prices, 2026-09-25: https://docs.z.ai/guides/overview/pricing
        // As for other subscriptions, Coding Plan usage shows equivalent API cost, not a subscription charge.
        ["glm-5.3-flash"]         = new(0.15, 0.50),
        ["glm-5.3-flashx"]        = new(0.37, 1.25),
        ["glm-5.3"]               = new(1.40, 4.40),
    };

    /// <summary>Opus-tier fallback for a model we don't have a listed price for (the app defaults to Opus).</summary>
    private static readonly Price Fallback = new(5.00, 25.00);

    /// <summary>Look up a model's price, tolerant of a null id or a "[1m]"-style variant suffix.</summary>
    public static Price For(string? modelId)
    {
        var id = Strip(modelId);
        return id.Length > 0 && Table.TryGetValue(id, out var p) ? p : Fallback;
    }

    /// <summary>True when this exact model id is priced here rather than falling back to the Opus tier. The usage
    /// page uses it to mark an estimate it can't stand behind instead of quoting a confident number.</summary>
    public static bool IsPriced(string? modelId) => Table.ContainsKey(Strip(modelId));

    /// <summary>The id usage history is keyed by: variant tag dropped, trimmed, never null. Logging the raw id
    /// would split one model across several rows the first time a "[1m]" context variant is selected.</summary>
    public static string CanonicalId(string? modelId)
    {
        var id = Strip(modelId);
        return id.Length == 0 ? "unknown" : id;
    }

    /// <summary>
    /// USD cost of one usage snapshot. The three input buckets are priced at the selected model's fresh,
    /// cache-write, and cache-hit rates; output is priced at the output rate.
    /// </summary>
    public static double TurnCost(string? modelId, double input, double cacheWrite, double cacheRead, double output,
        string? serviceTier = null, double? contextInputTokens = null, double cacheWrite1h = 0)
    {
        var id = Strip(modelId);
        var p = For(modelId);
        var isKimi = id.Equals("k3", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("kimi", StringComparison.OrdinalIgnoreCase);
        // Baseten publishes no cache-write price for GLM at all, so a write is charged as ordinary input - the
        // same shape as Kimi's automatic cache rather than Anthropic's paid-write one.
        var isOfficialGlm = id.StartsWith("glm-", StringComparison.OrdinalIgnoreCase);
        var isGlm = isOfficialGlm || id.StartsWith("zai-org/", StringComparison.OrdinalIgnoreCase);
        var cacheWriteMultiplier = isKimi || isGlm ? 1.0 : CacheWriteMultiplier;
        // Frontier 5.1 reads cost $0.25/MTok; Opus 5.5 reads cost $0.20/MTok against $4 fresh input.
        var isCheapCacheRead = id.Equals("claude-fable-5-1", StringComparison.OrdinalIgnoreCase)
                               || id.Equals("claude-mythos-5-1", StringComparison.OrdinalIgnoreCase);
        var cacheReadMultiplier = isOfficialGlm ? id.ToLowerInvariant() switch
        {
            "glm-5.3" => 0.26 / 1.40,
            "glm-5.3-flashx" => 0.075 / 0.37,
            _ => 0.20,
        } : isCheapCacheRead
            ? CheapCacheReadMultiplier
            : id.Equals("claude-opus-5-5", StringComparison.OrdinalIgnoreCase)
                ? 0.05
            : id.Equals("gpt-6.1-sol", StringComparison.OrdinalIgnoreCase)
                ? 0.05
            : isKimi && (id.Contains("k2.7", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("for-coding", StringComparison.OrdinalIgnoreCase))
                ? 0.20
                // GLM's cache-hit rate is per model, read from the endpoint: 0.20x input on 4.7 and 5.3 Flash,
                // 0.10x on the 5.2 pair (which is what CacheReadMultiplier already is).
                : isGlm && (id.Contains("glm-4.7", StringComparison.OrdinalIgnoreCase)
                            || id.Contains("glm-5.3", StringComparison.OrdinalIgnoreCase))
                    ? 0.20
                    : CacheReadMultiplier;
        // Claude's 1h writes cost 2x fresh input instead of the 5m write rate (1.25x).
        var longWrites = id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(cacheWrite1h, 0, Math.Max(0, cacheWrite)) : 0;
        double inputUsd = (input + (cacheWrite - longWrites) * cacheWriteMultiplier + longWrites * 2
                          + cacheRead * cacheReadMultiplier) * p.InputPerMTok;
        double outputUsd = output * p.OutputPerMTok;
        if (contextInputTokens > 272_000 && (id.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("gpt-5.6-", StringComparison.OrdinalIgnoreCase) || id == "gpt-5.5"))
        {
            inputUsd *= 2;
            outputUsd *= 1.5;
        }
        return (inputUsd + outputUsd) * SpeedMultiplier(id, serviceTier) / 1_000_000.0;
    }

    /// <summary>API dollar rates, not subscription quota consumption. Verified 2026-10-02.
    /// https://developers.openai.com/api/docs/pricing
    /// https://platform.claude.com/docs/en/build-with-claude/fast-mode
    /// A model with a separately priced fast/highspeed id already includes its premium in Table.</summary>
    public static double SpeedMultiplier(string? modelId, string? serviceTier)
    {
        var id = Strip(modelId).ToLowerInvariant();
        var tier = serviceTier?.Trim().ToLowerInvariant();
        if (tier == "ultrafast" && id == "gpt-6-astra") return 6;
        if (tier is not ("fast" or "priority")) return 1;
        if (id == "gpt-5.5") return 2.5;
        return id is "claude-opus-5-5" or "claude-opus-5" or "claude-opus-4-8"
            or "gpt-6-astra" or "gpt-6.1-sol" or "gpt-6-sol" or "gpt-6-luna"
            or "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" ? 2 : 1;
    }

    private static string Strip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('[');
        var id = (i >= 0 ? s[..i] : s).Trim();
        // Claude Code resolves Haiku to its dated API snapshot. That is the same priced model as the alias.
        if (id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) && id.Length > 9
            && id[^9] == '-' && id[^8..].All(char.IsAsciiDigit) && Table.ContainsKey(id[..^9]))
            id = id[..^9];
        return id;
    }
}
