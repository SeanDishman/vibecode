namespace VibeCode.Services;

/// <summary>
/// Product-level guardrails for provider-native child agents. A Bridge peer is a separate root CLI session;
/// a swarm worker is a child owned by one of those roots. Keeping that distinction here prevents UI labels,
/// prompts, and provider launch limits from drifting into different definitions of "agent".
/// </summary>
public static class SwarmPolicy
{
    /// <summary>Conservative default: Codex itself defaults to six and nearby products generally ship four to ten.</summary>
    public const int DefaultMaxWorkers = 6;

    /// <summary>Absolute VibeCode ceiling, even if settings.json is edited by hand.</summary>
    public const int HardMaxWorkers = 16;

    public const int MinMaxWorkers = 2;

    public static bool SupportsProvider(string? provider) =>
        string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase);

    public static int ClampMaxWorkers(int value) => Math.Clamp(value, MinMaxWorkers, HardMaxWorkers);

    /// <summary>Sent at launch and on every turn, including resumed chats with older swarm instructions.</summary>
    public static string SessionRuntimeRule(string provider, int maxWorkers)
    {
        if (!SupportsProvider(provider)) return "";
        maxWorkers = ClampMaxWorkers(maxWorkers);
        var nativeName = provider.ToLowerInvariant() switch
        {
            "codex" => "Codex collaboration subagents",
            _ => "Claude Agent subagents",
        };
        return "[VIBECODE CHILD-AGENT POLICY]\n" +
               $"{nativeName} are available. When the user asks you to swarm, delegate, use subagents, or work in " +
               "parallel, follow that request in normal language. No special request marker, button, or enable " +
               "setting is required. This replaces earlier VibeCode instructions requiring those controls. " +
               "Do not spawn children for an ordinary message without a delegation request. " +
               $"Use the smallest useful team, with at most {maxWorkers} concurrent or total child workers per " +
               "request. Keep the team flat: child workers must not spawn more agents. Give workers distinct " +
               "scopes, avoid duplicate edits, wait for every child, then verify and integrate their results " +
               "before answering.\n[END VIBECODE CHILD-AGENT POLICY]";
    }

    public static string BridgeRuntimeRule(int maxWorkers) =>
        "- You may use native child agents when the user asks for a swarm or delegation in normal language. " +
        "No special request marker, button, or enable setting is required. " +
        $"Keep the team flat and within {ClampMaxWorkers(maxWorkers)} child workers per request. " +
        "Bridge peers remain separate root sessions; do not count or commandeer them as swarm children.";
}
