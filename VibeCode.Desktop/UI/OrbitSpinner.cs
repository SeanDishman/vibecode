using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The shared thinking indicator. Each style lives in its own partial file (OrbitSpinner.Globe.cs and so on);
/// this file is the part they share: the one clock, the ink and its tints, and the depth-shaded wire.
///
/// Every style is honestly 3D or honestly solid, because those are the two things that survive being 22px
/// across. A flat ring of dots, or a thin outline, has one idea in it ("something is going round"); the eye
/// finishes reading it in about 300ms and then has to watch it for the rest of the turn. A form with depth -
/// near parts bigger and brighter, far parts smaller and dimmer, and things passing in front of and behind each
/// other - keeps reading as an object turning in space however long you look at it.
///
/// One clock drives all of it: <see cref="PhaseProperty"/> runs 0 -> 1 forever over <see cref="Period"/> and
/// every other quantity is a function of it. The loop has to close exactly rather than nearly (a fractional
/// rate visibly jumps at the wrap), so each style either uses whole turns per cycle or, where it is built from
/// identical parts, advances exactly one part per cycle - see OrbitSpinner.Atom.cs.
///
/// The clock is stopped whenever the element is not visible. That is not a micro-optimisation: the transcript
/// is a virtualizing ListBox that recycles containers, so an orb scrolled out of view would otherwise leave a
/// 30fps invalidation running for the rest of the chat's life.
/// </summary>
public sealed partial class OrbitSpinner : FrameworkElement
{
    private const double Tau = Math.PI * 2;

    /// <summary>
    /// Points a planar ring is traced with, as ONE CLOSED path. Drawing a ring as a run of individually-inked
    /// DrawLine calls leaves a flat-cap notch at every joint, and at this size that does not read as segmentation -
    /// it reads as a furry, half-dashed wire. Closed, every joint is a round join and there are no caps at all.
    /// </summary>
    private const int Segments = 48;

    /// <summary>Stops used to draw the depth ramp along a wire. Gradient stop count is free at raster time -
    /// WPF builds a lookup table - so this is only about how faithfully the curve is sampled.</summary>
    private const int RampStops = 9;

    /// <summary>Ink left in a wire at the very back, and how sharply it ramps to the front. Not zero: the far
    /// side has to be a whisper rather than a hole, or a ring reads as a broken arc instead of a ring seen
    /// through a sphere.</summary>
    private const double FarInk = 0.15;
    private const double DepthExp = 1.25;
    private const double WirePeak = 0.92;

    /// <summary>How white a bead's core goes. It is the only thing that is not pure accent, which is exactly
    /// why it reads as the subject and the wire reads as the thing it is travelling on.</summary>
    private const double BeadWhite = 0.45;

    /// <summary>Relative luminance of the blue accent (#4C8DF5) these alphas were tuned against. The green CLI
    /// accent is 2.1x brighter at the same alpha, so without normalising, one theme gets a whisper and the other
    /// gets a light bulb. See <see cref="EnsureResources"/>.</summary>
    private const double RefLuminance = 0.2719;

    private bool _running;

    // Per-frame scratch, allocated once: OnRender must not produce garbage beyond WPF's own render data.
    private readonly Point[] _at = new Point[Segments];

    // Frozen resource set, rebuilt only when the accent colour or the stroke width actually changes. Every
    // brush and pen that reaches a DrawingContext more than once per frame MUST be frozen: an unfrozen shared
    // Freezable makes DrawingContext registration O(N^2), which is invisible in review and looks like "WPF is
    // slow". Alpha is baked into the colours rather than pushed, because PushOpacity allocates a full-bounds
    // intermediate surface per call.
    private const int Tones = 24;
    private const int GlowTones = 8;
    private readonly Brush[] _hot = new Brush[Tones];
    private readonly Brush[] _glow = new Brush[GlowTones];
    private readonly Color[] _ramp = new Color[RampStops];
    private bool _built;
    private Color _builtColour;
    private double _builtThickness = -1;
    private double _gain = 1;

    public OrbitSpinner()
    {
        // Purely decorative, and it sits inside a transcript people select text in: never take the mouse.
        IsHitTestVisible = false;
        SetResourceReference(PreferredStyleProperty, ThinkingOrbStyles.ResourceKey);
        Loaded += (_, _) => Sync();
        IsVisibleChanged += (_, _) => Sync();
        Unloaded += (_, _) => StopClock();
    }

    /// <summary>Ink for the mark. Bind it to a theme brush (<c>{DynamicResource Accent}</c>) rather than a
    /// colour so switching themes repaints without this control knowing anything about themes.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink), typeof(Brush), typeof(OrbitSpinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeriodProperty = DependencyProperty.Register(
        nameof(Period), typeof(TimeSpan), typeof(OrbitSpinner),
        new PropertyMetadata(TimeSpan.FromSeconds(6), OnClockChanged));

    /// <summary>Whether the clock should run. Off still draws the mark, at rest - a spinner that vanishes when
    /// it stops leaves a hole in the layout.</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(
        nameof(Spin), typeof(bool), typeof(OrbitSpinner),
        new PropertyMetadata(true, OnClockChanged));

    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        "Phase", typeof(double), typeof(OrbitSpinner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Ink { get => (Brush?)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public TimeSpan Period { get => (TimeSpan)GetValue(PeriodProperty); set => SetValue(PeriodProperty, value); }
    public bool Spin { get => (bool)GetValue(SpinProperty); set => SetValue(SpinProperty, value); }

    private static void OnClockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((OrbitSpinner)d).Sync(restart: true);

    /// <summary>Start or stop the one clock to match "should be spinning and is actually on screen".</summary>
    private void Sync(bool restart = false)
    {
        var run = Spin && IsLoaded && IsVisible && Period > TimeSpan.Zero;
        if (run == _running && !restart) return;
        _running = run;

        if (!run)
        {
            // Hand the property back to its local value, so a stopped orb draws its rest frame rather than
            // holding whichever frame the clock happened to die on.
            StopClock();
            return;
        }

        var clock = new DoubleAnimation(0, 1, new Duration(Period)) { RepeatBehavior = RepeatBehavior.Forever };
        // Same reasoning as the telemetry wall's ripple: this runs for as long as a turn takes, and nothing here
        // moves faster than about a pixel per frame at chat size, so 30 reads as continuous. Repainting a
        // decorative mark at the full refresh rate is not worth the battery.
        Timeline.SetDesiredFrameRate(clock, HudMotion.RippleFrameRate);
        BeginAnimation(PhaseProperty, clock);
    }

    private void StopClock()
    {
        _running = false;
        BeginAnimation(PhaseProperty, null);
    }

    /// <summary>An orb with no size set still has to occupy something, or it collapses to nothing inside a
    /// StackPanel and the layout silently loses it.</summary>
    protected override Size MeasureOverride(Size available)
    {
        const double natural = 22;
        return new Size(
            double.IsInfinity(available.Width) ? natural : Math.Min(natural, available.Width),
            double.IsInfinity(available.Height) ? natural : Math.Min(natural, available.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var half = Math.Min(w, h) / 2;
        if (half <= 0) return;

        // Fall back to the theme accent rather than drawing nothing: an unset Ink should look wrong, not absent.
        var brush = Ink ?? TryFindResource("Accent") as Brush;
        if (brush is null) return;

        // Hairlines stay hairlines as the mark grows, but never thin out below a pixel at the size it ships at.
        var thickness = Math.Max(1.0, 1.2 * half / 11.0);
        EnsureResources(InkColour(brush), thickness);

        // Styles only ever see 0..1, so phase and phase + 1 are the same frame bit for bit.
        var phase = (double)GetValue(PhaseProperty);
        DrawSelectedStyle(dc, new Point(w / 2, h / 2), half, thickness, phase - Math.Floor(phase));
    }

    /// <summary>
    /// The pen a planar ring is stroked with: one brush whose ramp along <paramref name="axis"/> reproduces the
    /// depth shading exactly. Depth is not merely correlated with screen position on a planar ring, it is an
    /// EXACT affine function of it, which is what lets one gradient-stroked path carry a shading that would
    /// otherwise need a separate draw call per segment - and a path has joins, so no notches. Built per frame,
    /// which costs a few microseconds; the thing that must never happen is building one per PRIMITIVE.
    /// </summary>
    /// <param name="span">Half-length of the ring's projected minor axis: the distance from the centre to its
    /// nearest (and farthest) point on screen.</param>
    /// <param name="sinT">Sine of the ring's tilt, i.e. how much depth it actually spans.</param>
    /// <param name="peak">Ink at the very front; a ring that is scenery rather than subject passes less.</param>
    private Pen Wire(Point centre, Vector axis, double span, double sinT, double thickness, double peak = WirePeak)
    {
        // Offset t along the axis maps to sin(u) = 2t-1, so the depth at that point is sinT*(2t-1) and the ink
        // is the same curve the far/near ramp is defined by. Sampled rather than solved because gradient stops
        // are free at raster time.
        for (var i = 0; i < RampStops; i++)
        {
            var lit = (1 + sinT * (2.0 * i / (RampStops - 1) - 1)) / 2;
            _ramp[i] = Tint(_builtColour, peak * (FarInk + (1 - FarInk) * Math.Pow(lit, DepthExp)) * _gain);
        }

        Brush brush;
        if (span < 0.05)
        {
            // A zero-length gradient axis paints undefined, and "undefined" on a decorative mark means a flicker.
            brush = Frozen(new SolidColorBrush(_ramp[RampStops / 2]));
        }
        else
        {
            var stops = new GradientStopCollection(RampStops);
            for (var i = 0; i < RampStops; i++) stops.Add(new GradientStop(_ramp[i], i / (double)(RampStops - 1)));

            brush = Frozen(new LinearGradientBrush(stops)
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = centre - axis * span,   // sin(u) = -1: the far extreme
                EndPoint = centre + axis * span,     // sin(u) = +1: the near extreme
            });
        }

        return Frozen(new Pen(brush, thickness)
        {
            // Round joins are the whole point of stroking a path instead of a run of lines: they close the
            // notch that a flat cap leaves on the outside of every direction change.
            LineJoin = PenLineJoin.Round,
            // Flat at the ends: where a ring is split into a near and a far arc, the split falls where the curve
            // is smooth and depth is zero, so flat caps abut exactly there. Round ones would overlap and
            // composite into a bead at each seam.
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
        });
    }

    /// <summary>The ring in <see cref="_at"/> as one closed stroked path - closed so that it has joins everywhere
    /// and caps nowhere.</summary>
    private Geometry Ring()
    {
        var ring = new StreamGeometry();
        using (var open = ring.Open())
        {
            open.BeginFigure(_at[0], isFilled: false, isClosed: true);
            for (var k = 1; k < Segments; k++) open.LineTo(_at[k], isStroked: true, isSmoothJoin: true);
        }

        return Frozen(ring);
    }

    private void TraceEllipse(Point centre, double major, double minor, double rotation)
    {
        for (var index = 0; index < Segments; index++)
            _at[index] = OnEllipse(centre, major, minor, rotation, Tau * index / Segments);
    }

    private static Point OnEllipse(Point centre, double major, double minor, double rotation, double angle)
    {
        var x = major * Math.Cos(angle);
        var y = minor * Math.Sin(angle);
        return new Point(centre.X + x * Math.Cos(rotation) - y * Math.Sin(rotation),
            centre.Y + x * Math.Sin(rotation) + y * Math.Cos(rotation));
    }

    /// <summary>
    /// A bead: a soft bloom with a near-white core on it. The bloom is not decoration - a core this small would
    /// otherwise swing up to 3x in apparent brightness purely with its sub-pixel position, and twinkle as it
    /// travelled. The bloom's coverage is stable across sub-pixel positions, so it holds the brightness steady
    /// and the core only has to supply the hard centre.
    /// </summary>
    /// <param name="vis">0..1: how present the bead is, which sets both the bloom and the core.</param>
    private void DrawBead(DrawingContext dc, Point at, double radius, double bloom, double vis)
    {
        dc.DrawEllipse(_glow[Math.Clamp((int)Math.Round((GlowTones - 1) * vis), 0, GlowTones - 1)], null, at, bloom, bloom);
        dc.DrawEllipse(_hot[Tone(vis)], null, at, radius, radius);
    }

    private static int Tone(double ink) => Math.Clamp((int)Math.Round((Tones - 1) * ink), 0, Tones - 1);

    /// <summary>
    /// Rebuild the frozen brushes and pens, and only when something they depend on has actually changed - the
    /// comparison is two struct/double reads, and the rebuild is well under a millisecond, so this is free per frame.
    /// </summary>
    private void EnsureResources(Color colour, double thickness)
    {
        if (_built && colour == _builtColour && Math.Abs(thickness - _builtThickness) < 0.001) return;
        _built = true;
        _builtColour = colour;
        _builtThickness = thickness;

        // Equal alpha does NOT mean equal presence: the CLI theme's green is a little over twice the blue's
        // relative luminance, so the same numbers that read as a whisper on one theme read as a light bulb on
        // the other. Scale every alpha by the luminance ratio (softened, because perceived weight does not
        // track luminance linearly, and clamped so an extreme custom accent cannot erase the mark).
        _gain = Math.Clamp(Math.Pow(RefLuminance / Math.Max(Luminance(colour), 1e-4), 0.75), 0.55, 1.5);

        var core = Lerp(colour, Colors.White, BeadWhite);
        for (var i = 0; i < Tones; i++) _hot[i] = Solid(core, i / (double)(Tones - 1) * _gain);

        for (var i = 0; i < GlowTones; i++)
        {
            var level = i / (double)(GlowTones - 1) * 0.42 * _gain;
            // Four stops tuned to a real Gaussian falloff. A BlurEffect would be the obvious way to get this and
            // is the wrong one: it is a UIElement property (so it cannot apply to one primitive inside OnRender
            // at all), it allocates an intermediate surface, and it costs with device-pixel AREA - i.e. it gets
            // 4x worse at 200% DPI. This is one DrawEllipse with a cached brush.
            _glow[i] = Frozen(new RadialGradientBrush(new GradientStopCollection
            {
                new(Tint(colour, level), 0.00),
                new(Tint(colour, level * 0.92), 0.28),
                new(Tint(colour, level * 0.30), 0.60),
                new(Tint(colour, 0), 1.00),
            }));
        }

        BuildStyleResources(colour);
    }

    private static Color Tint(Color colour, double alpha) => Color.FromArgb(
        (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), colour.R, colour.G, colour.B);

    private static SolidColorBrush Solid(Color colour, double alpha) =>
        Frozen(new SolidColorBrush(Tint(colour, alpha)));

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private static Color Lerp(Color from, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * t),
        (byte)Math.Round(from.G + (to.G - from.G) * t),
        (byte)Math.Round(from.B + (to.B - from.B) * t));

    /// <summary>
    /// The accent turned round the colour wheel by <paramref name="degrees"/>, keeping its lightness and
    /// saturation. Neighbouring hues are what make a glow look like light rather than paint, and taking them
    /// from the accent keeps a custom accent - or the CLI theme's green - in a family of its own.
    /// </summary>
    private static Color HueShift(Color colour, double degrees)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var light = (max + min) / 2;
        if (max - min < 1e-6) return colour; // a grey has no hue to turn
        var delta = max - min;
        var sat = light > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        var hue = max == r ? (g - b) / delta + (g < b ? 6 : 0) : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
        hue = ((hue * 60 + degrees) % 360 + 360) % 360 / 360;

        static double Channel(double p, double q, double t)
        {
            t = (t % 1 + 1) % 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }

        var hi = light < 0.5 ? light * (1 + sat) : light + sat - light * sat;
        var lo = 2 * light - hi;
        return Color.FromRgb((byte)Math.Round(Channel(lo, hi, hue + 1 / 3.0) * 255),
            (byte)Math.Round(Channel(lo, hi, hue) * 255), (byte)Math.Round(Channel(lo, hi, hue - 1 / 3.0) * 255));
    }

    /// <summary>WCAG relative luminance, which is what "how bright does this ink read" actually means.</summary>
    private static double Luminance(Color c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// The one colour every tint is derived from. A gradient brush has no single colour, so average its stops
    /// rather than refusing to draw; anything with no readable colour at all falls back to the theme's muted
    /// grey, which looks deliberate instead of looking broken.
    /// </summary>
    private static Color InkColour(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush { GradientStops.Count: > 0 } gradient => Average(gradient.GradientStops),
        _ => Color.FromRgb(0x9E, 0xA0, 0xA8),
    };

    private static Color Average(GradientStopCollection stops)
    {
        double r = 0, g = 0, b = 0;
        foreach (var stop in stops)
        {
            r += stop.Color.R;
            g += stop.Color.G;
            b += stop.Color.B;
        }

        return Color.FromRgb((byte)(r / stops.Count), (byte)(g / stops.Count), (byte)(b / stops.Count));
    }
}
