using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VibeCode.Services;

/// <summary>
/// VibeCode's notification-area icon: the way back to a shell that was closed while its agents kept working, and
/// the only place to actually quit once the window is gone.
///
/// Hand-rolled on <c>Shell_NotifyIcon</c> rather than WinForms' NotifyIcon, which would mean turning
/// <c>UseWindowsForms</c> on for one control and dragging that framework into every build of a WPF app. The
/// surface here is four calls wide, and the file already sits beside <see cref="NotificationService"/>, which
/// reaches for the same layer.
///
/// Every entry point is failure-tolerant and <see cref="TryShow"/> REPORTS its failure rather than swallowing it:
/// the caller hides its window on the strength of this icon appearing, so an icon that never installed has to
/// come back as false. Hiding the only window behind an icon that is not there is how an app becomes
/// unreachable - visible in Task Manager, holding the user's work, with no way to open or end it.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Action _onOpen;
    private readonly Action _onQuit;
    private HwndSource? _window;
    private nint _icon;
    private bool _ownsIcon;          // extracted from the exe (must be destroyed) vs the shared system fallback
    private string _tooltip = "VibeCode";
    private bool _added;
    private bool _disposed;

    /// <summary>Explorer restarting takes every notification icon with it and then broadcasts this. Without the
    /// re-add, one Explorer crash would leave a backgrounded VibeCode with no icon and no window.</summary>
    private readonly uint _taskbarCreated;

    public TrayIcon(Action onOpen, Action onQuit)
    {
        _onOpen = onOpen;
        _onQuit = onQuit;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>True while the icon is actually in the notification area.</summary>
    public bool IsVisible => _added;

    /// <summary>Put the icon up. Returns false if anything at all went wrong - the caller must then close
    /// normally instead of hiding behind an icon that does not exist.</summary>
    public bool TryShow(string tooltip)
    {
        if (_disposed) return false;
        try
        {
            _tooltip = Clamp(tooltip);
            if (!EnsureWindow() || !EnsureIcon()) return false;
            if (_added) { Update(); return true; }

            var data = Describe(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _added = Shell_NotifyIcon(NIM_ADD, ref data);
            return _added;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TrayIcon.TryShow");
            return false;
        }
    }

    /// <summary>Retitle the icon in place - the tooltip carries how many agents are working, so it changes while
    /// the window is away. A no-op when the icon is down.</summary>
    public void UpdateTooltip(string tooltip)
    {
        var clamped = Clamp(tooltip);
        if (!_added || string.Equals(clamped, _tooltip, StringComparison.Ordinal)) return;
        _tooltip = clamped;
        Update();
    }

    /// <summary>The one-time "your window went here" balloon. Best-effort by design: Windows may present it as a
    /// toast, or suppress it entirely under focus assist, and none of that is worth an error path.</summary>
    public void ShowNotice(string title, string body)
    {
        if (!_added) return;
        try
        {
            var data = Describe(NIF_INFO);
            data.szInfoTitle = Clamp(title, 63);
            data.szInfo = Clamp(body, 255);
            data.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* the balloon is a courtesy; the icon is the contract */ }
    }

    /// <summary>Take the icon down (the window is back), keeping the helper window for the next time.</summary>
    public void Hide()
    {
        if (!_added) return;
        _added = false;
        try
        {
            var data = Describe(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch { /* leaving a ghost icon behind is bad, but throwing on the way back in is worse */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        try { _window?.Dispose(); } catch { /* already torn down with the dispatcher */ }
        _window = null;
        if (_ownsIcon && _icon != nint.Zero) { try { DestroyIcon(_icon); } catch { /* ignore */ } }
        _icon = nint.Zero;
        _ownsIcon = false;
    }

    // ---------------- plumbing ----------------

    private void Update()
    {
        try
        {
            var data = Describe(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* a stale tooltip is not worth an exception */ }
    }

    private NOTIFYICONDATA Describe(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window?.Handle ?? nint.Zero,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>A hidden top-level window purely to receive the icon's callbacks. Deliberately NOT message-only
    /// (<c>HWND_MESSAGE</c>): such a window can never be foreground, and a tray context menu whose owner cannot
    /// take the foreground stays on screen after the user clicks away from it.</summary>
    private bool EnsureWindow()
    {
        if (_window is { IsDisposed: false }) return true;
        // Zero-sized, WS_POPUP without WS_VISIBLE (top-level but never shown) and WS_EX_TOOLWINDOW (never in alt-tab).
        _window = new HwndSource(0, unchecked((int)WS_POPUP), WS_EX_TOOLWINDOW, 0, 0, "VibeCode.Tray", nint.Zero);
        _window.AddHook(OnMessage);
        return _window.Handle != nint.Zero;
    }

    /// <summary>The app's own icon at the size the notification area actually asks for, taken from the running
    /// executable so it always matches the build. <c>PrivateExtractIcons</c> picks the best frame in the icon
    /// group for that size, which <c>ExtractIcon</c> cannot do; the system icon is the last-resort fallback so a
    /// failure here still leaves the user something to click.</summary>
    private bool EnsureIcon()
    {
        if (_icon != nint.Zero) return true;
        var width = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        var height = Math.Max(16, GetSystemMetrics(SM_CYSMICON));
        if (Environment.ProcessPath is { Length: > 0 } exe)
        {
            try
            {
                var handles = new nint[1];
                var ids = new int[1];
                if (PrivateExtractIcons(exe, 0, width, height, handles, ids, 1, 0) > 0 && handles[0] != nint.Zero)
                {
                    _icon = handles[0];
                    _ownsIcon = true;
                    return true;
                }
            }
            catch { /* fall through to the shared system icon */ }
        }
        _icon = LoadIcon(nint.Zero, IDI_APPLICATION);
        _ownsIcon = false;   // shared: destroying it is not ours to do
        return _icon != nint.Zero;
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && message == _taskbarCreated && _added)
        {
            // Explorer came back and forgot us. Re-add rather than assume: the previous icon no longer exists.
            _added = false;
            TryShow(_tooltip);
            return nint.Zero;
        }

        if (message != WM_TRAY) return nint.Zero;
        handled = true;
        switch ((int)lParam)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case NIN_BALLOONUSERCLICK:
                Invoke(_onOpen);
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu(hwnd);
                break;
        }
        return nint.Zero;
    }

    private void ShowMenu(nint hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, MenuOpen, "Open VibeCode");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuQuit, "Quit VibeCode");
            SetMenuDefaultItem(menu, MenuOpen, byPosition: false);

            GetCursorPos(out var point);
            // Required by the shell: the menu only dismisses on a click elsewhere if its owner is foreground,
            // and the trailing WM_NULL flushes the case where it is dismissed without a selection.
            SetForegroundWindow(hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X, point.Y, hwnd, nint.Zero);
            PostMessage(hwnd, WM_NULL, nint.Zero, nint.Zero);

            if (chosen == MenuOpen) Invoke(_onOpen);
            else if (chosen == MenuQuit) Invoke(_onQuit);
        }
        catch (Exception ex) { CrashLog.Write(ex, "TrayIcon.ShowMenu"); }
        finally { DestroyMenu(menu); }
    }

    /// <summary>Hand the click back to the app OUTSIDE this window procedure. Showing or tearing down windows from
    /// inside a tray callback runs a nested message loop in the middle of one, and quitting from in there would
    /// dispose the very window whose procedure is still on the stack.</summary>
    private static void Invoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { try { action(); } catch { /* ignore */ } return; }
        dispatcher.BeginInvoke(new Action(() =>
        {
            try { action(); }
            catch (Exception ex) { CrashLog.Write(ex, "TrayIcon.Invoke"); }
        }));
    }

    /// <summary>Classic <c>NOTIFYICONDATA</c> fields are fixed-length: an over-long string is not truncated by the
    /// shell, it fails the marshal outright.</summary>
    private static string Clamp(string? text, int max = 63)
    {
        text = text?.Replace('\r', ' ').Replace('\n', ' ') ?? "";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    // ---------------- Win32 ----------------

    private const int IconId = 1;
    private const int MenuOpen = 1;
    private const int MenuQuit = 2;

    private const int WM_TRAY = 0x8000 + 1;   // WM_APP + 1
    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private const int NIM_ADD = 0x0, NIM_MODIFY = 0x1, NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x1;

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private static readonly nint IDI_APPLICATION = 32512;

    private const int MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const int TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
        nint[] icons, int[] iconIds, int count, int flags);

    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(nint menu, int flags, int id, string? item);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(nint menu, int item, bool byPosition);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, int flags, int x, int y,
        nint hwnd, nint parameters);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
