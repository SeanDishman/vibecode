namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    /// <summary>Navigate to an already-open agent, including a parked Bridge, without starting a new session.</summary>
    public bool OpenWorkflowAgent(ChatViewModel chat)
    {
        if (chat.Status == "closed") return false;
        if (TryGetLiveBridge(chat, out var bridge))
        {
            OpenChat(bridge.Panes[0]);
            ResetBridgeExpand();
            RestoreBridgePane(chat);
            SelectBridgePane(chat);
            return true;
        }
        if (!Chats.Contains(chat)) return false;
        OpenChat(chat);
        return true;
    }
}
