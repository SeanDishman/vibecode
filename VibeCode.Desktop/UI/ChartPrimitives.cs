using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Shared drawing kit for the usage charts. Everything here encodes one of the fixed mark specs the charts are
/// held to, so a spec lives in exactly one place:
///   bars capped at 24px with a 4px rounded data-end and a square baseline end - lines 2px, round join/cap -
///   markers >= 8px carrying a 2px surface ring - a 2px SURFACE-COLOURED gap doing every separation (never a
///   stroke drawn around a mark) - grid and axis rules hairline, solid, one step off the surface.
/// </summary>
internal static class Chart
{
    public const double BarThickness = 14;      // <= 24px cap; the band's leftover is deliberate air
    public const double DataEndRadius = 4;
    public const double SurfaceGap = 2;
    public const double LineThickness = 2;
    public const double MarkerRadius = 4.5;     // >= 8px diameter
    public const double AreaFillOpacity = 0.10;

    /// <summary>A bar/column with its data end rounded and its baseline end square, per the mark spec.
    /// <paramref name="grow"/> is the direction the bar grows away from its baseline.</summary>
    public static Geometry Bar(Rect r, Direction grow, double radius = DataEndRadius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        if (radius <= 0.5) return new RectangleGeometry(r);

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            var s = new Size(radius, radius);
            switch (grow)
            {
                case Direction.Right:
                    c.BeginFigure(new Point(r.Left, r.Top), isFilled: true, isClosed: true);
                    c.LineTo(new Point(r.Right - radius, r.Top), true, false);
                    c.ArcTo(new Point(r.Right, r.Top + radius), s, 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(r.Right, r.Bottom - radius), true, false);
                    c.ArcTo(new Point(r.Right - radius, r.Bottom), s, 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(r.Left, r.Bottom), true, false);
                    break;
                case Direction.Up:
                    c.BeginFigure(new Point(r.Left, r.Bottom), isFilled: true, isClosed: true);
                    c.LineTo(new Point(r.Left, r.Top + radius), true, false);
                    c.ArcTo(new Point(r.Left + radius, r.Top), s, 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(r.Right - radius, r.Top), true, false);
                    c.ArcTo(new Point(r.Right, r.Top + radius), s, 0, false, SweepDirection.Clockwise, true, false);
                    c.LineTo(new Point(r.Right, r.Bottom), true, false);
                    break;
                default:   // Left
                    c.BeginFigure(new Point(r.Right, r.Top), isFilled: true, isClosed: true);
                    c.LineTo(new Point(r.Left + radius, r.Top), true, false);
                    c.ArcTo(new Point(r.Left, r.Top + radius), s, 0, false, SweepDirection.Counterclockwise, true, false);
                    c.LineTo(new Point(r.Left, r.Bottom - radius), true, false);
                    c.ArcTo(new Point(r.Left + radius, r.Bottom), s, 0, false, SweepDirection.Counterclockwise, true, false);
                    c.LineTo(new Point(r.Right, r.Bottom), true, false);
                    break;
            }
        }
        g.Freeze();
        return g;
    }

    public enum Direction { Right, Left, Up }

    /// <summary>One ring segment of a donut, already inset by the 2px surface gap on both ends. Angles are
    /// degrees clockwise from 12 o'clock.</summary>
    public static Geometry DonutSegment(Point centre, double outer, double inner, double startDeg, double sweepDeg)
    {
        // Convert the 2px surface gap into the angle it occupies at this radius, then take half off each end.
        var gapDeg = outer > 0 ? SurfaceGap / outer * (180 / Math.PI) : 0;
        var a0 = startDeg + gapDeg / 2;
        var a1 = startDeg + sweepDeg - gapDeg / 2;
        if (a1 <= a0) { a0 = startDeg; a1 = startDeg + sweepDeg; }   // slice too thin to gap; keep it visible
        var sweep = a1 - a0;

        var p0Out = OnCircle(centre, outer, a0);
        var p1Out = OnCircle(centre, outer, a1);
        var p1In = OnCircle(centre, inner, a1);
        var p0In = OnCircle(centre, inner, a0);
        var large = sweep > 180;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(p0Out, isFilled: true, isClosed: true);
            c.ArcTo(p1Out, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            c.LineTo(p1In, true, false);
            c.ArcTo(p0In, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    public static Point OnCircle(Point centre, double radius, double degreesFromTop)
    {
        var rad = (degreesFromTop - 90) * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(rad), centre.Y + radius * Math.Sin(rad));
    }

    /// <summary>A dot with the 2px surface ring that keeps it legible where it crosses a line or another dot.
    /// The ring is drawn as a wider circle in the surface colour underneath, never as a stroke on the mark.</summary>
    public static void Marker(DrawingContext dc, Point at, Brush fill, Brush surface, double radius = MarkerRadius)
    {
        dc.DrawEllipse(surface, null, at, radius + SurfaceGap, radius + SurfaceGap);
        dc.DrawEllipse(fill, null, at, radius, radius);
    }

    /// <summary>Hairline solid rule, snapped to the pixel grid so it stays one physical pixel instead of a
    /// blurry two. Never dashed: dashing reads as "threshold" when it is only a grid.</summary>
    public static void Rule(DrawingContext dc, Pen pen, double x0, double y0, double x1, double y1)
    {
        if (Math.Abs(y0 - y1) < 0.01) { y0 = y1 = Math.Round(y0) + 0.5; }
        if (Math.Abs(x0 - x1) < 0.01) { x0 = x1 = Math.Round(x0) + 0.5; }
        dc.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
    }

    public static Pen Hairline(Brush brush)
    {
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }

    public static Pen Series(Brush brush)
    {
        var pen = new Pen(brush, LineThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    public static Brush Frozen(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>Round an axis maximum up to a clean 1 / 2 / 2.5 / 5 x 10^n so the ticks read as round numbers.</summary>
    public static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
        return step * magnitude;
    }
}

/// <summary>Text measured and drawn with the chart's own ink tokens. Labels never wear a series colour: hue
/// belongs to the marks, and identity reaches the text through the swatch beside it.</summary>
public sealed class ChartText
{
    private readonly Typeface _face;
    private readonly double _dpi;

    public ChartText(FrameworkElement owner, FontFamily family, FontWeight weight)
    {
        _face = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        _dpi = VisualTreeHelper.GetDpi(owner).PixelsPerDip;
    }

    public FormattedText Make(string text, double size, Brush ink) =>
        new(text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, size, ink, _dpi)
        {
            TextAlignment = TextAlignment.Left,
        };

    public void Draw(DrawingContext dc, string text, double size, Brush ink, double x, double y) =>
        dc.DrawText(Make(text, size, ink), new Point(x, y));

    /// <summary>Draw right-aligned to <paramref name="right"/>, vertically centred on <paramref name="centreY"/>.</summary>
    public void DrawRight(DrawingContext dc, string text, double size, Brush ink, double right, double centreY)
    {
        var ft = Make(text, size, ink);
        dc.DrawText(ft, new Point(right - ft.Width, centreY - ft.Height / 2));
    }

    /// <summary>Draw left-aligned at <paramref name="left"/>, vertically centred on <paramref name="centreY"/>,
    /// trimmed with an ellipsis if it would exceed <paramref name="maxWidth"/>.</summary>
    public void DrawLeft(DrawingContext dc, string text, double size, Brush ink, double left, double centreY,
        double maxWidth = double.PositiveInfinity)
    {
        var ft = Make(text, size, ink);
        if (!double.IsPositiveInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        dc.DrawText(ft, new Point(left, centreY - ft.Height / 2));
    }

    public void DrawCentred(DrawingContext dc, string text, double size, Brush ink, double centreX, double top)
    {
        var ft = Make(text, size, ink);
        dc.DrawText(ft, new Point(centreX - ft.Width / 2, top));
    }

    public double Width(string text, double size) => Make(text, size, Brushes.Black).Width;
    public double Height(string text, double size) => Make(text, size, Brushes.Black).Height;
}
