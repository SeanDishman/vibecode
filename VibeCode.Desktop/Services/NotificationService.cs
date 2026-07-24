using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VibeCode.UI;

namespace VibeCode.Services;

/// <summary>
/// Desktop toasts for "a chat finished" and "an agent is waiting on you" (both opt-in, off by default).
/// A toast is raised only when the chat it concerns is NOT actually in front of the user: if any VibeCode shell
/// window currently renders that chat AND that window is fully visible on screen (not minimized, not on another
/// virtual desktop, and no other app's window overlapping it - Discord over VibeCode still notifies), the event
/// is considered seen and stays silent. Toasts are topmost so they appear over whatever covered the app, never
/// steal focus, and clicking one brings the shell up and selects the chat (re-entering its bridge if needed).
/// When a toast is shown it plays whichever chime the user picked in Settings — see <see cref="NotificationSounds"/>.
/// </summary>
public static class NotificationService
{
    /// <summary>Called after every terminal result envelope. Bridge panes announce under their agent label.</summary>
    public static void NotifyTurnFinished(ChatViewModel chat, bool isError)
    {
        if (!AppSettings.Current.NotifyOnTurnEnd) return;
        if (chat.HasQueued) return;   // queued prompts auto-send right now - the agent never stopped for the user
        if (IsChatVisibleToUser(chat)) return;

        // Bridge panes announce as their agent ("Claude 2"); a plain chat announces by its own title so the
        // user knows WHICH chat finished when several are running.
        var name = chat.IsBridgeAgent ? chat.BridgeLabel : Snippet(chat.Title, 46);
        var title = string.IsNullOrEmpty(name)
            ? isError ? "Chat hit an error" : "Chat finished"
            : $"{name} — {(isError ? "hit an error" : "finished")}";
        var body = isError
            ? "The turn stopped with an error."
            : Snippet(chat.LastTurnReplyText()) is { Length: > 0 } reply ? reply : "The turn is done.";
        Show(chat, ToastKind.Finished, title, body, isError);
    }

    /// <summary>Called when a question / plan-review / permission card lands and the provider is now blocked on the user.</summary>
    public static void NotifyAwaitingUser(ChatViewModel chat, PermItem item)
    {
        if (!AppSettings.Current.NotifyOnAwaitingInput) return;
        if (IsChatVisibleToUser(chat)) return;

        var who = chat.IsBridgeAgent ? chat.BridgeLabel : chat.AgentDisplay;
        var (title, body) = item.Kind switch
        {
            "question" => ($"{who} asked you a question",
                Snippet(item.Questions.FirstOrDefault()?.Question) is { Length: > 0 } q ? q : "Waiting on your answer."),
            "plan" => ($"{who} has a plan ready", "Waiting on your review to start the work."),
            _ => ($"{who} needs permission", Snippet(FirstNonEmpty(item.Summary, item.BodyText, item.ToolName)) is { Length: > 0 } p
                ? $"{item.ToolName}: {p}"
                : $"Waiting on approval to use {item.ToolName}."),
        };
        Show(chat, ToastKind.Waiting, title, body + WithChatName(chat), isError: false);
    }

    /// <summary>" — <chat title>" for plain chats, so a waiting toast names the chat it belongs to; bridge panes
    /// already carry their agent label in the title.</summary>
    private static string WithChatName(ChatViewModel chat) =>
        !chat.IsBridgeAgent && Snippet(chat.Title, 60) is { Length: > 0 } t ? $" — {t}" : "";

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>One line, hard-capped, so a long reply or command never turns the toast into a wall of text.</summary>
    private static string Snippet(string? text, int max = 140)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > max) break;
        }
        return sb.Length > max ? sb.ToString(0, max).TrimEnd() + "…" : sb.ToString();
    }

    // ---------------- "is the chat already in front of the user?" ----------------

    private static bool IsChatVisibleToUser(ChatViewModel chat)
    {
        try
        {
            foreach (Window window in Application.Current.Windows)
                if (window is MainWindow shell && shell.DisplaysChat(chat) && IsWindowFullyVisible(window))
                    return true;
        }
        catch { /* enumeration raced a closing window - treat as not visible and notify */ }
        return false;
    }

    /// <summary>True when the window is on-screen with no other APP's window overlapping any part of it. Our own
    /// popups/toasts/dialogs don't count as cover - the user is still inside VibeCode when those are up.</summary>
    private static bool IsWindowFullyVisible(Window window)
    {
        if (window is not { IsLoaded: true, IsVisible: true } || window.WindowState == WindowState.Minimized) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) return false;
        if (!TryGetVisibleBounds(hwnd, out var mine)) return false;

        var ourPid = Environment.ProcessId;
        // Walk every window ABOVE ours in z-order; the first real overlap from another process means covered.
        for (var above = GetWindow(hwnd, GW_HWNDPREV); above != 0; above = GetWindow(above, GW_HWNDPREV))
        {
            if (!IsWindowVisible(above) || IsIconic(above) || IsCloaked(above)) continue;
            GetWindowThreadProcessId(above, out var pid);
            if (pid == ourPid) continue;
            var ex = GetWindowLong(above, GWL_EXSTYLE);
            if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) continue;   // click-through overlay, not cover
            if (!TryGetVisibleBounds(above, out var theirs)) continue;
            // A couple of px of tolerance: extended-frame rounding on snapped neighbours must not read as "covered".
            if (Math.Min(mine.Right, theirs.Right) - Math.Max(mine.Left, theirs.Left) > 2
                && Math.Min(mine.Bottom, theirs.Bottom) - Math.Max(mine.Top, theirs.Top) > 2)
                return false;
        }
        return true;
    }

    /// <summary>The frame the user actually sees. GetWindowRect includes the invisible resize border, which would
    /// make two cleanly snapped windows look overlapped; DWM's extended frame bounds exclude it.</summary>
    private static bool TryGetVisibleBounds(nint hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hwnd, out rect))
        {
            rect = default;
            return false;
        }
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ---------------- the toast surface ----------------

    private enum ToastKind { Finished, Waiting }

    private static readonly List<ToastWindow> Open = new();
    private const int MaxOpenToasts = 4;

    private static void Show(ChatViewModel chat, ToastKind kind, string title, string body, bool isError)
    {
        // Raised from provider event handlers that are already marshalled to the UI thread, but stay defensive:
        // a toast created on a worker thread would crash the whole app, not just skip a notification.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => Show(chat, kind, title, body, isError)); return; }

        try
        {
            // Same chat + same kind again (e.g. several permission cards in one turn): replace, don't stack dupes.
            foreach (var stale in Open.Where(t => ReferenceEquals(t.Chat, chat) && t.Kind == kind).ToList())
                stale.Dismiss(immediate: true);
            while (Open.Count >= MaxOpenToasts) Open[0].Dismiss(immediate: true);

            var toast = new ToastWindow(chat, kind, title, body, isError);
            Open.Add(toast);
            toast.Show();
            Reflow();
            PlayToastSound();
        }
        catch { /* notifications are best-effort chrome - never take the app down over one */ }
    }

    // ---------------- notification sound ----------------

    /// <summary>
    /// Plays whichever chime the user picked in Settings, off the UI thread. The catalogue, the stacking guard
    /// and the actual playback all live in <see cref="NotificationSounds"/> so the settings preview button and a
    /// real toast go through exactly the same path — a preview is never a different sound to the real thing.
    /// </summary>
    private static void PlayToastSound() =>
        NotificationSounds.Play(AppSettings.Current.NotificationSound);

    private static void Remove(ToastWindow toast)
    {
        Open.Remove(toast);
        Reflow();
    }

    /// <summary>Newest toast sits in the bottom-right corner of the work area; older ones stack upward.</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 12;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var toast = Open[i];
            toast.UpdateLayout();
            toast.Left = area.Right - toast.ActualWidth - 12;
            toast.Top = bottom - toast.ActualHeight;
            bottom = toast.Top - 10;
        }
    }

    private static void ActivateChat(ChatViewModel chat)
    {
        if (Application.Current?.MainWindow is MainWindow shell) shell.NavigateToChat(chat);
    }

    private static object? Res(string key) => Application.Current?.TryFindResource(key);
    private static Brush BrushRes(string key, string fallback) =>
        Res(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private sealed class ToastWindow : Window
    {
        public ChatViewModel Chat { get; }
        public ToastKind Kind { get; }
        private readonly DispatcherTimer _dismissTimer;
        private bool _closing;

        public ToastWindow(ChatViewModel chat, ToastKind kind, string title, string body, bool isError)
        {
            Chat = chat;
            Kind = kind;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;                    // must surface over whatever is covering the app
            ShowActivated = false;             // never yank focus from what the user is doing
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = 356;
            Cursor = Cursors.Hand;

            var icons = Res("Icons") as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var accent = isError ? new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x6B)) : BrushRes("Accent", "#4C8DF5");

            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushRes("Text", "#ECEDF1"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 18, 0),   // keep clear of the close glyph
            };
            var bodyText = new TextBlock
            {
                Text = body,
                FontSize = 11.5,
                Foreground = BrushRes("Muted", "#9EA0A8"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 48,
                Margin = new Thickness(0, 3, 0, 0),
            };
            var glyph = new TextBlock
            {
                Text = isError ? "" : kind == ToastKind.Waiting ? "" : "",   // error badge | chat bubbles | completed
                FontFamily = icons,
                FontSize = 15,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var iconHost = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = BrushRes("AccentSoft", "#244C8DF5"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 0),
                Child = glyph,
            };
            var close = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = icons, FontSize = 10 },
                Foreground = BrushRes("Faint", "#6C6E77"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Arrow,
                Focusable = false,
            };
            close.Click += (_, e) => { e.Handled = true; Dismiss(immediate: false); };

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(titleText);
            textStack.Children.Add(bodyText);
            var row = new DockPanel();
            DockPanel.SetDock(iconHost, Dock.Left);
            row.Children.Add(iconHost);
            row.Children.Add(textStack);

            var host = new Grid();
            host.Children.Add(row);
            host.Children.Add(close);

            var card = new Border
            {
                CornerRadius = (CornerRadius?)Res("Rad12") ?? new CornerRadius(12),
                Background = BrushRes("Bg2", "#F51B1C22"),
                BorderBrush = BrushRes("BorderSoft", "#2C2D32"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 12, 12),
                Child = host,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black,
                },
            };
            Content = card;
            Margin = new Thickness(0);

            MouseLeftButtonUp += (_, _) =>
            {
                if (_closing) return;
                Dismiss(immediate: true);
                ActivateChat(Chat);
            };

            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _dismissTimer.Tick += (_, _) => Dismiss(immediate: false);
            _dismissTimer.Start();
            MouseEnter += (_, _) => _dismissTimer.Stop();          // reading it - don't yank it away
            MouseLeave += (_, _) => { if (!_closing) _dismissTimer.Start(); };

            SourceInitialized += (_, _) =>
            {
                // Tool window + no-activate: never in alt-tab, and even the click that dismisses it doesn't
                // deactivate the foreground app until we explicitly bring the shell up.
                var handle = new WindowInteropHelper(this).Handle;
                _ = SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
            Closed += (_, _) => { _dismissTimer.Stop(); Remove(this); };

            Opacity = 0;
            Loaded += (_, _) => BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        public void Dismiss(bool immediate)
        {
            if (_closing) return;
            _closing = true;
            _dismissTimer.Stop();
            if (immediate) { Close(); return; }
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------------- Win32 ----------------

    private const int GW_HWNDPREV = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOACTIVATE = 0x8000000;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);
}
