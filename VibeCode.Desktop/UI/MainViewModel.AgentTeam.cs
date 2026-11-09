using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Protocol;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>
/// The composer's robot toggle. A prompt sent while it is on goes to a free agent of the team; when every agent is busy a
/// fresh agent of the composer's provider, model and account joins the one shared terminal instead of the prompt queuing.
/// Started from a chat outside any Bridge, the team is a regular single-terminal Bridge of its own; started from a pane
/// of a classic Bridge, it lives inside that pane, and the Bridge keeps every other pane - so one classic Bridge can hold
/// several teams. Add agent on a team's own Bridge turns it into the classic kind, the whole team in its first pane. Either
/// way every Bridge tool (roster, messages, transcripts, file edits) works unchanged;
/// <see cref="AgentTeamPolicy"/> adds names, announcements and the shared context every prompt carries.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Flip the robot toggle. A live roster shares one switch, so every member's composer agrees. The team
    /// replaces queuing, so turning it on also turns the extended queue off (pending prompts fold back into the
    /// ordinary queue, exactly as when the user turns the queue off by hand).</summary>
    public void SetAgentTeamMode(ChatViewModel chat, bool on)
    {
        if (on && !chat.ShowAgentTeamToggle) return;
        // A Bridge that is one team shares one switch. In a classic Bridge each pane's robot is its own: it belongs to
        // that pane's team, and every other pane keeps whatever it had.
        var panes = TryGetLiveBridge(chat, out var bridge) && bridge.Panes.All(p => p.ShowAgentTeamToggle)
            ? IsAgentTeamRoster(bridge.Panes) ? bridge.Panes.ToList() : PaneTeam(chat, bridge.Panes)
            : new List<ChatViewModel> { chat };
        foreach (var pane in panes)
        {
            pane.AgentTeamMode = on;
            if (on && pane.ExtendedQueueEnabled) pane.SetExtendedQueueEnabled(false);
        }
        RequestSave();
    }

    /// <summary>The robot's right-click menu: Automatic first (this chat's account, or another one while it nears its
    /// limit), then every account of the chat's provider, the team's choice ticked. A login that must sign in again is
    /// listed but cannot be picked.</summary>
    public IReadOnlyList<TeamAccountOption> AgentTeamAccountOptions(ChatViewModel chat)
    {
        var accounts = BridgeAccountChoice.For(chat.Provider);
        var chosen = chat.AgentTeamAccountId;
        var own = accounts.FirstOrDefault(a => SameAccount(a.AccountId, chat.AccountId))?.Label ?? chat.AccountLabel;
        var options = new List<TeamAccountOption>
        {
            new(null, "Automatic", $"{(string.IsNullOrWhiteSpace(own) ? "This chat's account" : own)} until it nears its limit",
                chosen is null, true),
        };
        options.AddRange(accounts.Select(a => new TeamAccountOption(a.AccountId, a.Label, a.IsUsable || a.AtUsageLimit ? a.Detail : "Sign in again in AI accounts",
            chosen is not null && SameAccount(a.AccountId, chosen), a.IsUsable)));
        return options;
    }

    /// <summary>Pick the account the robot's new agents join on, for the whole team (null: automatic). Agents already
    /// working stay where they are; only the next one summoned uses it.</summary>
    public void SetAgentTeamAccount(ChatViewModel chat, string? accountId)
    {
        var panes = TryGetLiveBridge(chat, out var bridge) && bridge.Panes.All(p => p.ShowAgentTeamToggle)
            ? IsAgentTeamRoster(bridge.Panes) ? bridge.Panes.ToList() : PaneTeam(chat, bridge.Panes)
            : new List<ChatViewModel> { chat };
        if (!panes.Contains(chat)) panes.Add(chat);
        foreach (var pane in panes) pane.AgentTeamAccountId = accountId;
        RequestSave();
    }

    /// <summary>The account picked for the team, while it can still sign in. Null leaves the choice automatic.</summary>
    private static string? ChosenTeamAccount(ChatViewModel origin) =>
        origin.AgentTeamAccountId is { } chosen
        && BridgeAccountChoice.For(origin.Provider).Any(a => a.IsUsable && SameAccount(a.AccountId, chosen)) ? chosen : null;

    /// <summary>
    /// Route one composer prompt while the robot toggle is on. Returns false (keeping the composer text) only when
    /// nothing could take it. A free agent always takes the prompt - in a chat with no team yet, that is the chat's own
    /// agent - and a new agent is summoned only when every agent is busy. Never into a queue.
    /// </summary>
    public bool SendToAgentTeam(ChatViewModel origin, string text, IReadOnlyList<Attachment>? attachments)
    {
        var atts = attachments is { Count: > 0 } ? attachments.ToList() : null;
        if (string.IsNullOrWhiteSpace(text) && atts is null) return false;
        // Commands act on the chat that typed them: summoning an agent to run /compact or a /goal control is nonsense.
        if (text.TrimStart().StartsWith('/')) return origin.Send(text, atts);

        var live = TryGetLiveBridge(origin, out var bridge);
        if (live && !bridge.Panes.All(p => p.ShowAgentTeamToggle)) return origin.Send(text, atts);   // Advanced Bridge roles
        // A classic Bridge stays a classic Bridge: the robot grows a team inside the pane it was turned on in.
        if (live && !IsAgentTeamRoster(bridge.Panes)) return SendToPaneTeam(origin, bridge, text, atts);
        if (!live && !HasSavedBridgeFor(origin) && (origin.IsFreshForAgentTeam || origin.IsAvailableForAgentTeamPrompt))
        {
            // Nobody else is here and this chat's own agent is free, so it takes the prompt: a teammate is summoned only
            // to work alongside an agent that is busy.
            if (!origin.AgentTeamIntroduced && origin.AgentTeamName.Length == 0)
            {
                StagePeerNotice(origin, AgentTeamPolicy.FirstAgentNote(origin.Provider, NameHint(origin)));
                origin.AgentTeamIntroduced = true;
            }
            return origin.Send(text, atts);
        }

        LiveBridge? target;
        if (live) target = BridgePanes.Contains(origin) ? null : bridge;
        else if (!BeginAgentTeam(origin, out target)) return origin.Send(text, atts);
        // A saved classic Bridge - a team with a pane added beside it, say - resumes classic. The prompt then takes the
        // live route above, into the host pane's team, instead of every pane being folded into one team.
        else if (!IsAgentTeamRoster(PanesFor(target))) return SendToAgentTeam(origin, text, atts);

        var panes = PanesFor(target);
        var board = BoardFor(target);
        MarkAgentTeam(panes, board);
        RefreshBridgeWorkingCue(panes);   // a converted Bridge's composer must offer Stop for teammates already running
        RevealAgentTeam(target);

        if (ChatViewModel.NextFreeTeammate(panes) is { } free) return HandToTeammate(free, panes, text, atts, target);
        if (!BridgeAgentPolicy.CanAdd(BridgeAgentPolicy.Seats(panes), AppSettings.Current.BridgeAgentLimit)) return RefuseAllBusy(origin, panes);
        return SummonTeammate(origin, target, panes, board, text, atts);
    }

    /// <summary>
    /// The robot in a classic Bridge pane. The team grows inside that pane: a free member takes the prompt, and when every
    /// member is busy a newcomer joins the pane's own terminal - never a pane of its own. Every other pane, and any team it
    /// runs, stays exactly as it was, so one classic Bridge can hold several teams.
    /// </summary>
    private bool SendToPaneTeam(ChatViewModel origin, LiveBridge bridge, string text, IReadOnlyList<Attachment>? atts)
    {
        var target = BridgePanes.Contains(origin) ? null : bridge;
        var roster = PanesFor(target);
        var board = BoardFor(target);
        var team = PaneTeam(origin, roster);
        var anchor = team[0];
        if (team.Count == 1 && (anchor.IsFreshForAgentTeam || anchor.IsAvailableForAgentTeamPrompt))
        {
            // Nobody else in this pane yet and its own agent is free, so it takes the prompt: a teammate is summoned only
            // to work alongside an agent that is busy.
            if (!anchor.AgentTeamIntroduced && anchor.AgentTeamName.Length == 0)
            {
                StagePeerNotice(anchor, AgentTeamPolicy.FirstAgentNote(anchor.Provider, NameHint(anchor)));
                anchor.AgentTeamIntroduced = true;
            }
            return anchor.Send(text, atts);
        }
        StartPaneTeam(anchor, roster, board);
        if (ChatViewModel.NextFreeTeammate(team) is { } free) return HandToTeammate(free, team, text, atts, target);
        // The pane's team is capped on its own; the Bridge's other panes and their teams don't count against it.
        if (!BridgeAgentPolicy.CanAdd(team.Count, AppSettings.Current.BridgeAgentLimit)) return RefuseAllBusy(origin, team, paneTeam: true);
        return SummonTeammate(anchor, target, team, board, text, atts, teamAnchor: anchor);
    }

    /// <summary>Make <paramref name="anchor"/>'s pane the home of a team, once. Its agent is told it now leads a team of
    /// its own inside the Bridge; nobody else in the Bridge changes.</summary>
    private static void StartPaneTeam(ChatViewModel anchor, IReadOnlyList<ChatViewModel> roster, string board)
    {
        if (anchor.AgentTeamAnchorId == anchor.BridgeAgentId) return;
        anchor.AgentTeamAnchorId = anchor.BridgeAgentId;
        anchor.AgentTeamSeatNumber = 1;   // the pane's own agent is its team's agent 1, whatever its Bridge number
        anchor.AgentTeamMode = true;
        if (anchor.ExtendedQueueEnabled) anchor.SetExtendedQueueEnabled(false);
        anchor.IsAgentTeamMember = true;
        anchor.AppendSystemPrompt = BridgePrompt(board, anchor, BridgeNumberOf(anchor), roster.Select(BridgeNumberOf).Where(n => n > 0).ToList());
        StagePeerNotice(anchor, AgentTeamPolicy.Header + "\nYour pane in this Bridge just became an agent team: prompts from it go to a free " +
            "teammate, and a new agent joins your pane's terminal when every teammate is busy. The Bridge's other agents are not in your " +
            "team and carry on as before." + AgentTeamPolicy.SessionClause(anchor.Provider, paneTeam: true));
    }

    /// <summary>Add agent on a Bridge that is one agent team. The team moves into the Bridge's first pane - its host that
    /// pane's agent, every other member that pane's team, the shape the robot builds inside a classic pane - so the agent
    /// being added gets a pane of its own beside the team instead of joining it. Nobody stops or restarts: each member keeps
    /// its session, name and transcript, and the pane's robot still hands every prompt to the team.</summary>
    private static void MoveTeamIntoFirstPane(IReadOnlyList<ChatViewModel> panes)
    {
        if (!IsAgentTeamRoster(panes)) return;
        var host = panes[0];
        foreach (var member in panes)
        {
            member.AgentTeamAnchorId = host.BridgeAgentId;
            member.BridgeSingleTerminal = false;   // the Bridge's panes sit side by side; the team's one terminal fills the first
            StagePeerNotice(member, AgentTeamPolicy.Header + "\nYour agent team is now the first pane of a larger Bridge: the user is " +
                "adding agents with panes of their own beside it. They are not in your team; your pane's robot still gives each prompt " +
                "to a free teammate, and a new agent still joins your pane's terminal when every teammate is busy." +
                AgentTeamPolicy.SessionClause(member.Provider, paneTeam: true));
        }
        AssignTeamSeats(panes);   // the numbers the team already went by: the host is agent 1 and the rest follow in order
    }

    /// <summary>A classic-Bridge pane's team, the pane whose robot started it first; just the chat when it has none.</summary>
    private static List<ChatViewModel> PaneTeam(ChatViewModel chat, IReadOnlyList<ChatViewModel> roster)
    {
        if (chat.AgentTeamAnchorId is not { } anchor) return [chat];
        return roster.Where(p => p.AgentTeamAnchorId == anchor).OrderBy(p => p.BridgeAgentId == anchor ? 0 : 1).ToList();
    }

    /// <summary>Number each classic-Bridge pane's team on its own: the pane's agent is 1 and the rest follow by arrival, so
    /// the second agent in a pane is "Agent 2" however many agents the Bridge's other panes hold. Everyone else keeps their
    /// Bridge number. Runs with every roster change, so a departure closes the gap.</summary>
    private static void AssignTeamSeats(IReadOnlyList<ChatViewModel> panes)
    {
        var seats = new Dictionary<ChatViewModel, int>();
        foreach (var team in panes.Where(p => p.AgentTeamAnchorId is not null).GroupBy(p => p.AgentTeamAnchorId!))
        {
            var seat = 0;
            foreach (var member in team.OrderBy(p => p.BridgeAgentId == team.Key ? 0 : 1)) seats[member] = ++seat;
        }
        foreach (var pane in panes) pane.AgentTeamSeatNumber = seats.GetValueOrDefault(pane);
    }

    /// <summary>The team <paramref name="chat"/> works in, its first agent first: the whole roster of a Bridge that is one
    /// team, the chat's own pane team inside a classic Bridge, or just the chat.</summary>
    private List<ChatViewModel> TeamFor(ChatViewModel chat) =>
        !TryGetLiveBridge(chat, out var bridge) ? [chat]
        : IsAgentTeamRoster(bridge.Panes) ? bridge.Panes.ToList()
        : PaneTeam(chat, bridge.Panes);

    /// <summary>Every agent is busy: a newcomer joins with the team's context. Where the provider can fork a session

    /// decision in it - plus what the others did since that teammate last heard; otherwise (Gemini) it gets the team log
    /// so far. <paramref name="teamAnchor"/> is the classic-Bridge pane the team belongs to; null when the team is the
    /// whole Bridge.</summary>
    private bool SummonTeammate(ChatViewModel origin, LiveBridge? target, IReadOnlyList<ChatViewModel> panes, string board,
        string text, IReadOnlyList<Attachment>? atts, ChatViewModel? teamAnchor = null, BridgeAgentConfiguration? configuration = null)
    {
        // The account picked from the robot's right-click menu; otherwise the origin's own, or past the pacing threshold
        // another usable one. On another account no teammate's session lives to fork.
        var chosen = ChosenTeamAccount(origin);
        var originUsable = BridgeAccountChoice.For(origin.Provider)
            .Any(a => a.IsUsable && SameAccount(a.AccountId, origin.AccountId));
        var accountId = chosen ?? PacedAccountFor(origin) ?? origin.AccountId;
        var source = ForkSourceFor(origin, panes, out var forkPercent, out var skippedFull, out var compactFirst);
        if (source is not null && !SameAccount(accountId, origin.AccountId))
            (source, forkPercent, skippedFull, compactFirst) = (null, null, false, false);
        string context;
        var checkpoints = 0;
        if (source is not null)
        {
            var seen = new Dictionary<string, long>(source.TeamSeen, StringComparer.Ordinal);
            // Its latest request and the work on it so far are in the copy too; the update says so in as many words.
            seen[source.BridgeAgentId] = LastPromptSequence(source) - 1;
            context = AgentTeamPolicy.TeamUpdate(UnseenTeamEntries(seen, panes, recipient: null));
        }
        else
        {
            // Without a copy, what each teammate settled travels as its checkpoint; the log alone keeps only the newest lines.
            var saved = TeamCheckpointContext(panes);
            checkpoints = panes.Count(p => !string.IsNullOrWhiteSpace(p.RecoveryCheckpoint?.Objective));
            context = string.Join("\n", new[] { AgentTeamPolicy.TeamChat(TeamEntries(panes)), saved }.Where(part => part.Length > 0));
        }
        var agent = AddBridgeAgent(origin.Provider, target, origin, null, configuration, accountId: accountId, forkFrom: source, teamAnchor: teamAnchor);
        if (agent is null) return RefuseAllBusy(origin, panes, paneTeam: teamAnchor is not null);
        // A parked roster is not reconciled by the on-screen refresh, so number the newcomer's team here.
        AssignTeamSeats(PanesFor(target));
        // The team as it is now, newcomer included; for a whole-Bridge team that is the live roster itself.
        var team = teamAnchor is null ? panes : PaneTeam(teamAnchor, PanesFor(target));
        agent.CarriesTeamHistory = source is not null;
        // A copy of a teammate past the fork ceiling is compacted before the newcomer's first prompt, which waits behind it.
        if (compactFirst) agent.CompactCopyBeforeFirstPrompt();
        agent.Prelude = AgentTeamPolicy.JoinNote(agent.Provider, agent.AgentTeamNumber, team.Count, context,
            source is null ? null : TeamSpeaker(source), NameHint(agent));


        {
            agent.Items.Add(new BannerItem
            {
                Level = "info",
                Text = TeamMemoryPolicy.JoinBanner(source is null ? null : source.AgentTeamName.Length > 0 ? source.AgentTeamName : TeamSpeaker(source),
                    forkPercent, skippedFull, checkpoints, compactFirst),
            });
        }
        MarkTeamSeen(agent, team);
        if (agent.SendAgentTeamPrompt(text, atts))
        {
            TellUnnamedTeammates(agent, team);
            NoteBridgeActivity(target);
            SaveBridge(PanesFor(target), board);
            return true;
        }
        origin.Items.Add(new BannerItem { Level = "error", Text = $"{agent.AgentDisplay} agent #{agent.AgentTeamNumber} could not take the prompt. It is still in the composer." });
        return false;
    }

    /// <summary>A teammate still without a name - usually the team's first agent, busy since before anyone joined - learns at
    /// once that it is no longer alone, steered into its running turn where the provider allows it and otherwise with its
    /// next message, so it names itself instead of staying "Agent 1". Each agent is told this once; a later hand-off still
    /// asks for a name while it has none.</summary>
    private void TellUnnamedTeammates(ChatViewModel newcomer, IReadOnlyList<ChatViewModel> team)
    {
        foreach (var mate in team.Where(p => !ReferenceEquals(p, newcomer) && p.AgentTeamName.Length == 0 && !p.AgentTeamNamePrompted).ToList())
        {
            mate.AgentTeamNamePrompted = true;
            _ = SteerOrStageAsync(mate, AgentTeamPolicy.NotAloneNote(mate.Provider, mate.AgentTeamNumber, team.Count,
                TeamSpeaker(newcomer), NameHint(mate)));
        }
    }

    private static async Task SteerOrStageAsync(ChatViewModel mate, string note)
    {
        if (await mate.NotifyAgentTeamNowAsync(note)) return;
        if (mate.Status != "closed" && mate.AgentTeamName.Length == 0) StagePeerNotice(mate, note);
    }

    /// <summary>Whose conversation a newcomer copies: a teammate of the newcomer's provider - one that can fork a session
    /// (<see cref="ChatViewModel.ForksSessions"/>) - on the same account and folder (where its session lives) that holds
    /// the team's history, preferring the one with the most free context (see <see cref="TeamMemoryPolicy"/>). When every
    /// candidate is too full, a provider that compacts sessions still copies the emptiest and sets
    /// <paramref name="compactFirst"/>; any other returns null with <paramref name="skippedFull"/>. Null too when no session
    /// can be forked; the newcomer then gets the team log and the teammates' checkpoints instead.</summary>
    private static ChatViewModel? ForkSourceFor(ChatViewModel origin, IReadOnlyList<ChatViewModel> panes,
        out double? contextPercent, out bool skippedFull, out bool compactFirst)
    {
        contextPercent = null;
        skippedFull = false;
        compactFirst = false;
        if (!ChatViewModel.ForksSessions(origin.Provider) || panes.Count == 0) return null;
        var anchor = panes[0];
        return EmptiestForkSource(panes
            .Where(p => string.Equals(p.Provider, origin.Provider, StringComparison.OrdinalIgnoreCase)
                        && p.CanFork && p.Status is not ("error" or "closed")
                        && string.Equals(p.AccountId, origin.AccountId, StringComparison.Ordinal)
                        && string.Equals(p.Cwd, anchor.Cwd, StringComparison.OrdinalIgnoreCase)
                        && (ReferenceEquals(p, anchor) || p.CarriesTeamHistory)),
            ChatViewModel.CompactsSessions(origin.Provider), out contextPercent, out skippedFull, out compactFirst);
    }



    private static long LastPromptSequence(ChatViewModel pane) =>
        pane.Items.OfType<UserItem>().LastOrDefault(u => u.IsUserPrompt && !u.FromSubagent)?.DisplaySequence ?? 0;

    /// <summary>Make a standalone chat agent #1 of a new team, without the automatic second agent Bridge adds.
    /// A chat with a saved roster resumes it instead, so turning the robot on never overwrites a saved Bridge.</summary>
    private bool BeginAgentTeam(ChatViewModel host, out LiveBridge? target)
    {
        target = null;
        var primary = ReferenceEquals(host, ActiveChat);
        if (!primary && !ReferenceEquals(host, SecondaryActiveChat)) return false;
        if (HasSavedBridgeFor(host))
        {
            if (primary)
            {
                if (IsBridge) ParkActiveBridge();
                if (RestoreBridge(host)) return BridgePanes.Contains(host);
            }
            else if (RestoreBridge(host, showPrimary: false) && TryGetLiveBridge(host, out var resumed))
            {
                target = resumed;
                return true;
            }
            if (TryGetLiveBridge(host, out _)) return false;
        }

        if (primary)
        {
            if (IsBridge) ParkActiveBridge();
            _bridgeBoard = DefaultBridgeBoard;
            _peerTraffic.Clear();
            PrepareAgentTeamHost(host, _bridgeBoard);
            BridgePanes.Add(host);
            ShowBridge = true;
            NoteBridgeActivity();
            StartBridgeIdleTimer();
            RaiseBridgeUi();
        }
        else
        {
            // Shell 2 builds its roster straight into a parked slot, exactly like Bridge there.
            var bridge = new LiveBridge
            {
                Panes = new BridgePaneCollection(),
                Errored = new HashSet<ChatViewModel>(),
                Activity = DateTime.Now,
                Board = FreeBridgeBoard(host.Cwd),
                Peers = new PeerTrafficLedger(),
            };
            _parkedBridges[host] = bridge;
            PrepareAgentTeamHost(host, bridge.Board);
            bridge.Panes.Add(host);
            bridge.PanelChat = host;
            SetSecondaryBridgeHost(host);
            SecondaryShowBridge = true;
            NoteBridgeActivity(bridge);
            StartBridgeIdleTimer();
            RaiseSecondaryBridgeUi();
            RefreshResumableBridges();
            target = bridge;
        }
        _ = host.RequestBridgeTaskTitleNowAsync();   // a host mid-task titles itself now, not at its next request
        return true;
    }

    private static void PrepareAgentTeamHost(ChatViewModel host, string board)
    {
        host.BridgeLabel = AgentLabel(host, 1);
        host.IsBridgeHost = true;
        host.IsBridgeManager = false;
        host.IsAgentTeamMember = true;
        host.BridgeSingleTerminal = true;
        host.BridgeTerminalSelected = true;
        // The host learns about the team on its next message; its first teammate's join note is appended by AddBridgeAgent.
        StagePeerNotice(host, BridgeJoinPrelude(board, host, 1, new[] { 1, 2 }));
        host.Items.Add(new DividerItem { Label = $"🤖 Agent team started — you are {host.BridgeLabel}" });
    }

    /// <summary>Every member of a team that is a whole Bridge shares the robot switch, the single terminal and the team
    /// contract; a member that has not heard it yet is told on its next message.</summary>
    private static void MarkAgentTeam(IReadOnlyList<ChatViewModel> panes, string board)
    {
        var roster = panes.Select(BridgeNumberOf).Where(n => n > 0).ToList();
        foreach (var pane in panes)
        {
            pane.AgentTeamMode = true;
            if (pane.ExtendedQueueEnabled) pane.SetExtendedQueueEnabled(false);
            pane.BridgeSingleTerminal = true;
            if (pane.IsAgentTeamMember) continue;
            pane.IsAgentTeamMember = true;
            pane.AppendSystemPrompt = BridgePrompt(board, pane, BridgeNumberOf(pane), roster);
            StagePeerNotice(pane, AgentTeamPolicy.Header + "\nThis Bridge just became an agent team: new prompts go to a free agent, " +
                "and a new agent joins when every agent is busy." + AgentTeamPolicy.SessionClause(pane.Provider));
        }
        // One terminal, one composer: it stays on the chat the team grew from.
        foreach (var pane in panes) pane.BridgeTerminalSelected = ReferenceEquals(pane, panes[0]);
    }

    /// <summary>The team composer's Stop (and Esc): interrupt every agent with a running turn in this one terminal.
    /// Outside a team it is the ordinary stop for that chat alone.</summary>
    public void InterruptAgentTeam(ChatViewModel chat)
    {
        if (TryGetLiveBridge(chat, out var bridge) && (IsAgentTeamRoster(bridge.Panes) || chat.AgentTeamAnchorId is not null))
        {
            // A pane's team stops as one, and only that team: the Bridge's other panes keep working.
            foreach (var agent in TeamFor(chat).Where(p => p.CanInterrupt).ToList()) agent.Interrupt();
            return;
        }
        if (chat.CanInterrupt) chat.Interrupt();
    }

    /// <summary>Every member came from the robot toggle of a chat that was not in a Bridge, so the whole roster is one team
    /// shown as one terminal and nothing else. A classic Bridge whose panes run teams of their own is not.</summary>
    private static bool IsAgentTeamRoster(IReadOnlyList<ChatViewModel> panes) =>
        panes.Count > 0 && panes.All(p => p.IsAgentTeamMember && p.AgentTeamAnchorId is null);

    /// <summary>The seats Add agent counts. A Bridge that is one agent team moves into a single pane when an agent is added
    /// beside it (<see cref="MoveTeamIntoFirstPane"/>), so the whole team fills one seat however many agents it holds.</summary>
    private static int AddAgentSeats(IReadOnlyList<ChatViewModel> panes) =>
        IsAgentTeamRoster(panes) ? 1 : BridgeAgentPolicy.Seats(panes);

    /// <summary>Add agent's tooltip on a Bridge that is one agent team, where adding an agent is how a Bridge starts.</summary>
    private static string AddBesideTeamToolTip =>
        "Start a Bridge: the agent you choose gets a pane of its own, and this team keeps working together in the first pane " +
        $"(up to {AppSettings.Current.BridgeAgentLimit} panes; agents in a pane's team don't count)";

    private void RevealAgentTeam(LiveBridge? target)
    {
        if (target is null)
        {
            ShowBridge = true;
            RaiseBridgeUi();
            return;
        }
        if (ReferenceEquals(target, SecondaryBridge)) SecondaryShowBridge = true;
        RaiseSecondaryBridgeUi();
    }

    /// <summary>A free agent takes the prompt, with everything its teammates were asked, said and did since it last
    /// heard - so whichever agent the prompt lands on works from the same shared context.</summary>
    private bool HandToTeammate(ChatViewModel free, IReadOnlyList<ChatViewModel> panes, string text, IReadOnlyList<Attachment>? atts,
        LiveBridge? target)
    {
        var update = AgentTeamPolicy.TeamUpdate(UnseenTeamEntries(free.TeamSeen, panes, free));
        StagePeerNotice(free, AgentTeamPolicy.HandOffNote(free.Provider, named: free.AgentTeamName.Length > 0, update,
            free.AgentTeamName.Length > 0 ? "" : NameHint(free)));
        MarkTeamSeen(free, panes);
        if (!free.SendAgentTeamPrompt(text, atts)) return false;
        NoteBridgeActivity(target);
        return true;
    }

    /// <summary>Every agent is busy and nobody new can join: the prompt stays in the composer — still no queue.</summary>
    private static bool RefuseAllBusy(ChatViewModel origin, IReadOnlyList<ChatViewModel> panes, bool paneTeam = false)
    {
        var limit = BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit);
        origin.Items.Add(new BannerItem
        {
            Level = "info",
            Text = (paneTeam
                       ? $"All {panes.Count} agents in this pane's team are busy and the team is at its limit of {limit}. "
                       : $"All {panes.Count} agents are busy and the team is at its limit of {limit}. ") +
                   "Your prompt is still in the composer: send it when one finishes, stop one with /stop and its name, close an agent, " +
                   "or raise the Bridge agent limit in Settings.",
        });
        return false;
    }

    /// <summary>The whole shared transcript in display order, for a newcomer that cannot start from a copy of a
    /// teammate's conversation.</summary>
    private static IEnumerable<AgentTeamPolicy.ChatEntry> TeamEntries(IEnumerable<ChatViewModel> panes) => panes
        .SelectMany(pane => TeamEntries(pane, after: 0))
        .OrderBy(entry => entry.Sequence)
        .Select(entry => entry.Entry)
        .ToList();

    /// <summary>What <paramref name="recipient"/> has not been told: every other agent's entries after the newest row of
    /// that agent it has seen.</summary>
    private static IEnumerable<AgentTeamPolicy.ChatEntry> UnseenTeamEntries(IReadOnlyDictionary<string, long> seen,
        IReadOnlyList<ChatViewModel> panes, ChatViewModel? recipient) => panes
        .Where(pane => !ReferenceEquals(pane, recipient))
        .SelectMany(pane => TeamEntries(pane, seen.GetValueOrDefault(pane.BridgeAgentId)))
        .OrderBy(entry => entry.Sequence)
        .Select(entry => entry.Entry)
        .ToList();

    /// <summary>Remember the settled prefix. A streaming reply or running tool keeps its sequence when its final
    /// text/result arrives, so it must remain eligible for the next hand-off.</summary>
    private static void MarkTeamSeen(ChatViewModel recipient, IReadOnlyList<ChatViewModel> panes)
    {
        foreach (var pane in panes.Where(p => !ReferenceEquals(p, recipient)))
        {
            var seen = 0L;
            foreach (var item in TeamTranscriptItems(pane).OrderBy(item => item.DisplaySequence))
            {
                if (item is TextItem { Streaming: true } or ToolItem { IsRunning: true }) break;
                seen = item.DisplaySequence;
            }
            recipient.TeamSeen[pane.BridgeAgentId] = seen;
        }
    }

    /// <summary>Tools retain their own sequence inside a compact group. The group may receive another tool after
    /// its earlier contents were shared, so its container sequence cannot be the hand-off cursor.</summary>
    private static IEnumerable<ItemVm> TeamTranscriptItems(ChatViewModel pane)
    {
        foreach (var item in pane.Items.ToList())
        {
            if (item is CompactToolGroupItem group)
            {
                foreach (var tool in group.Tools.ToList()) yield return tool;
            }
            else yield return item;
        }
    }

    /// <summary>One agent's part of the shared transcript after <paramref name="after"/>: the prompts it was given, its
    /// replies, and what it actually did - commands, edits and messages - labelled by name. Reasoning, app notes and
    /// read-only roster checks stay out; bridge_read_chat has the full record.</summary>
    private static IEnumerable<(long Sequence, AgentTeamPolicy.ChatEntry Entry)> TeamEntries(ChatViewModel pane, long after)
    {
        var speaker = TeamSpeaker(pane);
        foreach (var item in TeamTranscriptItems(pane))
        {
            if (item.DisplaySequence <= after) continue;
            switch (item)
            {
                case UserItem { IsUserPrompt: true, FromSubagent: false, HasText: true } prompt when !ChatViewModel.IsSystemInjectedPrompt(prompt.Text):
                    yield return (item.DisplaySequence, new(speaker, prompt.Text, FromUser: true));
                    break;
                case TextItem { HasText: true } message:
                    yield return (item.DisplaySequence, new(speaker, message.Text, FromUser: false));
                    break;
                case ToolItem tool when TeamAction(tool) is { } action:
                    yield return (item.DisplaySequence, new(speaker, action, FromUser: false, IsAction: true));
                    break;
            }
        }
    }

    /// <summary>Tools that only look something up for the agent itself; the log would just be noise with them in it.</summary>
    private static readonly string[] QuietTeamTools =
    [
        "ToolSearch", "TodoWrite", "TaskCreate", "TaskUpdate", "TaskList", "TaskGet", "bridge_list_agents", "bridge_agent_status",
        "bridge_list_tasks", "bridge_read_messages", "bridge_mark_message", "bridge_read_chat", BridgeMcpTools.ProjectReadChatToolName, "bridge_file_edits",
        BridgeMcpTools.AgentNameToolName, "bridge_set_task_title", "chat_set_title", "chat_checkpoint", BridgeMcpTools.TodoToolName,
    ];

    /// <summary>"ran Bash: ssh pi@robot.local … → camera found", or null for a tool the shared log leaves out.</summary>
    private static string? TeamAction(ToolItem tool)
    {
        if (tool.IsTodoTool || QuietTeamTools.Contains(BridgeMcpConnection.CatalogToolName(tool.Name)))
            return null;
        var summary = OneLine(tool.Summary);
        var action = "ran " + tool.DisplayName + (summary.Length > 0 ? ": " + summary : "");
        // A file or page's contents would only crowd out what the agents actually did.
        if (tool.Name is "Read" or "Grep" or "Glob" or "LS" or "WebFetch" or "WebSearch" || tool.IsRunning) return action;
        var result = OneLine(tool.Result);
        if (result.Length > 160) result = result[..159] + "…";
        return result.Length == 0 ? action : action + (tool.IsError ? " → error: " : " → ") + result;
    }

    private static string OneLine(string? text) => string.IsNullOrWhiteSpace(text) ? ""
        : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// /stop in a team composer. "/stop" or "/stop all" stops every agent that is working; "/stop Volt" - a name, an
    /// agent number or a seat label - stops that one. Typing "Volt, stop" only reaches whichever agent takes the
    /// prompt, so this is how the user turns one teammate off. Returns false for anything else, which sends normally.
    /// </summary>
    public bool TryRunAgentTeamCommand(ChatViewModel chat, string text)
    {
        if (!chat.AgentTeamMode && !chat.IsAgentTeamMember) return false;
        var isMessage = AgentTeamPolicy.TryParseMessage(text, out var rest);
        var who = "";
        if (!isMessage && !AgentTeamPolicy.TryParseStop(text, out who)) return false;
        var team = TeamFor(chat);
        var anchor = team.FirstOrDefault() ?? chat;
        if (isMessage) return MessageTeammate(anchor, team, rest);
        if (who.Length == 0)
        {
            var working = team.Where(p => p.CanInterrupt).ToList();
            foreach (var agent in working)
            {
                agent.Interrupt();
                agent.Items.Add(new BannerItem { Level = "info", Text = "Stopped with /stop." });
            }
            if (working.Count == 0) anchor.Items.Add(new BannerItem { Level = "info", Text = "/stop: no agent is working right now." });
            return true;
        }
        var target = FindTeammate(team, who);
        if (target is null)
        {
            anchor.Items.Add(new BannerItem
            {
                Level = "info",
                Text = $"/stop: no agent called \"{who}\". The team: {string.Join(", ", team.Select(p => p.BridgeTerminalIdentity))}.",
            });
            return true;
        }
        if (!target.CanInterrupt)
        {
            anchor.Items.Add(new BannerItem { Level = "info", Text = $"/stop: {target.BridgeTerminalIdentity} isn't working right now." });
            return true;
        }
        target.Interrupt();
        target.Items.Add(new BannerItem { Level = "info", Text = "Stopped with /stop." });
        return true;
    }

    /// <summary>
    /// /message Volt are you done with the camera? - the words after the name become that agent's very next prompt. A
    /// working agent is stopped first and then runs the message, as a real prompt rather than a steer, and is told to
    /// carry on with its unfinished task afterwards; an idle agent simply receives it. Either way it also hears what its
    /// teammates did since it last heard, like any prompt in the team.
    /// </summary>
    private bool MessageTeammate(ChatViewModel anchor, IReadOnlyList<ChatViewModel> team, string rest)
    {
        if (rest.Length == 0)
        {
            anchor.Items.Add(new BannerItem { Level = "info", Text = "/message: write who and what, e.g. /message Volt are you done with the camera?" });
            return true;
        }
        var (target, message) = SplitRecipient(team, rest);
        if (target is null)
        {
            var first = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd(':', ',');
            anchor.Items.Add(new BannerItem
            {
                Level = "info",
                Text = $"/message: no agent called \"{first}\". The team: {string.Join(", ", team.Select(p => p.BridgeTerminalIdentity))}.",
            });
            return true;
        }
        if (message.Length == 0)
        {
            anchor.Items.Add(new BannerItem { Level = "info", Text = $"/message {target.BridgeTerminalIdentity}: add your message after the name." });
            return true;
        }
        var interrupting = target.Status is "running" or "preparing";
        var note = AgentTeamPolicy.DirectMessageNote(interrupting, AgentTeamPolicy.TeamUpdate(UnseenTeamEntries(target.TeamSeen, team, target)));
        if (!target.SendDirectMessage(message, note))
        {
            anchor.Items.Add(new BannerItem { Level = "info", Text = $"/message: {target.BridgeTerminalIdentity} can't take a message right now ({target.Status})." });
            return true;
        }
        MarkTeamSeen(target, team);
        NoteBridgeActivity(TryGetLiveBridge(target, out var live) && !BridgePanes.Contains(target) ? live : null);
        return true;
    }

    /// <summary>The longest leading run of words (up to three: "agent 2", "Red Fox", "Volt") that names a teammate,
    /// and the message after it, its own line breaks kept.</summary>
    private static (ChatViewModel? Target, string Message) SplitRecipient(IReadOnlyList<ChatViewModel> team, string rest)
    {
        var words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var take = Math.Min(3, words.Length); take >= 1; take--)
        {
            var name = string.Join(" ", words.Take(take)).TrimEnd(':', ',');
            if (FindTeammate(team, name) is not { } target) continue;
            var index = 0;
            for (var word = 0; word < take; word++)
            {
                while (index < rest.Length && char.IsWhiteSpace(rest[index])) index++;
                while (index < rest.Length && !char.IsWhiteSpace(rest[index])) index++;
            }
            return (target, rest[index..].TrimStart().TrimStart(':', ',').Trim());
        }
        return (null, "");
    }

    /// <summary>"Volt", "volt", "2", "#2", "agent 2" or "Claude 2".</summary>
    /// <summary>The names the composer offers while one is typed after "/message " or "/stop ": every agent of
    /// <paramref name="chat"/>'s team whose name starts with what was typed, then those with a later word that does
    /// ("Fox" for Red Fox, "2" for Agent 2), in team order. Never a match inside a word: "N" is not "Agent 1".
    /// Empty outside an agent team.</summary>
    public IReadOnlyList<TeammateChoice> TeammateChoices(ChatViewModel chat, string typed)
    {
        if (!chat.AgentTeamMode && !chat.IsAgentTeamMember) return [];
        var key = typed.TrimStart().TrimStart('@');
        static bool Starts(string text, string key) => text.StartsWith(key, StringComparison.OrdinalIgnoreCase);
        return TeamFor(chat)
            .Select(agent => (Agent: agent, Name: agent.BridgeTerminalIdentity))
            .Select(entry => (entry.Agent, entry.Name, Rank: Starts(entry.Name, key) ? 0
                : entry.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(word => Starts(word, key)) ? 1 : 2))
            .Where(entry => entry.Rank < 2)
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.Agent.AgentTeamNumber)
            .Select(entry => new TeammateChoice { Name = entry.Name, Description = TeammateDescription(entry.Agent) })
            .ToList();
    }

    private static string TeammateDescription(ChatViewModel agent)
    {
        var state = agent.Status switch
        {
            "running" or "preparing" => "working",
            "starting" => "starting",
            "idle" => "free",
            _ => agent.Status,
        };
        var seat = agent.AgentTeamName.Length > 0 && agent.AgentTeamNumber > 0 ? $"Agent {agent.AgentTeamNumber} · " : "";
        return $"{seat}{agent.AgentDisplay} · {state}";
    }

    private static ChatViewModel? FindTeammate(IReadOnlyList<ChatViewModel> team, string who)
    {
        var key = who.Trim().TrimStart('@').Trim();
        var number = key.StartsWith("agent ", StringComparison.OrdinalIgnoreCase) ? key[6..].Trim() : key;
        return team.FirstOrDefault(p => string.Equals(p.AgentTeamName, key, StringComparison.OrdinalIgnoreCase))
               ?? (int.TryParse(number.TrimStart('#'), out var seat) ? team.FirstOrDefault(p => p.AgentTeamNumber == seat) : null)
               ?? team.FirstOrDefault(p => string.Equals(p.BridgeLabel, key, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(p.BridgeTerminalIdentity, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string TeamSpeaker(ChatViewModel pane)
    {
        var number = pane.AgentTeamNumber;
        var seat = number > 0 ? $"agent #{number}" : pane.AgentDisplay;
        return pane.AgentTeamName.Length > 0 ? $"{pane.AgentTeamName} ({seat})" : seat;
    }

    /// <summary>Every name an agent already goes by, in any live Bridge (on screen, parked or on the second display) or
    /// chat. Names are one pool, so agents in different panes or Bridges never share one.</summary>
    private HashSet<string> ClaimedAgentNames(ChatViewModel? except = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in LiveBridges().SelectMany(bridge => bridge.Panes).Concat(Chats))
            if (!ReferenceEquals(agent, except) && agent.AgentTeamName.Length > 0) names.Add(agent.AgentTeamName);
        return names;
    }

    /// <summary>The taken names and a few free ones, for any note that asks <paramref name="agent"/> to name itself.</summary>
    private string NameHint(ChatViewModel agent) => AgentTeamPolicy.NamePoolHint(ClaimedAgentNames(agent));

    private static readonly string[] ReservedAgentNames = ["claude", "codex", "glm", "kimi", "gemini", "agent", "user", "vibecode"];

    /// <summary>bridge_set_name: an agent-team member's own display name. Refusals return applied=false with a reason
    /// rather than an error, so a model picks another name instead of retrying the same one.</summary>
    private JsonObject SetAgentTeamNameFromMcp(ChatViewModel caller, JsonObject args)
    {
        JsonObject Declined(string reason) => new() { ["applied"] = false, ["name"] = caller.AgentTeamName, ["reason"] = reason };
        if (!caller.IsAgentTeamMember && !caller.AgentTeamMode)
            return Declined("Only agents in an agent team (the chat's robot toggle) pick names. Do not call this tool.");
        var name = AgentTeamPolicy.NormalizeName(args["name"]?.GetValue<string>());
        if (name is null)
            return Declined("Use one or two words of letters or digits, at most 24 characters, e.g. Nova.");
        var key = name.ToLowerInvariant();
        if (ReservedAgentNames.Contains(key) || key.StartsWith("agent ", StringComparison.Ordinal) && key[6..].All(char.IsDigit))
            return Declined("Pick a personal name, not a provider, model, role or agent number. For example: Nova.");
        if (string.Equals(name, caller.AgentTeamName, StringComparison.OrdinalIgnoreCase))
            return Declined("That is already your name. Keep it and continue.");
        var claimed = ClaimedAgentNames(caller);
        if (claimed.Contains(name))
        {
            var holder = TryGetLiveBridge(caller, out var bridge)
                ? bridge.Panes.FirstOrDefault(p => !ReferenceEquals(p, caller) && string.Equals(p.AgentTeamName, name, StringComparison.OrdinalIgnoreCase))
                : null;
            var who = holder is null ? "an agent in another Bridge or chat" : $"agent #{BridgeNumberOf(holder)} of this Bridge";
            return Declined($"{name} is taken by {who}. Pick a different name; {AgentTeamPolicy.NamePoolHint(claimed)}.");
        }
        caller.AgentTeamName = name;
        RequestSave();
        return new JsonObject
        {
            ["applied"] = true, ["name"] = name,
            ["guidance"] = "Shown on your messages and in the roster. Keep it for the session; now open your reply with your one-line announcement.",
        };
    }

    /// <summary>Carry the robot switch, team membership and name across a rebuilt chat (account move, restart, reconnect).</summary>
    /// <summary>Session ids of <paramref name="agent"/>'s teammates among <paramref name="earlier"/>, in roster order: the
    /// agents whose transcripts already show any history it copied when it joined.</summary>
    private static List<string> TeammatesBefore(ChatViewModel agent, IEnumerable<ChatViewModel> earlier) => earlier
        .Where(p => p.IsAgentTeamMember && p.AgentTeamAnchorId == agent.AgentTeamAnchorId)
        .Select(p => p.SessionId).OfType<string>().ToList();

    private static void CopyAgentTeamState(ChatViewModel source, ChatViewModel target)
    {
        target.AgentTeamMode = source.AgentTeamMode;
        target.AgentTeamAccountId = source.AgentTeamAccountId;
        target.IsAgentTeamMember = source.IsAgentTeamMember;
        target.AgentTeamAnchorId = source.AgentTeamAnchorId;
        target.AgentTeamName = source.AgentTeamName;
        if (source.IsAgentTeamMember && !source.IsBridgeHost) target.SettleAgentTeamTitle();
    }
}
