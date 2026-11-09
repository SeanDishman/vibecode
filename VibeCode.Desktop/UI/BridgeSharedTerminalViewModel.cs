using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>One viewport and composer for a live roster. Provider transcripts retain their original owners.</summary>
public sealed partial class BridgeSharedTerminalViewModel : Observable, IDisposable
{
    private readonly Action<ChatViewModel> _select;
    private readonly Action<ChatViewModel, string> _setReviewLevel;
    private readonly Dictionary<ChatViewModel, Dictionary<ItemVm, BridgeTranscriptEntry>> _sources = new();
    private ChatViewModel? _target;
    private ChatViewModel? _reviewing;
    private ChatViewModel? _teamAnchor;
    private bool _reconcilingRoster;
    private bool _deferThinking;
    private (double Read, double Write, double Cost, bool Estimated, int AgentCount) _teamUsage;
    // What the total cannot hold yet: agents mid-turn with nothing reported (and their providers), or a provider that
    // reports only part of its usage. Either makes the total a minimum.
    private (int Agents, string Providers, bool Incomplete) _unreported = (0, "", false);

    // Coalescing: with more than CoalesceAbove agents working at once, every agent logging its own actions would add a
    // row apiece and make the terminal thrash (and scrolling and selecting text fight the churn). New rows are held here,
    // tracked and updating, then revealed in one batch each CoalesceWindow. Bulk rebuilds (reconcile, history replay,
    // thinking toggle) are never held, nor is anything you type, and a single agent under review is read live.
    // It counts agents WORKING, not agents in the roster: an agent team keeps every agent its robot ever summoned, so a
    // roster count left a team that had grown past four batching for good - one agent replying, in 30-second jumps.
    /// <summary>More than this many agents working at once in one terminal turns the coalescing window on.</summary>
    public const int CoalesceAbove = 4;
    /// <summary>How long a big team's new rows wait before the terminal reveals them together. Field, not const, so a
    /// test can shrink it.</summary>
    public static TimeSpan CoalesceWindow = TimeSpan.FromSeconds(30);
    private readonly List<BridgeTranscriptEntry> _pending = new();
    private DispatcherTimer? _flushTimer;
    private bool _bulkAdding;
    private bool Coalescing => _reviewing is null && Agents.Count(agent => agent.IsWorkingForTeam) > CoalesceAbove;

    public BridgeSharedTerminalViewModel(Action<ChatViewModel> select, Action<ChatViewModel, string> setReviewLevel)
    {
        _select = select;
        _setReviewLevel = setReviewLevel;
        Activity = new ListCollectionView(Entries) { Filter = item => item is BridgeTranscriptEntry { HasContent: true, Pending: false } entry &&
            (!entry.Owner.IsLoadingHistory || entry.Item is UserItem { IsLocalSubmission: true } or QueuedItem) &&
            (_reviewing is null || ReferenceEquals(entry.Owner, _reviewing)) };
        Activity.CollectionChanged += OnActivityChanged;
        // The footer's shortened forms change exactly when the full ones do.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TeamRoutingText)) Raise(nameof(TeamRoutingShortText));
            else if (e.PropertyName == nameof(InlineUsageText)) Raise(nameof(InlineTokenText));
        };
    }

    public ObservableCollection<ChatViewModel> Agents { get; } = new();
    public ObservableCollection<BridgeTranscriptEntry> Entries { get; } = new();
    public ICollectionView Activity { get; }
    /// <summary>How many new rows a big team's terminal is holding back for the next batch. Zero when not coalescing.</summary>
    public int PendingActionCount => _pending.Count;
    /// <summary>The terminal is holding rows for the next batch, so a footer can say the view updates every so often.</summary>
    public bool IsBatching => _pending.Count > 0;
    public IReadOnlyList<BridgeReviewChoice> ReviewLevels => BridgeReviewPolicy.Choices;
    public void SetReviewLevel(ChatViewModel agent, string level)
    {
        if (Agents.Contains(agent)) _setReviewLevel(agent, level);
    }
    /// <summary>An agent team (the composer's robot toggle) is ONE terminal: every agent's messages and actions log here
    /// in order, nobody is selected or reviewed separately, and the composer always talks to the team itself. A classic
    /// Bridge can hold several teams, so every agent here must belong to the same one.</summary>
    public bool IsAgentTeam => Agents.Count > 0 && Agents.All(agent => agent.IsAgentTeamMember)
                               && Agents.Select(agent => agent.AgentTeamAnchorId).Distinct().Count() == 1;

    /// <summary>The header presents an agent team only while two or more of its agents are working at once. With one
    /// (or none) working it is simply the chat: its own name, no team label or robot.</summary>
    public bool ShowsTeamHeader => IsAgentTeam && Agents.Count(agent => agent.IsWorkingForTeam) >= 2;

    /// <summary>"Agent team" while several agents work at once; otherwise the chat's own name.</summary>
    public string TeamHeaderTitle => ShowsTeamHeader ? "Agent team"
        : TeamAnchor is { } anchor ? (string.IsNullOrWhiteSpace(anchor.Title) ? anchor.AgentDisplay : anchor.Title) : "";

    /// <summary>The composer's routing line for a team: where the next prompt goes. With the robot on that is agent 1
    /// while nobody works, otherwise the free agent that finished most recently, or a newcomer when every agent is busy;
    /// with it off, the team's own chat.
    /// A team is capped on its own: this terminal's agents are the whole team, whether that team is the entire Bridge or
    /// one pane's team inside a classic Bridge (whose other panes don't count against it).</summary>
    public string TeamRoutingText => Target is not { } target ? ""
        : !target.AgentTeamMode ? $"Robot off · {target.BridgeTerminalIdentity} replies"
        : ChatViewModel.NextFreeTeammate(TeamInOrder) is { } free ? $"Next prompt → {free.BridgeTerminalIdentity}"
        : BridgeAgentPolicy.CanAdd(Agents.Count, AppSettings.Current.BridgeAgentLimit) ? "All busy · next prompt brings a new agent"
        : "All agents busy";

    /// <summary>The routing line for a footer with no room for the whole sentence (see <see cref="StatusWrapPanel"/>):
    /// the composer says less rather than wrapping onto a second line.</summary>
    public string TeamRoutingShortText => Target is not { } target ? ""
        : !target.AgentTeamMode ? "Robot off"
        : ChatViewModel.NextFreeTeammate(TeamInOrder) is { } free ? $"Next → {free.BridgeTerminalIdentity}"
        : "All busy";

    public ChatViewModel? Target
    {
        get => _target;
        set
        {
            if (value is not null && !Agents.Contains(value)) return;
            // A team has no recipient to choose: its composer stays on the team's first agent (the chat itself).
            if (value is not null && IsAgentTeam && !ReferenceEquals(value, TeamAnchor)) value = TeamAnchor;
            if (!Set(ref _target, value)) return;
            Raise(nameof(TargetLabel));
            Raise(nameof(TeamRoutingText));
            // Review is an explicit inspection action. Choosing a prompt recipient returns to team activity;
            // background roster reconciliation must preserve the conversation currently being inspected.
            if (_reviewing is not null && !_reconcilingRoster) ShowAll();
            if (value is not null && !_reconcilingRoster) _select(value);
        }
    }
    /// <summary>The roster's first agent: the chat the team grew from, which its composer always targets.</summary>
    private ChatViewModel? TeamAnchor => _teamAnchor is not null && Agents.Contains(_teamAnchor) ? _teamAnchor : Agents.FirstOrDefault();
    /// <summary>The team with its first agent first, the order <see cref="ChatViewModel.NextFreeTeammate"/> reads.</summary>
    private List<ChatViewModel> TeamInOrder => Agents.OrderBy(agent => ReferenceEquals(agent, TeamAnchor) ? 0 : 1).ToList();
    public string TargetLabel => Target is null ? "Choose an agent" : $"Send to {Target.BridgeTerminalIdentity}";
    public bool IsReviewing => _reviewing is not null;
    public string ActivityTitle => _reviewing is null ? "All activity" : $"{_reviewing.BridgeTerminalIdentity} conversation";
    public string ActivityNote => _reviewing is null ? "Every agent, one terminal" : "Reviewing this agent. Follow up below.";
    public bool HasWorkingAgents => Agents.Any(agent => agent.ShowWorkingText);
    public bool HasUsage => _teamUsage.Read + _teamUsage.Write > 0 || _teamUsage.Cost > 0;
    public string UsageText => HasUsage ? $"Total · {InlineUsageText}" : "";
    public string InlineUsageText => HasUsage
        ? InlineTokenText + (_teamUsage.Cost > 0 ? $" · {(_teamUsage.Estimated ? "~" : "")}${_teamUsage.Cost:0.00##}" : "")
        : "";
    /// <summary><see cref="InlineUsageText"/> without the cost, for a footer too narrow even for the short routing line.</summary>
    public string InlineTokenText => HasUsage
        ? (IsMinimum ? "≥ " : "") + $"{ChatViewModel.FmtTokens(_teamUsage.Read)} read · {ChatViewModel.FmtTokens(_teamUsage.Write)} write"
        : "";

    /// while a provider reports only part of its usage.</summary>
    private bool IsMinimum => _unreported.Agents > 0 || _unreported.Incomplete;
    public string UsageToolTip => $"Combined usage for all {_teamUsage.AgentCount} workers and orchestrators, including active turns.\n"
        + (PlanningUsage.HasUsage ? "Includes the central planner, even after planning finishes.\n" : "")
        + $"Read: {_teamUsage.Read:N0} tokens (input and cache). Write: {_teamUsage.Write:N0} tokens."
        + (_teamUsage.Estimated ? "\n~ marks estimated cost." : "")
        + (_unreported.Agents switch
        {
            0 => "",
            1 => $"\n≥ marks a minimum: 1 working agent ({_unreported.Providers}) has not reported its current turn yet. Its tokens are added when the turn ends.",
            var agents => $"\n≥ marks a minimum: {agents} working agents ({_unreported.Providers}) have not reported their current turns yet. Their tokens are added as each turn ends.",
        })
        + (_unreported.Incomplete && _unreported.Agents == 0 ? "\n≥ marks a minimum: a provider reported only part of its usage." : "");

    private void RefreshUsage()
    {
        Raise(nameof(HasWorkingAgents));
        var planning = PlanningUsage;
        var costs = Agents.Select(agent => agent.DisplayedCost).ToArray();
        var usage = (Read: Agents.Sum(agent => agent.DisplayedInputTokens) + planning.Read,
            Write: Agents.Sum(agent => agent.DisplayedOutputTokens) + planning.Write,
            Cost: costs.Sum(cost => cost.Amount) + planning.Cost,
            Estimated: costs.Any(cost => cost.Estimated) || planning.Estimated, AgentCount: Agents.Count);
        var pending = Agents.Where(agent => agent.TurnUsagePending).ToList();
        var unreported = (Agents: pending.Count, Providers: string.Join(", ", pending.Select(agent => agent.AgentDisplay).Distinct()),
            Incomplete: Agents.Any(agent => agent.TokenUsageIncomplete));
        if (usage == _teamUsage && unreported == _unreported) return;
        (_teamUsage, _unreported) = (usage, unreported);
        Raise(nameof(UsageText));
        Raise(nameof(InlineUsageText));
        Raise(nameof(HasUsage));
        Raise(nameof(UsageToolTip));
    }

    private BridgeUsageSummary PlanningUsage => Agents.FirstOrDefault()?.BridgeWork?.PlanningUsage ?? new();

    public void Review(ChatViewModel agent)
    {
        // A team is read as one log; there is no single agent's conversation to step into.
        if (!Agents.Contains(agent) || IsAgentTeam) return;
        Target = agent;
        _reviewing = agent;
        // Reading one agent is live, not coalesced: reveal anything held so the reviewed rows aren't stuck hidden.
        FlushPending();
        RefreshReview();
    }

    public void ShowAll()
    {
        _reviewing = null;
        RefreshReview();
    }

    private void RefreshReview()
    {
        Activity.Refresh();
        Raise(nameof(IsReviewing));
        Raise(nameof(ActivityTitle));
        Raise(nameof(ActivityNote));
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var view = (ListCollectionView)Activity;
        var first = 0;
        var last = view.Count - 1;
        // Only the changed rows and their neighbours can change group boundaries. A filter refresh
        // recalculates the whole visible view, so hidden messages never split an agent's group.
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
        {
            var index = e.NewStartingIndex >= 0 ? e.NewStartingIndex : e.OldStartingIndex;
            first = Math.Max(0, index - 1);
            last = Math.Min(last, index + (e.NewItems?.Count ?? 0));
        }
        for (var i = first; i <= last; i++)
        {
            var entry = (BridgeTranscriptEntry)view.GetItemAt(i);
            entry.StartsAgentGroup = i == 0 || !ReferenceEquals(entry.Owner, ((BridgeTranscriptEntry)view.GetItemAt(i - 1)).Owner);
            entry.EndsAgentGroup = i == view.Count - 1 || !ReferenceEquals(entry.Owner, ((BridgeTranscriptEntry)view.GetItemAt(i + 1)).Owner);
        }
    }

    internal void Reconcile(IReadOnlyList<ChatViewModel> roster)
    {
        // Restoring the view is not a user selection: _select switches layouts and saves the bridge.
        // Guard collection changes too, since WPF's two-way selection bindings can echo them into Target.
        // A reconcile is a bulk rebuild, so its existing rows are never held back; only live activity after it is.
        var wasReconciling = _reconcilingRoster;
        var wasBulk = _bulkAdding;
        _reconcilingRoster = true;
        _bulkAdding = true;
        try { ReconcileRoster(roster); }
        finally { _reconcilingRoster = wasReconciling; _bulkAdding = wasBulk; }
        // A team that just dropped back below the threshold stops coalescing: show whatever was still held.
        if (!Coalescing) FlushPending();
    }

    private void ReconcileRoster(IReadOnlyList<ChatViewModel> roster)
    {
        var changed = _sources.Keys.Any(p => !roster.Contains(p)) || roster.Any(p => !_sources.ContainsKey(p));
        foreach (var departed in _sources.Keys.Where(p => !roster.Contains(p)).ToArray())
        {
            departed.Items.CollectionChanged -= OnTranscriptChanged;
            departed.PropertyChanged -= OnAgentChanged;
            foreach (var entry in _sources[departed].Values) RemoveEntry(entry);
            _sources.Remove(departed);
        }
        _teamAnchor = roster.FirstOrDefault();
        SetThinkingDeferred(DefersThinking(roster));
        foreach (var agent in roster.Where(p => !_sources.ContainsKey(p)))
        {
            _sources[agent] = new();
            agent.Items.CollectionChanged += OnTranscriptChanged;
            agent.PropertyChanged += OnAgentChanged;
            foreach (var item in agent.Items) AddEntry(agent, item);
        }
        // Coordinators and their own workers stay together in the roster, independent of the activity filter.
        var ordered = roster.Where(p => p.IsBridgeManager).SelectMany(manager =>
            new[] { manager }.Concat(roster.Where(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId)))
            .Concat(roster.Where(p => !p.IsBridgeManager && p.BridgeCoordinatorAgentId is null)).Distinct().ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var existing = Agents.IndexOf(ordered[i]);
            if (existing < 0) Agents.Insert(i, ordered[i]);
            else if (existing != i) Agents.Move(existing, i);
        }
        while (Agents.Count > ordered.Length) Agents.RemoveAt(Agents.Count - 1);
        RefreshProgress();
        if (_reviewing is not null && (!roster.Contains(_reviewing) || IsAgentTeam)) ShowAll();
        Target = IsAgentTeam ? TeamAnchor : roster.FirstOrDefault(p => p.BridgeTerminalSelected) ?? roster.FirstOrDefault();
        if (changed)
        {
            Raise(nameof(Entries));
        }
        Raise(nameof(IsAgentTeam));
        Raise(nameof(TeamRoutingText));
        Raise(nameof(ShowsTeamHeader));
        Raise(nameof(TeamHeaderTitle));
        RefreshUsage();
    }

    /// <summary>An orchestrated single terminal omits thought process rows: reasoning stays in each agent's own transcript
    /// rather than adding thought-only blocks or rows between tool calls. An agent team decides row by row instead
    /// (<see cref="TeamIsBusy"/>).</summary>
    private static bool DefersThinking(IEnumerable<ChatViewModel> roster) =>
        roster.Any(p => p.IsBridgeManager && p.BridgeSingleTerminal);

    /// <summary>An agent team with two or more agents at work, whose thoughts would wedge between everyone's actions, so
    /// a thought arriving now stays out of the terminal. With one agent at work the team is simply that chat, and its
    /// thought process shows like any chat's. Decided as each thought arrives, never retroactively, so a second agent
    /// starting never pulls rows out from under the reader.</summary>
    private bool TeamIsBusy() =>
        _sources.Count > 0 && _sources.Keys.All(agent => agent.IsAgentTeamMember) && _sources.Keys.Count(agent => agent.IsWorkingForTeam) >= 2;

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var owner = _sources.Keys.FirstOrDefault(p => ReferenceEquals(p.Items, sender));
        if (owner is null) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // A whole-transcript rebuild (history replay) is bulk: its rows show at once, not trickled over 30s.
            var wasBulk = _bulkAdding;
            _bulkAdding = true;
            try
            {
                foreach (var entry in _sources[owner].Values) RemoveEntry(entry);
                _sources[owner].Clear();
                foreach (var item in owner.Items) AddEntry(owner, item);
            }
            finally { _bulkAdding = wasBulk; }
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Move) return; // Pinned tails already have their shared order.
        if (e.OldItems is not null)
            foreach (ItemVm item in e.OldItems)
            {
                if (_sources[owner].Remove(item, out var entry)) RemoveEntry(entry);
            }
        if (e.NewItems is not null)
            foreach (ItemVm item in e.NewItems) AddEntry(owner, item);
    }

    private void AddEntry(ChatViewModel owner, ItemVm item)
    {
        if (item is ThinkingItem && (_deferThinking || TeamIsBusy())) return;
        if (_sources[owner].ContainsKey(item)) return;
        var entry = new BridgeTranscriptEntry(owner, item);
        _sources[owner].Add(item, entry);
        if (item is TextItem or ThinkingItem or QueuedItem) item.PropertyChanged += OnItemChanged;
        // A held row stays out of the visible collection until the next flush, which then inserts it. Flushing by
        // insertion (not a view refresh) means each reveal is one incremental add, so scroll position and any text
        // selection survive it - unlike a full refresh, which resets both. What you type is never held: it is yours,
        // and seeing it wait half a minute reads as the app ignoring you.
        if (Coalescing && !_bulkAdding && item is not (UserItem { IsUserPrompt: true } or QueuedItem))
        {
            entry.Pending = true;
            _pending.Add(entry);
            Raise(nameof(PendingActionCount));
            Raise(nameof(IsBatching));
            ScheduleFlush();
            return;
        }
        InsertIntoEntries(entry);
    }

    private void InsertIntoEntries(BridgeTranscriptEntry entry)
    {
        var index = Entries.Count;
        var rank = TranscriptItems.TailRank(entry.Item);
        while (index > 0)
        {
            var previous = Entries[index - 1].Item;
            var previousRank = TranscriptItems.TailRank(previous);
            if (previousRank < rank || previousRank == rank && ComesBefore(previous, entry.Item)) break;
            index--;
        }
        Entries.Insert(index, entry);
    }

    /// <summary>Start the one batch timer if it is not already counting down. It flushes once, then stops; the next
    /// held row starts it again, so a steady stream reveals a batch about every <see cref="CoalesceWindow"/>.</summary>
    private void ScheduleFlush()
    {
        if (_flushTimer is null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CoalesceWindow };
            _flushTimer.Tick += (_, _) => FlushPending();
        }
        if (!_flushTimer.IsEnabled) _flushTimer.Start();
    }

    /// <summary>Reveal every held row at once by inserting it into the visible collection in its ordered slot. The rows
    /// arrived newest-last and are inserted in that order, so each lands at or near the end - an incremental add that
    /// leaves scroll and selection where they were, rather than a view refresh that resets them.</summary>
    private void FlushPending()
    {
        _flushTimer?.Stop();
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();
        foreach (var entry in batch)
        {
            entry.Pending = false;
            InsertIntoEntries(entry);
        }
        Raise(nameof(PendingActionCount));
        Raise(nameof(IsBatching));
    }

    /// <summary>Rows loaded from saved history keep the order they were written in, whichever agent finished loading
    /// first. Everything else keeps arrival order.</summary>
    private static bool ComesBefore(ItemVm earlier, ItemVm later) =>
        earlier.HistoryTime is { } first && later.HistoryTime is { } second
            ? first <= second
            : earlier.DisplaySequence <= later.DisplaySequence;

    private void SetThinkingDeferred(bool defer)
    {
        if (_deferThinking == defer) return;
        _deferThinking = defer;
        // Toggling reasoning rows for the whole roster is a bulk reshuffle: don't trickle the re-added rows.
        var wasBulk = _bulkAdding;
        _bulkAdding = true;
        try
        {
            foreach (var (owner, entries) in _sources)
            {
                if (defer)
                {
                    foreach (var thought in entries.Keys.OfType<ThinkingItem>().ToArray())
                    {
                        var entry = entries[thought];
                        entries.Remove(thought);
                        RemoveEntry(entry);
                    }
                }
                else
                    foreach (var item in owner.Items.OfType<ThinkingItem>())
                        AddEntry(owner, item);
            }
        }
        finally { _bulkAdding = wasBulk; }
    }

    /// <summary>Show or hide one row whose content came or went: out and straight back into the same slot, so the view
    /// adds or drops that row alone - an incremental change, like the batch flush, that leaves scroll and selection be.</summary>
    private void Reslot(BridgeTranscriptEntry entry)
    {
        var index = Entries.IndexOf(entry);
        if (index < 0) return;
        Entries.RemoveAt(index);
        Entries.Insert(index, entry);
    }

    private void RemoveEntry(BridgeTranscriptEntry entry)
    {
        entry.Item.PropertyChanged -= OnItemChanged;
        if (_pending.Remove(entry))
        {
            Raise(nameof(PendingActionCount));
            Raise(nameof(IsBatching));
        }
        Entries.Remove(entry);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ItemVm item || e.PropertyName is not (nameof(TextItem.Text) or nameof(QueuedItem.Attachments) or null or "")) return;
        foreach (var source in _sources.Values)
            if (source.TryGetValue(item, out var entry))
            {
                // Only when content appears or disappears, rather than on every streaming delta - and never for a row
                // still held for the next batch, which isn't in the view yet (the flush shows it). Re-slotting the one
                // row, not refreshing the whole view: a refresh re-laid every row and moved whatever the reader was
                // looking at, on every reply's first words in a busy team.
                if (entry.RefreshVisibility() && !entry.Owner.IsLoadingHistory && !entry.Pending) Reslot(entry);
                break;
            }
    }

    private void OnAgentChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Publish a restored conversation once. Other agents remain visible and can keep streaming
        // while this owner's history is replayed off the visual tree.
        if (e.PropertyName == nameof(ChatViewModel.IsLoadingHistory)) Activity.Refresh();
        if (e.PropertyName is nameof(ChatViewModel.ShowWorkingText) or null or "") Raise(nameof(HasWorkingAgents));
        if (e.PropertyName == nameof(ChatViewModel.BridgeProgressRevision)) { RefreshProgress(); RefreshUsage(); }
        if (e.PropertyName is nameof(ChatViewModel.IsBridgeManager) or nameof(ChatViewModel.BridgeSingleTerminal)
            or nameof(ChatViewModel.IsAgentTeamMember) or null or "")
            SetThinkingDeferred(DefersThinking(_sources.Keys));
        if (e.PropertyName is nameof(ChatViewModel.IsAgentTeamMember) or null or "")
        {
            Raise(nameof(IsAgentTeam));
            Raise(nameof(ShowsTeamHeader));
            Raise(nameof(TeamHeaderTitle));
            if (IsAgentTeam && !ReferenceEquals(Target, TeamAnchor))
            {
                // Like a roster reconcile, this is not a user selection: do not switch layouts or save from here.
                var wasReconciling = _reconcilingRoster;
                _reconcilingRoster = true;
                try { Target = TeamAnchor; }
                finally { _reconcilingRoster = wasReconciling; }
            }
            Raise(nameof(TeamRoutingText));
        }
        if (ReferenceEquals(sender, Target) && e.PropertyName is nameof(ChatViewModel.AgentTeamMode)) Raise(nameof(TeamRoutingText));
        // Who takes the next prompt, and whether the header shows a team at all, follow every agent's own state.
        if (IsAgentTeam && e.PropertyName is nameof(ChatViewModel.Status) or nameof(ChatViewModel.HasQueued)
            or nameof(ChatViewModel.AgentTeamName) or nameof(ChatViewModel.BridgeLabel) or null or "")
        {
            Raise(nameof(TeamRoutingText));
            Raise(nameof(ShowsTeamHeader));
            Raise(nameof(TeamHeaderTitle));
        }
        if (ReferenceEquals(sender, TeamAnchor) && e.PropertyName == nameof(ChatViewModel.Title)) Raise(nameof(TeamHeaderTitle));
        // Status too: a turn starting or ending moves the agent in or out of the not-yet-reported count.
        if (e.PropertyName is nameof(ChatViewModel.TokensText) or nameof(ChatViewModel.CostText) or nameof(ChatViewModel.Status) or null or "")
            RefreshUsage();
        // Down to four or fewer working: show what was held now rather than at the end of the window.
        if (_pending.Count > 0 && e.PropertyName is nameof(ChatViewModel.Status) or nameof(ChatViewModel.HasQueued) or null or ""
            && !Coalescing) FlushPending();
        if (ReferenceEquals(sender, Target) && e.PropertyName is nameof(ChatViewModel.BridgeLabel)
            or nameof(ChatViewModel.BridgeTaskName)) Raise(nameof(TargetLabel));
        if (ReferenceEquals(sender, _reviewing) && e.PropertyName == nameof(ChatViewModel.BridgeLabel))
            Raise(nameof(ActivityTitle));
    }

    public void Dispose()
    {
        Activity.CollectionChanged -= OnActivityChanged;
        _flushTimer?.Stop();
        _pending.Clear();
        foreach (var source in _sources.Keys)
        {
            source.Items.CollectionChanged -= OnTranscriptChanged;
            source.PropertyChanged -= OnAgentChanged;
        }
        foreach (var entry in Entries) entry.Item.PropertyChanged -= OnItemChanged;
        _sources.Clear();
        Agents.Clear();
        Entries.Clear();
        RefreshUsage();
    }
}

public sealed class BridgeTranscriptEntry : ItemVm
{
    public BridgeTranscriptEntry(ChatViewModel owner, ItemVm item)
    {
        Owner = owner;
        Item = item;
        _hasContent = ContainsContent(item);
    }

    public ChatViewModel Owner { get; }
    public ItemVm Item { get; }
    private bool _hasContent;
    public bool HasContent => _hasContent;
    internal bool RefreshVisibility() => Set(ref _hasContent, ContainsContent(Item), nameof(HasContent));

    private bool _pending;
    /// <summary>Held back by the terminal's coalescing window (big teams): the row is tracked and updating in place but
    /// kept out of the visible view until the next flush, so a crowd of agents doesn't add a row per action.</summary>
    public bool Pending
    {
        get => _pending;
        internal set => Set(ref _pending, value);
    }

    private bool _startsAgentGroup = true;
    public bool StartsAgentGroup
    {
        get => _startsAgentGroup;
        internal set => Set(ref _startsAgentGroup, value);
    }
    private bool _endsAgentGroup = true;
    public bool EndsAgentGroup
    {
        get => _endsAgentGroup;
        internal set => Set(ref _endsAgentGroup, value);
    }

    // Routing notices and per-agent idle spinners belong to individual terminals. The shared roster
    // already carries that status; reserving transcript rows for them creates empty, labelled gaps.
    private static bool ContainsContent(ItemVm item) => item switch
    {
        DividerItem or PendingItem => false,
        TextItem text => !string.IsNullOrWhiteSpace(text.Text),
        ThinkingItem thinking => !string.IsNullOrWhiteSpace(thinking.Text),
        UserItem user => user.AgentKind != "Coordination update" && (user.HasText || user.HasAttachments),
        QueuedItem queued => !IsCoordinationNotice(queued.Text) && (queued.HasText || queued.HasAttachments),
        _ => true
    };

    private static bool IsCoordinationNotice(string text) =>
        AgentMessagePresentation.TryParse(text, out var card) && card.Kind == "Coordination update";

    public bool IsShell => Item is CompactToolGroupItem { Kind: CompactToolGroupItem.BashKind }
        or ToolItem { Name: "Bash" };
}
