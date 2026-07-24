using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>The ink a chart draws with, resolved from whichever theme dictionary is loaded so one chart body
/// serves both the dark theme and CLI mode.</summary>
public sealed class ChartInk
{
    public required Brush Surface { get; init; }     // what the 2px gaps and marker rings are painted in
    public required Brush Primary { get; init; }
    public required Brush Secondary { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Grid { get; init; }
    public required Brush Axis { get; init; }
    public required Brush Overlay { get; init; }
    public required Brush Accent { get; init; }

    /// <summary>True in CLI mode, where the theme maps every corner radius to zero. The charts follow it so a
    /// rounded bar end is not the one soft corner left on an otherwise hard-edged terminal look.</summary>
    public required bool Square { get; init; }

    /// <summary>A spec radius, or 0 when the active theme squares its corners.</summary>
    public double Radius(double spec) => Square ? 0 : spec;

    public static ChartInk From(FrameworkElement owner, string surfaceKey)
    {
        Brush Get(string key, Color fallback) =>
            owner.TryFindResource(key) as Brush ?? Chart.Frozen(fallback);

        return new ChartInk
        {
            Square = owner.TryFindResource("Rad8") is CornerRadius corner && corner.TopLeft <= 0,
            Surface = Get(surfaceKey, Color.FromRgb(0x27, 0x28, 0x2C)),
            Primary = Get("Text", Color.FromRgb(0xEC, 0xED, 0xF1)),
            Secondary = Get("Muted", Color.FromRgb(0x9E, 0xA0, 0xA8)),
            Muted = Get("Faint", Color.FromRgb(0x6C, 0x6E, 0x77)),
            Grid = Get("BorderHairline", Color.FromRgb(0x23, 0x24, 0x28)),
            Axis = Get("BorderSoft", Color.FromRgb(0x2C, 0x2D, 0x32)),
            Overlay = Get("Overlay", Color.FromRgb(0x37, 0x38, 0x3E)),
            Accent = Get("Accent", Color.FromRgb(0x4C, 0x8D, 0xF5)),
        };
    }
}

/// <summary>What a usage chart is sliced by. Cost and tokens are the two money-and-volume readings of "what did
/// I use"; electricity and water are the physical twin of the same question - see <see cref="ModelEnergy"/>.
/// They are genuinely different rankings: a cheap model doing most of the turns leads on tokens, and a large
/// model answering a few of them leads on electricity.</summary>
public enum UsageMetric { Cost, Tokens, Energy, Water }

/// <summary>
/// How one <see cref="UsageMetric"/> reads a model row, a day, and its own axis. Keeping all four in one place
/// is what lets the donut and the trend line offer the same four choices without either restating the
/// formatting rules, and it is why adding a fifth metric later touches one type.
/// </summary>
internal static class Metrics
{
    public static double Of(UsageMetric metric, ModelUsage row) => metric switch
    {
        UsageMetric.Cost => row.CostUsd,
        UsageMetric.Tokens => row.Total,
        UsageMetric.Energy => row.EnergyWh,
        _ => row.WaterLitres,
    };

    public static double Of(UsageMetric metric, DayUsage day) => metric switch
    {
        UsageMetric.Cost => day.CostUsd,
        UsageMetric.Tokens => day.Total,
        UsageMetric.Energy => day.EnergyWh,
        _ => day.WaterLitres,
    };

    /// <summary>One value written against the scale its whole series shares, so an axis cannot mix Wh with kWh
    /// or mL with L.</summary>
    public static string Format(UsageMetric metric, double value, double scaleMax) => metric switch
    {
        UsageMetric.Cost => UsageAnalytics.MoneyScaled(value, scaleMax),
        UsageMetric.Tokens => UsageAnalytics.Tokens(value),
        UsageMetric.Energy => UsageAnalytics.EnergyScaled(value, scaleMax),
        _ => UsageAnalytics.WaterScaled(value, scaleMax),
    };

    /// <summary>The metric's name where it labels a total or a tooltip row.</summary>
    public static string Noun(UsageMetric metric) => metric switch
    {
        UsageMetric.Cost => "Cost",
        UsageMetric.Tokens => "Tokens",
        UsageMetric.Energy => "Electricity",
        _ => "Water",
    };

    /// <summary>What the donut writes under its centre total - lower case, because it sits under a number
    /// rather than starting a label.</summary>
    public static string CentreCaption(UsageMetric metric) => metric switch
    {
        UsageMetric.Cost => "total",
        UsageMetric.Tokens => "tokens",
        UsageMetric.Energy => "electricity",
        _ => "water",
    };

    /// <summary>Empty state, phrased for the metric so it reads as an answer rather than a fault.</summary>
    public static string Empty(UsageMetric metric) => metric switch
    {
        UsageMetric.Cost => "No spend in this range yet.",
        UsageMetric.Tokens => "No tokens in this range yet.",
        UsageMetric.Energy => "No electricity in this range yet.",
        _ => "No water in this range yet.",
    };
}

/// <summary>
/// Shared behaviour for the usage charts: the report they draw, the theme ink, and the hover layer.
/// A tooltip here always ENHANCES - every value it shows is also on an axis, a direct label, or the table view
/// below the charts - so nothing is reachable by hover alone.
/// </summary>
public abstract class UsageChartBase : FrameworkElement
{
    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(
        nameof(Report), typeof(UsageReport), typeof(UsageChartBase),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Which theme brush the chart sits on. The 2px separating gaps and the marker rings are painted in
    /// it, so it has to match the card the chart is actually inside.</summary>
    public static readonly DependencyProperty SurfaceKeyProperty = DependencyProperty.Register(
        nameof(SurfaceKey), typeof(string), typeof(UsageChartBase),
        new FrameworkPropertyMetadata("Bg2", FrameworkPropertyMetadataOptions.AffectsRender));

    public UsageReport? Report
    {
        get => (UsageReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    public string SurfaceKey
    {
        get => (string)GetValue(SurfaceKeyProperty);
        set => SetValue(SurfaceKeyProperty, value);
    }

    protected ChartText Label { get; private set; } = null!;
    protected int Hover { get; private set; } = -1;
    protected Point Pointer { get; private set; }

    protected UsageChartBase()
    {
        // A FrameworkElement with nothing painted under the cursor gets no mouse events, so every chart paints a
        // transparent backdrop over its own bounds in OnRender. Hit targets are then whole rows/columns rather
        // than the marks themselves, which keeps them far above the 24px minimum.
        Focusable = false;
        Label = null!;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Pointer = e.GetPosition(this);
        var index = HitTest(Pointer);
        if (index == Hover) { if (index >= 0) InvalidateVisual(); return; }
        Hover = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Hover < 0) return;
        Hover = -1;
        InvalidateVisual();
    }

    /// <summary>Index of the datum under the cursor, or -1. Rows/columns, never the mark's own pixels.</summary>
    protected abstract int HitTest(Point p);

    protected ChartInk Prepare(DrawingContext dc, Size size)
    {
        Label = new ChartText(this, TryFindResource("Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontWeights.Normal);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        return ChartInk.From(this, SurfaceKey);
    }

    /// <summary>Floating detail card, clamped inside the chart so it never renders half off the edge.</summary>
    protected void DrawTooltip(DrawingContext dc, ChartInk ink, Size size, string title, IReadOnlyList<(string Key, string Value)> rows,
        Color? swatch = null)
    {
        const double pad = 9, lineGap = 3, titleSize = 11.5, rowSize = 11, swatchSize = 8;
        var titleIndent = swatch is null ? 0 : swatchSize + 7;
        var width = Label.Width(title, titleSize) + titleIndent;
        var height = Label.Height(title, titleSize);
        foreach (var (key, value) in rows)
        {
            width = Math.Max(width, Label.Width(key + "   " + value, rowSize));
            height += lineGap + Label.Height(value, rowSize);
        }
        width += pad * 2;
        height += pad * 2;

        var x = Math.Min(Math.Max(6, Pointer.X + 14), Math.Max(6, size.Width - width - 6));
        var y = Math.Min(Math.Max(6, Pointer.Y - height - 12), Math.Max(6, size.Height - height - 6));
        var box = new Rect(x, y, width, height);

        var corner = ink.Radius(8);
        dc.DrawRoundedRectangle(ink.Overlay, Chart.Hairline(ink.Axis), box, corner, corner);

        var cursorY = box.Top + pad;
        if (swatch is { } colour)
        {
            var dotY = cursorY + Label.Height(title, titleSize) / 2;
            dc.DrawRoundedRectangle(Chart.Frozen(colour), null,
                new Rect(box.Left + pad, dotY - swatchSize / 2, swatchSize, swatchSize), ink.Radius(2), ink.Radius(2));
        }
        Label.Draw(dc, title, titleSize, ink.Primary, box.Left + pad + titleIndent, cursorY);
        cursorY += Label.Height(title, titleSize);
        foreach (var (key, value) in rows)
        {
            cursorY += lineGap;
            Label.Draw(dc, key, rowSize, ink.Secondary, box.Left + pad, cursorY);
            var ft = Label.Make(value, rowSize, ink.Primary);
            dc.DrawText(ft, new Point(box.Right - pad - ft.Width, cursorY));
            cursorY += Label.Height(value, rowSize);
        }
    }

    protected void DrawEmpty(DrawingContext dc, ChartInk ink, Size size, string message)
    {
        var ft = Label.Make(message, 11.5, ink.Muted);
        dc.DrawText(ft, new Point((size.Width - ft.Width) / 2, (size.Height - ft.Height) / 2));
    }
}

/// <summary>
/// Tokens in and out for every model in the window: one row per model, two thin bars per row.
///
/// Hue is the model's own fixed slot and nothing else - direction is carried by which bar of the pair it is
/// (labelled "in" / "out" in the gutter), so filtering the window can never repaint a model. Both bars sit on
/// ONE token axis; input and output are the same unit, so there is no second scale anywhere.
/// </summary>
public sealed class ModelTokenBarChart : UsageChartBase
{
    private const double RowHeight = 46;
    private const double HeaderHeight = 22;
    /// <summary>MINIMUM width of the name gutter. The real width is measured per render from the widest name
    /// the chart has to draw, so the column can grow in a wide font instead of ellipsizing the name.</summary>
    private const double NameColumn = 104;
    private const double ValueColumn = 62;

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Report?.Models.Count ?? 0;
        var height = HeaderHeight + Math.Max(1, rows) * RowHeight + 6;
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        return new Size(width, height);
    }

    protected override int HitTest(Point p)
    {
        var rows = Report?.Models.Count ?? 0;
        if (rows == 0 || p.Y < HeaderHeight) return -1;
        var index = (int)((p.Y - HeaderHeight) / RowHeight);
        return index >= 0 && index < rows ? index : -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var ink = Prepare(dc, size);
        var report = Report;
        if (report is null || report.Models.Count == 0)
        {
            DrawEmpty(dc, ink, size, "No model activity in this range yet.");
            return;
        }

        var models = report.Models;
        var max = Chart.NiceCeiling(models.Max(m => Math.Max(m.TotalIn, m.Output)));

        // The "in" / "out" row labels are right-aligned to plotLeft - axisGap, and the name used to be drawn to
        // that SAME right edge, so a long name ran straight over the "in". Ending the name column short of the
        // labels fixed the overlap but traded it for silent truncation: at a FIXED 104px column, CLI's mono face
        // is wide enough that "GPT-5.6 Sol" and "Sonnet 4.6" ellipsized there while Dark looked fine - which is
        // how a one-theme check passes and ships a regression.
        //
        // So the column GROWS to the widest name it actually has to draw, measured in whichever font the live
        // theme resolved, instead of the name shrinking to fit a constant. NameColumn is now the FLOOR, not the
        // width, and the upper clamp keeps a pathological model id from eating the plot it is supposed to label.
        // nameSlack exists because nameWidth is derived from widestName, so without it the widest name fits its
        // box with EXACTLY zero headroom and correctness rests on Label.Width() and DrawLeft's MaxTextWidth
        // agreeing to the last sub-pixel. They do today; a font-metric rounding change would ellipsize a name
        // that measures as fitting.
        const double nameLeft = 16, directionGap = 6, axisGap = 8, nameSlack = 1;
        var directionWidth = Math.Max(Label.Width("in", 9.5), Label.Width("out", 9.5));
        var widestName = models.Max(m => Math.Max(
            Label.Width(m.Display, 11.5),
            Label.Width(m.Turns == 1 ? "1 turn" : $"{m.Turns:N0} turns", 9.5)));
        var plotLeft = Math.Clamp(nameLeft + widestName + nameSlack + directionGap + directionWidth + axisGap,
            NameColumn, Math.Max(NameColumn, size.Width * 0.45));
        var nameWidth = Math.Max(24, plotLeft - axisGap - directionWidth - directionGap - nameLeft);

        var plotRight = Math.Max(plotLeft + 40, size.Width - ValueColumn);
        var plotWidth = plotRight - plotLeft;

        // Axis ticks carry every value the bars are not directly labelled with.
        var grid = Chart.Hairline(ink.Grid);
        for (var t = 0; t <= 2; t++)
        {
            var value = max * t / 2.0;
            var x = plotLeft + plotWidth * t / 2.0;
            Chart.Rule(dc, grid, x, HeaderHeight - 4, x, size.Height - 6);
            Label.DrawCentred(dc, t == 0 ? "0" : UsageAnalytics.Tokens(value), 9.5, ink.Muted, x, 2);
        }

        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var top = HeaderHeight + i * RowHeight;
            var fill = Chart.Frozen(model.Color);
            var isHover = i == Hover;

            if (isHover)
                dc.DrawRoundedRectangle(Chart.Frozen(model.Color, 0.07), null,
                    new Rect(0, top + 1, size.Width, RowHeight - 2), ink.Radius(8), ink.Radius(8));

            // Identity: a swatch beside the name. The text itself stays in ink tokens, never the series hue.
            var nameY = top + RowHeight / 2 - 8;
            dc.DrawRoundedRectangle(fill, null, new Rect(2, nameY - 4, 8, 8), ink.Radius(2), ink.Radius(2));
            Label.DrawLeft(dc, model.Display, 11.5, ink.Primary, 16, nameY, nameWidth);
            Label.DrawLeft(dc, model.Turns == 1 ? "1 turn" : $"{model.Turns:N0} turns", 9.5, ink.Muted,
                16, top + RowHeight / 2 + 9, nameWidth);

            // Two bars, a surface gap apart. "in" is every input-side token whatever cache tier billed it.
            DrawBar(dc, ink, fill, "in", model.TotalIn, max, plotLeft, plotWidth,
                top + RowHeight / 2 - Chart.BarThickness / 2 - Chart.SurfaceGap / 2 - 5, plotRight, size.Width);
            DrawBar(dc, ink, fill, "out", model.Output, max, plotLeft, plotWidth,
                top + RowHeight / 2 + Chart.SurfaceGap / 2 + 1, plotRight, size.Width);
        }

        Chart.Rule(dc, Chart.Hairline(ink.Axis), plotLeft, HeaderHeight - 4, plotLeft, size.Height - 6);

        if (Hover >= 0 && Hover < models.Count)
        {
            var m = models[Hover];
            DrawTooltip(dc, ink, size, m.Display, new (string, string)[]
            {
                ("Input", UsageAnalytics.Tokens(m.TotalIn)),
                ("  of which cached", UsageAnalytics.Tokens(m.CacheRead)),
                ("Output", UsageAnalytics.Tokens(m.Output)),
                ("Turns", m.Turns.ToString("N0")),
                (m.FullyPriced ? "Cost" : "Cost (estimated)", UsageAnalytics.Money(m.CostUsd)),
            }, m.Color);
        }
    }

    private void DrawBar(DrawingContext dc, ChartInk ink, Brush fill, string key, double value, double max,
        double left, double width, double top, double plotRight, double totalWidth)
    {
        var barHeight = (Chart.BarThickness - Chart.SurfaceGap) / 2 + 3;
        var length = max > 0 ? Math.Max(value > 0 ? 3 : 0, width * (value / max)) : 0;
        var centreY = top + barHeight / 2;

        Label.DrawRight(dc, key, 9.5, ink.Muted, left - 8, centreY);
        if (length > 0)
            dc.DrawGeometry(fill, null, Chart.Bar(new Rect(left, top, length, barHeight), Chart.Direction.Right,
                ink.Radius(Chart.DataEndRadius)));

        // Value at the tip - the point of this chart is the two numbers, so both are labelled rather than
        // leaving either to the tooltip alone.
        Label.DrawLeft(dc, UsageAnalytics.Tokens(value), 10, ink.Secondary,
            Math.Min(left + length + 7, totalWidth - 4), centreY, Math.Max(10, totalWidth - (plotRight + 4)));
    }
}

/// <summary>
/// Share of spend by model, as a donut. Part-to-whole at a glance only: the ring is capped at six slices plus a
/// folded "Other", and the exact figures live in the legend beside it and the table below, never in the ring.
/// The centre carries the window total, which is the number the card is really about.
/// </summary>
public sealed class ModelShareDonut : UsageChartBase
{
    /// <summary>Slices past this fold into one "Other" wedge. A donut stops being readable well before this.</summary>
    private const int MaxSlices = 6;

    private sealed record Slice(string Display, double Value, Color Color, double StartDeg, double SweepDeg, bool IsOther);

    private List<Slice> _slices = new();
    private Point _centre;
    private double _outer, _inner;

    /// <summary>Which reading the ring is sliced by. All four are legitimate answers to "which model do I use
    /// most", and they differ a lot: a cheap model can take the bulk of the turns while a large one takes the
    /// bulk of the electricity.</summary>
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(UsageMetric), typeof(ModelShareDonut),
        new FrameworkPropertyMetadata(UsageMetric.Cost, FrameworkPropertyMetadataOptions.AffectsRender));

    public UsageMetric Metric
    {
        get => (UsageMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width, 200);

    protected override int HitTest(Point p)
    {
        if (_slices.Count == 0) return -1;
        var dx = p.X - _centre.X;
        var dy = p.Y - _centre.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        // The hit ring is deliberately wider than the drawn ring so the target clears the 24px minimum.
        if (distance < _inner - 10 || distance > _outer + 10) return -1;
        var degrees = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        for (var i = 0; i < _slices.Count; i++)
            if (degrees >= _slices[i].StartDeg && degrees < _slices[i].StartDeg + _slices[i].SweepDeg) return i;
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var ink = Prepare(dc, size);
        var report = Report;
        _slices = new List<Slice>();

        var metric = Metric;
        double Measure(ModelUsage m) => Metrics.Of(metric, m);
        var total = report is null ? 0 : report.Models.Sum(Measure);
        if (report is null || total <= 0)
        {
            DrawEmpty(dc, ink, size, Metrics.Empty(metric));
            return;
        }

        var ranked = report.Models.Where(m => Measure(m) > 0).OrderByDescending(Measure).ToList();
        var shown = ranked.Take(MaxSlices).ToList();
        var tail = ranked.Skip(MaxSlices).ToList();

        var angle = 0.0;
        foreach (var m in shown)
        {
            var sweep = Measure(m) / total * 360;
            _slices.Add(new Slice(m.Display, Measure(m), m.Color, angle, sweep, false));
            angle += sweep;
        }
        if (tail.Count > 0)
        {
            var value = tail.Sum(Measure);
            _slices.Add(new Slice($"Other ({tail.Count})", value, Color.FromRgb(0x8A, 0x8C, 0x95), angle,
                Math.Max(0, 360 - angle), true));
        }

        _outer = Math.Min(size.Height, size.Width) / 2 - 6;
        _inner = _outer * 0.62;
        _centre = new Point(_outer + 6, size.Height / 2);

        for (var i = 0; i < _slices.Count; i++)
        {
            var slice = _slices[i];
            // Hover lifts a slice by growing it outward - never by recolouring it.
            var outer = i == Hover ? _outer + 4 : _outer;
            dc.DrawGeometry(Chart.Frozen(slice.Color), null,
                Chart.DonutSegment(_centre, outer, _inner, slice.StartDeg, slice.SweepDeg));
        }

        // Centre: the window total. One number, in ink, doing the job a slice label would do badly.
        var headline = Metrics.Format(metric, total, total);
        var headlineSize = headline.Length > 8 ? 17.0 : 21.0;
        var headlineHeight = Label.Height(headline, headlineSize);
        Label.DrawCentred(dc, headline, headlineSize, ink.Primary, _centre.X, _centre.Y - headlineHeight / 2 - 6);
        Label.DrawCentred(dc, Metrics.CentreCaption(metric), 10, ink.Muted, _centre.X, _centre.Y + headlineHeight / 2 - 4);

        // Legend - always present, because two or more series must never rely on colour matching alone.
        var legendLeft = _centre.X + _outer + 18;
        if (legendLeft < size.Width - 60)
        {
            var rowHeight = Math.Min(21, (size.Height - 8) / Math.Max(1, _slices.Count));
            var top = (size.Height - rowHeight * _slices.Count) / 2;
            for (var i = 0; i < _slices.Count; i++)
            {
                var slice = _slices[i];
                var centreY = top + rowHeight * i + rowHeight / 2;
                dc.DrawRoundedRectangle(Chart.Frozen(slice.Color), null,
                    new Rect(legendLeft, centreY - 4, 8, 8), ink.Radius(2), ink.Radius(2));
                var share = UsageAnalytics.Percent(slice.Value / total);
                var shareWidth = Label.Width(share, 10.5);
                Label.DrawRight(dc, share, 10.5, i == Hover ? ink.Primary : ink.Secondary, size.Width - 2, centreY);
                Label.DrawLeft(dc, slice.Display, 11, i == Hover ? ink.Primary : ink.Secondary,
                    legendLeft + 14, centreY, Math.Max(10, size.Width - legendLeft - 22 - shareWidth));
            }
        }

        if (Hover >= 0 && Hover < _slices.Count)
        {
            var slice = _slices[Hover];
            DrawTooltip(dc, ink, size, slice.Display, new (string, string)[]
            {
                (Metrics.Noun(metric), Metrics.Format(metric, slice.Value, total)),
                ("Share", UsageAnalytics.Percent(slice.Value / total)),
            }, slice.Color);
        }
    }
}

/// <summary>
/// One measure per day across the window: a 2px line over a 10% wash, with a crosshair and a value readout on
/// hover. One series, so no legend box - the card title already says what is plotted - and the end point is the
/// only direct label, because a number on every day would be unreadable at thirty points.
/// </summary>
public sealed class DailyTrend : UsageChartBase
{
    private const double AxisHeight = 18;
    private const double LeftGutter = 46;

    /// <summary>Which measure is plotted. Exactly one at a time: two measures of different scale on one plot
    /// would need a second y-axis, which invents a correlation that is not in the data. That is also why the
    /// footprint card below carries a second instance of this chart rather than overlaying water on spend.</summary>
    public static readonly DependencyProperty MetricProperty = DependencyProperty.Register(
        nameof(Metric), typeof(UsageMetric), typeof(DailyTrend),
        new FrameworkPropertyMetadata(UsageMetric.Cost, FrameworkPropertyMetadataOptions.AffectsRender));

    public UsageMetric Metric
    {
        get => (UsageMetric)GetValue(MetricProperty);
        set => SetValue(MetricProperty, value);
    }

    private double _plotLeft, _plotRight;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width, 168);

    protected override int HitTest(Point p)
    {
        var days = Report?.Days;
        if (days is null || days.Count == 0) return -1;
        if (p.X < _plotLeft - 12 || p.X > _plotRight + 12) return -1;
        if (days.Count == 1) return 0;
        var step = (_plotRight - _plotLeft) / (days.Count - 1);
        var index = (int)Math.Round((p.X - _plotLeft) / Math.Max(0.001, step));
        return Math.Clamp(index, 0, days.Count - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var ink = Prepare(dc, size);
        var days = Report?.Days;
        if (days is null || days.Count == 0)
        {
            DrawEmpty(dc, ink, size, "No history in this range yet.");
            return;
        }

        var metric = Metric;
        double Measure(DayUsage d) => Metrics.Of(metric, d);
        var max = Chart.NiceCeiling(days.Max(Measure));
        // One format for the whole scale, so the ticks read $0.00 / $5.00 / $10.00 rather than $0 / $5.000 / $10.00.
        string Format(double v) => Metrics.Format(metric, v, max);

        _plotLeft = LeftGutter;
        // Reserve the right gutter the end label needs. Without it the label ran under the end marker and off
        // the card edge - the plot has to be sized to include its own labels, not just its marks.
        // Size it to the WIDER of the largest axis tick and the end label itself: for money those match, but
        // "10.59 kWh" is three characters longer than the "20 kWh" tick above it and was being clipped.
        var endLabel = Format(Measure(days[days.Count - 1]));
        _plotRight = size.Width - Math.Max(14, Math.Max(Label.Width(Format(max), 10.5), Label.Width(endLabel, 10.5)) + 18);
        var plotTop = 10.0;
        var plotBottom = size.Height - AxisHeight;
        var step = days.Count > 1 ? (_plotRight - _plotLeft) / (days.Count - 1) : 0;

        double X(int i) => days.Count > 1 ? _plotLeft + step * i : (_plotLeft + _plotRight) / 2;
        double Y(double v) => plotBottom - (plotBottom - plotTop) * (max > 0 ? v / max : 0);

        // Hairline solid gridlines, one step off the surface, with round-number ticks.
        var grid = Chart.Hairline(ink.Grid);
        for (var t = 0; t <= 2; t++)
        {
            var value = max * t / 2.0;
            var y = Y(value);
            Chart.Rule(dc, grid, _plotLeft, y, _plotRight, y);
            Label.DrawRight(dc, t == 0 ? Format(0) : Format(value), 9.5, ink.Muted, _plotLeft - 8, y);
        }

        // Electricity and water wear the same hues here that they wear on the HUD's tiles, so a measure keeps one
        // colour across the app. Resolved from the theme so CLI mode gets its own pair.
        var seriesKey = metric switch
        {
            UsageMetric.Energy => "Amber",
            UsageMetric.Water => "Green",
            _ => "Accent",
        };
        var seriesColor = (TryFindResource(seriesKey) as SolidColorBrush)?.Color
                          ?? (ink.Accent as SolidColorBrush)?.Color
                          ?? Color.FromRgb(0x4C, 0x8D, 0xF5);
        var line = Chart.Series(Chart.Frozen(seriesColor));

        // Area wash first, then the line on top of it.
        var area = new StreamGeometry();
        using (var c = area.Open())
        {
            c.BeginFigure(new Point(X(0), plotBottom), isFilled: true, isClosed: true);
            for (var i = 0; i < days.Count; i++) c.LineTo(new Point(X(i), Y(Measure(days[i]))), true, false);
            c.LineTo(new Point(X(days.Count - 1), plotBottom), true, false);
        }
        area.Freeze();
        dc.DrawGeometry(Chart.Frozen(seriesColor, Chart.AreaFillOpacity), null, area);

        if (days.Count > 1)
        {
            var path = new StreamGeometry();
            using (var c = path.Open())
            {
                c.BeginFigure(new Point(X(0), Y(Measure(days[0]))), isFilled: false, isClosed: false);
                for (var i = 1; i < days.Count; i++) c.LineTo(new Point(X(i), Y(Measure(days[i]))), true, false);
            }
            path.Freeze();
            dc.DrawGeometry(null, line, path);
        }

        Chart.Rule(dc, Chart.Hairline(ink.Axis), _plotLeft, plotBottom, _plotRight, plotBottom);

        // Date ticks: first, last, and the middle - enough to orient without crowding thirty labels together.
        var ticks = days.Count <= 2 ? new[] { 0, days.Count - 1 } : new[] { 0, days.Count / 2, days.Count - 1 };
        foreach (var i in ticks.Distinct())
        {
            var text = days[i].Day.ToString("MMM d");
            var x = Math.Clamp(X(i), _plotLeft + Label.Width(text, 9.5) / 2, _plotRight - Label.Width(text, 9.5) / 2);
            Label.DrawCentred(dc, text, 9.5, ink.Muted, x, plotBottom + 4);
        }

        // The end point is the one direct label: the current value is what the card is about.
        var lastIndex = days.Count - 1;
        var lastPoint = new Point(X(lastIndex), Y(Measure(days[lastIndex])));
        Chart.Marker(dc, lastPoint, Chart.Frozen(seriesColor), ink.Surface);

        if (Hover >= 0 && Hover < days.Count)
        {
            var day = days[Hover];
            var at = new Point(X(Hover), Y(Measure(day)));
            Chart.Rule(dc, Chart.Hairline(ink.Axis), at.X, plotTop - 4, at.X, plotBottom);
            Chart.Marker(dc, at, Chart.Frozen(seriesColor), ink.Surface);
            // The plotted measure leads, then the context every reading wants. Cost is skipped when it IS the
            // plotted measure rather than repeated two rows apart.
            var rows = new List<(string, string)> { (Metrics.Noun(metric), Metrics.Format(metric, Measure(day), max)) };
            if (metric != UsageMetric.Cost) rows.Add(("Cost", UsageAnalytics.Money(day.CostUsd)));
            rows.Add(("Input", UsageAnalytics.Tokens(day.TokensIn)));
            rows.Add(("Output", UsageAnalytics.Tokens(day.TokensOut)));
            DrawTooltip(dc, ink, size, day.Day.ToString("ddd, MMM d"), rows, seriesColor);
        }
        else
        {
            // The gutter reserved above guarantees this fits, so it never needs clamping back over the marker.
            Label.DrawLeft(dc, Format(Measure(days[lastIndex])), 10.5, ink.Secondary, lastPoint.X + 10, lastPoint.Y);
        }
    }
}
