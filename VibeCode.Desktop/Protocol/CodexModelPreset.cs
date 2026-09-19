namespace VibeCode.Protocol;

/// <summary>
/// Known normal Codex models. Live catalog rows retain their advertised reasoning and speed options;
/// these definitions keep the menu usable when a runtime omits a known row.
/// </summary>
public static class CodexModelPreset
{
    public const string AstraModelId = "gpt-6-astra";
    public const string SolModelId = "gpt-5.6-sol";
    public const string LunaModelId = "gpt-5.6-luna";
    public const string TerraModelId = "gpt-5.6-terra";

    public const string SolDisplayName = "GPT Sol 5.6";
    public const string LunaDisplayName = "GPT 5.6 Luna";
    public const string TerraDisplayName = "GPT 5.6 Terra";

    public const string SolDescription = "Fast and affordable agentic coding model.";
    public const string TerraDescription = "Balanced GPT-5.6 agentic coding model.";
    public const string LunaDescription = "Fast GPT-5.6 agentic coding model.";

    public static IReadOnlyList<(string ModelId, string DisplayName, string Description, IReadOnlyList<string> EffortLevels)> All { get; } =
    [
        (AstraModelId, "GPT-6 Astra", "Our most capable model for complex, demanding work.", ["low", "medium", "high", "xhigh", "max", "ultra"]),
        (SolModelId, SolDisplayName, SolDescription, ["low", "medium", "high", "xhigh", "max", "ultra"]),
        (TerraModelId, TerraDisplayName, TerraDescription, ["low", "medium", "high", "xhigh", "max", "ultra"]),
        (LunaModelId, LunaDisplayName, LunaDescription, ["low", "medium", "high", "xhigh", "max"]),
        (CodexSession.SparkModelId, "GPT-5.3 Codex Spark", CodexSession.SparkDescription, ["low", "medium", "high", "xhigh"]),
    ];

    internal static bool IsKnown(string? model) => All.Any(row =>
        string.Equals(row.ModelId, model, StringComparison.OrdinalIgnoreCase));
}
