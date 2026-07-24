using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The telemetry WALL: a full-screen, borderless, live dashboard meant to sit on a second monitor and be
/// glanced at, not interacted with.
///
/// It differs from <see cref="UsageHudWindow"/> in one way that matters: the HUD answers "what is happening
/// right now" at a single resolution, this answers it at three at once, because the shape of a working session
/// is only visible when you can see the same activity at more than one zoom. The range strip in the header
/// picks WHICH window all three are zoomed into - anything from the last hour to the last month - and every
/// panel below it, tickers and donut included, redraws against that one span. All of them read the SAME metric
/// from the SAME <see cref="UsageLog"/>, so they can never quietly disagree; the only thing on the wall that is
/// a rate rather than a level is the per-ticker delta, and it is labelled as such.
/// </summary>
public partial class UsageDashboardWindow : Window
{
    /// <summary>
    /// One entry on the range strip: the span it covers, and the three bucket sizes the three graphs draw that
    /// span at. Counts are derived from the span rather than written down, so the two can never fall out of step.
    ///
    /// Every size here DIVIDES A DAY, and that is a correctness rule rather than a stylistic one:
    /// <see cref="UsageAnalytics.BuildBuckets"/> anchors its grid to midnight and steps back, so a size that
    /// does not divide a day leaves the newest bucket straddling "now" and the grid reaching less far back than
    /// the caption claims - a 3-day bucket over 30 days silently showed 28 of them.
    /// </summary>
    private sealed record WallRange(string Chip, string Label, TimeSpan Span, TimeSpan Fine, TimeSpan Mid,
        TimeSpan Coarse)
    {
        public int Count(TimeSpan bucket) => Math.Max(1, (int)Math.Round(Span / bucket));
    }

    /// <summary>1 h to 30 d. Every range gets three resolutions rather than a single "right" one: the fine
    /// panel is dense enough to show a session taking shape, the coarse one flat enough to show the trend it
    /// sits inside.</summary>
    private static readonly WallRange[] Ranges =
    {
        new("1 H",  "LAST 1 H",  TimeSpan.FromHours(1),  TimeSpan.FromMinutes(2),  TimeSpan.FromMinutes(5),  TimeSpan.FromMinutes(10)),
        new("6 H",  "LAST 6 H",  TimeSpan.FromHours(6),  TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)),
        new("12 H", "LAST 12 H", TimeSpan.FromHours(12), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),    TimeSpan.FromHours(2)),
        new("24 H", "LAST 24 H", TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),    TimeSpan.FromHours(4)),
        new("7 D",  "LAST 7 D",  TimeSpan.FromDays(7),   TimeSpan.FromHours(3),    TimeSpan.FromHours(12),   TimeSpan.FromDays(1)),
        new("30 D", "LAST 30 D", TimeSpan.FromDays(30),  TimeSpan.FromHours(4),    TimeSpan.FromHours(12),   TimeSpan.FromDays(1)),
    };

    private static UsageDashboardWindow? _open;

    /// <summary>Turns are a plain count, so they get the one formatter UsageAnalytics has no need for.</summary>
    private static readonly Func<double, string> Count = value => value.ToString("N0");

    /// <summary>Show the wall, or bring the existing one forward. One at a time - a second copy is the same
    /// numbers twice, and it would fight the first for the companion monitor.</summary>
    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Maximized;
            existing.Activate();
            return;
        }

        var wall = new UsageDashboardWindow { Owner = owner };
        _open = wall;

        // Off-screen verification runs the whole app past the right edge of every monitor; placing this on a
        // real display mid-run would throw it onto the user's desktop.
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            wall.Left = 6200;
            wall.Top = 200;
            wall.Show();
            return;
        }

        wall.Show();
        // The wall is full-screen either way; the setting only decides WHICH screen it takes over.
        if (AppSettings.Current.TelemetryOnCompanionDisplay) wall.MoveToCompanionMonitor(owner);
        else wall.WindowState = WindowState.Maximized;
    }

    /// <summary>Put the wall on the OTHER display when there is one, full-screen. Falls back to maximising
    /// wherever it already is: a single-monitor user asking for this still wants the big view.</summary>
    private void MoveToCompanionMonitor(Window? owner)
    {
        try
        {
            if (owner is not null && DisplayMonitorService.TryGetCompanionWorkArea(owner, out var target))
            {
                DisplayMonitorService.PlaceOnMonitor(this, target, maximize: true);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A display unplugged between enumeration and placement is not worth taking the window down for.
        }
        WindowState = WindowState.Maximized;
    }

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _hooked;
    private bool _ready;

    public UsageDashboardWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Before _ready, so restoring the chip cannot fire a refresh against half-built state - and so the wall
        // never paints the default range for one frame before jumping to the remembered one.
        RestoreRange();
        _ready = true;
        Refresh();
        // Paint the clock before the first tick, or the wall opens with a blank readout for a whole second -
        // which on a window whose entire job is to look live is the worst possible first frame.
        Tick();

        UsageLog.Instance.Changed += OnUsageChanged;
        _hooked = true;

        // The clock ticks every second; the data rebuilds when a turn lands, and otherwise every 15 seconds so
        // the current bucket's edge and the delta against the last complete one never go stale on an idle machine.
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    /// <summary>One second of wall time: the clock always, the data only when it can have moved. An idle wall
    /// costs a string format per second and nothing else.</summary>
    private void Tick()
    {
        DashClock.Text = DateTime.Now.ToString("ddd dd MMM · HH:mm:ss").ToUpperInvariant();
        if ((DateTime.Now - _lastRefresh).TotalSeconds >= 15) Refresh();
    }

    private void OnUsageChanged() => Dispatcher.BeginInvoke(() => { if (_ready) Refresh(); });

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        if (_hooked) UsageLog.Instance.Changed -= OnUsageChanged;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnMetricChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Refresh();
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.TelemetryWallRangeHours = SelectedRange.Span.TotalHours;
        AppSettings.Current.Save();
        Refresh();
    }

    /// <summary>The chips in <see cref="Ranges"/> order - the two are indexed against each other, so a chip
    /// added to the markup without a range beside it would be caught here rather than silently mapping to
    /// whatever sat in that slot.</summary>
    private System.Windows.Controls.RadioButton[] Chips =>
        new[] { Range1H, Range6H, Range12H, Range24H, Range7D, Range30D };

    /// <summary>Index of the wall's default. Named rather than written as 3 in three places, because moving a
    /// chip in the markup would otherwise silently change what a fresh install opens on.</summary>
    private static readonly int DefaultRange = Array.FindIndex(Ranges, r => r.Span == TimeSpan.FromHours(24));

    private WallRange SelectedRange
    {
        get
        {
            var chips = Chips;
            for (var i = 0; i < chips.Length && i < Ranges.Length; i++)
                if (chips[i].IsChecked == true) return Ranges[i];
            return Ranges[DefaultRange];
        }
    }

    /// <summary>Reopen on the range this machine was last left watching. A value that no longer matches any
    /// chip - a hand-edited settings file, or a range this build has dropped - falls back to the default
    /// rather than leaving every chip unchecked.</summary>
    private void RestoreRange()
    {
        var saved = AppSettings.Current.TelemetryWallRangeHours;
        var index = Array.FindIndex(Ranges, r => Math.Abs(r.Span.TotalHours - saved) < 0.01);
        Chips[index < 0 ? DefaultRange : index].IsChecked = true;
    }

    private HudMetric SelectedMetric =>
        WallByCost.IsChecked == true ? HudMetric.Cost :
        WallByEnergy.IsChecked == true ? HudMetric.Energy : HudMetric.Tokens;

    /// <summary>The wall's metric, said in the donut's vocabulary. The wall has no water chip - water is a fixed
    /// multiple of electricity, so a fifth slice of the same ring would be the electricity ring again.</summary>
    private static UsageMetric ShareMetric(HudMetric metric) => metric switch
    {
        HudMetric.Cost => UsageMetric.Cost,
        HudMetric.Energy => UsageMetric.Energy,
        _ => UsageMetric.Tokens,
    };

    /// <summary>A bucket size as a caption reads it: "30 MIN", "2 H", "1 D".</summary>
    private static string BucketLabel(TimeSpan bucket) =>
        bucket.TotalMinutes < 60 ? $"{bucket.TotalMinutes:0} MIN"
        : bucket.TotalHours < 24 ? $"{bucket.TotalHours:0} H"
        : $"{bucket.TotalDays:0} D";

    /// <summary>How much of the range actually did something, in whatever unit keeps it a small number. A
    /// 1-hour range measures its working time in minutes; a 30-day one in days.</summary>
    private static string ActiveLabel(double hours) =>
        hours < 1 ? $"{hours * 60:0.#} min"
        : hours < 48 ? $"{hours:0.#} h"
        : $"{hours / 24:0.#} d";

    private void Refresh()
    {
        _lastRefresh = DateTime.Now;
        var now = DateTimeOffset.Now;
        var range = SelectedRange;
        var entries = UsageLog.Instance.Entries();

        var fine = UsageAnalytics.BuildBuckets(entries, range.Fine, range.Count(range.Fine), now);
        var mid = UsageAnalytics.BuildBuckets(entries, range.Mid, range.Count(range.Mid), now);
        var coarse = UsageAnalytics.BuildBuckets(entries, range.Coarse, range.Count(range.Coarse), now);

        // The tickers, the donut and the bars all have to be summing the SAME turns, so the report starts
        // exactly where the finest grid starts rather than at now-minus-the-span: that grid is anchored to a
        // clock boundary, so the two are up to one bucket apart and the gap would show as a headline figure
        // that disagrees with the chart underneath it. The offset is taken AT that instant rather than now's,
        // so a range straddling a DST change does not slip an hour.
        var since = fine.Count > 0
            ? new DateTimeOffset(fine[0].Hour, TimeZoneInfo.Local.GetUtcOffset(fine[0].Hour))
            : now - range.Span;
        var report = UsageAnalytics.BuildSince(entries, since, now);

        var metric = SelectedMetric;
        GraphFine.BucketMinutes = range.Fine.TotalMinutes;
        GraphFine.Hours = fine;
        GraphFine.Metric = metric;
        GraphMid.BucketMinutes = range.Mid.TotalMinutes;
        GraphMid.Hours = mid;
        GraphMid.Metric = metric;
        GraphCoarse.BucketMinutes = range.Coarse.TotalMinutes;
        GraphCoarse.Hours = coarse;
        GraphCoarse.Metric = metric;

        var noun = metric switch
        {
            HudMetric.Cost => "SPEND",
            HudMetric.Energy => "ELECTRICITY",
            _ => "THROUGHPUT",
        };
        // The three captions are the same sentence with one word changed, because the three panels ARE the same
        // reading at three zooms. Anything more decorative would imply they were different measurements.
        GraphFineCaption.Text = $"{noun} · {BucketLabel(range.Fine)} BUCKETS · {range.Label}";
        GraphMidCaption.Text = $"{noun} · {BucketLabel(range.Mid)} BUCKETS · {range.Label}";
        GraphCoarseCaption.Text = $"{noun} · {BucketLabel(range.Coarse)} BUCKETS · {range.Label}";
        WallShareCaption.Text = $"SHARE BY MODEL · {range.Label}";
        WallModelsCaption.Text = $"BY MODEL · {range.Label}";

        // ---------- tickers, over the selected range ----------
        var cost = report.CostUsd;
        var tokens = report.Total;
        var energyWh = report.EnergyWh;
        var turns = report.Turns;
        var water = report.WaterLitres;

        // Rolled, like the HUD's tiles - this window is meant to be read from further away than that one.
        RollingNumber.Set(TickSpend, cost, UsageAnalytics.Money);
        RollingNumber.Set(TickTokens, tokens, UsageAnalytics.Tokens);
        RollingNumber.Set(TickEnergy, energyWh, UsageAnalytics.Energy);
        RollingNumber.Set(TickWater, water, UsageAnalytics.Water);
        RollingNumber.Set(TickTurns, turns, Count);

        // Working time is counted on the FINEST grid available, so a single burst inside an otherwise idle
        // month is reported as the few hours it was rather than as every day it touched.
        var activeHours = fine.Count(h => h.Turns > 0) * range.Fine.TotalHours;
        var span = range.Chip.ToLowerInvariant();
        TickSpendSub.Text = turns == 0 ? $"nothing in {span}" : $"{ActiveLabel(activeHours)} active";
        TickTokensSub.Text = report.TotalIn > 0
            ? $"{UsageAnalytics.Percent(report.CacheHitRatio)} from cache"
            : "no input yet";
        TickEnergySub.Text = UsageAnalytics.EnergyEquivalent(energyWh);
        TickWaterSub.Text = UsageAnalytics.WaterEquivalent(water);
        TickTurnsSub.Text = turns == 0 ? "idle" : $"{UsageAnalytics.Tokens(tokens / Math.Max(1, turns))} per turn";

        SparkSpend.Values = mid.Select(h => h.CostUsd).ToList();
        SparkTokens.Values = mid.Select(h => h.Total).ToList();
        SparkEnergy.Values = mid.Select(h => h.EnergyWh).ToList();
        // Water is a fixed multiple of energy, so this line is the same shape as the one above it on purpose.
        SparkWater.Values = mid.Select(h => ModelEnergy.OnSiteWaterLitres(h.EnergyWh)).ToList();
        SparkTurns.Values = mid.Select(h => (double)h.Turns).ToList();

        // The two most recent COMPLETE mid buckets. The current one is still filling, so including it would
        // report a fall every time a bucket rolled over - a number that says more about the clock than the
        // workload. The unit moves with the range, which is why the note under SYSTEM names it.
        var previous = mid.Count >= 2 ? mid[^2] : null;
        var before = mid.Count >= 3 ? mid[^3] : null;
        SetDelta(TickSpendDelta, previous?.CostUsd, before?.CostUsd);
        SetDelta(TickTokensDelta, previous?.Total, before?.Total);
        SetDelta(TickEnergyDelta, previous?.EnergyWh, before?.EnergyWh);
        SetDelta(TickWaterDelta, previous is null ? null : ModelEnergy.OnSiteWaterLitres(previous.EnergyWh),
            before is null ? null : ModelEnergy.OnSiteWaterLitres(before.EnergyWh));
        SetDelta(TickTurnsDelta, previous?.Turns, before?.Turns);

        // ---------- per model, same range ----------
        // The ring and the list beside it are fed the same report: one shows the share, the other the figure,
        // and neither is the only way to read a model off this wall.
        WallDonut.Metric = ShareMetric(metric);
        WallDonut.Report = report;

        double Measure(ModelUsage m) => metric switch
        {
            HudMetric.Cost => m.CostUsd,
            HudMetric.Energy => m.EnergyWh,
            _ => m.Total,
        };
        string Format(ModelUsage m) => metric switch
        {
            HudMetric.Cost => UsageAnalytics.Money(m.CostUsd),
            HudMetric.Energy => UsageAnalytics.Energy(m.EnergyWh),
            _ => UsageAnalytics.Tokens(m.Total),
        };

        var rows = report.Models.Where(m => Measure(m) > 0).OrderByDescending(Measure)
            .Select(m => new HudModelRow
            {
                Display = m.Display,
                Swatch = UsagePalette.BrushFor(m.Model),
                Value = Format(m),
            })
            .ToList();
        WallModels.ItemsSource = rows;
        WallModels.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WallModelsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WallModelsEmpty.Text = $"No model activity in {span}.";

        // ---------- system ----------
        WallCache.Text = UsageAnalytics.Percent(report.CacheHitRatio);
        WallCacheMeter.Fraction = report.CacheHitRatio;
        // Averaged over the time that actually did something - spreading a burst across an idle night would
        // report a rate this machine never ran at.
        WallBurn.Text = activeHours > 0 ? UsageAnalytics.Money(cost / activeHours) + " / active hour" : "—";
        var peak = fine.OrderByDescending(h => h.Total).FirstOrDefault();
        // The date is carried by the RANGE, not the bucket: a 6-hour bucket inside a 30-day window still needs
        // to say which day it was, or "18:00" is one of thirty identical answers.
        var peakFormat = range.Fine >= TimeSpan.FromDays(1) ? "dd MMM"
            : range.Span > TimeSpan.FromHours(24) ? "dd MMM HH:mm"
            : "HH:mm";
        WallPeak.Text = peak is null || peak.Turns == 0
            ? "—"
            : $"{peak.Hour.ToString(peakFormat)} · {UsageAnalytics.Tokens(peak.Total)}";
        WallNote.Text = $"Arrows compare the last complete {BucketLabel(range.Mid).ToLowerInvariant()} with the "
                        + "one before it. Electricity and water are estimates from each model's size and token "
                        + "counts, not meter readings — good to about a factor of 3. Water is on-site cooling "
                        + $"only ({UsageAnalytics.Water(ModelEnergy.TotalWaterLitres(energyWh))} including the "
                        + "water used to generate the electricity).";
    }

    /// <summary>
    /// The stock-ticker delta: the last complete hour against the one before it. Deliberately conservative
    /// about what it will claim - with no prior activity there is no percentage to quote, so it says NEW rather
    /// than inventing an infinite rise.
    /// </summary>
    private void SetDelta(System.Windows.Controls.TextBlock target, double? current, double? previous)
    {
        var now = current ?? 0;
        var was = previous ?? 0;

        if (now <= 0 && was <= 0)
        {
            target.Text = "—";
            target.Foreground = (Brush?)TryFindResource("Faint") ?? Brushes.Gray;
            return;
        }
        if (was <= 0)
        {
            target.Text = "NEW";
            target.Foreground = (Brush?)TryFindResource("Green") ?? Brushes.SeaGreen;
            return;
        }

        var change = (now - was) / was;
        var arrow = change > 0 ? "▲" : change < 0 ? "▼" : "•";
        target.Text = $"{arrow} {Math.Abs(change) * 100:0.#}%";
        target.Foreground = change > 0
            ? (Brush?)TryFindResource("Green") ?? Brushes.SeaGreen
            : change < 0 ? (Brush?)TryFindResource("Red") ?? Brushes.IndianRed
            : (Brush?)TryFindResource("Faint") ?? Brushes.Gray;
    }
}
