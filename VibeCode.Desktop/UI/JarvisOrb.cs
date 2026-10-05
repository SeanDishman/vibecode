using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeCode.UI;

/// <summary>A slowly orbiting field of dots. Input response is driven by measured microphone amplitude.</summary>
public sealed class JarvisOrb : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(JarvisOrb),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double), typeof(JarvisOrb),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AudioLevelProperty = DependencyProperty.Register(nameof(AudioLevel), typeof(double), typeof(JarvisOrb),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public double AudioLevel { get => (double)GetValue(AudioLevelProperty); set => SetValue(AudioLevelProperty, value); }
    private static readonly Brush[] Blue = BrushesFor(Color.FromRgb(153, 203, 255));
    private static readonly Brush[] Red = BrushesFor(Color.FromRgb(250, 164, 157));

    public JarvisOrb()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => RefreshClock();
        Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
        IsVisibleChanged += (_, _) => RefreshClock();
    }

    private static Brush[] BrushesFor(Color color) => Enumerable.Range(0, 16).Select(i =>
    {
        var brush = new SolidColorBrush(color) { Opacity = .12 + i / 15.0 * .88 };
        brush.Freeze(); return (Brush)brush;
    }).ToArray();

    private void RefreshClock()
    {
        BeginAnimation(PhaseProperty, null);
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(80)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 24);
        BeginAnimation(PhaseProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var palette = State == "error" ? Red : Blue;
        var phase = Phase * Math.PI * 2;
        var level = State == "listening" && double.IsFinite(AudioLevel) ? Math.Clamp(AudioLevel, 0, 1) : 0;
        // Fewer points preserve the same silhouette in VibeCode's 30px launcher.
        var bands = size < 100 ? 3 : 9;
        var points = size < 100 ? 28 : 112;
        for (var band = 0; band < bands; band++)
        {
            var depth = bands == 1 ? 0 : band / (double)(bands - 1);
            for (var i = 0; i < points; i++)
            {
                var theta = i * Math.PI * 2 / points + phase * (band % 2 == 0 ? 1 : -.65) + band * .063;
                var drift = Math.Sin(theta * 3 + phase * 2 + band * .6) * .006
                    + Math.Cos(theta * 7 - phase * 3 + band) * .003;
                var radius = size * (.325 + depth * .08 + drift + level * .022 * Math.Sin(theta * 5 + band));
                var prominence = Math.Sin(depth * Math.PI) * .50 + .28
                    + .17 * Math.Sin(theta * 2 - phase + depth * 4);
                var brush = palette[Math.Clamp((int)(prominence * 15), 0, 15)];
                var dot = Math.Max(.55, size * (.0015 + .0011 * (1 + Math.Sin(i * 12.9898 + band * 4.14)) / 2));
                dc.DrawEllipse(brush, null, new Point(center.X + Math.Cos(theta) * radius, center.Y + Math.Sin(theta) * radius), dot, dot);
            }
        }
    }
}
