using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _isLocked;
    private bool _isTitleManual;
    private bool _hasGeneratedTitle;
    public bool IsLocked => _isLocked;
    public bool CanCloseChat => !IsLocked;
    public string ChatLockLabel => IsLocked ? "Unlock chat" : "Lock chat";
    public string ChatLockGlyph => IsLocked ? "\uE785" : "\uE72E";
    public string CloseChatToolTip => IsLocked ? "Unlock this chat before closing or deleting it" : "Close chat";
    public string DeleteChatToolTip => IsLocked ? CloseChatToolTip : "Delete chat";
    public string ChatDirectoryToolTip => "Working directory\n" + Cwd +
        (IsLocked ? "\nLocked — unlock from the chat menu to close or delete." : "");
    internal bool IsTitleManual => _isTitleManual;
    internal bool HasGeneratedTitle => _hasGeneratedTitle;
    private string PreserveChatTitleInstructions => _isTitleManual
        ? "This chat was named by the user. Preserve their exact title. Do not call chat_set_title or rename it, even if the task changes. Only the user can rename it."
        : "This chat already has its permanent title. Do not call chat_set_title or rename it, even if the task changes. Only the user can rename it.";

    internal void RefreshSavedChatTitle()
    {
        // A second view may have named this provider session while this view was already open.
        if (SessionId is { Length: > 0 } id
            && AppSettings.Current.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(Provider, id)) is { Title.Length: > 0 } saved
            && (saved.IsTitleManual || saved.HasGeneratedTitle))
            RestoreChatMetadata(Metadata with
            { Title = saved.Title, IsTitleManual = saved.IsTitleManual, HasGeneratedTitle = saved.HasGeneratedTitle });
    }

    private string ChatNamingSessionInstructions()
    {
        RefreshSavedChatTitle();
        return _isTitleManual || _hasGeneratedTitle ? PreserveChatTitleInstructions
            : AgentStatus.Mcp.Bridge.BridgeMcpTools.ChatTitleInstructions + BridgeMcpConnection.ChatTitleCallInstructions(Provider);
    }
    internal ChatMetadata Metadata => new()
    {
        IsLocked = IsLocked, Title = _isTitleManual || _hasGeneratedTitle ? Title : null,
        IsTitleManual = _isTitleManual, HasGeneratedTitle = _hasGeneratedTitle,
    };

    internal void RestoreChatMetadata(ChatMetadata? metadata)
    {
        if (metadata is null) return;
        _isLocked = metadata.IsLocked;
        _isTitleManual = metadata.IsTitleManual;
        _hasGeneratedTitle = metadata.HasGeneratedTitle;
        if (!string.IsNullOrWhiteSpace(metadata.Title)) Title = metadata.Title;
        RaiseChatMetadata();
    }

    internal void SetChatLocked(bool locked)
    {
        _isLocked = locked;
        RememberChatMetadata();
        RaiseChatMetadata();
    }

    public void RenameChat(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _isTitleManual = true;
        _hasGeneratedTitle = false;
        Title = title;
        RememberChatMetadata();
    }

    internal void SetGeneratedChatTitle(string title)
    {
        if (_isTitleManual || _hasGeneratedTitle) return;
        _hasGeneratedTitle = true;
        Title = title;
        RememberChatMetadata();
    }

    internal void RememberChatMetadata()
    {
        if (SessionId is not { Length: > 0 } id) return;
        var key = ChatMetadata.Key(Provider, id);
        if (IsLocked || _isTitleManual || _hasGeneratedTitle || AppSettings.Current.ChatMetadata.ContainsKey(key))
            AppSettings.Current.ChatMetadata[key] = Metadata;
    }

    private void RaiseChatMetadata()
    {
        foreach (var property in new[] { nameof(IsLocked), nameof(CanCloseChat), nameof(ChatLockLabel),
                     nameof(ChatLockGlyph), nameof(CloseChatToolTip), nameof(DeleteChatToolTip), nameof(ChatDirectoryToolTip) }) Raise(property);
    }
}
