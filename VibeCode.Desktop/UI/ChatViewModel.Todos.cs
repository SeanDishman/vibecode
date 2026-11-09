using System.Text.Json.Nodes;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    /// <summary>False for an agent in an agent team. One shared terminal with a checklist per agent is clutter, so team
    /// agents are told to keep none, todo_write declines, and native todo, task and plan updates leave the list empty.</summary>
    internal bool KeepsTodoList => !IsAgentTeamMember;

    /// <summary>Drop the checklist and its Task* bookkeeping when the agent joins a team, so no stale list stays up.</summary>
    private void ClearTodoList()
    {
        if (Todos.Count == 0 && _tasksById.Count == 0) return;
        Todos.Clear();
        _tasksById.Clear();
        _taskIdByTool.Clear();
        _taskText.Clear();
        _adoptFailed.Clear();
        RaiseTodoProps();
    }

    /// <summary>todo_write from VibeCode's built-in MCP server: the agent's whole checklist, already validated against the
    /// tool schema. It lands in the same Todos list as Claude's TodoWrite and Codex's plan, so the header badge, task
    /// popup, Bridge pane menu, phone mirror and task checkpoint all follow it with no provider-specific code.</summary>
    internal JsonObject SetTodosFromMcp(JsonObject args)
    {
        if (!KeepsTodoList)
            return new JsonObject
            {
                ["saved"] = false,
                ["reason"] = "Agents in a team keep no todo list. Skip todo_write and report progress in your messages instead.",
            };
        ReplaceTodos(args["todos"]!.AsArray());
        var inProgress = Todos.Count(t => t.Status == "in_progress");
        return new JsonObject
        {
            ["saved"] = true, ["total"] = TodoTotal, ["completed"] = TodoDone, ["in_progress"] = inProgress,
            ["pending"] = TodoTotal - TodoDone - inProgress,
            ["note"] = TodoTotal == 0 ? "Checklist cleared."
                : TodoDone == TodoTotal ? "Every item is completed; the user sees the finished checklist."
                : "Shown to the user as checkboxes. Call todo_write again with the whole list as soon as an item finishes or the plan changes.",
        };
    }
}
