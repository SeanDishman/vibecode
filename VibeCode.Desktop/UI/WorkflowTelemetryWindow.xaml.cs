using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>A separate workflow wall using recent agent activity and the local token ledger.</summary>
public partial class WorkflowTelemetryWindow : Window
{
    private static WorkflowTelemetryWindow? _open;
    private MainViewModel? _source;
    private IReadOnlyList<AgentActivityProject> _projects = [];
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatingFilters;
    private bool _ready;
    private IDisposable? _liveWatch;
    private IReadOnlyList<UsageEntry> _committedUsage = [];
    private volatile bool _usageDirty = true;
    private sealed record ProjectChoice(string Key, string Label);

    public WorkflowTelemetryWindow() : this(null) { }
    public WorkflowTelemetryWindow(MainViewModel? source)
    {
        _source = source;
        InitializeComponent();
        _ready = true;
        Graph.AgentSelected += OpenAgent;
        Graph.ProjectSelected += key => ProjectFilter.SelectedValue = key;
        _tick.Tick += (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) Refresh(); };
    }

    public static void Open(Window? owner)
    {
        if (_open is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var wall = new WorkflowTelemetryWindow(AgentActivitySnapshot.ResolveSource(owner)) { Owner = owner };
        _open = wall;
        if (Environment.GetEnvironmentVariable("VIBECODE_HIDDEN") == "1")
        { wall.WindowStartupLocation = WindowStartupLocation.Manual; wall.Left = 6200; wall.Top = 200; wall.Show(); return; }
        wall.Show();
        try
        {
            if (AppSettings.Current.TelemetryOnCompanionDisplay && owner is not null
                && DisplayMonitorService.TryGetCompanionWorkArea(owner, out var target))
                DisplayMonitorService.PlaceOnMonitor(wall, target, maximize: true);
            else wall.WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { wall.WindowState = WindowState.Maximized; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source ??= AgentActivitySnapshot.ResolveSource(Owner);
        _liveWatch ??= LiveTurnTelemetry.Instance.Watch();
        UsageLog.Instance.Changed += OnUsageChanged;
        Refresh();
        _tick.Start();
    }

    public void Refresh()
    {
        if (!_ready) return;
        var now = DateTimeOffset.Now;
        _projects = AgentActivitySnapshot.Capture(_source, now);
        Clock.Text = DateTime.Now.ToString("HH:mm:ss");
        var agents = _projects.SelectMany(p => p.Agents).ToArray();
        var working = agents.Count(a => a.State == AgentActivityState.Working);
        var waiting = agents.Count(a => a.State == AgentActivityState.Waiting);
        var errors = agents.Count(a => a.State == AgentActivityState.Error);
        var children = agents.Sum(a => a.ActiveChildren);
        var projectCount = _projects.Count(p => p.Directory.Length > 0);
        Summary.Text = $"{working} working · {waiting} waiting · {agents.Length} agent{(agents.Length == 1 ? "" : "s")} · {projectCount} project{(projectCount == 1 ? "" : "s")}"
            + (errors > 0 ? $" · {errors} with errors" : "")
            + (children > 0 ? $" · {children} active subagents" : "");

        var choices = new[] { new ProjectChoice("*", "All projects") }
            .Concat(_projects.Select(p => new ProjectChoice(p.Key,
                _projects.Count(other => other.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) > 1 ? p.Directory : p.Name))).ToArray();
        if (ProjectFilter.ItemsSource is not ProjectChoice[] current || !current.SequenceEqual(choices))
        {
            var selected = ProjectFilter.SelectedValue as string ?? "*";
            _updatingFilters = true;
            ProjectFilter.ItemsSource = choices;
            ProjectFilter.SelectedValue = choices.Any(c => c.Key == selected) ? selected : "*";
            _updatingFilters = false;
        }
        ApplyFilters();
        RefreshUsage(now);
    }

    private void OnUsageChanged() => _usageDirty = true;

    private void RefreshUsage(DateTimeOffset now)
    {
        if (_usageDirty)
        {
            _usageDirty = false;
            _committedUsage = UsageLog.Instance.Entries();
        }
        // Merge replaces provisional turns when the existing accounting path commits them. Never add Flow()
        // or per-chat lifetime totals: those contain the same tokens and would count them twice.
        var usage = WorkflowUsageSnapshot.Build(LiveTurnTelemetry.Instance.Merge(_committedUsage), now);
        UsageTotal.Text = UsageAnalytics.Tokens(usage.Report.Total) + " total tokens";
        UsageInput.Text = UsageAnalytics.Tokens(usage.Report.TotalIn) + " input";
        UsageOutput.Text = UsageAnalytics.Tokens(usage.Report.Output) + " output";
        UsageCache.Text = UsageAnalytics.Tokens(usage.Report.CacheRead) + " cache read";
        UsageCacheWrite.Text = UsageAnalytics.Tokens(usage.Report.CacheWrite) + " cache write";
        UsageCacheRate.Text = $"{usage.Report.CacheHitRatio:P0} cache hit";
        UsageTurns.Text = $"{usage.Report.Turns:N0} turns";
        UsagePerTurn.Text = UsageAnalytics.Tokens(usage.TokensPerTurn) + " tokens / turn";
        UsageRate.Text = UsageAnalytics.Tokens(usage.TokensPerMinute) + " tokens / min";
        UsageRecentRate.Text = UsageAnalytics.Tokens(usage.RecentTokensPerMinute) + " / min · last 5m";
        UsageModelCount.Text = $"{usage.Report.Models.Count:N0} models";
        TokenTimeline.Hours = usage.Buckets;
        ModelTokens.Report = usage.Report;
        System.Windows.Automation.AutomationProperties.SetName(TokenTimeline,
            $"Tokens in the last hour: {usage.Report.Total:N0} total, {usage.Report.TotalIn:N0} input, {usage.Report.Output:N0} output.");
        System.Windows.Automation.AutomationProperties.SetName(ModelTokens,
            "Token usage by model. " + string.Join("; ", usage.Report.Models.Select(m => $"{m.Display}: {m.TotalIn:N0} input, {m.Output:N0} output")));
    }

    private void OnWallSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        var compact = WallScroll.ActualWidth < 1000;
        var height = Math.Max(280, WallScroll.ActualHeight);
        // 28% of the width by 70% of the height: roughly one fifth of the workspace, bottom right.
        UsagePanel.Width = compact ? Math.Min(440, WallScroll.ActualWidth - 24) : WallScroll.ActualWidth * .28;
        UsagePanel.Height = compact ? Math.Max(160, Math.Min(400, WallScroll.ActualHeight)) : height * .70;
        Grid.SetColumn(UsagePanel, compact ? 0 : 1);
        Grid.SetRow(UsagePanel, compact ? 1 : 0);
        UsagePanel.Margin = new Thickness(0, compact ? 12 : 0, 12, 0);
        MapFrame.Height = compact ? Math.Max(320, height * .70) : height;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    { if (_ready && !_updatingFilters) ApplyFilters(); }

    private void ApplyFilters()
    {
        var selected = ProjectFilter.SelectedValue as string ?? "*";
        var search = Search.Text.Trim();
        bool Contains(string text) => text.Contains(search, StringComparison.OrdinalIgnoreCase);
        var filtered = _projects.Where(p => selected == "*" || p.Key == selected)
            .Select(p => p with { Agents = p.Agents.Where(a =>
                (WorkingOnly.IsChecked != true || a.State == AgentActivityState.Working)
                && (search.Length == 0 || Contains(p.Name) || Contains(p.Directory) || Contains(a.Label)
                    || Contains(a.Task) || Contains(a.Model) || Contains(a.Account))).ToArray() })
            .Where(p => p.Agents.Count > 0).ToArray();
        Graph.SetProjects(filtered);
        var count = filtered.Sum(p => p.Agents.Count);
        VisibleCount.Text = $"{count} shown · click an agent to open its chat";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _projects.Count == 0 ? "No recent agent activity" : "No matching agents";
        EmptyDetail.Text = _projects.Count == 0
            ? "Agents disappear after 10 minutes without work. Start a task in any project or Bridge."
            : "Try another project or search, or clear the filters to see every open agent.";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        Search.Clear(); WorkingOnly.IsChecked = false; ProjectFilter.SelectedValue = "*";
        _updatingFilters = false;
        ApplyFilters();
    }

    private void OpenAgent(AgentActivityNode node)
    {
        if (_source is null || !_source.OpenWorkflowAgent(node.Chat)) { Refresh(); return; }
        for (var window = Owner; window is not null; window = window.Owner)
        {
            if (window is not MainWindow) continue;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            break;
        }
    }

    private void OnPinChanged(object sender, RoutedEventArgs e) => Topmost = Pin.IsChecked == true;
    private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void ToggleFullScreen() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnStateChanged(object? sender, EventArgs e) => Graph?.UpdateMotion();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _ready = false;
        _tick.Stop();
        UsageLog.Instance.Changed -= OnUsageChanged;
        _liveWatch?.Dispose();
        _liveWatch = null;
        Graph.MotionEnabled = false;
        Graph.UpdateMotion();
        _source = null;
        if (ReferenceEquals(_open, this)) _open = null;
    }
}
