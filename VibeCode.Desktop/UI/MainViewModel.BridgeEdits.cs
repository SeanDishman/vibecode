using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The Bridge edit log: which agent changed which lines of which file. Every successful file edit made by a bridge
/// pane (any provider, regular Bridge or Advanced Bridge orchestrator/worker) is recorded by the app, and agents read
/// the log through bridge_file_edits and the recent_edits roster field. Storage and retention: <see cref="BridgeEditLedger"/>.
/// </summary>
public sealed partial class MainViewModel
{
    private const int DefaultEditLimit = 30;
    private const int RecentEditsPerAgent = 3;
    private const int EditFileSummaryLimit = 15;

    private void RecordBridgeEdit(ChatViewModel pane, BridgeEditObservation edit)
    {
        if (!pane.IsBridgeAgent || pane.Status == "closed" || !TryGetLiveBridge(pane, out var bridge) || bridge.Panes.Count == 0) return;
        var role = pane.IsBridgeManager ? "orchestrator" : pane.BridgeCoordinatorAgentId is not null ? "worker" : "agent";
        var runId = bridge.Panes.Select(p => p.BridgeWork).FirstOrDefault(w => w is not null)?.RunId;
        _ = BridgeEditLedger.Record(bridge.Panes[0].Cwd, new BridgeEditAuthor(pane.BridgeAgentId, BridgeNumberOf(pane),
            pane.BridgeLabel, pane.Provider, pane.Model, role, pane.BridgeHeaderTaskTitle, runId), edit);
    }

    private JsonObject ListBridgeFileEdits(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        var root = Path.GetFullPath(bridge.Panes[0].Cwd);
        var now = DateTimeOffset.UtcNow;
        var path = EditQueryPath(root, args["path"]?.GetValue<string>());
        var first = args["start_line"]?.GetValue<int>();
        var last = args["end_line"]?.GetValue<int>();
        if ((first ?? last) is not null && path is null)
            throw new StatusValidationException("start_line and end_line need path: name the file whose lines you are asking about, e.g. path=\"src/app.ts\".");
        first ??= last;
        last ??= first;
        if (first > last) (first, last) = (last, first);
        var agentId = args["agent"]?.GetValue<string>() is { } agent ? EditAgentId(caller, bridge, agent) : null;
        var since = args["since_minutes"]?.GetValue<int>() is { } minutes ? now.AddMinutes(-minutes) : DateTimeOffset.MinValue;

        var matches = BridgeEditLedger.Read(root, now).Where(e => e.At >= since
            && (path is null || EditPathMatches(e.File, path))
            && (agentId is null || e.AgentId == agentId)
            // An edit with unknown lines, or a deleted file, may have touched the range, so it stays in.
            && (first is null || e.Ranges.Count == 0 || e.Ranges.Any(r => r.Overlaps(first.Value, last!.Value)))).ToList();
        var shown = matches.Take(args["limit"]?.GetValue<int>() ?? DefaultEditLimit).ToList();
        var result = new JsonObject
        {
            ["project"] = root,
            ["kept_days"] = (int)BridgeEditLedger.Retention.TotalDays,
            ["matched"] = matches.Count,
            ["returned"] = shown.Count,
            ["has_more"] = matches.Count > shown.Count,
            ["edits"] = new JsonArray(shown.Select(e => (JsonNode?)EditJson(e, caller, bridge, now)).ToArray()),
        };
        if (path is null && matches.Count > 0)
            result["files"] = new JsonArray(matches.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase).Take(EditFileSummaryLimit)
                .Select(file => (JsonNode?)new JsonObject
                {
                    ["file"] = file.Key, ["edits"] = file.Count(),
                    ["agents"] = new JsonArray(file.Select(e => EditAgentLabel(e, bridge)).Distinct()
                        .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
                    ["last_edit"] = Age(now - file.First().At),
                }).ToArray());
        result["note"] = matches.Count == 0
            ? "No recorded edits match. The log holds file edits made by Bridge and Advanced Bridge agents in this project during the last 7 days."
            : "lines = where each change landed right after that edit; later edits above it can shift them. History, never a lock: message the agent before reworking its recent lines.";
        return result;
    }

    /// <summary>The newest few files an agent edited, for its bridge_list_agents row: "src/app.ts: 120-135 (4 min ago)".</summary>
    private static JsonArray RecentEditsJson(string agentId, IReadOnlyList<BridgeEditEntry> edits, DateTimeOffset now) =>
        new(edits.Where(e => e.AgentId == agentId).GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase).Take(RecentEditsPerAgent)
            .Select(file => (JsonNode?)JsonValue.Create($"{file.Key}: {file.First().Lines} ({Age(now - file.First().At)})")).ToArray());

    private static IReadOnlyList<BridgeEditEntry> BridgeEditsFor(LiveBridge bridge) =>
        bridge.Panes.Count == 0 ? [] : BridgeEditLedger.Read(bridge.Panes[0].Cwd, DateTimeOffset.UtcNow);

    private static JsonObject EditJson(BridgeEditEntry e, ChatViewModel caller, LiveBridge bridge, DateTimeOffset now)
    {
        var live = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == e.AgentId);
        return new JsonObject
        {
            ["when"] = e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["age"] = Age(now - e.At),
            ["agent"] = EditAgentLabel(e, bridge),
            ["agent_number"] = live is null ? e.Agent : BridgeNumberOf(live),
            ["agent_id"] = e.AgentId,
            ["message_recipient"] = live is null ? null : BridgeMessageRecipient(live, bridge.Panes),
            ["you"] = e.AgentId == caller.BridgeAgentId,
            ["in_your_bridge"] = live is not null,
            ["role"] = e.Role,
            ["task"] = e.Task,
            ["file"] = e.File,
            ["change"] = e.Change,
            ["lines"] = e.Lines,
            ["lines_exact"] = e.Exact,
            ["added"] = e.Added,
            ["removed"] = e.Removed,
            ["provider"] = e.Provider,
            ["model"] = e.Model,
        };
    }

    /// <summary>The agent's current roster label when it is still here (numbers change when peers leave), else the
    /// label it had when it made the edit.</summary>
    private static string EditAgentLabel(BridgeEditEntry e, LiveBridge bridge) =>
        bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == e.AgentId)?.BridgeLabel is { Length: > 0 } label ? label : e.Label;

    /// <summary>Accepts what a model is likely to pass: me, a message_recipient or agent_id, a label (Codex 3), or a
    /// roster number (3, Agent 3, #3). A full agent_id from an earlier result also finds agents that have left.</summary>
    private static string EditAgentId(ChatViewModel caller, LiveBridge bridge, string text)
    {
        var value = text.Trim();
        if (value.ToLowerInvariant() is "me" or "self" or "myself" or "mine") return caller.BridgeAgentId;
        foreach (var pane in bridge.Panes)
            if (pane.BridgeAgentId == value || BridgeMessageRecipient(pane, bridge.Panes) == value
                || string.Equals(pane.BridgeLabel, value, StringComparison.OrdinalIgnoreCase))
                return pane.BridgeAgentId;
        var digits = (value.StartsWith("agent", StringComparison.OrdinalIgnoreCase) ? value[5..] : value).Trim().TrimStart('#');
        if (int.TryParse(digits, out var number) && bridge.Panes.FirstOrDefault(p => BridgeNumberOf(p) == number) is { } numbered)
            return numbered.BridgeAgentId;
        if (value.Length == 32 && value.All(char.IsAsciiHexDigit)) return value;
        throw new StatusValidationException($"agent '{value}' is not in this bridge. Use a message_recipient or agent_id from bridge_list_agents, " +
            "an agent number such as 3, or me. Omit agent to see every agent's edits.");
    }

    private static string? EditQueryPath(string root, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().Trim('"', '\'', '`');
        if (Path.IsPathRooted(value))
        {
            try { value = BridgeEditLedger.ProjectPath(root, Path.GetFullPath(value)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        value = value.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimEnd('/');
        return value.Length == 0 || value == "." ? null : value;
    }

    /// <summary>The exact path, a folder that contains it, or a trailing part of it ("Items.cs", "UI/Items.cs", "UI").</summary>
    private static bool EditPathMatches(string file, string query) =>
        string.Equals(file, query, StringComparison.OrdinalIgnoreCase)
        || file.StartsWith(query + "/", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith("/" + query, StringComparison.OrdinalIgnoreCase)
        || file.Contains("/" + query + "/", StringComparison.OrdinalIgnoreCase);

    private static string Age(TimeSpan age) => age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} days ago";
}
