using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A role's launch settings. Null effort means the provider's model default.</summary>
public sealed record BridgeAgentConfiguration(string Provider, string? Model, string? Effort,
    string ReviewLevel = BridgeReviewPolicy.Normal, bool? FastMode = null);

internal static class BridgeAgentConfigurationPolicy
{
    public static readonly string[] Providers = ["claude", "codex", "kimi", "grok", GlmPreset.ProviderId];

    public static BridgeAgentConfiguration From(ChatViewModel chat) => new(chat.Provider, chat.Model, chat.Effort, chat.BridgeReviewLevel, chat.FastMode);

    public static BridgeAgentConfiguration Defaults(string provider, ChatViewModel? host = null)
    {
        provider = ProviderModelCatalog.Normalize(provider);
        if (host?.Provider == provider) return Normalize(From(host));
        var settings = AppSettings.Current;
        var (model, effort) = provider switch
        {
            "codex" => (settings.DefaultCodexModel, settings.DefaultCodexEffort),
            "kimi" => (settings.DefaultKimiModel, settings.DefaultKimiEffort),
            "grok" => (settings.DefaultGrokModel, settings.DefaultGrokEffort),
            GlmPreset.ProviderId => (settings.DefaultGlmModel, settings.DefaultGlmEffort),
            _ => (settings.DefaultModel, settings.DefaultEffort),
        };
        return Normalize(new(provider, model, effort, FastMode: settings.FastMode));
    }

    public static IReadOnlyList<ModelChoice> ModelsFor(string provider)
    {
        provider = ProviderModelCatalog.Normalize(provider);
        var models = ProviderModelCatalog.For(provider);
        if (ProviderModelCatalog.HasLiveCatalog(provider)) return models;
        // The protocol's own offline metadata supplies known effort tiers. Unknown models
        // keep their capability flags and explicitly show that effort is unavailable.
        return models.Select(model =>
        {
            var levels = model.EffortLevels;
            if (provider == "codex" && CodexModelCatalog.Find(model.Value) is { } spec)
                levels = spec.EffortLevels;
            else if (provider == "codex" && model.Value == CodexSession.SparkModelId)
                levels = ["low", "medium", "high", "xhigh"];
            if (levels.Count == 0) return model;
            return new ModelChoice
            {
                Provider = model.Provider, Value = model.Value, Display = model.Display,
                Description = model.Description, ResolvedModel = model.ResolvedModel,
                SupportsEffort = true, EffortLevels = levels, SupportsAutoMode = true,
                SupportsFastMode = model.SupportsFastMode,
            };
        }).ToArray();
    }

    public static List<EffortChoice> EffortsFor(string provider, ModelChoice? model, string? current = null)
    {
        var choices = ChatViewModel.EffortChoicesFor(model, provider == "claude", current,
            ProviderModelCatalog.DisplayName(provider));
        // A null wire value is valid for every provider and means its default, even
        // when the live catalog doesn't advertise an automatic reasoning mode.
        if (choices.Count > 0 && choices.All(choice => choice.Value is not null))
            choices.Insert(0, new EffortChoice { Value = null, Label = "Model default", IsSelected = current is null });
        return choices;
    }

    public static bool SupportsFastMode(string provider, ModelChoice? model) =>
        provider is "claude" or "codex" && model?.SupportsFastMode == true;

    public static BridgeAgentConfiguration Normalize(BridgeAgentConfiguration configuration)
    {
        var provider = ProviderModelCatalog.Normalize(configuration.Provider);
        if (!Providers.Contains(provider)) throw new ArgumentException("Choose a supported agent provider.");
        var models = ModelsFor(provider);
        var model = models.FirstOrDefault(row => string.Equals(row.Value, configuration.Model, StringComparison.OrdinalIgnoreCase)
            || configuration.Model is not null && string.Equals(row.ResolvedModel, configuration.Model, StringComparison.OrdinalIgnoreCase));
        // Offline previews use explicit current IDs; saved family aliases still select the same family.
        if (model is null && provider == "claude" && ClaudeModelCatalog.ForAlias(configuration.Model) is { } alias)
            model = models.FirstOrDefault(row => string.Equals(row.Value, alias.Id, StringComparison.OrdinalIgnoreCase));
        model ??= models.FirstOrDefault(row => row.Value == "default") ?? models.FirstOrDefault();
        var efforts = EffortsFor(provider, model, configuration.Effort);
        var effort = efforts.FirstOrDefault(row => string.Equals(row.Value, configuration.Effort, StringComparison.OrdinalIgnoreCase))?.Value;
        // Missing speed in older saved bridges leaves the chat's restored preference intact.
        bool? fastMode = configuration.FastMode is { } fast ? fast && SupportsFastMode(provider, model) : null;
        return new(provider, model?.Value, effort, BridgeReviewPolicy.Normalize(configuration.ReviewLevel), fastMode);
    }
}
