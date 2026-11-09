using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A saved login a chat can run under, as the wall's right-click menu lists it.</summary>
public sealed record ChatAccountOption(string Id, string Label, bool IsCurrent);

/// <summary>
/// Which saved login an agent runs under, picked per chat from its right-click menu on the wall. A chat starts on the
/// login it was created with; a pick moves the SAME conversation onto another login of its provider, where it stays -
/// across restarts too - until the next pick. Nothing is announced: the pick is the user's own, not a rescue.
/// </summary>
public sealed partial class MainViewModel
{
    // Picks waiting for their chat's current turn to end. A move respawns the provider, which would cut a turn off.
    private readonly Dictionary<ChatViewModel, string> _pendingChatAccounts = new();

    /// <summary>The logins <paramref name="chat"/> can move between, the one it runs under (or is about to) marked
    /// current. Empty when there is nothing to choose: one login, a provider without logins of its own (Gemini, GLM),
    /// or a Claude or Codex chat on an API key, which has no login home for a move to carry its transcript out of.</summary>
    public IReadOnlyList<ChatAccountOption> AccountOptionsFor(ChatViewModel chat)
    {
        if (chat.Status == "closed") return [];
        var provider = ProviderModelCatalog.Normalize(chat.Provider);
        var logins = SavedLogins(provider);
        if (logins.Count < 2) return [];

        if (ApiKeyAccountService.Instance.For(provider).Any(key => SameAccount(key.Id, chat.AccountId)))
            return [];
        var current = _pendingChatAccounts.GetValueOrDefault(chat) ?? chat.AccountId;
        // A login that has to sign in again can't take a chat; the one the chat already runs under is still listed.
        var choices = logins.Where(l => l.Usable || SameAccount(l.Id, current)).ToArray();
        if (choices.Length < 2) return [];
        // Two logins with one name (two "Personal" workspaces) are told apart the way the Bridge picker does it.
        var twice = choices.GroupBy(l => l.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return choices.Select(l => new ChatAccountOption(l.Id,
            twice.Contains(l.Label) ? $"{l.Label} · {l.Id[^Math.Min(6, l.Id.Length)..]}" : l.Label,
            SameAccount(l.Id, current))).ToArray();
    }

    /// <summary>Run <paramref name="chat"/> under <paramref name="accountId"/> from now on. Between turns the move
    /// happens at once; mid-turn it waits for the turn - and anything queued behind it - to finish. Picking the login
    /// the chat already runs under cancels a move still waiting.</summary>
    public void ChooseChatAccount(ChatViewModel chat, string accountId)
    {
        if (chat.Status == "closed") return;
        if (SameAccount(chat.AccountId, accountId)) { ForgetChatAccountPick(chat); return; }
        if (!_pendingChatAccounts.ContainsKey(chat)) chat.PropertyChanged += OnPickedChatChanged;
        _pendingChatAccounts[chat] = accountId;
        ApplyChatAccountPick(chat);
    }

    private void OnPickedChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChatViewModel chat
            || e.PropertyName is not (nameof(ChatViewModel.Status) or nameof(ChatViewModel.HasWorkingSubagents))) return;
        // Background priority, so the chat's own reaction to going idle - sending a queued prompt - runs first. The
        // move then sees the new turn and keeps waiting instead of cutting it off.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyChatAccountPick(chat));
    }

    private void ApplyChatAccountPick(ChatViewModel chat)
    {
        if (!_pendingChatAccounts.TryGetValue(chat, out var accountId)) return;
        if (chat.Status == "closed") { ForgetChatAccountPick(chat); return; }
        if (chat.IsWorking || chat.HasWorkingSubagents || chat.HasPendingDispatch) return;   // asked again when that changes
        ForgetChatAccountPick(chat);
        var provider = ProviderModelCatalog.Normalize(chat.Provider);
        if (chat.SessionId is null)
        {
            // Nothing said yet, so nothing to carry: the chat simply starts over on the other login.
            RespawnOnAccount(chat, null, accountId, provider, "", quiet: true);
            return;
        }
        var moved = provider switch
        {
            "claude" => MoveClaudeChatToAccount(chat, accountId, quiet: true),
            "codex" => MoveCodexChatToAccount(chat, accountId, quiet: true),
            _ => false,
        };
        if (!moved)
            chat.Items.Add(new BannerItem
            {
                Level = "error",
                Text = $"Couldn't switch this chat's account: its conversation wasn't found under the {chat.ProviderDisplay} " +
                       "login it runs on, so it stays there.",
            });
    }

    private void ForgetChatAccountPick(ChatViewModel chat)
    {
        if (_pendingChatAccounts.Remove(chat)) chat.PropertyChanged -= OnPickedChatChanged;
    }

    /// <summary>A provider's saved logins, in its account manager's order. Gemini's one Google login and GLM's API
    /// keys are not logins to move a chat between.</summary>
    private static IReadOnlyList<(string Id, string Label, bool Usable)> SavedLogins(string provider) => provider switch
    {
        "claude" => AccountService.Instance.List().Select(a => (a.Id, a.Label, a.Usable)).ToArray(),
        "codex" => CodexAccountService.Instance.List().Select(a => (a.Id, a.Label, a.Usable)).ToArray(),
        _ => [],
    };

    private static bool SameAccount(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
