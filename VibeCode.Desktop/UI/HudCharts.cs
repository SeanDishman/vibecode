using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Which number the HUD's graph and tiles are showing. One metric at a time, on one axis - a second
/// scale overlaid on the same panel would let any two series be made to look correlated.</summary>
public enum HudMetric { Tokens, Cost, Energy }

/// <summary>
/// The HUD's main panel: one bar per clock hour, each bar stacked by model.
///
/// Stacking by model is the only "extra" here, and it earns its place - the segments share ONE axis and one
/// unit, and the hue is the model's own fixed <see cref="UsagePalette"/> slot, the same colour it wears in
/// settings. Empty hours are drawn as a baseline tick rather than skipped, so an idle night reads as idle
/// instead of being compressed away.
/// </summary>
public sealed class HourlyThroughput : FrameworkElement
{
    public static readonly DependencyProperty HoursProperty = DependencyProperty.Register(
        nameof(Hours), typeof(IReadOnlyList<HourUsage>), typeof(HourlyThroughput),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnHoursChanged));

    /// <summary>
    /// How far the bars have travelled from the heights they were drawn at to the heights the reading that just
    /// arrived asks for. The AXIS deliberately does not move with it - gridlines sliding while bars grow makes
    /// both unreadable - so the scale is the new reading's from the first frame and only the bars are in motion.
    /// At rest this is 1 and the panel is exactly the data.
    /// </summary>
    private static readonly DependencyProperty MorphProperty = DependencyProperty.Register(
        "Morph", typeof(double), typeof(HourlyThroughput),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(HudMetric), typeof(HourlyThroughput),
        new FrameworkPropertyMetadata(HudMetric.Tokens, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Minutes per bar. Drives the x-axis tick spacing, the label format and the tooltip title, so the
    /// same panel reads correctly at 30-minute, hourly and daily resolution. 60 keeps the original 24-hour HUD
    /// strip byte-for-byte as it was.</summary>
    public static readonly DependencyProperty BucketMinutesProperty = DependencyProperty.Register(
        nameof(BucketMinutes), typeof(double), typeof(HourlyThroughput),
        new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double BucketMinutes
    {
        get => (double)GetValue(BucketMinutesProperty);
        set => SetValue(BucketMinutesProperty, value);
    }

    public IReadOnlyList<HourUsage>? Hours
    {
        get => (IReadOnlyList<HourUsage>?)GetValue(HoursProperty);
        set => SetValue(HoursProperty, value);
    }

    public HudMetric Metric
    {
        get => (HudMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    private ChartText _label = null!;
    private int _hover = -1;
    private Point _pointer;

    /// <summary>Bucket start -> the total that bucket's bar was actually drawn at when the last reading landed.
    /// Keyed by time rather than by index, so the window scrolling on by one bucket slides each bar to its own
    /// new slot instead of making all twenty-four jump to their neighbour's height.</summary>
    private Dictionary<DateTime, double> _from = new();

    private const double GutterLeft = 46, GutterBottom = 18, PadTop = 10;

    private static void OnHoursChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (HourlyThroughput)d;
        if (!HudMotion.Enabled)
        {
            chart._from.Clear();
            chart.BeginAnimation(MorphProperty, null);
            return;
        }

        // Snapshot the heights that are ON SCREEN, not the ones the previous reading asked for: a turn landing
        // mid-tween would otherwise yank every bar back to where the tween before it started.
        var morph = (double)chart.GetValue(MorphProperty);
        var previous = chart._from;
        var snapshot = new Dictionary<DateTime, double>();
        if (e.OldValue is IReadOnlyList<HourUsage> old)
            foreach (var hour in old)
                snapshot[hour.Hour] = HudMotion.Lerp(previous.GetValueOrDefault(hour.Hour), chart.Value(hour), morph);
        chart._from = snapshot;

        // With no previous reading the snapshot is empty, every bar starts at zero and the panel grows in when
        // the window opens - which is the one moment a dashboard is allowed to show off.
        chart.BeginAnimation(MorphProperty, new DoubleAnimation(0, 1, HudMotion.Morph)
        {
            EasingFunction = HudMotion.Ease,
        });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
        var index = HitTest(_pointer);
        if (index != _hover) { _hover = index; }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    private int HitTest(Point p)
    {
        var hours = Hours;
        if (hours is null || hours.Count == 0) return -1;
        var plot = new Rect(GutterLeft, PadTop, Math.Max(1, ActualWidth - GutterLeft - 8),
            Math.Max(1, ActualHeight - PadTop - GutterBottom));
        if (!plot.Contains(p)) return -1;
        var slot = plot.Width / hours.Count;
        var index = (int)((p.X - plot.Left) / slot);
        return index < 0 || index >= hours.Count ? -1 : index;
    }

    private double Value(HourUsage h) => Metric switch
    {
        HudMetric.Cost => h.CostUsd,
        HudMetric.Energy => h.EnergyWh,
        _ => h.Total,
    };

    private static double Value(HourSlice s, HudMetric metric) => metric switch
    {
        HudMetric.Cost => s.CostUsd,
        HudMetric.Energy => s.EnergyWh,
        _ => s.Tokens,
    };

    /// <summary>
    /// Round the axis up to a clean number, on a FINER ladder than <see cref="Chart.NiceCeiling"/>.
    /// The shared one steps 1 / 2 / 2.5 / 5 / 10, which sends a 110M peak to a 200M ceiling and leaves the
    /// panel looking half empty - fine on a settings card that is read once, wrong on a monitor that is meant
    /// to be glanced at. The extra rungs keep the tallest bar in the top third without ever showing a ragged
    /// axis label: every rung is still divisible by four, so the gridlines stay round.
    /// </summary>
    private static double NiceMax(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var n = value / magnitude;
        var step = n <= 1 ? 1 : n <= 1.2 ? 1.2 : n <= 1.6 ? 1.6 : n <= 2 ? 2 : n <= 2.4 ? 2.4
            : n <= 3 ? 3 : n <= 4 ? 4 : n <= 5 ? 5 : n <= 6 ? 6 : n <= 8 ? 8 : 10;
        return step * magnitude;
    }

    /// <summary>For an axis: every tick shares the unit the largest value picked.</summary>
    private string Axis(double v, double scaleMax) => Metric switch
    {
        HudMetric.Cost => UsageAnalytics.MoneyScaled(v, scaleMax),
        HudMetric.Energy => UsageAnalytics.EnergyScaled(v, scaleMax),
        _ => UsageAnalytics.Tokens(v),
    };

    /// <summary>For a tooltip, where each figure stands alone and should pick its own unit.</summary>
    private string Format(double v) => Metric switch
    {
        HudMetric.Cost => UsageAnalytics.Money(v),
        HudMetric.Energy => UsageAnalytics.Energy(v),
        _ => UsageAnalytics.Tokens(v),
    };

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        _label = new ChartText(this, TryFindResource("Mono") as FontFamily ?? new FontFamily("Consolas"), FontWeights.Normal);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        var ink = ChartInk.From(this, "Bg1");

        var hours = Hours;
        if (hours is null || hours.Count == 0)
        {
            var ft = _label.Make("awaiting telemetry", 11, ink.Muted);
            dc.DrawText(ft, new Point((size.Width - ft.Width) / 2, (size.Height - ft.Height) / 2));
            return;
        }

        var plot = new Rect(GutterLeft, PadTop, Math.Max(1, size.Width - GutterLeft - 8),
            Math.Max(1, size.Height - PadTop - GutterBottom));
        var max = NiceMax(hours.Max(Value));
        if (max <= 0) max = 1;

        // Grid + y labels. Four bands is enough to read a height off; more turns the panel into graph paper.
        var grid = Chart.Hairline(ink.Grid);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4.0;
            Chart.Rule(dc, grid, plot.Left, y, plot.Right, y);
            _label.DrawRight(dc, Axis(max * i / 4.0, max), 8.5, ink.Muted, plot.Left - 6, y);
        }

        var slot = plot.Width / hours.Count;
        var barWidth = Math.Max(2, Math.Min(24, slot - 3));
        var morph = (double)GetValue(MorphProperty);
        var spansDays = hours.Count > 1 && hours[^1].Hour.Date != hours[0].Hour.Date;

        for (var i = 0; i < hours.Count; i++)
        {
            var hour = hours[i];
            var centre = plot.Left + slot * (i + 0.5);
            var left = centre - barWidth / 2;
            var total = Value(hour);

            // How tall to draw the bar THIS frame: on its way from the height it had to the height the reading
            // asks for. The stack keeps the new reading's proportions and is simply scaled, so no segment ever
            // has to be interpolated against a model that was not in the old bucket at all.
            var scale = morph >= 1 || total <= 0
                ? 1
                : HudMotion.Lerp(_from.GetValueOrDefault(hour.Hour), total, morph) / total;

            // An hour with nothing in it still gets a tick on the baseline, so idle time is visible as idle
            // rather than as a gap the eye reads straight through.
            if (total <= 0)
            {
                dc.DrawRectangle(ink.Axis, null, new Rect(left, plot.Bottom - 2, barWidth, 2));
            }
            else
            {
                // Segments run largest-first from the baseline up, so the dominant model is always the block
                // sitting on the axis and the eye can compare it hour to hour without re-reading the legend.
                var y = plot.Bottom;
                foreach (var (model, slice) in hour.ByModel.OrderByDescending(kv => Value(kv.Value, Metric)))
                {
                    var v = Value(slice, Metric);
                    if (v <= 0) continue;
                    var h = plot.Height * (v / max);
                    // Floor first, scale second. The other order would give a bar growing from nothing a stack
                    // of 1px segments, which reads as a thick line rather than as an empty hour.
                    if (h < 1) h = 1;
                    h *= scale;
                    if (h <= 0) continue;
                    var rect = new Rect(left, y - h, barWidth, h);
                    dc.DrawRectangle(Chart.Frozen(UsagePalette.ColorFor(model), i == _hover ? 1 : 0.88), null, rect);
                    y -= h;
                }
            }

            // Hourly keeps its original rhythm: a tick every 3 clock hours, plus midnight, which is what makes a
            // 24h strip readable at a glance. Other resolutions aim for roughly eight labels across the panel
            // instead, because a 30-minute strip on the same rule would print a label every six bars.
            var isMidnight = hour.Hour is { Hour: 0, Minute: 0 };
            var showTick = BucketMinutes == 60
                ? hour.Hour.Hour % 3 == 0
                : i % Math.Max(1, (int)Math.Ceiling(hours.Count / 8.0)) == 0;
            if (showTick)
            {
                // A clock time recurs every day, so once a panel covers more than one day at a bucket size of a
                // few hours the tick has to carry the day as well: a 7-day strip of 12-hour buckets otherwise
                // labelled itself "12:00" six times running, which is worse than labelling nothing. Hourly is
                // exempt because its own rule always lands on midnight, and midnight prints the weekday.
                var text = BucketMinutes >= 1440 ? hour.Hour.ToString("dd MMM")
                    : isMidnight ? hour.Hour.ToString("ddd")
                    : BucketMinutes == 60 ? hour.Hour.ToString("HH")
                    : BucketMinutes >= 120 && spansDays ? hour.Hour.ToString("dd HH:mm")
                    : hour.Hour.ToString("HH:mm");
                var ft = _label.Make(text, 8.5, isMidnight ? ink.Secondary : ink.Muted);
                dc.DrawText(ft, new Point(centre - ft.Width / 2, plot.Bottom + 4));
            }
        }

        Chart.Rule(dc, Chart.Hairline(ink.Axis), plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        if (_hover >= 0 && _hover < hours.Count) DrawTooltip(dc, ink, size, hours[_hover]);
    }

    private void DrawTooltip(DrawingContext dc, ChartInk ink, Size size, HourUsage hour)
    {
        var rows = new List<(string, string)>
        {
            ("turns", hour.Turns.ToString("N0")),
            ("tokens", UsageAnalytics.Tokens(hour.Total)),
            ("cost", UsageAnalytics.Money(hour.CostUsd)),
            ("energy", UsageAnalytics.Energy(hour.EnergyWh)),
        };
        foreach (var (model, slice) in hour.ByModel.OrderByDescending(kv => Value(kv.Value, Metric)))
            rows.Add((UsagePalette.DisplayName(model), Format(Value(slice, Metric))));

        const double pad = 8, gap = 3, titleSize = 10.5, rowSize = 9.5;
        var title = BucketMinutes >= 1440 ? hour.Hour.ToString("ddd dd MMM")
            : BucketMinutes == 60 ? hour.Hour.ToString("ddd HH:00")
            : hour.Hour.ToString("ddd HH:mm");
        var width = _label.Width(title, titleSize);
        var height = _label.Height(title, titleSize);
        foreach (var (key, value) in rows)
        {
            width = Math.Max(width, _label.Width(key + "    " + value, rowSize));
            height += gap + _label.Height(value, rowSize);
        }
        width += pad * 2;
        height += pad * 2;

        var x = Math.Min(Math.Max(4, _pointer.X + 14), Math.Max(4, size.Width - width - 4));
        var y = Math.Min(Math.Max(4, _pointer.Y - height - 10), Math.Max(4, size.Height - height - 4));
        var box = new Rect(x, y, width, height);
        dc.DrawRectangle(ink.Overlay, Chart.Hairline(ink.Axis), box);

        var cursor = box.Top + pad;
        _label.Draw(dc, title, titleSize, ink.Primary, box.Left + pad, cursor);
        cursor += _label.Height(title, titleSize);
        foreach (var (key, value) in rows)
        {
            cursor += gap;
            _label.Draw(dc, key, rowSize, ink.Secondary, box.Left + pad, cursor);
            var ft = _label.Make(value, rowSize, ink.Primary);
            dc.DrawText(ft, new Point(box.Right - pad - ft.Width, cursor));
            cursor += _label.Height(value, rowSize);
        }
    }
}

/// <summary>
/// A thumbnail trend for a stat tile: filled area, no axes, no labels. It carries shape only - every tile
/// prints its actual number right above it, so nothing here is the sole source of a value.
///
/// This is also the one thing on the HUD that moves while nothing is happening: a slow ripple travels along the
/// curve, so a glance from across the room answers "is this thing still running" without waiting for a turn to
/// land. It is held to one rule - the ripple's amplitude is scaled by each point's OWN value, so a series that
/// is flat at zero is drawn flat at zero. An idle night is never dressed up as a quiet one.
/// </summary>
public sealed class HudSparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(HudSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(HudSparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the drawn curve has travelled from the series it was showing to the one it was last
    /// handed. 1, and therefore exactly the data, within <see cref="HudMotion.Morph"/> of every reading.</summary>
    private static readonly DependencyProperty MorphProperty = DependencyProperty.Register(
        "Morph", typeof(double), typeof(HudSparkline),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Free-running 0 -> 1 ramp behind the ripple. Decoration, and the only thing on either window
    /// that repaints without new data.</summary>
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(HudSparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Peak vertical travel of the ripple, in device-independent pixels, against tiles that are 20
    /// (HUD) to 26 (wall) tall. Small enough to read as breathing rather than as a figure that will not sit
    /// still - at this size the curve moves about a twentieth of its own height.</summary>
    private const double RippleAmplitude = 1.1;

    private IReadOnlyList<double>? _from;
    private bool _rippling;

    public HudSparkline()
    {
        // Started on load rather than in the constructor: an element that is never shown - and both of these
        // windows build tiles the layout may not need - should not be holding an animation clock open.
        Loaded += (_, _) =>
        {
            // Detach first: WPF raises Loaded again for an element re-parented within a live tree, and a second
            // subscription would outlive the single Unloaded that eventually arrives.
            AppSettings.Changed -= OnSettingsChanged;
            AppSettings.Changed += OnSettingsChanged;
            SyncRipple();
        };
        Unloaded += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            BeginAnimation(PhaseProperty, null);
            _rippling = false;
        };
    }

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>Saves can come off a background thread; the animation clock cannot be touched from one.</summary>
    private void OnSettingsChanged() => Dispatcher.BeginInvoke(SyncRipple);

    /// <summary>Start or stop the ripple to match the setting. Guarded on the current state rather than run
    /// unconditionally, because every unrelated save raises Changed and restarting the clock each time would
    /// snap the wave back to its start. The switch lives in a dialog the user can have open beside this very
    /// window, so waiting for a reopen to honour it would look broken.</summary>
    private void SyncRipple()
    {
        if (HudMotion.Enabled == _rippling) return;
        _rippling = HudMotion.Enabled;
        if (!_rippling)
        {
            BeginAnimation(PhaseProperty, null);
            return;
        }

        var ripple = new DoubleAnimation(0, 1, HudMotion.Ripple) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(ripple, HudMotion.RippleFrameRate);
        BeginAnimation(PhaseProperty, ripple);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spark = (HudSparkline)d;
        if (!HudMotion.Enabled)
        {
            spark._from = null;
            spark.BeginAnimation(MorphProperty, null);
            return;
        }

        // Blend from what is on screen this instant, not from the series that was handed over last time: a turn
        // landing mid-tween would otherwise snap the curve back to where the previous tween began.
        spark._from = Blend(spark._from, (IReadOnlyList<double>?)e.OldValue, (double)spark.GetValue(MorphProperty));
        spark.BeginAnimation(MorphProperty, new DoubleAnimation(0, 1, HudMotion.Morph)
        {
            EasingFunction = HudMotion.Ease,
        });
    }

    /// <summary>The series as drawn at <paramref name="t"/> of the way from <paramref name="previous"/> to
    /// <paramref name="target"/>. Missing indices count as zero, so a series that gains or loses a bucket grows
    /// or shrinks that end rather than shifting every point along.</summary>
    private static IReadOnlyList<double>? Blend(IReadOnlyList<double>? previous, IReadOnlyList<double>? target, double t)
    {
        if (target is null || target.Count == 0) return previous;
        if (previous is null || previous.Count == 0 || t >= 1) return target;

        var blended = new double[Math.Max(previous.Count, target.Count)];
        for (var i = 0; i < blended.Length; i++)
            blended[i] = HudMotion.Lerp(i < previous.Count ? previous[i] : 0, i < target.Count ? target[i] : 0, t);
        return blended;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Blend(_from, Values, (double)GetValue(MorphProperty));
        if (values is null || values.Count < 2 || ActualWidth <= 1 || ActualHeight <= 1) return;

        var tint = Tint ?? TryFindResource("Accent") as Brush ?? Brushes.SteelBlue;
        var max = values.Max();
        if (max <= 0) max = 1;

        // Gated on the clock actually running, not merely on the setting: Phase sits at 0 until the ripple
        // starts, and sin() of a standing phase is a FROZEN distortion of the curve rather than no distortion.
        // Before load, and the moment the setting goes off, the tile must be the data and nothing else.
        var live = _rippling;
        var wave = (double)GetValue(PhaseProperty) * Math.Tau;

        var step = ActualWidth / (values.Count - 1);
        var figure = new PathFigure { StartPoint = new Point(0, ActualHeight), IsClosed = true, IsFilled = true };
        for (var i = 0; i < values.Count; i++)
        {
            var norm = Math.Max(0, values[i]) / max;
            var y = ActualHeight - norm * (ActualHeight - 1.5);
            if (live)
            {
                // 0.55 rad of phase per point puts a little over two crests across a 24-bucket tile - a travelling
                // wave rather than the whole line bobbing in unison, which is what makes it read as flowing.
                // The amplitude taper is the honesty clause: near-zero points barely move at all.
                y += Math.Sin(wave + i * 0.55) * RippleAmplitude * Math.Min(1, norm * 3);
                y = Math.Clamp(y, 0, ActualHeight);
            }
            figure.Segments.Add(new LineSegment(new Point(i * step, y), true));
        }
        figure.Segments.Add(new LineSegment(new Point(ActualWidth, ActualHeight), false));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        var fill = tint.Clone();
        fill.Opacity = 0.22;
        fill.Freeze();
        dc.DrawGeometry(fill, null, geometry);

        var stroke = new Pen(tint, 1.2) { LineJoin = PenLineJoin.Round };
        stroke.Freeze();
        dc.DrawGeometry(null, stroke, geometry);
    }
}

/// <summary>A segmented fill meter. Twenty ticks rather than a smooth bar purely because a HUD reads better
/// that way; the percentage is always printed beside it, so the segments are decoration, not the datum.</summary>
public sealed class HudMeter : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(HudMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Brush), typeof(HudMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? Tint
    {
        get => (Brush?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return;
        const int ticks = 20;
        var tint = Tint ?? TryFindResource("Accent") as Brush ?? Brushes.SteelBlue;
        var off = TryFindResource("BorderSoft") as Brush ?? Brushes.DimGray;
        var lit = (int)Math.Round(Math.Clamp(Fraction, 0, 1) * ticks);

        var slot = ActualWidth / ticks;
        var w = Math.Max(1, slot - 1.5);
        for (var i = 0; i < ticks; i++)
            dc.DrawRectangle(i < lit ? tint : off, null, new Rect(i * slot, 0, w, ActualHeight));
    }
}
