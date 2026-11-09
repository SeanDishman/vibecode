using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private const string BridgeTerminalUnavailable = "Bridge and Advanced Bridge chats cannot open in Terminal.";

    public string? TerminalUnavailableReason(ChatViewModel chat)
    {
        if (!AppSettings.Current.TerminalEnabled) return "Enable Vibe Code Terminal in Settings > Developer > CLI first.";
        if (chat.Status == "closed" || !Chats.Contains(chat)) return "This chat is no longer open.";
        if (chat.IsAdvancedBridgeHost || chat.IsBridgeManager || chat.BridgeCoordinatorAgentId is not null)
            return BridgeTerminalUnavailable;
        // Robot-toggle teams use the Bridge backend, but are distinct from classic/Advanced Bridge panes.
        if (TryGetLiveBridge(chat, out var bridge))
            return IsAgentTeamRoster(bridge.Panes) && bridge.Panes.All(p => !p.IsBridgeManager && p.BridgeCoordinatorAgentId is null)
                ? null : BridgeTerminalUnavailable;
        if (HasSavedBridgeFor(chat) && SavedBridgeFor(chat) is { } saved)
            return saved.AgentTeam && !saved.HostIsManager && !saved.HostCoordinatesOnly && saved.HostCoordinatorSessionId is null
                ? null : BridgeTerminalUnavailable;
        return chat.IsBridgeAgent || chat.IsBridgeHost ? BridgeTerminalUnavailable : null;
    }

    /// <summary>Attach the clicked chat itself, keeping its provider session, history and pending work.</summary>
    public ChatViewModel OpenChatInTerminal(ChatViewModel chat)
    {
        if (TerminalUnavailableReason(chat) is { } reason) throw new InvalidOperationException(reason);
        if (!TryGetLiveBridge(chat, out _) && HasSavedBridgeFor(chat))
        {
            OpenChat(chat);
            if (!TryGetLiveBridge(chat, out _)) throw new InvalidOperationException("The saved agent team could not be restored. Open it in the desktop first.");
        }
        if (TryGetLiveBridge(chat, out var team))
        {
            var host = team.Panes[0];
            WakeOrReveal(host);
            return host;
        }
        ParkTerminalTeam(chat);
        WakeOrReveal(chat);
        return chat;
    }

    /// <summary>A fresh parked team for /new, copying configuration without changing the source chat.</summary>
    public ChatViewModel CreateTerminalTeam(ChatViewModel? source = null, string? cwd = null)
    {
        var host = new ChatViewModel(cwd ?? source?.Cwd ?? PreferredNewChatCwd,
            title: "Vibe Code Terminal", accountId: source?.AccountId,
            provider: source?.Provider ?? AppSettings.Current.DefaultProvider);
        if (source is not null)
        {
            host.Model = source.ChosenModel;
            host.Effort = source.Effort;
            host.FastMode = source.FastMode;
            host.UltrafastMode = source.UltrafastMode;
            host.ExcludeFromMemory = source.ExcludeFromMemory;
            host.AgentTeamAccountId = source.AgentTeamAccountId;
        }
        host.SetMode(source?.Mode ?? AppSettings.Current.DefaultMode, remember: false);
        host.AgentTeamMode = true;
        host.WaitForAgentTeamPrompt();
        Track(host);
        ParkTerminalTeam(host);
        Chats.Insert(0, host);
        RememberRecentDirectory(host.Cwd);
        return host;
    }

    private void ParkTerminalTeam(ChatViewModel host)
    {
        var bridge = new LiveBridge
        {
            Panes = new BridgePaneCollection(), Errored = new HashSet<ChatViewModel>(),
            Activity = DateTime.Now, Board = FreeBridgeBoard(host.Cwd), Peers = new PeerTrafficLedger(),
            PanelChat = host,
        };
        _parkedBridges[host] = bridge;
        PrepareAgentTeamHost(host, bridge.Board);
        bridge.Panes.Add(host);
        MarkAgentTeam(bridge.Panes, bridge.Board);
        StartBridgeIdleTimer();
        RefreshResumableBridges();
        RequestSave();
    }

    public IReadOnlyList<ChatViewModel> TerminalTeamRoster(ChatViewModel host) =>
        TryGetLiveBridge(host, out var bridge) ? bridge.Panes : Array.Empty<ChatViewModel>();

    public bool TryRunTerminalTeamCommand(ChatViewModel host, string text)
    {
        if (AgentTeamPolicy.TryParseMessage(text, out var rest))
        {
            var team = TeamFor(host);
            var (target, message) = SplitRecipient(team, rest);
            if (target is { IsIdleAsleep: true } && message.Length > 0)
            {
                StagePeerNotice(target, AgentTeamPolicy.DirectMessageNote(false,
                    AgentTeamPolicy.TeamUpdate(UnseenTeamEntries(target.TeamSeen, team, target))));
                if (!target.Send(message, null)) return false;
                MarkTeamSeen(target, team);
                return true;
            }
        }
        return TryRunAgentTeamCommand(host, text);
    }

    /// <summary>Reuse a free teammate and its session; summon another only when none is available.</summary>
    public bool SendToTerminalTeam(ChatViewModel host, string text, BridgeAgentConfiguration? configuration = null,
        IReadOnlyList<Attachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 } || !TryGetLiveBridge(host, out var bridge)) return false;
        var target = BridgePanes.Contains(host) ? null : bridge;
        var panes = PanesFor(target);
        if (ChatViewModel.NextFreeTeammate(panes) is { } free)
        {
            if (configuration is not null) free.ApplyBridgeConfiguration(configuration);
            StagePeerNotice(free, TerminalTeamNotice);
            return HandToTeammate(free, panes, text, attachments, target);
        }
        if (!BridgeAgentPolicy.CanAdd(BridgeAgentPolicy.Seats(panes), AppSettings.Current.BridgeAgentLimit)) return false;
        var accepted = SummonTeammate(host, target, panes, BoardFor(target), text, attachments, configuration: configuration);
        // Prelude is consumed by the next dispatcher turn, after the new session starts.
        if (accepted && panes.LastOrDefault() is { } newcomer) StagePeerNotice(newcomer, TerminalTeamNotice);
        return accepted;
    }

    private const string TerminalTeamNotice = "[BRIDGE TERMINAL]\nThe user is in the separate Vibe Code Terminal console. " +
        "A free teammate takes each prompt; a new teammate joins only when every teammate is busy. You own the prompts handed to you. " +
        "Use the Bridge roster, peer messages and file-edit tools to coordinate in this shared project. " +
        "Keep replies readable as plain terminal text. The user can address an existing agent explicitly with /message.";
}
