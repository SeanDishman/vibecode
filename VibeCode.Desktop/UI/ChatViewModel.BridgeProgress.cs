using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal BridgeWorkState? BridgeWork { get; set; }
    public int BridgeProgressRevision { get; private set; }
    internal void RaiseBridgeProgress()
    {
        BridgeProgressRevision++;
        Raise(nameof(BridgeProgressRevision));
        Raise(nameof(BridgeFileActivity));
    }
    public string BridgeFileActivity => BridgeWork is null ? "" : string.Join(", ", BridgeWork.Tasks
        .Where(t => t.OwnerId == BridgeAgentId && t.State is "running" or "blocked")
        .SelectMany(t => t.Files).Distinct().Take(8));
}
