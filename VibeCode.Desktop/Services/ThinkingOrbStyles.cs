namespace VibeCode.Services;

public enum ThinkingOrbStyle { Globe, Gyroscope, Atom, Morph, Nebula, Spark }

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
        new ThinkingOrbOption("nebula", "Nebula", "A glowing orb of drifting light", ThinkingOrbStyle.Nebula),
        new ThinkingOrbOption("spark", "Spark", "A twinkling sparkle", ThinkingOrbStyle.Spark),
    });

    /// <summary>Unknown and retired IDs (the old Orbit, Comet, Particles, Pulse, Ripple and Wave) fall back to
    /// the default rather than failing.</summary>
    public static ThinkingOrbOption Resolve(string? id) =>
        All.FirstOrDefault(option => string.Equals(option.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
