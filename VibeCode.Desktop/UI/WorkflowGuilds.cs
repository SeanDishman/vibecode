using System.Windows.Media;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// Provider guilds for the drawn wall views. Running several CLIs side by side is the point of the app, so a villager or

/// while its hair and skin stay hashed from its own id, and its role shows in what it carries: an orchestrator keeps
/// its crown, a Bridge worker wears a tool belt and the central planner holds a scroll. Subagents run errands in their
/// parent's colours. With Settings › Appearance › Guild colours off, every shirt is hashed from the agent id again.
/// </summary>
public static class WorkflowGuilds
{
    private static readonly (string Provider, string Label, Color Tabard)[] Guilds =
    [
        ("claude", "Claude", PixelArt.C("#E39A3B")), ("codex", "Codex", PixelArt.C("#2FB5A8")),
        (Protocol.GlmPreset.ProviderId, "GLM", PixelArt.C("#5368E0")), ("kimi", "Kimi", PixelArt.C("#9B68DE")), ("gemini", "Gemini", PixelArt.C("#3FA6EE")),
    ];

    public static bool Enabled => AppSettings.Current.WorkflowGuildColours;

    /// <summary>The provider an agent's turns run on. The central planner runs its own session, whose provider can
    /// differ from the host chat it is drawn from.</summary>
    public static string ProviderOf(AgentActivityNode agent) => ProviderModelCatalog.Normalize(
        agent.IsCentralPlanner ? agent.Chat.BridgeWork?.CentralPlan?.Provider ?? agent.Chat.Provider : agent.Chat.Provider);

    /// <summary>The guild tabard for a provider, or null for one without a guild (its agents keep hashed shirts).</summary>
    public static Color? Tabard(string provider)
    {
        foreach (var guild in Guilds) if (guild.Provider == provider) return guild.Tabard;
        return null;
    }

    public static string Label(string provider)
    {
        foreach (var guild in Guilds) if (guild.Provider == provider) return guild.Label;
        return ProviderModelCatalog.DisplayName(provider);
    }

    /// <summary>The shirt override for an agent's sprite: its guild's tabard while guild colours are on, otherwise
    /// null so the sprite keeps the colour hashed from its id.</summary>
    public static Color? Shirt(AgentActivityNode agent) => Enabled ? Tabard(ProviderOf(agent)) : null;

    /// <summary>The colour an agent reads as on the wall - its tabard, or its hashed shirt - for name tags and keepsakes.</summary>
    public static Color ShirtOf(AgentActivityNode agent) => Shirt(agent) ?? PixelArt.ShirtOf(agent.Id);

    /// <summary>What an agent carries for its role. Orchestrators already wear the crown the views draw over them.</summary>
    internal static PixelArt.Prop PropOf(AgentActivityNode agent) =>
        agent.IsCentralPlanner ? PixelArt.Prop.Scroll
        : agent.Chat.IsBridgeManager ? PixelArt.Prop.None
        : agent.Role is "Worker" or "Bridge" ? PixelArt.Prop.Belt : PixelArt.Prop.None;

    /// <summary>The guilds present on the wall, in a stable order, for the legend chip. Empty while guild colours are off.</summary>
    public static IReadOnlyList<(string Label, Color Tabard)> Present(IReadOnlyList<AgentActivityProject> projects)
    {
        if (!Enabled) return [];
        var providers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var agent in project.Agents) providers.Add(ProviderOf(agent));
        return Guilds.Where(g => providers.Contains(g.Provider)).Select(g => (g.Label, g.Tabard)).ToArray();
    }
}
