namespace VibeCode.Services;

/// <summary>
/// Pure, platform-neutral rules for the optional dual-monitor Bridge layout. Keeping the policy free of WPF/Win32
/// makes the important threshold and partition contract cheap to regression-test.
/// </summary>
public static class DualMonitorBridgePolicy
{
    public const int MinimumAgentCount = 3;

    public static bool ShouldSplit(bool enabled, bool bridgeVisible, int agentCount, int monitorCount) =>
        enabled && bridgeVisible && agentCount >= MinimumAgentCount && monitorCount >= 2;

    /// <summary>A double-session shell stays available whenever a second display exists; otherwise retain the
    /// original three-agent Bridge threshold.</summary>
    public static bool ShouldOpenCompanion(bool bridgeSplitEnabled, bool doubleSessionsEnabled,
        bool bridgeVisible, int agentCount, int monitorCount) =>
        monitorCount >= 2 && (doubleSessionsEnabled
                              || ShouldSplit(bridgeSplitEnabled, bridgeVisible, agentCount, monitorCount));

    /// <summary>In double-session mode, only partition a roster while both shells show it. If either shell navigates
    /// to another chat, the remaining Bridge surface must retain every pane instead of hiding half off-screen.</summary>
    public static bool ShouldPartition(bool splitEligible, bool doubleSessionsEnabled,
        bool primaryBridgeVisible, bool secondaryBridgeVisible) =>
        splitEligible && (!doubleSessionsEnabled || (primaryBridgeVisible && secondaryBridgeVisible));

    /// <summary>How many agents stay on the main VibeCode shell when the split opens. The roster is divided as evenly
    /// as possible, keeping the odd one out on the main window (8 → 4 here / 4 across; 9 → 5 / 4; 3 → 2 / 1).</summary>
    public static int PrimaryPaneCount(int totalPaneCount) => Math.Max(1, (totalPaneCount + 1) / 2);

    /// <summary>
    /// The INITIAL spill only, applied once when the split opens: the first half of the roster stays with the main
    /// VibeCode shell and the rest move to the companion display, split as evenly as possible. From then on each pane
    /// owns its surface (<c>ChatViewModel.OnSecondMonitor</c>), so adding or removing an agent never drags peers across.
    /// </summary>
    public static bool IsCompanionPane(int zeroBasedIndex, int totalPaneCount) =>
        zeroBasedIndex >= PrimaryPaneCount(totalPaneCount);

    /// <summary>
    /// WPF rejects Show() when an unseen window is already maximized and ShowActivated is false. Companion windows
    /// deliberately avoid activation, so their initial maximize must happen immediately after Show().
    /// </summary>
    public static bool RequiresPostShowMaximize(bool showActivated, bool isVisible) =>
        !showActivated && !isVisible;

    /// <summary>Match the original Bridge grid density, but calculate it independently for each monitor.</summary>
    public static int RowsForVisiblePaneCount(int visiblePaneCount) =>
        visiblePaneCount switch { <= 2 => 1, <= 6 => 2, _ => 3 };
}
