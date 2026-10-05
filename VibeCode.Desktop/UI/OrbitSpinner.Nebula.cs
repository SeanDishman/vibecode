using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

/// <summary>
/// Nebula: a glass orb with light drifting around inside it - the voice-assistant orb (Siri, ChatGPT's voice
/// mode, ElevenLabs' agent orb) done with plain fills.
///
/// Those orbs get their life from a few soft blobs of neighbouring hues drifting out of step with each other,
/// so the colour field never settles into a pattern. The same recipe works here: three radial blobs - the
/// accent and the hues either side of it - each on its own slow loop, a small bright core wandering among them,
/// a dark glass body that gets brighter towards its rim, and a fixed highlight that says "sphere". Each blob
/// fades to nothing before it reaches the rim, so nothing needs clipping and nothing allocates a layer.
/// </summary>
public sealed partial class OrbitSpinner
{
    private const double NebulaFrac = 0.70;
    private const double BlobFrac = 0.70;

    private readonly Brush[] _nebulaBlobs = new Brush[3];
    private Brush? _nebulaCore, _nebulaBody, _nebulaHalo, _nebulaShine;

    private void BuildNebulaResources(Color colour)
    {
        var warm = HueShift(colour, 42);
        var cool = HueShift(colour, -38);
        _nebulaBlobs[0] = Blob(Lerp(colour, Colors.White, 0.06), 0.95);
        _nebulaBlobs[1] = Blob(Lerp(warm, Colors.White, 0.08), 0.92);
        _nebulaBlobs[2] = Blob(Lerp(cool, Colors.White, 0.16), 0.90);
        _nebulaCore = Blob(Lerp(colour, Colors.White, 0.85), 0.80);
        _nebulaShine = Blob(Colors.White, 0.45);

        // Dark glass, catching light in a thin band at the rim the way a sphere does at grazing angles.
        _nebulaBody = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Tint(Lerp(colour, Colors.Black, 0.55), 0.62), 0.0),
            new(Tint(Lerp(colour, Colors.Black, 0.42), 0.66), 0.84),
            new(Tint(Lerp(colour, Colors.White, 0.12), 0.72 * _gain), 0.96),
            new(Tint(colour, 0.30 * _gain), 1.0),
        }));

        _nebulaHalo = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Tint(colour, 0.26 * _gain), 0.0),
            new(Tint(colour, 0.20 * _gain), 0.72),
            new(Tint(colour, 0.06 * _gain), 0.88),
            new(Tint(colour, 0), 1.0),
        }));
    }

    private Brush Blob(Color colour, double alpha) => Frozen(new RadialGradientBrush(new GradientStopCollection
    {
        new(Tint(colour, alpha * _gain), 0.00),
        new(Tint(colour, alpha * 0.62 * _gain), 0.35),
        new(Tint(colour, alpha * 0.16 * _gain), 0.72),
        new(Tint(colour, 0), 1.00),
    }));

    private void DrawNebula(DrawingContext dc, Point centre, double half, double phase)
    {
        var radius = NebulaFrac * half * (1 + 0.03 * Math.Sin(Tau * 2 * phase + 0.4));
        var blob = BlobFrac * radius;
        var t = Tau * phase;

        dc.DrawEllipse(_nebulaHalo, null, centre, half * 0.95, half * 0.95);
        dc.DrawEllipse(_nebulaBody, null, centre, radius, radius);

        // Three loops at different speeds and directions, each a whole number of laps per cycle.
        dc.DrawEllipse(_nebulaBlobs[0], null, Offset(centre, radius, 0.30 * Math.Cos(t + 0.5), 0.27 * Math.Sin(2 * t + 1.3)), blob, blob);
        dc.DrawEllipse(_nebulaBlobs[1], null, Offset(centre, radius, 0.30 * Math.Cos(-2 * t + 2.6), 0.28 * Math.Sin(t + 0.4)), blob, blob);
        dc.DrawEllipse(_nebulaBlobs[2], null, Offset(centre, radius, 0.28 * Math.Sin(-t + 4.1), 0.30 * Math.Cos(2 * t + 0.9)), blob, blob);
        dc.DrawEllipse(_nebulaCore, null, Offset(centre, radius, 0.14 * Math.Cos(-t + 1.0), 0.12 * Math.Sin(2 * t + 2.2)),
            radius * 0.36, radius * 0.36);

        // The window highlight stays put while everything moves under it.
        dc.DrawEllipse(_nebulaShine, null, Offset(centre, radius, -0.34, -0.40), radius * 0.30, radius * 0.22);
    }

    private static Point Offset(Point centre, double radius, double x, double y) =>
        new(centre.X + x * radius, centre.Y + y * radius);
}
