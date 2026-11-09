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
    private IReadOnlyList<ModelUsage> _speedModels = [];
    private WorkflowModelSpeedRow[] _speedRows = [];
    private string _speedSelection = "";
    private DateTime _nextSpeedRefresh;
    private readonly FullScreenPlacement _fullScreen;
    private readonly WorkflowHistory _history = new();
    private IReadOnlyList<AgentActivityProject> _filtered = [];
    private string _view = WorkflowViews.Graph;
    private sealed record ProjectChoice(string Key, string Label);
    private sealed record SceneChoice(string Key, string Label);

    private static readonly SceneChoice[] SkyChoices =
        [new("auto", "Sky: your clock"), new("dawn", "Dawn"), new("day", "Day"), new("dusk", "Dusk"), new("night", "Night")];
    private static readonly SceneChoice[] WeatherChoices =
    [
        new("auto", "Weather: auto"), new("clear", "Clear"), new("cloudy", "Cloudy"), new("rain", "Rain"),
        new("storm", "Thunderstorm"), new("snow", "Snow"), new("fog", "Fog"),
    ];

    private IWorkflowWallView[] Views => [Graph, Tavern, Office, Floor, Lanes, Board];
    private IWorkflowWallView ActiveView => _view switch
    {
        WorkflowViews.Tavern => Tavern, WorkflowViews.Office => Office, WorkflowViews.Floor => Floor, WorkflowViews.Lanes => Lanes,
        WorkflowViews.Board => Board, _ => Graph,
    };
    /// <summary>The view on screen, as its settings key. Exposed for verification.</summary>
    public string CurrentView => _view;
    public bool IsFullScreen => _fullScreen.IsActive;
    /// <summary>Whether full screen is also holding the wall above the taskbar (only off the app's display).</summary>
    public bool FullScreenAboveTaskbar => _fullScreen.AboveTaskbar;

    public WorkflowTelemetryWindow() : this(null) { }
    public WorkflowTelemetryWindow(MainViewModel? source)
    {
        _source = source;
        InitializeComponent();
        TelemetryTheme.Attach(this);
        _fullScreen = new FullScreenPlacement(this);
        _fullScreen.Changed += OnFullScreenChanged;
        foreach (var view in Views)
        {
            view.AgentSelected += OpenAgent;
            view.AgentMenuRequested += ShowAccountMenu;
            view.ProjectSelected += key => ProjectFilter.SelectedValue = key;
        }
        // Flow speed comes from the shared OpenRouter cache only; the graph itself never requests anything.
        Graph.SpeedLookup = id => ModelSpeedService.Instance.ById(id);
        Board.SpeedLookup = Graph.SpeedLookup;
        Lanes.History = _history;
        _updatingFilters = true;
        ViewPicker.ItemsSource = WorkflowViews.All;
        ViewPicker.SelectedValue = WorkflowViews.For(AppSettings.Current.WorkflowWallView).Key;
        SkyPicker.ItemsSource = SkyChoices;
        SkyPicker.SelectedValue = SkyChoices.Any(c => c.Key == AppSettings.Current.WorkflowSceneSky) ? AppSettings.Current.WorkflowSceneSky : "auto";
        WeatherPicker.ItemsSource = WeatherChoices;
        WeatherPicker.SelectedValue = WeatherChoices.Any(c => c.Key == AppSettings.Current.WorkflowSceneWeather) ? AppSettings.Current.WorkflowSceneWeather : "auto";
        _updatingFilters = false;
        ApplyScene();
        ShowView(WorkflowViews.For(AppSettings.Current.WorkflowWallView).Key);
        _ready = true;
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
        wall._ownership = new PinnedWindowOwner(wall, owner);
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
            // Full screen means the whole display, taskbar included - not the maximized work area. On a display the
            // app is not on, the wall also stays over the taskbar while the app has focus.
            wall._fullScreen.Enter(FullScreenPlacement.OnOtherDisplay(wall, owner));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { wall.WindowState = WindowState.Maximized; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _source ??= AgentActivitySnapshot.ResolveSource(_ownership?.Anchor ?? Owner);
        _liveWatch ??= LiveTurnTelemetry.Instance.Watch();
        WorkflowViews.Chosen += OnViewChosen;
        UsageLog.Instance.Changed += OnUsageChanged;
        ModelSpeedService.Instance.Updated += OnModelSpeedsUpdated;
        Refresh();
        _tick.Start();
    }

    public void Refresh()
    {
        if (!_ready) return;
        var now = DateTimeOffset.Now;
        _projects = AgentActivitySnapshot.Capture(_source, now);
        // Every snapshot, whichever view is showing, so the swimlanes have the history when they are opened.
        _history.Record(_projects, now.UtcDateTime);
        ObserveFleet(now.UtcDateTime);
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
        _speedModels = usage.Report.Models;
        RefreshModelSpeeds();
        System.Windows.Automation.AutomationProperties.SetName(TokenTimeline,
            $"Tokens in the last hour: {usage.Report.Total:N0} total, {usage.Report.TotalIn:N0} input, {usage.Report.Output:N0} output.");
        System.Windows.Automation.AutomationProperties.SetName(ModelTokens,
            "Token usage by model. " + string.Join("; ", usage.Report.Models.Select(m => $"{m.Display}: {m.TotalIn:N0} input, {m.Output:N0} output")));
    }

    private void OnModelSpeedsUpdated()
    {
        if (!_ready || !IsVisible || WindowState == WindowState.Minimized) return;
        RefreshModelSpeeds();
        Graph.RefreshSpeeds();
    }

    private void RefreshModelSpeeds()
    {
        var service = ModelSpeedService.Instance;
        var rows = WorkflowModelSpeeds.Build(_speedModels, service.ById, service.FetchedAt);
        if (!_speedRows.SequenceEqual(rows))
        {
            _speedRows = rows;
            ModelSpeeds.ItemsSource = rows;
        }
        SpeedCaption.Text = WorkflowModelSpeeds.Caption(rows);
        // Every agent on the wall streams at its own model's speed, including a model that has not used tokens
        // this hour yet, so those ids are watched alongside the board's most-used models.
        var ids = rows.Select(r => r.OpenRouterId).OfType<string>()
            .Concat(_projects.SelectMany(p => p.Agents).Select(a => a.OpenRouterId).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selection = string.Join("|", ids);
        if (selection != _speedSelection || DateTime.UtcNow >= _nextSpeedRefresh)
        {
            _speedSelection = selection;
            _nextSpeedRefresh = DateTime.UtcNow + WorkflowModelSpeeds.RefreshInterval;
            service.PrefetchIds(ids, WorkflowModelSpeeds.RefreshInterval - TimeSpan.FromSeconds(30));
        }
    }

    private void OnWallSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ready) return;
        var compact = WallScroll.ActualWidth < 1000;
        var height = Math.Max(280, WallScroll.ActualHeight);
        // The map leads. Public model performance fills the previous gap above the local token charts.
        AnalyticsRail.Width = compact ? Math.Min(440, WallScroll.ActualWidth - 24) : WallScroll.ActualWidth * .28;
        var speedHeight = compact ? 204 : Math.Clamp(height * .24, 204, 236);
        AnalyticsRail.Height = compact ? speedHeight + Math.Max(160, Math.Min(400, WallScroll.ActualHeight)) : height;
        SpeedRow.Height = new GridLength(speedHeight);
        Grid.SetColumn(AnalyticsRail, compact ? 0 : 1);
        Grid.SetRow(AnalyticsRail, compact ? 1 : 0);
        AnalyticsRail.Margin = new Thickness(0, compact ? 12 : 0, 12, 0);
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
        _filtered = filtered;
        ActiveView.SetProjects(filtered);
        var count = filtered.Sum(p => p.Agents.Count);
        VisibleCount.Text = $"{count} shown · click an agent to open its chat";
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _projects.Count == 0 ? "No recent agent activity" : "No matching agents";
        EmptyDetail.Text = _projects.Count == 0
            ? "Agents disappear after 1 minute without work. Start a task in any project or Bridge."
            : "Try another project or search, or clear the filters to see every open agent.";
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters || ViewPicker.SelectedValue is not string key || key == _view) return;
        ShowView(key);
        WorkflowViews.Choose(key);
    }

    /// <summary>Settings › Appearance picked a view: an open wall redraws in it at once.</summary>
    private void OnViewChosen(string key)
    {
        if (_ready && key != _view) ShowView(key);
    }

    /// <summary>Show one view and park the rest. Every view gets the current roster the moment it appears; the
    /// hidden ones stop their motion through their own visibility handlers.</summary>
    private void ShowView(string key)
    {
        var choice = WorkflowViews.For(key);
        _view = choice.Key;
        MapScroll.Visibility = Visible(WorkflowViews.Graph);
        Tavern.Visibility = Visible(WorkflowViews.Tavern);
        Office.Visibility = Visible(WorkflowViews.Office);
        Floor.Visibility = Visible(WorkflowViews.Floor);
        LanesScroll.Visibility = Visible(WorkflowViews.Lanes);
        Board.Visibility = Visible(WorkflowViews.Board);
        MapDots.Visibility = _view is WorkflowViews.Graph or WorkflowViews.Board ? Visibility.Visible : Visibility.Collapsed;
        SceneControls.Visibility = _view is WorkflowViews.Tavern or WorkflowViews.Office or WorkflowViews.Floor ? Visibility.Visible : Visibility.Collapsed;
        Legend.Text = choice.Legend;
        if (!Equals(ViewPicker.SelectedValue, _view))
        {
            _updatingFilters = true;
            ViewPicker.SelectedValue = _view;
            _updatingFilters = false;
        }
        ActiveView.SetProjects(_filtered);
        foreach (var view in Views) view.UpdateMotion();
        RestyleRecap();

        Visibility Visible(string view) => _view == view ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Switch views from code; the dropdown follows. For verification and keyboard shortcuts.</summary>
    public void SelectView(string key)
    {
        var choice = WorkflowViews.For(key);
        if (ViewPicker.Items.OfType<WorkflowViewChoice>().Any(c => c.Key == choice.Key))
        {
            ViewPicker.SelectedValue = choice.Key;
        }
        else
        {
            ShowView(key);
            WorkflowViews.Choose(key);
        }
    }

    private void OnSceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters) return;
        ApplyScene();
        AppSettings.Current.WorkflowSceneSky = Tavern.SkyMode;
        AppSettings.Current.WorkflowSceneWeather = Tavern.WeatherMode;
        AppSettings.Current.Save();
    }

    private void ApplyScene()
    {
        var sky = SkyPicker.SelectedValue as string ?? "auto";
        var weather = WeatherPicker.SelectedValue as string ?? "auto";
        Tavern.SkyMode = Office.SkyMode = Floor.SkyMode = sky;
        Tavern.WeatherMode = Office.WeatherMode = Floor.WeatherMode = weather;
        Tavern.InvalidateVisual();
        Office.InvalidateVisual();
        Floor.InvalidateVisual();
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
        // A pinned wall has no owner, so the main window is found from the window it belongs to.
        for (var window = _ownership?.Anchor ?? Owner; window is not null; window = window.Owner)
        {
            if (window is not MainWindow) continue;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            break;
        }
    }

    /// <summary>The account menu last opened on an agent. Exposed for verification.</summary>
    public ContextMenu? AccountMenu { get; private set; }

    /// <summary>A right-click on an agent: its provider's saved logins and nothing else, the one it runs under ticked.
    /// Picking another carries the chat onto it until the next pick (<see cref="MainViewModel.ChooseChatAccount"/>).
    /// With one login - nothing to choose - no menu opens at all.</summary>
    private void ShowAccountMenu(AgentActivityNode node)
    {
        if (_source is not { } source || source.AccountOptionsFor(node.Chat) is not { Count: > 1 } options) return;
        // At the pointer, measured from the view: where MousePoint would put it for a real click, without opening the
        // tests' offscreen wall's menu under the user's own pointer.
        var view = ActiveView as UIElement;
        var at = view is null ? new Point() : Mouse.GetPosition(view);
        var menu = new ContextMenu
        {
            PlacementTarget = view, Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = at.X, VerticalOffset = at.Y,
        };
        menu.SetResourceReference(StyleProperty, "DarkContextMenu");
        foreach (var option in options)
        {
            // A TextBlock rather than a string header, so a login called "dev_box" keeps its underscore instead of
            // losing it to an access key. Checkable keeps every name aligned whether or not it carries the tick.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = option.Label, MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis },
                IsCheckable = true, IsChecked = option.IsCurrent,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, option.Label);
            var chat = node.Chat;
            var id = option.Id;
            item.Click += (_, _) => source.ChooseChatAccount(chat, id);
            menu.Items.Add(item);
        }
        AccountMenu = menu;
        menu.IsOpen = true;
    }

    private PinnedWindowOwner? _ownership;

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        Topmost = Pin.IsChecked == true || _fullScreen.AboveTaskbar;
        _ownership?.Apply(Pin.IsChecked == true);
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        try { _fullScreen.Toggle(_ownership?.Anchor ?? Owner); }
        catch (System.ComponentModel.Win32Exception) { WindowState = WindowState.Maximized; }
    }

    private void OnFullScreenChanged()
    {
        var full = _fullScreen.IsActive;
        FullScreenButton.Content = full ? "\uE73F" : "\uE740";
        FullScreenButton.ToolTip = full ? "Exit full screen (F11)" : "Full screen (F11)";
        WindowFrame.BorderThickness = new Thickness(full ? 0 : 1);
        Topmost = Pin.IsChecked == true || _fullScreen.AboveTaskbar;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        foreach (var view in Views) view.UpdateMotion();
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { if (!CloseRecap()) Close(); e.Handled = true; }
        else if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _ready = false;
        _tick.Stop();
        WorkflowViews.Chosen -= OnViewChosen;
        UsageLog.Instance.Changed -= OnUsageChanged;
        ModelSpeedService.Instance.Updated -= OnModelSpeedsUpdated;
        _liveWatch?.Dispose();
        CloseExtras();
        _liveWatch = null;
        foreach (var view in Views)
        {
            view.MotionEnabled = false;
            view.UpdateMotion();
        }
        _source = null;
        if (ReferenceEquals(_open, this)) _open = null;
    }
}
