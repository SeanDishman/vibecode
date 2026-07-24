namespace VibeCode.Services;

/// <summary>
/// Product-level limits for the number of root agent sessions in one Bridge. Keeping the clamp here gives settings,
/// the add-agent guard, and regression tests one definition of the supported range.
/// </summary>
public static class BridgeAgentPolicy
{
    public const int MinimumAgentLimit = 4;
    public const int DefaultAgentLimit = 9;
    public const int MaximumAgentLimit = 16;

    public static int ClampLimit(int value) => Math.Clamp(value, MinimumAgentLimit, MaximumAgentLimit);

    public static bool CanAdd(int activeAgentCount, int configuredLimit) =>
        activeAgentCount > 0 && activeAgentCount < ClampLimit(configuredLimit);
}
