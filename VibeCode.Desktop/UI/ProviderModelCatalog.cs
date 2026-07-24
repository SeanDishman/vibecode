using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// A small presentation cache for provider model menus. A running chat owns its real <see cref="ChatViewModel.Models"/>
/// catalog; this cache only lets an already-open chat preview the provider selected for NEW chats without changing
/// that chat's session, model, account, or process.
/// </summary>
internal static class ProviderModelCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<ModelChoice>> Catalogs =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? "claude" : provider.Trim().ToLowerInvariant();

    public static string DisplayName(string? provider) => Normalize(provider) switch
    {
        "codex" => "OpenAI Codex",
        "claude" => "Claude Code",
        "kimi" => "Kimi Code",
        "grok" => "Grok",
        var value => char.ToUpperInvariant(value[0]) + value[1..],
    };

    /// <summary>Remember the authoritative catalog returned by a live provider session.</summary>
    public static void Remember(string provider, IEnumerable<ModelChoice> models)
    {
        var key = Normalize(provider);
        var snapshot = models.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList();
        if (snapshot.Count == 0) return;
        if (key == "claude") NormalizeClaudeCatalog(snapshot);
        lock (Gate) Catalogs[key] = snapshot;
    }

    /// <summary>Return the most recently observed live catalog, or an instant alias-only preview before one exists.</summary>
    public static IReadOnlyList<ModelChoice> For(string provider)
    {
        var key = Normalize(provider);
        lock (Gate)
        {
            if (Catalogs.TryGetValue(key, out var catalog))
            {
                if (key != "claude") return catalog;
                // Old sessions may have Remember'd a catalog before Opus 5 existed — re-merge.
                var merged = catalog.ToList();
                if (NormalizeClaudeCatalog(merged)) Catalogs[key] = merged;
                return Catalogs[key];
            }
        }
        return Fallback(key);
    }

    /// <summary>
    /// Ensure a chat pane's live <see cref="ChatViewModel.Models"/> list carries the known Claude rows
    /// (Opus 5, Opus 4.8, Opus 4.6), titles each tier alias after the model it really resolves to, and is
    /// ordered strongest-first with "default" at the very bottom.
    /// </summary>
    public static void EnsureClaudeModelsVisible(IList<ModelChoice> models)
    {
        if (models is List<ModelChoice> list)
            NormalizeClaudeCatalog(list);
        else
        {
            var tmp = models.ToList();
            if (!NormalizeClaudeCatalog(tmp)) return;
            // Rebuild observable / non-list collections in place.
            models.Clear();
            foreach (var m in tmp) models.Add(m);
        }
    }

    /// <summary>
    /// Claude Code's live catalog lags new Anthropic IDs and titles its tier rows with bare aliases, so the
    /// picker listed a nameless "Opus" beside "Claude Opus 5" with nothing to tell them apart, and buried
    /// the strongest models under Haiku. Rename the aliases, add the missing rows, then sort.
    /// </summary>
    /// <returns>True if the list was mutated.</returns>
    private static bool NormalizeClaudeCatalog(List<ModelChoice> models)
    {
        if (models.Count == 0) return false;
        var before = models.Select(m => (m.Value, m.Display)).ToList();

        for (int i = 0; i < models.Count; i++)
            if (AliasName(models[i]) is { } renamed)
                models[i] = WithDisplay(models[i], renamed);

        foreach (var (value, display, description) in ClaudeExtras)
        {
            // Match on the title too: an alias we just renamed "Opus 4.8" already fills that slot, even
            // when the CLI gave us no resolvedModel to prove it is the same model.
            if (models.Any(m => IsSameModel(m, value)
                                || string.Equals(m.Display, display, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Copy effort/fast flags from a live Opus row so the injected row feels native.
            var opus = models.FirstOrDefault(m =>
                m.Value.Contains("opus", StringComparison.OrdinalIgnoreCase)
                || (m.ResolvedModel?.Contains("opus", StringComparison.OrdinalIgnoreCase) ?? false));
            models.Add(new ModelChoice
            {
                Provider = "claude",
                Value = value,
                Display = display,
                Description = description,
                ResolvedModel = value,
                EffortLevels = opus?.EffortLevels?.ToList() ?? new List<string>(),
                SupportsEffort = opus?.SupportsEffort ?? true,
                SupportsAutoMode = opus?.SupportsAutoMode ?? false,
                SupportsFastMode = true,
            });
        }

        var sorted = models.OrderBy(Rank).ToList();   // OrderBy is stable, so same-rank rows keep CLI order
        models.Clear();
        models.AddRange(sorted);

        return !before.SequenceEqual(models.Select(m => (m.Value, m.Display)));
    }

    /// <summary>True when a catalog row already is the given model, ignoring "[1m]"-style variant tags.</summary>
    private static bool IsSameModel(ModelChoice m, string id) =>
        string.Equals(m.Value, id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ModelPricing.CanonicalId(m.ResolvedModel), id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Names to fall back on when a bare tier alias arrives with no resolvedModel to read.</summary>
    private static readonly Dictionary<string, string> AliasNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "Opus 4.8",
        ["sonnet"] = "Sonnet 5",
        ["haiku"] = "Haiku 4.5",
        ["fable"] = "Fable 5",
        ["mythos"] = "Mythos 5",
    };

    /// <summary>
    /// New title for a row the CLI labelled with a bare tier word ("Opus"), or null to leave it alone.
    /// Reads <see cref="ModelChoice.ResolvedModel"/> first so the label stays right if the alias moves.
    /// </summary>
    private static string? AliasName(ModelChoice m)
    {
        if (!AliasNames.TryGetValue(m.Display.Trim(), out var fallback)) return null;
        var named = UsagePalette.KnownName(m.ResolvedModel) ?? fallback;
        return string.Equals(named, m.Display, StringComparison.Ordinal) ? null : named;
    }

    private static ModelChoice WithDisplay(ModelChoice m, string display) => new()
    {
        Provider = m.Provider,
        Value = m.Value,
        Display = display,
        Description = m.Description,
        ResolvedModel = m.ResolvedModel,
        EffortLevels = m.EffortLevels,
        SupportsEffort = m.SupportsEffort,
        SupportsAutoMode = m.SupportsAutoMode,
        SupportsFastMode = m.SupportsFastMode,
    };

    /// <summary>Picker order: Opus 5 then Fable on top, older Opus under them, Sonnet/Haiku low, default last.</summary>
    private static int Rank(ModelChoice m)
    {
        if (string.Equals(m.Value, "default", StringComparison.OrdinalIgnoreCase)) return 900;

        var id = ModelPricing.CanonicalId(m.ResolvedModel);
        if (id == "unknown") id = ModelPricing.CanonicalId(m.Value);
        // "Opus 4.8" -> "opus-4-8", so a row we could only date by its title still sorts with its version.
        var titled = m.Display.Replace(' ', '-').Replace('.', '-');
        bool Has(string part) =>
            id.Contains(part, StringComparison.OrdinalIgnoreCase)
            || m.Value.Contains(part, StringComparison.OrdinalIgnoreCase)
            || titled.Contains(part, StringComparison.OrdinalIgnoreCase);

        if (Has("opus-5") || Has("opus5")) return 0;
        if (Has("fable")) return 10;
        if (Has("mythos")) return 20;
        if (Has("opus-4-8")) return 30;
        if (Has("opus-4-7")) return 31;
        if (Has("opus-4-6")) return 32;
        if (Has("opus-4-5")) return 33;
        if (Has("opus")) return 39;      // bare alias / opusplan with nothing to date it
        if (Has("sonnet")) return 50;
        if (Has("haiku")) return 60;
        return 80;
    }

    /// <summary>
    /// Claude models the local CLI may not advertise in <c>initialize.models</c>. IDs must be the real
    /// Anthropic ones - the picker sends them straight through as <c>--model</c> / set_model.
    /// Kept in sync with <see cref="VibeCode.Protocol.ClaudeSession"/>'s copy.
    /// </summary>
    private static readonly (string Value, string Display, string Description)[] ClaudeExtras =
    [
        ("claude-opus-5", "Claude Opus 5",
            "Near-Fable intelligence for complex agentic coding and enterprise work, at half Fable price."),
        ("claude-opus-4-8", "Opus 4.8",
            "Previous flagship Opus. Highly autonomous on long-horizon agentic and knowledge work."),
        ("claude-opus-4-6", "Opus 4.6",
            "Older Opus. Still accepts temperature and a fixed thinking budget that 4.7+ dropped."),
    ];

    private static IReadOnlyList<ModelChoice> Fallback(string provider) => provider switch
    {
        "claude" =>
        [
            Choice("claude", "claude-opus-5", "Claude Opus 5",
                "Near-Fable intelligence for complex agentic coding and enterprise work, at half Fable price."),
            Choice("claude", "fable", "Fable 5", "Anthropic's most capable model, for the most demanding work."),
            Choice("claude", "claude-opus-4-8", "Opus 4.8",
                "Previous flagship Opus. Highly autonomous on long-horizon agentic and knowledge work."),
            Choice("claude", "claude-opus-4-6", "Opus 4.6",
                "Older Opus. Still accepts temperature and a fixed thinking budget that 4.7+ dropped."),
            Choice("claude", "sonnet", "Sonnet 5", "Balanced speed and capability."),
            Choice("claude", "haiku", "Haiku 4.5", "Fastest Claude model."),
            Choice("claude", "default", "Claude default", "Claude Code chooses the recommended model."),
        ],
        "codex" =>
        [
            Choice("codex", "default", "Codex default", "OpenAI Codex chooses the recommended model."),
            Choice("codex", CodexModelPreset.SolModelId, CodexModelPreset.SolDisplayName,
                CodexModelPreset.SolDescription),
            Choice("codex", CodexModelPreset.TerraModelId, CodexModelPreset.TerraDisplayName,
                CodexModelPreset.TerraDescription),
            Choice("codex", CodexModelPreset.LunaModelId, CodexModelPreset.LunaDisplayName,
                CodexModelPreset.LunaDescription),
        ],
        "kimi" =>
        [
            Choice("kimi", "default", "Kimi Code default",
                "The signed-in Kimi CLI supplies its live model catalog, including K3 when available."),
        ],
        "grok" =>
        [
            Choice("grok", Grok45Preset.NormalModelId, Grok45Preset.NormalDisplayName,
                Grok45Preset.NormalDescription),
        ],
        _ =>
        [
            Choice(provider, "default", $"{DisplayName(provider)} default", $"{DisplayName(provider)} chooses the recommended model."),
        ],
    };

    private static ModelChoice Choice(string provider, string value, string display, string description) => new()
    {
        Provider = provider,
        Value = value,
        Display = display,
        Description = description,
    };
}

/// <summary>One row in a pane's model popup, including whether it belongs to that pane's live provider.</summary>
public sealed class ModelPickerChoice : Observable
{
    public required ModelChoice Model { get; init; }
    public required bool CanApply { get; init; }
    public string Value => Model.Value;
    public string Display => Model.Display;
    public string? Description => Model.Description;
    public string Scope => CanApply ? "" : $"For new {ProviderModelCatalog.DisplayName(Model.Provider)} chats";

    /// <summary>Measured "48 tok/s · 2.9s latency", or "" until a lookup lands (or for a model nobody reports on).</summary>
    public string Speed => ModelSpeedService.Instance.For(Model)?.Text ?? "";

    /// <summary>Fill in an already-rendered row once its lookup finishes, instead of rebuilding the open popup.</summary>
    internal void RefreshSpeed() => Raise(nameof(Speed));
}
