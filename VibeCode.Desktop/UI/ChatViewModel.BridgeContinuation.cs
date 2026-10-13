namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    // Include hidden mailbox dispatches and recovery holds, not just the visible running indicator.
    internal bool SettledForBridgePlanReminder => Status is "idle" or "error" or "closed"
        && !HasPendingDispatch && !IsCompactingContext && !IsPeerNotificationTurn
        && !WaitingForLimitReset && !WaitingForBridgeRetry && !_interruptRequested
        && !RewindHoldsDispatch && !_steerSubmitting && !_steerQueueHold
        && !ExtendedQueuePaused && _pendingPerms.Count == 0;

    internal bool SendBridgePlanReminder(string text)
    {
        if (!IsBridgeManager || !BridgeCoordinatesOnly || Status != "idle"
            || !SettledForBridgePlanReminder || !CanAcceptBridgeObjective) return false;
        SendAutomaticContinuation(text);
        return true;
    }
}
