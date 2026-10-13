using System.Text.Json;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private void ScheduleBridgePlanReminder(ChatViewModel member, LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        if (work.IdlePlanReminderScheduled || work.IdlePlanRemindersPaused
            || !work.Tasks.Any(t => t.State != "completed")
            || !bridge.Panes.Any(p => p.IsBridgeManager && p.BridgeCoordinatesOnly)) return;

        work.IdlePlanReminderScheduled = true;
        // Worker reports, mailbox turns, and dependency dispatches get the first chance to resume work.
        // One deferred check covers simultaneous completions and runs after ApplyResult finishes updating state.
        ApplicationDispatcher.BeginInvoke(() =>
        {
            work.IdlePlanReminderScheduled = false;
            if (TryGetLiveBridge(member, out var live) && ReferenceEquals(WorkFor(live.Panes), work))
                RemindIdleBridgeOrchestrators(live);
        }, DispatcherPriority.ContextIdle);
    }

    private void RemindIdleBridgeOrchestrators(LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        if (work.IdlePlanRemindersPaused || work.CentralPlan is { State: not "completed" }
            || bridge.Panes.Any(p => !p.SettledForBridgePlanReminder)) return;

        try { StartReadyBridgeTasks(bridge); }
        catch (StatusValidationException ex)
        {
            bridge.Panes.FirstOrDefault(p => p.IsBridgeManager)?.Items.Add(
                new BannerItem { Level = "warning", Text = ex.Message });
            return;
        }
        if (bridge.Panes.Any(p => !p.SettledForBridgePlanReminder)) return;

        // Ignore report text and timestamps: repeated status/activity updates must not buy another turn.
        // Include other groups' progress because it can unblock this orchestrator's dependencies.
        var signature = JsonSerializer.Serialize(new
        {
            work.PlanVersion,
            CoordinationReady = CoordinationReady(bridge.Panes),
            Tasks = work.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal)
                .Select(t => new { t.Id, t.CoordinatorId, t.OwnerId, t.State, t.Attempt }),
        });
        // Snapshot all eligible orchestrators before sending: waking the first must not exclude the second.
        var reminders = bridge.Panes.Where(p => p.IsBridgeManager && p.BridgeCoordinatesOnly
                && p.Status == "idle" && p.CanAcceptBridgeObjective
                && work.Tasks.Any(t => t.CoordinatorId == p.BridgeAgentId && t.State != "completed")
                && work.IdlePlanReminderSignatures.GetValueOrDefault(p.BridgeAgentId) != signature)
            .ToArray();
        foreach (var manager in reminders)
        {
            var unfinished = work.Tasks.Where(t => t.CoordinatorId == manager.BridgeAgentId && t.State != "completed").ToArray();
            var total = work.Tasks.Count(t => t.CoordinatorId == manager.BridgeAgentId);
            var steps = string.Join("\n", unfinished.Take(20).Select(t => $"- {t.Id}: {t.Title} ({t.State})"));
            var more = unfinished.Length > 20 ? $"\n...and {unfinished.Length - 20} more; read bridge_list_tasks for the full plan." : "";
            var message = "[MANAGER UPDATE] Your plan is not fully done. No AIs are working in this Advanced Bridge, " +
                "and there are no pending turns or automatic recovery attempts. " +
                $"Only {total - unfinished.Length} of your {total} planned steps are complete.\n\n" + steps + more + "\n\n" +
                "Read bridge_list_tasks and resume your unfinished steps within your assigned scope. " +
                "Dispatch ready steps to your own workers with bridge_dispatch_task. For blocked, failed or interrupted " +
                "assignments, inspect existing work before using bridge_retry_task; do not repeat successful actions. " +
                "Keep the saved plan and task IDs, and record real outcomes with bridge_update_task. " +
                "If progress requires user input or an unavailable dependency, explain the specific blocker to the user. " +
                "Respect the existing review budget; this reminder does not authorize another review pass. " +
                "Record scope completion with bridge_review_scope only when all required work is complete.";
            if (!manager.SendBridgePlanReminder(message)) continue;
            work.IdlePlanReminderSignatures[manager.BridgeAgentId] = signature;
            NoteBridgeActivity(BridgePanes.Contains(manager) ? null : bridge);
        }
    }
}
