using System.Runtime.CompilerServices;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Sleeping tabs for agents. A chat or Bridge pane left idle past Settings' idle time returns to the asleep state the
/// restore path already uses (see <see cref="ChatViewModel.SleepWhileIdle"/>): its processes end, its transcript stays, and
/// the next prompt, opening it or a peer message resumes the same session. When Windows signals low memory the agents idle
/// longest sleep first, silently: a toast and chime on every sweep was noise, so only the app log
/// (<see cref="CrashLog.Note"/>) records how many slept and the memory before and after. Agents on screen never sleep, and
/// neither does any agent that could lose work by it (<see cref="ChatViewModel.IdleSleepBlocker"/>).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Under memory pressure an agent idle at least this long may sleep before its configured idle time.</summary>
    private static readonly TimeSpan LowMemoryMinimumIdle = TimeSpan.FromMinutes(2);
    private readonly ConditionalWeakTable<ChatViewModel, StrongBox<DateTimeOffset>> _seenAwakeSince = new();
    private DispatcherTimer? _idleSleepTimer;
    private bool _lowMemorySleeping;

    internal Func<DateTimeOffset> IdleSleepClock { get; set; } = () => DateTimeOffset.UtcNow;

    private void StartIdleSleep()
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1") return;   // smoke runs and tests drive sleep themselves
        _idleSleepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _idleSleepTimer.Tick += (_, _) => SleepIdleAgents();
        _idleSleepTimer.Start();
        LowMemorySignal.Start(() => ApplicationDispatcher.InvokeAsync(SleepForLowMemoryAsync));
        OverlayHookWatch.Start();   // Discord's overlay hook leaks memory here in bursts; tell the user, see the class

        // Opening a Bridge opens every pane in it, so its sleeping panes wake the way an opened chat does.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShowBridge) or nameof(SecondaryShowBridge)) WakeShownSleepers();
        };
        BridgePanes.CollectionChanged += (_, _) => ApplicationDispatcher.InvokeAsync(WakeShownSleepers, DispatcherPriority.Background);
    }

    private HashSet<ChatViewModel> ShownAgents()
    {
        var shown = new HashSet<ChatViewModel>();
        if (ActiveChat is { } primary) shown.Add(primary);
        if (SecondaryActiveChat is { } secondary) shown.Add(secondary);
        if (ShowBridge) shown.UnionWith(BridgePanes);
        if (SecondaryShowBridge) shown.UnionWith(SecondaryBridgePanes);
        return shown;
    }

    private void WakeShownSleepers()
    {
        foreach (var chat in ShownAgents())
        {
            if (chat.IsGemini) chat.RevealGeminiWhileAsleep();
            else if (chat.IsIdleAsleep) chat.Wake();
        }
    }

    /// <summary>Every agent that could sleep, longest idle first. An agent's idle time runs from its last activity, or from
    /// when it was last seen awake or on screen, so a chat restored with an old timestamp still gets a full idle period.</summary>
    private List<(ChatViewModel Chat, TimeSpan Idle)> IdleAgents(DateTimeOffset now)
    {
        var shown = ShownAgents();
        var idle = new List<(ChatViewModel Chat, TimeSpan Idle)>();
        foreach (var chat in Chats.Concat(AllLivePanes()).Distinct())
        {
            if (chat.IsAsleep) { _seenAwakeSince.Remove(chat); continue; }
            // A Gemini tab on screen still sleeps: looking at the transcript is not a reason to keep Antigravity in RAM.
            if (shown.Contains(chat) && !chat.IsGemini) { _seenAwakeSince.AddOrUpdate(chat, new StrongBox<DateTimeOffset>(now)); continue; }
            var since = new[]
            {
                _seenAwakeSince.GetValue(chat, _ => new StrongBox<DateTimeOffset>(now)).Value,
                chat.LastActivityAt ?? DateTimeOffset.MinValue,
                chat.LastAgentActivityAt ?? DateTimeOffset.MinValue,
            }.Max();
            if (chat.IdleSleepBlocker is null) idle.Add((chat, now - since));
        }
        return idle.OrderByDescending(agent => agent.Idle).ToList();
    }

    private void SleepIdleAgents()
    {
        var minutes = AppSettings.Current.IdleSleepMinutes;
        if (minutes <= 0) return;
        foreach (var (chat, idle) in IdleAgents(IdleSleepClock()))
            if (idle >= TimeSpan.FromMinutes(minutes)) chat.SleepWhileIdle();
    }

    /// <summary>Windows says memory is low: the longest-idle half of the idle agents sleep now, whatever the idle time is set
    /// to, and the app log notes the outcome - no toast, no sound. If memory stays low the signal comes back and the next
    /// half sleeps.</summary>
    private async Task SleepForLowMemoryAsync()
    {
        if (_lowMemorySleeping || AppSettings.Current.IdleSleepMinutes <= 0) return;
        _lowMemorySleeping = true;
        try
        {
            var idle = IdleAgents(IdleSleepClock()).Where(agent => agent.Idle >= LowMemoryMinimumIdle).ToList();
            if (idle.Count == 0) return;
            var before = await Task.Run(LowMemorySignal.ChildProcessMemory);
            var slept = 0;
            foreach (var (chat, _) in idle.Take((idle.Count + 1) / 2))
                if (chat.SleepWhileIdle()) slept++;
            if (slept == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(5));   // an ended CLI gets a few seconds to exit before it is counted
            var after = await Task.Run(LowMemorySignal.ChildProcessMemory);
            CrashLog.Note("LowMemorySleep",
                $"Windows signalled low memory; {slept} idle agent{(slept == 1 ? "" : "s")} put to sleep. " +
                $"Processes VibeCode started: {before >> 20} MB before, {after >> 20} MB after.");
        }
        catch (Exception ex) { CrashLog.Write(ex, "LowMemorySleep"); }
        finally { _lowMemorySleeping = false; }
    }
}
