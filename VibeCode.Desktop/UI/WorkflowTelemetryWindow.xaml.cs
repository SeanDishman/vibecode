using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;

namespace VibeCode.UI;

/// <summary>The workflow wall is separate from the usage HUD/wall and never subscribes to usage services.</summary>
public partial class WorkflowTelemetryWindow : Window
{
    private static WorkflowTelemetryWindow? _open;
    private MainViewModel? _source;
    private IReadOnlyList<AgentActivityProject> _projects = [];
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private bool _updatingFilters;
    private bool _ready;
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
        Refresh();
        _tick.Start();
    }

    public void Refresh()
    {
        if (!_ready) return;
        _projects = AgentActivitySnapshot.Capture(_source);
        Clock.Text = DateTime.Now.ToString("HH:mm:ss");
        var agents = _projects.SelectMany(p => p.Agents).ToArray();
        var working = agents.Count(a => a.State == AgentActivityState.Working);
        var waiting = agents.Count(a => a.State == AgentActivityState.Waiting);
        var errors = agents.Count(a => a.State == AgentActivityState.Error);
        var children = agents.Sum(a => a.ActiveChildren);
        Summary.Text = $"{working} working · {waiting} waiting · {agents.Length} agents · {_projects.Count(p => p.Directory.Length > 0)} projects"
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
        EmptyTitle.Text = _projects.Count == 0 ? "No open agents yet" : "No matching agents";
        EmptyDetail.Text = _projects.Count == 0
            ? "Open a project chat or Bridge in VibeCode. Its agents appear here automatically."
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
        Graph.MotionEnabled = false;
        Graph.UpdateMotion();
        _source = null;
        if (ReferenceEquals(_open, this)) _open = null;
    }
}
