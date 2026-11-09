using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    /// <summary><see cref="RestoreBridge"/> during startup, timed into the startup timeline and noted like the other
    /// startup steps. A reconnect starts every pane of the roster at once, so it is one of the larger startup costs.</summary>
    private bool ReconnectBridgeAtStartup(ChatViewModel host, bool showPrimary = true, bool showOnSecondary = true)
    {
        var started = StartupTimeline.NowMs;
        var restored = RestoreBridge(host, showPrimary, showOnSecondary);
        var elapsed = StartupTimeline.NowMs - started;
        var panes = TryGetLiveBridge(host, out var bridge) ? bridge.Panes.Count : 0;
        StartupTimeline.Record("bridge", "BridgeReconnect", started, elapsed,
            $"{host.Title}: {panes} panes{(restored ? "" : ", nothing restored")}");
        CrashLog.Note("StartupPhase", $"BridgeReconnect: {elapsed:0} ms ({panes} panes)");
        return restored;
    }
}
