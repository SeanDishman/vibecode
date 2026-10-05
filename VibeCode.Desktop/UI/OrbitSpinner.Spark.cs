using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Spark: a four-pointed sparkle that snaps round a quarter turn on every beat, with small sparkles twinkling
/// beside it while it rests - the shape the industry settled on for "AI is working" (Gemini, Firefly and most
/// assistants since).
///
/// Bold because it is filled: at 22px the main star is still thirteen pixels tip to tip. The points are a
/// superellipse with an exponent above 2, which pinches the sides in and leaves the tips sharp without a single
/// special case; the fill is lit from its centre so the star reads as light rather than as a cut-out.
///
/// The turn rides a spring, so it lands with a small overshoot instead of a mechanical stop, and the star
/// squashes a little while it turns. The companions only appear in the rest after each turn, in the gaps
/// between the main star's points, so the two never cross.
/// </summary>
public sealed partial class OrbitSpinner
{
    private const double SparkFrac = 0.58;
    private const double SparkPinch = 3.1;
    private const int SparkSamples = 72;
    private const int SparkBeats = 4;

    private readonly Point[] _sparkAt = new Point[SparkSamples];
    private Brush? _sparkFill;

    private void BuildSparkResources(Color colour)
    {
        _sparkFill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Lerp(colour, Colors.White, 0.92), 0.00),
            new(Lerp(colour, Colors.White, 0.50), 0.18),
            new(Lerp(colour, Colors.White, 0.08), 0.50),
            new(colour, 1.00),
        }));
    }

    private void DrawSpark(DrawingContext dc, Point centre, double half, double phase)
    {
        var position = phase * SparkBeats;
        var beat = Math.Min(SparkBeats - 1, (int)position);
        var local = position - beat;
        // A softer spring than Morph's: a quarter turn at Android's stiffness lands in a fifth of a second, which
        // on a mark that may sit on screen for minutes reads as a twitch. This one takes about a third of a
        // second, overshoots by about 12% and is at rest well before the beat ends.
        var turn = SpringStep(local, Period.TotalSeconds / SparkBeats, stiffness: 70, damping: 0.55);

        // Squash while turning and spring back as it lands; flare the glow on the way.
        var effort = Math.Sin(Math.PI * Math.Clamp(local / 0.45, 0, 1));
        var size = SparkFrac * half * (1 - 0.14 * effort);
        dc.DrawEllipse(_glow[Math.Clamp((int)Math.Round((GlowTones - 1) * (0.50 + 0.40 * effort)), 0, GlowTones - 1)],
            null, centre, half * 0.66, half * 0.66);
        DrawStar(dc, centre, size, Math.PI / 2 * (beat + turn));

        // In the rest after each turn, a small sparkle twinkles in one of the diagonal gaps, alternating sides.
        // Big enough to still be a star at 22px (six pixels tip to tip) rather than a stray speck; its points
        // stay two pixels inside the box even at full size.
        var gap = beat % 2 == 0 ? new Vector(0.53, -0.53) : new Vector(-0.53, 0.53);
        var twinkle = Math.Clamp((local - 0.40) / 0.55, 0, 1);
        var pop = Math.Sin(Math.PI * twinkle);
        DrawStar(dc, centre + gap * half, 0.26 * half * pop, Math.PI / 4 * twinkle);
    }

    private void DrawStar(DrawingContext dc, Point at, double size, double rotation)
    {
        if (size < 0.3) return;
        double cr = Math.Cos(rotation), sr = Math.Sin(rotation);
        for (var i = 0; i < SparkSamples; i++)
        {
            var a = Tau * i / SparkSamples;
            double c = Math.Cos(a), s = Math.Sin(a);
            var x = Math.Sign(c) * Math.Pow(Math.Abs(c), SparkPinch) * size;
            var y = Math.Sign(s) * Math.Pow(Math.Abs(s), SparkPinch) * size;
            _sparkAt[i] = new Point(at.X + x * cr - y * sr, at.Y + x * sr + y * cr);
        }

        var star = new StreamGeometry();
        using (var open = star.Open())
        {
            open.BeginFigure(_sparkAt[0], isFilled: true, isClosed: true);
            for (var i = 1; i < SparkSamples; i++) open.LineTo(_sparkAt[i], isStroked: false, isSmoothJoin: false);
        }

        dc.DrawGeometry(_sparkFill, null, Frozen(star));
    }
}
