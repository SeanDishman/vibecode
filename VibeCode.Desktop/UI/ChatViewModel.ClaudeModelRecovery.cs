using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    // Recognize recovery prompts in older saved transcripts.
    private const string ClaudeModelRestartHeader = "[VIBECODE CLAUDE MODEL RECOVERY]";
    // Model shows the refusal fallback Claude actually runs. The user's own choice is kept beside it, so copying,
    // saving or restarting this agent never turns a temporary fallback into its model.
    private bool _claudeFallbackActive;
    private string? _claudeFallbackChosenModel;
    private string? _claudeFallbackModel;

    /// <summary>The model the user chose. The same as <see cref="Model"/> except while Claude runs a refusal fallback:
    /// an agent team's newcomer, a saved roster and a restarted process use this, never the fallback.</summary>
    public string? ChosenModel => _claudeFallbackActive ? _claudeFallbackChosenModel : _model;

    private void ShowClaudeFallback(string? fallbackModel)
    {
        if (!_claudeFallbackActive)
        {
            _claudeFallbackChosenModel = _model;
            _claudeFallbackActive = true;
        }
        _claudeFallbackModel = fallbackModel;
        Model = fallbackModel;
        RebuildEffortOptions();
    }

    private void LeaveClaudeFallback()
    {
        _claudeFallbackActive = false;
        _claudeFallbackChosenModel = _claudeFallbackModel = null;
    }

    /// <summary>The CLI reported the model it runs. Any model but the fallback means the fallback is over.</summary>
    private void ObserveClaudeRunningModel(string? model)
    {
        if (_claudeFallbackActive && model is { Length: > 0 }
            && ClaudeModelCatalog.CanonicalId(model) != ClaudeModelCatalog.CanonicalId(_claudeFallbackModel))
            LeaveClaudeFallback();
    }

    /// <summary>A new process starts on the model VibeCode passes it, so a restart leaves any fallback behind.</summary>
    private void RestoreChosenModelForLaunch()
    {
        if (!_claudeFallbackActive) return;
        var chosen = _claudeFallbackChosenModel;
        LeaveClaudeFallback();
        Model = chosen;
    }

}
