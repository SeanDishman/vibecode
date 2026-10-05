using System.IO;
using System.ComponentModel;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel : IJarvisActionHost, IJarvisContextHost, IJarvisChatActionHost
{
    private JarvisViewModel? _jarvis;
    private ChatViewModel? _jarvisOpenedChat;
    private bool _jarvisMemoryWatching;
    private ChatViewModel? _jarvisMemoryChat;
    private JarvisMemoryPermission? _jarvisMemoryPermission;
    private CancellationTokenSource? _jarvisMemoryEpoch;
    private JarvisMemoryAccess? _jarvisMemoryAccess;
    private CancellationToken _jarvisChatLifetime;
    private readonly object _jarvisMemoryGate = new();
    public JarvisViewModel Jarvis => _jarvis ??= new(this);
    public void ShutdownJarvis()
    {
        _jarvis?.Dispose();
        lock (_jarvisMemoryGate)
        {
            if (_jarvisMemoryWatching)
            {
                AppSettings.Changed -= RefreshJarvisMemoryPermission;
                PropertyChanged -= OnJarvisHostChanged;
                if (_jarvisMemoryChat is not null) _jarvisMemoryChat.PropertyChanged -= OnJarvisChatChanged;
                _jarvisMemoryWatching = false;
            }
            _jarvisMemoryEpoch?.Cancel();
            _jarvisMemoryEpoch?.Dispose();
            _jarvisMemoryEpoch = null;
            _jarvisMemoryAccess = null;
            _jarvisMemoryPermission = null;
            _jarvisMemoryChat = null;
        }
    }

    JarvisContext IJarvisActionHost.GetContext()
    {
        EnsureJarvisMemoryWatch();
        RefreshJarvisMemoryPermission();
        return new(ActiveChat?.Cwd,
            Projects.Select(p => p.Cwd).Concat(RecentProjects.Select(p => p.Cwd)).Concat(Chats.Select(c => c.Cwd))
                .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppSettings.Current.SecondBrainEnabled)
        {
            MemoryAccess = _jarvisMemoryAccess, ReadMemoryPermission = ReadJarvisMemoryPermission,
            CurrentChatId = ActiveChat?.BridgeAgentId,
            OpenChats = Chats.Select(JarvisSummary).ToArray(),
            Settings = JarvisSettingsCatalog.Snapshot(AppSettings.Current),
        };
    }

    private JarvisMemoryPermission ReadJarvisMemoryPermission()
    {
        var chat = ActiveChat;
        var memory = chat?.JarvisMemoryContext();
        return new(AppSettings.Current.SecondBrainEnabled,
            chat is not null && !chat.ExcludeFromMemory && chat.Status != "closed",
            memory?.MemorySessionId);
    }

    private void EnsureJarvisMemoryWatch()
    {
        lock (_jarvisMemoryGate)
        {
            if (_jarvisMemoryWatching) return;
            _jarvisMemoryWatching = true;
            AppSettings.Changed += RefreshJarvisMemoryPermission;
            PropertyChanged += OnJarvisHostChanged;
        }
    }

    private void OnJarvisHostChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ActiveChat)) RefreshJarvisMemoryPermission(); }

    private void OnJarvisChatChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ChatViewModel.ExcludeFromMemory) or nameof(ChatViewModel.Status)
            or nameof(ChatViewModel.SecondBrainActive)) RefreshJarvisMemoryPermission();
    }

    private void RefreshJarvisMemoryPermission()
    {
        lock (_jarvisMemoryGate)
        {
            if (!_jarvisMemoryWatching) return;
            if (!ReferenceEquals(_jarvisMemoryChat, ActiveChat))
            {
                if (_jarvisMemoryChat is not null) _jarvisMemoryChat.PropertyChanged -= OnJarvisChatChanged;
                _jarvisMemoryChat = ActiveChat;
                if (_jarvisMemoryChat is not null) _jarvisMemoryChat.PropertyChanged += OnJarvisChatChanged;
            }
            var permission = ReadJarvisMemoryPermission();
            var lifetime = _jarvisMemoryChat?.JarvisMemoryContext().AccessCancellation ?? CancellationToken.None;
            if (_jarvisMemoryPermission == permission && _jarvisChatLifetime == lifetime) return;
            _jarvisMemoryEpoch?.Cancel();
            _jarvisMemoryEpoch?.Dispose();
            _jarvisMemoryEpoch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _jarvisChatLifetime = lifetime;
            _jarvisMemoryPermission = permission;
            _jarvisMemoryAccess = permission.EffectiveAllowed
                ? new(permission.SourceId!, _jarvisMemoryEpoch.Token, ReadJarvisMemoryPermission) : null;
            _jarvis?.RefreshMemoryPermission();
        }
    }

    async Task<JarvisContext> IJarvisContextHost.GetContextAsync(string request, CancellationToken cancellationToken)
    {
        var context = ((IJarvisActionHost)this).GetContext();
        // Do not even instantiate/contact the memory backend while the extension is disabled.
        if (!context.CurrentMemoryPermission.EffectiveAllowed || context.MemoryAccess is not { IsAllowed: true }
            || context.CurrentProject is null) return context;
        var active = ActiveChat;
        var memoryChat = active?.JarvisMemoryContext();
        if (memoryChat is null || !AgentMemoryService.CanAccessMemory(memoryChat)) return context;
        var recalled = await AgentMemoryService.Instance.RecallForAssistantAsync(memoryChat, request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Revocation can arrive during the asynchronous request; discard context before crossing the model boundary.
        return context.MemoryAccess.IsAllowed && AgentMemoryService.CanAccessMemory(memoryChat)
            ? context with { MemoryContext = recalled ?? "" }
            : context with { MemoryEnabled = AppSettings.Current.SecondBrainEnabled, MemoryContext = "", MemoryAccess = null };
    }

    async Task<JarvisActionReceipt> IJarvisActionHost.OpenProjectAsync(string path, JarvisSelection selection,
        CancellationToken cancellationToken)
    {
        JarvisPathPolicy.RequireExisting(path);
        cancellationToken.ThrowIfCancellationRequested();
        var existing = Chats.FirstOrDefault(c => JarvisPathPolicy.PathsEqual(c.Cwd, path) && c.Status is not ("closed" or "error"));
        var chat = existing ?? NewJarvisChat(path, selection);
        if (existing is null) _jarvisOpenedChat = chat;
        await chat.WaitForJarvisReadyAsync(selection, cancellationToken, verifySelection: existing is null);
        cancellationToken.ThrowIfCancellationRequested();
        HideBridge();
        ActiveChat = chat;
        RememberDirectorySelection(path);
        if (!Chats.Contains(chat) || !ReferenceEquals(ActiveChat, chat) || !JarvisPathPolicy.PathsEqual(chat.Cwd, path))
            throw new InvalidOperationException("VibeCode did not activate the requested project.");
        return new("open_project", path, $"Opened {path} in VibeCode.", chat.SessionId, chat.Provider, chat.Model);
    }

    async Task<JarvisActionReceipt> IJarvisActionHost.StartCodingChatAsync(string path, string task,
        JarvisSelection selection, CancellationToken cancellationToken)
    {
        JarvisPathPolicy.RequireExisting(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException("A coding chat needs a task.");
        var chat = _jarvisOpenedChat is { } opened && Chats.Contains(opened)
            && JarvisPathPolicy.PathsEqual(opened.Cwd, path) && opened.Provider == selection.Provider
            && opened.Model == selection.Model && opened.Effort == selection.Effort
            && !opened.Items.OfType<UserItem>().Any() && opened.Status == "idle"
            ? opened : NewJarvisChat(path, selection);
        _jarvisOpenedChat = null;
        await chat.WaitForJarvisReadyAsync(selection, cancellationToken);
        HideBridge();
        ActiveChat = chat;
        await chat.DispatchJarvisTaskAsync(task, cancellationToken);
        if (!Chats.Contains(chat) || !ReferenceEquals(ActiveChat, chat) || !chat.Items.OfType<UserItem>().Any(i => i.Text == task))
            throw new InvalidOperationException("The coding task could not be verified in the active chat.");
        return new("start_chat", path, $"Started a coding chat with {chat.ProviderDisplay} in {path} and handed it your task: {task}",
            chat.SessionId, chat.Provider, chat.Model);
    }

    private ChatViewModel NewJarvisChat(string path, JarvisSelection selection)
    {
        JarvisDialogueService.ValidateSelection(selection);
        return NewChat(path, provider: selection.Provider, accountId: selection.AccountId,
            configure: chat => chat.ApplyBridgeConfiguration(new(selection.Provider, selection.Model, selection.Effort)));
    }
}
