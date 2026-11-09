using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    /// <summary>Fact check orchestrator: once nothing in the Bridge is working, ask each orchestrator that has not
    /// answered for its current request whether it really did everything the user asked.</summary>
    private void ScheduleBridgeFactCheck(ChatViewModel member, LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        if (!work.FactCheckOrchestrators || work.FactCheckScheduled || work.IdlePlanRemindersPaused) return;
        work.FactCheckScheduled = true;
        // The plan reminder's deferral: reports, mailbox turns and dependency dispatches resume work first, and an
        // unfinished-plan reminder queued ahead of this one reaches the idle orchestrator first.
        ApplicationDispatcher.BeginInvoke(() =>
        {
            work.FactCheckScheduled = false;
            if (TryGetLiveBridge(member, out var live) && ReferenceEquals(WorkFor(live.Panes), work))
                FactCheckIdleOrchestrators(live);
        }, DispatcherPriority.ContextIdle);
    }

    private void FactCheckIdleOrchestrators(LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        if (!work.FactCheckOrchestrators || work.IdlePlanRemindersPaused || work.CentralPlan is { State: not "completed" }
            || bridge.Panes.Any(p => !p.SettledForBridgePlanReminder)) return;
        // Snapshot first: asking one orchestrator starts its turn, which must not hide the others from this settled moment.
        var due = bridge.Panes.Where(p => p.IsBridgeManager && p.BridgeCoordinatesOnly && p.Status == "idle"
            && p.CanAcceptBridgeObjective && !work.FactCheckOutcomes.ContainsKey(p.BridgeAgentId)).ToArray();
        foreach (var manager in due)
        {
            var sent = work.FactChecksSent.GetValueOrDefault(manager.BridgeAgentId);
            if (sent >= BridgeFactCheckPolicy.MaxChecks)
            {
                // Asking forever would spend the user's usage on a model that will not answer; hand it back to them.
                work.FactCheckOutcomes[manager.BridgeAgentId] = "unconfirmed";
                manager.Items.Add(new BannerItem { Level = "warn", Text = BridgeFactCheckPolicy.UnconfirmedNotice(manager.BridgeTerminalIdentity) });
                continue;
            }
            // An orchestrator that slept while idle has no provider to ask, and a send would fail it into an error.
            // Waking ends with it idle again, and that settled moment asks it.
            if (manager.IsAsleep)
            {
                manager.Wake();
                continue;
            }
            var prompt = BridgeFactCheckPolicy.Prompt(work.OrchestratorObjectives.GetValueOrDefault(manager.BridgeAgentId) ?? "",
                manager.BridgeOrchestrationScope, sent + 1);
            if (!manager.SendBridgePlanReminder(prompt)) continue;
            work.FactChecksSent[manager.BridgeAgentId] = sent + 1;
            NoteBridgeActivity(BridgePanes.Contains(manager) ? null : bridge);
        }
    }

    /// <summary>An orchestrator's answer to the fact check. Accepted only after the app has asked, so every request is
    /// checked at least once, and done must hold up against the plan and the workers just as review approval does.</summary>
    private JsonObject MarkBridgeTaskDone(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        if (!caller.IsBridgeManager || !caller.BridgeCoordinatesOnly)
            throw new StatusValidationException("Only an Advanced Bridge orchestrator answers the fact check. Workers report to their own orchestrator.");
        var work = WorkFor(bridge.Panes);
        if (!work.FactCheckOrchestrators)
            throw new StatusValidationException("Fact check is off for this Advanced Bridge. Record scope completion with bridge_review_scope.");
        if (work.FactChecksSent.GetValueOrDefault(caller.BridgeAgentId) == 0)
            throw new StatusValidationException("The app has not fact-checked this request yet. Finish the user's task and your turn; once every agent " +
                "has stopped, the app asks you to check it and you answer then.");
        var status = args["status"]!.GetValue<string>();
        if (status == "done")
        {
            if (work.Tasks.Any(t => t.CoordinatorId == caller.BridgeAgentId && t.State != "completed"))
                throw new StatusValidationException("Your plan still has unfinished, blocked or interrupted steps. Finish or recover them first " +
                    "(read bridge_list_tasks), or answer status=blocked if only the user can unblock them.");
            if (caller.HasQueued)
                throw new StatusValidationException("Updates are queued for your next turn. Finish your turn so they arrive, then check them before confirming.");
            if (bridge.Panes.Any(p => p.BridgeCoordinatorAgentId == caller.BridgeAgentId && (p.IsWorking || p.HasQueued)))
                throw new StatusValidationException("Your workers still have active or queued assignments. Finish your turn; their reports arrive " +
                    "automatically. Confirm only after checking them.");
        }
        var summary = args["summary"]!.GetValue<string>().Trim();
        work.FactCheckOutcomes[caller.BridgeAgentId] = status;
        caller.Items.Add(new DividerItem { Label = status == "done" ? "Fact check passed · task done" : "Fact check · needs you" });
        if (status == "blocked")
            caller.Items.Add(new BannerItem { Level = "warn", Text = BridgeFactCheckPolicy.BlockedNotice(caller.BridgeTerminalIdentity, summary) });
        NoteBridgeActivity(BridgePanes.Contains(caller) ? null : bridge);
        return new JsonObject
        {
            ["agent_id"] = caller.BridgeAgentId, ["fact_check"] = status, ["summary"] = summary,
            ["next_step"] = status == "done"
                ? "Report the outcome to the user and finish your turn. Do not start new work or send acknowledgments."
                : "Tell the user exactly what you need, then finish your turn. The app asks nothing more until the user replies.",
        };
    }
}
