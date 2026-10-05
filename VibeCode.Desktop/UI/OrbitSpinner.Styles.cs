using System.Windows;
using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class OrbitSpinner
{
    public static readonly DependencyProperty StyleKindProperty = DependencyProperty.Register(
        nameof(StyleKind), typeof(ThinkingOrbStyle?), typeof(OrbitSpinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Keep the global resource on its own property. A local resource on StyleKind would outrank the
    // Appearance DataTemplate's binding, making every preview show the currently selected style.
    private static readonly DependencyProperty PreferredStyleProperty = DependencyProperty.Register(
        "PreferredStyle", typeof(ThinkingOrbStyle), typeof(OrbitSpinner),
        new FrameworkPropertyMetadata(ThinkingOrbStyles.All[0].Style, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>An explicit preview style; null follows the saved Appearance preference.</summary>
    public ThinkingOrbStyle? StyleKind
    {
        get => (ThinkingOrbStyle?)GetValue(StyleKindProperty);
        set => SetValue(StyleKindProperty, value);
    }

    public ThinkingOrbStyle EffectiveStyle => StyleKind ?? (ThinkingOrbStyle)GetValue(PreferredStyleProperty);

    /// <summary>Everything the styles keep beyond the shared bead and wire, rebuilt with them.</summary>
    private void BuildStyleResources(Color colour)
    {
        BuildGlobeResources(colour);
        BuildAtomResources(colour);
        BuildMorphResources(colour);
        BuildNebulaResources(colour);
        BuildSparkResources(colour);
    }

    private void DrawSelectedStyle(DrawingContext dc, Point centre, double half, double thickness, double phase)
    {
        switch (EffectiveStyle)
        {
            case ThinkingOrbStyle.Gyroscope: DrawGyroscope(dc, centre, half, thickness, phase); break;
            case ThinkingOrbStyle.Atom: DrawAtom(dc, centre, half, thickness, phase); break;
            case ThinkingOrbStyle.Morph: DrawMorph(dc, centre, half, phase); break;
            case ThinkingOrbStyle.Nebula: DrawNebula(dc, centre, half, phase); break;
            case ThinkingOrbStyle.Spark: DrawSpark(dc, centre, half, phase); break;
            // Globe, and anything unknown from an older or newer resource dictionary.
            default: DrawGlobe(dc, centre, half, phase); break;
        }
    }
}
