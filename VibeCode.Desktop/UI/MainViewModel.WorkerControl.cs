using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.UI;

public sealed partial class MainViewModel
{
    private ChatViewModel ManagedWorker(ChatViewModel caller, LiveBridge bridge, JsonObject args, bool requireReady)
    {
        if (!caller.IsBridgeManager || !caller.BridgeCoordinatesOnly)
            throw new StatusValidationException("Only an Advanced Bridge orchestrator can stop or restart its own workers.");
        var id = EditAgentId(caller, bridge, args["recipient"]!.GetValue<string>());
        var worker = bridge.Panes.FirstOrDefault(p => p.BridgeAgentId == id)
            ?? throw new StatusValidationException("Worker is not in this bridge. Refresh bridge_list_agents.");
        RequireOwnedWorker(caller, worker, bridge, requireReady);
        if (worker.Status == "closed") throw new StatusValidationException("The worker's chat is closed. Reopen it first.");
        return worker;
    }

    private JsonObject StopBridgeWorker(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        // Stopping does not need scope agreement: an orchestrator may halt its own workers during recovery too.
        var worker = ManagedWorker(caller, bridge, args, requireReady: false);
        var work = WorkFor(bridge.Panes);
        var reason = args["reason"]!.GetValue<string>().Trim();
        var newlyPaused = work.PausedWorkers.Add(worker.BridgeAgentId);
        foreach (var task in work.Tasks.Where(t => t.OwnerId == worker.BridgeAgentId && t.Active))
        {
            task.State = "interrupted";
            task.Summary = "Stopped by orchestrator: " + reason;
            task.UpdatedAt = DateTimeOffset.UtcNow;
        }
        string? warning = null;
        try { SaveWork(bridge); }
        catch (StatusValidationException ex) { warning = ex.Message; }
        // A journal failure must never prevent a requested stop. Report it; restarting still requires a saved journal.
        if (newlyPaused) worker.StopManagedWorker(reason);
        var result = WorkerControlJson(worker);
        result["journal_warning"] = warning;
        result["next_step"] = "The chat and existing work are retained. Wait for stopping to finish, inspect existing changes, then use bridge_retry_task with optional revised message for interrupted work. Use bridge_resume_worker to release queued work without replaying interrupted tasks.";
        return result;
    }

    private JsonObject ResumeBridgeWorker(ChatViewModel caller, LiveBridge bridge, JsonObject args)
    {
        var worker = ManagedWorker(caller, bridge, args, requireReady: true);
        if (worker.ManagedWorkerResumeError is { } error) throw new StatusValidationException(error);
        var work = WorkFor(bridge.Panes);
        if (work.Tasks.Any(t => t.OwnerId == worker.BridgeAgentId && t.State == "interrupted"))
            throw new StatusValidationException("Inspect this worker's interrupted assignments, then restart with bridge_retry_task(task_id, message). Resume never silently replays interrupted work.");
        var paused = work.PausedWorkers.Remove(worker.BridgeAgentId);
        try { SaveWork(bridge); }
        catch { if (paused) work.PausedWorkers.Add(worker.BridgeAgentId); throw; }
        worker.ResumeManagedWorker();
        StartReadyBridgeTasks(bridge);
        var result = WorkerControlJson(worker);
        result["next_step"] = "Queued work can run. For a new assignment, use bridge_dispatch_task; resuming an idle worker with no queued work does not invent a task.";
        return result;
    }

    private static JsonObject WorkerControlJson(ChatViewModel worker) => new()
    {
        ["agent_id"] = worker.BridgeAgentId,
        ["paused"] = worker.BridgeWorkerPaused,
        ["stop_pending"] = worker.BridgeWorkerPaused && worker.Status is "running" or "preparing",
        ["status"] = worker.Status,
        ["chat_preserved"] = true,
    };
}
