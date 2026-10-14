using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static JsonObject WorkerStopArgs(ChatViewModel worker) => new()
    { ["recipient"] = worker.BridgeAgentId, ["reason"] = "Taking too long; inspect progress and narrow the task." };

    private static void VerifyWorkerControls()
    {
        VerifyWorkerControlOwnership();
        VerifyWorkerStopAndRetry();
        VerifyWorkerHeldQueue();
        VerifyWorkerPreparationStop();
        VerifyWorkerRecoveryStop();
        VerifyWorkerProviderLifecycle();
    }

    private static void VerifyWorkerControlOwnership()
    {
        var (_, regular) = Team("regular-control", 2);
        Reject("regular peers cannot stop each other", () => Tool(regular[0], "bridge_stop_worker", WorkerStopArgs(regular[1])));
        Reject("regular peers cannot resume each other", () => Tool(regular[0], "bridge_resume_worker", new() { ["recipient"] = regular[1].BridgeAgentId }));
        var (_, team) = WorkTeam("control-ownership", 5);
        var a = team[0]; var aw = team[1]; var b = team[2]; var bw = team[3];
        Property(b, "IsBridgeManager", true); Property(b, "BridgeCoordinatesOnly", true);
        Property(bw, "BridgeCoordinatorAgentId", b.BridgeAgentId);
        Property(team[4], "BridgeCoordinatorAgentId", null!);
        Reject("worker cannot stop its orchestrator", () => Tool(aw, "bridge_stop_worker", WorkerStopArgs(a)));
        Reject("orchestrator cannot stop another orchestrator", () => Tool(a, "bridge_stop_worker", WorkerStopArgs(b)));
        Reject("orchestrator cannot stop another group's worker", () => Tool(a, "bridge_stop_worker", WorkerStopArgs(bw)));
        Reject("orchestrator cannot stop an unassigned peer", () => Tool(a, "bridge_stop_worker", WorkerStopArgs(team[4])));
        Reject("orchestrator cannot reach another bridge", () => Tool(a, "bridge_stop_worker", WorkerStopArgs(regular[1])));
        Tool(b, "bridge_stop_worker", WorkerStopArgs(bw));
        Reject("orchestrator cannot resume another group's worker", () => Tool(a, "bridge_resume_worker", new() { ["recipient"] = bw.BridgeAgentId }));
        var stop = Tool(a, "bridge_stop_worker", WorkerStopArgs(aw));
        Check("own workers can be stopped before scope agreement", stop["paused"]!.GetValue<bool>());
        Reject("resume still requires coordination agreement", () => Tool(a, "bridge_resume_worker", new() { ["recipient"] = aw.BridgeAgentId }));
        var roster = Tool(a, "bridge_list_agents")["agents"]!.AsArray();
        Check("roster exposes control only for caller's own worker", roster.Single(p => p!["agent_id"]!.ToString() == aw.BridgeAgentId)!["can_control_worker"]!.GetValue<bool>()
            && !roster.Single(p => p!["agent_id"]!.ToString() == bw.BridgeAgentId)!["can_control_worker"]!.GetValue<bool>());
    }

    private static void VerifyWorkerStopAndRetry()
    {
        var (vm, team) = WorkTeam("stop-retry");
        var manager = team[0]; var worker = team[1]; var other = team[2];
        Tool(manager, "bridge_dispatch_task", Assignment("slow", worker));
        Tool(manager, "bridge_dispatch_task", Assignment("later", worker));
        Tool(manager, "bridge_dispatch_task", Assignment("independent", other));
        PumpUntil(() => Session(worker).Sent.Count == 1 && Session(other).Sent.Count == 1);
        var session = Session(worker); var sessionId = worker.SessionId; var items = worker.Items.Count;
        var work = (BridgeWorkState)Property(worker, "BridgeWork")!;
        var pipe = (string)Property(Call(manager, "EnsureBridgeMcp")!, "PipeName")!;
        var request = BridgeMcpClient.InvokeAsync(pipe, "bridge_stop_worker", WorkerStopArgs(worker));
        PumpUntil(() => request.IsCompleted);
        var result = request.GetAwaiter().GetResult();
        Check("real MCP pipe requests one provider interrupt", session.Interrupts == 1 && result["stop_pending"]!.GetValue<bool>());
        Check("stop journals interrupted assignment and retains queued assignment", work.Tasks.Single(t => t.Id == "slow").State == "interrupted"
            && work.Tasks.Single(t => t.Id == "later").State == "queued");
        Check("stop preserves chat, transcript and provider session", vm.Chats.Contains(worker) && vm.BridgePanes.Contains(worker)
            && worker.SessionId == sessionId && ReferenceEquals(Session(worker), session) && !session.HasExited && worker.Items.Count >= items);
        Check("worker stop does not pause the whole Bridge plan", !(bool)Property(work, "IdlePlanRemindersPaused")!);
        Check("other worker keeps running", other.Status == "running" && Session(other).Interrupts == 0);
        Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
        Check("repeated stop does not issue a duplicate interrupt", session.Interrupts == 1);
        Reject("restart cannot race the old running turn", () => Tool(manager, "bridge_retry_task", new() { ["task_id"] = "slow" }));
        Reject("stopped worker's late task update cannot claim completion", () => Tool(worker, "bridge_update_task", new()
        { ["task_id"] = "slow", ["status"] = "completed", ["summary"] = "Stale completion" }));
        FinishWork(worker, "Partial output from the stopped attempt.");
        Check("orchestrator receives a stop acknowledgement", manager.Items.OfType<UserItem>().Any(i => i.Text.Contains("has stopped"))
            || manager.Items.OfType<QueuedItem>().Any(i => i.Text.Contains("has stopped")));
        Check("provider result does not release held queued work", session.Sent.Count == 1 && worker.Status == "idle"
            && work.Tasks.Single(t => t.Id == "slow").State == "interrupted");
        Check("paused status is reported accurately", Tool(manager, "bridge_agent_status", new() { ["agent"] = worker.BridgeAgentId })["state"]!.ToString() == "paused");
        var restored = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(work))!;
        restored.Recover();
        Check("stop hold survives saved Bridge recovery", restored.PausedWorkers.Contains(worker.BridgeAgentId)
            && restored.Tasks.Single(t => t.Id == "slow").State == "interrupted");
        Reject("resume cannot silently replay interrupted work", () => Tool(manager, "bridge_resume_worker", new() { ["recipient"] = worker.BridgeAgentId }));
        Reject("new dispatch cannot bypass the worker hold", () => Tool(manager, "bridge_dispatch_task", Assignment("bypass", worker)));
        const string revised = "Inspect prior edits. Finish only the failing parser test and report the result.";
        var original = work.Tasks.Single(t => t.Id == "slow").Instruction;
        var settingsPath = (string)typeof(AppSettings).GetProperty("FilePath", Flags)!.GetValue(null)!;
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject("restart cannot run without saving its new attempt", () => Tool(manager, "bridge_retry_task", new() { ["task_id"] = "slow", ["message"] = revised }));
        Check("failed restart keeps original task and hold", work.PausedWorkers.Contains(worker.BridgeAgentId)
            && work.Tasks.Single(t => t.Id == "slow").State == "interrupted" && work.Tasks.Single(t => t.Id == "slow").Instruction == original);
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "slow", ["message"] = revised });
        PumpUntil(() => session.Sent.Count == 2);
        Check("explicit retry starts same task with revised instructions", session.Sent[^1].Contains(revised)
            && session.Sent[^1].Contains("RECOVERY:") && work.Tasks.Single(t => t.Id == "slow").Attempt == 2
            && !work.PausedWorkers.Contains(worker.BridgeAgentId));
        FinishWork(worker);
        PumpUntil(() => session.Sent.Count == 3);
        Check("queued task follows successful restarted task exactly once", session.Sent[^1].Contains("Task later") && work.Tasks.Single(t => t.Id == "slow").State == "completed");
        FinishWork(worker); FinishWork(other);
    }

    private static void VerifyWorkerHeldQueue()
    {
        var (_, team) = WorkTeam("held-input"); var manager = team[0]; var worker = team[1];
        worker.Status = "running";
        Check("user prompt can queue while worker runs", worker.Send("Preserve this pending user request."));
        Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
        FinishWork(worker);
        Check("stop keeps unsent user input without dispatching it", worker.HasQueued && Session(worker).Sent.Count == 0 && !worker.CanSendQueuedNow);
        Tool(manager, "bridge_resume_worker", new() { ["recipient"] = worker.BridgeAgentId });
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Check("resume sends retained user input exactly once", Session(worker).Sent[0].Contains("Preserve this pending user request"));
        FinishWork(worker);
        Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
        Tool(manager, "bridge_resume_worker", new() { ["recipient"] = worker.BridgeAgentId });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("resuming empty worker does not invent a turn", Session(worker).Sent.Count == 1 && worker.Status == "idle" && worker.BridgeTaskState == "ready");
    }

    private static void VerifyWorkerPreparationStop()
    {
        var (_, team) = WorkTeam("preparing-stop"); var manager = team[0]; var worker = team[1];
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ChatViewModel).GetField("_bridgeConfigurationApplication", Flags)!.SetValue(worker, hold.Task);
        Tool(manager, "bridge_dispatch_task", Assignment("prepare", worker));
        Check("fixture stops before provider receives prompt", worker.Status == "preparing" && Session(worker).Sent.Count == 0);
        Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
        Reject("preparation stop stays busy until old dispatch settles", () => Tool(manager, "bridge_retry_task", new() { ["task_id"] = "prepare" }));
        hold.SetResult();
        PumpUntil(() => worker.Status == "idle");
        Check("stopped preparation never sends old prompt", Session(worker).Sent.Count == 0 && Session(worker).Interrupts == 0);
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "prepare", ["message"] = "Revised preparation work." });
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Check("restarted preparation sends only revised prompt", Session(worker).Sent.Single().Contains("Revised preparation work"));
        FinishWork(worker);
    }

    private static void VerifyWorkerRecoveryStop()
    {
        var (_, team) = WorkTeam("recovery-stop"); var manager = team[0]; var worker = team[1];
        Tool(manager, "bridge_dispatch_task", Assignment("recover", worker));
        PumpUntil(() => Session(worker).Sent.Count == 1);
        typeof(ChatViewModel).GetField("_bridgeRetrySession", Flags)!.SetValue(worker, Session(worker));
        typeof(ChatViewModel).GetField("_limitRecoveryResult", Flags)!.SetValue(worker, new JsonObject());
        worker.Status = "error";
        var settingsPath = (string)typeof(AppSettings).GetProperty("FilePath", Flags)!.GetValue(null)!;
        JsonObject stop;
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            stop = Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
        Check("stop cancels provider retry and limit recovery", !worker.WaitingForBridgeRetry && !worker.WaitingForLimitReset);
        Check("journal failure still stops worker and reports warning", stop["paused"]!.GetValue<bool>() && stop["journal_warning"] is not null);
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "recover" });
        PumpUntil(() => Session(worker).Sent.Count == 2);
        Check("retry recovers same live errored session", worker.Status == "running" && !Session(worker).HasExited);
        FinishWork(worker);
    }

    private static void VerifyWorkerProviderLifecycle()
    {
        var log = Path.Combine(_root, "managed-worker-rpc.jsonl");
        File.WriteAllText(log, "");
        var environment = new Dictionary<string, string>
        {
            ["VIBECODE_CODEX_PATH"] = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"),
            ["VIBECODE_CODEX_ACCOUNT_STORE"] = Path.Combine(_root, "worker-fixture-accounts"),
            ["VIBECODE_CODEX_LEGACY_HOME"] = Path.Combine(_root, "worker-fixture-home"),
            ["VIBECODE_WORKER_CONTROL_FIXTURE_LOG"] = log,
        };
        var previous = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        var (_, team) = WorkTeam("provider-worker"); var manager = team[0]; var worker = team[1];
        var thread = worker.SessionId;
        worker.Draft = "Keep this draft";
        try
        {
            Tool(manager, "bridge_dispatch_task", Assignment("provider", worker));
            PumpUntil(() => Session(worker).Sent.Count == 1);
            Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
            Session(worker).Dispose(); worker.Status = "error";
            Tool(manager, "bridge_retry_task", new() { ["task_id"] = "provider" });
            PumpUntil(() => worker.Status == "running" && File.ReadAllText(log).Contains("turn/start"), 30000);
            // Process the provider's turn/started envelope before interrupting its assigned turn ID.
            PumpUntil(() => typeof(VibeCode.Protocol.CodexSession).GetField("_turnId", Flags)!.GetValue(
                typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(worker)) is not null);
            Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
            PumpUntil(() => worker.Status == "idle", 15000);
            Check("actual Codex adapter receives and settles worker interrupt", File.ReadLines(log).Select(l => JsonNode.Parse(l)!)
                .Count(r => r["method"]!.ToString() == "turn/interrupt") == 1 && worker.BridgeTaskState == "interrupted");
            Tool(manager, "bridge_retry_task", new() { ["task_id"] = "provider", ["message"] = "Finish the revised local fixture task." });
            var work = (BridgeWorkState)Property(worker, "BridgeWork")!;
            PumpUntil(() => worker.Status == "idle" && work.Tasks.Single(t => t.Id == "provider").State == "completed", 15000);
            var requests = File.ReadLines(log).Select(l => JsonNode.Parse(l)!).ToArray();
            Check("disconnected worker reconnects its original thread", worker.SessionId == thread
                && requests.Count(r => r["method"]!.ToString() == "thread/resume") == 1
                && requests.All(r => r["method"]!.ToString() != "thread/start"));
            Check("provider stop and revised retry preserve draft and chat", worker.Draft == "Keep this draft"
                && requests.Count(r => r["method"]!.ToString() == "turn/start") == 2
                && requests.Last(r => r["method"]!.ToString() == "turn/start").ToJsonString().Contains("revised local fixture task"));
        }
        finally
        {
            worker.Close();
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static int RunWorkerControlFixture()
    {
        var thread = "fixture-worker"; var turns = 0;
        void Emit(JsonObject value) { Console.WriteLine(value.ToJsonString()); Console.Out.Flush(); }
        void Notify(string method, JsonObject value) => Emit(new() { ["method"] = method, ["params"] = value });
        while (Console.ReadLine() is { } line)
        {
            var request = JsonNode.Parse(line)!;
            if (request["id"] is not { } id) continue;
            File.AppendAllText(Environment.GetEnvironmentVariable("VIBECODE_WORKER_CONTROL_FIXTURE_LOG")!, line + "\n");
            var method = request["method"]!.ToString();
            if (method == "thread/resume") thread = request["params"]!["threadId"]!.ToString();
            var result = method switch
            {
                "model/list" => new JsonObject { ["data"] = new JsonArray() },
                "thread/start" or "thread/resume" => new JsonObject { ["thread"] = new JsonObject { ["id"] = thread } },
                "turn/start" => new JsonObject { ["turn"] = new JsonObject { ["id"] = "turn-" + ++turns } },
                _ => new JsonObject(),
            };
            Emit(new() { ["id"] = id.DeepClone(), ["result"] = result });
            var turn = "turn-" + turns;
            if (method == "turn/start")
                Notify("turn/started", new() { ["threadId"] = thread, ["turn"] = new JsonObject { ["id"] = turn } });
            if (method == "turn/interrupt")
                Notify("turn/completed", new() { ["threadId"] = thread, ["turn"] = new JsonObject { ["id"] = turn, ["status"] = "interrupted" } });
            if (method == "turn/start" && turns > 1)
            {
                Notify("item/completed", new() { ["threadId"] = thread, ["turnId"] = turn,
                    ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "reply-" + turn, ["text"] = "Managed worker completed its revised task." } });
                Notify("turn/completed", new() { ["threadId"] = thread, ["turn"] = new JsonObject { ["id"] = turn, ["status"] = "completed" } });
            }
        }
        return 0;
    }
}
