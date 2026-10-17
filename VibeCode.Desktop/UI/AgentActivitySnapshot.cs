using System.IO;
using System.Windows;
using VibeCode.Services;

namespace VibeCode.UI;

public enum AgentActivityState { Working, Waiting, Idle, Error, Paused }

/// <summary>Presentation-only snapshot of an open chat. Never starts a provider or reads chat history.</summary>
public sealed record AgentActivityNode(
    string Id, string ProjectKey, string ProjectName, string Directory, string Label,
    string Task, string Model, string Account, string Role, AgentActivityState State,
    string Status, int ActiveChildren, ChatViewModel Chat)
{
    public string? ParentAgentId { get; init; }
    public bool IsCentralPlanner { get; init; }
}

public sealed record AgentActivityProject(string Key, string Name, string Directory,
    IReadOnlyList<AgentActivityNode> Agents)
{
    public int Working => Agents.Count(a => a.State == AgentActivityState.Working);
}

public static class AgentActivitySnapshot
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);
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
            nodes.Add(template with { Id = id, Label = "Central orchestrator", Role = "Central planner", Account = "",
                Task = live ? central.Activity!.Status : central.State == "completed" ? "Assignments delivered" : "Assignment " + central.State,
                Model = ProviderModelCatalog.DisplayName(central.Provider) + " · " + (central.Model ?? "Default model"),
                State = state, Status = live ? "Planning" : central.State == "completed" ? "Completed" : central.State == "failed" ? "Error" : "Stopped",
                ActiveChildren = 0, IsCentralPlanner = true, ParentAgentId = null });
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
        return new(chat.BridgeAgentId, directory, name, directory,
            chat.IsBridgeAgent ? chat.BridgeLabel : chat.AgentDisplay,
            chat.IsBridgeAgent && chat.BridgeHeaderTaskTitle.Length > 0 ? chat.BridgeHeaderTaskTitle : chat.Title,
            chat.ModelDisplay, chat.ShowBridgeAccountLabel ? chat.AccountLabel : "",
            chat.IsBridgeManager ? "Orchestrator" : chat.BridgeHasCoordinator ? "Worker" : chat.IsBridgeAgent ? "Bridge" : "Chat",
            state, status, chat.ActiveSubagentCount, chat) { ParentAgentId = chat.BridgeCoordinatorAgentId };
    }

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
