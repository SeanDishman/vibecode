using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static int PlanReminders(ChatViewModel manager) => Session(manager).Sent.Count(
        text => text.Contains("Your plan is not fully done.", StringComparison.Ordinal));

    private static (MainViewModel Vm, ChatViewModel[] Team, BridgeWorkState Work) IdlePlanTeam(string name)
    {
        var (vm, team) = WorkTeam("idle-plan-" + name, 2);
        team[0].Status = "running";
        Tool(team[0], "bridge_set_plan", new()
        {
            ["plan_version"] = 1,
            ["steps"] = new JsonArray(PlanStep("implement", "Implement feature", team[1]),
                PlanStep("verify", "Verify feature", team[1], "implement")),
        });
        return (vm, team, (BridgeWorkState)Property(team[0], "BridgeWork")!);
    }

    private static void VerifyIdleBridgePlans()
    {
        VerifyIdlePlanProgress();
        VerifyIdlePlanDualOrchestrators();
        VerifyIdlePlanHolds();
        VerifyIdlePlanStopAndRecovery();
        VerifyIdlePlanQueueAndScope();
        VerifyIdlePlanCentralAndParked();
    }

    private static void VerifyIdlePlanProgress()
    {
        var (vm, team, work) = IdlePlanTeam("progress");
        var manager = team[0]; var worker = team[1];
        FinishWork(manager, "Everything is done.");
        PumpUntil(() => PlanReminders(manager) == 1);
        var reminder = Session(manager).Sent.Last();
        Check("unfinished plan wakes an idle orchestrator with an actual provider turn", manager.Status == "running");
        Check("reminder names the idle team and exact unfinished steps", reminder.Contains("No AIs are working")
            && reminder.Contains("0 of your 2 planned steps") && reminder.Contains("implement: Implement feature (planned)")
            && reminder.Contains("verify: Verify feature (planned)"));
        Check("reminder preserves durable recovery and review limits", reminder.Contains("bridge_list_tasks")
            && reminder.Contains("bridge_retry_task") && reminder.Contains("inspect existing work")
            && reminder.Contains("does not authorize another review pass"));
        Check("reminder is app traffic, not a user prompt", (bool)Call(manager, "IsSystemInjectedPrompt", "[MANAGER UPDATE] Your plan is not fully done.")!);
        Check("worker assignment is app traffic and cannot undo Stop", (bool)Call(manager, "IsSystemInjectedPrompt", "[FROM ORCHESTRATOR Agent 1] Task implement")!);
        Check("idle reminder never dispatches unassigned work itself", Session(worker).Sent.Count == 0 && work.Tasks.All(t => t.State == "planned"));

        FinishWork(manager, "I still think this is done.");
        for (var i = 0; i < 4; i++) Call(vm, "OnBridgeManagerStatusChanged", manager);
        Settle();
        Check("unchanged plan receives only one reminder despite repeated idle events", PlanReminders(manager) == 1 && manager.Status == "idle");
        work.Tasks[0].Summary = "Updated activity, no task progress";
        work.Tasks[0].UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        Call(vm, "OnBridgeManagerStatusChanged", manager);
        Settle();
        Check("summary and timestamp updates cannot create a reminder loop", PlanReminders(manager) == 1);

        Tool(manager, "bridge_dispatch_task", Assignment("implement", worker));
        PumpUntil(() => Session(worker).Sent.Count == 1);
        FinishWork(worker);
        PumpUntil(() => manager.Status == "running");
        Check("worker result arrives before another idle-plan reminder", PlanReminders(manager) == 1);
        FinishWork(manager, "The implementation is done.");
        PumpUntil(() => PlanReminders(manager) == 2);
        Check("real task progress permits a new reminder for the remaining step", Session(manager).Sent.Last().Contains("1 of your 2 planned steps")
            && work.Tasks.Single(t => t.Id == "implement").State == "completed");
        Tool(manager, "bridge_dispatch_task", Assignment("verify", worker));
        PumpUntil(() => Session(worker).Sent.Count == 2);
        FinishWork(manager, "Waiting for verification.");
        FinishWork(worker);
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager, "All steps complete.");
        Check("completed plan stays idle without another reminder", work.Tasks.All(t => t.State == "completed")
            && PlanReminders(manager) == 2 && manager.Status == "idle");
    }

    private static void VerifyIdlePlanDualOrchestrators()
    {
        var (vm, team) = WorkTeam("idle-plan-dual", 4);
        var a = team[0]; var b = team[2];
        Property(b, "IsBridgeManager", true); Property(b, "BridgeCoordinatesOnly", true);
        Property(team[3], "BridgeCoordinatorAgentId", b.BridgeAgentId);
        a.Status = b.Status = "running";
        Tool(a, "bridge_set_plan", new()
        {
            ["plan_version"] = 1,
            ["steps"] = new JsonArray(PlanStep("a-step", "First group task", team[1]))
        });
        Tool(b, "bridge_set_plan", new()
        {
            ["plan_version"] = 2,
            ["steps"] = new JsonArray(PlanStep("b-step", "Second group task", team[3]))
        });
        FinishWork(a, "Done with my group.");
        Check("one idle group is not nudged while another orchestrator works", PlanReminders(a) == 0);
        FinishWork(b, "Done with my group too.");
        PumpUntil(() => PlanReminders(a) == 1 && PlanReminders(b) == 1);
        Check("both orchestrators wake from the same all-idle snapshot", a.IsWorking && b.IsWorking);
        Check("each orchestrator sees only its own unfinished scope", Session(a).Sent.Last().Contains("a-step: First group task")
            && !Session(a).Sent.Last().Contains("b-step: Second group task")
            && Session(b).Sent.Last().Contains("b-step: Second group task"));
        Check("dual reminders do not wake workers", Session(team[1]).Sent.Count == 0 && Session(team[3]).Sent.Count == 0);
        FinishWork(a); FinishWork(b);
        Check("dual orchestrators do not keep waking one another", PlanReminders(a) == 1 && PlanReminders(b) == 1);

        var work = (BridgeWorkState)Property(a, "BridgeWork")!;
        work.Tasks.Single(t => t.CoordinatorId == b.BridgeAgentId).State = "completed";
        Call(vm, "OnBridgeManagerStatusChanged", a);
        PumpUntil(() => PlanReminders(a) == 2);
        Check("finished group stays asleep when another scope is unfinished", PlanReminders(b) == 1 && b.Status == "idle");
        FinishWork(a);
    }

    private static void VerifyIdlePlanHolds()
    {
        foreach (var status in new[] { "starting", "preparing", "running" })
        {
            var (vm, team, _) = IdlePlanTeam(status);
            team[1].Status = status;
            FinishWork(team[0]);
            Check(status + " peer prevents premature idle-plan reminder", PlanReminders(team[0]) == 0);
            team[1].Status = "idle";
            PumpUntil(() => PlanReminders(team[0]) == 1);
            FinishWork(team[0]);
        }
        foreach (var hold in new[] { "retry", "limit", "compaction", "permission", "steering" })
        {
            var (vm, team, _) = IdlePlanTeam(hold);
            var worker = team[1];
            void SetHold(bool enabled)
            {
                var field = hold switch
                {
                    "retry" => "_bridgeRetrySession",
                    "limit" => "_limitRecoveryResult",
                    "compaction" => "_manualCompactCommand",
                    "steering" => "_steerSubmitting",
                    _ => "_pendingPerms"
                };
                if (hold == "limit") AppSettings.Current.ContinueAfterLimitResets = enabled;
                if (hold == "permission")
                {
                    var pending = (IDictionary)typeof(ChatViewModel).GetField(field, Flags)!.GetValue(worker)!;
                    if (enabled) pending.Add("pending", new PermItem { RequestId = "pending", ToolName = "AskUserQuestion", Owner = worker });
                    else pending.Clear();
                }
                else typeof(ChatViewModel).GetField(field, Flags)!.SetValue(worker, hold switch
                {
                    "retry" => enabled ? Session(worker) : null,
                    "limit" => enabled ? new JsonObject() : null,
                    "compaction" => enabled ? "/compact" : null,
                    _ => (object)enabled,
                });
            }
            SetHold(true);
            FinishWork(team[0]);
            Check(hold + " hold prevents automatic plan reminders", PlanReminders(team[0]) == 0);
            SetHold(false);
            Call(vm, "OnBridgeManagerStatusChanged", worker);
            PumpUntil(() => PlanReminders(team[0]) == 1);
            FinishWork(team[0]);
        }
    }

    private static void VerifyIdlePlanStopAndRecovery()
    {
        var (vm, team, work) = IdlePlanTeam("stop");
        var manager = team[0];
        manager.Send("A user request queued before Stop.");
        manager.Interrupt();
        FinishWork(manager, "Stopped as requested.");
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager);
        Check("user Stop prevents unfinished plan from restarting", PlanReminders(manager) == 0 && Session(manager).Interrupts == 1);
        Check("older queued user input does not re-arm reminders after Stop", (bool)Property(work, "IdlePlanRemindersPaused")!);
        manager.Send("[MANAGER UPDATE] A worker result arrived after Stop.");
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager);
        Check("an app-injected update does not undo user Stop", PlanReminders(manager) == 0);
        Property(manager, "IsPeerNotificationTurn", true);
        manager.Send("Continue the unfinished plan.");
        Property(manager, "IsPeerNotificationTurn", false);
        Check("user input accepted during an app notification re-enables reminders", !(bool)Property(work, "IdlePlanRemindersPaused")!);
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager);
        PumpUntil(() => PlanReminders(manager) == 1);
        Check("new user work re-enables plan reminders", PlanReminders(manager) == 1);
        FinishWork(manager);

        var recovered = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(work))!;
        recovered.Recover();
        foreach (var pane in team) Property(pane, "BridgeWork", recovered);
        Call(vm, "OnBridgeManagerStatusChanged", manager);
        Settle();
        Check("reopening a saved plan cannot silently restart model work", PlanReminders(manager) == 1);
        manager.Send("Resume the saved plan.");
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager);
        PumpUntil(() => PlanReminders(manager) == 2);
        Check("explicit resume re-arms reminders for a recovered plan", PlanReminders(manager) == 2);
        FinishWork(manager);
    }

    private static void VerifyIdlePlanQueueAndScope()
    {
        var (_, team, work) = IdlePlanTeam("queued-dependency");
        var manager = team[0]; var worker = team[1];
        Tool(manager, "bridge_dispatch_task", Assignment("verify", worker));
        work.Tasks.Single(t => t.Id == "implement").State = "completed";
        FinishWork(manager);
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Check("ready queued dependency starts before an idle-plan reminder", PlanReminders(manager) == 0
            && work.Tasks.Single(t => t.Id == "verify").State == "running");
        FinishWork(worker);
        PumpUntil(() => manager.Status == "running");
        FinishWork(manager);

        var (pendingVm, pendingTeam, _) = IdlePlanTeam("pending-message");
        pendingTeam[0].Send("[MANAGER UPDATE] Process this result first.");
        Check("fixture has a queued manager update", pendingTeam[0].HasQueued);
        FinishWork(pendingTeam[0]);
        PumpUntil(() => pendingTeam[0].Status == "running");
        Check("queued result turn takes precedence over reminder", PlanReminders(pendingTeam[0]) == 0);
        FinishWork(pendingTeam[0]);
        PumpUntil(() => PlanReminders(pendingTeam[0]) == 1);
        FinishWork(pendingTeam[0]);

        var (regularVm, regular) = Team("idle-plan-regular", 2);
        var ordinaryWork = new BridgeWorkState { Tasks = [new BridgeWorkTask { Id = "legacy", CoordinatorId = regular[0].BridgeAgentId }] };
        foreach (var pane in regular) { Property(pane, "BridgeWork", ordinaryWork); pane.Status = "idle"; }
        Settle();
        Check("regular bridge peers never receive orchestrator reminders", regular.All(p => Session(p).Sent.Count == 0));

        var (removedVm, removed, _) = IdlePlanTeam("removed");
        removed[0].Status = "idle";
        removedVm.BridgePanes.Clear();
        Settle();
        Check("removing bridge before deferred check prevents a stale wakeup", PlanReminders(removed[0]) == 0);
    }

    private static void VerifyIdlePlanCentralAndParked()
    {
        var (vm, team, work) = IdlePlanTeam("central");
        work.CentralPlan = new BridgeCentralPlan { State = "planning" };
        FinishWork(team[0]);
        Check("central planner activity prevents a premature reminder", PlanReminders(team[0]) == 0);
        work.CentralPlan.State = "handing_off";
        Call(vm, "OnBridgeManagerStatusChanged", team[0]);
        Settle();
        Check("central handoff must finish before reminding its orchestrators", PlanReminders(team[0]) == 0);
        work.CentralPlan.State = "completed";
        Call(vm, "OnBridgeManagerStatusChanged", team[0]);
        PumpUntil(() => PlanReminders(team[0]) == 1);
        Check("completed central handoff allows unfinished-plan reminders", PlanReminders(team[0]) == 1);
        FinishWork(team[0]);

        var (parkedVm, parked, _) = IdlePlanTeam("parked");
        Call(parkedVm, "ParkActiveBridge", true);
        FinishWork(parked[0]);
        PumpUntil(() => PlanReminders(parked[0]) == 1);
        Check("background bridge receives reminders without reopening its surface", parkedVm.BridgePanes.Count == 0
            && PlanReminders(parked[0]) == 1 && Session(parked[1]).Sent.Count == 0);
        FinishWork(parked[0]);
    }
}
