namespace VibeCode.Services;

/// <summary>A settings target and its current location in the real Settings window.</summary>
public sealed record SettingsDestination(string Category, string RailName, string? SectionName, string Path);

/// <summary>Shared by Settings navigation and Jarvis so section names cannot masquerade as old rail tabs.</summary>
public static class SettingsNavigation
{
    public static IReadOnlyList<SettingsDestination> Destinations { get; } =
    [
        new("General", "General", null, "Settings > General"),
        new("Appearance", "Appearance", null, "Settings > Appearance"),
        new("Notifications", "General", "SectionNotifications", "Settings > General > Notifications"),
        new("Bridge", "Bridge and agents", null, "Settings > Bridge & agents"),
        new("Usage", "Usage", null, "Settings > Usage"),
        new("Projects", "General", "SectionProjects", "Settings > General > Projects"),
        new("MCP servers", "MCP and extensions", "McpServersCard", "Settings > MCP & extensions > MCP servers"),
        new("Extensions", "MCP and extensions", "SectionExtensions", "Settings > MCP & extensions > Extensions"),
        new("Jarvis", "Jarvis", null, "Settings > Jarvis"),
        new("Privacy", "General", "SectionPrivacy", "Settings > General > Privacy"),
        new("About", "General", "SectionAbout", "Settings > General > About"),
        new("MCP & extensions", "MCP and extensions", null, "Settings > MCP & extensions"),
    ];

    public static SettingsDestination? Resolve(string? category)
    {
        var name = category?.Trim();
        var canonical = name?.ToLowerInvariant() switch
        {
            "bridge & agents" or "bridge and agents" => "Bridge",
            "mcp and extensions" => "MCP & extensions",
            "mcp" => "MCP servers",
            _ => name,
        };
        if (string.Equals(canonical, "Hidden projects", StringComparison.OrdinalIgnoreCase))
            return new("Hidden projects", "General", "HiddenProjectsCard", "Settings > General > Projects > Hidden projects");
        return Destinations.FirstOrDefault(destination =>
            destination.Category.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeCategory(string? category) => Resolve(category)?.Category;

    /// <summary>A pane read includes its sections; a section read stays scoped to that section.</summary>
    public static bool ContainsCategory(string selectedCategory, string preferenceCategory)
    {
        var selected = Resolve(selectedCategory);
        var preference = Resolve(preferenceCategory);
        return selected is not null && preference is not null
            && (selected.Category == preference.Category
                || selected.SectionName is null && selected.RailName == preference.RailName);
    }
}
