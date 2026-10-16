using System.IO;
using System.Windows;
using VibeCode.Services;

namespace VibeCode.UI;

public enum AgentActivityState { Working, Waiting, Idle, Error, Paused }

/// <summary>Presentation-only snapshot of an open chat. Never starts a provider or reads chat history.</summary>
public sealed record AgentActivityNode(
    string Id, string ProjectKey, string ProjectName, string Directory, string Label,
    string Task, string Model, string Account, string Role, AgentActivityState State,
    string Status, int ActiveChildren, ChatViewModel Chat);

public sealed record AgentActivityProject(string Key, string Name, string Directory,
    IReadOnlyList<AgentActivityNode> Agents)
{
    public int Working => Agents.Count(a => a.State == AgentActivityState.Working);
}

public static class AgentActivitySnapshot
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);
    // MainViewModel owns all open windows. LiveBridgePeers includes parked/second-monitor Bridges;
    // BridgePanes also covers a host while its sidebar membership is being reconciled.
    public static IReadOnlyList<AgentActivityProject> Capture(MainViewModel? source, DateTimeOffset? now = null) => source is null
        ? [] : Build(source.Chats.Concat(source.BridgePanes).Concat(source.LiveBridgePeers), now);

    public static IReadOnlyList<AgentActivityProject> Build(IEnumerable<ChatViewModel> chats, DateTimeOffset? now = null)
    {
        var observedAt = now ?? DateTimeOffset.UtcNow;
        return chats.Distinct().Where(c => c.Status != "closed" && RecentlyActive(c, observedAt)).Select(Node)
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
        return activity is { } at && at >= now - RecentWindow && at <= now;
    }

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
            state, status, chat.ActiveSubagentCount, chat);
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
