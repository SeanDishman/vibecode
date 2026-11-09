using VibeCode.UI;

namespace VibeCode.Terminal;

/// <summary>Tracks independent console windows on the desktop dispatcher, including pending launches.</summary>
public sealed class TerminalWindowCollection : IDisposable
{
    private sealed class Window(TerminalSession session)
    {
        public TerminalSession Session { get; } = session;
        public TaskCompletionSource<TerminalSession> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action DesktopRequested { get; set; } = null!;
        public Action Ended { get; set; } = null!;
    }

    private readonly List<Window> _windows = [];
    private readonly Func<ChatViewModel, TerminalSession> _create;
    private readonly Func<TerminalSession, Task> _start;
    private readonly Action<TerminalSession> _focus;
    private bool _disposed;

    public TerminalWindowCollection(Func<ChatViewModel, TerminalSession> create,
        Func<TerminalSession, Task>? start = null, Action<TerminalSession>? focus = null)
    {
        _create = create;
        _start = start ?? (session => session.StartAsync());
        _focus = focus ?? (session => session.Focus());
    }

    public int Count => _windows.Count;
    public event Action<TerminalSession>? DesktopRequested;
    public event Action<TerminalSession>? Ended;

    // /new changes a window's host. Look at its current host rather than indexing its original chat.
    public TerminalSession? Find(ChatViewModel host) =>
        _windows.FirstOrDefault(window => ReferenceEquals(window.Session.Host, host))?.Session;

    public Task<TerminalSession> OpenAsync(ChatViewModel host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var existing = _windows.FirstOrDefault(window => ReferenceEquals(window.Session.Host, host));
        if (existing is not null) return FocusWhenReadyAsync(existing);

        var window = new Window(_create(host));
        window.DesktopRequested = () => DesktopRequested?.Invoke(window.Session);
        window.Ended = () => OnEnded(window);
        window.Session.DesktopRequested += window.DesktopRequested;
        window.Session.Ended += window.Ended;
        // Publish the pending launch before starting it so another click shares its completion.
        _windows.Add(window);
        _ = StartAsync(window);
        return window.Ready.Task;
    }

    private async Task StartAsync(Window window)
    {
        try
        {
            await _start(window.Session);
            if (_windows.Contains(window)) window.Ready.TrySetResult(window.Session);
        }
        catch (Exception ex)
        {
            Remove(window);
            window.Ready.TrySetException(ex);
        }
    }

    private async Task<TerminalSession> FocusWhenReadyAsync(Window window)
    {
        var session = await window.Ready.Task;
        if (_windows.Contains(window)) _focus(session);
        return session;
    }

    private void OnEnded(Window window)
    {
        if (!Remove(window)) return;
        window.Ready.TrySetCanceled();
        Ended?.Invoke(window.Session);
    }

    private bool Remove(Window window)
    {
        if (!_windows.Remove(window)) return false;
        window.Session.DesktopRequested -= window.DesktopRequested;
        window.Session.Ended -= window.Ended;
        window.Session.Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows.ToArray())
        {
            Remove(window);
            window.Ready.TrySetCanceled();
        }
    }
}
