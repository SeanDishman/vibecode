using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Atom: three electrons on tilted orbits round a lit nucleus, going ALL the way round it.
///
/// The old atom drew three flat ellipses with dots on them, and the dots never read as going round anything:
/// on a narrow ellipse a dot at constant angular speed dawdles at the two tips and flicks across the middle,
/// and with nothing to say which crossing was in front, it looked like a bead sliding back and forth on a wire.
/// Going round is a depth story, so this tells it with every depth cue there is:
///  - each orbit is a real circle in 3D, its wire shaded near-bright / far-dim along its true depth;
///  - an electron grows and whitens across the front and shrinks and dims across the back;
///  - the nucleus OCCLUDES: an electron passing behind it disappears under it, and one passing in front
///    crosses over it - draw order is the whole trick;
///  - each electron drags a short fading trail, so its direction is never ambiguous, even at the tips.
///
/// The loop closes by symmetry rather than by whole turns. The three orbits are identical parts a third of a
/// turn apart, so every per-orbit rate is a whole number of turns PLUS one third per cycle: at the end of a
/// cycle each orbit has moved exactly into the place of the next one, electron and all, and the frame is
/// identical. That is what lets the orbits precess at a calm third of a turn per cycle instead of a full one.
/// </summary>
public sealed partial class OrbitSpinner
{
    /// <summary>Orbit radius, as a fraction of the half-size. Not larger: an electron at the tip of an orbit
    /// carries its bloom with it, and at 0.8 that bloom reached the edge of a 22px box and was cut off flat.</summary>
    private const double OrbitRadiusFrac = 0.74;
    private const double NucleusFrac = 0.20;

    /// <summary>Angle between each orbit's axis and the line of sight: 65..75 degrees keeps every orbit an open
    /// ellipse (never an edge-on line, which reads as a bead on a stick) while still narrow enough that the front
    /// crossing passes over the nucleus and the back one passes under it.</summary>
    private const double AtomTilt = 70 * Math.PI / 180;
    private const double AtomTiltSwing = 5 * Math.PI / 180;

    /// <summary>Laps per cycle: three and a third. The extra third is what makes the loop close; see above.</summary>
    private const double ElectronLaps = 10 / 3.0;
    private const double TrailArc = 1.35;
    private const int TrailSteps = 14;
    private const int OrbitSteps = 96;

    private readonly AtomElectron[] _electrons = new AtomElectron[3];
    private readonly Pen[] _orbitWire = new Pen[3];
    private Brush? _nucleusInk;

    private struct AtomElectron
    {
        public Point At, Tail;
        public double Lit;
        public int Orbit;
        public double Angle;
    }

    private void BuildAtomResources(Color colour)
    {
        // Lit from the upper left, so the nucleus is a ball rather than a disc.
        _nucleusInk = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Lerp(colour, Colors.White, 0.88), 0.00),
            new(Lerp(colour, Colors.White, 0.34), 0.42),
            new(colour, 0.78),
            new(Lerp(colour, Colors.Black, 0.30), 1.00),
        })
        {
            GradientOrigin = new Point(0.36, 0.30),
            Center = new Point(0.46, 0.42),
            RadiusX = 0.62,
            RadiusY = 0.62,
        });
    }

    private void DrawAtom(DrawingContext dc, Point centre, double half, double thickness, double phase)
    {
        var orbit = OrbitRadiusFrac * half;
        var nucleus = NucleusFrac * half * (1 + 0.045 * Math.Sin(Tau * 2 * phase));

        // Geometry of each orbit, and where its electron is.
        Span<double> sinN = stackalloc double[3], cosN = stackalloc double[3];
        Span<double> sinT = stackalloc double[3], cosT = stackalloc double[3];
        for (var k = 0; k < 3; k++)
        {
            var node = Tau * (k + phase) / 3 + 0.35;
            var tilt = AtomTilt + AtomTiltSwing * Math.Sin(Tau * (k / 3.0 + phase * 4 / 3));
            sinN[k] = Math.Sin(node);
            cosN[k] = Math.Cos(node);
            sinT[k] = Math.Sin(tilt);
            cosT[k] = Math.Cos(tilt);

            var angle = Tau * (k / 3.0 + phase * ElectronLaps) + 0.5;
            _electrons[k] = new AtomElectron
            {
                At = OrbitPoint(centre, orbit, sinN[k], cosN[k], cosT[k], angle),
                Tail = OrbitPoint(centre, orbit, sinN[k], cosN[k], cosT[k], angle - TrailArc),
                Lit = (Math.Sin(angle) + 1) / 2,
                Orbit = k,
                Angle = angle,
            };
        }

        // Nearest last, so the back half of the scene goes down first.
        Array.Sort(_electrons, (a, b) => a.Lit.CompareTo(b.Lit));

        // One depth-shaded pen per orbit, shared by its far and near halves.
        for (var k = 0; k < 3; k++)
            _orbitWire[k] = Wire(centre, new Vector(-cosN[k], sinN[k]), orbit * cosT[k], sinT[k], thickness * 0.85, peak: 0.55);

        // 1. the far halves of the orbits, then everything that is behind the nucleus
        for (var k = 0; k < 3; k++)
            DrawOrbitArc(dc, centre, orbit, sinN[k], cosN[k], cosT[k], _orbitWire[k], far: true);
        foreach (var electron in _electrons)
            if (electron.Lit < 0.5) DrawElectron(dc, centre, orbit, half, sinN, cosN, cosT, electron);

        // 2. the nucleus, which hides whatever is passing behind it
        dc.DrawEllipse(_glow[Math.Clamp((int)Math.Round((GlowTones - 1) * (0.55 + 0.20 * Math.Sin(Tau * 2 * phase))), 0, GlowTones - 1)],
            null, centre, half * 0.42, half * 0.42);
        dc.DrawEllipse(_nucleusInk, null, centre, nucleus, nucleus);

        // 3. the near halves, and everything crossing in front
        for (var k = 0; k < 3; k++)
            DrawOrbitArc(dc, centre, orbit, sinN[k], cosN[k], cosT[k], _orbitWire[k], far: false);
        foreach (var electron in _electrons)
            if (electron.Lit >= 0.5) DrawElectron(dc, centre, orbit, half, sinN, cosN, cosT, electron);
    }

    /// <summary>A point on an orbit of radius <paramref name="r"/> whose line of nodes is at angle N and whose
    /// plane is tilted T from face-on: e1 = (-sinN, cosN, 0) lies in the view plane, e2 = n x e1 =
    /// (-cosT cosN, -cosT sinN, sinT) carries all of the tilt, so the depth is exactly r sinT sin(u).</summary>
    private static Point OrbitPoint(Point centre, double r, double sinN, double cosN, double cosT, double u)
    {
        double cu = Math.Cos(u), su = Math.Sin(u);
        return new Point(centre.X + r * (-sinN * cu - cosT * cosN * su), centre.Y - r * (cosN * cu - cosT * sinN * su));
    }

    /// <summary>Half an orbit - the far half or the near half. The halves meet where the depth is zero, at the
    /// tips of the ellipse, so the flat caps abut there; 96 steps keep the turn at each joint small enough that
    /// they meet without a notch.</summary>
    private static void DrawOrbitArc(DrawingContext dc, Point centre, double r, double sinN, double cosN, double cosT,
        Pen wire, bool far)
    {
        const int halfSteps = OrbitSteps / 2;
        var arc = new StreamGeometry();
        using (var open = arc.Open())
        {
            for (var j = 0; j <= halfSteps; j++)
            {
                var u = Tau * ((far ? halfSteps : 0) + j) / OrbitSteps;
                var at = OrbitPoint(centre, r, sinN, cosN, cosT, u);
                if (j == 0) open.BeginFigure(at, isFilled: false, isClosed: false);
                else open.LineTo(at, isStroked: true, isSmoothJoin: true);
            }
        }

        dc.DrawGeometry(null, wire, Frozen(arc));
    }

    private void DrawElectron(DrawingContext dc, Point centre, double r, double half, ReadOnlySpan<double> sinN,
        ReadOnlySpan<double> cosN, ReadOnlySpan<double> cosT, AtomElectron electron)
    {
        var k = electron.Orbit;
        var lit = electron.Lit;
        var vis = 0.42 + 0.58 * Math.Pow(lit, 1.2);
        var core = half * (0.085 + 0.055 * lit);

        // The trail: one stroked path along the orbit behind the electron, faded by distance from the head. On a
        // convex curve the distance from the head only grows along an arc this short, so a radial gradient
        // centred on the head IS a fade along the trail - one primitive, no per-segment alpha, no notches.
        var trail = new StreamGeometry();
        using (var open = trail.Open())
        {
            for (var j = 0; j <= TrailSteps; j++)
            {
                var at = OrbitPoint(centre, r, sinN[k], cosN[k], cosT[k], electron.Angle - TrailArc * (1 - j / (double)TrailSteps));
                if (j == 0) open.BeginFigure(at, isFilled: false, isClosed: false);
                else open.LineTo(at, isStroked: true, isSmoothJoin: true);
            }
        }

        var reach = Math.Max(0.5, (electron.Tail - electron.At).Length * 1.05);
        var fade = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Tint(_builtColour, 0.80 * vis * _gain), 0.00),
            new(Tint(_builtColour, 0.36 * vis * _gain), 0.40),
            new(Tint(_builtColour, 0), 1.00),
        })
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = electron.At,
            GradientOrigin = electron.At,
            RadiusX = reach,
            RadiusY = reach,
        });
        dc.DrawGeometry(null, Frozen(new Pen(fade, Math.Max(1.0, core * 1.25))
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        }), Frozen(trail));

        DrawBead(dc, electron.At, core, half * 0.22 * (0.8 + 0.2 * lit), vis);
    }
}
