using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One line of the HUD's per-model panel.</summary>
public sealed class HudModelRow
{
    public required string Display { get; init; }
    public required Brush Swatch { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// The always-on-top telemetry window - the thing you park on a second monitor and leave running.
///
/// Everything on it is the SAME rolling 24 hours, taken from one <see cref="UsageAnalytics.BuildHours"/> call:
/// the tiles are that slice summed, the graph is that slice hour by hour, the model panel is its per-model
/// split. That is the point - a dashboard whose panels quietly disagree because each one asked a slightly
/// different question is worse than no dashboard.
/// </summary>
public partial class UsageHudWindow : Window
{
    /// <summary>Where the window was last left. Kept beside the usage log rather than in settings.json so that
    /// dragging this to another monitor never touches the file the whole app three-way merges on save.</summary>
    private sealed class HudPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Topmost { get; set; } = true;
    }

    private const int WindowHours = 24;
    private static string PlacementPath => Path.Combine(AppSettings.Dir, "hud-window.json");

    private static UsageHudWindow? _open;

    /// <summary>Show the HUD, or bring the existing one forward. One at a time, like the radar window - a second
    /// copy would just be the same numbers twice.</summary>
    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        // Owned, so it goes away with the app rather than holding the process open on its own.
        var hud = new UsageHudWindow { Owner = owner };
        // Off-screen verification runs the whole app past the right edge of every monitor; a restored placement
        // would otherwise drop this onto the user's real desktop mid-run.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            hud.Topmost = false;
            hud.PinToggle.IsChecked = false;
            hud.Left = 6200;
            hud.Top = 200;
        }
        else if (AppSettings.Current.TelemetryOnCompanionDisplay)
        {
            hud.MoveToCompanionMonitor(owner);
        }
        _open = hud;
        hud.Show();
    }

    /// <summary>
    /// Park the HUD on the display VibeCode is NOT using - the whole point of this window is being watched while
    /// the app is worked in, and stacked on top of the app it can only be watched by covering the thing it is
    /// reporting on.
    ///
    /// Only when the restored placement would have landed it on the main window's own screen. A HUD already
    /// dragged onto some third display is where the user put it, and "helpfully" moving it every launch is the
    /// kind of behaviour you can only escape by finding the setting. Runs before Show, so the window appears
    /// there rather than being watched to jump.
    /// </summary>
    private void MoveToCompanionMonitor(Window? owner)
    {
        if (owner is null) return;
        try
        {
            if (!DisplayMonitorService.TryGetCompanionWorkArea(owner, out var target)) return;
            // ensureHandle: this window has not been shown, so without it there is no HWND to ask.
            var here = DisplayMonitorService.MonitorFor(this, ensureHandle: true);
            if (here != nint.Zero && here != DisplayMonitorService.MonitorFor(owner)) return;
            DisplayMonitorService.CentreOnMonitor(this, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A display unplugged between enumeration and placement is not worth taking the window down for.
        }
    }

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _hooked;
    private bool _ready;

    public UsageHudWindow()
    {
        InitializeComponent();
        RestorePlacement();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        Refresh();

        UsageLog.Instance.Changed += OnUsageChanged;
        _hooked = true;

        // The clock ticks every second; the data only rebuilds when a turn lands or the hour rolls over, so an
        // idle HUD costs a string format per second and nothing else.
        _tick.Tick += (_, _) =>
        {
            HudClock.Text = DateTime.Now.ToString("ddd dd MMM · HH:mm:ss").ToUpperInvariant();
            if ((DateTime.Now - _lastRefresh).TotalSeconds >= 60) Refresh();
        };
        _tick.Start();

        var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LiveDot.BeginAnimation(OpacityProperty, pulse);
    }

    private void OnUsageChanged() => Dispatcher.BeginInvoke(() => { if (_ready) Refresh(); });

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        if (_hooked) UsageLog.Instance.Changed -= OnUsageChanged;
        if (ReferenceEquals(_open, this)) _open = null;
        SavePlacement();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPinChanged(object sender, RoutedEventArgs e) => Topmost = PinToggle.IsChecked == true;

    /// <summary>Hand off to the full-screen wall. Owner is this window's owner rather than the HUD itself, so
    /// the wall picks the display the MAIN window is not on - parking it beside the HUD would defeat it.</summary>
    private void OnOpenWall(object sender, RoutedEventArgs e) => UsageDashboardWindow.Open(Owner ?? this);

    private void OnMetricChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        HudGraph.Metric = SelectedMetric;
        HudGraphCaption.Text = SelectedMetric switch
        {
            HudMetric.Cost => "SPEND BY HOUR",
            HudMetric.Energy => "ELECTRICITY BY HOUR",
            _ => "THROUGHPUT BY HOUR",
        };
        Refresh();
    }

    private HudMetric SelectedMetric =>
        HudByCost.IsChecked == true ? HudMetric.Cost :
        HudByEnergy.IsChecked == true ? HudMetric.Energy : HudMetric.Tokens;

    private void Refresh()
    {
        _lastRefresh = DateTime.Now;
        var hours = UsageAnalytics.BuildHours(UsageLog.Instance.Entries(), WindowHours, DateTimeOffset.Now);
        HudGraph.Hours = hours;
        HudGraph.Metric = SelectedMetric;

        double cost = 0, tokens = 0, energyWh = 0, tokensIn = 0, cacheRead = 0;
        var turns = 0;
        foreach (var h in hours)
        {
            cost += h.CostUsd;
            tokens += h.Total;
            energyWh += h.EnergyWh;
            tokensIn += h.TokensIn;
            cacheRead += h.CacheRead;
            turns += h.Turns;
        }
        var water = ModelEnergy.OnSiteWaterLitres(energyWh);

        // Rolled rather than assigned: these four are the figures read from across a room, and a number that
        // travels to its new value is the difference between noticing a change and having to re-read the tile.
        RollingNumber.Set(TileSpend, cost, UsageAnalytics.Money);
        RollingNumber.Set(TileTokens, tokens, UsageAnalytics.Tokens);
        RollingNumber.Set(TileEnergy, energyWh, UsageAnalytics.Energy);
        RollingNumber.Set(TileWater, water, UsageAnalytics.Water);

        var activeHours = hours.Count(h => h.Turns > 0);
        TileSpendSub.Text = turns == 0 ? "nothing in 24 h" : $"{turns:N0} turns · {activeHours} active hours";
        TileTokensSub.Text = tokensIn > 0
            ? $"{UsageAnalytics.Percent(cacheRead / tokensIn)} from cache"
            : "no input yet";
        TileEnergySub.Text = UsageAnalytics.EnergyEquivalent(energyWh);
        TileWaterSub.Text = UsageAnalytics.WaterEquivalent(water);

        SparkSpend.Values = hours.Select(h => h.CostUsd).ToList();
        SparkTokens.Values = hours.Select(h => h.Total).ToList();
        SparkEnergy.Values = hours.Select(h => h.EnergyWh).ToList();
        // Water is a fixed multiple of energy, so this line is the same shape as the one above it on purpose -
        // it is the water's own series, not a second reading of the same number dressed up as new information.
        SparkWater.Values = hours.Select(h => ModelEnergy.OnSiteWaterLitres(h.EnergyWh)).ToList();

        // Per-model panel, over the same 24 h, ordered by whichever metric the graph is showing.
        var metric = SelectedMetric;
        var byModel = new Dictionary<string, HourSlice>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hours)
            foreach (var (model, slice) in h.ByModel)
            {
                if (!byModel.TryGetValue(model, out var total)) byModel[model] = total = new HourSlice();
                total.Tokens += slice.Tokens;
                total.CostUsd += slice.CostUsd;
                total.EnergyWh += slice.EnergyWh;
            }

        double Measure(HourSlice s) => metric switch
        {
            HudMetric.Cost => s.CostUsd,
            HudMetric.Energy => s.EnergyWh,
            _ => s.Tokens,
        };
        string Format(HourSlice s) => metric switch
        {
            HudMetric.Cost => UsageAnalytics.Money(s.CostUsd),
            HudMetric.Energy => UsageAnalytics.Energy(s.EnergyWh),
            _ => UsageAnalytics.Tokens(s.Tokens),
        };

        var rows = byModel.OrderByDescending(kv => Measure(kv.Value))
            .Select(kv => new HudModelRow
            {
                Display = UsagePalette.DisplayName(kv.Key),
                Swatch = UsagePalette.BrushFor(kv.Key),
                Value = Format(kv.Value),
            })
            .ToList();
        HudModels.ItemsSource = rows;
        HudModels.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HudModelsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var cacheRatio = tokensIn > 0 ? cacheRead / tokensIn : 0;
        SysCache.Text = UsageAnalytics.Percent(cacheRatio);
        SysCacheMeter.Fraction = cacheRatio;
        // Averaged over hours that actually did something - spreading a burst across a night of idle would
        // report a rate this machine never ran at.
        SysBurn.Text = activeHours > 0 ? UsageAnalytics.Money(cost / activeHours) + " / active hour" : "—";
        SysTurns.Text = turns.ToString("N0");
        var peak = hours.OrderByDescending(h => h.Total).FirstOrDefault();
        SysPeak.Text = peak is null || peak.Turns == 0
            ? "—"
            : $"{peak.Hour:HH:00} · {UsageAnalytics.Tokens(peak.Total)}";
        SysNote.Text = "Electricity and water are estimates from each model's size and token counts, not meter "
                       + "readings — good to about a factor of 3. Water is on-site cooling only ("
                       + UsageAnalytics.Water(ModelEnergy.TotalWaterLitres(energyWh))
                       + " including the water used to generate the electricity).";
    }

    // ---------- placement ----------

    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementPath)) { CentreOnPrimary(); return; }
            var saved = JsonSerializer.Deserialize<HudPlacement>(File.ReadAllText(PlacementPath));
            if (saved is null || saved.Width < MinWidth || saved.Height < MinHeight) { CentreOnPrimary(); return; }

            // A monitor that has since been unplugged would otherwise put this window somewhere unreachable.
            var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var wanted = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            if (!bounds.IntersectsWith(wanted)) { CentreOnPrimary(); return; }

            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            Topmost = saved.Topmost;
            PinToggle.IsChecked = saved.Topmost;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CentreOnPrimary();
        }
    }

    private void CentreOnPrimary()
    {
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
    }

    private void SavePlacement()
    {
        try
        {
            // RestoreBounds is the pre-maximise rectangle; Left/Top are meaningless while maximised.
            var rect = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(new HudPlacement
            {
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Width,
                Height = rect.Height,
                Topmost = PinToggle.IsChecked == true,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the remembered position is not worth taking the window down for.
        }
    }
}
