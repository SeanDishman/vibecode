namespace VibeCode.Services;

/// <summary>
/// Product-level limits for the number of root agent sessions in one Bridge. Keeping the clamp and the seat count here
/// gives settings, the add-agent guard, and regression tests one definition of the supported range.
/// </summary>
public static class BridgeAgentPolicy
{
    public const int MinimumAgentLimit = 4;
    public const int DefaultAgentLimit = 9;
    /// <summary>Maximum root sessions supported by a Bridge.</summary>
    public const int MaximumAgentLimit = 20;
    /// <summary>Larger advanced teams share one terminal instead of rendering every agent side by side.</summary>
    public const int MaximumSeparateTerminalAgents = 9;

    public static bool RequiresSingleTerminal(int totalAgents) => totalAgents > MaximumSeparateTerminalAgents;

    public static int ClampLimit(int value) => Math.Clamp(value, MinimumAgentLimit, MaximumAgentLimit);

    public static bool CanAdd(int activeAgentCount, int configuredLimit) =>
        activeAgentCount > 0 && activeAgentCount < ClampLimit(configuredLimit);

    /// <summary>
    /// The seats a live roster fills against the limit: one per agent with a pane of its own (in a Bridge that is one
    /// agent team, every member). Agents a classic-Bridge pane's robot brought into that pane's team work inside their
    /// anchor's pane, so they take no seat - each such team is capped on its own with <see cref="CanAdd"/> over its
    /// members. Counting them here made a Bridge with five panes report "already has 9 agents" and refuse Add agent.
    /// </summary>
    public static int Seats(IEnumerable<UI.ChatViewModel> roster) => roster.Count(agent => !agent.IsTeamPaneMember);
}
