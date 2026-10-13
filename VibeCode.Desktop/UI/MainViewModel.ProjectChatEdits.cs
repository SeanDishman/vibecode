using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private JsonObject ListProjectChatEdits(ChatViewModel caller, IReadOnlyList<ChatViewModel> peers, JsonObject args)
    {
        var root = ProjectChatRoot(caller);
        var now = DateTimeOffset.UtcNow;
        var path = EditQueryPath(root, args["path"]?.GetValue<string>());
        var first = args["start_line"]?.GetValue<int>();
        var last = args["end_line"]?.GetValue<int>();
        if ((first ?? last) is not null && path is null)
            throw new StatusValidationException("start_line and end_line need a file path.");
        first ??= last;
        last ??= first;
        if (first > last) (first, last) = (last, first);
        var agent = args["agent"]?.GetValue<string>() is { } id ? ResolveProjectPeer(caller, peers, id).BridgeAgentId : null;
        var since = args["since_minutes"]?.GetValue<int>() is { } minutes ? now.AddMinutes(-minutes) : DateTimeOffset.MinValue;
        var matches = BridgeEditLedger.Read(root, now).Where(e => e.At >= since
            && (path is null || EditPathMatches(e.File, path)) && (agent is null || agent == e.AgentId)
            && (first is null || e.Ranges.Count == 0 || e.Ranges.Any(r => r.Overlaps(first.Value, last!.Value)))).ToArray();
        var shown = matches.Take(args["limit"]?.GetValue<int>() ?? DefaultEditLimit).ToArray();
        return new JsonObject
        {
            ["scope"] = "project",
            ["project"] = root,
            ["kept_days"] = (int)BridgeEditLedger.Retention.TotalDays,
            ["matched"] = matches.Length,
            ["returned"] = shown.Length,
            ["has_more"] = shown.Length < matches.Length,
            ["edits"] = new JsonArray(shown.Select(e =>
            {
                var live = peers.FirstOrDefault(p => p.BridgeAgentId == e.AgentId);
                return (JsonNode?)new JsonObject
                {
                    ["agent_id"] = e.AgentId,
                    ["label"] = live?.Title ?? e.Label,
                    ["message_recipient"] = live is null ? null : BridgeMessageRecipient(live, peers),
                    ["you"] = caller.BridgeAgentId == e.AgentId,
                    ["role"] = e.Role,
                    ["task"] = e.Task,
                    ["when"] = e.At.ToString("O"),
                    ["age"] = Age(now - e.At),
                    ["file"] = e.File,
                    ["change"] = e.Change,
                    ["lines"] = e.Lines,
                    ["lines_exact"] = e.Exact,
                    ["added"] = e.Added,
                    ["removed"] = e.Removed,
                };
            }).ToArray()),
            ["note"] = "Recent file activity is history, never an ownership lock. Check overlapping lines before changing them. Continue independently; message only when useful or urgent.",
        };
    }
}
