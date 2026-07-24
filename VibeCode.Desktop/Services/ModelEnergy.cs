namespace VibeCode.Services;

/// <summary>
/// Rough ELECTRICITY and WATER cost of the tokens on the usage page, the physical twin of
/// <see cref="ModelPricing"/>. Money answers "what are these tokens worth"; this answers "what did it take to
/// serve them".
///
/// The whole table is derived from one anchor and one number per model, so every figure here can be checked by
/// hand rather than taken on faith:
///
///   Wh per 1M output tokens = <see cref="WhPerMTokPerBillionActiveParams"/> x active parameters in billions
///
/// The anchor (4 Wh per 1M output tokens per billion active params, all-in) comes from two independent routes
/// that agree to within 7%:
///   - DeepSeek's published V3/R1 production numbers (2025-02-27, 1,814 H800s, 608B input / 168B output tokens
///     in 24h) work out to ~5.6 Wh per 1M output tokens per billion active params on Hopper.
///   - Epoch AI's GPT-4o estimate (0.3 Wh for a 500-token answer at ~100B active params) gives ~6.0.
///   - LMSYS' GB200 NVL72 measurements give ~1.1, i.e. Blackwell is several times better, so 4 is a deliberate
///     2026 midpoint rather than the Hopper figure.
/// "All-in" is the boundary Google publishes against: accelerator + host CPU/DRAM + idle provisioned capacity
/// + datacenter overhead. Chip-only numbers are roughly 2.4x smaller and are not what this shows.
///
/// The three token buckets are weighted the way <see cref="ModelPricing"/> weights them, because marginal
/// serving cost is mostly GPU-seconds:
///   - output costs ~5x an input token (identical FLOPs, but decoding runs at 5-15% utilisation against
///     prefill's 30-50%; measured production ratios land between 2x and 10x)
///   - a cache READ skips prefill entirely, ~0.1x an input token (DeepSeek's production split implies ~0.095)
///   - a cache WRITE is a prefill plus a store, ~1.25x
/// That last pair is why this app's numbers are not dominated by its 300M+ token counts: almost all of them are
/// cache reads.
///
/// Water is Google's formula - cooling water scales with the energy that reached the machines, not with the
/// datacenter overhead on top of it:  litres = (kWh - PUE overhead) x WUE.
///
/// HONESTY: the uncertainty here is a factor of 3-5x for any single model, and up to 20x across published
/// methodologies. The dominant unknown is active parameter count, which only Moonshot and DeepSeek publish;
/// every closed model below is a tier estimate. Efficiency also improves roughly 2-5x a year, so these
/// constants decay. Show one significant figure and call it an estimate.
///
/// Sources: Google/Elsworth et al., arXiv:2508.15734 (Aug 2025) - the 0.24 Wh median prompt, its breakdown and
/// WUE 1.15; Epoch AI, "How much energy does ChatGPT use?" (Feb 2025); Oviedo et al., Joule 2026
/// (arXiv:2509.20241) - median 0.31 Wh/query, and the finding that non-production assumptions overstate by
/// 4-20x; DeepSeek open-infra-index V3/R1 inference disclosure (Mar 2025); LMSYS GB200 NVL72 (Sep 2025);
/// LBNL 2024 US Data Center Energy Usage Report - off-site water.
/// </summary>
public static class ModelEnergy
{
    // ---------- the anchor ----------

    /// <summary>Watt-hours per 1M OUTPUT tokens for every billion active parameters, counting accelerator,
    /// host, idle capacity and datacenter overhead. See the class remarks for the two derivations.</summary>
    public const double WhPerMTokPerBillionActiveParams = 4.0;

    /// <summary>An input token against an output token. Same FLOPs; prefill just batches far better.</summary>
    public const double InputShareOfOutput = 0.20;

    /// <summary>A cache hit against a fresh input token. It skips prefill compute altogether.</summary>
    public const double CacheReadShareOfInput = 0.10;

    /// <summary>Writing the cache is a prefill plus a store.</summary>
    public const double CacheWriteShareOfInput = 1.25;

    // ---------- water ----------

    /// <summary>Litres of water consumed per kWh that reaches the machines - Google's fleet-wide WUE, ISO
    /// Category 2 (consumptive, not withdrawal). Hyperscalers using less evaporative cooling report far less:
    /// Microsoft 0.27, AWS 0.15. This is the conservative published figure.</summary>
    public const double OnSiteWaterLitresPerKWh = 1.1;

    /// <summary>Datacenter overhead. Cooling water tracks the energy delivered to the machines, so the water
    /// calculation divides this back out - the overhead is what the cooling IS.</summary>
    public const double DatacenterPue = 1.09;

    /// <summary>Litres consumed generating a kWh on the US grid, i.e. the water this never sees on the utility
    /// bill. LBNL's 2024 report puts US datacenter indirect water at ~4.5 L/kWh all-in; the IEA reckons off-site
    /// is about 60% of the total. Reported separately because leaving it out is the single most common
    /// criticism of published AI water figures.</summary>
    public const double GridWaterLitresPerKWh = 3.4;

    // ---------- per-model active parameters ----------

    /// <summary>
    /// Billions of ACTIVE parameters per token. Only Moonshot and DeepSeek publish this; everything else is a
    /// tier estimate, and the honest error bar is about 3x either way.
    ///
    /// Closed models are tiered by their own provider's output price, which is a fair proxy WITHIN a provider
    /// (marginal serving cost is mostly GPU-seconds) and a bad one across providers (margins differ wildly).
    /// The Claude tiers are anchored on Opus ~= 300B active; OpenAI on Epoch AI's GPT-4o estimate of ~100B
    /// active out of ~400B total.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> ActiveParamsB = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        // Claude. Frontier tier bills at 2x Opus, so it is carried at 2x Opus here too.
        ["claude-fable-5"]    = 600,
        ["claude-mythos-5"]   = 600,
        ["claude-opus-5"]     = 300,
        ["claude-opus-4-8"]   = 300,
        ["claude-opus-4-7"]   = 300,
        ["claude-opus-4-6"]   = 300,
        ["claude-opus-4-5"]   = 300,
        ["claude-sonnet-5"]   = 60,
        ["claude-sonnet-4-6"] = 60,
        ["claude-sonnet-4-5"] = 60,
        ["claude-haiku-4-5"]  = 15,

        // OpenAI, scaled off Epoch AI's GPT-4o figure by this lineup's own output prices ($30 / $15 / $6).
        ["gpt-5.6-sol"]   = 150,
        ["gpt-5.5"]       = 150,
        ["gpt-5.6-terra"] = 75,
        ["gpt-5.6-luna"]  = 30,

        // Moonshot. PUBLISHED: Kimi K2 activates 32B of 1.04T (8 of 384 experts). Carried forward to the later
        // Kimi ids, which have not published an architecture. The "highspeed" tiers are the same model served
        // with less batching, which does cost more per token - not modelled, so those two read low.
        ["k3"]                                  = 32,
        ["kimi-k3"]                             = 32,
        ["kimi-code/k3"]                        = 32,
        ["kimi-k2.7-code"]                      = 32,
        ["kimi-for-coding"]                     = 32,
        ["kimi-code/kimi-for-coding"]           = 32,
        ["kimi-k2.7-code-highspeed"]            = 32,
        ["kimi-for-coding-highspeed"]           = 32,
        ["kimi-code/kimi-for-coding-highspeed"] = 32,

        // xAI publishes nothing. Sized just under Opus on the strength of it being a very large MoE.
        ["grok-4.5"]                     = 250,
        ["grok-4-5"]                     = 250,
    };

    /// <summary>Opus-tier fallback, matching the pricing fallback: this app defaults to Opus.</summary>
    private const double FallbackActiveParamsB = 300;

    /// <summary>True when this exact model has its own entry rather than falling back to the Opus tier. The
    /// usage page uses it the same way it uses <see cref="ModelPricing.IsPriced"/> - to avoid quoting a
    /// confident number it cannot stand behind.</summary>
    public static bool IsRated(string? modelId) => ActiveParamsB.ContainsKey(ModelPricing.CanonicalId(modelId));

    /// <summary>Active parameters in billions for a model, or the Opus-tier fallback.</summary>
    public static double ActiveParamsBillions(string? modelId) =>
        ActiveParamsB.TryGetValue(ModelPricing.CanonicalId(modelId), out var n) ? n : FallbackActiveParamsB;

    // ---------- the estimate ----------

    /// <summary>Watt-hours per 1M tokens of each kind, for one model.</summary>
    public readonly record struct Intensity(double InputWhPerMTok, double CacheWriteWhPerMTok,
                                            double CacheReadWhPerMTok, double OutputWhPerMTok);

    public static Intensity For(string? modelId)
    {
        var output = ActiveParamsBillions(modelId) * WhPerMTokPerBillionActiveParams;
        var input = output * InputShareOfOutput;
        return new Intensity(input, input * CacheWriteShareOfInput, input * CacheReadShareOfInput, output);
    }

    /// <summary>Watt-hours behind one usage snapshot. Same shape as <see cref="ModelPricing.TurnCost"/>, and
    /// the same reason for splitting the input buckets: a cache hit is ~50x cheaper than an output token, and
    /// this app's input is almost entirely cache hits.</summary>
    public static double TurnEnergyWh(string? modelId, double input, double cacheWrite, double cacheRead, double output)
    {
        var i = For(modelId);
        return (input * i.InputWhPerMTok
                + cacheWrite * i.CacheWriteWhPerMTok
                + cacheRead * i.CacheReadWhPerMTok
                + output * i.OutputWhPerMTok) / 1_000_000.0;
    }

    /// <summary>Litres evaporated cooling the machines that served this. Google's formula: the datacenter
    /// overhead is stripped out first, because that overhead is largely the cooling itself.</summary>
    public static double OnSiteWaterLitres(double energyWh) =>
        energyWh / 1000.0 / DatacenterPue * OnSiteWaterLitresPerKWh;

    /// <summary>Litres including the water consumed generating the electricity, which is roughly 3x the cooling
    /// water again and is what published AI water figures usually leave out.</summary>
    public static double TotalWaterLitres(double energyWh) =>
        OnSiteWaterLitres(energyWh) + energyWh / 1000.0 * GridWaterLitresPerKWh;
}
