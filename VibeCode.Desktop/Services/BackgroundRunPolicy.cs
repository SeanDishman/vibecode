namespace VibeCode.Services;

/// <summary>
/// Pure rules for "run in background": when a close is really a close, and what the notification area says while
/// the window is gone. Free of WPF and Win32 on purpose - the decision that separates "hide the shell" from "end
/// nine running agents" is the one part of this feature that must be cheap to regression-test.
/// </summary>
public static class BackgroundRunPolicy
{
    /// <summary>The app's existing off-screen test hook. An automated run closes the window to end the process, so
    /// a harness must never be answered with a trayed survivor - it would hang, and the next run would find it.</summary>
    public const string AutomationVariable = "VIBECODE_HIDDEN";

    public static bool IsAutomationRun =>
        Environment.GetEnvironmentVariable(AutomationVariable) == "1";

    /// <summary>
    /// Should this closing window hide into the notification area instead of taking the app down with it?
    /// </summary>
    /// <param name="enabled">The user's setting.</param>
    /// <param name="applicationExiting">Something already decided the process is ending - the tray's own Quit, an
    /// update or theme restart, or Windows logging the user off. Those must reach the full session teardown.</param>
    /// <param name="isPrimaryShell">Only the shell that OWNS the sessions may stand in for the app. Closing the
    /// second-monitor companion is a layout choice and is handled entirely separately.</param>
    /// <param name="replacedByReskin">An appearance change closes the shell while handing its live view model to a
    /// replacement window. Nothing is going away, so there is nothing to background.</param>
    /// <param name="automationRun">See <see cref="AutomationVariable"/>.</param>
    public static bool ShouldStayResident(bool enabled, bool applicationExiting, bool isPrimaryShell,
        bool replacedByReskin, bool automationRun) =>
        enabled && isPrimaryShell && !applicationExiting && !replacedByReskin && !automationRun;

    /// <summary>Notification-area tooltip. Kept under the 63 characters a classic <c>NOTIFYICONDATA.szTip</c> holds,
    /// and it says what is actually happening rather than only that the app exists: the whole reason to leave
    /// VibeCode resident is the agents, so the icon reports on them.</summary>
    public static string Tooltip(int workingAgents) => workingAgents switch
    {
        <= 0 => "VibeCode — running in the background",
        1 => "VibeCode — 1 agent working",
        _ => $"VibeCode — {workingAgents} agents working",
    };

    /// <summary>Shown once, the first time the window disappears into the icon. Without it the app looks like it
    /// crashed on the close button - and the one thing the user needs is where their window went.</summary>
    public const string NoticeTitle = "VibeCode is still running";

    public const string NoticeBody =
        "Your agents keep coding in the background. Click this icon to bring the window back, " +
        "or right-click it to quit. Turn this off in Settings ▸ General.";

    /// <summary>Tooltip for the shell's own close button, so the behaviour is legible before it happens rather
    /// than only after the window has vanished.</summary>
    public static string CloseButtonTooltip(bool enabled) => enabled
        ? "Close to the notification area — agents keep working (Settings ▸ General)"
        : "Close VibeCode — this ends every running agent";
}
