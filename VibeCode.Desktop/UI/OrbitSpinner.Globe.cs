using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Globe: a sphere of dots turning on a tilted axis, with a scan sweeping across its face.
///
/// It is a lattice of real points on a real sphere, rotated, depth-sorted and drawn far to near, so the back
/// hemisphere shows through the front one as a fainter, finer field - the same "honestly 3D" dotted orb the
/// thinking-orbs project uses for AI and agent UIs. Depth is carried by size and ink together, near dots
/// whitening as well as growing, because at 22px either cue alone is too weak to read.
///
/// The scan is two meridians half a turn apart, so one of them is always crossing the face: dots it passes over
/// swell and light up, and everything it is not touching sits a little dimmer, which is what lets a column of
/// five or six dots read as a sweep instead of as noise.
/// </summary>
public sealed partial class OrbitSpinner
{
    private const double GlobeFrac = 0.74;

    /// <summary>Lattice pitch in DIPs: a floor, plus a share of the size. Small globes get FEWER, BIGGER dots
    /// rather than a shrunken copy of a big one - about twelve round the equator at 22px, where a finer lattice
    /// melts into a checkerboard - while the 32px preview still gets enough to read as a sphere.</summary>
    private const double GlobePitchBase = 3.0;
    private const double GlobePitchGrowth = 0.13;

    private const double GlobeTilt = 0.42;
    private const double GlobeTiltSwing = 0.07;

    /// <summary>World turns the scan makes per cycle, against the globe's one. Faster than the globe and the same
    /// way round, so it reads as light rolling over a turning surface rather than as the surface itself.</summary>
    private const double ScanTurns = 2;
    private const double ScanSharpness = 13;

    /// <summary>Dot size in lattice pitches: far, extra at the very front, extra under the scan.</summary>
    private const double DotFar = 0.12;
    private const double DotDepth = 0.20;
    private const double DotScan = 0.15;

    /// <summary>Ink at the very back, and the share of its ink a dot keeps when the scan is not on it. Dimming
    /// the unlit face is what makes the scan read as light moving over the globe, not as a brighter stripe.</summary>
    private const double GlobeFarInk = 0.16;
    private const double GlobeUnlit = 0.50;

    private const int DotTones = 32;
    private readonly Brush[] _dotInk = new Brush[DotTones];
    private Brush? _globeBody;

    // The lattice in model space, rebuilt only when a resize changes the dot count.
    private double[] _globeX = [], _globeY = [], _globeZ = [], _globeLat = [];
    private int _globeDots;
    private int _globeEquator = -1;

    // Per-frame scratch, sized with the lattice.
    private double[] _globeDepth = [];
    private int[] _globeOrder = [];
    private Point[] _globeAt = [];
    private double[] _globeR = [];
    private int[] _globeTone = [];

    private void BuildGlobeResources(Color colour)
    {
        for (var i = 0; i < DotTones; i++)
        {
            var v = i / (double)(DotTones - 1);
            _dotInk[i] = Solid(Lerp(colour, Colors.White, 0.06 + 0.52 * v * v), v * _gain);
        }

        // A breath of atmosphere at the limb, so the globe has an edge where the lattice is sparse.
        _globeBody = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Tint(colour, 0), 0.0),
            new(Tint(colour, 0.03 * _gain), 0.70),
            new(Tint(colour, 0.11 * _gain), 0.94),
            new(Tint(colour, 0), 1.0),
        }));
    }

    private void DrawGlobe(DrawingContext dc, Point centre, double half, double phase)
    {
        var radius = GlobeFrac * half;
        var equator = Math.Clamp((int)Math.Round(Tau * radius / (GlobePitchBase + GlobePitchGrowth * half)), 10, 48);
        if (equator != _globeEquator) BuildLattice(equator);
        var pitch = Tau * radius / equator;

        dc.DrawEllipse(_globeBody, null, centre, radius * 1.06, radius * 1.06);

        var spin = Tau * phase;
        var tilt = GlobeTilt + GlobeTiltSwing * Math.Sin(Tau * phase + 0.6);
        var scan = 1.9 - Tau * ScanTurns * phase;
        double cs = Math.Cos(spin), ss = Math.Sin(spin), ct = Math.Cos(tilt), st = Math.Sin(tilt);
        double cScan = Math.Cos(scan), sScan = Math.Sin(scan);

        for (var i = 0; i < _globeDots; i++)
        {
            double x = _globeX[i], y = _globeY[i], z = _globeZ[i];
            var x1 = x * cs + z * ss;            // spin about the vertical axis
            var z1 = -x * ss + z * cs;
            var y1 = y * ct - z1 * st;           // then tip the top towards the viewer
            var z2 = y * st + z1 * ct;
            var depth = (z2 + 1) / 2;

            // Cosine of the angle between this dot's longitude and the nearer of the two scan meridians, turned
            // into a Gaussian band; only the face the viewer can see is lit.
            var boost = 0.0;
            var cosLat = _globeLat[i];
            if (cosLat > 0.05 && z2 > 0)
            {
                var along = Math.Abs(x1 * cScan + z1 * sScan) / cosLat;
                boost = Math.Exp((along - 1) * ScanSharpness) * Math.Min(1, z2 * 1.6);
            }

            var ink = (GlobeFarInk + (1 - GlobeFarInk) * Math.Pow(depth, 1.3)) * (GlobeUnlit + (1 - GlobeUnlit) * boost);
            _globeAt[i] = new Point(centre.X + x1 * radius, centre.Y - y1 * radius);
            _globeR[i] = pitch * (DotFar + DotDepth * depth + DotScan * boost);
            _globeTone[i] = Math.Clamp((int)Math.Round(ink * (DotTones - 1)), 0, DotTones - 1);
            _globeDepth[i] = z2;
            _globeOrder[i] = i;
        }

        // Far to near: the back hemisphere is drawn first and the front one over it.
        Array.Sort(_globeDepth, _globeOrder, 0, _globeDots);
        for (var k = 0; k < _globeDots; k++)
        {
            var i = _globeOrder[k];
            dc.DrawEllipse(_dotInk[_globeTone[i]], null, _globeAt[i], _globeR[i], _globeR[i]);
        }
    }

    /// <summary>Parallels pole to pole with dots spread evenly along each, alternate parallels offset half a
    /// step so the field packs like a halftone rather than lining up into stripes.</summary>
    private void BuildLattice(int equator)
    {
        _globeEquator = equator;
        var parallels = Math.Max(4, (int)Math.Round(equator / 2.0));
        var x = new List<double>();
        var y = new List<double>();
        var z = new List<double>();
        var lat = new List<double>();
        for (var p = 0; p <= parallels; p++)
        {
            var latitude = -Math.PI / 2 + Math.PI * p / parallels;
            var cosLat = Math.Cos(latitude);
            var count = Math.Max(1, (int)Math.Round(Math.Abs(cosLat) * equator));
            for (var d = 0; d < count; d++)
            {
                var longitude = Tau * (d + (p % 2) * 0.5) / count;
                x.Add(cosLat * Math.Cos(longitude));
                y.Add(Math.Sin(latitude));
                z.Add(cosLat * Math.Sin(longitude));
                lat.Add(Math.Max(0, cosLat));
            }
        }

        _globeDots = x.Count;
        _globeX = x.ToArray();
        _globeY = y.ToArray();
        _globeZ = z.ToArray();
        _globeLat = lat.ToArray();
        _globeDepth = new double[_globeDots];
        _globeOrder = new int[_globeDots];
        _globeAt = new Point[_globeDots];
        _globeR = new double[_globeDots];
        _globeTone = new int[_globeDots];
    }
}
