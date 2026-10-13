using VibeCode.Protocol;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal Func<string?>? ProjectChatContext { get; set; }
    internal Action? ProjectChatTurnStarting { get; set; }
    private bool _sendingAutomaticContinuation;

    private void SendAutomaticContinuation(string text)
    {
        _sendingAutomaticContinuation = true;
        try { SendNow(text, null); }
        finally { _sendingAutomaticContinuation = false; }
    }

    // Best-effort app notice inside an existing turn. Never interrupt, start a turn, or put peer data in
    // the user's prompt history. The next ordinary prompt also carries the current roster and inbox count.
    internal async Task<bool> NotifyProjectChatAsync(string note)
    {
        if (IsBridgeAgent || !CanSteer || _session is not ISteerableSession session) return false;
        _steerSubmitting = true;
        Raise(nameof(CanSteer));
        Raise(nameof(CanSendQueuedNow));
        try
        {
            await session.SteerAsync(BuildContent(note, true, null));
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            _steerSubmitting = false;
            Raise(nameof(CanSteer));
            Raise(nameof(CanSendQueuedNow));
            if (!_steerQueueHold && _status == "idle") Post(FlushQueue);
        }
    }
}
