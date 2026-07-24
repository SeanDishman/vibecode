using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Native weather maps: the animated NWS reflectivity mosaic and SPC Day 1–3 categorical outlooks share one dark
/// basemap and the user's saved-location marker. One window at a time - clicking the titlebar chip again brings the
/// existing one forward rather than stacking copies.
/// </summary>
public partial class RadarWindow : Window
{
    private const double PlaceZoom = 7.0;        // close enough to read a metro area, wide enough to see weather coming
    private const double CountryZoom = 4.2;      // the whole CONUS mosaic, used when no place is saved

    private static RadarWindow? _open;

    /// <summary>Show the radar, or focus it if it is already up.</summary>
    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var w = new RadarWindow { Owner = owner };
        // Off-screen verification runs the whole app past the right edge of every monitor; a CenterOwner child would
        // otherwise land on the user's real desktop.
        if (Hidden)
        { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = 6300; w.Top = 240; }
        else ApplySavedBounds(w);
        _open = w;
        w.Show();
    }

    private static bool Hidden => Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1";

    /// <summary>Reopen where the user last left it - but only if those bounds still land on a monitor that exists.</summary>
    private static void ApplySavedBounds(RadarWindow w)
    {
        var s = AppSettings.Current;
        if (s.RadarWidth is { } width && s.RadarHeight is { } height &&
            width >= w.MinWidth && height >= w.MinHeight)
        {
            w.Width = width;
            w.Height = height;
        }

        if (s.RadarLeft is not { } left || s.RadarTop is not { } top) return;

        // Keep at least a corner of the titlebar reachable; a since-unplugged monitor would otherwise strand it.
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        if (left + 120 < vl || top + 40 < vt || left > vr - 120 || top > vb - 40) return;

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = left;
        w.Top = top;
    }

    private void SaveBounds()
    {
        if (Hidden) return;                      // a test instance must not move the user's real radar window
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)) return;

        var s = AppSettings.Current;
        s.RadarLeft = bounds.X;
        s.RadarTop = bounds.Y;
        s.RadarWidth = bounds.Width;
        s.RadarHeight = bounds.Height;
        s.Save();
    }

    private readonly WeatherService _weather = WeatherService.Instance;
    private readonly SpcOutlookService _spc = SpcOutlookService.Instance;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private List<string> _frames = new();
    private bool _loadingFrames;                 // suppresses the ValueChanged handler while the timeline is rebuilt
    private CancellationTokenSource? _spcLoad;
    private SpcOutlook? _currentSpc;
    private bool _spcMode;
    private int _spcDay = 1;

    public RadarWindow()
    {
        InitializeComponent();

        if (TryFindResource("WeatherBlue") is Brush accent) Map.MarkerBrush = accent;
        Map.RadarOpacity = OpacitySlider.Value / 100.0;

        _anim.Tick += OnAnimTick;
        _weather.LocationChanged += OnLocationChanged;
        _weather.PropertyChanged += OnWeatherChanged;

        ApplyLocation(recentre: true);
        ApplyConditions();

        _ = _weather.RefreshAsync();
        _ = LoadFramesAsync();
    }

    // ---------- location + conditions ----------

    private void ApplyLocation(bool recentre)
    {
        var has = _weather.HasLocation;
        TitlePlace.Text = _spcMode
            ? $"Day {_spcDay} · {(has ? _weather.Place : "United States")}"
            : has ? _weather.Place : "United States";
        NoPlaceHint.Visibility = !has && !_spcMode ? Visibility.Visible : Visibility.Collapsed;
        NoPlaceText.Text = "No location set — pick one in Settings › Extensions to centre the map and see conditions.";
        RecenterBtn.ToolTip = _spcMode ? "Show the national SPC outlook"
            : has ? "Centre on your saved location" : "Centre on the continental US";
        RecenterText.Text = _spcMode ? "National" : "Recentre";

        Map.MarkerLat = has ? _weather.Lat : null;
        Map.MarkerLon = has ? _weather.Lon : null;
        Map.MarkerLabel = has ? _weather.Place : "";

        if (recentre) Recentre();
        if (_spcMode) UpdateLocalSpcRisk();
    }

    private void Recentre()
    {
        if (_spcMode) Map.CenterOn(39.5, -98.35, CountryZoom);
        else if (_weather is { HasLocation: true, Lat: { } lat, Lon: { } lon }) Map.CenterOn(lat, lon, PlaceZoom);
        else Map.CenterOn(39.5, -98.35, CountryZoom);
    }

    private void ApplyConditions()
    {
        if (_spcMode)
        {
            ApplySpcTitle();
            return;
        }
        var parts = new List<string>();
        if (_weather.HasLocation && _weather.TempText != "--") parts.Add(_weather.TempText);
        if (!string.IsNullOrEmpty(_weather.Conditions)) parts.Add(_weather.Conditions);
        if (_weather.HasError) parts.Add(_weather.Error);
        TitleConditions.Text = string.Join("  ·  ", parts);
        TitleGlyph.Text = _weather.ConditionGlyph;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        ApplyLocation(recentre: true);
        if (!_spcMode) _ = LoadFramesAsync();
    }

    private void OnWeatherChanged(object? sender, PropertyChangedEventArgs e) => ApplyConditions();

    // ---------- radar frames ----------

    private async Task LoadFramesAsync()
    {
        var frames = await WeatherService.RadarFrameTimesAsync();

        _loadingFrames = true;
        try
        {
            _frames = frames;
            Timeline.Maximum = Math.Max(0, frames.Count - 1);
            Timeline.Value = Timeline.Maximum;            // land on the newest frame
            Timeline.IsEnabled = frames.Count > 1;
            PlayBtn.IsEnabled = frames.Count > 1;
        }
        finally { _loadingFrames = false; }

        ShowFrame((int)Timeline.Value);
        if (_frames.Count > 1 && !_spcMode) StartLoop();
        else StopLoop();
    }

    /// <summary>Points the map at one frame. An empty frame list means "whatever the server serves now".</summary>
    private void ShowFrame(int index)
    {
        var iso = index >= 0 && index < _frames.Count ? _frames[index] : null;
        Map.FrameTime = iso;
        FrameLabel.Text = _frames.Count == 0 ? "Latest"
            : index == _frames.Count - 1 ? "Latest"
            : WeatherService.FrameLabel(iso);
    }

    private void OnTimelineChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingFrames) return;
        ShowFrame((int)Math.Round(e.NewValue));
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_frames.Count < 2) { StopLoop(); return; }
        var next = (int)Math.Round(Timeline.Value) + 1;
        Timeline.Value = next > Timeline.Maximum ? 0 : next;   // ValueChanged draws the frame
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_anim.IsEnabled) StopLoop();
        else StartLoop();
    }

    private void StartLoop()
    {
        if (_frames.Count < 2) { StopLoop(); return; }
        _anim.Start();
        PlayBtn.Content = "\uE769";                  // pause
        PlayBtn.ToolTip = "Pause the radar loop";
        AutomationProperties.SetName(PlayBtn, "Pause radar loop");
    }

    private void StopLoop()
    {
        _anim.Stop();
        PlayBtn.Content = "\uE768";                  // play
        PlayBtn.ToolTip = "Play the last hour of radar";
        AutomationProperties.SetName(PlayBtn, "Play radar loop");
    }

    // ---------- view controls ----------

    private void OnZoomIn(object sender, RoutedEventArgs e) => Map.ZoomBy(0.5);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Map.ZoomBy(-0.5);
    private void OnRecenter(object sender, RoutedEventArgs e) => Recentre();

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Map is not null) Map.RadarOpacity = e.NewValue / 100.0;
    }

    private void OnLabelsChanged(object sender, RoutedEventArgs e) => Map.ShowLabels = LabelsToggle.IsChecked == true;

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_spcMode)
        {
            await LoadSpcAsync(forceRefresh: true);
            return;
        }
        _ = _weather.RefreshAsync();
        _ = LoadFramesAsync();
    }

    // ---------- Storm Prediction Center outlooks ----------

    private async void OnSpcModeChanged(object sender, RoutedEventArgs e)
    {
        var enabled = SpcToggle.IsChecked == true;
        if (_spcMode == enabled) return;
        _spcMode = enabled;

        RadarAnimationPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        Timeline.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        RadarOpacityPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        SpcStatusPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SpcDayPicker.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SpcLegend.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Map.ShowRadar = !enabled;
        TitleMode.Text = enabled ? "SPC outlook" : "NWS radar";
        TitleGlyph.Text = enabled ? "\uE945" : _weather.ConditionGlyph;
        AutomationProperties.SetName(Map, enabled ? "SPC categorical outlook map" : "Radar map");

        if (enabled)
        {
            StopLoop();
            Map.SpcOutlook = _currentSpc?.Day == _spcDay ? _currentSpc : null;
            ApplyLocation(recentre: true);
            await LoadSpcAsync();
        }
        else
        {
            _spcLoad?.Cancel();
            SpcMessage.Visibility = Visibility.Collapsed;
            Map.SpcOutlook = null;
            ApplyLocation(recentre: true);
            ApplyConditions();
            if (_frames.Count > 1) StartLoop();
        }
    }

    private async void OnSpcDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value } || !int.TryParse(value, out var day)) return;
        _spcDay = Math.Clamp(day, 1, 3);
        SpcDay1.IsChecked = _spcDay == 1;
        SpcDay2.IsChecked = _spcDay == 2;
        SpcDay3.IsChecked = _spcDay == 3;
        ApplyLocation(recentre: false);
        await LoadSpcAsync();
    }

    private async Task LoadSpcAsync(bool forceRefresh = false)
    {
        var day = _spcDay;
        _spcLoad?.Cancel();
        _spcLoad?.Dispose();
        var load = _spcLoad = new CancellationTokenSource();

        SpcValidText.Text = $"Loading Day {day} official outlook…";
        SpcMessageText.Text = "Loading SPC outlook…";
        SpcMessage.Visibility = Visibility.Visible;
        if (_currentSpc?.Day != day) Map.SpcOutlook = null;
        try
        {
            var outlook = await _spc.GetAsync(day, forceRefresh, load.Token);
            if (load.IsCancellationRequested || !_spcMode || day != _spcDay) return;
            _currentSpc = outlook;
            Map.SpcOutlook = outlook;
            SpcValidText.Text = outlook.ValidWindowUtc;

            var severeAreas = outlook.Areas.Count(area => area.Rank >= 3);
            if (severeAreas == 0)
            {
                SpcMessageText.Text = $"No severe thunderstorm areas are forecast on Day {day}. General thunderstorm areas may still be shown.";
                SpcMessage.Visibility = Visibility.Visible;
            }
            else SpcMessage.Visibility = Visibility.Collapsed;

            UpdateLocalSpcRisk();
            ApplySpcTitle();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (load.IsCancellationRequested || !_spcMode || day != _spcDay) return;
            SpcValidText.Text = $"Day {day} outlook unavailable";
            SpcMessageText.Text = ex is HttpRequestException
                ? "Could not reach the Storm Prediction Center. Check your connection and try Refresh."
                : $"Could not read the SPC outlook: {ex.Message}";
            SpcMessage.Visibility = Visibility.Visible;
            UpdateLocalSpcRisk();
            ApplySpcTitle();
        }
        await CaptureSmokeScreenshotAsync(day);
    }

    /// <summary>Hidden UI verification renders through WPF itself; PrintWindow returns a black frame for off-screen GPU surfaces.</summary>
    private async Task CaptureSmokeScreenshotAsync(int day)
    {
        if (!Hidden || Environment.GetEnvironmentVariable("VIBECODE_WEATHER_MAPS_SCREENSHOT") is not { Length: > 0 } path)
            return;
        try
        {
            await Task.Delay(750);
            if (!_spcMode || day != _spcDay) return;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (Content is not FrameworkElement root || root.ActualWidth <= 0 || root.ActualHeight <= 0) return;
            root.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(root);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var output = File.Create(Path.GetFullPath(path));
            encoder.Save(output);
        }
        catch { /* screenshot verification must never affect a real weather window */ }
    }

    private void UpdateLocalSpcRisk()
    {
        if (!_spcMode) return;
        if (!_weather.HasLocation || _weather.Lat is not { } lat || _weather.Lon is not { } lon)
        {
            SpcLocalRiskText.Text = "Set a location to place your dot and see the local outlook";
            SpcRiskDot.Fill = TryFindResource("Faint") as Brush ?? Brushes.Gray;
            return;
        }

        var area = Map.SpcOutlook?.RiskAt(lat, lon);
        SpcLocalRiskText.Text = area is null
            ? $"{_weather.Place}: outside outlined thunderstorm areas"
            : $"{_weather.Place}: {area.Label}";
        SpcRiskDot.Fill = area is null ? TryFindResource("Faint") as Brush ?? Brushes.Gray : BrushOf(area.Fill);
    }

    private void ApplySpcTitle()
    {
        TitlePlace.Text = $"Day {_spcDay} · {(_weather.HasLocation ? _weather.Place : "United States")}";
        TitleConditions.Text = Map.SpcOutlook is { } outlook
            ? $"{outlook.ValidWindowUtc}{(string.IsNullOrWhiteSpace(outlook.Forecaster) ? "" : $"  ·  {outlook.Forecaster}")}"
            : "Official categorical outlook";
    }

    private static Brush BrushOf(string value)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
        catch { return Brushes.Gray; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        SaveBounds();
        _spcLoad?.Cancel();
        _spcLoad?.Dispose();
        _anim.Stop();
        _anim.Tick -= OnAnimTick;
        _weather.LocationChanged -= OnLocationChanged;
        _weather.PropertyChanged -= OnWeatherChanged;
        if (ReferenceEquals(_open, this)) _open = null;
    }
}
