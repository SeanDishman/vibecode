namespace VibeCode.Services;

/// <summary>Every provider keeps the same visible checklist, through VibeCode's built-in todo_write MCP tool or a native
/// plan tool that already feeds it, so the user can follow a long task as checkboxes in any chat. The rule names long
/// work only: a one-item list for "what does this function do?" is noise, not progress.</summary>
public static class TodoPolicy
{
    public const string Header = "[VIBECODE TODOS]";
    public const string Footer = "[END VIBECODE TODOS]";

    /// <summary>The note riding each user turn. It must stay one paragraph: resumed transcripts drop app notes block by
    /// block at blank lines, and a blank line inside would leave the rest of the rule glued to the user's bubble.</summary>
    public static string TurnInstructions(string provider) => Header + "\n" +
        "The user follows your progress as a checklist of checkboxes in VibeCode. " +
        "Required for long tasks: when a request needs several steps or more than a few tool calls (a feature, a fix across files, an investigation, a build-and-verify loop), " +
        "call todo_write before starting with every concrete step, then again each time a step finishes or the plan changes. " +
        "Keep exactly one item in_progress, mark each item completed as soon as it is actually done, and send the whole list on every call because it replaces the previous one. " +
        "Before your final answer, every item should be completed or removed. " +
        "Skip it for questions, explanations, short answers and small single-step changes. " +
        BridgeMcpConnection.TodoCallInstructions(provider) + "\n" + Footer;

    /// <summary>The note for an agent in an agent team instead. A shared terminal with a checklist per agent is clutter,
    /// not progress, so team agents keep none; todo_write declines for them as well.</summary>
    public const string TeamTurnInstructions = Header + "\n" +
        "You are working in an agent team, so do not keep a todo checklist. Skip todo_write and any built-in todo, task-list " +
        "or plan tool (TodoWrite, TaskCreate, update_plan). The user and your teammates follow your messages in the team's " +
        "shared terminal; report progress there in plain words.\n" + Footer;
}
