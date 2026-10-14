namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    internal bool BridgeWorkerPaused => IsBridgeAgent && !IsBridgeManager && BridgeCoordinatorAgentId is not null
        && BridgeWork?.PausedWorkers.Contains(BridgeAgentId) == true;

    internal bool ManagedWorkerStopAwaitingReport { get; set; }

    internal void RefreshWorkerControl()
    {
        Raise(nameof(BridgeAvailabilityText));
        Raise(nameof(CanSendQueuedNow));
    }

    internal void StopManagedWorker(string reason)
    {
        CancelBridgeErrorRecovery();
        CancelLimitRecovery();
        PauseGoal();
        RefreshWorkerControl();
        ManagedWorkerStopAwaitingReport = _status is "running" or "preparing";
        Items.Add(new BannerItem { Level = "info", Text = "Worker paused by orchestrator: " + reason });
        // Preparation is still using this session. Keep it busy until that async operation observes the hold,
        // so a retry cannot mistake the old preparation for the new turn and send both prompts.
        if (_status == "preparing") _interruptRequested = true;
        else if (_status == "running") InterruptCore(pauseBridgePlan: false);
        else BridgeTaskState = "interrupted";
        ItemsChanged?.Invoke();
    }

    private bool StopManagedPreparation(UserItem item)
    {
        if (!BridgeWorkerPaused || _status != "preparing") return false;
        CaptureMemoryDispatchFailure(item, "Stopped by orchestrator before dispatch.");
        if (ReferenceEquals(_activeExtendedDispatch?.UserItem, item)) RequeueActiveExtendedDispatch();
        _interruptRequested = false;
        BridgeTaskState = "interrupted";
        Status = "idle";
        ItemsChanged?.Invoke();
        return true;
    }

    internal string? ManagedWorkerResumeError => Status == "closed" ? "The worker's chat is closed. Reopen it before restarting work."
        : IsWorking || _interruptRequested && _session is { HasExited: false } ? "The worker is still stopping or connecting. Wait for its turn to settle before restarting."
        : AuthNeeded ? "The worker needs sign-in before it can restart."
        : WaitingForLimitReset || WaitingForBridgeRetry ? "Stop the worker's automatic recovery before explicitly restarting it."
        : RewindHoldsDispatch || _steerSubmitting || _steerQueueHold ? "The worker is still settling a prompt or workspace change."
        : null;

    internal void ResumeManagedWorker()
    {
        _interruptRequested = false;
        ManagedWorkerStopAwaitingReport = false;
        if (BridgeTaskState == "interrupted") BridgeTaskState = "ready";
        RefreshWorkerControl();
        if (_session is { HasExited: false })
        {
            if (Status == "error") Status = "idle";
            Post(FlushQueue);
            return;
        }
        // Reconnect the existing conversation; keep its transcript, account, model and permission settings.
        var previous = _session;
        _session = null;
        previous?.Dispose();
        ResumeSessionId = SessionId ?? ResumeSessionId;
        ForkSession = false;
        _transcriptLoaded = true;
        Status = "starting";
        Items.Add(new DividerItem { Label = "Orchestrator resumed worker · reconnecting chat" });
        try { Start(); }
        catch (Exception ex)
        {
            Status = "error";
            Items.Add(new BannerItem { Level = "error", Text = "Could not restart this worker: " + ex.Message });
        }
    }
}
