using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private static JarvisChatSummary JarvisSummary(ChatViewModel chat) =>
        new(chat.BridgeAgentId, chat.Title, chat.Cwd, chat.Status, chat.IsLocked);

    Task<JarvisActionReceipt> IJarvisChatActionHost.ExecuteChatActionAsync(JarvisAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filter = action.ChatFilter ?? throw new ArgumentException("A chat action needs a filter.");
        JarvisChatPolicy.Validate(action.Kind, filter);
        // Snapshot before closing anything: closing the active chat changes selection and the collection.
        var matches = Chats.Where(chat => JarvisChatPolicy.Matches(JarvisSummary(chat), filter,
            chat.JarvisSearchableMessages())).ToArray();
        if (matches.Length == 0) return Receipt("No open chats match that request.");

        if (action.Kind == "list_chats")
            return Receipt($"Found {matches.Length} matching open chat{(matches.Length == 1 ? "" : "s")}:\n"
                + Describe(matches));
        if (action.Kind == "open_chat")
        {
            if (matches.Length != 1)
                return Receipt("More than one chat matches. Tell me the title or directory of the one to open:\n" + Describe(matches));
            var chat = matches[0];
            OpenChat(chat);
            if (!ReferenceEquals(ActiveChat, chat)) throw new InvalidOperationException("VibeCode did not activate that chat.");
            return Receipt($"Opened \"{chat.Title}\" in {chat.Cwd}.");
        }

        var closed = new List<ChatViewModel>();
        var locked = new List<ChatViewModel>();
        var failed = new List<string>();
        var cancelled = false;
        foreach (var chat in matches)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            if (!Chats.Contains(chat)) continue;
            // A bridge host closes its peers too. Preserve their locks as well as the sidebar row's lock.
            if (chat.IsLocked || PeersOf(chat).Any(peer => peer.IsLocked)) { locked.Add(chat); continue; }
            try
            {
                CloseChat(chat);
                if (Chats.Contains(chat)) failed.Add(chat.Title);
                else
                {
                    closed.Add(chat);
                    if (ReferenceEquals(_jarvisOpenedChat, chat)) _jarvisOpenedChat = null;
                }
            }
            catch (Exception)
            {
                // Count a removal that completed before a later UI refresh failed; never claim all succeeded.
                if (!Chats.Contains(chat)) closed.Add(chat);
                else failed.Add(chat.Title);
            }
        }
        var message = closed.Count == 0 ? "No chats were closed."
            : $"Closed {closed.Count} chat{(closed.Count == 1 ? "" : "s")}: {Names(closed)}. Chat history is preserved.";
        if (locked.Count > 0)
            message += $" Left {locked.Count} locked chat{(locked.Count == 1 ? "" : "s")} or bridges with locked chats open: {Names(locked)}.";
        if (failed.Count > 0) message += " Could not close: " + string.Join(", ", failed) + ".";
        if (cancelled) message += " Stopped before closing the remaining chats.";
        return Receipt(message);

        Task<JarvisActionReceipt> Receipt(string message) => Task.FromResult(new JarvisActionReceipt(action.Kind, action.Path, message));
        static string Names(IEnumerable<ChatViewModel> chats) => string.Join(", ", chats.Select(chat => $"\"{chat.Title}\""));
        static string Describe(IEnumerable<ChatViewModel> chats) => string.Join("\n", chats.Select(chat =>
            $"- {chat.Title} — {chat.Cwd}{(chat.IsLocked ? " (locked)" : "")} [chat_id: {chat.BridgeAgentId}]"));
    }
}
