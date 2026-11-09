namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    // Changes whenever a chat leaves its mailbox scope. Old user-requested wake preferences must not follow it.
    internal int PeerMailboxEpoch { get; private set; }
    internal bool PeerWakeScopeAllowed => !IsBridgeManager && !BridgeCoordinatesOnly && BridgeCoordinatorAgentId is null;
    internal string? PeerWakeUnavailableReason => Status == "closed" ? "That chat is closed. Reopen it before sending."
        : IsWaitingForAgentTeamPrompt ? "This team is waiting for its next user prompt."
        : !PeerWakeScopeAllowed ? "Advanced Bridge workers require dispatch; explicit message wakeups are for regular Bridges and ordinary chats."
        : AuthNeeded ? "The recipient needs sign-in before it can be woken."
        : WaitingForLimitReset || WaitingForBridgeRetry ? "The recipient is waiting for provider recovery. A wake request cannot bypass that wait."
        : PeerMailbox?.Store.Available == false ? "The recipient mailbox is unavailable."
        : null;

    private bool CanDispatchPeerNotice(MailboxNotice notice) => notice.WakeReason is null
        ? CanWakeForPeerMessages
        : notice.WakeAllowed?.Invoke() == true && PeerWakeUnavailableReason is null;

    private void ProcessExplicitPeerWake()
    {
        if (!_mailboxNotices.Any(n => n.WakeReason is not null && !n.Message.ReadAt.HasValue && CanDispatchPeerNotice(n))) return;
        // Never interrupt a turn, a pending permission, or a rewind. Initialization/turn completion flushes later.
        if (IsWorking || IsPeerNotificationTurn || ModelRestartHoldsDispatch || _steerSubmitting || _steerQueueHold) return;
        if (_session is { HasExited: false })
        {
            if (Status == "error") Status = "idle";
            FlushQueue();
            return;
        }
        // An agent asleep after sitting idle resumes its session; once it is ready its start delivers the wake-up.
        if (IsIdleAsleep)
        {
            Wake();
            return;
        }
        // One reconnect attempt for this arrival, using the same chat, account, permissions and transcript.
        // A startup/auth failure leaves the durable inbox intact; it never starts an automatic retry loop.
        var previous = _session;
        _session = null;
        previous?.Dispose();
        ResumeSessionId = SessionId ?? ResumeSessionId;
        ForkSession = false;
        _transcriptLoaded = true;
        _sessionUsageRestored = true;
        Status = "starting";
        Items.Add(new DividerItem { Label = "Peer wake-up requested · reconnecting chat" });
        try { Start(); }
        catch (Exception ex)
        {
            Status = "error";
            Items.Add(new BannerItem { Level = "error", Text = "Could not wake this chat: " + ex.Message });
        }
    }
}
