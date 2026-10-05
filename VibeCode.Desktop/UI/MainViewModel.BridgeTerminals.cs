namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    public bool IsSingleTerminalBridge => BridgePanes.FirstOrDefault()?.BridgeSingleTerminal == true;
    public bool SecondaryIsSingleTerminalBridge => SecondaryBridgePanes.FirstOrDefault()?.BridgeSingleTerminal == true;
    public string BridgeTerminalsText => $"Agents ({BridgePanes.Count})";
    public string SecondaryBridgeTerminalsText => $"Agents ({SecondaryBridgePanes.Count})";
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
        var single = panes[0].BridgeSingleTerminal;
        var selected = panes.FirstOrDefault(p => p.BridgeTerminalSelected) ?? panes[0];
        foreach (var pane in panes)
        {
            pane.BridgeSingleTerminal = single;
            pane.BridgeTerminalSelected = ReferenceEquals(pane, selected);
        }
    }

    public void SetBridgeTerminalMode(bool single, bool secondary = false)
    {
        var roster = SurfaceRoster(secondary);
        var panes = PanesFor(roster);
        if (panes.Count == 0) return;
        var selected = (secondary ? SecondaryBridgePanelChat : BridgePanelChat) ?? panes.FirstOrDefault();
        if (!panes.Contains(selected!)) selected = panes[0];
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
