using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
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
    private bool _reconcilingRoster;
    private bool _suppressThinking;
    private (double Read, double Write, double Cost, bool Estimated, int AgentCount) _teamUsage;

    public BridgeSharedTerminalViewModel(Action<ChatViewModel> select, Action<ChatViewModel, string> setReviewLevel)
    {
        _select = select;
        _setReviewLevel = setReviewLevel;
        Activity = new ListCollectionView(Entries) { Filter = item => item is BridgeTranscriptEntry { HasContent: true } entry &&
            (_reviewing is null || ReferenceEquals(entry.Owner, _reviewing)) };
        Activity.CollectionChanged += OnActivityChanged;
    }

    public ObservableCollection<ChatViewModel> Agents { get; } = new();
    public ObservableCollection<BridgeTranscriptEntry> Entries { get; } = new();
    public ICollectionView Activity { get; }
    public IReadOnlyList<BridgeReviewChoice> ReviewLevels => BridgeReviewPolicy.Choices;
    public void SetReviewLevel(ChatViewModel agent, string level)
    {
        if (Agents.Contains(agent)) _setReviewLevel(agent, level);
    }
    public ChatViewModel? Target
    {
        get => _target;
        set
        {
            if (value is not null && !Agents.Contains(value)) return;
            if (!Set(ref _target, value)) return;
            Raise(nameof(TargetLabel));
            if (_reviewing is not null && !ReferenceEquals(_reviewing, value))
            {
                _reviewing = value;
                RefreshReview();
            }
            if (value is not null && !_reconcilingRoster) _select(value);
        }
    }
    public string TargetLabel => Target is null ? "Choose an agent" : $"Send to {Target.BridgeTerminalIdentity}";
    public bool IsReviewing => _reviewing is not null;
    public string ActivityTitle => _reviewing is null ? "All activity" : $"{_reviewing.BridgeTerminalIdentity} conversation";
    public string ActivityNote => _reviewing is null ? "Every agent, one terminal" : "Reviewing this agent. Follow up below.";
    public bool HasWorkingAgents => Agents.Any(agent => agent.ShowWorkingText);
    public bool HasUsage => _teamUsage.Read + _teamUsage.Write > 0 || _teamUsage.Cost > 0;
    public string UsageText => HasUsage ? $"Total · {InlineUsageText}" : "";
    public string InlineUsageText => HasUsage
        ? $"{ChatViewModel.FmtTokens(_teamUsage.Read)} read · {ChatViewModel.FmtTokens(_teamUsage.Write)} write"
            + (_teamUsage.Cost > 0 ? $" · {(_teamUsage.Estimated ? "~" : "")}${_teamUsage.Cost:0.00##}" : "")
        : "";
    public string UsageToolTip => $"Combined usage for all {_teamUsage.AgentCount} workers and orchestrators, including active turns.\n"
        + $"Read: {_teamUsage.Read:N0} tokens (input and cache). Write: {_teamUsage.Write:N0} tokens."
        + (_teamUsage.Estimated ? "\n~ marks estimated cost." : "");

    private void RefreshUsage()
    {
        Raise(nameof(HasWorkingAgents));
        var costs = Agents.Select(agent => agent.DisplayedCost).ToArray();
        var usage = (Read: Agents.Sum(agent => agent.DisplayedInputTokens),
            Write: Agents.Sum(agent => agent.DisplayedOutputTokens),
            Cost: costs.Sum(cost => cost.Amount), Estimated: costs.Any(cost => cost.Estimated), AgentCount: Agents.Count);
        if (!Set(ref _teamUsage, usage, nameof(UsageText))) return;
        Raise(nameof(InlineUsageText));
        Raise(nameof(HasUsage));
        Raise(nameof(UsageToolTip));
    }

    public void Review(ChatViewModel agent)
    {
        if (!Agents.Contains(agent)) return;
        Target = agent;
        _reviewing = agent;
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
        var wasReconciling = _reconcilingRoster;
        _reconcilingRoster = true;
        try { ReconcileRoster(roster); }
        finally { _reconcilingRoster = wasReconciling; }
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
        SetThinkingSuppressed(roster.Any(p => p.IsBridgeManager && p.BridgeSingleTerminal));
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
        if (_reviewing is not null && !roster.Contains(_reviewing)) ShowAll();
        Target = roster.FirstOrDefault(p => p.BridgeTerminalSelected) ?? roster.FirstOrDefault();
        if (changed)
        {
            Raise(nameof(Entries));
            RefreshUsage();
        }
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var owner = _sources.Keys.FirstOrDefault(p => ReferenceEquals(p.Items, sender));
        if (owner is null) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _sources[owner].Values) RemoveEntry(entry);
            _sources[owner].Clear();
            foreach (var item in owner.Items) AddEntry(owner, item);
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Move) return; // Pinned tails already have their shared order.
        if (e.OldItems is not null)
            foreach (ItemVm item in e.OldItems)
                if (_sources[owner].Remove(item, out var entry)) RemoveEntry(entry);
        if (e.NewItems is not null)
            foreach (ItemVm item in e.NewItems) AddEntry(owner, item);
    }

    private void AddEntry(ChatViewModel owner, ItemVm item)
    {
        // Keep reasoning in its original chat, but do not allocate shared rows or subscribe to
        // their streaming updates when one terminal is displaying an orchestrated team's activity.
        if (_suppressThinking && item is ThinkingItem) return;
        if (_sources[owner].ContainsKey(item)) return;
        var entry = new BridgeTranscriptEntry(owner, item);
        _sources[owner].Add(item, entry);
        if (item is TextItem or ThinkingItem or QueuedItem) item.PropertyChanged += OnItemChanged;
        var index = Entries.Count;
        var rank = TranscriptItems.TailRank(item);
        while (index > 0)
        {
            var previous = Entries[index - 1].Item;
            var previousRank = TranscriptItems.TailRank(previous);
            if (previousRank < rank || previousRank == rank && previous.DisplaySequence <= item.DisplaySequence) break;
            index--;
        }
        Entries.Insert(index, entry);
    }

    private void SetThinkingSuppressed(bool suppress)
    {
        if (_suppressThinking == suppress) return;
        _suppressThinking = suppress;
        foreach (var (owner, entries) in _sources)
        {
            if (suppress)
            {
                foreach (var item in entries.Keys.OfType<ThinkingItem>().ToArray())
                {
                    var entry = entries[item];
                    entries.Remove(item);
                    RemoveEntry(entry);
                }
            }
            else
                foreach (var item in owner.Items.OfType<ThinkingItem>()) AddEntry(owner, item);
        }
    }

    private void RemoveEntry(BridgeTranscriptEntry entry)
    {
        entry.Item.PropertyChanged -= OnItemChanged;
        Entries.Remove(entry);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ItemVm item || e.PropertyName is not (nameof(TextItem.Text) or nameof(QueuedItem.Attachments) or null or "")) return;
        foreach (var source in _sources.Values)
            if (source.TryGetValue(item, out var entry))
            {
                // Refresh only when content appears or disappears, rather than on every streaming delta.
                if (entry.RefreshVisibility()) Activity.Refresh();
                break;
            }
    }

    private void OnAgentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.ShowWorkingText) or null or "") Raise(nameof(HasWorkingAgents));
        if (e.PropertyName == nameof(ChatViewModel.BridgeProgressRevision)) RefreshProgress();
        if (e.PropertyName is nameof(ChatViewModel.IsBridgeManager) or nameof(ChatViewModel.BridgeSingleTerminal) or null or "")
            SetThinkingSuppressed(_sources.Keys.Any(p => p.IsBridgeManager && p.BridgeSingleTerminal));
        if (e.PropertyName is nameof(ChatViewModel.TokensText) or nameof(ChatViewModel.CostText) or null or "")
            RefreshUsage();
        if (ReferenceEquals(sender, Target) && e.PropertyName is nameof(ChatViewModel.BridgeLabel)
            or nameof(ChatViewModel.BridgeTaskName)) Raise(nameof(TargetLabel));
        if (ReferenceEquals(sender, _reviewing) && e.PropertyName == nameof(ChatViewModel.BridgeLabel))
            Raise(nameof(ActivityTitle));
    }

    public void Dispose()
    {
        Activity.CollectionChanged -= OnActivityChanged;
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
