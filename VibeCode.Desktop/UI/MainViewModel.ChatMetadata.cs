using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    public void ToggleChatLock(ChatViewModel chat)
    {
        chat.SetChatLocked(!chat.IsLocked);
        SaveEverything();
    }

    public void RenameChat(ChatViewModel chat, string title)
    {
        chat.RenameChat(title);
        RefreshChatTitleInHistory(chat);
        SaveSession();
    }

    private static bool ChatRemovalBlocked(ChatViewModel chat)
    {
        if (!chat.IsLocked) return false;
        const string message = "This chat is locked. Right-click it and choose Unlock chat before closing or deleting it.";
        if (chat.Items.LastOrDefault() is not BannerItem { Text: message })
            chat.Items.Add(new BannerItem { Level = "info", Text = message });
        return true;
    }

    private JsonObject SetChatTitleFromMcp(ChatViewModel chat, JsonObject args)
    {
        if (chat.Status == "closed") throw new StatusValidationException("This chat is closed.");
        chat.RefreshSavedChatTitle();
        if (chat.IsTitleManual)
            return new() { ["applied"] = false, ["title"] = chat.Title, ["reason"] = "The user named this chat. Preserve their title." };
        if (chat.HasGeneratedTitle)
            return new() { ["applied"] = false, ["title"] = chat.Title, ["reason"] = "This chat already has its AI title. Only the user can rename it. Do not retry." };
        var oldTitle = chat.Title;
        var oldMetadata = chat.Metadata;
        chat.SetGeneratedChatTitle(string.Join(" ", args["title"]!.GetValue<string>().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        SnapshotSession();
        if (AppSettings.Current.TrySave() is { } error)
        {
            chat.RestoreChatMetadata(oldMetadata);
            chat.Title = oldTitle;
            chat.RememberChatMetadata();
            SnapshotSession();
            throw new StatusValidationException("Could not save the chat name. Retry after storage access is restored: " + error.Message);
        }
        RefreshChatTitleInHistory(chat);
        return new() { ["applied"] = true, ["title"] = chat.Title };
    }

    private static SessionEntry WithChatTitle(SessionEntry session) =>
        AppSettings.Current.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(session.Provider, session.SessionId))?.Title is { Length: > 0 } title
            ? session with { Title = title } : session;

    private void RefreshChatTitleInHistory(ChatViewModel chat)
    {
        foreach (var sessions in Projects.Select(p => p.Sessions).Append(RecentSessions))
            for (var i = 0; i < sessions.Count; i++)
                if (sessions[i].SessionId == chat.SessionId && sessions[i].Provider == chat.Provider)
                    sessions[i] = WithChatTitle(sessions[i]);
    }
}
