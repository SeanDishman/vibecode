using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

public sealed partial class OrbitSpinner
{
    private const int KnotSegments = 144;

    /// <summary>A trefoil with three open lobes and solid over/under crossings, shaded as one continuous tube.</summary>
    private void DrawKnot(DrawingContext dc, Point centre, double half, double phase)
    {
        EnsureFilamentResources(half);
        var turn = Tau * phase;
        // Threefold symmetry closes the loop after 120 degrees. Rotate BEFORE the camera tilt so the seam
        // is an exact symmetry, including depth and shading, rather than a jump to a similar silhouette.
        var rotation = turn / 3 + 0.32;
        var tilt = 0.48 + 0.12 * Math.Sin(turn);
        var cr = Math.Cos(rotation); var sr = Math.Sin(rotation);
        var ct = Math.Cos(tilt); var st = Math.Sin(tilt);
        var scale = half * 0.35;

        (Point At, double Z) At(double angle)
        {
            var radius = 1.65 + 0.60 * Math.Cos(3 * angle);
            var x = radius * Math.Cos(2 * angle);
            var y = radius * Math.Sin(2 * angle);
            var z = 0.72 * Math.Sin(3 * angle);
            var rotatedX = x * cr - y * sr;
            var rotatedY = x * sr + y * cr;
            return (new(centre.X + scale * rotatedX, centre.Y + scale * (rotatedY * ct - z * st)),
                (rotatedY * st + z * ct) / 1.8);
        }

        var from = At(0);
        for (var i = 0; i < KnotSegments; i++)
        {
            var to = At(Tau * (i + 1) / KnotSegments);
            SetFilament(i, from.At, to.At, (from.Z + to.Z) * 0.5);
            from = to;
        }
        DrawFilaments(dc, KnotSegments, style: 1);
    }
}
