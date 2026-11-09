using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

namespace VibeCode.Terminal;

public sealed partial class TerminalSession
{
    // Choices apply when the next prompt claims an agent, leaving ongoing work unchanged.
    private BridgeAgentConfiguration? _launchConfiguration;

    private IReadOnlyList<ModelChoice> ControlModels() =>
        (Host.Models.Count > 0 ? Host.Models.ToArray() : BridgeAgentConfigurationPolicy.ModelsFor(Host.Provider))
        .Where(model => !Host.IsCodex || !CodexSession.IsRetiredModel(model.Value))
        .DistinctBy(model => model.Value, StringComparer.OrdinalIgnoreCase).Take(200).ToArray();

    private BridgeAgentConfiguration NextConfiguration() => _launchConfiguration ?? BridgeAgentConfigurationPolicy.From(Host);

    private ModelChoice? ControlModel(BridgeAgentConfiguration configuration, IReadOnlyList<ModelChoice> models) =>
        models.FirstOrDefault(model => string.Equals(model.Value, configuration.Model, StringComparison.OrdinalIgnoreCase)
            || configuration.Model is not null && string.Equals(model.ResolvedModel, configuration.Model, StringComparison.OrdinalIgnoreCase))
        ?? (configuration.Model is null or "" or "default" ? models.FirstOrDefault(model => model.Value == "default") ?? models.FirstOrDefault() : null);

    private TerminalControls ControlSnapshot()
    {
        var configuration = NextConfiguration();
        var models = ControlModels();
        var model = ControlModel(configuration, models);
        var efforts = BridgeAgentConfigurationPolicy.EffortsFor(Host.Provider, model, configuration.Effort);
        return new(model?.Value ?? configuration.Model, model?.ShortName ?? configuration.Model ?? "Default",
            models.Select(row => new TerminalOption(row.Value, row.ShortName)).ToArray(), configuration.Effort,
            efforts.FirstOrDefault(row => string.Equals(row.Value, configuration.Effort, StringComparison.OrdinalIgnoreCase))?.Label
                ?? (efforts.Count == 0 ? "Unavailable" : "Model default"),
            efforts.Select(row => new TerminalOption(row.Value, row.Label)).ToArray(),
            configuration.FastMode == true && BridgeAgentConfigurationPolicy.SupportsFastMode(Host.Provider, model),
            BridgeAgentConfigurationPolicy.SupportsFastMode(Host.Provider, model), configuration.UltrafastMode);
    }

    public TerminalPacket HandleSetting(TerminalSetting? setting)
    {
        var configuration = NextConfiguration();
        var models = ControlModels();
        var model = ControlModel(configuration, models);
        string? error = null;
        switch (setting?.Name)
        {
            case "model":
                model = models.FirstOrDefault(row => string.Equals(row.Value, setting.Value, StringComparison.Ordinal));
                if (model is null) { error = "That model is no longer available. Choose a model from this terminal's list."; break; }
                var efforts = BridgeAgentConfigurationPolicy.EffortsFor(Host.Provider, model);
                configuration = configuration with
                {
                    Model = model.Value,
                    Effort = efforts.Any(row => row.Value == configuration.Effort) ? configuration.Effort : null,
                    FastMode = configuration.FastMode == true && BridgeAgentConfigurationPolicy.SupportsFastMode(Host.Provider, model),
                    UltrafastMode = configuration.UltrafastMode && BridgeAgentConfigurationPolicy.SupportsUltrafastMode(Host.Provider, model),
                };
                break;
            case "effort":
                if (!BridgeAgentConfigurationPolicy.EffortsFor(Host.Provider, model).Any(row => row.Value == setting.Value))
                    error = "That effort level is unavailable for this model.";
                else configuration = configuration with { Effort = setting.Value };
                break;
            case "fast":
                if (setting.Value is not ("on" or "off")) error = "Choose On or Off for fast mode.";
                else if (setting.Value == "on" && !BridgeAgentConfigurationPolicy.SupportsFastMode(Host.Provider, model))
                    error = "Fast mode is unavailable for this model.";
                else configuration = configuration with { FastMode = setting.Value == "on", UltrafastMode = false };
                break;
            default: error = "Unknown terminal setting."; break;
        }
        if (error is null) { _launchConfiguration = configuration; MarkDirty(); }
        return new("settings", error ?? "Updated for the next prompt.", error is null, Controls: ControlSnapshot());
    }
}
