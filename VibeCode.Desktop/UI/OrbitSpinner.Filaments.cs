using System.Windows;
using System.Windows.Media;

namespace VibeCode.UI;

public sealed partial class OrbitSpinner
{
    private const int FilamentTones = 48;
    private const int FilamentCapacity = 144;
    private readonly record struct Filament(Point From, Point To, double Depth, int Tone);
    private readonly Filament[] _filaments = new Filament[FilamentCapacity];
    private readonly double[] _filamentDepth = new double[FilamentCapacity];
    private readonly int[] _filamentOrder = new int[FilamentCapacity];
    private readonly Pen[] _filamentBody = new Pen[FilamentTones * 2];
    private readonly Pen[] _filamentLight = new Pen[FilamentTones * 2];
    private Pen? _helixRung;
    private bool _filamentBuilt;
    private double _filamentHalf;

    // Opaque, rounded strokes make real over/under crossings. A narrow offset highlight describes the tube's
    // surface; transparent glow at every joint would instead build bright beads along a dashed-looking line.
    // All resources and sort buffers are reused. A frame allocates only WPF's drawing instructions.
    private void EnsureFilamentResources(double half)
    {
        if (_filamentBuilt && Math.Abs(_filamentHalf - half) < 0.001) return;
        _filamentHalf = half;
        _filamentBuilt = true;
        var colour = Lerp(Colors.Black, _builtColour, Math.Clamp(_gain, 0.75, 1));
        for (var style = 0; style < 2; style++)
            for (var tone = 0; tone < FilamentTones; tone++)
            {
                var depth = tone / (double)(FilamentTones - 1);
                var body = Lerp(Colors.Black, colour, 0.42 + 0.58 * depth);
                var light = Lerp(body, Colors.White, 0.12 + 0.42 * depth * depth);
                var width = Math.Max(style == 0 ? 1.35 : 1.5, half * (style == 0 ? 0.147 : 0.175)) * (0.9 + 0.2 * depth);
                var index = style * FilamentTones + tone;
                _filamentBody[index] = FilamentPen(body, width);
                _filamentLight[index] = FilamentPen(light, width * 0.36);
            }
        _helixRung = FilamentPen(Lerp(Colors.Black, colour, 0.48), Math.Max(0.65, half * 0.057));
    }

    private static Pen FilamentPen(Color colour, double width) => Frozen(new Pen(Frozen(new SolidColorBrush(colour)), width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    });

    private void SetFilament(int index, Point from, Point to, double depth)
    {
        var tone = Math.Clamp((int)Math.Round((depth + 1) * 0.5 * (FilamentTones - 1)), 0, FilamentTones - 1);
        _filaments[index] = new(from, to, depth, tone);
        _filamentDepth[index] = depth;
        _filamentOrder[index] = index;
    }

    private void DrawFilaments(DrawingContext dc, int count, int style)
    {
        Array.Sort(_filamentDepth, _filamentOrder, 0, count);
        for (var i = 0; i < count; i++)
        {
            var segment = _filaments[_filamentOrder[i]];
            var tone = style * FilamentTones + segment.Tone;
            var body = _filamentBody[tone];
            var lightOffset = new Vector(-body.Thickness * 0.13, -body.Thickness * 0.17);
            dc.DrawLine(body, segment.From, segment.To);
            dc.DrawLine(_filamentLight[tone], segment.From + lightOffset, segment.To + lightOffset);
        }
    }
}
