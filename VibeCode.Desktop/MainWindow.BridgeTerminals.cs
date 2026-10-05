using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VibeCode.UI;

namespace VibeCode;

public partial class MainWindow
{
    private BridgeSetupPanel? _orchestratorSetupDraft;

    private void OnBridgeSetupStart(object? sender, EventArgs e)
    {
        var host = BridgeSetup.Host;
        if (host is null) return;
        var originalHost = host;
        try
        {
            var setup = BridgeSetup;
            if (setup.IsAdvanced && setup.Provider == host.Provider && !host.CanAcceptBridgeObjective)
                throw new InvalidOperationException("Reconnect this chat’s account before using it as the orchestrator, or choose another provider.");
            // A fresh advanced roster contains the selected roles. Keep an unmatched or busy source chat
            // separate instead of importing it as an idle, unconfigured independent agent.
            if (setup.IsAdvanced && _vm.WouldStartFreshBridge(host) && setup.Provider != host.Provider &&
                (setup.WorkerConfiguration.Provider != host.Provider || !host.AvailableForBridgeAssignment))
            {
                var configuration = setup.OrchestratorConfiguration;
                host = NewChatForWindow(originalHost.Cwd, provider: configuration.Provider, configure: chat =>
                {
                    chat.ExcludeFromMemory = originalHost.ExcludeFromMemory;
                    chat.SetMode(originalHost.Mode, remember: false);
                    chat.ApplyBridgeConfiguration(configuration);
                });
            }
            if (!ReferenceEquals(ActiveChatForWindow, host)) OpenChatForWindow(host);
            ActivateBridgeForWindow(setup.IsAdvanced && setup.Provider == host.Provider
                ? setup.WorkerConfiguration.Provider : setup.Provider);
            if (setup.IsAdvanced)
            {
                var coordinator = setup.Provider == host.Provider ? host
                    : BridgeRosterForWindow.First(p => !ReferenceEquals(p, host) && p.Provider == setup.Provider);
                _vm.ConfigureBridgeOrchestrators(coordinator, setup.WorkerAllocation, setup.Objective, setup.SingleTerminal,
                    orchestratorConfiguration: setup.OrchestratorConfiguration, workerConfiguration: setup.WorkerConfiguration,
                    centralConfiguration: setup.CentralConfiguration);
                if (originalHost.Draft.Trim() == setup.Objective) originalHost.Draft = "";
            }
            else _vm.SetBridgeTerminalMode(false, OwnsSecondaryBridge);
            BridgePeerPopup.IsOpen = false;
            RefreshBridgePartitions();
        }
        catch (Exception ex) { BridgeSetup.ShowError(ex.Message); }
    }

    private void OnBridgeSetupCancel(object? sender, EventArgs e)
    {
        BridgePeerPopup.IsOpen = false;
        BridgeButton.Focus();
    }

    private void OnBridgeSetupClosed(object? sender, EventArgs e)
    {
        BridgeSetup.Suspend();
    }

    private void SetupBridgeOrchestrator()
    {
        BridgePeerPopup.IsOpen = false;
        var host = SharedTerminalForWindow.Target;
        if (host is null) return;
        var maximum = _vm.MaximumOrchestratorWorkers(host, createNew: true);
        if (maximum < 1) { ShowBridgeHint("This bridge has no room for another orchestrator and worker. Raise the agent limit in Settings."); return; }
        _orchestratorSetupDraft ??= new BridgeSetupPanel();
        var setup = new BridgeOrchestratorWindow(host, maximum, _orchestratorSetupDraft,
            workerCapacity: _ => Math.Max(0, Services.BridgeAgentPolicy.ClampLimit(Services.AppSettings.Current.BridgeAgentLimit)
                - BridgeRosterForWindow.Count - 1 + BridgeRosterForWindow.Count(p => !p.IsBridgeManager &&
                    p.BridgeCoordinatorAgentId is null && p.AvailableForBridgeAssignment &&
                    p.Provider == _orchestratorSetupDraft.WorkerConfiguration.Provider)),
            hasOtherOrchestrators: BridgeRosterForWindow.Any(p => p.IsBridgeManager)) { Owner = this };
        if (setup.ShowDialog() != true) return;
        try
        {
            _vm.LaunchBridgeOrchestrators(host, setup.WorkerAllocation, setup.Objective, setup.SingleTerminal, setup.Provider,
                orchestratorConfiguration: setup.OrchestratorConfiguration, workerConfiguration: setup.WorkerConfiguration,
                centralConfiguration: setup.CentralConfiguration);
            _orchestratorSetupDraft = null;
            if (host.Draft.Trim() == setup.Objective) host.Draft = "";
            RefreshBridgePartitions();
        }
        catch (Exception ex) { ShowBridgeHint(ex.Message); }
    }

    private BridgeSharedTerminalViewModel SharedTerminalForWindow => OwnsSecondaryBridge
        ? _vm.SecondarySharedBridgeTerminal : _vm.SharedBridgeTerminal;

    private static ContextMenu CreateBridgeMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, "DarkContextMenu");
        return menu;
    }

    private void OnSharedLaunchOrchestrator(object sender, RoutedEventArgs e)
    {
        SetupBridgeOrchestrator();
        e.Handled = true;
    }

    private void OnSharedLaunchWorker(object sender, RoutedEventArgs e)
    {
        if (SharedTerminalForWindow.Target is not { } template || e.OriginalSource is not FrameworkElement anchor) return;
        if (!CanAddBridgeAgentForWindow) { ShowBridgeAgentLimitReached(); return; }
        var menu = CreateBridgeMenu(anchor);
        foreach (var (provider, label) in new[] { ("claude", "Claude Code"), ("codex", "Codex"), ("kimi", "Kimi Code"), ("grok", "Grok"), ("glm", "GLM") })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                try { _vm.LaunchBridgeWorker(template, provider); RefreshBridgePartitions(); }
                catch (Exception ex) { ShowBridgeHint(ex.Message); }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnSharedAgentMenu(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ChatViewModel agent } anchor) return;
        var menu = CreateBridgeMenu(anchor);
        var review = new MenuItem { Header = "Review conversation" };
        review.Click += (_, _) => SharedTerminalForWindow.Review(agent);
        menu.Items.Add(review);
        if (!agent.IsBridgeManager)
        {
            var group = new MenuItem { Header = "Worker group", IsEnabled = !agent.IsWorking };
            foreach (var coordinator in BridgeRosterForWindow.Where(p => p.IsBridgeManager))
            {
                var item = new MenuItem { Header = coordinator.BridgeTerminalIdentity, IsCheckable = true,
                    IsChecked = agent.BridgeCoordinatorAgentId == coordinator.BridgeAgentId };
                item.Click += (_, _) =>
                {
                    try { _vm.AssignBridgeWorker(agent, coordinator); }
                    catch (Exception ex) { ShowBridgeHint(ex.Message); }
                };
                group.Items.Add(item);
            }
            menu.Items.Add(group);
        }
        if (agent.CanInterrupt)
        {
            var stop = new MenuItem { Header = "Stop agent" };
            stop.Click += (_, _) => agent.Interrupt();
            menu.Items.Add(stop);
        }
        var close = new MenuItem { Header = "Close agent" };
        close.Click += (_, _) => _vm.RemoveBridgePane(agent);
        menu.Items.Add(close);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnPaneReviewLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ChatViewModel agent, SelectedValue: string level } && agent.BridgeReviewLevel != level)
            _vm.SetBridgeReviewLevel(agent, level);
    }

    private void OnBridgeTerminals(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var panes = OwnsSecondaryBridge ? _vm.SecondaryBridgePanes : _vm.BridgePanes;
        var single = panes.FirstOrDefault()?.BridgeSingleTerminal == true;
        var menu = CreateBridgeMenu(anchor);
        foreach (var pane in panes)
        {
            var item = new MenuItem
            {
                Header = $"Review {pane.BridgeTerminalLabel} · {pane.Status}",
                ToolTip = pane.BridgeActivitySummary,
                IsCheckable = true,
                IsChecked = single && pane.BridgeTerminalSelected,
            };
            item.Click += (_, _) => { SelectTerminal(pane); SharedTerminalForWindow.Review(pane); };
            menu.Items.Add(item);
        }
        var separator = new Separator();
        separator.SetResourceReference(StyleProperty, "DarkSeparator");
        menu.Items.Add(separator);
        var all = new MenuItem { Header = "All activity" };
        all.Click += (_, _) => { _vm.SetBridgeTerminalMode(true, OwnsSecondaryBridge); SharedTerminalForWindow.ShowAll(); };
        menu.Items.Add(all);
        AddLayout("Single terminal", true);
        AddLayout("Separate terminals", false);
        menu.IsOpen = true;
        e.Handled = true;

        void AddLayout(string label, bool useSingle)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = single == useSingle };
            item.Click += (_, _) =>
            {
                _vm.SetBridgeTerminalMode(useSingle, OwnsSecondaryBridge);
                RefreshBridgePartitions();
            };
            menu.Items.Add(item);
        }
    }

    private void OnSelectBridgeTerminal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatViewModel pane }) SelectTerminal(pane);
        e.Handled = true;
    }

    private void SelectTerminal(ChatViewModel pane)
    {
        _vm.SelectBridgeTerminal(pane);
        RefreshBridgePartitions();
        _vm.NoteBridgeActivity();
    }
}
