using System.IO;

namespace VibeCode.Services;

public sealed record JarvisChatSummary(string Id, string Title, string Path, string Status, bool IsLocked);

public sealed record JarvisChatFilter(string? ChatId = null, string? Directory = null,
    IReadOnlyList<string>? Terms = null, string Match = "all", string SearchIn = "all",
    bool IncludeSubdirectories = false, bool All = false);

/// <summary>Searches live chat data locally; transcripts are never sent to Jarvis just to find a match.</summary>
public static class JarvisChatPolicy
{
    public static void Validate(string kind, JarvisChatFilter filter)
    {
        if (kind is not ("list_chats" or "open_chat" or "close_chats"))
            throw new ArgumentException("Unsupported chat action.");
        if (filter.Match is not ("all" or "any") || filter.SearchIn is not ("all" or "title" or "messages"))
            throw new ArgumentException("Choose all/any matching and all/title/messages search.");
        if (filter.Terms is { } terms && (terms.Count > 20 || terms.Any(term =>
                string.IsNullOrWhiteSpace(term) || term.Length > 500 || term.Contains('\0'))))
            throw new ArgumentException("Use at most 20 nonempty search words or phrases.");
        if (!string.IsNullOrEmpty(filter.Directory)) JarvisPathPolicy.Normalize(filter.Directory);
        if (filter.IncludeSubdirectories && string.IsNullOrEmpty(filter.Directory))
            throw new ArgumentException("Subfolder matching needs a directory.");
        var hasSelector = !string.IsNullOrEmpty(filter.ChatId) || !string.IsNullOrEmpty(filter.Directory)
            || filter.Terms is { Count: > 0 };
        if (filter.All && hasSelector)
            throw new ArgumentException("Use all chats or a chat filter, not both.");
        if (kind == "open_chat" && (!hasSelector || filter.All))
            throw new ArgumentException("Specify which chat to open.");
        if (kind == "close_chats" && !hasSelector && !filter.All)
            throw new ArgumentException("Specify chats to close, or explicitly request all chats.");
    }

    public static bool Matches(JarvisChatSummary chat, JarvisChatFilter filter, IEnumerable<string> messages)
    {
        if (!string.IsNullOrEmpty(filter.ChatId) && !string.Equals(chat.Id, filter.ChatId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(filter.Directory) && !MatchesDirectory(chat.Path, filter.Directory, filter.IncludeSubdirectories)) return false;
        if (filter.Terms is not { Count: > 0 } terms) return true;
        // Match each term separately so "all" can find words spread across different messages.
        var found = new bool[terms.Count];
        bool Scan(string text)
        {
            for (var i = 0; i < terms.Count; i++)
                found[i] |= text.Contains(terms[i], StringComparison.OrdinalIgnoreCase);
            return filter.Match == "any" ? found.Any(value => value) : found.All(value => value);
        }
        if (filter.SearchIn != "messages" && Scan(chat.Title)) return true;
        if (filter.SearchIn != "title")
            foreach (var message in messages)
                if (Scan(message)) return true;
        return false;
    }

    private static bool MatchesDirectory(string candidate, string directory, bool descendants)
    {
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || descendants && path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
