namespace VibeCode.Services;

public enum ThinkingOrbStyle { Globe, Gyroscope, Atom, Morph, Helix, Knot }

public sealed record ThinkingOrbOption(string Id, string Name, string Description, ThinkingOrbStyle Style)
{
    public string AutomationName => $"{Name} thinking orb";
}

/// <summary>Stable preference IDs, shared by the renderer and Appearance picker.</summary>
public static class ThinkingOrbStyles
{
    public const string DefaultId = "globe";
    public const string ResourceKey = "ThinkingOrbStyle";

    public static IReadOnlyList<ThinkingOrbOption> All { get; } = Array.AsReadOnly(new[]
    {
        new ThinkingOrbOption(DefaultId, "Globe", "A dotted globe turning under a sweeping scan", ThinkingOrbStyle.Globe),
        new ThinkingOrbOption("gyroscope", "Gyroscope", "Three tumbling rings", ThinkingOrbStyle.Gyroscope),
        new ThinkingOrbOption("atom", "Atom", "Electrons circling all the way round a nucleus", ThinkingOrbStyle.Atom),
        new ThinkingOrbOption("morph", "Morph", "A shape that springs from form to form", ThinkingOrbStyle.Morph),
        new ThinkingOrbOption("helix", "Helix", "Two sculpted strands turning around a shared axis", ThinkingOrbStyle.Helix),
        new ThinkingOrbOption("knot", "Knot", "A continuous strand weaving over and under itself", ThinkingOrbStyle.Knot),
    });

    /// <summary>Unknown and retired IDs (the old Orbit, Comet, Particles, Pulse, Ripple and Wave) fall back to
    /// the default rather than failing.</summary>
    public static ThinkingOrbOption Resolve(string? id)
    {
        // Carry an existing selection to its replacement; never leave a removed style in the picker.
        var normalized = id?.Trim().ToLowerInvariant() switch
        {
            "nebula" => "helix",
            "spark" => "knot",
            var current => current,
        };
        return All.FirstOrDefault(option => option.Id == normalized) ?? All[0];
    }
}
