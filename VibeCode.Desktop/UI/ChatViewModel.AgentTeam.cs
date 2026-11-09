using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>An account the robot's new agents can join on, as its right-click menu lists it. A null id is Automatic.</summary>
public sealed record TeamAccountOption(string? AccountId, string Label, string Detail, bool IsChosen, bool IsUsable);

/// <summary>A teammate the composer's menu offers after "/message " or "/stop ": Tab or Enter writes <see cref="Name"/>
/// into the message, the way /message and /stop resolve it.</summary>
public sealed class TeammateChoice
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ArgumentHint => null;
    /// <summary>Command rows start with "/"; a name is written as it is.</summary>
    public string Prefix => "";
}

/// <summary>The composer's robot toggle: an agent team, where a free agent takes each prompt and a new agent of this
/// model joins only when every agent is busy.</summary>
public sealed partial class ChatViewModel
{
    internal bool IsWaitingForAgentTeamPrompt { get; private set; }
    private BannerItem? _emptyTeamNotice;

    internal void WaitForAgentTeamPrompt()
    {
        IsWaitingForAgentTeamPrompt = true;
        Status = "idle";
        _emptyTeamNotice = new BannerItem { Level = "info", Text = "No agents are running. Send a message to start a new agent." };
        Items.Add(_emptyTeamNotice);
    }

    private bool StartWaitingTeamPrompt(string text, IReadOnlyList<Attachment>? attachments)
    {
        if (string.IsNullOrWhiteSpace(text) && attachments is not { Count: > 0 }) return false;
        IsWaitingForAgentTeamPrompt = false;
        if (_emptyTeamNotice is not null) Items.Remove(_emptyTeamNotice);
        _emptyTeamNotice = null;
        if (_session is null || _session.HasExited)
        {
            Status = "starting";
            Start();
        }
        return SendAgentTeamPrompt(text, attachments);
    }

    private bool _agentTeamMode;
    /// <summary>
    /// Robot toggle. While on, a prompt from this composer goes to a free agent of the team, and when every agent is busy a
    /// fresh agent of the same provider and model joins the one shared terminal instead of the prompt queuing. Per chat,
    /// like fast mode, and shared by every member of a live team so whichever agent the composer targets shows the same switch.
    /// </summary>
    public bool AgentTeamMode
    {
        get => _agentTeamMode;
        internal set
        {
            if (!Set(ref _agentTeamMode, value)) return;
            Raise(nameof(ShowExtendedQueueControl));
            Raise(nameof(AgentTeamHint));
            Raise(nameof(ComposerPlaceholder));
            Raise(nameof(ShowsStopButton));
            RefreshAgentTeamCommands();
        }
    }

    /// <summary>The team replaces queuing, so the extended-queue control is hidden while the robot toggle is on.</summary>
    public bool ShowExtendedQueueControl => !_agentTeamMode;

    /// <summary>Normal chats and regular Bridge agents. Advanced Bridge roles keep their assignment flow.</summary>
    public bool ShowAgentTeamToggle => CanCompose && !IsBridgeManager && !BridgeCoordinatesOnly && BridgeCoordinatorAgentId is null;

    public string AgentTeamHint => _agentTeamMode
        ? $"Agent team · on\nA free agent takes each prompt. When every agent is busy, a new {AgentDisplay} agent joins this chat " +
          "with the conversation so far. They share the chat, name themselves and coordinate like a Bridge. " +
          "/stop name stops an agent; /message name text (or /msg) interrupts one with a message. Extended queue is off.\n" +
          $"New agents join on {AgentTeamAccountText}. Right-click to change."
        : $"Agent team · off\nTurn on so a prompt sent while {AgentDisplay} is busy brings in a new agent instead of queuing. " +
          "Right-click to choose the account new agents join on.";

    private string? _agentTeamAccountId;
    /// <summary>The login the robot's new agents join on, picked from its right-click menu. Null is automatic: this
    /// chat's own account, or another usable one while it is past the pacing threshold. Shared by every member of a
    /// live team, like the robot switch itself.</summary>
    public string? AgentTeamAccountId
    {
        get => _agentTeamAccountId;
        internal set { if (Set(ref _agentTeamAccountId, value)) Raise(nameof(AgentTeamHint)); }
    }

    /// <summary>"Tom" for a picked login, or "this chat's account" while the choice is automatic.</summary>
    private string AgentTeamAccountText => _agentTeamAccountId is not { } id ? "this chat's account"
        : BridgeAccountChoice.For(Provider).FirstOrDefault(a => string.Equals(a.AccountId, id, StringComparison.OrdinalIgnoreCase))?.Label
          ?? "the account you picked";

    private bool _isAgentTeamMember;
    /// <summary>This pane belongs to a live agent team (a single-terminal Bridge built by the robot toggle).</summary>
    public bool IsAgentTeamMember
    {
        get => _isAgentTeamMember;
        internal set
        {
            if (!Set(ref _isAgentTeamMember, value)) return;
            RefreshAgentTeamCommands();
            if (value) ClearTodoList();   // team agents keep no checklist (KeepsTodoList)
        }
    }

    private string? _agentTeamAnchorId;
    /// <summary>
    /// A classic (multi-pane) Bridge can hold several agent teams: each belongs to the pane whose robot started it, and
    /// this is that pane's agent id, on the pane itself and on every agent it summoned. Null outside such a team, which
    /// includes a team that is a whole Bridge of its own (started from a chat that was not in a Bridge).
    /// </summary>
    public string? AgentTeamAnchorId
    {
        get => _agentTeamAnchorId;
        internal set
        {
            if (!Set(ref _agentTeamAnchorId, value)) return;
            Raise(nameof(IsTeamPaneMember));
            Raise(nameof(BridgePaneShown));
        }
    }

    /// <summary>Summoned into another pane's team: it shows in that pane's one terminal, never as a pane of its own.</summary>
    public bool IsTeamPaneMember => _agentTeamAnchorId is not null && _agentTeamAnchorId != BridgeAgentId;

    private BridgeSharedTerminalViewModel? _teamTerminal;
    /// <summary>On a classic-Bridge pane whose team has grown past one agent: the one terminal the team shares, shown in
    /// place of the pane's own transcript and composer. Null on every other pane.</summary>
    public BridgeSharedTerminalViewModel? TeamTerminal
    {
        get => _teamTerminal;
        internal set { if (Set(ref _teamTerminal, value)) Raise(nameof(HasTeamTerminal)); }
    }

    public bool HasTeamTerminal => _teamTerminal is not null;

    private string _agentTeamName = "";
    /// <summary>The name this agent picked for itself with bridge_set_name. Empty until it does.</summary>
    public string AgentTeamName
    {
        get => _agentTeamName;
        internal set
        {
            if (!Set(ref _agentTeamName, value)) return;
            Raise(nameof(BridgeTerminalIdentity));
            Raise(nameof(BridgeTerminalLabel));
            Raise(nameof(AgentTeamRole));
        }
    }

    /// <summary>The seat, once its name takes the agent's line: "Agent 2 · Codex"; before it has a name, the provider.</summary>
    public string AgentTeamRole => _agentTeamName.Length > 0 && AgentTeamSeat is { } seat ? $"{seat} · {AgentDisplay}" : AgentDisplay;

    internal string? AgentTeamSeat => AgentTeamNumber > 0 ? $"Agent {AgentTeamNumber}" : null;

    private int _agentTeamSeatNumber;
    /// <summary>This agent's number inside a classic-Bridge pane's team, counting that pane's own agent as 1 and the rest by
    /// arrival. Zero outside such a team: a team that is a whole Bridge, and every other pane, go by their Bridge number.</summary>
    internal int AgentTeamSeatNumber
    {
        get => _agentTeamSeatNumber;
        set
        {
            if (!Set(ref _agentTeamSeatNumber, value)) return;
            Raise(nameof(BridgeTerminalIdentity));
            Raise(nameof(BridgeTerminalLabel));
            Raise(nameof(AgentTeamRole));
        }
    }

    /// <summary>The number this agent goes by in its team: its seat in a pane's team, otherwise its Bridge number.</summary>
    internal int AgentTeamNumber => _agentTeamSeatNumber > 0 ? _agentTeamSeatNumber
        : int.TryParse(BridgeLabel.Split(' ').LastOrDefault(), out var number) ? number : 0;

    /// <summary>An unnamed agent was already told, mid-turn, that teammates joined it; it is not steered again for every
    /// later newcomer. Its next hand-off still asks for a name while it has none.</summary>
    internal bool AgentTeamNamePrompted { get; set; }

    /// <summary>Steer a team note into the running turn: the app's note, so no bubble, prompt history or memory record. A
    /// steer already being submitted is waited out briefly. False when the turn can't take one (idle, or a provider without
    /// steering); the caller then sends the note with the next message instead.</summary>
    internal async Task<bool> NotifyAgentTeamNowAsync(string note)
    {
        for (var wait = 0; wait < 20 && _steerSubmitting && HasSteerableTurn; wait++) await Task.Delay(250);
        return await SteerAppNoteAsync(note);
    }

    /// <summary>Send unless a turn is running; in team mode text in the composer always means "send to the team". An
    /// agent team's composer stops the whole terminal, so it offers Stop while ANY of its agents is running.</summary>
    public bool ShowsStopButton => (CanInterrupt || _teamCanInterrupt) && !(_agentTeamMode && !string.IsNullOrWhiteSpace(_draft));

    private bool _teamCanInterrupt;
    /// <summary>Set on a team's first agent (the chat that owns its composer) while any teammate has a running turn.</summary>
    internal bool TeamCanInterrupt
    {
        get => _teamCanInterrupt;
        set { if (Set(ref _teamCanInterrupt, value)) Raise(nameof(ShowsStopButton)); }
    }

    /// <summary>No prompt has reached this chat yet, so its own agent can take the first robot-mode prompt.</summary>
    internal bool IsFreshForAgentTeam =>
        !Items.OfType<UserItem>().Any() && _sendQueue.Count == 0 && _status is not ("running" or "preparing" or "error" or "closed");

    /// <summary>Finished and holding nothing, so a prompt handed to it runs right away.</summary>
    internal bool IsFreeForAgentTeamPrompt => _status == "idle" && !HasPendingDispatch && _pendingPerms.Count == 0
        && (IsWaitingForAgentTeamPrompt || _session is { HasExited: false } || IsIdleAsleep || IsGemini && IsAsleep);

    /// <summary>Not working on anything: free, or still starting with nothing waiting for it (a restored team's agents,
    /// for a few seconds). Such an agent takes the next prompt instead of a newcomer being summoned beside it.</summary>
    internal bool IsAvailableForAgentTeamPrompt => IsFreeForAgentTeamPrompt
        || _status == "starting" && _sendQueue.Count == 0 && !HasPendingDispatch;

    /// <summary>Actually doing something: a turn running or about to, or a newcomer still starting with its prompt
    /// waiting. A restored agent that is merely starting with nothing to do does not count.</summary>
    internal bool IsWorkingForTeam => _status is "running" or "preparing"
        || _status == "starting" && (_sendQueue.Count > 0 || HasPendingDispatch);

    /// <summary>The teammate that takes the next robot-mode prompt, from <paramref name="team"/> listed first agent
    /// first. While nobody is working that is agent 1, the team's own first agent, so a quiet team always starts in the
    /// same place. While someone works it is the free one that finished most recently, so a follow-up lands where that
    /// work is freshest. Null when every agent is busy, which is when a newcomer joins.</summary>
    internal static ChatViewModel? NextFreeTeammate(IEnumerable<ChatViewModel> team)
    {
        var members = team as IReadOnlyList<ChatViewModel> ?? team.ToList();
        if (members.Count > 0 && members[0].IsAvailableForAgentTeamPrompt && !members.Any(agent => agent.IsWorkingForTeam))
            return members[0];
        return members
            .Where(agent => agent.IsAvailableForAgentTeamPrompt)
            .OrderByDescending(agent => agent.WorkEndedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    /// <summary>For each teammate (by agent id), the newest of its transcript rows this agent has been told about. Every
    /// prompt an agent receives carries what its teammates were asked, said and did after that, so the agents share one
    /// context however the prompts were spread between them.</summary>
    internal Dictionary<string, long> TeamSeen { get; } = new(StringComparer.Ordinal);

    /// <summary>This agent began as a copy of a teammate's conversation, so its own history holds the team's. A later
    /// newcomer may be copied from it as well as from the chat the team grew from.</summary>
    internal bool CarriesTeamHistory { get; set; }

    /// <summary>The first-agent note went out, so it is not repeated with every prompt while the agent is still unnamed.</summary>
    internal bool AgentTeamIntroduced { get; set; }

    /// <summary>A newcomer that starts as a copy of a teammate's conversation: its provider gets that whole history as
    /// context, but this pane must not draw it again - the shared terminal already shows every row under its owner.</summary>
    internal void SkipTranscriptReplay() => _transcriptLoaded = true;

    /// <summary>Session ids of the teammates listed before this agent, set when a team is restored or forked. An agent
    /// that joined as a copy of one of them starts its transcript with that copy, which the terminal already shows
    /// under its owner, so replay begins where this agent's own work does.</summary>
    internal IReadOnlyList<string>? TeamHistoryShownBy { get; set; }

    /// <summary>Drop the opening run of messages that teammates' transcripts already hold. Only a leading run: an agent's
    /// own work follows its copied history, and nothing after it can be a copy.</summary>
    private static List<TranscriptMessage> WithoutInheritedHistory(List<TranscriptMessage> transcript, IReadOnlyList<string>? teammates)
    {
        if (teammates is not { Count: > 0 } || transcript.Count == 0 || transcript[0].Uuid is null) return transcript;
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sessionId in teammates) shown.UnionWith(SessionCatalog.LoadMessageIds(sessionId));
        var inherited = 0;
        while (inherited < transcript.Count && transcript[inherited].Uuid is { } uuid && shown.Contains(uuid)) inherited++;
        return inherited == 0 ? transcript : transcript.GetRange(inherited, transcript.Count - inherited);
    }

    /// <summary>Where the history being replayed began in <see cref="ChatViewModel.Items"/>.</summary>
    private int _historyReplayStart;

    /// <summary>
    /// A replayed prompt of a restored or forked teammate that carries a join note saying it starts as a copy: every row
    /// this replay drew before it is copied history, which the terminal shows under the teammates who wrote it. Codex,

    /// copy is cut here. A copy of a copy carries the earlier agents' notes too; cutting at each one leaves this agent's
    /// own work, which starts at the last. Only a note in the injected prelude counts, never the user's own words.
    /// </summary>
    private void DropInheritedHistory(string wire, string shown)
    {
        if (TeamHistoryShownBy is null) return;
        var marker = wire.IndexOf(AgentTeamPolicy.CopiedConversationMarker, StringComparison.Ordinal);
        var own = wire.LastIndexOf(shown, StringComparison.Ordinal);
        if (marker < 0 || marker >= (own >= 0 ? own : wire.Length - shown.Length)) return;
        for (var i = Items.Count - 1; i >= Math.Min(_historyReplayStart, Items.Count); i--)
            // Prompts can be accepted while history is loading. They are not part of the inherited copy.
            if (Items[i] is not (BannerItem or DividerItem or PendingItem or QueuedItem or UserItem { IsLocalSubmission: true }))
                Items.RemoveAt(i);
    }

    /// <summary>Team composers get /stop ("/stop Volt" stops one agent, "/stop" every agent that is working) and
    /// /message or /msg ("/message Volt …" puts a prompt in front of that agent, stopping it first if it is working).</summary>
    private void RefreshAgentTeamCommands()
    {
        var offered = _agentTeamMode || _isAgentTeamMember;
        foreach (var (name, description, hint) in new[]
                 {
                     ("stop", "Stop an agent, or every working agent", "name | all"),
                     ("message", "Interrupt an agent with a message; it carries on after", "name message"),
                     ("msg", "Alias for /message: interrupt an agent with a message", "name message"),
                 })
        {
            var existing = Commands.FirstOrDefault(command => command.Name == name);
            if (offered && existing is null) Commands.Add(new CommandChoice { Name = name, Description = description, ArgumentHint = hint });
            else if (!offered && existing is not null) Commands.Remove(existing);
        }
    }

    /// <summary>
    /// /message: the user's words become this agent's very next prompt, with <paramref name="note"/> riding on the wire
    /// with exactly that prompt. A working agent is stopped first - its turn ends and the message runs straight after,
    /// as a real prompt rather than a steer into the running turn - and an idle one simply receives it.
    /// </summary>
    internal bool SendDirectMessage(string text, string note)
    {
        if (_status == "closed" || _session is null || _session.HasExited) return false;
        if (_status is "running" or "preparing")
        {
            QueuePrompt(text, null, extended: false, wireNote: note);
            MessageSent?.Invoke();
            // "Send prompt now": interrupt the turn so this queued prompt is the next thing the agent sees. If a stop is
            // already on its way (or the turn is still preparing), the prompt simply goes the moment that turn ends.
            if (!SendQueuedNow(_sendQueue.Peek())) Interrupt();
            return true;
        }
        Prelude = string.IsNullOrEmpty(Prelude) ? note : Prelude + "\n" + note;
        return Send(text, null);
    }

    /// <summary>A summoned agent's chat has no sidebar row of its own. Its name comes from bridge_set_name, so it is
    /// not also asked for a chat title (the host keeps the chat's real title).</summary>
    internal void SettleAgentTeamTitle() => _hasGeneratedTitle = true;

    /// <summary>Deliver a robot-mode prompt to this agent even while its session is still starting. Behind a pending
    /// compaction of its copied conversation it always queues, so the compaction goes first.</summary>
    internal bool SendAgentTeamPrompt(string text, IReadOnlyList<Attachment>? attachments)
    {
        if (Status is "error" or "closed") return false;
        if (_session is null && Status == "starting" || _compactBeforeFirstPrompt is not null)
        {
            QueuePrompt(text, attachments is { Count: > 0 } ? attachments.ToList() : null, extended: false);
            MessageSent?.Invoke();
            if (_status == "idle") Post(FlushQueue);
            return true;
        }
        return Send(text, attachments);
    }
}
