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
        // Description counts as a change too: the model pill reads ShortName, which is derived from the
        // description, so a row whose title was already right but whose description was not still has to be saved.
        var before = models.Select(m => (m.Value, m.Display, m.Description)).ToList();

        for (int i = 0; i < models.Count; i++)
            models[i] = Retitle(models[i]);

        foreach (var (value, display, description, fast) in ClaudeExtras)
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
                SupportsFastMode = fast,
            });
        }

        var sorted = models.OrderBy(Rank).ToList();   // OrderBy is stable, so same-rank rows keep CLI order
        models.Clear();
        models.AddRange(sorted);

        return !before.SequenceEqual(models.Select(m => (m.Value, m.Display, m.Description)));
    }

    /// <summary>
    /// Model Claude Code's <c>/fast</c> would switch to when the current one has no fast mode.
    /// Strongest fast-capable row (Opus 5, then 4.8). Null when nothing in the list can do fast.
    /// </summary>
    public static ModelChoice? FastModeSwitchTarget(IEnumerable<ModelChoice>? models) =>
        models?.Where(m => m.SupportsFastMode).OrderBy(Rank).FirstOrDefault();

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
        // The CLI's own catalog moved latest_per_family.fable to claude-fable-5-1 in 2.1.257, so a bare "fable"
        // with nothing to resolve is a 5.1 now. A CLI that does report resolvedModel never reaches this line.
        ["fable"] = "Fable 5.1",
        ["mythos"] = "Mythos 5",
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
        var display = AliasName(m) ?? m.Display;
        var description = DescriptionWithoutContextNote(m.Description);
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
        if (!AliasNames.TryGetValue(WithoutContextNote(m.Display), out var fallback)) return null;
        var named = UsagePalette.KnownName(m.ResolvedModel) ?? fallback;
        return string.Equals(named, m.Display, StringComparison.Ordinal) ? null : named;
    }

    /// <summary>
    /// Drop the context note from the model name a description leads with, keeping the "Name · blurb" shape the
    /// rest of the catalog uses. Only the name is touched: the blurb after the dot is the CLI's to write, and the
    /// default row keeps saying which model it resolves to.
    /// </summary>
    private static string? DescriptionWithoutContextNote(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return description;
        var dot = description.IndexOf('·');
        if (dot <= 0) return description;
        var name = description[..dot].Trim();
        var trimmed = WithoutContextNote(name);
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
    /// <para><c>Fast</c> is per-model on purpose: fast mode is an Opus 4.8-and-newer tier. Marking 4.6
    /// fast-capable made the toggle look like it worked while the API ran standard speed. The popup now
    /// switches to Opus 5 (see <see cref="FastModeSwitchTarget"/>) instead of greying out with "Opus only".</para>
    /// </summary>
    private static readonly (string Value, string Display, string Description, bool Fast)[] ClaudeExtras =
    [
        ("claude-opus-5", "Opus 5",
            "Near-Fable intelligence for complex agentic coding and enterprise work, at half Fable price.", true),
        ("claude-opus-4-8", "Opus 4.8",
            "Previous flagship Opus. Highly autonomous on long-horizon agentic and knowledge work.", true),
        ("claude-opus-4-6", "Opus 4.6",
            "Older Opus. Still accepts temperature and a fixed thinking budget that 4.7+ dropped.", false),
    ];

    private static IReadOnlyList<ModelChoice> Fallback(string provider) => provider switch
    {
        "claude" =>
        [
            // fastMode: true keeps the Fast mode row reachable before a live CLI catalog exists. Without it the
            // preview/offline picker reported Opus 5 as not fast-capable, so the toggle never appeared at all.
            Choice("claude", "claude-opus-5", "Opus 5",
                "Near-Fable intelligence for complex agentic coding and enterprise work, at half Fable price.",
                fastMode: true),
            Choice("claude", "fable", "Fable 5.1", "Anthropic's most capable model, for the most demanding work."),
            Choice("claude", "claude-opus-4-8", "Opus 4.8",
                "Previous flagship Opus. Highly autonomous on long-horizon agentic and knowledge work.",
                fastMode: true),
            Choice("claude", "claude-opus-4-6", "Opus 4.6",
                "Older Opus. Still accepts temperature and a fixed thinking budget that 4.7+ dropped."),
            Choice("claude", "sonnet", "Sonnet 5", "Balanced speed and capability."),
            Choice("claude", "haiku", "Haiku 4.5", "Fastest Claude model."),
            Choice("claude", "default", "Claude default", "Claude Code chooses the recommended model."),
        ],
        "codex" =>
        [
            Choice("codex", "default", "Codex default", "OpenAI Codex chooses the recommended model."),
            // Astra leads the list because it is priority 1 in the CLI's own catalog. It needs codex >= 0.153.0;
            // an older binary rejects the slug, which is why the account menu surfaces the runtime version.
            Choice("codex", "gpt-6-astra", "GPT-6 Astra",
                "Our most capable model for complex, demanding work."),
            Choice("codex", CodexModelPreset.SolModelId, CodexModelPreset.SolDisplayName,
                CodexModelPreset.SolDescription),
            Choice("codex", "gpt-5.6-terra", "GPT 5.6 Terra",
                "Balanced GPT-5.6 agentic coding model."),
            Choice("codex", "gpt-5.6-luna", "GPT 5.6 Luna",
                "Fast GPT-5.6 agentic coding model."),
            Choice("codex", CodexSession.SparkModelId, "GPT-5.3 Codex Spark", CodexSession.SparkDescription),
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
        // No "default" row: this provider has a fixed catalog rather than a CLI that picks for you, and every id
        // here is one Baseten actually serves.
        GlmPreset.ProviderId =>
            GlmPreset.Models
                .Select(m => new ModelChoice
                {
                    Provider = GlmPreset.ProviderId,
                    Value = m.Value,
                    Display = m.Display,
                    Description = m.Description,
                    ResolvedModel = m.Value,
                    SupportsEffort = GlmPreset.SupportsEffort,
                    EffortLevels = new List<string>(),
                })
                .ToList(),
        _ =>
        [
            Choice(provider, "default", $"{DisplayName(provider)} default", $"{DisplayName(provider)} chooses the recommended model."),
        ],
    };

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
