using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

public sealed partial class OrbitSpinner
{
    /// <summary>Three rings tumbling through each other, each shaded by its own depth.</summary>
    private void DrawGyroscope(DrawingContext dc, Point centre, double half, double thickness, double phase)
    {
        for (var ring = 0; ring < 3; ring++)
        {
            var tilt = 0.83 + 0.36 * Math.Sin(Tau * phase + ring * Tau / 3);
            var rotation = Tau * phase + ring * Math.PI / 3;
            var radius = half * 0.79;
            var minor = radius * Math.Cos(tilt);
            TraceEllipse(centre, radius, minor, rotation);
            var depthAxis = new Vector(-Math.Sin(rotation), Math.Cos(rotation));
            dc.DrawGeometry(null, Wire(centre, depthAxis, minor, Math.Sin(tilt), thickness), Ring());
        }
    }
}
