using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.Services;

/// <summary>
/// The targeted reads behind bridge_read_chat's view argument. The default read stays the full lossless transcript, but
/// paging through a peer's whole history to learn what it decided pours that history into the reader's own context and
/// brings its compaction sooner. Each view is one small, bounded page: the peer's saved checkpoint, only the human's
/// messages, the newest messages first, or the messages that mention a term.
/// </summary>
public static class BridgeChatViews
{
    public static readonly IReadOnlyList<string> Names = ["checkpoint", "user_messages", "tail", "query"];
    public const int PageCharacters = 16_000;
    public const int MessageCharacters = 1_500;
    public const int TermMaxLength = 120;
    private const int MaxMessages = 40;

    public static JsonObject Build(string view, string? term, BridgeChatSnapshot chat, TaskCheckpoint? checkpoint)
    {
        if (!Names.Contains(view))
            throw new StatusValidationException("view must be checkpoint, user_messages, tail or query; omit it for the full read.");
        if (view != "query" && !string.IsNullOrEmpty(term))
            throw new StatusValidationException("term is only used with view=query.");
        var page = new JsonObject
        {
            ["agent_id"] = chat.ChatId, ["agent_number"] = chat.AgentNumber, ["provider"] = chat.Provider,
            ["title"] = chat.Title, ["status"] = chat.Status, ["view"] = view,
            ["note"] = "Targeted, bounded view. Omit view and page with cursor for the full lossless transcript.",
        };
        if (view == "checkpoint")
        {
            page["available"] = checkpoint is not null;
            if (checkpoint is null)
            {
                page["note"] = "This agent has not saved a checkpoint yet. Use view=tail for its latest messages, or omit view for the full transcript.";
                return page;
            }
            page["checkpoint"] = new JsonObject
            {
                ["saved_at"] = checkpoint.SavedAt.ToString("O"), ["turn_state"] = checkpoint.TurnState,
                ["explicit"] = checkpoint.Explicit, ["objective"] = checkpoint.Objective,
                ["constraints"] = Rows(checkpoint.Constraints), ["decisions"] = Rows(checkpoint.Decisions),
                ["completed"] = Rows(checkpoint.Completed), ["remaining"] = Rows(checkpoint.Remaining),
                ["evidence"] = Rows(checkpoint.Evidence),
            };
            return page;
        }

        IEnumerable<BridgeChatMessage> source = chat.Messages;
        var cleanTerm = term?.Trim() ?? "";
        if (view == "user_messages") source = source.Where(m => m.Role == "user");
        else if (view == "tail") source = source.Reverse();
        else
        {
            if (cleanTerm.Length == 0 || cleanTerm.Length > TermMaxLength)
                throw new StatusValidationException($"view=query needs a term of 1 to {TermMaxLength} characters.");
            source = source.Where(m => m.Text.Contains(cleanTerm, StringComparison.OrdinalIgnoreCase)).Reverse();
            page["term"] = cleanTerm;
        }

        var matching = source.ToList();
        var messages = new JsonArray();
        var used = 0;
        foreach (var message in matching)
        {
            if (messages.Count >= MaxMessages) break;
            var text = Excerpt(message.Text, view == "query" ? cleanTerm : null, out var cut);
            if (used + text.Length > PageCharacters && messages.Count > 0) break;
            used += text.Length;
            messages.Add(new JsonObject
            {
                ["index"] = message.Index, ["role"] = message.Role, ["tool"] = message.Tool, ["text"] = text,
                ["truncated"] = cut, ["in_progress"] = message.InProgress,
            });
        }
        page["order"] = view == "user_messages" ? "oldest_first" : "newest_first";
        page["messages"] = messages;
        page["matching_messages"] = matching.Count;
        page["omitted_messages"] = matching.Count - messages.Count;
        return page;
    }

    private static JsonArray Rows(IEnumerable<string> rows) => new(rows.Select(row => (JsonNode?)JsonValue.Create(row)).ToArray());

    /// <summary>At most <see cref="MessageCharacters"/>, centred on the first match of <paramref name="term"/>.</summary>
    private static string Excerpt(string text, string? term, out bool cut)
    {
        cut = text.Length > MessageCharacters;
        if (!cut) return text;
        var at = term is null ? 0 : Math.Max(0, text.IndexOf(term, StringComparison.OrdinalIgnoreCase));
        var start = Math.Clamp(at - MessageCharacters / 3, 0, text.Length - MessageCharacters);
        return (start > 0 ? "…" : "") + text.Substring(start, MessageCharacters) + (start + MessageCharacters < text.Length ? "…" : "");
    }
}
