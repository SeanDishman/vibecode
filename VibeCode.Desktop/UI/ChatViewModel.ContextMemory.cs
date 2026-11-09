using System.IO;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Memory and context upkeep for one chat: pinned Second Brain cards on the first turn and after each compaction, a
/// one-line checkpoint nudge when the context meter crosses the user's threshold, a fresh-chat handoff where the
/// provider cannot compact, Claude's PreCompact snapshot, the CLI's own memory folder, the Bridge prompt and the
/// background memory review triggers.
/// </summary>
public sealed partial class ChatViewModel
{
    private static readonly List<WeakReference<ChatViewModel>> CheckpointAgeWatchers = new();
    private static DispatcherTimer? _checkpointAgeClock;
    private bool _checkpointAgeWatched;
    private bool _pinnedMemoryPending = true;
    private bool _contextNudgeArmed = true;
    private string? _stagedContextNote;
    private bool _freshChatHandoffOffered;
    private DispatcherTimer? _memoryReviewIdle;

    /// <summary>The user's Bridge prompt block for this chat's Bridge, or null; set by the host for every tracked chat.</summary>
    internal Func<string?>? BridgeUserPromptContext { get; set; }

    /// <summary>"checkpoint 3 min ago" beside the context readout, or "checkpoint none". Counts the model's own
    /// chat_checkpoint calls and PreCompact snapshots, not the automatic per-turn saves, which would always read "just now".</summary>
    public string CheckpointAgeText => ContextCheckpointPolicy.AgeText(_lastCheckpointAt, DateTimeOffset.UtcNow);
    public bool ShowCheckpointAge => _ctxUsed > 0;
    private DateTimeOffset? _lastCheckpointAt;

    private void NoteCheckpointSaved(DateTimeOffset at)
    {
        _lastCheckpointAt = at;
        Raise(nameof(CheckpointAgeText));
    }

    /// <summary>Offered once the meter crosses the threshold on a provider VibeCode cannot compact or fork.</summary>
    public bool FreshChatHandoffAvailable => _freshChatHandoffOffered && Status != "closed";

    public string FreshChatHandoffDraft() => ContextCheckpointPolicy.HandoffDraft(RecoveryCheckpoint, Title, CtxPercentValue);

    /// <summary>The mute only counts while the extension is on, where the user can see and change it.</summary>
    private bool NativeMemoryMuted => MemoryControlsAvailable && ExcludeFromMemory;

    /// <summary>The project's VibeCode-owned folder for Claude's auto memory, or null to leave the CLI's default.</summary>
    private string? NativeMemoryDirectoryForLaunch(string? configDirectory) =>
        NativeMemoryMuted ? null : CliNativeMemory.PrepareClaude(Cwd, configDirectory, DateTimeOffset.UtcNow);

    /// <summary>The pinned block for this turn: on the first real prompt and again after each compaction.</summary>
    private string? PinnedMemoryForTurn(string text)
    {
        if (!_pinnedMemoryPending || !SecondBrainActive || IsSystemInjectedPrompt(text)) return null;
        _pinnedMemoryPending = false;
        try { return PinnedMemory.Block(Cwd, DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The Bridge prompt opens every message a Bridge agent receives, not only the first.</summary>
    private string? BridgeUserPromptForTurn() =>
        IsBridgeAgent && BridgeUserPromptContext?.Invoke() is { Length: > 0 } block ? block : null;

    /// <summary>A nudge the running turn could not take rides the next prompt, unless the window has emptied since.</summary>
    private string? TakeStagedContextNote()
    {
        var note = _stagedContextNote;
        _stagedContextNote = null;
        return note is not null && CtxPercentValue >= AppSettings.Current.ContextCheckpointNudgePercent - ContextCheckpointPolicy.RearmMargin
            ? note : null;
    }

    private void OnContextMeterChanged()
    {
        Raise(nameof(CheckpointAgeText));
        Raise(nameof(ShowCheckpointAge));
        if (_ctxUsed > 0) WatchCheckpointAge();
        // A replayed transcript reports old usage; only live turns may nudge.
        if (_ctxUsed <= 0 || IsLoadingHistory) return;
        var percent = CtxPercentValue;
        if (!ContextCheckpointPolicy.Crossed(percent, AppSettings.Current.ContextCheckpointNudgePercent, ref _contextNudgeArmed)) return;
        if (_session is not ICompactableSession) OfferFreshChatHandoff(percent);
        _ = NudgeCheckpointAsync(ContextCheckpointPolicy.Note(percent));
    }

    private async Task NudgeCheckpointAsync(string note)
    {
        if (!await SteerAppNoteAsync(note)) _stagedContextNote = note;
    }

    private void OfferFreshChatHandoff(double percent)
    {
        _freshChatHandoffOffered = true;
        InsertBeforeQueued(new BannerItem
        {
            Level = "info",
            Text = $"Context is {percent:0}% full and {ProviderDisplay} cannot compact here. VibeCode asked the agent to save a checkpoint. " +
                   "Use \"Continue in a fresh chat\" beside the context readout to carry it into a new chat with an editable first prompt.",
        });
        ItemsChanged?.Invoke();
        Raise(nameof(FreshChatHandoffAvailable));
    }

    /// <summary>Claude's PreCompact hook: snapshot what the post-compaction recovery block will restore, as it stands
    /// right before the CLI compacts.</summary>
    private void OnPreCompact()
    {
        SaveTaskProgress("pre_compact", response: _checkpointTurnActive ? LastTurnReplyText() : null);
        if (_taskCheckpoint is { TurnState: "pre_compact" } snapshot) NoteCheckpointSaved(snapshot.SavedAt);
    }

    private void OnContextCompacted()
    {
        _pinnedMemoryPending = true;
        // A replayed transcript's old compactions are history, not a reason to spend a review.
        if (!IsLoadingHistory) QueueMemoryReview("it was compacted", MemoryReviewService.EventDelay);
    }

    /// <summary>One shared 30-second clock keeps every visible "checkpoint N min ago" honest.</summary>
    private void WatchCheckpointAge()
    {
        if (_checkpointAgeWatched) return;
        _checkpointAgeWatched = true;
        CheckpointAgeWatchers.Add(new WeakReference<ChatViewModel>(this));
        if (_checkpointAgeClock is not null) return;
        _checkpointAgeClock = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(30) };
        _checkpointAgeClock.Tick += (_, _) =>
        {
            CheckpointAgeWatchers.RemoveAll(watcher => !watcher.TryGetTarget(out var chat) || chat.Status == "closed");
            foreach (var watcher in CheckpointAgeWatchers)
                if (watcher.TryGetTarget(out var chat)) chat.Raise(nameof(CheckpointAgeText));
        };
        _checkpointAgeClock.Start();
    }

    private string MemoryReviewKey => Provider + ":" + SessionId;

    private bool MemoryReviewAllowed => MemoryReviewService.Enabled && SecondBrainActive && SessionId is not null
                                        && SubscriptionMemoryReviewRunner.Supports(Provider);

    /// <summary>A finished turn starts the idle clock; the next prompt stops it.</summary>
    private void ArmMemoryReviewIdle()
    {
        if (!MemoryReviewAllowed || Status == "closed") return;
        if (_memoryReviewIdle is null)
        {
            _memoryReviewIdle = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = MemoryReviewService.IdleDelay };
            _memoryReviewIdle.Tick += (_, _) => QueueMemoryReview("it was idle for 10 minutes", TimeSpan.Zero);
        }
        _memoryReviewIdle.Stop();
        _memoryReviewIdle.Start();
    }

    private void CancelMemoryReviewIdle()
    {
        _memoryReviewIdle?.Stop();
        if (MemoryReviewService.Enabled && SessionId is not null) MemoryReviewService.Instance.Cancel(MemoryReviewKey);
    }

    private void QueueMemoryReview(string trigger, TimeSpan delay)
    {
        _memoryReviewIdle?.Stop();
        if (!MemoryReviewAllowed) return;
        MemoryReviewService.Instance.Schedule(new MemoryReviewRequest(MemoryReviewKey, Provider, AccountId, ChosenModel, Cwd, Title,
            RecoveryCheckpoint, RecentReviewMessages(), trigger), delay);
    }

    private List<string> RecentReviewMessages() => Items
        .Select(item => item switch
        {
            UserItem { FromSubagent: false, IsUserPrompt: true } user => "User: " + user.Text,
            TextItem { HasText: true } reply => "Assistant: " + reply.Text,
            _ => null,
        })
        .OfType<string>()
        .TakeLast(16)
        .ToList();
}
