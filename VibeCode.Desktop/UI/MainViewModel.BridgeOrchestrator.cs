using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    public void ConfigureBridgeOrchestrator(ChatViewModel host, int workerCount, string objective, bool singleTerminal,
        BridgeAgentConfiguration? orchestratorConfiguration = null, BridgeAgentConfiguration? workerConfiguration = null)
        => ConfigureBridgeOrchestrators(host, [workerCount], objective, singleTerminal, orchestratorConfiguration, workerConfiguration);

    /// <summary>Build every group, then let one temporary central session divide multi-group objectives before delivery.</summary>
    public IReadOnlyList<ChatViewModel> ConfigureBridgeOrchestrators(ChatViewModel host, IReadOnlyList<int> workerAllocation,
        string objective, bool singleTerminal, BridgeAgentConfiguration? orchestratorConfiguration = null,
        BridgeAgentConfiguration? workerConfiguration = null, BridgeAgentConfiguration? centralConfiguration = null)
    {
        if (!TryGetLiveBridge(host, out var bridge))
            throw new InvalidOperationException("Open a regular bridge before setting up its orchestrators.");
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("Enter the task for the orchestrators.");
        BridgeTeamAllocationPolicy.Validate(workerAllocation, BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - 1);
        if (WorkFor(bridge.Panes).DispatchStarted)
            throw new InvalidOperationException("Worker dispatch has already begun. Start a new bridge for a new orchestrator setup.");
        if (WorkFor(bridge.Panes).CentralPlan is { State: "planning" or "handing_off" })
            throw new InvalidOperationException("The central orchestrator is still dividing this task. Wait for its handoff before changing the team.");
        if (!host.CanAcceptBridgeObjective)
            throw new InvalidOperationException("The orchestrator is disconnected. Reconnect its account, then start the team again.");
        var orchestratorSettings = BridgeAgentConfigurationPolicy.Normalize(orchestratorConfiguration ?? BridgeAgentConfigurationPolicy.From(host));
        var centralSettings = BridgeAgentConfigurationPolicy.Normalize(centralConfiguration ?? orchestratorSettings);
        if (orchestratorSettings.Provider != host.Provider)
            throw new InvalidOperationException("An existing orchestrator cannot change provider. Start a new orchestrator with that provider.");
        var workerSettings = BridgeAgentConfigurationPolicy.Normalize(workerConfiguration ??
            host.BridgeWorkerConfiguration ?? BridgeAgentConfigurationPolicy.From(host));
        var owned = bridge.Panes.Where(p => !p.IsBridgeManager && p.BridgeCoordinatorAgentId == host.BridgeAgentId).ToArray();
        var available = bridge.Panes.Count(p => !ReferenceEquals(p, host) && AvailableBridgeWorker(p) && p.Provider == workerSettings.Provider);
        BridgeTeamAllocationPolicy.Validate(workerAllocation,
            BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - bridge.Panes.Count + available + owned.Length);
        if (owned.Length > workerAllocation[0] || owned.Any(p => p.Provider != workerSettings.Provider ||
            p.IsWorking && (p.Model != workerSettings.Model || p.Effort != workerSettings.Effort)))
            throw new InvalidOperationException("Keep this orchestrator's current worker group and settings until its work finishes.");
        var previousManagers = bridge.Panes.Where(p => p.IsBridgeManager && !ReferenceEquals(p, host)).ToArray();
        var managers = new List<ChatViewModel> { host };
        var roster = BridgePanes.Contains(host) ? null : bridge;
        _launchingOrchestratorGroup = true;
        try
        {
            for (var index = 1; index < workerAllocation.Count; index++)
            {
                var before = bridge.Panes.ToHashSet();
                AddBridgeAgent(orchestratorSettings.Provider, roster, host, null, orchestratorSettings);
                var manager = bridge.Panes.FirstOrDefault(p => !before.Contains(p))
                    ?? throw new InvalidOperationException("The bridge could not add another orchestrator.");
                managers.Add(manager);
            }
            // Reserve all managers before choosing reusable workers, including cross-provider host chats.
            foreach (var manager in managers)
            {
                manager.IsBridgeManager = true;
                manager.BridgeCoordinatesOnly = true;
                manager.BridgeCoordinatorAgentId = null;
            }
            for (var index = 0; index < managers.Count; index++)
                PrepareBridgeOrchestrator(managers[index], workerAllocation[index], singleTerminal,
                    index == 0 ? orchestratorConfiguration : orchestratorSettings, workerSettings);
        }
        finally { _launchingOrchestratorGroup = false; }
        var work = WorkFor(bridge.Panes);
        foreach (var manager in managers) work.OrchestratorObjectives[manager.BridgeAgentId] = objective.Trim();
        var allManagers = previousManagers.Concat(managers).ToArray();
        if (allManagers.Length > 1)
        {
            work.CentralPlan = new BridgeCentralPlan
            {
                Provider = centralSettings.Provider, Model = centralSettings.Model, Effort = centralSettings.Effort,
                Cancellation = new CancellationTokenSource(),
            };
            work.ConfirmedVersions.Clear();
            foreach (var manager in allManagers) manager.BridgeScopeConfirmed = false;
        }
        else work.CentralPlan = null;
        foreach (var manager in previousManagers.Concat(managers)) manager.BridgeCoordinationRoster = "";
        RebriefBridgeGroups(bridge);
        foreach (var pane in bridge.Panes) pane.BridgeTerminalSelected = ReferenceEquals(pane, host);
        SelectBridgePane(host);
        if (allManagers.Length > 1)
            _ = RunCentralOrchestratorAsync(host, bridge, allManagers, previousManagers, centralSettings);
        else
        {
            if (!host.SendBridgeObjective("[BRIDGE ORCHESTRATOR] Coordinate " + workerAllocation[0] +
                " worker AI(s) toward this goal:\n\n" + objective.Trim()))
                throw new InvalidOperationException("An orchestrator is not accepting input. Reconnect its account, then send the task again.");
        }
        AppSettings.Current.BridgeOrchestratorCount = workerAllocation.Count;
        AppSettings.Current.BridgeOrchestratorWorkerCounts = workerAllocation.ToArray();
        NoteBridgeActivity(roster);
        SaveBridge(bridge.Panes, bridge.Board);
        RaiseRosterUi(roster);
        RequestSave();
        return managers;
    }

    private void PrepareBridgeOrchestrator(ChatViewModel host, int workerCount, bool singleTerminal,
        BridgeAgentConfiguration? orchestratorConfiguration, BridgeAgentConfiguration? workerConfiguration)
    {
        if (!TryGetLiveBridge(host, out var bridge))
            throw new InvalidOperationException("Open a regular bridge before setting up its orchestrator.");
        if (!host.CanAcceptBridgeObjective)
            throw new InvalidOperationException("The orchestrator is disconnected. Reconnect its account, then start the team again.");
        if (orchestratorConfiguration is not null &&
            !string.Equals(orchestratorConfiguration.Provider, host.Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose this agent's provider for its orchestrator settings, or start a new orchestrator.");
        var workerSettings = BridgeAgentConfigurationPolicy.Normalize(workerConfiguration ??
            host.BridgeWorkerConfiguration ?? BridgeAgentConfigurationPolicy.From(host));
        var maximum = BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - 1;
        if (workerCount < 1 || workerCount > maximum)
            throw new ArgumentOutOfRangeException(nameof(workerCount), $"Choose between 1 and {maximum} workers.");
        var owned = bridge.Panes.Where(p => !ReferenceEquals(p, host) && !p.IsBridgeManager &&
            p.BridgeCoordinatorAgentId == host.BridgeAgentId).ToList();
        if (owned.Count > workerCount)
            throw new InvalidOperationException("This orchestrator already has more workers. Choose at least its current group size.");
        if (owned.Any(worker => worker.Provider != workerSettings.Provider))
            throw new InvalidOperationException("This group already has workers with another provider. Keep that provider or start a new orchestrator group.");
        if (owned.Any(worker => worker.IsWorking &&
            (worker.Model != workerSettings.Model || worker.Effort != workerSettings.Effort)))
            throw new InvalidOperationException("Let this group's workers finish before changing their model or effort.");
        var available = bridge.Panes.Where(p => !ReferenceEquals(p, host) && AvailableBridgeWorker(p)
                                               && p.Provider == workerSettings.Provider)
            .Take(workerCount - owned.Count).ToList();
        var missing = workerCount - owned.Count - available.Count;
        if (bridge.Panes.Count + missing > maximum + 1)
            throw new InvalidOperationException("This group exceeds the bridge's agent limit. Choose fewer workers or raise the limit in Settings.");
        if (orchestratorConfiguration is not null) host.ApplyBridgeConfiguration(orchestratorConfiguration);
        host.BridgeWorkerConfiguration = workerSettings;
        var roster = BridgePanes.Contains(host) ? null : bridge;
        host.IsBridgeManager = true;
        host.BridgeCoordinatesOnly = true;
        host.BridgeCoordinatorAgentId = null;
        // Group creation must not auto-dispatch its new sessions through another orchestrator.
        var wasLaunching = _launchingOrchestratorGroup;
        _launchingOrchestratorGroup = true;
        try
        {
            while (missing-- > 0)
            {
                var previous = bridge.Panes.ToHashSet();
                AddBridgeAgent(workerSettings.Provider, roster, host, host, workerSettings);
                var added = bridge.Panes.FirstOrDefault(p => !previous.Contains(p));
                if (added is null) throw new InvalidOperationException("The bridge could not add another worker.");
                available.Add(added);
            }
        }
        finally { _launchingOrchestratorGroup = wasLaunching; }
        foreach (var worker in owned.Concat(available))
        {
            worker.ApplyBridgeConfiguration(workerSettings);
            worker.BridgeCoordinatorAgentId = host.BridgeAgentId;
        }
        foreach (var pane in bridge.Panes)
        {
            pane.BridgeSingleTerminal = singleTerminal;
            pane.BridgeTerminalSelected = ReferenceEquals(pane, host);
            if (ReferenceEquals(pane, host))
            {
                pane.BridgeTaskName = "Orchestrator";
                pane.BridgeActivitySummary = "Planning and coordinating its worker group";
            }
            else if (available.Contains(pane))
            {
                pane.BridgeTaskName = "Awaiting assignment";
                pane.BridgeActivitySummary = $"Ready for {host.BridgeTerminalIdentity}'s assignment";
            }
            _bridgeSeenStatus[pane] = pane.Status;
        }
    }
}
