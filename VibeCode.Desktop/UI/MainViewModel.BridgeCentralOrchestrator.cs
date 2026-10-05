using System.ComponentModel;
using System.Text.Json.Nodes;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private async Task RunCentralOrchestratorAsync(ChatViewModel host, LiveBridge bridge, ChatViewModel[] managers,
        ChatViewModel[] previousManagers, BridgeAgentConfiguration configuration)
    {
        var work = WorkFor(bridge.Panes);
        var central = work.CentralPlan!;
        using var cancellation = central.Cancellation!;
        var panes = bridge.Panes.ToArray();
        var status = new BannerItem { Level = "info", Text = "Central orchestrator is dividing the task. " +
            $"{ProviderModelCatalog.DisplayName(configuration.Provider)} · {configuration.Model ?? "default model"} · Thinking: {configuration.Effort ?? "model default"}. Your orchestrators will start after the handoff." };
        host.Items.Add(status);
        foreach (var manager in managers)
        {
            manager.BridgeTaskName = "Awaiting central assignment";
            manager.BridgeActivitySummary = "Waiting for the central orchestrator to divide the task";
        }
        try
        {
            var groups = managers.Select(manager => new BridgeOrchestratorGroup(manager.BridgeAgentId,
                panes.Count(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId),
                work.OrchestratorObjectives.GetValueOrDefault(manager.BridgeAgentId, manager.BridgeOrchestrationScope),
                previousManagers.Contains(manager) && manager.BridgeOrchestrationScope.Length > 0 ? manager.BridgeOrchestrationScope : null)).ToArray();
            var assignments = await BridgeCentralOrchestratorService.DivideAsync(host, configuration, groups, cancellation.Token, waiting =>
            {
                var at = host.Items.IndexOf(status);
                status = new BannerItem { Level = "info", Text = waiting
                    ? "Central planner is waiting for its usage limit to reset. The team will continue automatically."
                    : "Central planner is continuing the task division." };
                if (at >= 0) host.Items[at] = status;
            });
            // The planning provider has been disposed before its result is handed to any permanent group.
            await WaitForCentralRosterAsync(panes, cancellation.Token);
            if (!TryGetLiveBridge(host, out var live) || !ReferenceEquals(WorkFor(live.Panes), work) ||
                !live.Panes.SequenceEqual(panes) || !managers.All(m => m.IsBridgeManager && m.CanAcceptBridgeObjective))
                throw new InvalidOperationException("The team changed before the central handoff. Start a new team with the current roster.");
            central.Assignments = assignments.ToList();
            central.State = "handing_off";
            work.ConfirmedVersions.Clear();
            foreach (var manager in managers)
            {
                var assignment = assignments.Single(a => a.AgentId == manager.BridgeAgentId);
                work.Scopes[manager.BridgeAgentId] = assignment.Scope;
                manager.BridgeOrchestrationScope = assignment.Scope;
                manager.BridgeScopeConfirmed = false;
                manager.BridgeTaskName = assignment.TaskName;
                manager.BridgeActivitySummary = "Preparing its assigned scope and worker plan";
            }
            SaveWork(live); // Save the whole division before sending any assignment.
            RebriefBridgeGroups(live);
            var division = string.Join("\n", managers.Select(m =>
                $"{m.BridgeTerminalIdentity} ({m.BridgeAgentId}): {assignments.Single(a => a.AgentId == m.BridgeAgentId).Scope}"));
            foreach (var manager in managers)
            {
                var assignment = assignments.Single(a => a.AgentId == manager.BridgeAgentId);
                var count = panes.Count(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId);
                var prompt = $"[BRIDGE ORCHESTRATOR] Central orchestrator assignment: {assignment.TaskName}\n" +
                    $"Coordinate {count} worker AI(s) only within your assigned scope. The central orchestrator has divided the task.\n\n" +
                    assignment.Instructions + "\n\nTeam division (context for boundaries):\n" + division +
                    "\n\nPublish your group's nonempty worker plan with bridge_set_plan using the current version from bridge_list_tasks. " +
                    "Do not negotiate or take over another group's scope. The app opens dispatch after every group publishes its plan. " +
                    "If coordination_ready is false, finish your turn; the app will notify you when all plans are ready.";
                if (!manager.SendBridgeObjective(prompt))
                    throw new InvalidOperationException($"The assignment could not reach {manager.BridgeTerminalIdentity}. Start a new team after reconnecting its account.");
            }
            central.State = "completed";
            SaveWork(live);
            host.Items.Remove(status);
            host.Items.Add(new BannerItem { Level = "info", Text =
                $"Central orchestrator has been removed. Reason: assigned distinct tasks to all {managers.Length} orchestrators; task done." });
            RaiseRosterUi(BridgePanes.Contains(host) ? null : live);
            RequestSave();
        }
        catch (Exception ex)
        {
            central.State = ex is OperationCanceledException ? "interrupted" : "failed";
            central.Error = ex is OperationCanceledException ? "The team was closed or changed before the handoff finished." : ex.Message;
            var shortReason = ex is CentralPlannerException planner ? planner.ShortReason
                : ex is OperationCanceledException ? "the team changed" : null;
            work.ConfirmedVersions.Clear();
            foreach (var manager in managers.Where(m => ReferenceEquals(m.BridgeWork, work)))
            {
                manager.BridgeScopeConfirmed = false;
                manager.BridgeTaskName = "Assignment paused";
                manager.BridgeActivitySummary = shortReason is null
                    ? "Central assignment did not finish. Start a new team to retry."
                    : $"Central assignment failed: {shortReason}. Start a new team to retry.";
            }
            host.Items.Remove(status);
            host.Items.Add(new BannerItem { Level = "warn", Text = "Central orchestrator removed before completing the handoff. " + central.Error +
                " Worker dispatch is paused. Start a new team to retry." });
            if (TryGetLiveBridge(host, out var live) && ReferenceEquals(WorkFor(live.Panes), work))
            {
                SaveBridge(live.Panes, live.Board);
                RaiseRosterUi(BridgePanes.Contains(host) ? null : live);
                RequestSave();
            }
        }
        finally { central.Cancellation = null; }
    }

    private static async Task WaitForCentralRosterAsync(ChatViewModel[] panes, CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (panes.Where(p => p.Status is "error" or "closed").Select(p => $"{p.BridgeTerminalIdentity} ({p.Status})").ToArray() is { Length: > 0 } lost)
                ready.TrySetException(new CentralPlannerException($"The central orchestrator finished the split, but {string.Join(", ", lost)} " +
                    "disconnected before its assignment could be delivered. Reconnect it and start a new team.", "a team session disconnected"));
            else if (panes.All(p => p.SessionId is not null && p.CanAcceptBridgeObjective)) ready.TrySetResult();
        }
        PropertyChangedEventHandler changed = (_, _) => Check();
        foreach (var pane in panes) pane.PropertyChanged += changed;
        try
        {
            Check();
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Name the panes that held it up: the bare TimeoutException only said "The operation has timed out."
            var waiting = panes.Where(p => p.SessionId is null || !p.CanAcceptBridgeObjective).Select(p =>
                $"{p.BridgeTerminalIdentity} ({(p.SessionId is null ? "its session never finished starting" : $"still {p.Status}, not ready for an objective")})");
            throw new CentralPlannerException("The central orchestrator finished the split, but not every team session could receive " +
                $"its assignment within 2 minutes: {string.Join(", ", waiting)}. Let every pane finish starting or go idle, then start a new team.",
                "a team session was not ready for its assignment");
        }
        finally { foreach (var pane in panes) pane.PropertyChanged -= changed; }
    }

    private static JsonObject? CentralPlanJson(BridgeWorkState work) => work.CentralPlan is not { } central ? null : new()
    {
        ["state"] = central.State, ["provider"] = central.Provider, ["model"] = central.Model, ["effort"] = central.Effort,
        ["removed"] = central.State != "planning", ["error"] = central.Error,
        ["assignments"] = new JsonArray(central.Assignments.Select(a => (JsonNode?)new JsonObject
        {
            ["agent_id"] = a.AgentId, ["task_name"] = a.TaskName, ["scope"] = a.Scope,
            ["exclusions"] = a.Exclusions, ["verification"] = a.Verification, ["handoffs"] = a.Handoffs,
            ["plan_published"] = central.PublishedPlans.Contains(a.AgentId),
        }).ToArray()),
    };

    private void ConfirmCentralPlans(LiveBridge bridge, ChatViewModel caller)
    {
        var work = WorkFor(bridge.Panes);
        if (work.CentralPlan is not { State: "completed" } central) return;
        var managers = bridge.Panes.Where(p => p.IsBridgeManager).ToArray();
        if (central.Assignments.Count != managers.Length ||
            !managers.All(m => central.Assignments.Any(a => a.AgentId == m.BridgeAgentId) && central.PublishedPlans.Contains(m.BridgeAgentId))) return;
        foreach (var manager in managers)
        {
            work.ConfirmedVersions[manager.BridgeAgentId] = work.PlanVersion;
            manager.BridgeScopeConfirmed = true;
        }
        SaveWork(bridge);
        foreach (var manager in managers.Where(m => !ReferenceEquals(m, caller)))
            SendManagerUpdate(manager, "Every orchestrator has published its worker plan for the central division. " +
                "Read bridge_list_tasks for the current plan, then dispatch only your own group's tasks within its assigned scope.");
    }
}
