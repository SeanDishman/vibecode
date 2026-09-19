using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.Services;

// Passive runtime metadata plus bounded, explicitly submitted MCP status summaries. No automatic
// capture of prompts, commands, arguments or tool output. Reports do not grant any permissions.
internal static class MitreRuntimeTelemetry
{
    public static JsonObject? FromNotification(string method, JsonObject? p, string? rootId)
    {
        if (rootId is null || p is null) return null;
        var threadId = String(p["threadId"]) ?? rootId;
        if (method == "thread/settings/updated")
            return Access(p["threadSettings"] ?? p["settings"] ?? p, rootId, threadId, "server");
        if (method == "thread/tokenUsage/updated")
        {
            // This is the cumulative total for this exact thread, including earlier turns on resume.
            // Cached input and reasoning output are already included; never add those subsets again.
            var total = (p["tokenUsage"] ?? p["usage"])?["total"];
            if (TokenCount(total?["totalTokens"] ?? total?["total_tokens"]) is not { } tokens) return null;
            var usage = Envelope(rootId, threadId, "token_usage");
            usage["turnId"] = String(p["turnId"]);
            usage["totalTokens"] = tokens;
            return usage;
        }
        var item = p["item"];
        var kind = method switch
        {
            "turn/started" => "turn_started",
            "turn/completed" => "turn_completed",
            "item/started" when Operation(String(item?["type"])) is not null => "operation_started",
            "item/completed" when Operation(String(item?["type"])) is not null => "operation_completed",
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"
                or "item/permissions/requestApproval" => "approval_requested",
            "serverRequest/resolved" => "approval_resolved",
            "error" => "runtime_error",
            _ => null,
        };
        if (kind is null) return null;
        var e = Envelope(rootId, threadId, kind);
        e["turnId"] = String(p["turnId"]) ?? String(p["turn"]?["id"]);
        e["itemId"] = String(item?["id"]);
        e["operation"] = DescribeOperation(item);
        e["status"] = Status(String(item?["status"]) ?? String(p["turn"]?["status"])
            ?? (kind == "operation_completed" ? "completed" : kind == "operation_started" ? "inProgress" : null));
        if (kind == "operation_completed" && String(item?["type"]) == "commandExecution"
            && item?["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var code) && code != 0) e["status"] = "failed";
        if (kind == "operation_completed" && String(item?["type"]) == "agentMessage"
            && MitreTacticCatalog.ParseReport(String(item?["text"])) is { } report)
        {
            e["tacticReported"] = true;
            e["tacticId"] = report.Tactic?.Id;
        }
        if (kind == "operation_completed" && AgentStatusMcpRegistration.ReadReceipt(item) is { } mcpReport)
            e["agentReport"] = mcpReport.ToJson();
        return e;
    }

    public static JsonObject Access(JsonNode? settings, string rootId, string threadId, string source)
    {
        var policy = settings?["sandboxPolicy"] ?? settings?["sandbox"];
        var type = policy is JsonObject ? String(policy["type"]) : null;
        var profileId = String(settings?["activePermissionProfile"]?["id"]);
        var e = Envelope(rootId, threadId, "access");
        e["access"] = new JsonObject
        {
            ["sandbox"] = type is "readOnly" or "workspaceWrite" or "dangerFullAccess" or "externalSandbox" ? type : "unknown",
            ["network"] = policy is JsonObject && policy["networkAccess"] is JsonValue network && network.TryGetValue<bool>(out var allowed)
                ? (allowed ? "enabled" : "blocked") : policy is JsonObject && String(policy["networkAccess"]) is "enabled" or "restricted"
                    ? String(policy["networkAccess"]) : type == "dangerFullAccess" ? "enabled" : "unknown",
            ["approval"] = String(settings?["approvalPolicy"]) is "never" or "on-request" or "on-failure" or "untrusted" or "unlessTrusted" ? String(settings?["approvalPolicy"])
                : settings?["approvalPolicy"] is JsonObject granular && granular["granular"] is JsonObject ? "granular" : "unknown",
            ["writableRootCount"] = policy is JsonObject && policy["writableRoots"] is JsonArray roots ? roots.Count : null,
            ["readScope"] = profileId is not null && !profileId.StartsWith(':') ? "profile-controlled"
                : policy is JsonObject && (policy["access"] ?? policy["readOnlyAccess"])?["type"]?.ToString() == "restricted" ? "restricted" : "provider-defined",
            ["profile"] = profileId is ":read-only" or ":workspace" or ":danger-full-access" ? profileId : profileId is not null ? "custom" : null,
            ["source"] = source == "accepted-turn" ? "accepted-turn" : "server",
        };
        return e;
    }

    private static JsonObject Envelope(string rootId, string threadId, string kind) => new()
    {
        ["type"] = "system", ["subtype"] = "mitre_telemetry", ["rootId"] = rootId,
        ["threadId"] = threadId, ["kind"] = kind, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["subagent_thread_id"] = rootId == threadId ? null : threadId,
    };

    private static string? Operation(string? type) => type switch
    {
        "commandExecution" => "Shell command", "fileChange" => "Editing files",
        "mcpToolCall" => "Using MCP tool", "dynamicToolCall" => "Using a tool", "webSearch" => "Searching the web",
        "reasoning" => "Reasoning", "agentMessage" => "Writing response", "plan" => "Updating plan",
        "collabAgentToolCall" => "Agent coordination", "imageGeneration" => "Image generation",
        "contextCompaction" => "Compacting context", _ => null,
    };
    private static string? DescribeOperation(JsonNode? item)
    {
        var type = String(item?["type"]);
        if (type == "mcpToolCall")
        {
            var tool = TruncateLabel(String(item?["tool"]));
            return string.IsNullOrWhiteSpace(tool) ? "Using MCP tool" : "Using · " + tool;
        }
        if (type == "dynamicToolCall")
        {
            var tool = TruncateLabel(String(item?["tool"]));
            return string.IsNullOrWhiteSpace(tool) ? "Using a tool" : "Using · " + tool;
        }
        if (type == "webSearch") return "Searching the web";
        if (type == "fileChange") return "Editing files";
        var description = Operation(type);
        if (type != "commandExecution" || item?["commandActions"] is not JsonArray actions) return description;
        var categories = actions.Select(action => String(action?["type"]) switch
        {
            "read" => "read files", "listFiles" => "list files", "search" => "search files", _ => null,
        }).Where(label => label is not null).Distinct().ToArray();
        return categories.Length > 0 ? description + " · " + string.Join(", ", categories) : description;
    }
    private static string? TruncateLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= 80 ? text : text[..80];
    }
    private static string Status(string? value) => value switch
    {
        "completed" or "inProgress" or "failed" or "declined" or "interrupted" or "cancelled" => value,
        _ => "unknown",
    };
    internal static string? String(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    internal static long? TokenCount(JsonNode? value)
    {
        if (value is not JsonValue scalar) return null;
        if (scalar.TryGetValue<long>(out var count) && count >= 0) return count;
        if (scalar.TryGetValue<int>(out var smallCount) && smallCount >= 0) return smallCount;
        return null;
    }
}

internal sealed class MitreRuntimeState
{
    internal Dictionary<string, string> Active { get; } = new(StringComparer.Ordinal);
    public string Activity { get; internal set; } = "No live runtime events yet";
    public string LastOperation { get; internal set; } = "";
    public string? TurnId { get; internal set; }
    public bool Working { get; internal set; }
    public JsonObject? Access { get; internal set; }
    public string? TacticId { get; internal set; }
    public bool TacticReported { get; internal set; }
    public AgentStatusReport? AgentReport { get; internal set; }
    public long? TotalTokens { get; internal set; }
    public DateTimeOffset? UpdatedAt { get; internal set; }
    public string AccessSummary
    {
        get
        {
            if (Access is null) return "Access not reported by runtime";
            var sandbox = Access["sandbox"]?.ToString() switch
            {
                "readOnly" => "Read-only", "workspaceWrite" => "Workspace write",
                "dangerFullAccess" => "No sandbox", "externalSandbox" => "External sandbox", _ => "Sandbox unknown",
            };
            var approval = Access["approval"]?.ToString() switch
            {
                "never" => "No approval prompts", "on-request" => "Approval on request",
                "on-failure" => "Approval on failure", "untrusted" or "unlessTrusted" => "Untrusted actions need approval",
                "granular" => "Category-specific approvals",
                _ => "Approvals unknown",
            };
            return $"{sandbox} · Network {Access["network"]} · {approval}";
        }
    }
    public string CurrentToolLine
    {
        get
        {
            if (AgentReport is { } ai && !string.IsNullOrWhiteSpace(ai.Tool))
                return string.IsNullOrWhiteSpace(ai.ToolAction)
                    ? "Using · " + ai.Tool
                    : "Using · " + ai.Tool + " — " + ai.ToolAction;
            if (Working && Active.Count > 0)
                return Active.Last().Value + (Active.Count > 1 ? $" · {Active.Count} active operations" : "");
            return "";
        }
    }
    public string AccessDetail => (Access is null ? "No policy was reported for this agent."
        : (Access["source"]?.ToString() == "server" ? "Server-reported session policy." : "Accepted turn policy; not independently probed.")
          + $" Read scope: {Access["readScope"]}. Explicit writable roots: {Access["writableRootCount"]?.ToString() ?? "not reported"}.")
        + " This policy covers local sandboxed commands; web search, MCP, connectors and approved tool grants have separate controls."
        + " Host elevation and remote access are not observed; a tactic is not proof of access.";
}

// Owned by a ChatViewModel and consumed on its dispatcher. Record every event, including operations
// that start AND finish between display refreshes. Each instance owns a separate append-only log.
internal sealed class MitreSessionTelemetry
{
    private readonly Dictionary<string, MitreRuntimeState> _threads = new(StringComparer.Ordinal);
    private readonly string _logId = Guid.NewGuid().ToString("N");
    private string? _rootId;
    private string? _logPath;
    public string? LogPath => _logPath;
    public string? LogError { get; private set; }
    public int LoggedEvents { get; private set; }
    public MitreRuntimeState? Get(string? childId = null) =>
        _threads.GetValueOrDefault(childId ?? _rootId ?? "");

    public void Observe(JsonNode e, bool log)
    {
        var rootId = MitreRuntimeTelemetry.String(e["rootId"]);
        var threadId = MitreRuntimeTelemetry.String(e["threadId"]);
        if (rootId is null || threadId is null) return;
        if (_rootId != rootId) { _threads.Clear(); _rootId = rootId; }
        if (!_threads.TryGetValue(threadId, out var state)) _threads[threadId] = state = new();
        var kind = e["kind"]?.ToString();
        var itemId = e["itemId"]?.ToString() ?? "unknown";
        var operation = e["operation"]?.ToString() ?? "Runtime operation";
        var status = e["status"]?.ToString() ?? "unknown";
        var eventTurnId = e["turnId"]?.ToString();
        if (kind is not ("turn_started" or "access") && eventTurnId is not null
            && state.TurnId is not null && eventTurnId != state.TurnId)
        {
            if (log) Append(e, state, stale: true);
            return; // late events are history, not the current operation or tactic
        }
        if (DateTimeOffset.TryParse(e["timestamp"]?.ToString(), out var timestamp)) state.UpdatedAt = timestamp;
        switch (kind)
        {
            case "access": state.Access = e["access"]?.DeepClone() as JsonObject; break;
            case "token_usage": state.TotalTokens = MitreRuntimeTelemetry.TokenCount(e["totalTokens"]) ?? state.TotalTokens; break;
            case "turn_started":
                state.Active.Clear(); state.TacticId = null; state.TacticReported = false; state.AgentReport = null; state.LastOperation = "";
                state.TurnId = e["turnId"]?.ToString(); state.Working = true;
                state.Activity = "Turn started · awaiting runtime activity";
                break;
            case "turn_completed":
                state.Active.Clear(); state.Working = false;
                state.Activity = status switch { "failed" => "Turn failed", "interrupted" or "cancelled" => "Turn interrupted", _ => "Turn complete" };
                break;
            case "operation_started":
                state.Active[itemId] = operation;
                UpdateActivity();
                break;
            case "operation_completed":
                state.Active.Remove(itemId);
                state.LastOperation = $"{operation} · {status}";
                if (AgentStatusReport.FromReceipt(e["agentReport"]) is { } acceptedReport)
                {
                    state.AgentReport = acceptedReport;
                    state.TacticId = acceptedReport.Stage == "unknown" ? null : acceptedReport.Stage;
                    state.TacticReported = true;
                }
                else if (state.AgentReport is null && e["tacticReported"]?.GetValue<bool>() == true)
                {
                    state.TacticId = e["tacticId"]?.ToString(); state.TacticReported = true;
                }
                UpdateActivity();
                break;
            case "approval_requested": state.Activity = "Approval requested · grant not yet observed"; break;
            case "approval_resolved": UpdateActivity(); break;
            case "runtime_error": state.Activity = "Runtime reported an error"; break;
        }
        if (log) Append(e, state);

        void UpdateActivity() => state.Activity = state.Active.Count > 0
            ? state.Active.Last().Value + (state.Active.Count > 1 ? $" · {state.Active.Count} active operations" : "")
            : state.Working ? "Awaiting next runtime event" : "Idle";
    }

    private void Append(JsonNode e, MitreRuntimeState state, bool stale = false)
    {
        try
        {
            _logPath ??= Path.Combine(AppSettings.Dir, "mitre-monitor", "events-" + _logId + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            // A full log remains recoverable; stop and surface the limit instead of silently deleting history.
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length >= 20 * 1024 * 1024)
                throw new IOException("The session log reached its 20 MB limit.");
            var entry = new JsonObject
            {
                ["timestamp"] = e["timestamp"]?.DeepClone(), ["threadId"] = e["threadId"]?.DeepClone(),
                ["parentThreadId"] = e["threadId"]?.ToString() == _rootId ? null : _rootId,
                ["turnId"] = e["turnId"]?.ToString() ?? state.TurnId, ["event"] = e["kind"]?.DeepClone(),
                ["itemId"] = e["itemId"]?.DeepClone(), ["operation"] = e["operation"]?.DeepClone(),
                ["status"] = e["status"]?.DeepClone(), ["activity"] = stale ? "Late event from a previous turn" : state.Activity,
                ["stale"] = stale, ["reportedTacticId"] = stale ? null : state.TacticId,
                ["tacticReportState"] = stale ? "notCurrent" : !state.TacticReported ? "notReported" : state.TacticId is null ? "unmapped" : "mapped",
                ["access"] = stale ? null : state.Access?.DeepClone(),
                ["agentReport"] = stale ? null : state.AgentReport?.ToJson(),
                ["totalTokens"] = stale ? null : state.TotalTokens,
            };
            File.AppendAllText(_logPath, entry.ToJsonString() + Environment.NewLine);
            LoggedEvents++;
            LogError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogError = "Event log unavailable or full; some events were not saved.";
        }
    }
}
