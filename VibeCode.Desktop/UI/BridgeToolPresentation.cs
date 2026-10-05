using System.Text.Json.Nodes;

namespace VibeCode.UI;

internal static class BridgeToolPresentation
{
    private static string ToolName(string name) => name[(name.LastIndexOf("__", StringComparison.Ordinal) is var index && index >= 0 ? index + 2 : 0)..];

    public static string? DisplayName(string name) => ToolName(name) switch
    {
        "chat_set_title" => "Name chat",
        "bridge_list_agents" => "View bridge agents",
        "bridge_dispatch_task" => "Assign worker task",
        "bridge_set_plan" => "Publish plan steps",
        "bridge_list_tasks" => "View plan progress",
        "bridge_update_task" => "Record task progress",
        "bridge_retry_task" => "Recover assignment",
        "bridge_agree_scope" => "Agree group scope",
        "bridge_review_scope" => "Review final product",
        "bridge_send_message" => "Message teammate",
        "bridge_broadcast" => "Broadcast to bridge",
        "bridge_read_messages" => "Read bridge messages",
        "bridge_mark_message" => "Mark message handled",
        "bridge_report_activity" => "Update agent activity",
        "bridge_set_task_title" => "Set task title",
        "bridge_file_edits" => "Check file edit history",
        "bridge_agent_status" => "Check agent status",
        _ => null,
    };

    public static string? Summary(string name, JsonNode? input) => ToolName(name) switch
    {
        "chat_set_title" => Text(input, "title") ?? "Summarize this chat",
        "bridge_list_agents" => "Current agents and assignments",
        "bridge_dispatch_task" => Text(input, "task_name") ?? "Work queued for a teammate",
        "bridge_set_plan" => "Publish this group's versioned plan",
        "bridge_list_tasks" => "Steps, dependencies and actual progress",
        "bridge_update_task" => Text(input, "summary") ?? "Task outcome and optional evidence",
        "bridge_retry_task" => "Retry after checking existing work",
        "bridge_agree_scope" => Text(input, "scope") ?? "Confirm this group's agreed scope",
        "bridge_review_scope" => Text(input, "summary") ?? "Record this group's final review",
        "bridge_send_message" => Text(input, "message") ?? "Message queued for a teammate",
        "bridge_broadcast" => Text(input, "message") ?? "Important update queued for bridge agents",
        "bridge_read_messages" => "Latest peer messages",
        "bridge_mark_message" => "Incoming message handled",
        "bridge_report_activity" => Text(input, "summary") ?? Text(input, "task_name") ?? "Current task and progress",
        "bridge_set_task_title" => Text(input, "title") ?? "Title shown beside this agent's name",
        "bridge_file_edits" => Text(input, "path") ?? "Which agents changed which files and lines",
        "bridge_agent_status" => Text(input, "agent") ?? "Whether other agents are still working",
        _ => null,
    };

    private static string? Text(JsonNode? input, string field) =>
        input is JsonObject obj && obj[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
