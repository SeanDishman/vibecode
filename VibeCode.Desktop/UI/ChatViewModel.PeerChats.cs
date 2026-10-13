namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal Action? RefreshPeerChatAccess { get; set; }
    internal BridgeChatMirror? PeerChatMirror { get; set; }
    internal bool PeerChatSetupWarningShown { get; set; }
    internal bool HasRunningPeerChatSession => _session is { HasExited: false };
    internal bool IsPeerChatHistoryLoading => ResumeSessionId is not null
        && ((IsClaude || IsGlm) ? !_transcriptLoaded : !_bridgeSessionInitialized);
    private string? PeerChatInstructions => PeerChatMirror?.Store.Instructions;

    internal void ClearPeerChatAccess()
    {
        PeerChatMirror?.Dispose();
        PeerChatMirror = null;
        PeerChatSetupWarningShown = false;
    }
}
