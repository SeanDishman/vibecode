using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private string? _claudeModelRestartId;
    private const string ClaudeModelRestartHeader = "[VIBECODE CLAUDE MODEL RECOVERY]";

    private void ApplyClaudeModelRecovery(JsonNode message)
    {
        if (_session is not ClaudeSession session || Status == "closed"
            || NodeString(message["recovery_id"]) is not { } id) return;
        var phase = NodeString(message["phase"]);
        var current = message["selection_version"]?.GetValue<long>() is { } version && session.IsCurrentModelSelection(version);
        if (phase is "waiting" or "fallback" or "selected")
        {
            if (!current || _interruptRequested) return;
            // Cooldown and exhausted retries leave the current turn, steering, and queued prompts running normally.
            if (phase is "fallback" or "selected")
            {
                Model = NodeString(message["model"]);
                RebuildEffortOptions();
            }
            InsertBeforeQueued(new BannerItem { Level = "info", Text = NodeString(message["message"])! });
        }
        else if (phase == "stopping")
        {
            if (!current || _interruptRequested) return;
            _claudeModelRestartId = id;
            CancelGoalCheck();
            InsertBeforeQueued(new BannerItem { Level = "info", Text = NodeString(message["message"])! });
        }
        else
        {
            if (_claudeModelRestartId != id) return;
            _claudeModelRestartId = null;
            if (phase == "ready" && current && !_interruptRequested)
            {
                Model = NodeString(message["model"]);
                RebuildEffortOptions();
                InsertBeforeQueued(new BannerItem { Level = "info", Text = NodeString(message["message"])! });
                SendAutomaticContinuation(ClaudeModelRestartHeader +
                    "\nThe previous turn was stopped after Claude switched to Opus 4.8. Opus 5.5 is now selected. " +
                    "Continue the user's current task from the conversation and existing workspace state. " +
                    "Keep completed work, check any interrupted operation before retrying it, and do not repeat completed actions.");
            }
            else if (phase == "failed" && current)
            {
                // Keep queued user requests from silently starting on the fallback after a failed switch.
                if (!session.CanSteer) Status = "error";
                PauseGoal();
                InsertBeforeQueued(new BannerItem { Level = "warn", Text = NodeString(message["message"])! });
            }
            else if (!session.CanSteer && Status == "running") Status = "idle";
        }
        Raise(nameof(CanSendQueuedNow));
        Raise(nameof(CanSteer));
        ItemsChanged?.Invoke();
    }
}
