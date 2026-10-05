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
    }

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
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) =>
        RosterColumn.Width = new GridLength(e.NewSize.Width < 620 ? 166 : e.NewSize.Width < 820 ? 192 : 232);
}
