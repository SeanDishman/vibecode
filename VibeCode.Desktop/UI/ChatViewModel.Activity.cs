using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private DateTimeOffset? _lastActivityAt;
    private DateTimeOffset? _lastAssistantAt;
    private DateTimeOffset? _lastAgentActivityAt;
    private bool _assistantActivityRecorded;
    private bool _activityTurnStarted;

    public DateTimeOffset? LastActivityAt => _lastActivityAt;
    internal DateTimeOffset? LastAssistantAt => _lastAssistantAt;
    /// <summary>Last observed agent turn activity. Opening a chat, starting its CLI, and receiving mail do not count.</summary>
    internal DateTimeOffset? LastAgentActivityAt => _lastAgentActivityAt;
    public DateTimeOffset SidebarActivityAt => _lastAssistantAt ?? _lastActivityAt ?? DateTimeOffset.MinValue;

    private void TrackAgentActivity(bool wasActive)
    {
        if (!wasActive && Status is not ("running" or "preparing")) return;
        _lastAgentActivityAt = DateTimeOffset.UtcNow;
        RememberChatMetadata();
        Raise(nameof(LastAgentActivityAt));
    }

    internal void RecordUserActivity()
    {
        _assistantActivityRecorded = false;
        _activityTurnStarted = true;
        RecordActivity(assistant: false);
    }

    // First output and turn completion reorder once each; streaming tokens do not rebuild the sidebar.
    private void RecordAssistantActivity()
    {
        if (!_activityTurnStarted || _assistantActivityRecorded) return;
        _assistantActivityRecorded = true;
        RecordActivity(assistant: true);
    }

    internal void RecordTurnActivity()
    {
        if (!_activityTurnStarted) return;
        RecordActivity(assistant: _assistantActivityRecorded);
        _activityTurnStarted = false;
    }

    internal void RecordActivity(bool assistant)
    {
        var previousRank = SidebarActivityAt;
        _lastActivityAt = DateTimeOffset.UtcNow;
        if (assistant) _lastAssistantAt = _lastActivityAt;
        RememberChatMetadata();
        Raise(nameof(LastActivityAt));
        if (SidebarActivityAt != previousRank) Raise(nameof(SidebarActivityAt));
    }

    internal void AdoptHistoryActivity(SessionEntry session)
    {
        var activity = new DateTimeOffset(session.LastModified).ToUniversalTime();
        if (_lastActivityAt is null || activity > _lastActivityAt) _lastActivityAt = activity;
        if (session.LastAssistantAt is { } reply && (_lastAssistantAt is null || reply > _lastAssistantAt))
            _lastAssistantAt = reply;
        RememberChatMetadata();
    }
}
