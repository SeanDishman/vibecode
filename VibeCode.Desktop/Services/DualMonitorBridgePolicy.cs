namespace VibeCode.Services;

/// <summary>
/// Pure, platform-neutral rules for the optional dual-monitor Bridge layout. Keeping the policy free of WPF/Win32
/// makes the important threshold and partition contract cheap to regression-test.
/// </summary>
public static class DualMonitorBridgePolicy
{
    public const int MinimumAgentCount = 3;

    /// <summary>
    /// Demon Mode and the dual-monitor layout are mutually exclusive, and the refusal belongs here rather than in an
    /// early return at the call site — it is a rule about the feature, and a rule spread across two windows is a rule
    /// that gets forgotten in one of them. A Demon wall is a single arrangement: the orchestrator holds a 2x2 block
    /// with its workers around it, and cutting that down the middle would put the only pane the user can type into on
    /// whichever display the partition happened to pick.
    /// <para>
    /// It is deliberately the ON-SCREEN wall that refuses, not the mere existence of a team: a Demon team parked
    /// behind another chat's Bridge is not being laid out at all, and has no business dictating how the Bridge the
    /// user IS looking at is arranged.
    /// </para>
    /// </summary>
    public const string DemonRefusal =
        "Dual-monitor is off while Demon Mode is running — the wall keeps the orchestrator and its workers on one screen.";

    public static bool ShouldSplit(bool enabled, bool bridgeVisible, int agentCount, int monitorCount,
        bool demonWallOnScreen) =>
        !demonWallOnScreen && enabled && bridgeVisible && agentCount >= MinimumAgentCount && monitorCount >= 2;

    /// <summary>A double-session shell stays available whenever a second display exists; otherwise retain the
    /// original three-agent Bridge threshold. Neither is offered while a Demon wall is on screen.</summary>
    public static bool ShouldOpenCompanion(bool bridgeSplitEnabled, bool doubleSessionsEnabled,
        bool bridgeVisible, int agentCount, int monitorCount, bool demonWallOnScreen) =>
        !demonWallOnScreen && monitorCount >= 2
        && (doubleSessionsEnabled
            || ShouldSplit(bridgeSplitEnabled, bridgeVisible, agentCount, monitorCount, demonWallOnScreen));

    /// <summary>In double-session mode, only partition a roster while both shells show it. If either shell navigates
    /// to another chat, the remaining Bridge surface must retain every pane instead of hiding half off-screen.
    /// <para>
    /// <paramref name="secondaryHasOwnBridge"/> is the decisive one once two Bridges can be open at once: splitting
    /// means "one roster spread over two displays", and there is no second display left to spread onto when the
    /// other shell is running a Bridge of its own. Two whole Bridges beat half of one.
    /// </para></summary>
    public static bool ShouldPartition(bool splitEligible, bool doubleSessionsEnabled,
        bool primaryBridgeVisible, bool secondaryBridgeVisible, bool secondaryHasOwnBridge = false) =>
        !secondaryHasOwnBridge
        && splitEligible && (!doubleSessionsEnabled || (primaryBridgeVisible && secondaryBridgeVisible));

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
