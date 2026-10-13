using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

public sealed partial class OrbitSpinner
{
    private const int HelixSegments = 48;

    /// <summary>A double helix with an open centre, a stable axis, and continuous front-to-back rotation.</summary>
    private void DrawHelix(DrawingContext dc, Point centre, double half, double phase)
    {
        EnsureFilamentResources(half);
        var turn = Tau * phase;
        var lean = -0.43 + 0.07 * Math.Sin(turn);
        var cosine = Math.Cos(lean);
        var sine = Math.Sin(lean);

        (Point At, double Z) At(double height, int strand)
        {
            var angle = turn + Tau * height + strand * Math.PI;
            var radius = half * (0.34 + 0.055 * Math.Sin(Math.PI * height));
            var x = radius * Math.Cos(angle);
            var y = half * 1.46 * (height - 0.5);
            var z = Math.Sin(angle);
            return (new(centre.X + x * cosine - y * sine, centre.Y + x * sine + y * cosine), z);
        }

        // Seven quiet connections make the twist legible even at 22 DIPs. Draw them underneath the solid
        // strands so each junction is clean, without pulsing intersections or a bright central ladder.
        for (var rung = 0; rung < 7; rung++)
        {
            var height = (rung + 0.5) / 7;
            dc.DrawLine(_helixRung, At(height, 0).At, At(height, 1).At);
        }
        for (var strand = 0; strand < 2; strand++)
        {
            var from = At(0, strand);
            for (var i = 0; i < HelixSegments; i++)
            {
                var to = At((i + 1.0) / HelixSegments, strand);
                SetFilament(strand * HelixSegments + i, from.At, to.At, (from.Z + to.Z) * 0.5);
                from = to;
            }
        }
        DrawFilaments(dc, 2 * HelixSegments, style: 0);
    }
}
