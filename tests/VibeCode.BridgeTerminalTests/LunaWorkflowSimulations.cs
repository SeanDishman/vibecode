using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunLunaWorkflowCase(int index)
    {
        var common = "Tiny text-only bridge simulation. Use only bridge tools, no shell, files, web, status-board edits or native subagents. " +
            "Keep every message brief. Publish your structured plan before dispatch. Use the planned task IDs on dispatch. " +
            "Workers must record completed or blocked outcomes with bridge_update_task. Finish your turn after dispatching; do not poll waiting for results. " +
            "Your workers' requested checks ARE the final verification; do not invent additional assignments. " +
            "Once all workers are idle and their real reports arrive, you MUST call bridge_review_scope with verdict=approved and a summary of the actual checks BEFORE returning the final marker. " +
            "That tool records existing verification; it is not an extra review assignment. If the tool says reports are queued or workers are busy, finish your turn and handle the next real result. ";
        var name = $"workflow-{index:00}";
        switch (index % 8)
        {
            case 0:
                RunLiveCase(name, 1, "OPTIONAL_EVIDENCE_OK: 42", common + "Use one step and one worker. Have it calculate 17+25 and check 42-25=17, then complete its task with a short summary and OMIT the optional evidence field. Final marker OPTIONAL_EVIDENCE_OK: 42.");
                break;
            case 1:
                RunLiveCase(name, 2, "DEPENDENCY_QUEUE_OK: 42", common + "Publish exactly two steps: calculate owned by your first worker, and verify owned by your second worker, with verify depending on calculate. Dispatch VERIFY FIRST so it waits in the app, then dispatch calculate. Calculate checks 19+23=42 and records that evidence. Verify reads bridge_list_tasks to confirm its prerequisite completed, independently verifies 42-23=19, and completes. Do not manually redispatch verify: the app releases it. Final marker DEPENDENCY_QUEUE_OK: 42.");
                break;
            case 2:
                RunLiveCase(name, 3, "JOIN_OK: 84", common + "Publish exactly three steps: left for first worker computes 17+25=42, right for second computes 6*7=42, join for third depends on both left and right. Dispatch join first, then left and right. Each producer records its actual result as evidence. Join reads completed prerequisite evidence with bridge_list_tasks, adds the results to get 84, checks 84-42=42, and completes. Final marker JOIN_OK: 84.");
                break;
            case 3:
                RunLiveCase(name, 1, "PAGED_INBOX_OK: 42", common + "The worker inbox is preloaded with 65 real messages. Assign it to find the oldest message containing ARCHIVE_TARGET by using bridge_read_messages pages and passing next_cursor as before until found. It must mark that message answered, report its target value, then complete the task. Do not guess the value without reading the message. Final marker PAGED_INBOX_OK: 42.", SeedPagedInbox);
                break;
            case 4:
                RunLiveCase(name, 1, "IDEMPOTENT_OK: 42", common + "One planned task with id deduplicated for your worker: calculate 17+25 and independently check by subtraction. As an intentional retry test, call bridge_dispatch_task TWICE with EXACTLY the same task_id, recipient, task_name and message. The worker must execute only once. After its real completed report, final marker IDEMPOTENT_OK: 42.");
                break;
            case 5:
                RunLiveCase(name, 1, "RETRY_OK: 42", common + "One planned task with id recovery-check. Its original work order must instruct the worker: read bridge_list_tasks for your task's attempt; on attempt 1 report blocked using bridge_update_task with summary 'Simulation fixture temporarily unavailable' and end the turn with BLOCKED_FIXTURE. On attempt 2 calculate 17+25, verify by subtraction, complete with meaningful evidence, and return RETRY_OK: 42. When the first blocked report arrives, call bridge_retry_task for recovery-check exactly once; do not dispatch a new ID. After the successful real report, final marker RETRY_OK: 42.");
                break;
            case 6:
                RunLiveCase(name, 2, "SHARED_FILE_OK: 42", common + "Publish two independent steps for your two workers, both listing the SAME advisory file Shared.cs. No actual file edits in this simulation. First verifies 17+25=42; second verifies 42-25=17. Each worker must use bridge_list_tasks to see the overlapping file hints and confirm in its report that both agents remain allowed to edit Shared.cs. Complete the numerical checks. Final marker SHARED_FILE_OK: 42.");
                break;
            case 7:
                RunLiveCase(name, 2, "EVIDENCE_OK: 42", common + "Two steps: producer computes 17+25, records evidence '17+25=42; inverse check 42-25=17', and completes; reviewer depends on producer and reads its actual evidence with bridge_list_tasks, independently recomputes both checks, then completes with its findings. Dispatch both before yielding. These are the only required tasks. Final marker EVIDENCE_OK: 42.");
                break;
        }
    }

    private static void SeedPagedInbox(ChatViewModel[] team)
    {
        Tool(team[0], "bridge_read_messages"); Tool(team[1], "bridge_read_messages");
        var from = (BridgeMailboxStore.Mailbox)Property(team[0], "PeerMailbox")!;
        var to = (BridgeMailboxStore.Mailbox)Property(team[1], "PeerMailbox")!;
        for (var i = 0; i < 65; i++)
            from.Store.Deliver(from, to, i == 0 ? "ARCHIVE_TARGET=42. Preserve this oldest result for the assigned check." : $"Archive context {i}; not the target result.", 1, DateTimeOffset.UtcNow);
    }

    private static void AssertLunaWorkflow(int index, List<(string Agent, string Tool, JsonObject Input, JsonObject Result)> calls, JsonObject plan)
    {
        var dispatches = calls.Where(c => c.Tool == "bridge_dispatch_task").ToArray();
        var tasks = plan["tasks"]!.AsArray();
        switch (index % 8)
        {
            case 0: Check("Luna completes a simple task without evidence", tasks.Count == 1 && tasks[0]!["evidence"]!.ToString() == ""); break;
            case 1: Check("Luna dispatches the dependent task first and the app holds it", dispatches[0].Result["status"]!.ToString() == "queued" && tasks.Any(t => t!["dependencies"]!.AsArray().Count == 1)); break;
            case 2: Check("Luna completes a real two-prerequisite join", tasks.Count == 3 && tasks.Any(t => t!["dependencies"]!.AsArray().Count == 2)); break;
            case 3: Check("Luna reads multiple real inbox pages and acknowledges the oldest target", calls.Count(c => c.Tool == "bridge_read_messages" && c.Agent == "Agent 2") >= 2 && calls.Any(c => c.Tool == "bridge_read_messages" && c.Input.ContainsKey("before")) && calls.Any(c => c.Tool == "bridge_mark_message")); break;
            case 4: Check("Luna's repeated dispatch executes only one assignment attempt", dispatches.Length == 2 && tasks.Count == 1 && tasks[0]!["attempt"]!.GetValue<int>() == 1); break;
            case 5: Check("Luna recovers the same blocked task with a second attempt", calls.Any(c => c.Tool == "bridge_update_task" && c.Input["status"]!.ToString() == "blocked") && calls.Count(c => c.Tool == "bridge_retry_task") == 1 && tasks.Count == 1 && tasks[0]!["attempt"]!.GetValue<int>() == 2); break;
            case 6: Check("Luna workers accept overlapping advisory file activity", tasks.Count == 2 && tasks.All(t => t!["files"]!.AsArray().Any(f => f!.ToString() == "Shared.cs"))); break;
            case 7: Check("Luna reviewer inspects durable evidence", tasks.Any(t => t!["evidence"]!.ToString().Contains("42")) && calls.Any(c => c.Agent == "Agent 3" && c.Tool == "bridge_list_tasks")); break;
        }
    }
}
