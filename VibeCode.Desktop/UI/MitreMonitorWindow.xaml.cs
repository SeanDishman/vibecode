using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Shell;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class MitreMonitorWindow : Window
{
    private sealed class Placement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Topmost { get; set; } = true;
    }
    private static string PlacementPath => Path.Combine(AppSettings.Dir, "mitre-monitor-window.json");
    private static bool Hidden => Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1";
    private bool _fullScreen;
    private MonitorWorkArea _restorePixels;
    private Rect _restorePlacement;
    private WindowState _restoreState;
    private ResizeMode _restoreResizeMode;
    private WindowChrome? _restoreChrome;

    internal MitreMonitorWindow(MitreMonitorViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        PinToggle.IsChecked = !Hidden;
        if (!Hidden) RestorePlacement();
        UpdateCardWidth();
    }

    internal void PlaceForOwner(Window owner)
    {
        if (Hidden)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 6200;
            Top = 200;
            ShowActivated = false;
            return;
        }
        if (!AppSettings.Current.TelemetryOnCompanionDisplay) return;
        try
        {
            if (!DisplayMonitorService.TryGetLeftCompanionWorkArea(owner, out var target)) return;
            // Preserve an explicit placement on a different display, just like the spending HUD.
            if (WindowStartupLocation == WindowStartupLocation.Manual
                && DisplayMonitorService.MonitorFor(this, ensureHandle: true) != DisplayMonitorService.MonitorFor(owner)) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            DisplayMonitorService.CentreOnMonitor(this, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnPinChanged(object sender, RoutedEventArgs e) => Topmost = !Hidden && (_fullScreen || PinToggle.IsChecked == true);
    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (_fullScreen) { RestoreFromFullScreen(); return; }
        var bounds = DisplayMonitorService.FullBoundsFor(this);
        // Verification renders the same full display dimensions off-screen.
        if (Hidden) bounds = new(bounds.Handle, 6200, 200, 6200 + bounds.Width, 200 + bounds.Height);
        _restorePixels = DisplayMonitorService.WindowPixelBounds(this);
        _restoreState = WindowState;
        _restorePlacement = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _restoreResizeMode = ResizeMode;
        _restoreChrome = WindowChrome.GetWindowChrome(this);
        _fullScreen = true;
        WindowState = WindowState.Normal;
        ResizeMode = ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0) });
        WindowFrame.BorderThickness = new Thickness(0);
        Topmost = !Hidden;
        try { DisplayMonitorService.SetPixelBounds(this, bounds); }
        catch { RestoreFromFullScreen(); throw; }
        FullscreenButton.Content = "\uE73F";
        FullscreenButton.ToolTip = "Restore window (Esc or F11)";
        UpdateCardWidth();
    }

    private void RestoreFromFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;
        ResizeMode = _restoreResizeMode;
        WindowChrome.SetWindowChrome(this, _restoreChrome);
        WindowFrame.BorderThickness = new Thickness(1);
        Topmost = !Hidden && PinToggle.IsChecked == true;
        if (_restoreState == WindowState.Maximized && !_restorePlacement.IsEmpty)
        {
            Left = _restorePlacement.Left; Top = _restorePlacement.Top;
            Width = _restorePlacement.Width; Height = _restorePlacement.Height;
        }
        else DisplayMonitorService.SetPixelBounds(this, _restorePixels);
        WindowState = _restoreState;
        FullscreenButton.Content = "\uE740";
        FullscreenButton.ToolTip = "Full screen (F11)";
        UpdateCardWidth();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullScreen) { RestoreFromFullScreen(); e.Handled = true; }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();
    private void OnPreviousPage(object sender, RoutedEventArgs e)
    {
        if (DataContext is MitreMonitorViewModel model) model.PreviousPage();
        AgentScroll.ScrollToTop();
    }
    private void OnNextPage(object sender, RoutedEventArgs e)
    {
        if (DataContext is MitreMonitorViewModel model) model.NextPage();
        AgentScroll.ScrollToTop();
    }
    private void UpdateCardWidth()
    {
        if (DataContext is not MitreMonitorViewModel model) return;
        model.FullScreen = _fullScreen;
        AgentScroll.VerticalScrollBarVisibility = _fullScreen ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        var viewportWidth = CardsViewport.ActualWidth > 0 ? CardsViewport.ActualWidth
            : Math.Min(model.ContentMaxWidth, (ActualWidth > 0 ? ActualWidth : Width) - 50);
        // Reserve scroll bar space only in windowed mode. Full screen always has exactly two columns and rows.
        var available = Math.Max(200, viewportWidth - (_fullScreen ? 2 : 20));
        model.CardWidth = Math.Floor(available / (_fullScreen || available >= 1260 ? 2 : 1)) - 16;
        model.CardHeight = _fullScreen ? Math.Max(100, Math.Floor(CardsViewport.ActualHeight / 2) - 16) : double.NaN;
    }

    private void OnSourceLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(AppSettings.Dir, "mitre-monitor");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, "The event log folder could not be opened.", "MITRE monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementPath)) return;
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(PlacementPath));
            if (saved is null || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top)
                || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)
                || saved.Width < MinWidth || saved.Height < MinHeight) return;
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!desktop.IntersectsWith(new Rect(saved.Left, saved.Top, saved.Width, saved.Height))) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = Math.Min(saved.Width, desktop.Width);
            Height = Math.Min(saved.Height, desktop.Height);
            PinToggle.IsChecked = saved.Topmost;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Hidden) return;
        try
        {
            var bounds = _fullScreen ? _restorePlacement
                : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty) return;
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new Placement
            {
                Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
                Topmost = PinToggle.IsChecked == true,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
