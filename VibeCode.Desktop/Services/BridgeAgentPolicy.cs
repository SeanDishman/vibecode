namespace VibeCode.Services;

/// <summary>
/// Product-level limits for the number of root agent sessions in one Bridge. Keeping the clamp here gives settings,
/// the add-agent guard, and regression tests one definition of the supported range.
/// </summary>
public static class BridgeAgentPolicy
{
    public const int MinimumAgentLimit = 4;
    public const int DefaultAgentLimit = 9;
    /// <summary>Seventeen rather than a round sixteen because Demon Mode fills the roster to this ceiling and its wall
    /// is a 4x5 grid whose top-left 2x2 block is the orchestrator: that leaves exactly sixteen worker cells, so a
    /// sixteen-session team drew one permanently empty hole in the bottom-right corner.</summary>
    public const int MaximumAgentLimit = 17;

    public static int ClampLimit(int value) => Math.Clamp(value, MinimumAgentLimit, MaximumAgentLimit);

    public static bool CanAdd(int activeAgentCount, int configuredLimit) =>
        activeAgentCount > 0 && activeAgentCount < ClampLimit(configuredLimit);
}
