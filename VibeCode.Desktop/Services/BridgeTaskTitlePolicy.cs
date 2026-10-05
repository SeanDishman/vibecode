namespace VibeCode.Services;

/// <summary>Regular bridges: each agent keeps a short title of its overall task beside its name in the pane header
/// ("Claude 6 · Fix login redirect"). Unlike the once-per-chat AI chat name it follows the work, but only at the level
/// of whole tasks: a title per sub-step would fill every transcript with "Set task title" cards.</summary>
public static class BridgeTaskTitlePolicy
{
    public const string Header = "[BRIDGE TASK TITLE]";
    /// <summary>The roster's default task name. It means "no title yet" and is never shown in a pane header.</summary>
    public const string Placeholder = "Ready";
    /// <summary>Room for a request that lists up to three different tasks, one title each. Guidance, not this cap, is
    /// what keeps titles at task level; the cap only stops a model that narrates sub-steps anyway.</summary>
    public const int MaxChangesPerTurn = 3;

    // Spelled out as a concrete sequence: weaker models otherwise cover a two-task request with one umbrella title.
    private const string SeveralTasks = "Several distinct tasks in one request get one title each, in order, instead of one umbrella " +
        "title: for \"fix the login bug, then add dark mode\", set \"Fix login bug\" before the first and \"Add dark mode\" when you " +
        "start the second. ";

    /// <summary>Returned with every applied title, right where the model decides how to proceed. Scoped to the current
    /// request: an unscoped "call it again for the next task" made Luna re-send its title on plain follow-ups.</summary>
    public const string AppliedGuidance = "Shown beside your name. Keep it while you work on this task. Only if this same request " +
        "has another distinct task, set that task's own title when you start it. Do not call it again for follow-ups, sub-steps or completion.";

    public static string Normalize(string title) =>
        string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool IsPlaceholder(string title) => string.Equals(title, Placeholder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The note riding each user turn of a regular-bridge pane. Without a title, naming the task is the
    /// required first action. With one, the agent picks one of three explicit cases. Live GPT-6 Luna runs showed that
    /// a strong "do not call" stops weak models from ever retitling, leaving a finished task in the header; a mild
    /// keep rule is enough, because an occasional unchanged-title call is a silent no-op (the card is never shown).</summary>
    public static string TurnReminder(string currentTitle, string provider) => currentTitle.Length == 0
        ? Header + "\nRequired in this regular bridge: your pane has no task title yet. Before starting the request below, " +
          "call bridge_set_task_title with three or four words naming its overall task (for example \"Fix login redirect\"); " +
          "it is shown beside your name in the Bridge header. " + SeveralTasks +
          "Never retitle for sub-steps, progress or completion. " + BridgeMcpConnection.TaskTitleCallInstructions(provider)
        : Header + $"\nYour pane title is \"{currentTitle}\". Before starting the request below, pick the case that fits it:\n" +
          $"- Same task: a follow-up, fix, check or question about \"{currentTitle}\". Keep the title; no call is needed.\n" +
          "- Different task: a different feature, bug, file or question. Call bridge_set_task_title with its own three-or-four-word title before starting it.\n" +
          "- Several distinct tasks: one title each, in order, never one umbrella title. For \"fix the login bug, then add dark mode\", " +
          "set \"Fix login bug\" before the first and \"Add dark mode\" when you start the second.\n" +
          BridgeMcpConnection.TaskTitleCallInstructions(provider);

    /// <summary>Steered into the running turn of a chat that becomes a regular-bridge agent mid-task, which would
    /// otherwise wait for its next request to be asked. It names the pane title alone; the chat keeps its own name.</summary>
    public static string JoinReminder(string provider) =>
        Header + "\nThis chat just joined a regular bridge, and your pane has no task title yet. Call bridge_set_task_title " +
        "now with three or four words naming the task you are working on (for example \"Fix login redirect\"); it is shown " +
        "beside your name in the Bridge header. It does not rename the chat, so do not call chat_set_title for it. Then " +
        "continue what you were doing; if that work was already finished, just stop. " +
        BridgeMcpConnection.TaskTitleCallInstructions(provider);
}
