using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>Mirrors only the root pane's visible conversation. No provider-home scanning, hidden reasoning,
/// unsent drafts, queued prompts, attachment bytes, or nested child transcripts are exported.</summary>
internal sealed class BridgeChatMirror : IDisposable
{
    private readonly ChatViewModel _pane;
    private readonly Func<int> _number;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<ItemVm> _observed = new();
    private readonly HashSet<CompactToolGroupItem> _groups = new();
    private bool _disposed;
    private bool _warned;
    private int _lastNumber;
    public BridgeChatStore Store { get; }

    public BridgeChatMirror(ChatViewModel pane, BridgeChatStore store, Func<int> number)
    {
        (_pane, Store, _number) = (pane, store, number);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        pane.Items.CollectionChanged += OnItemsChanged;
        pane.PropertyChanged += OnPaneChanged;
        store.WriteFailed += OnWriteFailed;
        ObserveItems();
        Flush();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ObserveItems();
            Schedule();
            return;
        }
        // Replaying a long chat must not rescan every previous row on each insertion.
        var changed = false;
        if (e.OldItems is not null)
            foreach (ItemVm item in e.OldItems) changed |= SetObserved(item, false);
        if (e.NewItems is not null)
            foreach (ItemVm item in e.NewItems) changed |= SetObserved(item, true);
        if (changed) Schedule();
    }

    private bool SetObserved(ItemVm item, bool observe)
    {
        if (item is CompactToolGroupItem group)
        {
            if (observe && _groups.Add(group)) group.Tools.CollectionChanged += OnItemsChanged;
            else if (!observe && _groups.Remove(group)) group.Tools.CollectionChanged -= OnItemsChanged;
            foreach (var tool in group.Tools) SetObserved(tool, observe);
            return true;
        }
        if (item is UserItem or TextItem or ToolItem or SubagentItem)
        {
            if (observe && _observed.Add(item)) item.PropertyChanged += OnItemChanged;
            else if (!observe && _observed.Remove(item)) item.PropertyChanged -= OnItemChanged;
            return true;
        }
        return item is BannerItem or DividerItem;
    }

    private void ObserveItems()
    {
        var groups = _pane.Items.OfType<CompactToolGroupItem>().ToHashSet();
        foreach (var removed in _groups.Except(groups).ToArray())
        {
            removed.Tools.CollectionChanged -= OnItemsChanged;
            _groups.Remove(removed);
        }
        foreach (var added in groups.Except(_groups))
        {
            added.Tools.CollectionChanged += OnItemsChanged;
            _groups.Add(added);
        }
        var current = RootItems(_pane).Where(item => item is UserItem or TextItem or ToolItem or SubagentItem).ToHashSet();
        foreach (var removed in _observed.Except(current).ToArray())
        {
            removed.PropertyChanged -= OnItemChanged;
            _observed.Remove(removed);
        }
        foreach (var added in current.Except(_observed))
        {
            added.PropertyChanged += OnItemChanged;
            _observed.Add(added);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(TextItem.Text) or nameof(TextItem.Streaming)
            or nameof(ToolItem.Input) or nameof(ToolItem.Result) or nameof(ToolItem.Status) or nameof(UserItem.AgentBody)
            or nameof(SubagentItem.Label) or nameof(SubagentItem.Task) or nameof(SubagentItem.Activity))
            Schedule();
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.BridgeLabel) or nameof(ChatViewModel.Title)
            or nameof(ChatViewModel.SessionId) or nameof(ChatViewModel.Status)) Schedule();
    }

    private void Schedule() { if (!_disposed && !_timer.IsEnabled) _timer.Start(); }
    private void OnTick(object? sender, EventArgs e) => Flush();

    internal void Flush()
    {
        if (_disposed) return;
        _timer.Stop();
        Store.Publish(Snapshot(active: true));
    }

    private BridgeChatSnapshot Snapshot(bool active)
    {
        if (_number() is var number and > 0) _lastNumber = number;
        return Capture(_pane, _lastNumber, active);
    }

    private static IEnumerable<ItemVm> RootItems(ChatViewModel pane) => pane.Items.SelectMany(item =>
        item is CompactToolGroupItem group ? group.Tools.Cast<ItemVm>() : new[] { item });

    internal static BridgeChatSnapshot Capture(ChatViewModel pane, int number, bool active)
    {
        var messages = new List<BridgeChatMessage>();
        foreach (var item in RootItems(pane))
        {
            switch (item)
            {
                case UserItem { FromSubagent: false } user:
                    var text = user.IsAgentMessage ? user.AgentBody : user.Text;
                    if (user.Attachments is { Count: > 0 })
                        text += "\n[Attachments: " + string.Join(", ", user.Attachments.Select(a => a.FileName)) + "]";
                    messages.Add(new(messages.Count, user.IsAgentMessage ? "peer" : "user", text));
                    break;
                case TextItem { HasText: true } assistant:
                    messages.Add(new(messages.Count, "assistant", assistant.Text, InProgress: assistant.Streaming));
                    break;
                case ToolItem tool:
                    messages.Add(new(messages.Count, "tool",
                        "Input:\n" + (tool.Input?.ToJsonString() ?? "") + "\nOutput:\n" + (tool.Result ?? ""),
                        tool.Name, tool.Status == "running"));
                    break;
                case SubagentItem agent:
                    messages.Add(new(messages.Count, "tool",
                        $"{agent.Label}: {agent.StatusText}\nTask: {agent.Task}\n{agent.Activity}",
                        "spawn_agent", agent.IsActive));
                    break;
                case BannerItem banner:
                    messages.Add(new(messages.Count, "system", banner.Text));
                    break;
                case DividerItem divider:
                    messages.Add(new(messages.Count, "system", divider.Label));
                    break;
            }
        }
        return new(pane.BridgeId, number, pane.Provider, pane.Title, pane.SessionId,
            pane.Status, active, DateTimeOffset.UtcNow, messages.ToArray());
    }

    private void OnWriteFailed(string error)
    {
        _timer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _warned) return;
            _warned = true;
            _pane.Items.Add(new BannerItem { Level = "warning", Text = "Peer chat archive could not update: " + error });
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        // Keep the last conversation available by stable ID, explicitly marked as a departed peer.
        Store.Publish(Snapshot(active: false));
        _disposed = true;
        _pane.Items.CollectionChanged -= OnItemsChanged;
        _pane.PropertyChanged -= OnPaneChanged;
        Store.WriteFailed -= OnWriteFailed;
        foreach (var item in _observed) item.PropertyChanged -= OnItemChanged;
        _observed.Clear();
        foreach (var group in _groups) group.Tools.CollectionChanged -= OnItemsChanged;
        _groups.Clear();
    }
}
