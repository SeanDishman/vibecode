using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    public bool IsSingleTerminalBridge => BridgePanes.FirstOrDefault()?.BridgeSingleTerminal == true;
    public bool SecondaryIsSingleTerminalBridge => SecondaryBridgePanes.FirstOrDefault()?.BridgeSingleTerminal == true;
    public string BridgeTerminalsText => $"Agents ({BridgePanes.Count})";
    public string SecondaryBridgeTerminalsText => $"Agents ({SecondaryBridgePanes.Count})";
    public bool CanUseSeparateBridgeTerminals(bool secondary = false) =>
        !RequiresSingleBridgeTerminal(PanesFor(SurfaceRoster(secondary)));

    private static bool RequiresSingleBridgeTerminal(IReadOnlyList<ChatViewModel> panes) =>
        BridgeAgentPolicy.RequiresSingleTerminal(panes.Count) && panes.Any(p => p.IsBridgeManager);
    private BridgeSharedTerminalViewModel? _sharedBridgeTerminal;
    private BridgeSharedTerminalViewModel? _secondarySharedBridgeTerminal;
    public BridgeSharedTerminalViewModel SharedBridgeTerminal
    {
        get
        {
            if (_sharedBridgeTerminal is null)
            {
                _sharedBridgeTerminal = new(SelectBridgeTerminal, SetBridgeReviewLevel);
                _sharedBridgeTerminal.Reconcile(BridgePanes);
            }
            return _sharedBridgeTerminal;
        }
    }
    public BridgeSharedTerminalViewModel SecondarySharedBridgeTerminal
    {
        get
        {
            if (SecondaryBridge is null) return SharedBridgeTerminal;
            if (_secondarySharedBridgeTerminal is null)
            {
                _secondarySharedBridgeTerminal = new(SelectBridgeTerminal, SetBridgeReviewLevel);
                _secondarySharedBridgeTerminal.Reconcile(SecondaryBridgePanes);
            }
            return _secondarySharedBridgeTerminal;
        }
    }

    private static void ReconcileBridgeTerminals(IReadOnlyList<ChatViewModel> panes)
    {
        if (panes.Count == 0) return;
        ReconcileBridgeGroups(panes);
        ReconcilePaneTeams(panes);
        // An agent team is always one terminal whose composer stays on the chat it grew from.
        var team = IsAgentTeamRoster(panes);
        var single = team || panes[0].BridgeSingleTerminal || RequiresSingleBridgeTerminal(panes);
        var selected = team ? panes[0] : panes.FirstOrDefault(p => p.BridgeTerminalSelected) ?? panes[0];
        foreach (var pane in panes)
        {
            pane.BridgeSingleTerminal = single;
            pane.BridgeTerminalSelected = ReferenceEquals(pane, selected);
        }
    }

    /// <summary>
    /// Keep every team inside a classic Bridge whole. A team whose pane left the Bridge moves to its next member, which
    /// becomes the team's pane; and a pane whose team has grown past one agent shows the team's one terminal in place of
    /// its own transcript, while the summoned agents stay out of the grid. Teams that are a whole Bridge need none of this.
    /// </summary>
    private static void ReconcilePaneTeams(IReadOnlyList<ChatViewModel> panes)
    {
        foreach (var team in panes.Where(p => p.AgentTeamAnchorId is not null).GroupBy(p => p.AgentTeamAnchorId!).ToList())
        {
            if (team.Any(p => p.BridgeAgentId == team.Key)) continue;
            var heir = team.First().BridgeAgentId;
            foreach (var member in team) member.AgentTeamAnchorId = heir;
        }
        AssignTeamSeats(panes);
        foreach (var pane in panes)
        {
            var members = pane.AgentTeamAnchorId == pane.BridgeAgentId
                ? panes.Where(p => p.AgentTeamAnchorId == pane.BridgeAgentId).OrderBy(p => ReferenceEquals(p, pane) ? 0 : 1).ToList()
                : null;
            if (members is { Count: >= 2 })
            {
                // The terminal is the team's own: picking a recipient there must not switch the whole Bridge's layout.
                pane.TeamTerminal ??= new BridgeSharedTerminalViewModel(_ => { }, (_, _) => { });
                pane.TeamTerminal.Reconcile(members);
            }
            else if (pane.TeamTerminal is { } stale)
            {
                pane.TeamTerminal = null;
                stale.Dispose();
            }
        }
    }

    public void SetBridgeTerminalMode(bool single, bool secondary = false)
    {
        var roster = SurfaceRoster(secondary);
        var panes = PanesFor(roster);
        if (panes.Count == 0) return;
        single |= RequiresSingleBridgeTerminal(panes) || IsAgentTeamRoster(panes);
        var selected = (secondary ? SecondaryBridgePanelChat : BridgePanelChat) ?? panes.FirstOrDefault();
        if (!panes.Contains(selected!) || IsAgentTeamRoster(panes)) selected = panes[0];
        foreach (var pane in panes)
        {
            pane.BridgeSingleTerminal = single;
            pane.BridgeTerminalSelected = ReferenceEquals(pane, selected);
            pane.BridgeVisible = true;
            pane.BridgeExpanded = false;
            pane.BridgeMinimized = false;
        }
        RaiseRosterUi(roster);
        SaveBridge(panes, BoardFor(roster));
        RequestSave();
    }

    public void SelectBridgeTerminal(ChatViewModel pane)
    {
        if (!TryGetLiveBridge(pane, out var bridge)) return;
        if (IsAgentTeamRoster(bridge.Panes)) pane = bridge.Panes[0];   // a team has no agent to switch to
        if (BridgePanes.Contains(pane)) _sharedBridgeTerminal?.ShowAll();
        else if (SecondaryBridgePanes.Contains(pane)) _secondarySharedBridgeTerminal?.ShowAll();
        foreach (var peer in bridge.Panes)
        {
            peer.BridgeSingleTerminal = true;
            peer.BridgeTerminalSelected = ReferenceEquals(peer, pane);
            peer.BridgeVisible = true;
            peer.BridgeExpanded = false;
            peer.BridgeMinimized = false;
        }
        SelectBridgePane(pane);
        RaiseRosterUi(BridgePanes.Contains(pane) ? null : bridge);
        SaveBridge(bridge.Panes, bridge.Board);
        RequestSave();
    }

    private static void CopyBridgeTerminalState(ChatViewModel source, ChatViewModel target)
    {
        target.BridgeWork = source.BridgeWork;
        target.BridgeAgentId = source.BridgeAgentId;
        target.BridgeSingleTerminal = source.BridgeSingleTerminal;
        target.BridgeTerminalSelected = source.BridgeTerminalSelected;
        target.BridgeActivitySummary = source.BridgeActivitySummary;
        target.BridgeTaskName = source.BridgeTaskName;
        target.BridgeCoordinatesOnly = source.BridgeCoordinatesOnly;
        target.BridgeCoordinatorAgentId = source.BridgeCoordinatorAgentId;
        target.BridgeOrchestrationScope = source.BridgeOrchestrationScope;
        target.BridgeTaskState = source.BridgeTaskState;
        target.BridgeReviewState = source.BridgeReviewState;
        target.BridgeReviewSummary = source.BridgeReviewSummary;
        target.BridgeReviewLevel = source.BridgeReviewLevel;
        target.BridgeWorkerConfiguration = source.BridgeWorkerConfiguration;
    }

    private ChatViewModel ReconnectLiveBridgePane(ChatViewModel old)
    {
        var replacement = new ChatViewModel(old.Cwd, resume: old.SessionId, title: old.Title,
            accountId: old.AccountId, provider: old.Provider)
        {
            Pinned = old.Pinned, Draft = old.Draft, ExcludeFromMemory = old.ExcludeFromMemory,
            BridgeLabel = old.BridgeLabel, IsBridgeHost = old.IsBridgeHost, IsAdvancedBridgeHost = old.IsAdvancedBridgeHost,
            IsBridgeManager = old.IsBridgeManager,
            BridgeVisible = old.BridgeVisible, BridgeExpanded = old.BridgeExpanded,
            BridgeMinimized = old.BridgeMinimized, OnSecondMonitor = old.OnSecondMonitor,
            AppendSystemPrompt = old.AppendSystemPrompt, Prelude = old.Prelude,
        };
        replacement.SetMode(old.Mode);
        replacement.ApplyBridgeConfiguration(BridgeAgentConfigurationPolicy.From(old));
        replacement.FastMode = old.FastMode;
        replacement.UltrafastMode = old.UltrafastMode;
        CopyAgentTeamState(old, replacement);
        replacement.RestoreGoal(old.Goal);
        // Keep retained messages while replacing only the failed provider process.
        replacement.PeerMailbox = old.PeerMailbox;
        old.PeerMailbox = null;
        var wasActive = ReferenceEquals(ActiveChat, old);
        var wasSecondaryActive = ReferenceEquals(SecondaryActiveChat, old);
        var chatIndex = Chats.IndexOf(old);
        old.Close();
        Track(replacement);
        if (chatIndex >= 0) Chats[chatIndex] = replacement;
        var roster = ReplaceLivePane(old, replacement)!;
        if (wasActive) ActiveChat = replacement;
        if (wasSecondaryActive) SecondaryActiveChat = replacement;
        replacement.Start();
        SaveBridge(roster.Panes, roster.Board);
        RequestSave();
        return replacement;
    }
}
