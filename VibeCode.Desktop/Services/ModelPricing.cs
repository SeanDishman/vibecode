namespace VibeCode.Services;

/// <summary>
/// Anthropic and OpenAI API list prices in USD per 1,000,000 tokens (input / output), used to ESTIMATE a chat's
/// equivalent-API cost from its token usage. Prices are the public list prices as of July 2026.
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
    /// than the 0.10x every other Anthropic model uses. Taken from the pricing tier the CLI itself carries
    /// (<c>tier_10_50_cache_read_0_25</c>: input 10, output 50, cache_write_5m 12.5, cache_read 0.25), not a docs
    /// page - the docs model table still lists Fable 5 only.</summary>
    public const double CheapCacheReadMultiplier = 0.025;

    // input / output USD per 1M tokens. Keyed by the resolved model id with any "[1m]"-style variant tag stripped.
    private static readonly IReadOnlyDictionary<string, Price> Table = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5"]    = new(10.00, 50.00),
        ["claude-mythos-5"]   = new(10.00, 50.00),
        // 5.1 (2026-09): same $10/$50 base as 5, but a quarter the cache-read rate - see CheapCacheReadMultiplier.
        ["claude-fable-5-1"]  = new(10.00, 50.00),
        ["claude-mythos-5-1"] = new(10.00, 50.00),
        // Opus 5 (2026-07-24): near-Fable agentic coding at Opus list price; same $5/$25 as 4.x Opus.
        ["claude-opus-5"]     = new(5.00, 25.00),
        ["claude-opus-4-8"]   = new(5.00, 25.00),
        ["claude-opus-4-7"]   = new(5.00, 25.00),
        ["claude-opus-4-6"]   = new(5.00, 25.00),
        ["claude-opus-4-5"]   = new(5.00, 25.00),
        ["claude-sonnet-5"]   = new(3.00, 15.00),
        ["claude-sonnet-4-6"] = new(3.00, 15.00),
        ["claude-sonnet-4-5"] = new(3.00, 15.00),
        ["claude-haiku-4-5"]  = new(1.00, 5.00),

        // OpenAI standard processing rates. The GPT-5.6 cache-write prices are 1.25x input and
        // cached-input prices are 0.10x input, matching the multipliers used by TurnCost.
        // GPT-6 Astra (2026-09-03) is 2.5x Sol and lands on the same $10/$50 as the Anthropic frontier pair. Its
        // published $1 cached-input rate is 0.10x input, so CacheReadMultiplier already covers it. NOT modelled:
        // OpenAI prices a request whose input passes 272K tokens at 2x input/cache and 1.5x output for the WHOLE
        // request. The Codex CLI gives Astra a 272K window by default, so that surcharge only becomes reachable
        // on an explicitly enlarged context - past which this estimate reads low.
        ["gpt-6-astra"]        = new(10.00, 50.00),
        ["gpt-5.6-sol"]        = new(5.00, 30.00),
        ["gpt-5.6-terra"]      = new(2.50, 15.00),
        ["gpt-5.6-luna"]       = new(1.00, 6.00),
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
    public static double TurnCost(string? modelId, double input, double cacheWrite, double cacheRead, double output)
    {
        var id = Strip(modelId);
        var p = For(modelId);
        var isKimi = id.Equals("k3", StringComparison.OrdinalIgnoreCase)
                     || id.StartsWith("kimi", StringComparison.OrdinalIgnoreCase);
        // Baseten publishes no cache-write price for GLM at all, so a write is charged as ordinary input - the
        // same shape as Kimi's automatic cache rather than Anthropic's paid-write one.
        var isGlm = id.StartsWith("zai-org/", StringComparison.OrdinalIgnoreCase);
        var cacheWriteMultiplier = isKimi || isGlm ? 1.0 : CacheWriteMultiplier;
        // The 5.1 pair is the only Anthropic tier that moves cache_read off 0.10x input; cache writes are
        // unchanged, so this is the read multiplier alone rather than a whole alternate price shape.
        var isCheapCacheRead = id.Equals("claude-fable-5-1", StringComparison.OrdinalIgnoreCase)
                               || id.Equals("claude-mythos-5-1", StringComparison.OrdinalIgnoreCase);
        var cacheReadMultiplier = isCheapCacheRead
            ? CheapCacheReadMultiplier
            : isKimi && (id.Contains("k2.7", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("for-coding", StringComparison.OrdinalIgnoreCase))
                ? 0.20
                // GLM's cache-hit rate is per model, read from the endpoint: 0.20x input on 4.7 and 5.3 Flash,
                // 0.10x on the 5.2 pair (which is what CacheReadMultiplier already is).
                : isGlm && (id.Contains("glm-4.7", StringComparison.OrdinalIgnoreCase)
                            || id.Contains("glm-5.3", StringComparison.OrdinalIgnoreCase))
                    ? 0.20
                    : CacheReadMultiplier;
        double inputUsd = (input + cacheWrite * cacheWriteMultiplier + cacheRead * cacheReadMultiplier) * p.InputPerMTok;
        double outputUsd = output * p.OutputPerMTok;
        return (inputUsd + outputUsd) / 1_000_000.0;
    }

    private static string Strip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('[');
        return (i >= 0 ? s[..i] : s).Trim();
    }
}
