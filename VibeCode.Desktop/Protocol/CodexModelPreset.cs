namespace VibeCode.Protocol;

/// <summary>
/// The GPT-5.6 agentic coding models VibeCode surfaces for Codex. The app-server's live catalog is authoritative;
/// these ids only back the instant menu preview and the offline fallback rows.
/// </summary>
public static class CodexModelPreset
{
    public const string SolModelId = "gpt-5.6-sol";
    public const string LunaModelId = "gpt-5.6-luna";
    public const string TerraModelId = "gpt-5.6-terra";

    public const string SolDisplayName = "GPT 5.6 Sol";
    public const string LunaDisplayName = "GPT 5.6 Luna";
    public const string TerraDisplayName = "GPT 5.6 Terra";

    public const string SolDescription = "Flagship GPT-5.6 agentic coding model.";
    public const string TerraDescription = "Balanced GPT-5.6 agentic coding model.";
    public const string LunaDescription = "Fast GPT-5.6 agentic coding model.";

    public static IReadOnlyList<(string ModelId, string DisplayName, string Description)> All { get; } =
    [
        (SolModelId, SolDisplayName, SolDescription),
        (TerraModelId, TerraDisplayName, TerraDescription),
        (LunaModelId, LunaDisplayName, LunaDescription),
    ];
}
