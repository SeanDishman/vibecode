namespace VibeCode.UI;

/// <summary>
/// An idle chat returns to the asleep state of <see cref="StartAsleep"/> while the app runs: its provider process tree (the
/// CLI, its MCP servers and its Bridge helper) ends, and everything else stays - transcript, queue, usage, Bridge identity,
/// mailbox. Unlike a restored sleeper its history is already on screen, so waking only resumes the same session, and Codex
/// resumes its thread without replaying it into the transcript a second time.
/// </summary>
public sealed partial class ChatViewModel
{
    private bool _idleSleep;

    /// <summary>Asleep after sitting idle; the sidebar badge and the Bridge pane state show it.</summary>
    public bool IsIdleAsleep => _idleSleep && _providerAsleep;

    /// <summary>Why ending this chat's process now could lose something; null when it can sleep. An agent team member
    /// mid-task is working or has its hand-off queued, so the first checks cover it. Gemini may sleep before ACP
    /// returns a session id: the next prompt starts a fresh Antigravity session rather than leaving 800MB idle.</summary>
    internal string? IdleSleepBlocker =>
        Status != "idle" ? "it is not idle"
        : _providerAsleep ? "it is already asleep"
        : _session is not { HasExited: false } ? "it has no running provider"
        : SessionId is null && !IsGemini ? "it has no session to resume"
        : AuthNeeded ? "it needs sign-in"
        : _pendingPerms.Count > 0 ? "it is waiting on a permission or a question"
        : HasQueued ? "it has queued prompts"
        : _mailboxNotices.Count > 0 ? "it has peer messages to deliver"
        : Goal is { Completed: false, Paused: false } ? "it is working toward a /goal"
        : HasLiveBridgeTask ? "it has a running or queued Bridge task"
        : WaitingForLimitReset || WaitingForBridgeRetry || _healthProbe is not null ? "it is waiting for its provider to recover"
        : IsCompactingContext ? "it is compacting its context"
        : HasWorkingSubagents ? "its subagents are working"
        : IsPeerNotificationTurn || ModelRestartHoldsDispatch || _steerSubmitting || _steerQueueHold ? "it is settling a prompt"
        : IsLoadingHistory ? "it is still loading its history"
        : BridgeWorkerPaused ? "its orchestrator stopped it"
        : StartedBackgroundCommand ? "a command it started in the background may still be running"
        : null;

    private bool HasLiveBridgeTask => BridgeWork?.Tasks.Any(task => task.State is "queued" or "dispatching" or "running"
        && (task.OwnerId == BridgeAgentId || task.CoordinatorId == BridgeAgentId)) == true;

    // A run_in_background shell lives inside the CLI and dies with it, and nothing tells VibeCode when one has finished.
    private bool StartedBackgroundCommand => _toolById.Values.Any(tool => tool.HistoryTime is null && tool.Name == "Bash"
        && tool.Input?["run_in_background"]?.ToString() == "true");

    /// <summary>End this idle chat's provider process tree. False, with nothing changed, while anything would be lost.</summary>
    internal bool SleepWhileIdle()
    {
        if (IdleSleepBlocker is not null) return false;
        var session = _session!;
        // Waking resumes this conversation without reading back what is already on screen.
        var resume = SessionId ?? session.SessionId;
        if (SessionId is null && resume is not null) SessionId = resume;
        ResumeSessionId = resume;
        ForkSession = false;
        _transcriptLoaded = true;
        _sessionUsageRestored = true;
        _taskRecoveryLoaded = true;
        _session = null;
        _idleSleep = true;
        _providerAsleep = true;
        _asleepAtVersion = Interlocked.Increment(ref _startVersion);   // the old process's delayed callbacks no longer apply
        session.Dispose();
        Raise(nameof(IsAsleep));
        Raise(nameof(IsIdleAsleep));
        Raise(nameof(BridgeAvailabilityText));
        return true;
    }

    /// <summary><see cref="Wake"/> for a chat that slept while idle.</summary>
    private void ResumeFromIdleSleep(int version)
    {
        _idleSleep = false;
        Raise(nameof(IsIdleAsleep));
        Raise(nameof(BridgeAvailabilityText));
        // Codex would otherwise replay its whole thread into a transcript that never left the screen.
        if (IsCodex && ResumeSessionId is { } thread) StartCore(codexResumeSessionId: thread);
        else ContinueStart(version);
    }

}
