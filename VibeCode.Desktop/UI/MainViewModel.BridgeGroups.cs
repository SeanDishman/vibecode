using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    // A freshly launched session is connecting, not doing assigned work. Reuse it instead of
    // silently creating an extra worker while its provider is still initializing.
    private static bool AvailableBridgeWorker(ChatViewModel pane) =>
        !pane.IsBridgeManager && pane.BridgeCoordinatorAgentId is null && pane.AvailableForBridgeAssignment;

    // An original chat that cannot fill a selected role stays outside the new roster.
    public int MaximumFreshOrchestratorWorkers =>
        Math.Max(0, BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - 1);

    private static ChatViewModel? CoordinatorFor(ChatViewModel worker, IEnumerable<ChatViewModel> roster)
    {
        if (worker.IsBridgeManager) return null;
        var agents = roster.ToArray();
        if (worker.BridgeCoordinatorAgentId is { } id)
            return agents.FirstOrDefault(p => p.IsBridgeManager && p.BridgeAgentId == id);
        // Preserve older flat-manager teams, which predate explicit worker ownership.
        var managers = agents.Where(p => p.IsBridgeManager).ToArray();
        return managers.Length == 1 && !managers[0].BridgeCoordinatesOnly ? managers[0] : null;
    }

    private static void ReconcileBridgeGroups(IReadOnlyList<ChatViewModel> roster)
    {
        var work = WorkFor(roster);
        var managers = roster.Where(p => p.IsBridgeManager).ToArray();
        var signature = string.Join("|", managers.Select(p => p.BridgeAgentId).Order(StringComparer.Ordinal));
        if (work.OrchestratorRoster != signature)
        {
            if (!work.DispatchStarted && work.OrchestratorRoster.Length > 0 && (work.Tasks.Count > 0 || work.ConfirmedVersions.Count > 0))
            {
                work.PlanVersion++;
                work.ConfirmedVersions.Clear();
            }
            work.OrchestratorRoster = signature;
        }
        foreach (var manager in managers)
        {
            manager.BridgeCoordinatorAgentId = null;
            if (manager.BridgeCoordinationRoster != signature)
            {
                manager.BridgeCoordinationRoster = signature;
                manager.BridgeCoordinationSince = DateTimeOffset.UtcNow;
                manager.BridgeScopeConfirmed = work.Confirmed(manager.BridgeAgentId);
                if (work.Scopes.TryGetValue(manager.BridgeAgentId, out var scope)) manager.BridgeOrchestrationScope = scope;
            }
        }
        foreach (var agent in roster)
        {
            if (agent.BridgeCoordinatorAgentId is { } id && !managers.Any(p => p.BridgeAgentId == id))
                agent.BridgeCoordinatorAgentId = null;
            var coordinator = CoordinatorFor(agent, roster);
            if (coordinator is not null) agent.BridgeCoordinatorAgentId = coordinator.BridgeAgentId;
        }
        foreach (var agent in roster)
        {
            var coordinator = CoordinatorFor(agent, roster);
            var workers = roster.Count(p => p.BridgeCoordinatorAgentId == agent.BridgeAgentId && !p.IsBridgeManager);
            agent.BridgeGroupLabel = agent.IsBridgeManager
                ? $"Orchestrator · {workers} worker{(workers == 1 ? "" : "s")}" +
                  (managers.Length > 1 && !agent.BridgeScopeConfirmed
                      ? work.CentralPlan is null ? " · agreeing scope" : work.CentralPlan.State == "completed" ? " · planning workers" : " · awaiting assignment"
                      : "")
                : coordinator is not null ? $"Worker for {coordinator.BridgeTerminalIdentity}" : "Independent agent";
            agent.RaiseBridgeGroup();
        }
    }

    public void AssignBridgeWorker(ChatViewModel worker, ChatViewModel? coordinator)
    {
        if (!TryGetLiveBridge(worker, out var bridge) || worker.IsBridgeManager)
            throw new InvalidOperationException("Choose a worker in a regular bridge.");
        if (coordinator is not null && (!bridge.Panes.Contains(coordinator) || !coordinator.IsBridgeManager))
            throw new InvalidOperationException("Choose an orchestrator in this bridge.");
        if (worker.IsWorking && worker.BridgeCoordinatorAgentId != coordinator?.BridgeAgentId)
            throw new InvalidOperationException("Let this worker finish its assignment before moving it to another group.");
        if (WorkFor(bridge.Panes).Tasks.Any(t => t.OwnerId == worker.BridgeAgentId && t.State != "completed") &&
            worker.BridgeCoordinatorAgentId != coordinator?.BridgeAgentId)
            throw new InvalidOperationException("This worker has unfinished durable tasks. Finish those assignments before changing its group.");
        worker.BridgeCoordinatorAgentId = coordinator?.BridgeAgentId;
        var roster = BridgePanes.Contains(worker) ? null : bridge;
        RebriefBridgeGroups(bridge);
        RaiseRosterUi(roster);
        SaveBridge(bridge.Panes, bridge.Board);
        RequestSave();
    }

    private void RebriefBridgeGroups(LiveBridge bridge)
    {
        ReconcileBridgeGroups(bridge.Panes);
        var numbers = bridge.Panes.Select(BridgeNumberOf).ToArray();
        foreach (var pane in bridge.Panes)
        {
            var brief = BridgePrompt(bridge.Board, pane, BridgeNumberOf(pane), numbers, ManagerNumberIn(bridge.Panes));
            pane.AppendSystemPrompt = brief;
            StagePeerNotice(pane, brief);
        }
    }

    private static bool CoordinationReady(IReadOnlyList<ChatViewModel> roster)
    {
        var work = WorkFor(roster);
        var managers = roster.Where(p => p.IsBridgeManager).ToArray();
        if (work.CentralPlan is { } central)
            return central.State == "completed" && central.Assignments.Count == managers.Length && managers.All(m => central.Assignments.Any(a => a.AgentId == m.BridgeAgentId) &&
                central.PublishedPlans.Contains(m.BridgeAgentId) && work.Confirmed(m.BridgeAgentId));
        return managers.Length <= 1 || managers.All(m => work.Confirmed(m.BridgeAgentId));
    }

    private void RequireOwnedWorker(ChatViewModel manager, ChatViewModel worker, LiveBridge bridge)
    {
        ReconcileBridgeGroups(bridge.Panes);
        if (worker.IsBridgeManager || !ReferenceEquals(CoordinatorFor(worker, bridge.Panes), manager))
            throw new StatusValidationException("This worker belongs to another group or is unassigned. Dispatch only to agents whose coordinator_id is your agent_id; use bridge_send_message to coordinate with other orchestrators.");
        if (!CoordinationReady(bridge.Panes))
        {
            if (WorkFor(bridge.Panes).CentralPlan is not null)
                throw new StatusValidationException("Dispatch is paused until the central handoff succeeds and every orchestrator publishes its own worker plan with bridge_set_plan. Read bridge_list_tasks for the central assignments and current state; do not negotiate duplicate scopes.");
            throw new StatusValidationException("The orchestrators must agree on non-overlapping scopes before assigning new work. Message the other orchestrators, read their replies, and have each call bridge_agree_scope.");
        }
    }

    private JsonObject AgreeBridgeScope(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        if (!caller.IsBridgeManager) throw new StatusValidationException("Only an orchestrator can agree its group's scope.");
        ReconcileBridgeGroups(bridge.Panes);
        var work = WorkFor(bridge.Panes);
        var scope = args["scope"]!.GetValue<string>().Trim();
        var version = args["plan_version"]?.GetValue<int>();
        if (version is null && bridge.Panes.Count(p => p.IsBridgeManager) > 1)
            throw new StatusValidationException("Read bridge_list_tasks and confirm its exact plan_version.");
        if (version is not null && version != work.PlanVersion)
            throw new StatusValidationException($"Stale plan version. Read bridge_list_tasks; current plan_version is {work.PlanVersion}.");
        if (work.CentralPlan is { } central)
        {
            var assignment = central.Assignments.FirstOrDefault(a => a.AgentId == caller.BridgeAgentId);
            if (central.State != "completed" || assignment is null)
                throw new StatusValidationException("The central handoff has not completed. Read bridge_list_tasks for its state; worker dispatch remains paused.");
            if (scope != assignment.Scope)
                throw new StatusValidationException("The central orchestrator already assigned your scope. Keep that exact scope and publish only its worker plan with bridge_set_plan.");
            return new JsonObject { ["agent_id"] = caller.BridgeAgentId, ["scope"] = assignment.Scope,
                ["coordination_ready"] = CoordinationReady(bridge.Panes), ["plan_version"] = work.PlanVersion,
                ["next_step"] = "Publish your worker plan with bridge_set_plan. The app opens dispatch when every group's plan is published; no peer scope negotiation is required." };
        }
        if (caller.BridgeScopeConfirmed)
            return new JsonObject { ["agent_id"] = caller.BridgeAgentId, ["scope"] = caller.BridgeOrchestrationScope,
                ["coordination_ready"] = CoordinationReady(bridge.Panes), ["already_confirmed"] = true, ["plan_version"] = work.PlanVersion,
                ["next_step"] = "Your scope is already confirmed for this goal. Keep the existing division and worker ownership. Do not re-confirm or change it; a new orchestrator setup opens a fresh coordination round." };
        var peers = bridge.Panes.Where(p => p.IsBridgeManager && !ReferenceEquals(p, caller)).ToArray();
        if (work.DispatchStarted) throw new StatusValidationException("Worker dispatch has begun; the setup agreement is permanently closed for this run.");
        foreach (var peer in peers)
        {
            if (!work.SetupMessages.Contains(BridgeWorkState.PeerKey(caller.BridgeAgentId, peer.BridgeAgentId)) ||
                !work.SetupReads.Contains(BridgeWorkState.PeerKey(caller.BridgeAgentId, peer.BridgeAgentId)))
                throw new StatusValidationException($"First send your proposed division to {peer.BridgeTerminalIdentity} with bridge_send_message and read its reply with bridge_read_messages. Then confirm the agreed scope.");
        }
        var wasReady = CoordinationReady(bridge.Panes);
        if (caller.BridgeOrchestrationScope != scope) InvalidateBridgeReview(caller);
        caller.BridgeOrchestrationScope = scope;
        caller.BridgeScopeConfirmed = true;
        work.Scopes[caller.BridgeAgentId] = scope;
        work.ConfirmedVersions[caller.BridgeAgentId] = work.PlanVersion;
        var ready = CoordinationReady(bridge.Panes);
        ReconcileBridgeGroups(bridge.Panes);
        if (ready && !wasReady)
        {
            var division = string.Join("\n", bridge.Panes.Where(p => p.IsBridgeManager).Select(p =>
                $"{p.BridgeTerminalIdentity}: {p.BridgeOrchestrationScope}"));
            foreach (var manager in peers)
                SendManagerUpdate(manager, "All orchestrators confirmed their scopes. Assign work only to your own workers.\n" + division);
        }
        RaiseRosterUi(BridgePanes.Contains(caller) ? null : bridge);
        SaveWork(bridge);
        RequestSave();
        return new JsonObject { ["agent_id"] = caller.BridgeAgentId, ["scope"] = scope, ["plan_version"] = work.PlanVersion,
            ["coordination_ready"] = ready, ["next_step"] = ready
                ? "Assign useful work to your own workers with bridge_dispatch_task."
                : "Finish your turn. The app will notify you when the other orchestrators confirm their scopes." };
    }

    public ChatViewModel LaunchBridgeOrchestrator(ChatViewModel template, int workerCount, string objective,
        bool? singleTerminal = null, string? provider = null,
        BridgeAgentConfiguration? orchestratorConfiguration = null, BridgeAgentConfiguration? workerConfiguration = null)
        => LaunchBridgeOrchestrators(template, [workerCount], objective, singleTerminal, provider,
            orchestratorConfiguration, workerConfiguration)[0];

    public IReadOnlyList<ChatViewModel> LaunchBridgeOrchestrators(ChatViewModel template, IReadOnlyList<int> workerAllocation, string objective,
        bool? singleTerminal = null, string? provider = null,
        BridgeAgentConfiguration? orchestratorConfiguration = null, BridgeAgentConfiguration? workerConfiguration = null,
        BridgeAgentConfiguration? centralConfiguration = null)
    {
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Choose at least one worker and enter the orchestrator's objective.");
        BridgeTeamAllocationPolicy.Validate(workerAllocation, BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - 1);
        if (!TryGetLiveBridge(template, out var bridge)) throw new InvalidOperationException("Open a bridge first.");
        if (WorkFor(bridge.Panes).DispatchStarted)
            throw new InvalidOperationException("This run has already dispatched work. Start a new bridge to configure another orchestrator group.");
        if (WorkFor(bridge.Panes).CentralPlan is { State: "planning" or "handing_off" })
            throw new InvalidOperationException("Wait for the central orchestrator to finish the handoff before adding another group.");
        if (bridge.Panes.Count >= BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit))
            throw new InvalidOperationException("The bridge is at its agent limit. Raise it in Settings before adding an orchestrator.");
        var orchestratorSettings = BridgeAgentConfigurationPolicy.Normalize(orchestratorConfiguration ??
            BridgeAgentConfigurationPolicy.Defaults(provider ?? template.Provider, template));
        var workerSettings = BridgeAgentConfigurationPolicy.Normalize(workerConfiguration ??
            template.BridgeWorkerConfiguration ?? BridgeAgentConfigurationPolicy.From(template));
        var candidates = bridge.Panes.Count(p => AvailableBridgeWorker(p) && p.Provider == workerSettings.Provider);
        BridgeTeamAllocationPolicy.Validate(workerAllocation,
            BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - bridge.Panes.Count - 1 + candidates);
        var needed = workerAllocation.Count + Math.Max(0, workerAllocation.Sum() - candidates);
        if (bridge.Panes.Count + needed > BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit))
            throw new InvalidOperationException("This group would exceed the bridge's agent limit. Choose fewer workers or raise the limit in Settings.");
        var target = BridgePanes.Contains(template) ? null : bridge;
        var before = bridge.Panes.ToHashSet();
        _launchingOrchestratorGroup = true;
        try { AddBridgeAgent(orchestratorSettings.Provider, target, template, null, orchestratorSettings); }
        finally { _launchingOrchestratorGroup = false; }
        var coordinator = bridge.Panes.FirstOrDefault(p => !before.Contains(p))
            ?? throw new InvalidOperationException("Could not start an orchestrator.");
        return ConfigureBridgeOrchestrators(coordinator, workerAllocation, objective, singleTerminal ?? template.BridgeSingleTerminal,
            orchestratorSettings, workerSettings, centralConfiguration);
    }

    private bool _launchingOrchestratorGroup;

    private void ToggleBridgeGroupCoordinator(ChatViewModel pane)
    {
        if (!TryGetLiveBridge(pane, out var bridge)) return;
        if (!pane.IsBridgeManager && WorkFor(bridge.Panes).DispatchStarted)
        {
            pane.Items.Add(new BannerItem { Level = "warning", Text = "This run has already dispatched work. Start a new bridge to add another orchestrator." });
            return;
        }
        var target = BridgePanes.Contains(pane) ? null : bridge;
        if (pane.IsBridgeManager)
        {
            var workers = bridge.Panes.Where(p => p.BridgeCoordinatorAgentId == pane.BridgeAgentId).ToArray();
            pane.IsBridgeManager = false;
            pane.BridgeCoordinatesOnly = false;
            pane.BridgeOrchestrationScope = "";
            pane.BridgeReviewState = "pending";
            pane.BridgeReviewSummary = "";
            pane.BridgeTaskState = "ready";
            pane.BridgeTaskName = "Ready";
            pane.BridgeActivitySummary = "Orchestrator role removed";
            pane.PurgeManagerInjectedQueue();
            pane.ClearManagerPreludes();
            foreach (var worker in workers)
            {
                worker.BridgeCoordinatorAgentId = null;
                worker.PurgeManagerInjectedQueue();
                worker.ClearManagerPreludes();
                worker.Items.Add(new DividerItem { Label = $"{pane.BridgeTerminalIdentity} stepped down; this worker is unassigned" });
            }
            pane.Items.Add(new DividerItem { Label = "Orchestrator role removed" });
        }
        else
        {
            var firstManager = !bridge.Panes.Any(p => p.IsBridgeManager);
            pane.IsBridgeManager = true;
            pane.BridgeCoordinatesOnly = true;
            pane.BridgeCoordinatorAgentId = null;
            if (firstManager)
                foreach (var worker in bridge.Panes.Where(p => !p.IsBridgeManager && p.BridgeCoordinatorAgentId is null))
                    worker.BridgeCoordinatorAgentId = pane.BridgeAgentId;
            pane.BridgeTaskName = "Orchestrator";
            pane.Items.Add(new DividerItem { Label = "Orchestrator role assigned" });
        }
        RebriefBridgeGroups(bridge);
        if (pane.IsBridgeManager)
        {
            pane.Send("[BRIDGE ORCHESTRATOR] The user chose you to coordinate your own worker group. " +
                "Use bridge_list_agents for group ownership. If there are other orchestrators, exchange proposed non-overlapping scopes, " +
                "read their replies, and each call bridge_agree_scope before dispatching. Use bridge_dispatch_task for your own workers. " +
                "Use the goal already established in this conversation; ask the user for it if no goal was given.");
            foreach (var manager in bridge.Panes.Where(p => p.IsBridgeManager && !ReferenceEquals(p, pane)))
                SendManagerUpdate(manager, $"{pane.BridgeTerminalIdentity} is now another orchestrator. Keep your own workers, coordinate the goal division, and confirm your agreed scope with bridge_agree_scope before assigning new work.");
        }
        NoteBridgeActivity(target);
        SaveBridge(bridge.Panes, bridge.Board);
        RaiseRosterUi(target);
        RequestSave();
    }

    public int MaximumOrchestratorWorkers(ChatViewModel template, bool createNew)
    {
        if (!TryGetLiveBridge(template, out var bridge))
            return BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - 1;
        var available = bridge.Panes.Count(p => !p.IsBridgeManager &&
            (AvailableBridgeWorker(p) || !createNew && p.BridgeCoordinatorAgentId == template.BridgeAgentId) &&
            (createNew || !ReferenceEquals(p, template)));
        return Math.Max(0, BridgeAgentPolicy.ClampLimit(AppSettings.Current.BridgeAgentLimit) - bridge.Panes.Count -
            (createNew ? 1 : 0) + available);
    }

    public ChatViewModel LaunchBridgeWorker(ChatViewModel template, string? provider = null,
        BridgeAgentConfiguration? configuration = null)
    {
        if (!TryGetLiveBridge(template, out var bridge)) throw new InvalidOperationException("Open a bridge first.");
        var coordinator = template.IsBridgeManager ? template : CoordinatorFor(template, bridge.Panes);
        var workerSettings = configuration ?? coordinator?.BridgeWorkerConfiguration;
        if (provider is not null && workerSettings?.Provider != provider)
            workerSettings = BridgeAgentConfigurationPolicy.Defaults(provider, template);
        workerSettings ??= BridgeAgentConfigurationPolicy.Defaults(provider ?? template.Provider, template);
        workerSettings = BridgeAgentConfigurationPolicy.Normalize(workerSettings);
        var before = bridge.Panes.ToHashSet();
        _launchingOrchestratorGroup = true;
        try { AddBridgeAgent(workerSettings.Provider, BridgePanes.Contains(template) ? null : bridge, template, coordinator, workerSettings); }
        finally { _launchingOrchestratorGroup = false; }
        var worker = bridge.Panes.FirstOrDefault(p => !before.Contains(p))
            ?? throw new InvalidOperationException("The bridge is at its agent limit. Raise the limit in Settings to add another worker.");
        worker.BridgeCoordinatorAgentId = coordinator?.BridgeAgentId;
        RebriefBridgeGroups(bridge);
        if (coordinator is not null)
            SendManagerUpdate(coordinator, $"{worker.BridgeTerminalIdentity} joined your worker group. " +
                "When orchestrator scopes are agreed, assign it a useful unclaimed lane with bridge_dispatch_task.");
        RaiseRosterUi(BridgePanes.Contains(template) ? null : bridge);
        SaveBridge(bridge.Panes, bridge.Board);
        return worker;
    }
}
