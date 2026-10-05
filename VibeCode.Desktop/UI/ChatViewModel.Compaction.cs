using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.UI;

public sealed partial class ChatViewModel
{
    private string? _manualCompactCommand;
    private BannerItem? _manualCompactBanner;
    private bool _manualCompactSucceeded;
    private bool _handlingManualCompactionResult;
    public bool IsCompactingContext => _manualCompactCommand is not null || _handlingManualCompactionResult;

    private static bool IsCompactCommand(string text) => text.Trim() is var command
        && command.StartsWith("/compact", StringComparison.OrdinalIgnoreCase)
        && (command.Length == 8 || char.IsWhiteSpace(command[8]));

    private bool StartManualCompaction(string command, IReadOnlyList<Attachment>? attachments, bool recovering = false)
    {
        string? problem = null;
        var instructions = command.Trim()[8..].Trim();
        if (_session is not ICompactableSession) problem = $"/compact is not supported by {ProviderDisplay}.";
        else if (IsWorking || (!recovering && HasPendingDispatch) || RewindHoldsDispatch || _pendingPerms.Count > 0)
            problem = "Wait for the current turn and queued messages to finish, then use /compact.";
        else if (attachments is { Count: > 0 }) problem = "Remove attachments before using /compact.";
        else if (IsCodex && instructions.Length > 0) problem = "Use /compact on its own for Codex. Claude also accepts focus instructions.";
        if (problem is not null)
        {
            Items.Add(new BannerItem { Level = "info", Text = problem });
            ItemsChanged?.Invoke();
            return false;
        }

        CancelLimitRecovery();
        CancelGoalCheck();
        BeginTurnPricing();
        _manualCompactCommand = instructions.Length == 0 ? "/compact" : "/compact " + instructions;
        _manualCompactSucceeded = false;
        _promptHistory.Record(_manualCompactCommand);
        Items.Add(new UserItem { Text = _manualCompactCommand, Owner = this });
        _interruptRequested = false;
        AuthNeeded = false;
        Status = "running";
        _manualCompactBanner = new BannerItem { Level = "info", Text = "Compacting conversation context…" };
        Items.Add(_manualCompactBanner);
        MessageSent?.Invoke();
        ItemsChanged?.Invoke();
        _ = CompactSessionAsync(_session!, instructions);
        return true;
    }

    private async Task CompactSessionAsync(ICodingSession session, string instructions)
    {
        try { await ((ICompactableSession)session).CompactAsync(instructions); }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_session, session) || Status == "closed" || _manualCompactCommand is null) return;
            ApplyResult(new JsonObject
            {
                ["type"] = "result", ["is_error"] = true, ["subtype"] = "compaction_failed",
                ["result"] = ex.Message,
            });
            ItemsChanged?.Invoke();
        }
    }

    private string? FinishManualCompaction(JsonNode result, bool failed, bool interrupted)
    {
        var command = _manualCompactCommand;
        _manualCompactCommand = null;
        if (command is null) return null;
        _handlingManualCompactionResult = true;
        if (_manualCompactBanner is not null) Items.Remove(_manualCompactBanner);
        _manualCompactBanner = null;
        if (!failed && !_manualCompactSucceeded)
            Items.Add(new BannerItem { Level = "info", Text = interrupted ? "Compaction stopped."
                : result["result"]?.ToString() is { Length: > 0 } detail ? detail : "Compaction finished." });
        return command;
    }
}
