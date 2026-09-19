using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeCode.UI;

/// <summary>
/// The mark that says "still going" while nothing is being written: a bead running an orbit inside a faint
/// shell, with the orbit's plane slowly tipping over so the ellipse opens wide, leans, narrows and opens again.
///
/// It is a sphere, not a circle, and the whole design exists to earn that. Three cues do it, all of which
/// survive being 22px across because they are low-frequency: the wire is DIM WHERE IT IS FAR and bright where
/// it is near, so a 1.2px stroke visibly wraps around something; the projected orbit opens and closes, which
/// the eye cannot read as anything but a tilted disc in 3D; and the bead passes UNDER the near half of its own
/// path, which is occlusion - the least ambiguous depth signal there is, and it costs nothing but draw order.
///
/// The predecessor was a ring of dots chasing each other. Two things were wrong with it. It had one idea in it
/// ("something is going round"), which the eye finishes reading in about 300ms and then has to look at for the
/// rest of the turn. And its tail dots were 0.94px across, under the ~1.5px floor where WPF's antialiasing
/// starts to swing a small disc's peak alpha by up to 3x with sub-pixel position - so they twinkled, and the
/// ring read as a loose uneven scatter rather than a ring.
///
/// One clock drives all of it: <see cref="PhaseProperty"/> runs 0 -> 1 forever over <see cref="Period"/> and
/// every other quantity is a function of it. All three rates are whole turns per cycle, so the loop closes
/// exactly rather than nearly (a fractional one visibly jumps at the wrap).
///
/// The clock is stopped whenever the element is not visible. That is not a micro-optimisation: the transcript
/// is a virtualizing ListBox that recycles containers, so an orb scrolled out of view would otherwise leave a
/// 30fps invalidation running for the rest of the chat's life.
/// </summary>
public sealed class OrbitSpinner : FrameworkElement
{
    private const double Tau = Math.PI * 2;

    // ---- motion. Every rate is a WHOLE number of turns per cycle: the loop has to close. ----

    /// <summary>Turns of the orbit plane per cycle - the slow carrier the whole thing hangs off.</summary>
    private const double PrecessTurns = 1;

    /// <summary>Laps the bead runs per cycle. 3 against the plane's 1 means it never traces the same screen
    /// path twice within a cycle, so there is always something to notice and never a metronome to lock onto.</summary>
    private const double OrbitLaps = 3;

    /// <summary>
    /// Angle between the orbit's axis and the line of sight, swinging <c>Mid +- Swing</c>.
    ///
    /// This is parameterised by the plane's NORMAL rather than by spinning the plane about a fixed axis, and
    /// that is the single most important decision in the file. Spin a plane and its normal must at some point
    /// pass through the view plane - the ellipse collapses to a straight line and the mark becomes a stick with
    /// a ball on the end, which reads as a rendering fault, not as an orbit seen edge-on. Driving the normal
    /// directly bounds the projected minor axis at <c>R*cos(Mid+Swing)</c>, so it can never reach zero.
    /// 34..76 degrees keeps the minor axis between 24% and 83% of the major one: always an ellipse, never a
    /// line, and never a circle either - a circle inside the mount reads as two concentric rings and the depth
    /// story dies. It also keeps the bead's screen speed off zero, so it never appears to stall, which on a
    /// "still working" indicator is the one failure mode that actually costs the user something.
    /// </summary>
    private const double TiltMid = 55 * Math.PI / 180;
    private const double TiltSwing = 21 * Math.PI / 180;

    /// <summary>
    /// Where in each cycle the three motions start. Pure composition, and they cost nothing: a constant offset
    /// inside a whole-turn rotation is still a whole turn, so the loop closes exactly as before.
    ///
    /// They exist because p = 0 is not just an arbitrary moment - it is the REST FRAME, the pose the mark holds
    /// when the clock is stopped. Left at zero the loop happened to start at its narrowest tilt with the bead
    /// at the tip, i.e. a stick with a ball on the end, which is the one silhouette that reads as a fault
    /// rather than as an object. These put it at a mid tilt, leaning, with the bead off to one side.
    /// </summary>
    private const double TiltPose = 0.25;
    private const double NodePose = 0.07;
    private const double BeadPose = 0.15;

    // ---- geometry, as a fraction of the element's half-size, so the mark scales with whatever it is given ----

    private const double ShellFrac = 0.864;
    private const double OrbitFrac = 0.600;
    private const double BeadFrac = 0.159;
    private const double GlowFrac = 0.275;

    /// <summary>
    /// Points the orbit is traced with, as ONE CLOSED path.
    ///
    /// Both halves of that sentence were learned by looking. Drawing the ring as a run of individually-inked
    /// DrawLine calls leaves a flat-cap notch at every joint, and at this size that does not read as
    /// segmentation - it reads as a furry, half-dashed wire. Splitting it into a near arc and a far arc so the
    /// bead could be drawn between them fixed the fur but left two flat caps meeting at the ends of the
    /// ellipse, which is the worst possible place for them: those are the tight ends, where at full tilt the
    /// direction changes 43 degrees between adjacent samples, so the caps met at an angle and cut a visible
    /// notch out of the wire. Closed, every joint is a round join and there are no caps at all.
    ///
    /// 48 samples keeps the sagitta under 0.02px and keeps the per-joint turn moderate even when the ellipse is
    /// at its narrowest.
    /// </summary>
    private const int Segments = 48;

    /// <summary>Stops used to draw the depth ramp along the wire. Gradient stop count is free at raster time -
    /// WPF builds a lookup table - so this is only about how faithfully the curve is sampled.</summary>
    private const int RampStops = 9;

    /// <summary>Half-width of the gaps in the shell, at top and bottom. The shell is broken rather than closed
    /// because a closed one is a second ring: when the orbit comes near face-on the two sit concentric and the
    /// mark stops being an object and becomes a target. Broken, it reads as the mount the orbit turns in.</summary>
    private const double ShellGap = 17 * Math.PI / 180;

    /// <summary>Ink left in the wire at the very back, and how sharply it ramps to the front. Not zero: the far
    /// side has to be a whisper rather than a hole, or the orbit reads as a broken arc instead of a ring seen
    /// through a sphere.</summary>
    private const double FarInk = 0.15;
    private const double DepthExp = 1.25;
    private const double WirePeak = 0.92;
    private const double ShellInk = 0.15;

    /// <summary>How white the bead goes. It is the only thing here that is not pure accent, which is exactly
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
    // intermediate surface per call - the old dot ring spent more render time on nine of those than this whole
    // design spends on everything.
    private const int Tones = 24;
    private const int GlowTones = 8;
    private readonly Brush[] _hot = new Brush[Tones];
    private readonly Brush[] _glow = new Brush[GlowTones];
    private readonly Color[] _ramp = new Color[RampStops];
    private Pen? _shellPen;
    private Color _builtColour;
    private double _builtThickness = -1;
    private double _gain = 1;

    // The mount is the same every frame; only a resize can change it. Keyed on the centre as well as the
    // radius: the geometry is built in absolute coordinates, so a element that changes shape without changing
    // min(w,h) would otherwise keep drawing its mount at the old centre.
    private Geometry? _shell;
    private double _shellFor = -1;
    private Point _shellAt;

    public OrbitSpinner()
    {
        // Purely decorative, and it sits inside a transcript people select text in: never take the mouse.
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
        Unloaded += (_, _) => Sync();
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
        var run = Spin && IsVisible && Period > TimeSpan.Zero;
        if (run == _running && !restart) return;
        _running = run;

        if (!run)
        {
            // Hand the property back to its local value, so a stopped orb draws its rest frame rather than
            // holding whichever frame the clock happened to die on.
            BeginAnimation(PhaseProperty, null);
            return;
        }

        var clock = new DoubleAnimation(0, 1, new Duration(Period)) { RepeatBehavior = RepeatBehavior.Forever };
        // Same reasoning as the telemetry wall's ripple: this runs for as long as a turn takes, and nothing here
        // moves faster than about 0.7px per frame, so 30 reads as continuous. Repainting a decorative mark at
        // the full refresh rate is not worth the battery.
        Timeline.SetDesiredFrameRate(clock, HudMotion.RippleFrameRate);
        BeginAnimation(PhaseProperty, clock);
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

        var centre = new Point(w / 2, h / 2);
        var orbit = OrbitFrac * half;
        var phase = (double)GetValue(PhaseProperty);

        dc.DrawGeometry(null, _shellPen, Mount(centre, ShellFrac * half));

        // The orbit plane, given entirely by where its normal points. e1 lies in the view plane (so it always
        // projects to the full radius) and e2 carries all of the tilt, which is what makes the projected
        // ellipse exactly `orbit` by `orbit*cos(tilt)` and the depth exactly `sin(tilt)*sin(u)` - no fitting,
        // no special cases, and a front/back split that is a plain sign test on a smooth quantity.
        var tilt = TiltMid + TiltSwing * Math.Cos(Tau * (PrecessTurns * phase + TiltPose));
        var node = Tau * (PrecessTurns * phase + NodePose);
        double sinT = Math.Sin(tilt), cosT = Math.Cos(tilt);
        double sinN = Math.Sin(node), cosN = Math.Cos(node);

        // e1 = (-sinN, cosN, 0) ; e2 = n x e1 = (-cosT*cosN, -cosT*sinN, sinT)
        for (var k = 0; k < Segments; k++)
        {
            var u = Tau * k / Segments;
            double cu = Math.Cos(u), su = Math.Sin(u);

            var x = orbit * (-sinN * cu - cosT * cosN * su);
            var y = orbit * (cosN * cu - cosT * sinN * su);
            _at[k] = new Point(centre.X + x, centre.Y - y);   // screen y grows downward
        }

        // Depth is not merely correlated with screen position, it is an EXACT affine function of it: the orbit
        // lies in a plane, so substituting the plane equation gives sin(u) = ((P-centre) . d) / (orbit*cos t)
        // along the fixed screen direction d below. That is what lets one gradient-stroked path carry a shading
        // that would otherwise need a separate draw call per segment - and a path has joins, so no notches.
        var span = orbit * cosT;
        var axis = new Vector(-cosN, sinN);
        var wire = Wire(centre, axis, span, sinT, thickness);

        var beadAngle = Tau * (OrbitLaps * phase + BeadPose);
        double bc = Math.Cos(beadAngle), bs = Math.Sin(beadAngle);
        var beadAt = new Point(
            centre.X + orbit * (-sinN * bc - cosT * cosN * bs),
            centre.Y - orbit * (cosN * bc - cosT * sinN * bs));
        var beadDepth = sinT * bs;

        // Draw order IS the occlusion: when the bead is round the back it goes down first and the wire crosses
        // over it; when it is at the front it goes on top. That is the strongest depth cue available and it
        // costs two lines of code. The far half of the wire also passes over a far bead, which is technically
        // the wrong way round - but the far half is drawn at FarInk, so what it actually does is veil the bead
        // slightly while it is behind, which is the impression wanted anyway.
        if (beadDepth <= 0) DrawBead(dc, beadAt, beadDepth, half);
        dc.DrawGeometry(null, wire, Ring());
        if (beadDepth > 0) DrawBead(dc, beadAt, beadDepth, half);
    }

    /// <summary>
    /// The pen the whole orbit is stroked with: one brush whose ramp along <paramref name="axis"/> reproduces
    /// the depth shading exactly. Built per frame, which costs a few microseconds - the thing that must never
    /// happen is building one per PRIMITIVE, and this is referenced twice.
    /// </summary>
    private Pen Wire(Point centre, Vector axis, double span, double sinT, double thickness)
    {
        // Offset t along the axis maps to sin(u) = 2t-1, so the depth at that point is sinT*(2t-1) and the ink
        // is the same curve the far/near ramp is defined by. Sampled rather than solved because gradient stops
        // are free at raster time - WPF bakes them into a lookup table - so nine of them cost nothing to draw.
        for (var i = 0; i < RampStops; i++)
        {
            var lit = (1 + sinT * (2.0 * i / (RampStops - 1) - 1)) / 2;
            _ramp[i] = Tint(_builtColour, WirePeak * (FarInk + (1 - FarInk) * Math.Pow(lit, DepthExp)) * _gain);
        }

        Brush brush;
        if (span < 0.05)
        {
            // Degenerate only if the orbit could come edge-on, which the tilt range forbids. Handled anyway:
            // a zero-length gradient axis paints undefined, and "undefined" on a decorative mark means a flicker.
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
            // Flat at the two ends, though - the arcs are split where the curve is smooth and depth is zero, so
            // flat caps abut exactly there. Round ones would overlap and composite into a bead at each seam.
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
        });
    }

    /// <summary>The orbit as one closed stroked path - closed so that it has joins everywhere and caps nowhere.</summary>
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

    /// <summary>The broken outer shell. Cached: it only ever changes when the element is resized.</summary>
    private Geometry Mount(Point centre, double radius)
    {
        if (_shell is not null && Math.Abs(radius - _shellFor) < 0.001 &&
            Math.Abs(centre.X - _shellAt.X) < 0.001 && Math.Abs(centre.Y - _shellAt.Y) < 0.001) return _shell;
        _shellFor = radius;
        _shellAt = centre;

        var mount = new StreamGeometry();
        using (var open = mount.Open())
        {
            for (var side = 0; side < 2; side++)
            {
                var from = ShellGap + side * Math.PI;
                var to = Math.PI - ShellGap + side * Math.PI;
                const int steps = 20;

                for (var k = 0; k <= steps; k++)
                {
                    var a = from + (to - from) * k / steps;
                    var at = new Point(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a));
                    if (k == 0) open.BeginFigure(at, isFilled: false, isClosed: false);
                    else open.LineTo(at, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        return _shell = Frozen(mount);
    }

    /// <summary>
    /// The bead: a soft bloom with a near-white core on it. The bloom is not decoration - a core this small
    /// would otherwise swing up to 3x in apparent brightness purely with its sub-pixel position, and twinkle as
    /// it travelled. The bloom's coverage is stable across sub-pixel positions, so it holds the brightness
    /// steady and the core only has to supply the hard centre.
    /// </summary>
    private void DrawBead(DrawingContext dc, Point at, double depth, double half)
    {
        var lit = (depth + 1) / 2;
        var vis = 0.35 + 0.65 * Math.Pow(lit, 1.3);
        var radius = BeadFrac * half * (0.78 + 0.32 * lit);

        dc.DrawEllipse(_glow[Math.Clamp((int)Math.Round((GlowTones - 1) * vis), 0, GlowTones - 1)], null,
            at, GlowFrac * half, GlowFrac * half);
        dc.DrawEllipse(_hot[Tone(vis)], null, at, radius, radius);
    }

    private static int Tone(double ink) => Math.Clamp((int)Math.Round((Tones - 1) * ink), 0, Tones - 1);

    /// <summary>
    /// Rebuild the frozen brushes and pens, and only when something they depend on has actually changed - the
    /// comparison is two struct/double reads, and the rebuild is a few microseconds, so this is free per frame.
    /// </summary>
    private void EnsureResources(Color colour, double thickness)
    {
        if (colour == _builtColour && Math.Abs(thickness - _builtThickness) < 0.001 && _shellPen is not null) return;
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

        _shellPen = Frozen(new Pen(Solid(colour, ShellInk * _gain), Math.Max(1.0, thickness * 0.8))
        {
            // The mount is two open arcs; rounding their ends stops them reading as snapped-off wire.
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        });
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
