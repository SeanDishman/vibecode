using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// A small presentation cache for provider model menus. A running chat owns its real <see cref="ChatViewModel.Models"/>
/// catalog; this cache supplies provider-specific choices before initialization and in new-agent configuration menus.
/// </summary>
internal static class ProviderModelCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<ModelChoice>> Catalogs =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? provider) =>
        string.IsNullOrWhiteSpace(provider) ? "claude" : provider.Trim().ToLowerInvariant();

    public static bool ModelBelongsTo(string? model, string? provider) =>
        !CodexSession.IsRetiredModel(model)
        && (model is null || !model.StartsWith("vibecode:", StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string? provider) => Normalize(provider) switch
    {
        "codex" => "OpenAI Codex",
        "claude" => "Claude Code",
        "kimi" => "Kimi Code",
        "grok" => "Grok",
        GlmPreset.ProviderId => GlmPreset.DisplayName,
        var value => char.ToUpperInvariant(value[0]) + value[1..],
    };

    /// <summary>Remember the authoritative catalog returned by a live provider session.</summary>
    public static void Remember(string provider, IEnumerable<ModelChoice> models)
    {
        var key = Normalize(provider);
        var snapshot = models.Where(m => !string.IsNullOrWhiteSpace(m.Value)
                                         && !(key == "codex" && CodexSession.IsRetiredModel(m.Value))).ToList();
        if (snapshot.Count == 0) return;
        if (key == "claude") NormalizeClaudeCatalog(snapshot);
        lock (Gate) Catalogs[key] = snapshot;
    }

    /// <summary>Return the most recently observed live catalog, or an instant alias-only preview before one exists.</summary>
    public static IReadOnlyList<ModelChoice> For(string provider)
    {
        var key = Normalize(provider);
        // GLM's preview follows the selected account's service. An old Baseten session must not overwrite it.
        // Running chats still own their immutable service-specific live catalog.
        if (key == GlmPreset.ProviderId) return Fallback(key);
        lock (Gate)
        {
            if (Catalogs.TryGetValue(key, out var catalog))
            {
                if (key == "grok") return catalog;
                if (key == "codex") return catalog.Where(m => CodexModelCatalog.ShouldList(m.Value)).ToList();
                if (key != "claude") return catalog;
                // Re-merge catalogs remembered by sessions that predate the current Claude lineup.
                var merged = catalog.ToList();
                if (NormalizeClaudeCatalog(merged)) Catalogs[key] = merged;
                return Catalogs[key];
            }
        }
        return Fallback(key);
    }

    internal static bool HasLiveCatalog(string provider)
    {
        lock (Gate) return Catalogs.ContainsKey(Normalize(provider));
    }

    /// <summary>
    /// Ensure a chat pane's live <see cref="ChatViewModel.Models"/> list carries the known Claude rows
    /// from the shared Claude catalog, titles each tier alias after the model it resolves to, and puts
    /// current models first with "default" at the bottom.
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
    /// Claude Code can omit new IDs and title its rows with bare aliases or raw IDs.
    /// Name each row after its resolved model, add missing public models, then sort.
    /// </summary>
    /// <returns>True if the list was mutated.</returns>
    private static bool NormalizeClaudeCatalog(List<ModelChoice> models)
    {
        // Description counts as a change too: the model pill reads ShortName, which is derived from the
        // description, so a row whose title was already right but whose description was not still has to be saved.
        var before = models.Select(m => (m.Value, m.Display, m.Description)).ToList();

        for (int i = 0; i < models.Count; i++)
            models[i] = Retitle(models[i]);

        var available = models.SelectMany(row => new[] { row.Value, row.ResolvedModel }).ToArray();
        foreach (var model in ClaudeModelCatalog.ForAvailable(available))
        {
            if (models.Any(m => IsSameModel(m, model.Id)))
                continue;
            models.Add(ClaudeChoice(model));
        }

        var sorted = models.OrderBy(Rank).ToList();   // OrderBy is stable, so same-rank rows keep CLI order
        models.Clear();
        models.AddRange(sorted);

        return !before.SequenceEqual(models.Select(m => (m.Value, m.Display, m.Description)));
    }

    /// <summary>
    /// Model Claude Code's <c>/fast</c> would switch to when the current one has no fast mode.
    /// Current fast-capable Opus first, then older supported versions. Null when no row can do fast.
    /// </summary>
    public static ModelChoice? FastModeSwitchTarget(IEnumerable<ModelChoice>? models) =>
        models?.Where(m => m.SupportsFastMode).OrderBy(Rank).FirstOrDefault();

    /// <summary>True when a catalog row already is the given model, ignoring "[1m]"-style variant tags.</summary>
    private static bool IsSameModel(ModelChoice m, string id) =>
        !string.Equals(m.Value, "default", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(ClaudeModelCatalog.CanonicalId(m.Value), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ClaudeModelCatalog.CanonicalId(m.ResolvedModel), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names to fall back on when a bare tier alias arrives with no resolvedModel to read.</summary>
    private static readonly Dictionary<string, string> AliasNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = ClaudeModelCatalog.Find(ClaudeModelCatalog.Opus)!.Name,
        ["sonnet"] = ClaudeModelCatalog.Find(ClaudeModelCatalog.Sonnet)!.Name,
        ["haiku"] = ClaudeModelCatalog.Find(ClaudeModelCatalog.Haiku)!.Name,
        ["fable"] = ClaudeModelCatalog.Find(ClaudeModelCatalog.Fable)!.Name,
        ["mythos"] = "Mythos 5.1",
    };

    /// <summary>
    /// The context-window note Claude Code bakes into its Opus naming: it titles the row "Opus (1M context)" and
    /// describes it as "Opus 5 with 1M context · ...". Matches both shapes.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ContextNote = new(
        @"\s*(?:\((?=[^)]*context)[^)]*\)|\bwith\s+\d+\s*[MK]\s*context\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>"Opus (1M context)" -> "Opus"; "Opus 5 with 1M context" -> "Opus 5".</summary>
    private static string WithoutContextNote(string name) => ContextNote.Replace(name ?? "", "").Trim();

    /// <summary>
    /// Give a row the name the model is actually called.
    /// <para>
    /// Two things need fixing and they arrive in different fields. Claude Code titles its top row "Opus
    /// (1M context)" and opens the description with "Opus 5 with 1M context · ...", and because
    /// <see cref="ModelChoice.ShortName"/> takes the part of the description before the middle dot, that second
    /// one is what the composer's model pill reads. Every Opus has had a 1M context window for a while, so the
    /// qualifier distinguishes nothing and just makes the strongest model's name the longest thing on screen.
    /// </para>
    /// <para>
    /// Both are normalised here rather than at the two call sites, so the picker title, the picker subtitle and
    /// the pill agree. The title also still resolves a bare tier word through
    /// <see cref="ModelChoice.ResolvedModel"/>, which is how "Opus" became "Opus 4.8" before and how "Opus
    /// (1M context)" becomes "Opus 5" now: strip the note, recognise the tier, name it after what it resolves to.
    /// </para>
    /// </summary>
    private static ModelChoice Retitle(ModelChoice m)
    {
        var modelName = AliasName(m);
        var display = modelName ?? m.Display;
        var description = DescriptionWithoutContextNote(m.Description, modelName);
        return string.Equals(display, m.Display, StringComparison.Ordinal)
               && string.Equals(description, m.Description, StringComparison.Ordinal)
            ? m
            : With(m, display, description);
    }

    /// <summary>
    /// New title for a row the CLI labelled with a tier word - bare ("Opus") or qualified ("Opus (1M context)") -
    /// or null to leave it alone. Reads <see cref="ModelChoice.ResolvedModel"/> first so the label stays right if
    /// the alias moves.
    /// </summary>
    private static string? AliasName(ModelChoice m)
    {
        // A model supplied with --model may come back as a custom row whose title is the raw ID.
        if (m.Value.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Display, m.Value, StringComparison.OrdinalIgnoreCase))
            return UsagePalette.KnownName(m.ResolvedModel) ?? UsagePalette.KnownName(m.Value);
        if (!AliasNames.TryGetValue(WithoutContextNote(m.Display), out var fallback)) return null;
        var named = UsagePalette.KnownName(m.ResolvedModel) ?? fallback;
        return string.Equals(named, m.Display, StringComparison.Ordinal) ? null : named;
    }

    /// <summary>
    /// Drop the context note from the model name a description leads with, keeping the "Name · blurb" shape the
    /// rest of the catalog uses. Only the name is touched: the blurb after the dot is the CLI's to write, and the
    /// default row keeps saying which model it resolves to.
    /// </summary>
    private static string? DescriptionWithoutContextNote(string? description, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(description)) return description;
        var dot = description.IndexOf('·');
        if (dot <= 0) return description;
        var name = description[..dot].Trim();
        var trimmed = WithoutContextNote(name);
        if (modelName is not null && (trimmed.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            || AliasNames.Keys.Any(tier => trimmed.Equals(tier, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(tier + " ", StringComparison.OrdinalIgnoreCase))))
            trimmed = modelName;
        return trimmed.Length == 0 || string.Equals(trimmed, name, StringComparison.Ordinal)
            ? description
            : trimmed + " " + description[dot..];
    }

    private static ModelChoice With(ModelChoice m, string display, string? description) => new()
    {
        Provider = m.Provider,
        Value = m.Value,
        Display = display,
        Description = description,
        ResolvedModel = m.ResolvedModel,
        EffortLevels = m.EffortLevels,
        SupportsEffort = m.SupportsEffort,
        SupportsAutoMode = m.SupportsAutoMode,
        SupportsFastMode = m.SupportsFastMode,
    };

    /// <summary>Current Opus, Fable, and Sonnet first; earlier versions and default follow.</summary>
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

        if (Has("opus-5-5") || Has("opus-5.5")) return 0;
        if (Has("fable-5-1") || Has("fable-5.1")) return 10;
        if (Has("mythos-5-1") || Has("mythos-5.1")) return 15;
        if (Has("sonnet-5-5") || Has("sonnet-5.5")) return 20;
        if (Has("fable")) return 25;
        if (Has("mythos")) return 26;
        if (Has("opus-5") || Has("opus5")) return 30;
        if (Has("opus-4-8")) return 40;
        if (Has("opus-4-7")) return 41;
        if (Has("opus-4-6")) return 42;
        if (Has("opus-4-5")) return 43;
        if (Has("opus")) return 49;      // bare alias / opusplan with nothing to date it
        if (Has("sonnet")) return 50;
        if (Has("haiku")) return 60;
        return 80;
    }

    /// <summary>
    /// Build explicit native choices from the same metadata the Claude adapter injects.
    /// </summary>
    private static ModelChoice ClaudeChoice(ClaudeModelCatalog.Spec model) => new()
    {
        Provider = "claude", Value = model.Id, Display = model.Name, Description = model.Description,
        ResolvedModel = model.Id, EffortLevels = model.EffortLevels,
        SupportsEffort = model.EffortLevels.Count > 0,
        SupportsAutoMode = model.EffortLevels.Count > 0, SupportsFastMode = model.Fast,
    };

    private static IReadOnlyList<ModelChoice> ClaudeFallback()
    {
        var models = ClaudeModelCatalog.Models.Select(ClaudeChoice).ToList();
        models.Add(Choice("claude", "default", "Claude default", "Claude Code chooses the recommended model."));
        NormalizeClaudeCatalog(models);
        return models;
    }

    private static IReadOnlyList<ModelChoice> Fallback(string provider) => provider switch
    {
        "claude" => ClaudeFallback(),
        "codex" => CodexFallback(),
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
        // Model IDs and reasoning capabilities depend on the selected GLM service.
        GlmPreset.ProviderId =>
            GlmPreset.ModelsFor(ApiKeyAccountService.Instance.SelectedGlmBackend)
                .Select(m => new ModelChoice
                {
                    Provider = GlmPreset.ProviderId,
                    Value = m.Value,
                    Display = m.Display,
                    Description = m.Description,
                    ResolvedModel = m.Value,
                    SupportsEffort = GlmPreset.IsZai(ApiKeyAccountService.Instance.SelectedGlmBackend),
                    EffortLevels = GlmPreset.EffortsFor(ApiKeyAccountService.Instance.SelectedGlmBackend).ToList(),
                })
                .ToList(),
        _ =>
        [
            Choice(provider, "default", $"{DisplayName(provider)} default", $"{DisplayName(provider)} chooses the recommended model."),
        ],
    };

    private static IReadOnlyList<ModelChoice> CodexFallback()
    {
        var list = new List<ModelChoice>
        {
            Choice("codex", "default", "Codex default", "OpenAI Codex chooses the recommended model."),
        };
        // These slugs need a current Codex CLI. An older binary rejects a model it does not know.
        foreach (var model in CodexModelCatalog.Current)
            list.Add(Choice("codex", model.Id, model.DisplayName, model.Description));
        list.Add(Choice("codex", CodexSession.SparkModelId, "GPT-5.3 Codex Spark", CodexSession.SparkDescription));
        return list;
    }

    private static ModelChoice Choice(string provider, string value, string display, string description,
        bool fastMode = false) => new()
    {
        Provider = provider,
        Value = value,
        Display = display,
        Description = description,
        SupportsFastMode = fastMode || (provider == "codex" && CodexSession.KnownFastModel(value)),
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
