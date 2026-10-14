namespace VibeCode.UI;

/// <summary>The choices confirmed before a new folder chat starts. Existing chats and defaults are untouched.</summary>
public sealed record NewChatSelection(string Provider, string AccountId, string? Model)
{
    public void Apply(ChatViewModel chat)
    {
        if (chat.Provider != Provider || chat.AccountId != AccountId)
            throw new ArgumentException("The chat must be created with the selected provider and account.", nameof(chat));
        chat.Model = Model;
        chat.RefreshModelPicker();
    }
}
