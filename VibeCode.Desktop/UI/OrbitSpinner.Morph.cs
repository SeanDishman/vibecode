using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Morph: Material 3 Expressive's loading indicator - a solid shape springing through seven forms while it
/// turns. Google ships this as the Android 16 loader, and it earns its place here for a reason the thin styles
/// cannot match: at 22px a FILLED shape is the boldest mark there is, and a shape change is legible at any size.
///
/// Faithful to the platform where it matters. The seven shapes, their order and their corner roundings are
/// MaterialShapes' own (soft burst, 9-sided cookie, pentagon, pill, sunny, 4-sided cookie, oval); each step
/// is driven by the same spring (stiffness 200, damping ratio 0.6 - about a 9% overshoot, which is where the
/// "expressive" comes from); and the rotation has the same two parts, a steady turn plus an extra kick that
/// rides the spring. The one change is pace: Android gives each shape 650ms, and here the seven share the
/// six-second cycle so the loop closes, which also suits a mark that may sit on screen for minutes.
///
/// Shapes are stored as radius-by-angle tables. Every Material shape here is star-shaped about its centre, so
/// blending two tables angle by angle is a true morph with no swirl, and a frame is one filled polygon.
/// </summary>
public sealed partial class OrbitSpinner
{
    private const double MorphFrac = 0.80;
    private const int MorphSamples = 120;

    /// <summary>Degrees per shape: a steady turn across the step plus a kick carried by the spring. Android
    /// uses 50 + 90; the steady part is trimmed so seven steps make exactly three turns and the loop closes.</summary>
    private const double MorphSteady = 3 * 360 / 7.0 - MorphKick;
    private const double MorphKick = 90;

    private static readonly double[][] MorphShapes = MaterialShapeTables.Build(MorphSamples);
    private readonly Point[] _morphAt = new Point[MorphSamples];
    private Brush? _morphFill;
    private Point _morphFillAt;
    private double _morphFillFor = -1;
    private Color _morphLight, _morphBody, _morphShade;

    private void BuildMorphResources(Color colour)
    {
        _morphLight = Lerp(colour, Colors.White, 0.32);
        _morphBody = colour;
        _morphShade = Lerp(colour, Colors.Black, 0.22);
        _morphFill = null;
    }

    private void DrawMorph(DrawingContext dc, Point centre, double half, double phase)
    {
        var steps = MorphShapes.Length;
        var position = phase * steps;
        var step = Math.Min(steps - 1, (int)position);
        var local = position - step;
        var settle = SpringStep(local, Period.TotalSeconds / steps);

        var from = MorphShapes[step];
        var to = MorphShapes[(step + 1) % steps];
        var turn = (step * (MorphSteady + MorphKick) + MorphSteady * local + MorphKick * settle) * Math.PI / 180;
        var scale = MorphFrac * half;

        for (var j = 0; j < MorphSamples; j++)
        {
            var r = scale * (from[j] + (to[j] - from[j]) * settle);
            var angle = Tau * j / MorphSamples + turn;
            _morphAt[j] = new Point(centre.X + r * Math.Cos(angle), centre.Y + r * Math.Sin(angle));
        }

        var shape = new StreamGeometry();
        using (var open = shape.Open())
        {
            open.BeginFigure(_morphAt[0], isFilled: true, isClosed: true);
            for (var j = 1; j < MorphSamples; j++) open.LineTo(_morphAt[j], isStroked: false, isSmoothJoin: true);
        }

        dc.DrawGeometry(MorphFill(centre, scale), null, Frozen(shape));
    }

    /// <summary>A light that stays put while the shape turns under it, so the turn reads as a solid rotating
    /// rather than a sticker spinning. Cached until a resize moves it.</summary>
    private Brush MorphFill(Point centre, double scale)
    {
        if (_morphFill is not null && Math.Abs(scale - _morphFillFor) < 0.001 && _morphFillAt == centre) return _morphFill;
        _morphFillFor = scale;
        _morphFillAt = centre;
        return _morphFill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(_morphLight, 0.0),
            new(_morphBody, 0.62),
            new(_morphShade, 1.0),
        })
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = centre,
            GradientOrigin = new Point(centre.X - 0.38 * scale, centre.Y - 0.46 * scale),
            RadiusX = scale * 1.25,
            RadiusY = scale * 1.25,
        });
    }

    /// <summary>
    /// How far through its step a spring-driven move is, at <paramref name="local"/> (0..1) of a step lasting
    /// <paramref name="stepSeconds"/>. Divided by where the spring has got to when the step ends, so every step
    /// finishes at exactly 1 and the next one starts from exactly where it stopped - at 0.86s the spring is
    /// already within 0.1% of rest, so the curve is unchanged to the eye, but the joins become exact.
    /// </summary>
    private static double SpringStep(double local, double stepSeconds, double stiffness = 200, double damping = 0.6)
    {
        var end = Spring(stepSeconds, stiffness, damping);
        return stepSeconds <= 0 || Math.Abs(end) < 1e-6 ? local : Spring(local * stepSeconds, stiffness, damping) / end;
    }

    /// <summary>A unit step through a SpringForce from rest: the classic underdamped response. Android's loader
    /// values (stiffness 200, damping ratio 0.6) settle in about half a second, overshooting by about 9%.</summary>
    private static double Spring(double seconds, double stiffness, double damping)
    {
        var omega = Math.Sqrt(stiffness);
        var decay = damping * omega;
        var ringing = omega * Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-decay * seconds) * (Math.Cos(ringing * seconds) + decay / ringing * Math.Sin(ringing * seconds));
    }

    /// <summary>
    /// MaterialShapes, rebuilt from their published vertex-and-rounding definitions (material-components-android,
    /// MaterialShapes.java) and sampled as radius by angle. Rounding is the platform's: a circular fillet of the
    /// given radius at each vertex, with every cut scaled down together where two would overlap on one edge.
    /// </summary>
    private static class MaterialShapeTables
    {
        internal static double[][] Build(int samples) => new[]
        {
            Table(samples, Custom(new[] { (0.193, 0.277, 0.053), (0.176, 0.055, 0.053) }, 10, mirroring: false)), // soft burst
            Table(samples, Star(9, 0.8, 0.5, rotateDegrees: -90)),                                                  // cookie 9
            Table(samples, Custom(new[] { (0.500, -0.009, 0.172) }, 5, mirroring: false)),                         // pentagon
            Table(samples, Custom(new[] { (0.961, 0.039, 0.426), (1.001, 0.428, 0.0), (1.000, 0.609, 1.0) }, 2, mirroring: true)), // pill
            Table(samples, Star(8, 0.8, 0.15, rotateDegrees: 0)),                                                   // sunny
            Table(samples, Custom(new[] { (1.237, 1.236, 0.258), (0.500, 0.918, 0.233) }, 4, mirroring: false)),   // cookie 4
            Oval(samples, 0.64, -45),                                                                               // oval
        };

        /// <summary>MaterialShapes.customPolygon: a template of vertices in a unit box about (0.5, 0.5), repeated
        /// round the centre - and with mirroring, alternately reversed - then centred on the origin.</summary>
        private static (double X, double Y, double Round)[] Custom((double X, double Y, double Round)[] template, int repeat, bool mirroring)
        {
            var radial = template.Select(v => (Angle: Math.Atan2(v.Y - 0.5, v.X - 0.5), Distance: Math.Sqrt((v.X - 0.5) * (v.X - 0.5) + (v.Y - 0.5) * (v.Y - 0.5)), v.Round)).ToArray();
            var output = new List<(double, double, double)>();
            var span = 2 * Math.PI / repeat;
            if (mirroring)
            {
                span /= 2;
                for (var i = 0; i < repeat * 2; i++)
                    for (var j = 0; j < radial.Length; j++)
                    {
                        var reverse = i % 2 != 0;
                        var index = reverse ? radial.Length - 1 - j : j;
                        if (index == 0 && reverse) continue;
                        var angle = span * i + (reverse ? span - radial[index].Angle + 2 * radial[0].Angle : radial[index].Angle);
                        output.Add((radial[index].Distance * Math.Cos(angle), radial[index].Distance * Math.Sin(angle), radial[index].Round));
                    }
            }
            else
            {
                for (var i = 0; i < repeat; i++)
                    foreach (var v in radial)
                        output.Add((v.Distance * Math.Cos(span * i + v.Angle), v.Distance * Math.Sin(span * i + v.Angle), v.Round));
            }

            return output.ToArray();
        }

        /// <summary>The androidx star(): outer vertices at radius 1 from angle 0, inner ones between them.</summary>
        private static (double X, double Y, double Round)[] Star(int points, double inner, double round, double rotateDegrees)
        {
            var rotate = rotateDegrees * Math.PI / 180;
            var output = new (double, double, double)[points * 2];
            for (var i = 0; i < points; i++)
            {
                var outer = Math.PI * 2 * i / points + rotate;
                var between = Math.PI * (2 * i + 1) / points + rotate;
                output[2 * i] = (Math.Cos(outer), Math.Sin(outer), round);
                output[2 * i + 1] = (inner * Math.Cos(between), inner * Math.Sin(between), round);
            }

            return output;
        }

        private static double[] Oval(int samples, double ratio, double rotateDegrees)
        {
            var rotate = rotateDegrees * Math.PI / 180;
            var table = new double[samples];
            for (var j = 0; j < samples; j++)
            {
                var a = 2 * Math.PI * j / samples - rotate;
                table[j] = 1 / Math.Sqrt(Math.Cos(a) * Math.Cos(a) + Math.Sin(a) * Math.Sin(a) / (ratio * ratio));
            }

            return table;
        }

        /// <summary>Round every corner, trace the outline densely, then read its radius at each sample angle and
        /// normalise so the farthest point is 1 - MaterialShapes.normalize(radial: true).</summary>
        private static double[] Table(int samples, (double X, double Y, double Round)[] polygon)
        {
            var n = polygon.Length;
            var cut = new double[n];
            var tanHalf = new double[n];
            for (var i = 0; i < n; i++)
            {
                var (u1, u2) = Edges(polygon, i);
                var angle = Math.Acos(Math.Clamp(u1.X * u2.X + u1.Y * u2.Y, -1, 1));
                tanHalf[i] = Math.Tan(angle / 2);
                cut[i] = tanHalf[i] < 1e-9 ? 0 : polygon[i].Round / tanHalf[i];
            }

            // Where the two cuts on an edge would overlap, both shrink by the same ratio.
            var allowed = Enumerable.Repeat(1.0, n).ToArray();
            for (var i = 0; i < n; i++)
            {
                var next = (i + 1) % n;
                var length = Distance(polygon[i], polygon[next]);
                var want = cut[i] + cut[next];
                if (want <= length || want < 1e-12) continue;
                var ratio = length / want;
                allowed[i] = Math.Min(allowed[i], ratio);
                allowed[next] = Math.Min(allowed[next], ratio);
            }

            var outline = new List<Point>();
            for (var i = 0; i < n; i++)
            {
                var (u1, u2) = Edges(polygon, i);
                var vertex = new Point(polygon[i].X, polygon[i].Y);
                var c = cut[i] * allowed[i];
                var start = vertex + u1 * c;
                var end = vertex + u2 * c;
                if (c < 1e-9)
                {
                    outline.Add(vertex);
                    continue;
                }

                var bisector = u1 + u2;
                bisector.Normalize();
                var radius = c * tanHalf[i];
                var sinHalf = tanHalf[i] / Math.Sqrt(1 + tanHalf[i] * tanHalf[i]);
                var pivot = vertex + bisector * (radius / sinHalf);
                var a0 = Math.Atan2(start.Y - pivot.Y, start.X - pivot.X);
                var a1 = Math.Atan2(end.Y - pivot.Y, end.X - pivot.X);
                var sweep = Math.Atan2(Math.Sin(a1 - a0), Math.Cos(a1 - a0));
                for (var s = 0; s <= 12; s++)
                {
                    var a = a0 + sweep * s / 12;
                    outline.Add(new Point(pivot.X + radius * Math.Cos(a), pivot.Y + radius * Math.Sin(a)));
                }
            }

            // Dense along the straight runs too: radius is not linear in angle along a chord.
            var dense = new List<(double Angle, double Radius)>();
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                for (var s = 0; s < 16; s++)
                {
                    var p = a + (b - a) * (s / 16.0);
                    dense.Add((Math.Atan2(p.Y, p.X), Math.Sqrt(p.X * p.X + p.Y * p.Y)));
                }
            }

            dense.Sort((p, q) => p.Angle.CompareTo(q.Angle));
            var table = new double[samples];
            for (var j = 0; j < samples; j++)
            {
                var angle = Math.Atan2(Math.Sin(2 * Math.PI * j / samples), Math.Cos(2 * Math.PI * j / samples));
                // First outline point at or past this angle, by bisection: the tables are built on first use,
                // on the UI thread, and a linear scan per sample made that a visible few milliseconds.
                int lo = 0, hi = dense.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (dense[mid].Angle < angle) lo = mid + 1;
                    else hi = mid;
                }

                var after = lo == dense.Count ? dense[0] : dense[lo];
                var before = lo == 0 || lo == dense.Count ? dense[^1] : dense[lo - 1];
                var gap = after.Angle - before.Angle;
                if (gap <= 0) gap += 2 * Math.PI;
                var into = angle - before.Angle;
                if (into < 0) into += 2 * Math.PI;
                table[j] = before.Radius + (after.Radius - before.Radius) * (gap < 1e-12 ? 0 : into / gap);
            }

            var max = table.Max();
            for (var j = 0; j < samples; j++) table[j] /= max;
            return table;
        }

        private static (Vector ToPrevious, Vector ToNext) Edges((double X, double Y, double Round)[] polygon, int i)
        {
            var n = polygon.Length;
            var v = polygon[i];
            var p = polygon[(i + n - 1) % n];
            var q = polygon[(i + 1) % n];
            var u1 = new Vector(p.X - v.X, p.Y - v.Y);
            var u2 = new Vector(q.X - v.X, q.Y - v.Y);
            u1.Normalize();
            u2.Normalize();
            return (u1, u2);
        }

        private static double Distance((double X, double Y, double Round) a, (double X, double Y, double Round) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
