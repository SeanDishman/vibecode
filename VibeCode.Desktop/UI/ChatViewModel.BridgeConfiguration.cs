using VibeCode.Protocol;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private bool _bridgeSessionInitialized;
    private bool _applyingBridgeConfiguration;
    private BridgeAgentConfiguration? _bridgeLaunchConfiguration;
    private TaskCompletionSource? _bridgeInitializationConfigurationApplied;
    private Task _bridgeRuntimeConfigurationTask = Task.CompletedTask;
    private Task _bridgeConfigurationApplication = Task.CompletedTask;

    private BridgeAgentConfiguration? _bridgeWorkerConfiguration;
    public BridgeAgentConfiguration? BridgeWorkerConfiguration
    {
        get => _bridgeWorkerConfiguration;
        internal set => Set(ref _bridgeWorkerConfiguration, value);
    }

    public string BridgeConfigurationDisplay => $"{ModelDisplay} · {(Effort is null ? "effort default" : EffortDisplay)}";

    internal void ApplyBridgeConfiguration(BridgeAgentConfiguration configuration)
    {
        if (!string.Equals(configuration.Provider, Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An existing agent cannot change provider. Start a new agent with the selected provider.");
        configuration = BridgeAgentConfigurationPolicy.Normalize(configuration);
        _applyingBridgeConfiguration = true;
        try
        {
            Model = configuration.Model;
            Effort = configuration.Effort;
            BridgeReviewLevel = configuration.ReviewLevel;
            if (configuration.FastMode is { } fastMode) FastMode = fastMode;
        }
        finally { _applyingBridgeConfiguration = false; }
        // Update only this session. SetModel/SetEffort also write global new-chat
        // defaults, which would make a team's worker choice alter the user's host defaults.
        RebuildEffortOptions();
        if (!_bridgeSessionInitialized)
        {
            // A fresh setup can configure a provider that has already started on its
            // defaults. Its system/init arrives before capabilities and still describes
            // those defaults. Keep this choice until the catalog is ready, and hold the
            // objective behind the runtime update that follows initialization.
            _bridgeLaunchConfiguration = configuration;
            _bridgeInitializationConfigurationApplied ??=
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridgeConfigurationApplication = _bridgeInitializationConfigurationApplied.Task;
            return;
        }

        if (_session is { } session)
        {
            _bridgeRuntimeConfigurationTask = ApplyBridgeSessionConfigurationAsync(
                _bridgeRuntimeConfigurationTask, session, configuration);
            _bridgeConfigurationApplication = _bridgeRuntimeConfigurationTask;
        }
        Raise(nameof(BridgeConfigurationDisplay));
    }

    private async Task ApplyBridgeSessionConfigurationAsync(Task previous, ICodingSession session,
        BridgeAgentConfiguration configuration)
    {
        await previous;
        if (!ReferenceEquals(_session, session) || session.HasExited) return;
        var ultracode = IsClaude && ClaudeSession.IsUltracodeLevel(configuration.Effort);
        var wireEffort = ultracode ? "xhigh" : configuration.Effort;
        await session.SetModelAsync(configuration.Model == "default" ? null : configuration.Model, wireEffort);
        if (!ReferenceEquals(_session, session) || session.HasExited) return;
        if (session is ClaudeSession claude)
        {
            await claude.SetFlagSettingsAsync(wireEffort, ultracode);
            await claude.SetFastModeAsync(configuration.FastMode ?? EffectiveFastMode);
            _appliedFastMode = configuration.FastMode ?? EffectiveFastMode;
        }
        else if (session is CodexSession codex)
            await codex.SetFastModeAsync(configuration.FastMode ?? FastMode);
        _appliedEffort = configuration.Effort;
    }

    private void CompleteBridgeConfigurationInitialization()
    {
        _bridgeSessionInitialized = true;
        if (_bridgeLaunchConfiguration is not { } configuration) return;
        _bridgeLaunchConfiguration = null;
        var waiting = _bridgeInitializationConfigurationApplied;
        _bridgeInitializationConfigurationApplied = null;
        // Normalize again using the now-live capabilities before applying the role.
        ApplyBridgeConfiguration(configuration);
        _ = ReleaseBridgeConfigurationInitializationAsync(waiting, _bridgeConfigurationApplication);
    }

    private static async Task ReleaseBridgeConfigurationInitializationAsync(TaskCompletionSource? waiting, Task application)
    {
        try { await application; waiting?.TrySetResult(); }
        catch (Exception ex) { waiting?.TrySetException(ex); }
    }
}
