using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private sealed record PeerWakePreference(object Scope, int SenderEpoch, int RecipientEpoch, string Reason);
    private sealed record PeerWakeRequest(bool Wake, string Mode, string? Authorization, string? Reason);
    private readonly ConditionalWeakTable<ChatViewModel, ConditionalWeakTable<ChatViewModel, PeerWakePreference>> _peerWakePreferences = new();

    private PeerWakePreference? WakePreference(ChatViewModel sender, ChatViewModel recipient, object scope)
    {
        if (!_peerWakePreferences.TryGetValue(sender, out var recipients) || !recipients.TryGetValue(recipient, out var saved)) return null;
        if (ReferenceEquals(scope, saved.Scope) && sender.PeerMailboxEpoch == saved.SenderEpoch && recipient.PeerMailboxEpoch == saved.RecipientEpoch)
            return saved;
        recipients.Remove(recipient);
        return null;
    }

    private PeerWakeRequest ResolvePeerWake(ChatViewModel sender, ChatViewModel recipient, object scope, JsonObject args)
    {
        var mode = args["wake_mode"]?.GetValue<string>() ?? "once";
        var explicitWake = args["wake_up_ai"]?.GetValue<bool>();
        var saved = WakePreference(sender, recipient, scope);
        if (mode == "off")
        {
            if (explicitWake == true) throw new StatusValidationException("wake_mode=off conflicts with wake_up_ai=true.");
            return new(false, mode, null, null);
        }
        if (args.ContainsKey("wake_mode") && explicitWake == false)
            throw new StatusValidationException("A wake mode of once or always conflicts with wake_up_ai=false. Omit wake_mode to suppress only this message.");
        var wake = explicitWake ?? (mode == "always" || args.ContainsKey("wake_mode") || saved is not null);
        var authorization = args["wake_authorization"]?.GetValue<string>();
        var reason = args["wake_reason"]?.GetValue<string>()?.Trim();
        if (!wake)
        {
            if (authorization is not null || reason is not null)
                throw new StatusValidationException("Wake authorization/reason requires wake_up_ai=true or a wake_mode that wakes the peer.");
            return new(false, mode, null, null);
        }
        // A stored grant applies only to this sender/recipient in this live mailbox scope. It is never inherited by replies.
        if (mode != "always" && saved is not null && authorization is null && reason is null)
            return new(true, mode, "user_request", saved.Reason);
        if (authorization is not ("user_request" or "emergency") || string.IsNullOrWhiteSpace(reason))
            throw new StatusValidationException("Waking an AI requires wake_authorization=user_request or emergency and wake_reason stating the user's instruction or concrete imminent serious harm. Routine coordination is insufficient.");
        if (mode == "always" && authorization != "user_request")
            throw new StatusValidationException("wake_mode=always requires an explicit user instruction covering future messages. An emergency authorizes only this message.");
        return new(true, mode, authorization, reason);
    }

    private void CommitPeerWake(ChatViewModel sender, ChatViewModel recipient, object scope, PeerWakeRequest request)
    {
        if (request.Mode is not ("always" or "off")) return;
        var recipients = _peerWakePreferences.GetOrCreateValue(sender);
        recipients.Remove(recipient);
        if (request.Mode == "always") recipients.Add(recipient,
            new(scope, sender.PeerMailboxEpoch, recipient.PeerMailboxEpoch, request.Reason!));
    }

    private JsonObject PeerWakeInfo(ChatViewModel sender, ChatViewModel recipient, object scope, string? restriction = null) => new()
    {
        ["can_wake"] = restriction is null && !ReferenceEquals(sender, recipient) && sender.PeerWakeScopeAllowed && recipient.PeerWakeUnavailableReason is null,
        ["blocked_reason"] = restriction ?? (ReferenceEquals(sender, recipient) ? "This is you."
            : !sender.PeerWakeScopeAllowed ? "Advanced Bridge agents use dispatch and existing orchestration rules."
            : recipient.PeerWakeUnavailableReason),
        ["mode"] = WakePreference(sender, recipient, scope) is null ? "manual" : "always",
    };

    private void ValidatePeerWake(ChatViewModel sender, ChatViewModel recipient, PeerWakeRequest request)
    {
        if (!request.Wake) return;
        if (!sender.PeerWakeScopeAllowed)
            throw new StatusValidationException("Explicit peer wakeups are for ordinary chats and regular Bridges. Advanced Bridge agents use dispatch and existing orchestration rules.");
        if (recipient.PeerWakeUnavailableReason is { } reason) throw new StatusValidationException(reason);
    }

    private JsonObject PeerWakeReceipt(ChatViewModel sender, ChatViewModel recipient, object scope, PeerWakeRequest request) => new()
    {
        ["requested"] = request.Wake,
        ["authorization"] = request.Authorization,
        ["reason"] = request.Reason,
        ["future_messages"] = WakePreference(sender, recipient, scope) is null ? "manual" : "always",
        ["status"] = request.Wake ? "queued_for_idle_or_reconnect" : "not_requested",
    };
}
