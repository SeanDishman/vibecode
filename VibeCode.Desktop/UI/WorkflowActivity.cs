using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One line of an agent card: a Segoe Fluent glyph, the sentence beside it, and whether that step failed.</summary>
public sealed record AgentActivityStep(string Glyph, string Text, bool Failed = false)
{
    /// <summary>The tool call behind the step as the transcript names it ("Bash", "Edit", "mcp__github__create_issue");
    /// empty when the step is not a tool call (thinking, writing a reply, waiting for approval).</summary>
    public string Tool { get; init; } = "";
}

/// <summary>
/// What an agent is doing right now and what it finished last, in the words a person would use across the room:
/// "Editing WorkflowGraph.cs", "Ran dotnet build". A task title says what an agent was asked for; this says where
/// it actually is.
///
/// Read from the newest rows of the chat's in-memory transcript - the same tool cards the chat itself is showing -
/// and nothing else. It never reads message or reasoning text, stored history, or a provider. The scan walks back
/// from the newest row and stops at the first finished step it needs, so its cost is a handful of rows per agent
/// per refresh however long the conversation is; <see cref="ScanLimit"/> bounds the worst case (a turn of
/// hundreds of parallel reads) without ever reading the whole transcript.
///
/// Bookkeeping calls - naming the chat, listing peers, ticking a todo - are skipped: they are true but say nothing
/// about the work, and on a busy Bridge they would otherwise be most of what the wall showed.
/// </summary>
internal static class WorkflowActivity
{
    /// <summary>Rows inspected per chat per refresh, at most.</summary>
    internal const int ScanLimit = 240;

    /// <summary>Working subagents described per agent, at most. Its count of them stays exact.</summary>
    internal const int MaxSubagents = 12;

    private const int MaxText = 72;

    public const string ThinkingGlyph = "\uE82F";   // lightbulb
    public const string WritingGlyph = "\uE8BD";    // message
    public const string ApprovalGlyph = "\uE72E";   // lock
    public const string WaitingGlyph = "\uE823";    // clock
    public const string ErrorGlyph = "\uEA39";      // error badge
    public const string StoppedGlyph = "\uE71A";    // stop
    public const string DelegateGlyph = "\uE716";   // people
    public const string PlanGlyph = "\uE9D5";       // checklist
    public const string WorkingGlyph = "\uE768";    // play
    public const string IdleGlyph = "\uE769";       // pause
    public const string DoneGlyph = "\uE73E";       // check mark
    public const string ReportGlyph = "\uE7C1";     // flag

    // Claude Code passes shell commands as written. Codex wraps each one in a full interpreter invocation, which
    // would fill a card with "C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe -Command" and no command.
    private static readonly Regex ShellWrapper = new(
        @"^\s*(?:""[^""]*?(?:powershell|pwsh|bash|cmd)(?:\.exe)?""|[^\s""]*?(?:powershell|pwsh|bash|cmd)(?:\.exe)?)\s+"
        + @"(?:-(?:NoProfile|NoLogo|NonInteractive)\s+|-ExecutionPolicy\s+\S+\s+)*(?:-Command|-c|-lc|/c)\s+(?<inner>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The live step (null when nothing is in flight) and the newest finished step (null before the first).</summary>
    public static (AgentActivityStep? Now, AgentActivityStep? Last) Read(ChatViewModel chat, bool working)
    {
        ToolItem? running = null, finished = null;
        PermItem? approval = null;
        bool writing = false, thinking = false, currentTurn = true;
        var items = chat.Items;
        for (int i = items.Count - 1, seen = 0; i >= 0 && seen < ScanLimit; i--, seen++)
        {
            switch (items[i])
            {
                case UserItem:
                    // Older rows belong to an earlier turn: they can still say what finished last, never what is live.
                    currentTurn = false;
                    break;
                case ToolItem tool:
                    Visit(tool);
                    break;
                case CompactToolGroupItem group:
                    for (var j = group.Tools.Count - 1; j >= 0; j--) Visit(group.Tools[j]);
                    break;
                case PermItem { IsPending: true } request when currentTurn:
                    approval ??= request;
                    break;
                case TextItem { Streaming: true } when currentTurn:
                    writing = true;
                    break;
                case ThinkingItem { Streaming: true } when currentTurn:
                    thinking = true;
                    break;
            }
            // Parallel calls finish out of order, so a live agent keeps looking through its own turn for a call that
            // is still running. An idle agent only ever needs the newest finished step.
            if (finished is not null && (!working || !currentTurn || running is not null)) break;
        }

        AgentActivityStep? now = null;
        if (working)
        {
            if (running is not null) now = new(running.Icon, Phrase(running, past: false)) { Tool = running.Name };
            else if (approval is not null) now = new(ApprovalGlyph, "Waiting for approval · " + ToolLabel(approval.ToolName));
            else if (chat.ActiveSubagentCount > 0)
                now = new(DelegateGlyph, chat.ActiveSubagentCount == 1 ? "1 subagent working" : $"{chat.ActiveSubagentCount} subagents working");
            else if (writing) now = new(WritingGlyph, "Writing a reply");
            else if (thinking || chat.ThinkingTokens > 0) now = new(ThinkingGlyph, "Thinking");
            else if (chat.Status == "starting") now = new(WorkingGlyph, "Starting the session");
            else if (chat.Status == "preparing") now = new(WorkingGlyph, "Preparing the prompt");
            else if (InProgressTodo(chat) is { } todo) now = new(PlanGlyph, todo);
            else now = new(WorkingGlyph, "Working");
        }
        else if (approval is not null)
        {
            now = new(ApprovalGlyph, "Waiting for approval · " + ToolLabel(approval.ToolName));
        }
        var last = finished is null ? null
            : new AgentActivityStep(finished.Icon, Phrase(finished, past: true), finished.IsError || finished.Status == "error") { Tool = finished.Name };
        return (now, last);

        void Visit(ToolItem tool)
        {
            // Todo tools can carry bookkeeping updates inside a tool envelope.
            if (IsBookkeeping(tool.Name) || tool.IsTodoTool) return;
            if (tool.IsRunning)
            {
                // A row left "running" by an earlier, interrupted turn is a stale card, not live work.
                if (currentTurn) running ??= tool;
            }
            else finished ??= tool;
        }
    }

    /// <summary>Each working subagent of <paramref name="chat"/> in its roster's order, with the step it is on.</summary>
    public static IReadOnlyList<AgentActivitySubagent> ReadSubagents(ChatViewModel chat)
    {
        List<AgentActivitySubagent>? working = null;
        foreach (var agent in chat.Subagents)
        {
            if (!agent.IsActive) continue;
            var (now, last) = ReadSubagent(agent);
            (working ??= new()).Add(new(agent.ThreadId, agent.Label, Clip(agent.Task), now, last));
            if (working.Count == MaxSubagents) break;
        }
        return working is null ? [] : working;
    }

    /// <summary>
    /// What one subagent is doing now and the newest step it finished. A Claude or Codex subagent streams its
    /// tool rows into its transcript - the rows its Agent card shows when opened - so those say it the way a chat's
    /// own rows do. A Workflow's agents stream nothing; for them it is the step their transcript on disk last showed.
    /// Failing both, the provider's own one-line activity ("Using Read", a progress summary).
    /// </summary>
    public static (AgentActivityStep Now, AgentActivityStep? Last) ReadSubagent(SubagentItem agent)
    {
        ToolItem? running = null, finished = null;
        bool writing = false, thinking = false;
        var items = agent.Transcript;
        for (int i = items.Count - 1, seen = 0; i >= 0 && seen < ScanLimit && (running is null || finished is null); i--, seen++)
        {
            switch (items[i])
            {
                case ToolItem tool:
                    Visit(tool);
                    break;
                case CompactToolGroupItem group:
                    for (var j = group.Tools.Count - 1; j >= 0; j--) Visit(group.Tools[j]);
                    break;
                case TextItem { Streaming: true }:
                    writing = true;
                    break;
                case ThinkingItem { Streaming: true }:
                    thinking = true;
                    break;
            }
        }

        // The provider's line goes stale as soon as the call it names returns; a streamed transcript is fresher.
        var streamed = running is not null || finished is not null;
        var reported = agent.Activity.Trim() is { Length: > 0 } activity && !IsPlaceholder(activity)
            && !(streamed && activity.StartsWith("Using ", StringComparison.Ordinal)) ? Clip(activity) : null;
        AgentActivityStep now;
        if (running is not null) now = new(running.Icon, Phrase(running, past: false)) { Tool = running.Name };
        else if (agent.LiveStep is { } live) now = live;
        // A run names an agent the moment it starts; until then it is queued behind the run's concurrency cap.
        else if (agent.FromWorkflow && agent.WorkflowAgentId is null) now = new(WaitingGlyph, "Waiting to start");
        else if (reported is not null) now = new(WorkingGlyph, reported);
        else if (writing) now = new(WritingGlyph, "Writing its report");
        else if (thinking || streamed) now = new(ThinkingGlyph, "Thinking");
        else now = new(WorkingGlyph, agent.Status == "pendingInit" ? "Starting" : "Working");
        var last = finished is null ? agent.LastStep
            : new AgentActivityStep(finished.Icon, Phrase(finished, past: true), finished.IsError || finished.Status == "error") { Tool = finished.Name };
        return (now, last);

        void Visit(ToolItem tool)
        {
            if (IsBookkeeping(tool.Name) || tool.IsTodoTool) return;
            if (tool.IsRunning) running ??= tool;
            else finished ??= tool;
        }
    }

    /// <summary>Lifecycle words a provider fills the activity line with when it has nothing more specific to say.</summary>
    private static bool IsPlaceholder(string activity) => activity is "Starting" or "Working" or "Working in background"
        or "Reporting progress" or "Finished" or "Failed" or "Agent failed" or "Stopped";

    /// <summary>The hover text for an agent's working subagents: one line each, "Explore 1 · Reading Program.cs",
    /// with what it was asked for under it.</summary>
    public static string DescribeSubagents(AgentActivityNode agent)
    {
        var lines = new List<string> { agent.ActiveChildren == 1 ? "1 subagent working" : $"{agent.ActiveChildren} subagents working" };
        foreach (var sub in agent.Subagents)
        {
            lines.Add(sub.Label + " · " + sub.Now.Text);
            if (sub.Task.Length > 0 && sub.Task != sub.Label) lines.Add("    " + sub.Task);
        }
        if (agent.ActiveChildren > agent.Subagents.Count) lines.Add($"+{agent.ActiveChildren - agent.Subagents.Count} more");
        return string.Join("\n", lines);
    }

    /// <summary>The task an agent's own todo list marks as in progress, if it keeps one.</summary>
    public static string? InProgressTodo(ChatViewModel chat)
    {
        foreach (var todo in chat.Todos)
            if (todo.Status == "in_progress" && !string.IsNullOrWhiteSpace(todo.Text)) return Clip(todo.Text);
        return null;
    }

    internal static bool IsBookkeeping(string name)
    {
        var tool = BridgeMcpConnection.CatalogToolName(name);
        return tool is "ToolSearch" or "TodoWrite" or "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet"
            or "chat_set_title" or "chat_checkpoint" or AgentStatus.Mcp.Bridge.BridgeMcpTools.TodoToolName or "bridge_set_task_title" or "bridge_report_activity" or "bridge_list_agents"
            or "bridge_agent_status" or "bridge_read_messages" or "bridge_mark_message" or "bridge_list_tasks"
            // The agent-status server's own reporting calls describe the work; they are not the work.
            or "report_status" or "list_stages";
    }

    /// <summary>"Editing WorkflowGraph.cs" while it runs, "Edited WorkflowGraph.cs +42 −7" once it has finished.</summary>
    internal static string Phrase(ToolItem tool, bool past)
    {
        var input = tool.Input;
        string Verb(string present, string done) => past ? done : present;
        switch (tool.Name)
        {
            case "Bash" or "PowerShell":
            {
                // Claude describes each command in a short sentence of its own; that reads better than the command.
                if (Str(input?["description"]) is { Length: > 0 } description) return Clip(description);
                var command = Command(Str(input?["command"]));
                return command.Length == 0 ? Verb("Running a command", "Ran a command") : Verb("Running ", "Ran ") + Clip(command, 56);
            }
            case "BashOutput": return Verb("Checking command output", "Checked command output");
            case "KillShell": return Verb("Stopping a command", "Stopped a command");
            case "Read": return Verb("Reading ", "Read ") + FileName(tool.FilePath);
            case "Write": return Verb("Writing ", "Wrote ") + FileName(tool.FilePath);
            case "NotebookEdit": return Verb("Editing ", "Edited ") + FileName(tool.FilePath);
            case "Edit" or "MultiEdit" or "CodexEdit":
            {
                var kind = tool.Name == "CodexEdit" ? Str(input?["kind"]) : null;
                var verb = kind == "add" ? Verb("Creating ", "Created ") : kind == "delete" ? Verb("Deleting ", "Deleted ") : Verb("Editing ", "Edited ");
                var stat = past && tool.HasDiffStat ? $"  +{tool.Added} \u2212{tool.Removed}" : "";
                return verb + FileName(tool.FilePath) + stat;
            }
            case "Grep": return Verb("Searching for ", "Searched for ") + Quoted(Str(input?["pattern"]));
            case "Glob": return Verb("Finding ", "Found ") + Clip(Str(input?["pattern"]) ?? "files", 48);
            case "LS": return Verb("Listing ", "Listed ") + FileName(Str(input?["path"]));
            case "WebSearch": return Verb("Searching the web for ", "Searched the web for ") + Quoted(Str(input?["query"]) ?? tool.HeaderSummary);
            case "WebFetch": return Verb("Fetching ", "Fetched ") + Host(Str(input?["url"]));
            case "Task" or "Agent" or "spawn_subagent":
                return Verb("Delegating ", "Delegated ") + Clip(Str(input?["description"]) ?? "a subtask", 56);
            case "Workflow": return Verb("Running workflow ", "Ran workflow ") + Clip(tool.HeaderSummary, 52);
            case "Skill" or "SlashCommand": return Verb("Using ", "Used ") + Clip(tool.HeaderSummary.Length > 0 ? tool.HeaderSummary : "a skill", 52);
            case "AskUserQuestion": return Verb("Asking you a question", "Asked you a question");
            case "ExitPlanMode": return Verb("Proposing a plan", "Proposed a plan");
        }
        var label = ToolLabel(tool.Name);
        var summary = tool.HeaderSummary;
        // Unknown tools fall back to the card's own header. Raw JSON is what a card shows when it has nothing
        // better; a wall glanced at from across the room needs the name alone instead.
        return summary.Length == 0 || summary.StartsWith('{') ? label : Clip(label + " · " + summary);
    }

    /// <summary>"Message teammate" for Bridge tools, "github · create_issue" for other MCP tools.</summary>
    internal static string ToolLabel(string name)
    {
        if (BridgeToolPresentation.DisplayName(name) is { } bridge) return bridge;
        if (!name.StartsWith("mcp__", StringComparison.Ordinal)) return name;
        var parts = name.Split("__", 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[1] + " · " + parts[2].Replace('_', ' ') : name;
    }

    /// <summary>The command a person typed, without the interpreter wrapper and on one line.</summary>
    internal static string Command(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var text = command.Trim();
        if (ShellWrapper.Match(text) is { Success: true } wrapped)
        {
            text = wrapped.Groups["inner"].Value.Trim();
            if (text.Length >= 2 && (text[0] is '"' or '\'') && text[^1] == text[0]) text = text[1..^1];
        }
        return CompactToolPresentation.ToSingleLine(text).Trim();
    }

    internal static string Clip(string? text, int max = MaxText)
    {
        var line = CompactToolPresentation.ToSingleLine(text ?? "").Trim();
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "\u2026";
    }

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "a file";
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return Clip(name.Length > 0 ? name : trimmed, 48);
    }

    private static string Quoted(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "text" : "\u201C" + Clip(text, 44) + "\u201D";

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : Clip(url ?? "a page", 44);

    internal static string? Str(JsonNode? node)
    {
        try { return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null; }
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>
/// The step a Workflow agent is on, followed from its transcript on disk. A Workflow's agents stream nothing to the
/// session (see WorkflowTranscriptStore), so this is the only way to say what one is doing. Rows are fed in as its
/// file gains them: a call is current from its tool_use until its tool_result, and between calls the newest block
/// says whether it is thinking or writing. Only the phrases are kept, never the rows.
/// </summary>
internal sealed class WorkflowAgentSteps
{
    private readonly List<(string Id, AgentActivityStep Now, AgentActivityStep Done)> _open = new();
    private bool _writing, _thinking;

    /// <summary>The call it is in the middle of, or between calls what it is doing instead; null before any row.</summary>
    public AgentActivityStep? Now => _open.Count > 0 ? _open[^1].Now
        : _writing ? new(WorkflowActivity.WritingGlyph, "Writing its report")
        : _thinking || Last is not null ? new(WorkflowActivity.ThinkingGlyph, "Thinking") : null;

    /// <summary>The newest call that has returned.</summary>
    public AgentActivityStep? Last { get; private set; }

    public void Apply(IEnumerable<JsonObject> rows)
    {
        foreach (var row in rows)
        {
            if (row["message"]?["content"] is not JsonArray blocks) continue;
            var assistant = WorkflowActivity.Str(row["type"]) == "assistant";
            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (WorkflowActivity.Str(block["type"]))
                {
                    case "tool_use" when assistant:
                        _writing = _thinking = false;
                        if (WorkflowActivity.Str(block["id"]) is not { } id || WorkflowActivity.Str(block["name"]) is not { } name
                            || WorkflowActivity.IsBookkeeping(name)) break;
                        var tool = new ToolItem { Id = id, Name = name, Input = block["input"] };
                        if (tool.IsTodoTool) break;
                        _open.Add((id, new(tool.Icon, WorkflowActivity.Phrase(tool, past: false)) { Tool = name },
                            new(tool.Icon, WorkflowActivity.Phrase(tool, past: true)) { Tool = name }));
                        break;
                    case "tool_result" when !assistant:
                        var call = _open.FindIndex(open => open.Id == WorkflowActivity.Str(block["tool_use_id"]));
                        if (call < 0) break;
                        var failed = block["is_error"] is JsonValue error && error.TryGetValue<bool>(out var isError) && isError;
                        Last = _open[call].Done with { Failed = failed };
                        _open.RemoveAt(call);
                        break;
                    case "text" when assistant && WorkflowActivity.Str(block["text"]) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text):
                        _writing = true;
                        _thinking = false;
                        break;
                    case "thinking" or "redacted_thinking" when assistant:
                        _thinking = true;
                        _writing = false;
                        break;
                }
            }
        }
    }
}
