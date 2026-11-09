using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// A WrapPanel for composer footers whose status group would rather say less than wrap. The child marked
/// <see cref="ShortensToFitProperty"/> is laid out in its full form first; when that would not fit on the line it starts
/// on, the panel steps down through <see cref="FitProperty"/> levels - 1 is the short wording, 2 also drops the cost -
/// and keeps the first that fits. The group's descendants read the inherited <see cref="FitProperty"/> in their styles.
/// </summary>
public sealed class StatusWrapPanel : WrapPanel
{
    public const int ShortWording = 1;
    public const int WithoutCost = 2;

    public static readonly DependencyProperty ShortensToFitProperty = DependencyProperty.RegisterAttached(
        "ShortensToFit", typeof(bool), typeof(StatusWrapPanel), new PropertyMetadata(false));

    public static bool GetShortensToFit(DependencyObject element) => (bool)element.GetValue(ShortensToFitProperty);
    public static void SetShortensToFit(DependencyObject element, bool value) => element.SetValue(ShortensToFitProperty, value);

    public static readonly DependencyProperty FitProperty = DependencyProperty.RegisterAttached(
        "Fit", typeof(int), typeof(StatusWrapPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));

    public static int GetFit(DependencyObject element) => (int)element.GetValue(FitProperty);
    public static void SetFit(DependencyObject element, int value) => element.SetValue(FitProperty, value);

    protected override Size MeasureOverride(Size constraint)
    {
        var group = InternalChildren.OfType<UIElement>().FirstOrDefault(GetShortensToFit);
        if (group is null || Orientation != Orientation.Horizontal || double.IsInfinity(constraint.Width))
            return base.MeasureOverride(constraint);
        // Every pass starts from the full form, so a footer that has grown wider shows the whole sentence again.
        for (var fit = 0; ; fit++)
        {
            if (GetFit(group) != fit)
            {
                SetFit(group, fit);
                // The wording changes inside the group; its own measure is still cached, so it must be redone now.
                InvalidateMeasureBelow(group);
            }
            var size = base.MeasureOverride(constraint);
            if (fit == WithoutCost || FitsOnItsLine(group, constraint.Width)) return size;
        }
    }

    /// <summary>Replays WrapPanel's own line breaking with the children's fresh desired sizes and reports whether
    /// <paramref name="group"/> fits on the line it would start on.</summary>
    private bool FitsOnItsLine(UIElement group, double width)
    {
        var line = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (child is null) continue;
            var w = double.IsNaN(ItemWidth) ? child.DesiredSize.Width : ItemWidth;
            var breaks = GreaterThan(line + w, width);
            if (ReferenceEquals(child, group)) return !breaks;
            // A child wider than the whole line gets a line to itself; the next one starts fresh.
            line = !breaks ? line + w : GreaterThan(w, width) ? 0 : w;
        }
        return true;
    }

    /// <summary>WrapPanel's comparison (DoubleUtil.GreaterThan), so this panel breaks lines exactly where it does.</summary>
    private static bool GreaterThan(double a, double b)
    {
        if (a <= b) return false;
        var epsilon = (Math.Abs(a) + Math.Abs(b) + 10.0) * 2.2204460492503131e-016;
        return a - b >= epsilon;
    }

    private static void InvalidateMeasureBelow(DependencyObject element)
    {
        if (element is UIElement ui) ui.InvalidateMeasure();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            InvalidateMeasureBelow(VisualTreeHelper.GetChild(element, i));
    }
}
