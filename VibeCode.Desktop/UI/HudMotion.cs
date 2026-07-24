using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The one switch and the one set of timings behind everything that moves on the telemetry HUD and wall.
///
/// Shared rather than per-control on purpose: a dashboard where half the panels travel to a new reading and
/// half snap to it does not look half-animated, it looks broken. The motion is presentation only - every
/// animated value here settles on the real reading, and a series that is flat at zero is drawn flat at zero.
/// Nothing on these windows is allowed to look busier than the data it is drawn from.
/// </summary>
internal static class HudMotion
{
    public static bool Enabled => AppSettings.Current.TelemetryLiveAnimation;

    /// <summary>Long enough to read as travel, short enough that the true figure is on screen well before
    /// anyone has finished looking at it.</summary>
    public static readonly Duration Morph = new(TimeSpan.FromMilliseconds(650));

    /// <summary>Period of the idle ripple. Slow on purpose: this is a window left in the corner of someone's
    /// eye for hours, and anything quicker turns into something to look away from.</summary>
    public static readonly Duration Ripple = new(TimeSpan.FromSeconds(7));

    /// <summary>Repainting a decorative wave at the full refresh rate is not worth the battery on a window
    /// whose whole point is being left running. At this amplitude 30 reads as continuous.</summary>
    public const int RippleFrameRate = 30;

    /// <summary>Ease out, always: a value that decelerates into place looks like it arrived somewhere, a
    /// linear one looks like it was still going when the clock ran out.</summary>
    public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static double Lerp(double from, double to, double t) => from + (to - from) * t;
}

/// <summary>
/// Counts a headline figure from the reading it was showing to the one that just arrived.
///
/// The datum is not touched: the animation is driven towards the exact value it was handed and the last thing
/// written is <c>format(value)</c> for that value, not whatever the final frame happened to round to. What it
/// buys is that a number changing is visible from across the room, which on a window built to be glanced at
/// from a distance is the difference between telemetry and wallpaper.
/// </summary>
internal static class RollingNumber
{
    /// <summary>The figure currently being drawn. Animated; the local value underneath it is always the true
    /// reading, which is what the property falls back to the moment the clock stops.</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(double), typeof(RollingNumber), new PropertyMetadata(0.0, OnShownChanged));

    private static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(Func<double, string>), typeof(RollingNumber), new PropertyMetadata(null));

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text && d.GetValue(FormatProperty) is Func<double, string> format)
            text.Text = format((double)e.NewValue);
    }

    public static void Set(TextBlock target, double value, Func<double, string> format)
    {
        target.SetValue(FormatProperty, format);
        var from = (double)target.GetValue(ShownProperty);

        if (!HudMotion.Enabled || double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value - from) < 1e-9)
        {
            target.BeginAnimation(ShownProperty, null);
            target.SetValue(ShownProperty, value);
            target.Text = format(value);
            return;
        }

        target.BeginAnimation(ShownProperty, new DoubleAnimation(from, value, HudMotion.Morph)
        {
            EasingFunction = HudMotion.Ease,
            // Stop rather than hold, so when the clock finishes the property drops back to the local value set
            // below - the exact reading - instead of holding the last interpolated frame forever.
            FillBehavior = FillBehavior.Stop,
        });
        target.SetValue(ShownProperty, value);
    }
}
