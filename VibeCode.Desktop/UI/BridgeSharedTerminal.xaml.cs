using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;

namespace VibeCode.UI;

public partial class BridgeSharedTerminal : UserControl
{
    public static readonly RoutedEvent LaunchWorkerEvent = EventManager.RegisterRoutedEvent(nameof(LaunchWorker),
        RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BridgeSharedTerminal));
    public static readonly RoutedEvent LaunchOrchestratorEvent = EventManager.RegisterRoutedEvent(nameof(LaunchOrchestrator),
        RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BridgeSharedTerminal));
    public static readonly RoutedEvent AgentMenuEvent = EventManager.RegisterRoutedEvent(nameof(AgentMenu),
        RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BridgeSharedTerminal));
    public event RoutedEventHandler LaunchWorker { add => AddHandler(LaunchWorkerEvent, value); remove => RemoveHandler(LaunchWorkerEvent, value); }
    public event RoutedEventHandler LaunchOrchestrator { add => AddHandler(LaunchOrchestratorEvent, value); remove => RemoveHandler(LaunchOrchestratorEvent, value); }
    public event RoutedEventHandler AgentMenu { add => AddHandler(AgentMenuEvent, value); remove => RemoveHandler(AgentMenuEvent, value); }

    public BridgeSharedTerminal()
    {
        InitializeComponent();
        RefreshMessageDividers();
        DataContextChanged += OnTerminalDataContextChanged;
    }

    // An agent team has no roster column at all. The width follows the window for ordinary rosters, so the team
    // switch is watched here too: the column has to close the moment a regular Bridge becomes a team. The view model
    // outlives a rebuilt shell, so the subscription is weak and never keeps a closed window alive.
    private void OnTerminalDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        const string property = nameof(BridgeSharedTerminalViewModel.IsAgentTeam);
        if (e.OldValue is BridgeSharedTerminalViewModel previous)
            System.ComponentModel.PropertyChangedEventManager.RemoveHandler(previous, OnTerminalPropertyChanged, property);
        if (e.NewValue is BridgeSharedTerminalViewModel next)
            System.ComponentModel.PropertyChangedEventManager.AddHandler(next, OnTerminalPropertyChanged, property);
        UpdateRosterColumn(ActualWidth);
    }

    private void OnTerminalPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        UpdateRosterColumn(ActualWidth);

    private void UpdateRosterColumn(double width) =>
        RosterColumn.Width = new GridLength(DataContext is BridgeSharedTerminalViewModel { IsAgentTeam: true } ? 0
            : width < 620 ? 166 : width < 820 ? 192 : 232);

    public void RefreshMessageDividers()
    {
        var thickness = AppSettings.Current.ShowAgentMessageDividers ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        if (!Equals(Resources["AgentMessageDividerThickness"], thickness))
            Resources["AgentMessageDividerThickness"] = thickness;
    }

    private void OnLaunchWorker(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(LaunchWorkerEvent, sender));
    private void OnLaunchOrchestrator(object sender, RoutedEventArgs e) => RaiseEvent(new RoutedEventArgs(LaunchOrchestratorEvent, sender));
    private void OnAgentMenu(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(AgentMenuEvent, sender));
        e.Handled = true;
    }
    private void OnReviewAgent(object sender, RoutedEventArgs e)
    {
        if (DataContext is BridgeSharedTerminalViewModel model && sender is FrameworkElement { DataContext: ChatViewModel agent })
            model.Review(agent);
        e.Handled = true;
    }
    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is BridgeSharedTerminalViewModel model) model.ShowAll();
    }
    private void OnReviewLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is BridgeSharedTerminalViewModel model &&
            sender is ComboBox { DataContext: ChatViewModel agent, SelectedValue: string level } && agent.BridgeReviewLevel != level)
            model.SetReviewLevel(agent, level);
    }
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRosterColumn(e.NewSize.Width);
}
