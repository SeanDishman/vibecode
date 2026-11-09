using System.IO;
using System.Windows;
using VibeCode.Services;

namespace VibeCode.UI;

public enum AgentActivityState { Working, Waiting, Idle, Error, Paused }

/// <summary>Presentation-only snapshot of an open chat. Never starts a provider or reads stored history; the
/// activity lines come from the newest in-memory tool rows of the chat's own transcript (see WorkflowActivity).</summary>
public sealed record AgentActivityNode(
    string Id, string ProjectKey, string ProjectName, string Directory, string Label,
    string Task, string Model, string Account, string Role, AgentActivityState State,
    string Status, int ActiveChildren, ChatViewModel Chat)
{
    public string? ParentAgentId { get; init; }
    public bool IsCentralPlanner { get; init; }
    /// <summary>What the agent is doing at this moment; null while nothing is in flight.</summary>
    public AgentActivityStep? Now { get; init; }
    /// <summary>The newest step it finished, or for an idle Bridge agent the summary it reported itself.</summary>
    public AgentActivityStep? Last { get; init; }
    /// <summary>The agent's own todo list: completed and total items. Zero total means it keeps none.</summary>
    public int PlanDone { get; init; }
    public int PlanTotal { get; init; }
    /// <summary>The OpenRouter id of the agent's model, for public throughput; null when it has no listing.</summary>
    public string? OpenRouterId { get; init; }
    /// <summary>When the current turn started (UTC), while it is running.</summary>
    public DateTime? WorkingSinceUtc { get; init; }
    /// <summary>The last observed agent work, for "idle · 4m ago".</summary>
    public DateTimeOffset? LastActiveAt { get; init; }
    /// <summary>The Bridge activity summary the agent reported itself; empty for plain chats.</summary>
    public string Reported { get; init; } = "";
    /// <summary>Its working subagents in the roster's order, at most <see cref="WorkflowActivity.MaxSubagents"/> of
    /// them; <see cref="ActiveChildren"/> is the full count.</summary>
    public IReadOnlyList<AgentActivitySubagent> Subagents { get; init; } = [];
}

/// <summary>One of an agent's working subagents, as the wall shows it on hover: its name in the chat's Subagents
/// roster, what it was asked for, the step it is on and the newest one it finished.</summary>
public sealed record AgentActivitySubagent(string Id, string Label, string Task, AgentActivityStep Now, AgentActivityStep? Last);

public sealed record AgentActivityProject(string Key, string Name, string Directory,
    IReadOnlyList<AgentActivityNode> Agents)
{
    public int Working => Agents.Count(a => a.State == AgentActivityState.Working);
}

public static class AgentActivitySnapshot
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);
    // MainViewModel owns all open windows. LiveBridgePeers includes parked/second-monitor Bridges;
    // BridgePanes also covers a host while its sidebar membership is being reconciled.
    public static IReadOnlyList<AgentActivityProject> Capture(MainViewModel? source, DateTimeOffset? now = null) => source is null
        ? [] : Build(source.WorkflowAgents, now);

    public static IReadOnlyList<AgentActivityProject> Build(IEnumerable<ChatViewModel> chats, DateTimeOffset? now = null)
    {
        var observedAt = now ?? DateTimeOffset.UtcNow;
        var open = chats.Distinct().Where(c => c.Status != "closed").ToArray();
        var nodes = open.Where(c => RecentlyActive(c, observedAt)).Select(Node).ToList();
        // Central planning uses its own temporary provider session, not a ChatViewModel in the roster.
        foreach (var roster in open.Where(c => c.BridgeWork?.CentralPlan is not null).GroupBy(c => c.BridgeWork!.RunId))
        {
            var host = roster.FirstOrDefault(c => c.IsBridgeHost) ?? roster.First();
            var central = host.BridgeWork!.CentralPlan!;
            var live = central.Activity is { IsDisposed: false } && central.State is "planning" or "handing_off";
            if (!live && !WithinWindow(central.LastActivityAt, observedAt)) continue;
            var id = "central:" + roster.Key;
            var template = Node(host);
            var state = live ? AgentActivityState.Working : central.State == "failed" ? AgentActivityState.Error
                : central.State == "interrupted" ? AgentActivityState.Paused : AgentActivityState.Idle;
            var stage = live ? central.Activity!.Status : "";
            nodes.Add(template with { Id = id, Label = "Central orchestrator", Role = "Central planner", Account = "",
                Task = live ? "Plan and assign this Bridge's work" : central.State == "completed" ? "Assignments delivered" : "Assignment " + central.State,
                Model = ProviderModelCatalog.DisplayName(central.Provider) + " · " + (central.Model ?? "Default model"),
                State = state, Status = live ? "Planning" : central.State == "completed" ? "Completed" : central.State == "failed" ? "Error" : "Stopped",
                ActiveChildren = 0, Subagents = [], IsCentralPlanner = true, ParentAgentId = null,
                // The planner runs in its own temporary session, so the host's transcript says nothing about it;
                // its live stage ("Thinking…", "Writing assignments…") is what it is doing now.
                Now = live ? new AgentActivityStep(stage.StartsWith("Thinking", StringComparison.Ordinal) ? WorkflowActivity.ThinkingGlyph
                    : stage.StartsWith("Writing", StringComparison.Ordinal) ? WorkflowActivity.WritingGlyph : WorkflowActivity.PlanGlyph,
                    WorkflowActivity.Clip(stage)) : null,
                Last = central.State == "completed" ? new AgentActivityStep(WorkflowActivity.DoneGlyph, "Delivered the assignments") : null,
                PlanDone = 0, PlanTotal = 0, Reported = "", WorkingSinceUtc = null,
                LastActiveAt = central.LastActivityAt,
                OpenRouterId = SpeedId(central.Model, central.Provider) });
            for (var i = 0; i < nodes.Count; i++)
                if (!nodes[i].IsCentralPlanner && nodes[i].Chat.IsBridgeManager && nodes[i].Chat.BridgeWork?.RunId == roster.Key)
                    nodes[i] = nodes[i] with { ParentAgentId = id };
        }
        return nodes
            .GroupBy(n => n.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AgentActivityProject(g.Key, g.First().ProjectName, g.First().Directory,
                g.ToArray()))
            .OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool RecentlyActive(ChatViewModel chat, DateTimeOffset now)
    {
        if (chat.Status is "running" or "preparing" || chat.HasWorkingSubagents) return true;
        // Opening a chat, booting its CLI, or receiving Bridge mail is not agent work.
        // Assistant timestamps cover older sessions that predate explicit agent-activity tracking.
        var activity = chat.LastAgentActivityAt ?? chat.LastAssistantAt;
        return WithinWindow(activity, now);
    }

    private static bool WithinWindow(DateTimeOffset? activity, DateTimeOffset now) =>
        activity is { } at && at > now - RecentWindow && at <= now;

    private static AgentActivityNode Node(ChatViewModel chat)
    {
        var scratch = string.IsNullOrWhiteSpace(chat.Cwd) || NoDirectoryWorkspace.IsNoDirectory(chat.Cwd);
        var directory = scratch ? "" : NormalizeDirectory(chat.Cwd);
        var name = scratch ? NoDirectoryWorkspace.DisplayName : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (string.IsNullOrEmpty(name)) name = directory;
        var state = StateOf(chat);
        var status = state switch
        {
            AgentActivityState.Paused => chat.IsWorking ? "Stopping" : "Stopped",
            AgentActivityState.Error => "Error",
            AgentActivityState.Waiting => chat.AuthNeeded ? "Sign-in needed" : chat.PendingPermissionCount > 0
                ? "Approval needed" : chat.WaitingForLimitReset ? "Usage limit" : "Waiting",
            AgentActivityState.Working => chat.Status == "starting" ? "Starting" : chat.Status == "preparing" ? "Preparing" : "Working",
            _ => chat.IsBridgeAgent && chat.BridgeTaskState == "completed" ? "Completed" : "Idle",
        };
        var (now, last) = WorkflowActivity.Read(chat, state == AgentActivityState.Working);
        var reported = chat.IsBridgeAgent ? WorkflowActivity.Clip(chat.BridgeActivityDetail) : "";
        // An idle Bridge agent's own report ("Fixed the autoscroll; 422 checks pass") says more about what it got done
        // than the last tool it happened to call.
        if (state != AgentActivityState.Working && reported.Length > 0) last = new(WorkflowActivity.ReportGlyph, reported);
        return new(chat.BridgeAgentId, directory, name, directory,
            chat.IsBridgeAgent ? chat.BridgeLabel : chat.AgentDisplay,
            chat.IsBridgeAgent && chat.BridgeHeaderTaskTitle.Length > 0 ? chat.BridgeHeaderTaskTitle : chat.Title,
            chat.ModelDisplay, chat.ShowBridgeAccountLabel ? chat.AccountLabel : "",
            chat.IsBridgeManager ? "Orchestrator" : chat.BridgeHasCoordinator ? "Worker" : chat.IsBridgeAgent ? "Bridge" : "Chat",
            state, status, chat.ActiveSubagentCount, chat)
        {
            ParentAgentId = chat.BridgeCoordinatorAgentId,
            Now = now,
            Last = last,
            PlanDone = chat.TodoDone,
            PlanTotal = chat.TodoTotal,
            OpenRouterId = OpenRouterId(chat),
            WorkingSinceUtc = state == AgentActivityState.Working ? chat.WorkStartedAtUtc : null,
            LastActiveAt = chat.LastAgentActivityAt ?? chat.LastAssistantAt,
            Reported = reported,
            Subagents = WorkflowActivity.ReadSubagents(chat),
        };
    }

    private static string? OpenRouterId(ChatViewModel chat)
    {
        var id = chat.Model;
        var model = chat.CurrentModel;
        // CurrentModel falls back to the catalog's default entry for an id the catalog does not list. That entry's
        // speed would describe a different model, so an unlisted explicit id is mapped as written instead.
        var listed = model is not null && (string.IsNullOrEmpty(id) || id == "default" || model.Value != "default"
            || string.Equals(model.ResolvedModel, id, StringComparison.OrdinalIgnoreCase));
        return (listed ? ModelSpeedService.OpenRouterId(model!) : null) ?? SpeedId(id, chat.Provider);
    }

    /// <summary>A raw model id mapped through the same aliases as a picker row.</summary>
    private static string? SpeedId(string? model, string provider) => string.IsNullOrWhiteSpace(model) ? null
        : ModelSpeedService.OpenRouterId(new ModelChoice { Value = model, Display = model, Provider = provider });

    private static AgentActivityState StateOf(ChatViewModel chat)
    {
        if (chat.BridgeWorkerPaused) return AgentActivityState.Paused;
        if (chat.AuthNeeded || chat.PendingPermissionCount > 0 || chat.WaitingForLimitReset || chat.WaitingForBridgeRetry)
            return AgentActivityState.Waiting;
        if (chat.Status == "error") return AgentActivityState.Error;
        if (chat.IsWorking || chat.HasWorkingSubagents) return AgentActivityState.Working;
        if (chat.IsBridgeAgent && chat.BridgeTaskState == "interrupted") return AgentActivityState.Paused;
        if (chat.IsBridgeAgent && chat.BridgeTaskState == "waiting") return AgentActivityState.Waiting;
        return AgentActivityState.Idle;
    }

    private static string NormalizeDirectory(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return path.TrimEnd('\\', '/'); }
    }

    public static MainViewModel? ResolveSource(Window? window)
    {
        for (var current = window; current is not null; current = current.Owner)
            if (current.DataContext is MainViewModel source) return source;
        return Application.Current?.MainWindow?.DataContext as MainViewModel;
    }
}
