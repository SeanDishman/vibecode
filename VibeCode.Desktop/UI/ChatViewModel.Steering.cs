using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _steerSubmitting;
    private bool _steerQueueHold;

    /// <summary>Providers whose running turn can take a message without being interrupted (see
    /// <see cref="ISteerableSession"/>). Kimi's ACP adapter rejects any prompt while a turn runs, so it only queues.</summary>
    public bool SupportsSteer => IsCodex || IsClaude || IsGlm || IsGrok;

    /// <summary>A running root turn can accept guidance without starting another turn.</summary>
    public bool CanSteer => HasSteerableTurn && !_steerSubmitting && !_steerQueueHold;
    private bool HasSteerableTurn => SupportsSteer && !RewindHoldsDispatch && !_interruptRequested
        && !_sendAllQueuedNowRequested && _status == "running"
        && _session is ISteerableSession { CanSteer: true };

    /// <summary>Move a queued follow-up into the active turn, restoring its FIFO position if the provider rejects it.</summary>
    public async Task<bool> SteerQueuedAsync(QueuedItem queued)
    {
        if (!CanSteer || queued.Extended || queued.UseSwarm
            || GoalPolicy.TryParseCommand(queued.Text, out _)
            || !_sendQueue.TryPeek(out var head) || !ReferenceEquals(head, queued)) return false;
        _steerQueueHold = true;
        Raise(nameof(CanSteer));
        Raise(nameof(CanSendQueuedNow));
        try
        {
            if (!CancelQueued(queued)) return false;
            ItemsChanged?.Invoke();
            if (await SteerCoreAsync(queued.Text, queued.Attachments, fromQueue: true)) return true;

            // Removing the item before awaiting also keeps prompts typed during the request out of its snapshot.
            var remaining = _sendQueue.ToArray();
            _sendQueue.Clear();
            _sendQueue.Enqueue(queued);
            queued.IsQueueHead = true;
            foreach (var next in remaining)
            {
                next.IsQueueHead = false;
                _sendQueue.Enqueue(next);
            }
            Items.Add(queued);
            PinQueuedItemsToEnd();
            RefreshQueueState();
            ItemsChanged?.Invoke();
            return false;
        }
        finally
        {
            _steerQueueHold = false;
            Raise(nameof(CanSteer));
            Raise(nameof(CanSendQueuedNow));
            if (_status == "idle") Post(FlushQueue);
        }
    }

    /// <summary>Accept a same-turn message. The composer should clear only after this returns true.</summary>
    public Task<bool> SteerAsync(string text, IReadOnlyList<Attachment>? attachments = null) =>
        GoalPolicy.TryParseCommand(text, out _)
            ? Task.FromResult(Send(text, attachments))
            : SteerCoreAsync(text, attachments, fromQueue: false);

    private async Task<bool> SteerCoreAsync(string text, IReadOnlyList<Attachment>? attachments, bool fromQueue)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        var atts = attachments is { Count: > 0 } ? attachments.ToList() : null;
        if (!hasText && atts is null) return false;
        var preservedMessage = fromQueue ? "Your message remains queued." : "Your message is still in the composer.";
        if (!HasSteerableTurn || _steerSubmitting || (!fromQueue && _steerQueueHold)
            || _session is not ISteerableSession session)
        {
            InsertBeforeQueued(new BannerItem
            {
                Level = "info",
                Text = $"{ProviderDisplay} cannot accept steering right now. " + preservedMessage,
            });
            ItemsChanged?.Invoke();
            return false;
        }

        _steerSubmitting = true;
        Raise(nameof(CanSteer));
        Raise(nameof(CanSendQueuedNow));
        try
        {
            await session.SteerAsync(BuildContent(text, hasText, atts));
        }
        catch (Exception ex)
        {
            InsertBeforeQueued(new BannerItem
            {
                Level = "error",
                Text = $"{ProviderDisplay} did not confirm the steer ({ex.Message}). {preservedMessage} Check the active turn before retrying.",
            });
            ItemsChanged?.Invoke();
            return false;
        }
        finally
        {
            _steerSubmitting = false;
            Raise(nameof(CanSteer));
            Raise(nameof(CanSendQueuedNow));
            if (!_steerQueueHold && _status == "idle") Post(FlushQueue);
        }
        if (hasText) _promptHistory.Record(text);
        _lastLocalUserText = hasText ? text : null;
        _lastLocalUserAt = DateTime.UtcNow;
        if (_activeMemoryPrompt is { } prompt)
            _activeMemoryPrompt = prompt + "\n\n[User steered the active turn]\n" + MemoryPrompt(text, atts);
        InsertBeforeQueued(new UserItem { Text = text, Attachments = atts, Owner = this });
        ItemsChanged?.Invoke();
        MessageSent?.Invoke();
        return true;
    }

    /// <summary>A regular bridge asks each agent for its pane title in a note riding every new request, so a chat that
    /// becomes a bridge agent part-way through a turn stayed untitled until the user next wrote to it. Steer the
    /// question into the running turn instead. It is the app's note, not the user's message: no bubble, prompt history
    /// or memory record. Nothing is sent when the turn can't take it (idle, Kimi); the next request then asks as usual.</summary>
    internal async Task<bool> RequestBridgeTaskTitleNowAsync()
    {
        if (!UsesBridgeTaskTitle || BridgeHeaderTaskTitle.Length > 0 || !CanSteer
            || _session is not ISteerableSession session) return false;
        BridgeTaskTitleChangesThisTurn = 0;   // a count left from an earlier bridge must not refuse this title
        _steerSubmitting = true;
        Raise(nameof(CanSteer));
        Raise(nameof(CanSendQueuedNow));
        try
        {
            await session.SteerAsync(BuildContent(BridgeTaskTitlePolicy.JoinReminder(Provider), true, null));
            return true;
        }
        catch (Exception)
        {
            return false;   // best effort: the next request still carries the usual reminder
        }
        finally
        {
            _steerSubmitting = false;
            Raise(nameof(CanSteer));
            Raise(nameof(CanSendQueuedNow));
            if (!_steerQueueHold && _status == "idle") Post(FlushQueue);
        }
    }
}
