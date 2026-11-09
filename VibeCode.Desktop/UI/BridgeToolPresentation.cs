using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;

namespace VibeCode.UI;

internal static class BridgeToolPresentation
{
    private static string ToolName(string name) => BridgeMcpConnection.CatalogToolName(name);

    // arguments inside the envelope. Only our own server's tools are unwrapped. Gemini MCP calls wrap the same
    // fields in arguments and often repeat the tool title beside them.
    private static (string Name, JsonNode? Input) Unwrap(string name, JsonNode? input)
    {
        if (name == "use_tool" && Text(input, "tool_name") is { } inner
            && inner.StartsWith(BridgeMcpConnection.RuntimeName + "__", StringComparison.Ordinal))
            (name, input) = (inner, (input as JsonObject)?["tool_input"]);
        return (name, BridgeMcpConnection.ToolArguments(input));
    }

    public static string? DisplayName(string name) => Label(ToolName(name));

    public static string? CardDisplayName(string name, JsonNode? input) => Label(ToolName(Unwrap(name, input).Name));

    private static string? Label(string tool) => tool switch
    {
        "chat_set_title" => "Name chat",
        "chat_checkpoint" => "Save task checkpoint",
        BridgeMcpTools.TodoToolName => "Update todos",
        "bridge_list_agents" => "View bridge agents",
        "bridge_dispatch_task" => "Assign worker task",
        "bridge_set_plan" => "Publish plan steps",
        "bridge_list_tasks" => "View plan progress",
        "bridge_update_task" => "Record task progress",
        "bridge_retry_task" => "Recover assignment",
        "bridge_stop_worker" => "Stop managed worker",
        "bridge_resume_worker" => "Resume managed worker",
        "bridge_agree_scope" => "Agree group scope",
        "bridge_review_scope" => "Review final product",
        BridgeMcpTools.MarkTaskDoneToolName => "Answer fact check",
        "bridge_send_message" => "Message teammate",
        "bridge_broadcast" => "Broadcast to bridge",
        "bridge_read_messages" => "Read bridge messages",
        "bridge_read_chat" => "Read agent chat",
        BridgeMcpTools.ProjectReadChatToolName => "View project chats",
        "bridge_mark_message" => "Mark message handled",
        "bridge_report_activity" => "Update agent activity",
        "bridge_set_task_title" => "Set task title",
        BridgeMcpTools.AgentNameToolName => "Pick a name",
        "bridge_file_edits" => "Check file edit history",
        "bridge_agent_status" => "Check agent status",
        _ => null,
    };

    public static string? Summary(string name, JsonNode? input)
    {
        (name, input) = Unwrap(name, input);
        return ToolName(name) switch
        {
            "chat_set_title" => Text(input, "title") ?? "Summarize this chat",
            "chat_checkpoint" => Text(input, "objective") ?? "Preserve this task's progress",
            BridgeMcpTools.TodoToolName => TodoSummary(input),
            "bridge_list_agents" => "Current agents and assignments",
            "bridge_dispatch_task" => Text(input, "task_name") ?? "Work queued for a teammate",
            "bridge_set_plan" => "Publish this group's versioned plan",
            "bridge_list_tasks" => "Steps, dependencies and actual progress",
            "bridge_update_task" => Text(input, "summary") ?? "Task outcome and optional evidence",
            "bridge_retry_task" => "Retry after checking existing work",
            "bridge_stop_worker" => Text(input, "reason") ?? "Stop work and keep the chat",
            "bridge_resume_worker" => "Release the worker?s queued work",
            "bridge_agree_scope" => Text(input, "scope") ?? "Confirm this group's agreed scope",
            "bridge_review_scope" => Text(input, "summary") ?? "Record this group's final review",
            BridgeMcpTools.MarkTaskDoneToolName => Text(input, "summary") ?? "Confirm the user's request is finished",
            "bridge_send_message" => Text(input, "message") ?? "Message queued for a teammate",
            "bridge_broadcast" => Text(input, "message") ?? "Important update queued for bridge agents",
            "bridge_read_messages" => "Latest peer messages",
            "bridge_read_chat" => Text(input, "agent") is { } agent
                ? Text(input, "view") is { } view ? $"{view.Replace('_', ' ')} of {agent}'s chat" : $"Conversation with {agent}"
                : "Full agent conversation",
            BridgeMcpTools.ProjectReadChatToolName => Text(input, "agent") is { } chat
                ? Text(input, "view") is { } chatView ? $"{chatView.Replace('_', ' ')} of {chat}'s chat" : $"Conversation with {chat}"
                : "Chats open in this directory",
            "bridge_mark_message" => "Incoming message handled",
            "bridge_report_activity" => Text(input, "summary") ?? Text(input, "task_name") ?? "Current task and progress",
            "bridge_set_task_title" => Text(input, "title") ?? "Title shown beside this agent's name",
            BridgeMcpTools.AgentNameToolName => Text(input, "name") ?? "The name this agent goes by",
            "bridge_file_edits" => Text(input, "path") ?? "Which agents changed which files and lines",
            "bridge_agent_status" => Text(input, "agent") ?? "Whether other agents are still working",
            _ => null,
        };
    }

    /// <summary>"2 of 5 done · Running the tests": progress, then the step in hand, so a collapsed card reads as a
    /// progress line and the trail of cards shows how the task moved.</summary>
    private static string TodoSummary(JsonNode? input)
    {
        if ((input as JsonObject)?["todos"] is not JsonArray todos) return "Update the task checklist";
        var rows = todos.OfType<JsonObject>().ToList();
        if (rows.Count == 0) return "Cleared the checklist";
        var done = rows.Count(row => Text(row, "status") == "completed");
        var current = rows.FirstOrDefault(row => Text(row, "status") == "in_progress");
        var step = current is null ? null : Text(current, "activeForm") is { Length: > 0 } active && !string.IsNullOrWhiteSpace(active)
            ? active : Text(current, "content");
        return $"{done} of {rows.Count} done" + (string.IsNullOrWhiteSpace(step) ? "" : " · " + step.Trim());
    }

    private static string? Text(JsonNode? input, string field) =>
        input is JsonObject obj && obj[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
