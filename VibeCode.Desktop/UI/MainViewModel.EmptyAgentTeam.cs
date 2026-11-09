namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    // The composer belongs to the team even when its last provider process is gone.
    // Give it a fresh identity without starting or resuming a provider session.
    private ChatViewModel? CreateWaitingTeamIfEmpty(ChatViewModel departing, IEnumerable<ChatViewModel> roster)
    {
        var panes = roster.ToList();
        var wholeTeam = IsAgentTeamRoster(panes);
        var team = wholeTeam || departing.AgentTeamAnchorId is null && !departing.AgentTeamMode
            ? panes : PaneTeam(departing, panes);
        if (team.Any(p => !ReferenceEquals(p, departing) && p.Status != "closed")) return null;
        var waiting = NewWaitingTeam(team[0]);
        if (!wholeTeam)
        {
            waiting.AgentTeamAnchorId = waiting.BridgeAgentId;
            foreach (var member in team.Where(p => !ReferenceEquals(p, departing)))
                member.AgentTeamAnchorId = waiting.BridgeAgentId;
        }
        return waiting;
    }

    private ChatViewModel NewWaitingTeam(ChatViewModel source)
    {
        var waiting = new ChatViewModel(source.Cwd, title: source.Title, accountId: source.AccountId, provider: source.Provider)
        {
            Model = source.ChosenModel, Effort = source.Effort,
            FastMode = source.FastMode, UltrafastMode = source.UltrafastMode,
            ExcludeFromMemory = source.ExcludeFromMemory, Pinned = source.Pinned,
            AgentTeamMode = true, AgentTeamAccountId = source.AgentTeamAccountId,
            IsAgentTeamMember = true, BridgeLabel = source.BridgeLabel,
            BridgeSingleTerminal = source.BridgeSingleTerminal,
            BridgeTerminalSelected = source.BridgeTerminalSelected,
        };
        waiting.SetMode(source.Mode);
        CopyTeamComposer(source, waiting);
        waiting.WaitForAgentTeamPrompt();
        Track(waiting);
        return waiting;
    }

    private static void CopyTeamComposer(ChatViewModel source, ChatViewModel target)
    {
        if (string.IsNullOrEmpty(target.Draft)) target.Draft = source.Draft;
        foreach (var attachment in source.Attachments)
            if (!target.Attachments.Contains(attachment)) target.Attachments.Add(attachment);
    }

    private void KeepEmptyAgentTeam(ChatViewModel departed)
    {
        var waiting = NewWaitingTeam(departed);
        var index = Chats.IndexOf(departed);
        if (index >= 0) Chats[index] = waiting;
        else Chats.Add(waiting);
        ForgetPeerState(departed);
        if (ReferenceEquals(ActiveChat, departed)) ActiveChat = waiting;
        if (ReferenceEquals(SecondaryActiveChat, departed)) SecondaryActiveChat = waiting;
        BeginAgentTeam(waiting, out _);
        SaveSession();
    }
}
