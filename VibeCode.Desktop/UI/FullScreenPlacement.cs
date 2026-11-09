using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Real full screen for one window: the whole display, taskbar included.
///
/// Maximizing is not full screen. A maximized window - WindowChrome or not - fills the display's WORK AREA, which
/// stops at the taskbar, so a wall that "went full screen" kept the taskbar along its bottom edge. This puts the
/// window in the normal state at the display's full pixel bounds instead, with no resize border or caption, the
/// same geometry used by the full-screen telemetry window.
///
/// On a display VibeCode is not on, the window also stays above the taskbar while another window has focus: the
/// shell raises its taskbars again whenever focus leaves a full-screen window, so a passive wall on a second
/// display would otherwise lose its bottom edge the moment the user clicked back into the app. On the app's own
/// display it is never held on top - that would bury the app under the wall.
/// </summary>
public sealed class FullScreenPlacement
{
    private readonly Window _window;
    private readonly DispatcherTimer _raise;
    private Rect _restoreBounds = Rect.Empty;
    private MonitorWorkArea _restorePixels;
    private ResizeMode _restoreResizeMode;
    private WindowStyle _restoreStyle;
    private WindowChrome? _restoreChrome;
    private bool _restoreTopmost;
    private bool _aboveTaskbar;

    public FullScreenPlacement(Window window)
    {
        _window = window;
        // After the shell has re-raised its taskbar, not before: it does so in response to the same activation change.
        _raise = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        _raise.Tick += (_, _) =>
        {
            _raise.Stop();
            if (IsActive && _aboveTaskbar && !Hidden && _window.IsVisible) DisplayMonitorService.RaiseTopmost(_window);
        };
        window.Deactivated += (_, _) => { if (IsActive && _aboveTaskbar) _raise.Start(); };
        window.Closed += (_, _) => _raise.Stop();
    }

    private static bool Hidden => Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1";

    public bool IsActive { get; private set; }

    /// <summary>Whether this window is held above the taskbar while full screen.</summary>
    public bool AboveTaskbar => IsActive && _aboveTaskbar;

    /// <summary>Raised after entering or leaving full screen, so a window can swap its button glyph and frame.</summary>
    public event Action? Changed;

    /// <summary>True when <paramref name="window"/> sits on a different display from <paramref name="app"/>.</summary>
    public static bool OnOtherDisplay(Window window, Window? app)
    {
        if (app is null) return false;
        var mine = DisplayMonitorService.MonitorFor(window);
        return mine != nint.Zero && mine != DisplayMonitorService.MonitorFor(app);
    }

    /// <summary>Cover the display the window is on now. Calling it again re-fits the window to that display, which
    /// is what a display change or a move to another display needs.</summary>
    public void Enter(bool aboveTaskbar)
    {
        // Read the display before leaving the maximized state: restoring first can move the window elsewhere.
        var bounds = DisplayMonitorService.FullBoundsFor(_window);
        // Off-screen verification renders the same full display dimensions past the right edge, never on the desktop.
        if (Hidden) bounds = new(bounds.Handle, 6200, 200, 6200 + bounds.Width, 200 + bounds.Height);
        if (!IsActive)
        {
            var state = _window.WindowState;
            _restoreBounds = state == WindowState.Normal ? Rect.Empty : _window.RestoreBounds;
            _restorePixels = state == WindowState.Normal ? DisplayMonitorService.WindowPixelBounds(_window) : default;
            _restoreResizeMode = _window.ResizeMode;
            _restoreStyle = _window.WindowStyle;
            _restoreChrome = WindowChrome.GetWindowChrome(_window);
            _restoreTopmost = _window.Topmost;
        }
        IsActive = true;
        _aboveTaskbar = aboveTaskbar;
        _window.WindowState = WindowState.Normal;
        // No caption and no resize border, or the shell and DWM treat it as an ordinary window that happens to be
        // large: Windows 11 rounds its corners, and a drag on the header would pull it off the display.
        _window.WindowStyle = WindowStyle.None;
        _window.ResizeMode = ResizeMode.NoResize;
        if (_restoreChrome is not null)
            WindowChrome.SetWindowChrome(_window, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
        _window.Topmost = (aboveTaskbar && !Hidden) || _restoreTopmost;
        try { DisplayMonitorService.SetPixelBounds(_window, bounds); }
        catch (Win32Exception) { Exit(); throw; }
        Changed?.Invoke();
    }

    /// <summary>Back to an ordinary window at the size and place it had before.</summary>
    public void Exit()
    {
        if (!IsActive) return;
        IsActive = false;
        _aboveTaskbar = false;
        _raise.Stop();
        _window.ResizeMode = _restoreResizeMode;
        _window.WindowStyle = _restoreStyle;
        if (_restoreChrome is not null) WindowChrome.SetWindowChrome(_window, _restoreChrome);
        _window.Topmost = _restoreTopmost;
        try
        {
            if (!_restoreBounds.IsEmpty)
            {
                // Entered from maximized: come back as the window it would have restored to, not as a second maximize.
                _window.Left = _restoreBounds.Left; _window.Top = _restoreBounds.Top;
                _window.Width = _restoreBounds.Width; _window.Height = _restoreBounds.Height;
            }
            else if (_restorePixels.Width > 0) DisplayMonitorService.SetPixelBounds(_window, _restorePixels);
        }
        catch (Win32Exception) { /* the old display is gone; the window stays where it is, as a normal window */ }
        Changed?.Invoke();
    }

    /// <summary>F11: in or out, holding the window above the taskbar only off the app's own display.</summary>
    public void Toggle(Window? app)
    {
        if (IsActive) Exit();
        else Enter(OnOtherDisplay(_window, app));
    }
}
