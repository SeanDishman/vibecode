using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A native, provider-neutral view of the memories and relationships shared by every VibeCode agent.</summary>
public partial class MemoryMapWindow : Window, INotifyPropertyChanged
{
    private static MemoryMapWindow? _open;
    private readonly DispatcherTimer _autoRefresh = new();
    private AgentMemoryGraphNode? _selectedNode;

    public AgentMemoryService Service { get; } = AgentMemoryService.Instance;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AgentMemoryGraphNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value)) return;
            _selectedNode = value;
            Raise();
            Raise(nameof(StrengthText));
            Raise(nameof(UpdatedText));
        }
    }

    public string MemoryCountText => FormatCount(Service.CurrentGraph?.MemoryCount ?? 0);
    public string SessionCountText => FormatCount(Service.CurrentGraph?.SessionCount ?? 0);
    public string DurableText
    {
        get
        {
            var durable = Service.CurrentGraph?.DurableCount ?? 0;
            return durable > 0
                ? $"{durable:N0} saved as durable memories. Say “remember that…” to add one."
                : "Say “remember that…” in any chat to promote something into a durable memory.";
        }
    }
    public string SetupButtonText => Service.IsBusy ? "Setting up…" : "Set up & start";
    /// <summary>The map draws a bounded slice of a much larger corpus, so the node count sits well under what
    /// MEMORY SIGNAL reports. Naming the slice stops the two numbers reading as one contradicting the other.</summary>
    public string GraphCaption => Service.CurrentGraph is { } graph
        ? graph.MappedSessionCount > 0 && graph.MappedSessionCount < graph.SessionCount
            ? $"{graph.Nodes.Count:N0} nodes  ·  {graph.Edges.Count:N0} links  ·  newest {graph.MappedSessionCount:N0} of {graph.SessionCount:N0} sessions"
            : $"{graph.Nodes.Count:N0} nodes  ·  {graph.Edges.Count:N0} links"
        : "waiting for memory graph";

    /// <summary>Search over a map this size needs a result count; silently dimming nodes reads as "search is broken".</summary>
    public string SearchSummary
    {
        get
        {
            var query = SearchBox?.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return "";
            var nodes = Service.CurrentGraph?.Nodes ?? Array.Empty<AgentMemoryGraphNode>();
            var matches = nodes.Count(node => MemoryGraphControl.Matches(node, query));
            return matches == 0 ? "no matches" : $"{matches:N0} of {nodes.Count:N0} match  ·  Enter to zoom";
        }
    }
    public string StrengthText => SelectedNode is null ? "—" : $"{Math.Clamp(SelectedNode.Strength, 0, 1):P0}";
    public string UpdatedText => RelativeTime(SelectedNode?.UpdatedAt);
    public string FooterText
    {
        get
        {
            // Offline and switched-off both read as "not online", but only offline queues: with the master switch
            // off, capture returns before it can write anything, so promising a queue would be a lie.
            if (!Service.MemoryEnabled) return "Memory is off · the map below is the last cached snapshot";
            if (Service.ShowOffline) return "Offline cache stays visible · new memories queue safely";
            if (Service.IsStarting) return "Starting the brain · the map below is the last cached snapshot";
            if (!Service.IsOnline) return "Checking the brain · the map below is the last cached snapshot";
            var updated = Service.CurrentGraph?.UpdatedAt;
            return updated is null ? "Connected · waiting for the first memory" : $"Map refreshed {RelativeTime(updated)}";
        }
    }

    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = new MemoryMapWindow { Owner = owner };
        if (owner is not null) SizeJustInsideOwner(window, owner);
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 6400;
            window.Top = 220;
        }
        _open = window;
        window.Show();
    }

    private static void SizeJustInsideOwner(MemoryMapWindow window, Window owner)
    {
        // Match the game windows: nearly fill the IDE while leaving a small, deliberate rim around the brain.
        var ownerWidth = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        var ownerHeight = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        if (double.IsFinite(ownerWidth)) window.Width = Math.Max(window.MinWidth, ownerWidth - 96);
        if (double.IsFinite(ownerHeight)) window.Height = Math.Max(window.MinHeight, ownerHeight - 80);
    }

    public MemoryMapWindow()
    {
        InitializeComponent();
        DataContext = this;
        Service.PropertyChanged += OnServicePropertyChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MemoryGraph.Focus();
        // Chats keep writing while this window is open; without a poll the map is only ever as fresh as the click
        // that opened it, which is what made the brain look like it never stored anything.
        _autoRefresh.Interval = TimeSpan.FromSeconds(25);
        _autoRefresh.Tick += OnAutoRefresh;
        _autoRefresh.Start();
        try { await Service.StartAsync(); }
        catch { /* Connection state and the cached graph already explain failures in the surface. */ }
    }

    private async void OnAutoRefresh(object? sender, EventArgs e)
    {
        // Skipping the tick while offline is what made a wrong "offline" stick: nothing else re-checks with this
        // window open, so a brain that came back - or was never really gone - stayed greyed out until the user
        // clicked. RefreshGraphAsync leads with a forced liveness probe and returns early if it fails, so letting
        // the tick through costs one /livez and is what lets the surface correct itself.
        if (Service.IsBusy) return;
        try { await Service.RefreshGraphAsync(); }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _autoRefresh.Stop();
        _autoRefresh.Tick -= OnAutoRefresh;
        Service.PropertyChanged -= OnServicePropertyChanged;
        if (ReferenceEquals(_open, this)) _open = null;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnServicePropertyChanged(sender, e)));
            return;
        }

        if (e.PropertyName == nameof(AgentMemoryService.CurrentGraph))
        {
            var selectedId = SelectedNode?.Id;
            SelectedNode = selectedId is null
                ? null
                : Service.CurrentGraph?.Nodes.FirstOrDefault(node => node.Id == selectedId);
        }
        Raise(nameof(SetupButtonText));
        Raise(nameof(MemoryCountText));
        Raise(nameof(SessionCountText));
        Raise(nameof(DurableText));
        Raise(nameof(GraphCaption));
        Raise(nameof(SearchSummary));
        Raise(nameof(FooterText));
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await Service.RefreshGraphAsync(); }
        catch { }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        try { await Service.StartAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Second Brain", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Delete every stored memory. Irreversible, so it defaults to No and names the counts the map is actually
    /// reporting rather than a vague "all your data", then reports what was really removed.
    /// </summary>
    private async void OnForgetEverything(object sender, RoutedEventArgs e)
    {
        var graph = Service.CurrentGraph;
        var remembered = graph?.MemoryCount ?? 0;
        var sessions = graph?.SessionCount ?? 0;
        var scale = remembered == 0 && sessions == 0
            ? "Delete everything the second brain has stored?"
            : $"Delete all {remembered:N0} remembered items across {sessions:N0} chat sessions?";
        if (MessageBox.Show(this,
                $"{scale}\n\nThis erases every memory, session, and observation from the shared brain that "
                + "Claude, Codex, Kimi, and Grok all read from — plus the cached map and any queued writes.\n\n"
                + "It cannot be undone.",
                "Delete all memories", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        try
        {
            var purge = await Service.ForgetEverythingAsync();
            SelectedNode = null;
            MessageBox.Show(this, Describe(purge), "Delete all memories", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't delete every memory:\n{ex.Message}", "Delete all memories",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Describe(AgentMemoryPurge purge)
    {
        if (!purge.ReachedDaemon)
            return "The memory engine is offline, so only the local cache and "
                   + $"{purge.QueuedWrites:N0} queued writes were cleared.\n\nStart the brain and delete again to "
                   + "erase what the engine itself still holds.";
        var deleted = purge.Total == 0
            ? "There was nothing left to delete."
            : $"Deleted {purge.Memories:N0} memories and {purge.Sessions:N0} sessions.";
        return purge.QueuedWrites > 0
            ? $"{deleted}\n\n{purge.QueuedWrites:N0} queued writes were discarded as well."
            : deleted;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnFit(object sender, RoutedEventArgs e) => MemoryGraph.FitToView();
    private void OnZoomIn(object sender, RoutedEventArgs e) => MemoryGraph.ZoomBy(1.2);
    private void OnZoomOut(object sender, RoutedEventArgs e) => MemoryGraph.ZoomBy(1 / 1.2);

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnCloseDetails(object sender, RoutedEventArgs e) => SelectedNode = null;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        Raise(nameof(SearchSummary));
        Raise(nameof(FooterText));
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            MemoryGraph.FitToMatches();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void OnCopyMemory(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is null) return;
        try
        {
            Clipboard.SetText($"{SelectedNode.Label}\n\n{SelectedNode.Content}");
        }
        catch { }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if ((e.Key == Key.D0 || e.Key == Key.NumPad0) && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            MemoryGraph.FitToView();
            e.Handled = true;
        }
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            MemoryGraph.ZoomBy(1.2);
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            MemoryGraph.ZoomBy(1 / 1.2);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SelectedNode is not null)
        {
            SelectedNode = null;
            e.Handled = true;
        }
    }

    private static string FormatCount(int value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}m",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString("N0"),
    };

    private static string RelativeTime(DateTimeOffset? value)
    {
        if (value is null) return "—";
        var age = DateTimeOffset.Now - value.Value.ToLocalTime();
        if (age < TimeSpan.Zero) return "now";
        if (age.TotalMinutes < 1) return "now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays}d ago";
        return value.Value.ToLocalTime().ToString("MMM d");
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
