using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed class SidebarDirectory : Observable
{
    private bool _open = true;
    public required string Cwd { get; init; }
    /// <summary>The one section that collects pinned chats from every folder, shown above all folders.</summary>
    public bool IsPinned { get; init; }
    public string Name => IsPinned ? "Pinned" : RecentDirectoryHistory.DisplayName(Cwd);
    public string Description => IsPinned ? "Pinned chats stay above every folder" : Cwd;
    public bool Open { get => _open; set => Set(ref _open, value); }
}

internal sealed class SidebarDirectoryGrouping : GroupDescription
{
    private readonly Dictionary<string, SidebarDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly SidebarDirectory _pinned = new() { Cwd = "", IsPinned = true };
    public override object GroupNameFromItem(object item, int level, CultureInfo culture)
    {
        var chat = (ChatViewModel)item;
        if (chat.Pinned) return _pinned;
        var raw = chat.Cwd;
        var cwd = RecentDirectoryHistory.NormalizePath(raw) ?? raw;
        if (!_directories.TryGetValue(cwd, out var directory))
            _directories[cwd] = directory = new SidebarDirectory { Cwd = cwd };
        return directory;
    }
}

public sealed partial class MainViewModel
{
    private readonly List<ChatViewModel> _sidebarChats = new();
    private List<SessionEntry> _catalogChats = new();
    private List<ProjectEntry> _catalogProjects = new();
    private DispatcherTimer? _historyCleanupTimer;
    private readonly SemaphoreSlim _historyScanGate = new(1, 1);
    private DateTimeOffset _lastHistoryScan;
    private bool _catalogOwnedOnly;

    private ICollectionView CreateSidebarChatView()
    {
        var view = new ListCollectionView(_sidebarChats);
        view.GroupDescriptions.Add(new SidebarDirectoryGrouping());
        Chats.CollectionChanged += (_, _) => RefreshSidebarChats();
        return view;
    }

    private void RefreshSidebarChats()
    {
        // The sidebar represents open chats. Retained transcripts belong to history;
        // a catalog refresh must not bring a closed chat (or its folder) back here.
        // Groups appear in the order of their first row, so listing pinned chats first puts the Pinned section above
        // every folder. Within each section the newest assistant activity still comes first.
        var ordered = Chats.Where(ChatVisibleInSidebar).OrderByDescending(chat => chat.Pinned)
            .ThenByDescending(chat => chat.SidebarActivityAt).ToList();
        _sidebarChats.Clear();
        _sidebarChats.AddRange(ordered);
        ChatGroups.Refresh();
    }

    public void CloseChatGroup(CollectionViewGroup group)
    {
        // Closing refreshes the collection and may also remove a Bridge's peers. Snapshot the clicked group first.
        foreach (var chat in group.Items.OfType<ChatViewModel>().ToArray())
            if (Chats.Contains(chat)) CloseChat(chat);
    }

    private bool KeepHistoryChat(SessionEntry session)
    {
        var settings = AppSettings.Current;
        return !settings.DeletedSessions.Contains(session.SessionId)
            && (!settings.ShowOnlyOwnedSessions || settings.OwnedSessions.Contains(session.SessionId))
            && !settings.HiddenProjects.Any(p => RecentDirectoryHistory.PathsEqual(p, session.Cwd))
            && (!ChatHistoryRetention.Expired(ChatHistoryRetention.LastActivity(session, settings), DateTimeOffset.UtcNow)
                || settings.ChatMetadata.GetValueOrDefault(ChatMetadata.Key(session.Provider, session.SessionId))?.IsLocked == true
                || settings.SavedBridges.Any(b => b.HostSessionId == session.SessionId || b.Peers.Any(p => p.SessionId == session.SessionId)));
    }

    /// <summary>Runs before restoring provider processes; the same lightweight sweep runs only every six hours.</summary>
    public async Task InitializeChatHistoryAsync()
    {
        await RefreshChatHistoryAsync(cleanup: true);
        if (_historyCleanupTimer is not null) return;
        _historyCleanupTimer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = ChatHistoryRetention.CheckInterval };
        _historyCleanupTimer.Tick += async (_, _) => await RefreshChatHistoryAsync(cleanup: true);
        _historyCleanupTimer.Start();
    }

    private async Task RefreshChatHistoryAsync(bool cleanup)
    {
        if (cleanup) await _historyScanGate.WaitAsync();
        else if (!_historyScanGate.Wait(0)) return;
        try
        {
            var settings = AppSettings.Current;
            var allowed = settings.ShowOnlyOwnedSessions ? settings.OwnedSessions.Except(settings.DeletedSessions, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            if (DateTimeOffset.UtcNow - _lastHistoryScan >= TimeSpan.FromSeconds(5) || _catalogOwnedOnly != settings.ShowOnlyOwnedSessions)
            {
                _catalogOwnedOnly = settings.ShowOnlyOwnedSessions;
                _catalogProjects = await Task.Run(() => SessionCatalog.ListProjects(int.MaxValue, allowed));
                _lastHistoryScan = DateTimeOffset.UtcNow;
            }
            var projects = _catalogProjects;
            _catalogChats = projects.SelectMany(p => p.Sessions).ToList();
            var byId = _catalogChats.GroupBy(s => ChatMetadata.Key(s.Provider, s.SessionId), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.LastModified).First(), StringComparer.OrdinalIgnoreCase);
            foreach (var chat in Chats.Concat(LiveBridges().SelectMany(b => b.Panes)).Distinct())
                if (chat.SessionId is { } id && byId.TryGetValue(ChatMetadata.Key(chat.Provider, id), out var session)) chat.AdoptHistoryActivity(session);
            if (cleanup)
            {
                var protectedIds = Chats.Concat(LiveBridges().SelectMany(b => b.Panes))
                    .Where(c => c.ChatListIsWorking || c.IsLocked || !string.IsNullOrWhiteSpace(c.Draft))
                    .Select(c => c.SessionId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var expired = ChatHistoryRetention.Apply(settings, _catalogChats, DateTimeOffset.UtcNow, protectedIds);
                foreach (var chat in Chats.Where(c => c.SessionId is { } id && expired.Contains(id)).ToArray())
                {
                    CloseChat(chat);
                    settings.RemoveSavedBridge(chat.SessionId!, chat.Provider);
                }
                _unrestoredChats.RemoveAll(c => c.SessionId is { } id && expired.Contains(id));
                settings.Save();
                RefreshResumableBridges();
            }
            Projects.Clear();
            RecentSessions.Clear();
            foreach (var project in projects)
            {
                var sessions = project.Sessions.Where(KeepHistoryChat).OrderByDescending(s => ChatHistoryRetention.LastReply(s, settings)).ToList();
                if (sessions.Count == 0) continue;
                var vm = new ProjectVm { Cwd = project.Cwd, Name = project.Name, LastModified = sessions.Max(s => s.LastModified) };
                foreach (var session in sessions) vm.Sessions.Add(WithChatTitle(session));
                Projects.Add(vm);
            }
            foreach (var session in _catalogChats.Where(KeepHistoryChat).OrderByDescending(s => ChatHistoryRetention.LastReply(s, settings)).Take(10))
                RecentSessions.Add(WithChatTitle(session));
            RefreshRecentProjects();
            RefreshSidebarChats();
            Raise(nameof(HiddenCount));
        }
        catch (Exception error) { CrashLog.Write(error, "ChatHistoryCleanup"); }
        finally { _historyScanGate.Release(); }
    }
}
