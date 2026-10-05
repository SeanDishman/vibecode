using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private static BridgeWorkState WorkFor(IReadOnlyList<ChatViewModel> panes)
    {
        var work = panes.Select(p => p.BridgeWork).FirstOrDefault(w => w is not null) ?? new BridgeWorkState();
        foreach (var pane in panes) pane.BridgeWork = work;
        return work;
    }

    private static List<string> TaskStrings(JsonNode? node) => node is JsonArray list
        ? list.Select(n => n!.GetValue<string>().Trim()).Distinct(StringComparer.Ordinal).ToList() : new();

    private static JsonObject TaskJson(BridgeWorkTask task, BridgeWorkState work) => new()
    {
        ["task_id"] = task.Id, ["coordinator_id"] = task.CoordinatorId, ["owner_id"] = task.OwnerId,
        ["task_name"] = task.Title, ["status"] = task.State, ["summary"] = task.Summary, ["evidence"] = task.Evidence,
        ["attempt"] = task.Attempt, ["instruction"] = task.Instruction,
        ["dependencies"] = new JsonArray(task.Dependencies.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        ["dependencies_complete"] = work.Ready(task),
        ["files"] = new JsonArray(task.Files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        ["updated_at"] = task.UpdatedAt.ToString("O"),
    };

    private static JsonObject ListBridgeTasks(LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        return new()
        {
            ["run_id"] = work.RunId, ["plan_version"] = work.PlanVersion,
            ["coordination_mode"] = work.CentralPlan is null ? "peer_agreement" : "central_assignment",
            ["central_orchestrator"] = CentralPlanJson(work),
            ["coordination_ready"] = CoordinationReady(bridge.Panes),
            ["orchestrator_communication_locked"] = work.DispatchStarted,
            ["completed_steps"] = work.Tasks.Count(t => t.State == "completed"), ["total_steps"] = work.Tasks.Count,
            ["file_activity_policy"] = "Advisory only. Every agent may edit shared files; these are not exclusive claims or locks.",
            ["tasks"] = new JsonArray(work.Tasks.Select(t => (JsonNode?)TaskJson(t, work)).ToArray()),
        };
    }

    private static void SaveWork(LiveBridge bridge)
    {
        if (!SnapshotBridge(bridge.Panes, bridge.Board))
            throw new StatusValidationException("Wait for the bridge sessions to connect before saving assignments.");
        if (AppSettings.Current.TrySave() is { } error)
            throw new StatusValidationException("Could not save the bridge journal; no further work will start until storage is available. " + error.Message);
        foreach (var pane in bridge.Panes) pane.RaiseBridgeProgress();
    }

    private JsonObject SetBridgePlan(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        if (!caller.IsBridgeManager) throw new StatusValidationException("Only orchestrators publish plan steps.");
        var work = WorkFor(bridge.Panes);
        if (work.DispatchStarted) throw new StatusValidationException("The agreed plan is frozen after dispatch. Dispatch corrections within your existing scope as new tasks.");
        if (work.CentralPlan is { } pending && (pending.State != "completed" || pending.Assignments.All(a => a.AgentId != caller.BridgeAgentId)))
            throw new StatusValidationException("Wait for your central assignment before publishing a worker plan. Read bridge_list_tasks for the handoff state.");
        if (args["plan_version"]!.GetValue<int>() != work.PlanVersion)
            throw new StatusValidationException($"Stale plan version. Read bridge_list_tasks; current plan_version is {work.PlanVersion}.");
        var steps = args["steps"]!.AsArray().Select(row => new BridgeWorkTask
        {
            Id = row!["id"]!.GetValue<string>(), Title = row["title"]!.GetValue<string>(),
            CoordinatorId = caller.BridgeAgentId, OwnerId = row["owner_id"]!.GetValue<string>(),
            Dependencies = TaskStrings(row["dependencies"]), Files = TaskStrings(row["files"]),
        }).ToList();
        if (work.CentralPlan is not null && steps.Count == 0)
            throw new StatusValidationException("Publish at least one concrete worker step for your central assignment before dispatch can open.");
        if (steps.Any(t => !bridge.Panes.Any(p => p.BridgeAgentId == t.OwnerId && !p.IsBridgeManager &&
            ReferenceEquals(CoordinatorFor(p, bridge.Panes), caller))))
            throw new StatusValidationException("Each planned step must belong to one of your group's workers.");
        var candidate = work.Tasks.Where(t => t.CoordinatorId != caller.BridgeAgentId).Concat(steps).ToList();
        try { BridgeWorkState.ValidateGraph(candidate); }
        catch (InvalidOperationException ex) { throw new StatusValidationException(ex.Message); }
        work.Tasks = candidate;
        work.PlanVersion++;
        work.ConfirmedVersions.Clear();
        work.CentralPlan?.PublishedPlans.Add(caller.BridgeAgentId);
        foreach (var manager in bridge.Panes.Where(p => p.IsBridgeManager)) manager.BridgeScopeConfirmed = false;
        ConfirmCentralPlans(bridge, caller);
        ReconcileBridgeGroups(bridge.Panes);
        SaveWork(bridge);
        return ListBridgeTasks(bridge);
    }

    private JsonObject DispatchDurableTask(ChatViewModel manager, LiveBridge bridge, JsonObject args)
    {
        if (!manager.IsBridgeManager) throw new StatusValidationException("Only orchestrators assign work to their own workers.");
        var worker = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == args["recipient"]!.GetValue<string>());
        if (worker is null) throw new StatusValidationException("Worker is not in this bridge. Refresh bridge_list_agents.");
        RequireOwnedWorker(manager, worker, bridge);
        if (worker.Status is "closed" or "error") throw new StatusValidationException("Worker is not accepting work. Recover its session before dispatching.");
        var work = WorkFor(bridge.Panes);
        var id = args["task_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var task = work.Tasks.FirstOrDefault(t => t.Id == id);
        var body = args["message"]!.GetValue<string>();
        if (task is not null && (task.CoordinatorId != manager.BridgeAgentId || task.OwnerId != worker.BridgeAgentId))
            throw new StatusValidationException("This task ID belongs to another owner or group.");
        var dependencies = args.ContainsKey("dependencies") ? TaskStrings(args["dependencies"]) : task?.Dependencies ?? new();
        if (task is { State: "planned" } && !task.Dependencies.SequenceEqual(dependencies))
            throw new StatusValidationException("Dispatch must preserve the published dependencies. Revise the plan with bridge_set_plan before agreement and dispatch.");
        if (task is { State: not "planned" })
        {
            if (task.Instruction != body || !task.Dependencies.SequenceEqual(dependencies))
                throw new StatusValidationException("A dispatched task ID cannot be reused for a different assignment. Use a new ID for new work, or bridge_retry_task for recovery.");
            // A failed journal write may have left a queued assignment in memory. Retry its save, never duplicate it.
            if (task.State == "queued") { SaveWork(bridge); StartReadyBridgeTasks(bridge); }
            return TaskJson(task, work);
        }
        var isNew = task is null;
        task ??= new BridgeWorkTask { Id = id, CoordinatorId = manager.BridgeAgentId, OwnerId = worker.BridgeAgentId };
        var oldDependencies = task.Dependencies;
        task.Dependencies = dependencies;
        try { BridgeWorkState.ValidateGraph(isNew ? work.Tasks.Append(task).ToList() : work.Tasks); }
        catch (InvalidOperationException ex) { task.Dependencies = oldDependencies; throw new StatusValidationException(ex.Message); }
        if (isNew) task.Title = args["task_name"]!.GetValue<string>().Trim();
        task.Instruction = body;
        if (args.ContainsKey("files")) task.Files = TaskStrings(args["files"]);
        task.State = "queued";
        task.UpdatedAt = DateTimeOffset.UtcNow;
        if (isNew) work.Tasks.Add(task);
        work.DispatchStarted = true;
        InvalidateBridgeReview(worker);
        SaveWork(bridge); // Journal both the assignment and permanent communication lock before sending anything.
        StartReadyBridgeTasks(bridge);
        manager.Items.Add(new DividerItem { Label = $"Assigned {worker.BridgeTerminalIdentity} · {task.Title}" });
        NoteBridgeActivity(BridgePanes.Contains(manager) ? null : bridge);
        var result = TaskJson(task, work);
        result["agent_id"] = worker.BridgeAgentId;
        result["next_step"] = "Finish your turn after assigning available work. Results arrive automatically; do not poll while waiting. Dependencies release queued work when their actual tasks complete.";
        return result;
    }

    private void StartReadyBridgeTasks(LiveBridge bridge)
    {
        var work = WorkFor(bridge.Panes);
        foreach (var task in work.Tasks.Where(t => t.State == "queued").ToArray())
        {
            if (!work.Ready(task) || work.Tasks.Any(t => t.OwnerId == task.OwnerId && t.Active)) continue;
            var worker = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == task.OwnerId);
            var manager = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == task.CoordinatorId && p.IsBridgeManager);
            if (worker is null || manager is null || worker.Status != "idle" || worker.HasQueued) continue;
            task.State = "dispatching";
            task.Attempt++;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            try { SaveWork(bridge); }
            catch
            {
                task.State = "interrupted";
                task.Summary = "The assignment was not sent because its journal could not be saved. Restore storage access, then explicitly retry.";
                throw;
            }
            var wire = $"[FROM ORCHESTRATOR {manager.BridgeTerminalIdentity}] Task {task.Id}: {task.Title}\n\n{task.Instruction}\n\n" +
                $"Use bridge_update_task with task_id={task.Id} for actual progress and a final outcome. " +
                "File activity is advisory: other agents may edit the same files, and you may edit theirs. Coordinate overlapping edits with workers. " +
                "For substantial work, include useful checks/results or file references as evidence when available. Evidence is optional for simple work. " +
                "End with a factual report of changes, checks and blockers; the app relays it to your orchestrator. " +
                (task.Attempt > 1 ? "RECOVERY: inspect existing work first; this assignment may already have produced side effects before interruption.\n" : "");
            worker.BridgeTaskName = task.Title;
            worker.BridgeActivitySummary = task.Instruction.Length <= 180 ? task.Instruction : task.Instruction[..177] + "…";
            try
            {
                if (!worker.Send(wire)) throw new InvalidOperationException("Worker is not accepting input.");
                task.State = "running";
                ResetPeerChain(worker);
                NoteDispatch(worker, task.Instruction, null);
            }
            catch (Exception ex)
            {
                task.State = "interrupted";
                task.Summary = "Dispatch was not confirmed: " + ex.Message;
            }
            SaveWork(bridge);
        }
    }

    private JsonObject UpdateBridgeTask(ChatViewModel caller, LiveBridge bridge, JsonObject args, bool retry)
    {
        var work = WorkFor(bridge.Panes);
        var task = work.Tasks.FirstOrDefault(t => t.Id == args["task_id"]!.GetValue<string>())
            ?? throw new StatusValidationException("Unknown task ID. Read bridge_list_tasks.");
        var coordinator = caller.IsBridgeManager && task.CoordinatorId == caller.BridgeAgentId;
        if (!coordinator && (retry || task.OwnerId != caller.BridgeAgentId))
            throw new StatusValidationException("Only the task owner or its orchestrator may update it; only its orchestrator may retry it.");
        if (retry)
        {
            if (task.State is not ("failed" or "interrupted" or "blocked") || task.Instruction.Length == 0)
                throw new StatusValidationException("Only blocked, failed or interrupted assignments can be retried.");
            var previousOwner = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == task.OwnerId);
            if (previousOwner?.IsWorking == true) throw new StatusValidationException("Wait for the original owner's current turn to end before retrying.");
            var ownerId = args["recipient"]?.GetValue<string>() ?? task.OwnerId;
            var worker = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == ownerId);
            if (worker is null) throw new StatusValidationException("The task owner has left this bridge.");
            RequireOwnedWorker(caller, worker, bridge);
            if (worker.IsWorking) throw new StatusValidationException("Wait for the owner's current turn to end before retrying.");
            task.OwnerId = worker.BridgeAgentId;
            task.State = "queued";
            task.Evidence = "";
            InvalidateBridgeReview(worker);
        }
        else
        {
            var state = args["status"]!.GetValue<string>();
            if (state is not ("running" or "blocked" or "completed" or "failed")) throw new StatusValidationException("status must be running, blocked, completed or failed.");
            if (task.State == "completed" && state != "completed") throw new StatusValidationException("Completed tasks are immutable; dispatch a new task for corrections.");
            if (task.State is "planned" or "queued" or "interrupted" || !work.Ready(task))
                throw new StatusValidationException("This task has not started or requires explicit recovery. Dependencies must complete before progress can be recorded.");
            task.State = state;
            task.Summary = args["summary"]!.GetValue<string>();
            if (args["evidence"] is not null) task.Evidence = args["evidence"]!.GetValue<string>();
            if (args.ContainsKey("files")) task.Files = TaskStrings(args["files"]);
        }
        task.UpdatedAt = DateTimeOffset.UtcNow;
        SaveWork(bridge);
        ScheduleReadyTasks(caller);
        return TaskJson(task, work);
    }

    private void ScheduleReadyTasks(ChatViewModel member) => ApplicationDispatcher.BeginInvoke(() =>
    {
        try { if (TryGetLiveBridge(member, out var live)) StartReadyBridgeTasks(live); }
        catch (StatusValidationException ex)
        { member.Items.Add(new BannerItem { Level = "warning", Text = ex.Message }); }
    }, DispatcherPriority.Background);

    private static Dispatcher ApplicationDispatcher => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    private void RecordBridgeTaskTurn(ChatViewModel worker, LiveBridge bridge, bool turnEnded)
    {
        if (worker.IsBridgeManager || worker.IsPeerNotificationTurn || worker.WaitingForLimitReset || worker.IsCompactingContext) return;
        var work = WorkFor(bridge.Panes);
        var task = work.Tasks.FirstOrDefault(t => t.OwnerId == worker.BridgeAgentId && t.Active);
        if (task is not null && (turnEnded || worker.Status is "error" or "closed"))
        {
            var report = worker.LastTurnReplyText().Trim();
            task.State = worker.Status == "error" || worker.BridgeTaskState == "failed" ? "failed" : worker.BridgeTaskState == "interrupted" || report.Length == 0 ? "interrupted" : "completed";
            task.Summary = report.Length > 1800 ? report[^1800..] : report;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            try { SaveWork(bridge); }
            catch (StatusValidationException ex)
            { worker.Items.Add(new BannerItem { Level = "warning", Text = ex.Message }); }
        }
        if (worker.Status == "idle") ScheduleReadyTasks(worker);
    }

    private void DeleteBridgeMailboxForChat(ChatViewModel chat)
    {
        try
        {
            chat.PeerMailbox?.Store.DeleteAgent(chat.BridgeAgentId);
            foreach (var saved in AppSettings.Current.SavedBridges.Where(b => b.Work is not null &&
                (b.HostSessionId == chat.SessionId || b.Peers.Any(p => p.SessionId == chat.SessionId))))
            {
                var id = saved.HostSessionId == chat.SessionId ? saved.HostAgentId : saved.Peers.First(p => p.SessionId == chat.SessionId).AgentId;
                if (id is not null) new BridgeMailboxStore(saved.Cwd, saved.Work!.RunId).DeleteAgent(id);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            chat.Items.Add(new BannerItem { Level = "warning", Text = "Mailbox cleanup will retry on the next sweep: " + ex.Message });
        }
    }
}
