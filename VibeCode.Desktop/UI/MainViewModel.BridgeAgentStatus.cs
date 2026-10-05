using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// bridge_agent_status: a direct answer to "is that agent still working, or is it done?". Agents leave a peer's area
/// alone while it works. Without this they had to infer it from the full roster (whose summary is whatever the peer
/// last reported) or simply guess, and some kept waiting on peers that had finished long ago. Everything here is read
/// from the app's live session state, so it is accurate even when the peer never reported its progress.
/// </summary>
public sealed partial class MainViewModel
{
    private static JsonObject BridgeAgentStatus(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        var now = DateTimeOffset.UtcNow;
        var edits = BridgeEditsFor(bridge);
        if (args["agent"]?.GetValue<string>() is { } requested)
        {
            string id;
            try { id = EditAgentId(caller, bridge, requested); }
            catch (StatusValidationException)
            {
                throw new StatusValidationException($"agent '{requested.Trim()}' is not in this bridge. Use a message_recipient or agent_id " +
                    "from bridge_list_agents, an agent number such as 2, or a label such as Claude 2. Omit agent to check every other agent.");
            }
            return bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == id) is { } pane
                ? AgentStatusJson(caller, pane, bridge, edits, now)
                : DepartedAgentStatusJson(id, edits, now);
        }

        var peers = bridge.Panes.Where(p => !ReferenceEquals(p, caller)).ToArray();
        var busy = peers.Count(p => IsBusyState(AgentRunState(p)));
        return new JsonObject
        {
            ["agents"] = new JsonArray(peers.Select(p => (JsonNode?)AgentStatusJson(caller, p, bridge, edits, now)).ToArray()),
            ["busy_count"] = busy,
            ["summary"] = peers.Length == 0 ? "You are the only agent in this bridge."
                : busy == 0 ? "No other agent is working or about to resume work."
                : $"{busy} of {peers.Length} other agent(s) are working or will resume on their own.",
        };
    }

    /// <summary>One word for what the session is doing right now, from live app state rather than self-reports.</summary>
    private static string AgentRunState(ChatViewModel p) =>
        p.Status == "closed" ? "closed"
        : p.WaitingForLimitReset ? "waiting_for_limit_reset"
        : p.Status == "error" ? "error"
        : p.Status is "running" or "preparing" ? p.PendingPermissionCount > 0 ? "awaiting_approval" : "working"
        : p.QueuedPromptCount > 0 ? "queued"
        : p.Status == "starting" ? "starting"
        : p.BridgeTaskState == "waiting" ? "waiting"
        : p.BridgeTaskState == "completed" ? "finished"
        : "idle";

    /// <summary>Busy = working now, or will carry on by itself (queued prompts, workers to hear from, a limit reset).</summary>
    private static bool IsBusyState(string state) =>
        state is "working" or "awaiting_approval" or "queued" or "waiting" or "waiting_for_limit_reset";

    private static JsonObject AgentStatusJson(ChatViewModel caller, ChatViewModel p, LiveBridge bridge,
        IReadOnlyList<BridgeEditEntry> edits, DateTimeOffset now)
    {
        var state = AgentRunState(p);
        var task = p.BridgeHeaderTaskTitle is { Length: > 0 } title ? title : null;
        var lastEdit = edits.FirstOrDefault(e => e.AgentId == p.BridgeAgentId);
        var workingFor = p.IsWorking && p.WorkStartedAtUtc is { } started ? Elapsed(now.UtcDateTime - started) : null;
        var idleFor = !p.IsWorking && p.WorkEndedAtUtc is { } ended ? Elapsed(now.UtcDateTime - ended) : null;
        return new JsonObject
        {
            ["agent_id"] = p.BridgeAgentId, ["number"] = BridgeNumberOf(p), ["label"] = p.BridgeLabel,
            ["message_recipient"] = BridgeMessageRecipient(p, bridge.Panes),
            ["you"] = ReferenceEquals(p, caller),
            ["state"] = state,
            ["working"] = state is "working" or "awaiting_approval",
            ["busy"] = IsBusyState(state),
            ["task_name"] = task,
            ["task_state"] = p.BridgeTaskState,
            ["working_for"] = workingFor,
            ["idle_for"] = idleFor,
            ["queued_prompts"] = p.QueuedPromptCount,
            ["last_file_edit"] = lastEdit is null ? null : $"{lastEdit.File}: {lastEdit.Lines} ({Age(now - lastEdit.At)})",
            ["recent_edits"] = RecentEditsJson(p.BridgeAgentId, edits, now),
            ["reported_activity"] = p.BridgeActivitySummary,
            ["verdict"] = AgentVerdict(p, state, task, workingFor, idleFor),
        };
    }

    private static string AgentVerdict(ChatViewModel p, string state, string? task, string? workingFor, string? idleFor)
    {
        var name = p.BridgeTerminalIdentity;
        var on = task is null ? "" : $" on \"{task}\"";
        return state switch
        {
            "working" => $"{name} is working right now{on}" + (workingFor is null ? "" : $" ({workingFor} into this turn)") +
                ". Leave its area alone or message it, and check again later.",
            "awaiting_approval" => $"{name} is mid-turn{on} and waiting for the user to approve a tool call. Treat it as busy.",
            "queued" => $"{name} is between turns with {p.QueuedPromptCount} queued prompt(s) it will start next. Treat it as busy.",
            "waiting" => $"{name} is between turns, waiting on its workers or replies{on}, and will continue on its own. Treat its area as taken.",
            "waiting_for_limit_reset" => $"{name} is paused by its provider's usage limit and will resume{on} automatically. Treat its area as taken.",
            "starting" => $"{name}'s session is still starting; it has not begun any work.",
            "finished" => $"{name} finished{on}" + (idleFor is null ? "" : $" {idleFor} ago") +
                " and is not working now. Its area is free unless it is given new work.",
            "error" => $"{name} stopped with an error and is not working" + (task is null ? "." : $"; \"{task}\" may be unfinished."),
            "closed" => $"{name}'s session is closed; it is not working.",
            _ => $"{name} is idle and not working" + (idleFor is null ? "" : $" (idle {idleFor})") +
                (task is null || p.BridgeTaskState is "ready" or "working" ? "." : $"; its last task \"{task}\" ended as {p.BridgeTaskState}."),
        };
    }

    private static JsonObject DepartedAgentStatusJson(string agentId, IReadOnlyList<BridgeEditEntry> edits, DateTimeOffset now)
    {
        var last = edits.FirstOrDefault(e => e.AgentId == agentId);
        return new JsonObject
        {
            ["agent_id"] = agentId, ["state"] = "left", ["working"] = false, ["busy"] = false,
            ["last_file_edit"] = last is null ? null : $"{last.File}: {last.Lines} ({Age(now - last.At)})",
            ["verdict"] = (last is null ? "That agent" : last.Label) + " has left this bridge and is not working. Its recorded edits remain in bridge_file_edits.",
        };
    }

    private static string Elapsed(TimeSpan span) => AgentSupervisionPolicy.Duration((int)Math.Max(0, span.TotalSeconds));
}
