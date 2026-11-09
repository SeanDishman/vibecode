namespace VibeCode.UI;

/// <summary>The whole fleet in a few words: who works, who needs you, who failed.</summary>
public readonly record struct TaskbarFleet(int Working, int NeedsYou, int Failed)
{
    /// <summary>"3 working · 1 needs you", or empty when nothing is happening.</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Working > 0 ? $"{Working} working" : "", NeedsYou > 0 ? $"{NeedsYou} needs you" : "", Failed > 0 ? $"{Failed} failed" : "",
    }.Where(part => part.Length > 0));
}

/// <summary>
/// The tray icon's tooltip while the app runs in the background. The taskbar button itself carries no badge or
/// progress bar: VibeCode leaves it as Windows draws it.
/// </summary>
public static class WorkflowTaskbar
{
    /// <summary>An error older than this no longer counts as a failure: by then it is old news, not an alarm.</summary>
    public static readonly TimeSpan RecentError = TimeSpan.FromMinutes(15);

    /// <summary>The tray icon's tooltip while the app runs in the background.</summary>
    public static string Tooltip(MainViewModel vm) => Measure(vm.WorkflowAgents, DateTimeOffset.UtcNow).Summary is { Length: > 0 } summary
        ? "VibeCode — " + summary : Services.BackgroundRunPolicy.Tooltip(0);

    /// <summary>Count the fleet straight from the open chats, so the tooltip does not depend on the wall being open.</summary>
    public static TaskbarFleet Measure(IEnumerable<ChatViewModel> chats, DateTimeOffset now)
    {
        int working = 0, needs = 0, failed = 0;
        foreach (var chat in chats.Distinct())
        {
            if (chat.Status == "closed") continue;
            var asks = chat.AuthNeeded || chat.PendingPermissionCount > 0;
            var busy = !asks && (chat.IsWorking || chat.HasWorkingSubagents);
            var fault = !asks && !busy && chat.Status == "error"
                && (chat.LastAgentActivityAt ?? chat.LastAssistantAt) is { } at && now - at < RecentError;
            if (asks) needs++;
            if (busy) working++;
            if (fault) failed++;
        }
        return new(working, needs, failed);
    }
}
