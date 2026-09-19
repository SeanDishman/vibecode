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
/// right now" at a single resolution, this answers it at two that no single panel could carry at once. The
/// range strip in the header picks the HISTORY window - anything from the last hour to the last month - and the
/// tickers, the throughput graph, the donut and the model list all redraw against that one span, read from the
/// SAME <see cref="UsageLog"/>, so they can never quietly disagree.
///
/// Under them sit the two LIVE panels, and those deliberately ignore the range strip. They are the last few
/// minutes at 5- and 10-second resolution, drawn from <see cref="LiveTurnTelemetry.Flow"/> - tokens stamped as
/// they streamed rather than as the turn that committed them. Nothing scoped to a range can show that: the
/// shortest range's finest bucket is two minutes wide, and the committed log has no sub-turn timing in it at
/// all. The only other thing on this wall that is a rate rather than a level is the per-ticker delta, and it is
/// labelled as such.
/// </summary>
public partial class UsageDashboardWindow : Window
{
    /// <summary>
    /// One entry on the range strip: the span it covers, the bucket size the throughput graph draws it at, and
    /// the coarser size the tickers' sparklines and their deltas are taken over. Counts are derived from the
    /// span rather than written down, so the two can never fall out of step.
    ///
    /// Every size here DIVIDES A DAY, and that is a correctness rule rather than a stylistic one:
    /// <see cref="UsageAnalytics.BuildBuckets"/> anchors its grid to midnight and steps back, so a size that
    /// does not divide a day leaves the newest bucket straddling "now" and the grid reaching less far back than
    /// the caption claims - a 3-day bucket over 30 days silently showed 28 of them.
    /// </summary>
    private sealed record WallRange(string Chip, string Label, TimeSpan Span, TimeSpan Fine, TimeSpan Mid)
    {
        public int Count(TimeSpan bucket) => Math.Max(1, (int)Math.Round(Span / bucket));
    }

    /// <summary>1 h to 30 d. Fine is what the throughput graph plots; Mid is the coarser grid the tickers'
    /// sparklines and their "against the last complete bucket" arrows are taken over, which is why the note
    /// under SYSTEM names it.</summary>
    private static readonly WallRange[] Ranges =
    {
        new("1 H",  "LAST 1 H",  TimeSpan.FromHours(1),  TimeSpan.FromMinutes(2),  TimeSpan.FromMinutes(5)),
        new("6 H",  "LAST 6 H",  TimeSpan.FromHours(6),  TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)),
        new("12 H", "LAST 12 H", TimeSpan.FromHours(12), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)),
        new("24 H", "LAST 24 H", TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)),
        new("7 D",  "LAST 7 D",  TimeSpan.FromDays(7),   TimeSpan.FromHours(3),    TimeSpan.FromHours(12)),
        new("30 D", "LAST 30 D", TimeSpan.FromDays(30),  TimeSpan.FromHours(4),    TimeSpan.FromHours(12)),
    };

    /// <summary>
    /// The two live panels. Fixed sizes, never scoped by the range strip, and both divide a day so
    /// <see cref="UsageAnalytics.BuildBuckets"/>' midnight anchoring holds at this resolution too
    /// (86400 / 5 = 17280 bars, 86400 / 10 = 8640).
    ///
    /// Sixty bars each, so one panel is the last five minutes and the other the last ten: the same live traffic
    /// at two zooms, which is what makes a single burst distinguishable from a machine that has been busy for
    /// ten minutes straight.
    /// </summary>
    private static readonly TimeSpan LiveFast = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LiveSlow = TimeSpan.FromSeconds(10);
    private const int LiveBars = 60;

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

    /// <summary>Automated-verification hook, mirroring VIBECODE_OPEN_GAME: with VIBECODE_HIDDEN=1 set,
    /// VIBECODE_OPEN_WALL=1 opens the wall off-screen at startup. Its normal entry points sit behind a Settings
    /// pane that is Collapsed until selected, so a smoke run cannot otherwise reach it through the UIA tree.
    /// Does nothing during a normal run.</summary>
    internal static void MaybeAutoOpenForSmoke(Window owner)
    {
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") != "1") return;
        if (Environment.GetEnvironmentVariable("VIBECODE_OPEN_WALL") != "1") return;
        Open(owner);
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
    /// <summary>Whether anything was generating as of the last read. Drives the one-second rebuild, and because
    /// it is only cleared by a read that found nothing, a turn ENDING still gets the extra refresh that takes
    /// its in-flight row back off the wall.</summary>
    private bool _live;
    /// <summary>The newest live bucket, and how many flow rows the live panels were last drawn from. Together
    /// they are the whole "has anything changed" test for the second-resolution pair: a rebuild is needed when
    /// tokens arrived, when tokens have just stopped arriving, or when the window scrolled on a bucket.</summary>
    private long _liveBucket = -1;
    private int _flowRows;
    /// <summary>Held for as long as this window is open. Its existence is what makes the chats publish their
    /// in-flight turns at all - see <see cref="LiveTurnTelemetry"/>.</summary>
    private IDisposable? _liveWatch;
    /// <summary>Held while the wall is open. Polling the model speed board is tied to this rather than running for
    /// the life of the app: nothing else on screen reads those figures.</summary>
    private IDisposable? _speedWatch;

    public UsageDashboardWindow() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Before the first Refresh, so a turn already streaming when the wall opens is on the very first frame
        // rather than appearing a second later.
        _liveWatch = LiveTurnTelemetry.Instance.Watch();

        // The speed board is bound once and mutates in place: its rows are a fixed roster, so rebuilding the
        // ItemsSource every poll would rebuild seven containers to change two strings.
        WallModelSpeeds.ItemsSource = ModelSpeedBoard.Instance.Rows;
        ModelSpeedBoard.Instance.Updated += OnModelSpeedsUpdated;
        _speedWatch = ModelSpeedBoard.Instance.Watch();
        RefreshSpeedBoardCaption();

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

        // The clock ticks every second; the data rebuilds when a turn lands, every second while one is actually
        // streaming, and otherwise every 15 seconds so the current bucket's edge and the delta against the last
        // complete one never go stale on an idle machine.
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    /// <summary>
    /// One second of wall time: the clock always, the data only when it can have moved.
    ///
    /// While a turn is generating it CAN have moved every second, so it rebuilds every second - that is what
    /// makes this window live rather than a screenshot of the last turn to finish. One second rather than
    /// anything faster because <see cref="HudMotion.Morph"/> is 650 ms: refreshing inside that would restart
    /// every rolling figure before it had arrived, so the wall would update more often and read as less current.
    /// An idle wall is unchanged - a string format per second and nothing else.
    /// </summary>
    private void Tick()
    {
        DashClock.Text = DateTime.Now.ToString("ddd dd MMM · HH:mm:ss").ToUpperInvariant();
        // Always, and BEFORE the decision below: it is two counter reads, it is what sets _live, and taking it
        // first is what lets a turn that has just started be rebuilt on this tick rather than the next one.
        ShowLiveRate();
        var idle = (DateTime.Now - _lastRefresh).TotalSeconds;
        if (idle >= 15 || (_live && idle >= 1)) Refresh();
        // Unconditionally, and after the branch above so a refresh that already redrew them does not do it
        // twice: the live pair is on the clock, not on the data - its axis scrolls every five seconds whether
        // or not a token arrived. It no-ops when there is genuinely nothing new.
        else RefreshLivePanels();
        // The speed board's age is on the clock too: it counts up between polls, and a caption frozen at
        // "UPDATED 0 MIN AGO" would claim the figures were fresher than they are. Setting a TextBlock to the
        // string it already holds is a no-op, so this costs a subtraction on the 59 seconds in 60 it is unchanged.
        RefreshSpeedBoardCaption();
    }

    private void OnUsageChanged() => Dispatcher.BeginInvoke(() => { if (_ready) Refresh(); });

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        if (_hooked) UsageLog.Instance.Changed -= OnUsageChanged;
        // Last window out disarms live tracking entirely, so a closed wall costs a streaming turn one branch.
        _liveWatch?.Dispose();
        _liveWatch = null;
        // Same reasoning for the speed board: a closed wall must not keep polling OpenRouter every five minutes.
        ModelSpeedBoard.Instance.Updated -= OnModelSpeedsUpdated;
        _speedWatch?.Dispose();
        _speedWatch = null;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    private void OnModelSpeedsUpdated() => RefreshSpeedBoardCaption();

    /// <summary>
    /// Say how old these figures are. They are not live and must not look it: OpenRouter's medians move on their
    /// own clock, this wall polls on a five-minute one, and an unlabelled number on a screen full of live ones
    /// would be read as current to the second.
    /// </summary>
    private void RefreshSpeedBoardCaption()
    {
        if (ModelSpeedBoard.Instance.LastUpdated is not { } at)
        {
            WallSpeedAge.Text = "FETCHING…";
            return;
        }
        var age = DateTime.UtcNow - at;
        WallSpeedAge.Text = age < TimeSpan.FromMinutes(1)
            ? "UPDATED JUST NOW · 30-MIN MEDIANS"
            : $"UPDATED {(int)age.TotalMinutes} MIN AGO · 30-MIN MEDIANS";
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

    /// <summary>Index of the wall's default. Named rather than written as a number in three places, because
    /// moving a chip in the markup would otherwise silently change what a fresh install opens on. The last
    /// hour rather than the last day: this window is watched while working, and a 24-hour span puts the session
    /// you are actually in inside the last inch of the graph.</summary>
    private static readonly int DefaultRange = Array.FindIndex(Ranges, r => r.Span == TimeSpan.FromHours(1));

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

    /// <summary>A bucket size as a caption reads it: "5 S", "30 MIN", "2 H", "1 D".</summary>
    private static string BucketLabel(TimeSpan bucket) =>
        bucket.TotalSeconds < 60 ? $"{bucket.TotalSeconds:0} S"
        : bucket.TotalMinutes < 60 ? $"{bucket.TotalMinutes:0} MIN"
        : bucket.TotalHours < 24 ? $"{bucket.TotalHours:0} H"
        : $"{bucket.TotalDays:0} D";

    /// <summary>How far a live panel reaches back, as its caption says it: "LAST 5 MIN".</summary>
    private static string LiveSpanLabel(TimeSpan bucket) =>
        $"LAST {(bucket * LiveBars).TotalMinutes:0} MIN";

    /// <summary>How much of the range actually did something, in whatever unit keeps it a small number. A
    /// 1-hour range measures its working time in minutes; a 30-day one in days.</summary>
    private static string ActiveLabel(double hours) =>
        hours < 1 ? $"{hours * 60:0.#} min"
        : hours < 48 ? $"{hours:0.#} h"
        : $"{hours / 24:0.#} d";

    /// <summary>
    /// The header's live readout and the SYSTEM card's rate row. Split out of <see cref="Refresh"/> because it
    /// is the one thing on this wall cheap enough to repaint on a tick that did not rebuild anything: it reads
    /// two counters, where a full refresh re-aggregates the whole log at three resolutions.
    /// </summary>
    private void ShowLiveRate()
    {
        var streaming = LiveTurnTelemetry.Instance.StreamingCount;
        var rate = LiveTurnTelemetry.Instance.OutputTokensPerSecond;
        _live = streaming > 0 || rate > 0;

        // The header is the only thing on this wall read from the far side of a room, so the rate goes there
        // rather than only in the stats card - "LIVE" on its own is a label, "LIVE · 84 TOK/S" is telemetry.
        DashRange.Text = rate > 0
            ? $"LIVE · {UsageAnalytics.Tokens(rate)} TOK/S"
            : "LIVE";
        WallLive.Text = streaming == 0
            ? "—"
            : $"{UsageAnalytics.Tokens(rate)} tok/s · {UsageAnalytics.Tokens(LiveTurnTelemetry.Instance.StreamingTokens)} so far"
              + (streaming > 1 ? $" · {streaming} turns" : "");
    }

    /// <summary>What the selected metric is called where a caption names it.</summary>
    private static string MetricNoun(HudMetric metric) => metric switch
    {
        HudMetric.Cost => "SPEND",
        HudMetric.Energy => "ELECTRICITY",
        _ => "THROUGHPUT",
    };

    /// <summary>
    /// The two second-resolution panels.
    ///
    /// Split out of <see cref="Refresh"/> and driven from the one-second tick instead, because a panel of
    /// 5-second bars redrawn on the wall's idle 15-second schedule would sit three bars behind its own axis -
    /// the window it is drawing scrolls whether or not any tokens arrive. It is cheap enough to do that with:
    /// <see cref="LiveTurnTelemetry.Flow"/> holds minutes, not history, so this aggregates a few hundred rows
    /// where a full refresh re-aggregates the whole log twice.
    ///
    /// Idle costs nothing at all. With no flow to draw and the newest bucket unchanged there is, by definition,
    /// nothing new to paint, so the rebuild is skipped rather than restarting both morph animations every
    /// second on a pair of panels that are already correct.
    /// </summary>
    private void RefreshLivePanels(bool force = false)
    {
        var now = DateTimeOffset.Now;
        var flow = LiveTurnTelemetry.Instance.Flow();
        var bucket = now.LocalDateTime.Ticks / LiveFast.Ticks;
        if (!force && flow.Count == 0 && _flowRows == 0 && bucket == _liveBucket) return;
        _flowRows = flow.Count;
        _liveBucket = bucket;

        var metric = SelectedMetric;
        GraphLiveFast.BucketMinutes = LiveFast.TotalMinutes;
        GraphLiveFast.Hours = UsageAnalytics.BuildBuckets(flow, LiveFast, LiveBars, now);
        GraphLiveFast.Metric = metric;
        GraphLiveSlow.BucketMinutes = LiveSlow.TotalMinutes;
        GraphLiveSlow.Hours = UsageAnalytics.BuildBuckets(flow, LiveSlow, LiveBars, now);
        GraphLiveSlow.Metric = metric;

        // "LIVE" rather than the range's label, because that word is the whole difference between these two
        // panels and the one above them.
        var noun = MetricNoun(metric);
        GraphLiveFastCaption.Text = $"LIVE {noun} · {BucketLabel(LiveFast)} BUCKETS · {LiveSpanLabel(LiveFast)}";
        GraphLiveSlowCaption.Text = $"LIVE {noun} · {BucketLabel(LiveSlow)} BUCKETS · {LiveSpanLabel(LiveSlow)}";
    }

    private void Refresh()
    {
        _lastRefresh = DateTime.Now;
        var now = DateTimeOffset.Now;
        var range = SelectedRange;
        ShowLiveRate();
        // Turns still generating are laid over the committed history rather than waited for. Every panel below
        // reads this one list, so an in-flight turn moves the tickers, the graphs, the donut and the model list
        // together - a wall where only the headline figure was live would be a wall whose panels disagree.
        var entries = LiveTurnTelemetry.Instance.Merge(UsageLog.Instance.Entries());

        var fine = UsageAnalytics.BuildBuckets(entries, range.Fine, range.Count(range.Fine), now);
        var mid = UsageAnalytics.BuildBuckets(entries, range.Mid, range.Count(range.Mid), now);

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

        GraphFineCaption.Text = $"{MetricNoun(metric)} · {BucketLabel(range.Fine)} BUCKETS · {range.Label}";
        WallShareCaption.Text = $"SHARE BY MODEL · {range.Label}";
        WallModelsCaption.Text = $"BY MODEL · {range.Label}";
        // The live panels carry the wall's METRIC - one wall, one measure - but never its span. Forced,
        // because the metric they are drawn in may be the only thing that changed.
        RefreshLivePanels(force: true);

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
