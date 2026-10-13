namespace VibeCode.Protocol;

public static class CodexModelCatalog
{

    private static readonly string[] UltraEfforts = ["low", "medium", "high", "xhigh", "max", "ultra"];
    private static readonly string[] MaxEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Recommended Codex models as of 2026-09-30, then the GPT-5.6 family that is still available
    /// during the GPT-6 rollout. GPT-6 Luna stops at Max; the others include Ultra.
    /// </summary>
    public static readonly ModelSpec[] Current =
    [
        new("gpt-6-astra", "GPT-6 Astra",
            "Our most capable model for complex work across code, apps, and research.", UltraEfforts),
        new("gpt-6.1-sol", "GPT-6.1 Sol",
            "Near-Astra performance for complex coding and professional work at a lower cost.", UltraEfforts),
        new("gpt-6-sol", "GPT-6 Sol",
            "Built for complex coding and agentic workflows, with stronger factual reliability than GPT-5.6 Sol.",
            UltraEfforts),
        new("gpt-6-luna", "GPT-6 Luna",
            "Most efficient GPT-6 model for focused, high-volume tasks. Reasoning goes up to Max, not Ultra.",
            MaxEfforts),
        new("gpt-5.6-sol", "GPT Sol 5.6",
            "Flagship GPT-5.6 agentic coding model. Still available during the GPT-6 rollout.", UltraEfforts),
        new("gpt-5.6-terra", "GPT-5.6 Terra",
            "Balanced GPT-5.6 agentic coding model. Still available during the GPT-6 rollout.", UltraEfforts),
        new("gpt-5.6-luna", "GPT-5.6 Luna",
            "Fast GPT-5.6 agentic coding model. Still available during the GPT-6 rollout.", MaxEfforts),
    ];

    public readonly record struct ModelSpec(string Id, string DisplayName, string Description, string[] EffortLevels);

    public static bool IsCurrentModel(string? id) =>
        Current.Any(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static ModelSpec? Find(string? id)
    {
        foreach (var model in Current)
            if (string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
                return model;
        return null;
    }

    /// <summary>A VibeCode-owned id. Real OpenAI slugs are not owned and may be sent to Codex as-is.</summary>
    public static bool IsOwnedPreset(string? model) => IsLegacyAlias(model);

    public static bool IsLegacyAlias(string? model) => model?.StartsWith("vibecode:", StringComparison.OrdinalIgnoreCase) == true;

    public static string? WireModel(string? model) => IsLegacyAlias(model) ? null : model?.Trim();

    /// <summary>
    /// Models Codex no longer serves for ChatGPT sign-in. GPT-5.3-Codex-Spark retired on
    /// 2026-09-14. GPT-5.5 retires from Codex on
    /// 2026-10-14 and is already withheld. GPT-5.4 and GPT-5.4-mini retired on 2026-08-31.
    /// GPT-5.2 and GPT-5.3-Codex were deprecated before that.
    /// </summary>
    public static bool IsRetiredModel(string? id)
    {
        var backend = WireModel(id)?.Trim();
        if (string.IsNullOrWhiteSpace(backend)) return false;
        return Family(backend, "gpt-5.5")
               || Family(backend, "gpt-5.4")
               || Family(backend, "gpt-5.3")
               || Family(backend, "gpt-5.2")
               || Family(backend, "gpt-5.1")
               || Family(backend, "gpt-5-codex")
               || Family(backend, "gpt-5-mini")
               || backend.Equals("gpt-5", StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeSelection(string? id) => IsLegacyAlias(id) || IsRetiredModel(id) ? null : id?.Trim();

    public static bool ShouldList(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && !IsRetiredModel(id)
        && !IsLegacyAlias(id);

    private static bool Family(string backend, string id) =>
        backend.Equals(id, StringComparison.OrdinalIgnoreCase)
        || backend.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase);

    private static string Tail(string? id)
    {
        var value = id?.Trim() ?? "";
        var slash = value.LastIndexOf('/');
        return slash >= 0 ? value[(slash + 1)..] : value;
    }
}
