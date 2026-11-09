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
        VerifyWorkerProviderErrorRecovery();
        VerifyWorkerProviderLifecycle();
    }

    private static void VerifyWorkerProviderErrorRecovery()
    {
        const string transportError = "Could not reach the GLM endpoint: An error occurred while sending the request.";
        void Fail(ChatViewModel chat) => Call(chat, "ApplyResult", new JsonObject { ["is_error"] = true, ["result"] = transportError });
        // A transport failure waits on its provider's shared health gate, so "retry now" makes that gate's probe due.
        ProviderHealthGate? Gate(ChatViewModel chat) => (ProviderHealthGate?)typeof(ChatViewModel).GetField("_bridgeRetryGate", Flags)!.GetValue(chat);
        void RetryNow(ChatViewModel chat)
        {
            typeof(ChatViewModel).GetField("_bridgeRetryAt", Flags)!.SetValue(chat, DateTimeOffset.MinValue);
            Gate(chat)?.ProbeNow();
            Call(chat, "CheckBridgeErrorRecovery");
        }
        IEnumerable<string> Notices(ChatViewModel chat) => chat.Items.OfType<UserItem>().Select(i => i.Text)
            .Concat(chat.Items.OfType<QueuedItem>().Select(i => i.Text)).Where(t => t.Contains("waiting to recover"));

        var (vm, team) = WorkTeam("provider-error-owner");
        var manager = team[0]; var worker = team[1]; var other = team[2];
        Tool(manager, "bridge_dispatch_task", Assignment("recover-provider", worker));
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Fail(worker);
        var work = (BridgeWorkState)Property(worker, "BridgeWork")!;
        var task = work.Tasks.Single(t => t.Id == "recover-provider");
        var gate = Gate(worker)!;
        Check("provider error asks only the owning orchestrator to revive its worker", worker.WaitingForBridgeRetry
            && Notices(manager).Single().Contains("task_id=recover-provider") && !Notices(other).Any());
        Call(vm, "OnBridgeManagerStatusChanged", worker);
        Check("repeated status inspection does not duplicate the recovery request", Notices(manager).Count() == 1);
        Check("worker lane and saved assignment stay owned while recovery is pending", task.State == "running"
            && task.Attempt == 1 && task.OwnerId == worker.BridgeAgentId);
        Reject("pending recovery cannot be reassigned while its original worker may resume", () => Tool(manager, "bridge_retry_task",
            new() { ["task_id"] = task.Id, ["recipient"] = other.BridgeAgentId }));
        var original = task.Instruction;
        // SaveWork's on-disk-before-dispatch write is this Bridge's own journal file, so that is the one made unwritable.
        var journalPath = BridgeJournalStore.PathFor(manager.SessionId, manager.Provider);
        using (var locked = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
            Reject("failed orchestrator recovery preserves the generic fallback", () => Tool(manager, "bridge_retry_task",
                new() { ["task_id"] = task.Id, ["message"] = "Finish only the remaining work." }));
        Check("journal failure retains the original assignment and its wait on the provider gate", task.State == "running"
            && task.Instruction == original && worker.WaitingForBridgeRetry && gate.Holds(worker) && Session(worker).Sent.Count == 1);
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = task.Id });
        PumpUntil(() => Session(worker).Sent.Count == 2);
        RetryNow(worker);
        Check("successful owner recovery replaces the automatic retry without a duplicate turn", !worker.WaitingForBridgeRetry
            && !gate.Holds(worker) && Session(worker).Sent.Count == 2 && task.Attempt == 2
            && Session(worker).Sent[^1].Contains("RECOVERY:") && task.OwnerId == worker.BridgeAgentId);
        FinishWork(worker);

        var (_, fallbackTeam) = WorkTeam("provider-error-fallback");
        var fallbackManager = fallbackTeam[0]; var fallback = fallbackTeam[1];
        Tool(fallbackManager, "bridge_dispatch_task", Assignment("fallback-provider", fallback));
        PumpUntil(() => Session(fallback).Sent.Count == 1);
        Fail(fallback);
        fallbackManager.Status = "error";
        RetryNow(fallback);
        PumpUntil(() => Session(fallback).Sent.Count == 2);
        Check("unavailable orchestrator leaves the worker a generic continuation", Session(fallback).Sent[^1].Contains(BridgeErrorRecovery.WorkerContinuePrompt)
            && !Session(fallback).Sent[^1].Contains("Figure out why", StringComparison.OrdinalIgnoreCase));
        Check("fallback resumes the existing durable attempt rather than replaying dispatch", ((BridgeWorkState)Property(fallback, "BridgeWork")!)
            .Tasks.Single(t => t.Id == "fallback-provider") is { State: "running", Attempt: 1 });
        FinishWork(fallback);

        var (_, orchestratorTeam) = WorkTeam("provider-error-orchestrator");
        var orchestrator = orchestratorTeam[0];
        orchestrator.Status = "running";
        Fail(orchestrator);
        Check("an errored orchestrator does not ask its workers to revive it", orchestrator.WaitingForBridgeRetry
            && orchestratorTeam.Skip(1).All(p => !Notices(p).Any() && Session(p).Sent.Count == 0));
        RetryNow(orchestrator);
        PumpUntil(() => Session(orchestrator).Sent.Count == 1);
        Check("orchestrator fallback explains why another AI could not revive it", Session(orchestrator).Sent.Single()
            .Contains(BridgeErrorRecovery.OrchestratorContinuePrompt));
        FinishWork(orchestrator);   // its continuation was the provider gate's probe; settle it so later fixtures are not held behind it

        var (_, stoppedTeam) = WorkTeam("provider-error-held");
        Tool(stoppedTeam[0], "bridge_dispatch_task", Assignment("held-provider", stoppedTeam[1]));
        PumpUntil(() => Session(stoppedTeam[1]).Sent.Count == 1);
        Fail(stoppedTeam[1]);
        Tool(stoppedTeam[0], "bridge_stop_worker", WorkerStopArgs(stoppedTeam[1]));
        RetryNow(stoppedTeam[1]);
        Check("orchestrator stop cancels both recovery paths", !stoppedTeam[1].WaitingForBridgeRetry
            && Session(stoppedTeam[1]).Sent.Count == 1);
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
        var journalPath = BridgeJournalStore.PathFor(manager.SessionId, manager.Provider);   // what SaveWork writes first
        using (var locked = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
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
        var journalPath = BridgeJournalStore.PathFor(manager.SessionId, manager.Provider);   // what SaveWork writes first
        JsonObject stop;
        using (var locked = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None))
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
            // The fixture appends to its log while this reads it, so every read here shares the file (ReadShared).
            PumpUntil(() => worker.Status == "running" && ReadShared(log).Any(line => line.Contains("turn/start")), 30000);
            // Process the provider's turn/started envelope before interrupting its assigned turn ID.
            PumpUntil(() => typeof(VibeCode.Protocol.CodexSession).GetField("_turnId", Flags)!.GetValue(
                typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(worker)) is not null);
            Tool(manager, "bridge_stop_worker", WorkerStopArgs(worker));
            PumpUntil(() => worker.Status == "idle", 15000);
            Check("actual Codex adapter receives and settles worker interrupt", ReadShared(log).Select(l => JsonNode.Parse(l)!)
                .Count(r => r["method"]!.ToString() == "turn/interrupt") == 1 && worker.BridgeTaskState == "interrupted");
            Tool(manager, "bridge_retry_task", new() { ["task_id"] = "provider", ["message"] = "Finish the revised local fixture task." });
            var work = (BridgeWorkState)Property(worker, "BridgeWork")!;
            PumpUntil(() => worker.Status == "idle" && work.Tasks.Single(t => t.Id == "provider").State == "completed", 15000);
            var requests = ReadShared(log).Select(l => JsonNode.Parse(l)!).ToArray();
            Check("disconnected worker reconnects its original thread", worker.SessionId == thread
                && requests.Count(r => r["method"]!.ToString() == "thread/resume") == 1
                && requests.All(r => r["method"]!.ToString() != "thread/start"));
            Check("provider stop and revised retry preserve draft and chat", worker.Draft == "Keep this draft"
                && requests.Count(r => r["method"]!.ToString() == "turn/start") == 2
                && requests.Last(r => r["method"]!.ToString() == "turn/start").ToJsonString().Contains("revised local fixture task"));

            var crash = Assignment("crash-recovery", worker);
            crash["message"] = "Crash the local recovery fixture.";
            Tool(manager, "bridge_dispatch_task", crash);
            worker.Send("Keep this later queued request.");
            PumpUntil(() => worker.Status == "error" && worker.WaitingForBridgeRetry);
            manager.Status = "error";
            typeof(ChatViewModel).GetField("_bridgeRetryAt", Flags)!.SetValue(worker, DateTimeOffset.MinValue);
            ((ProviderHealthGate?)typeof(ChatViewModel).GetField("_bridgeRetryGate", Flags)!.GetValue(worker))?.ProbeNow();   // a transport exit waits on the shared gate
            Call(worker, "CheckBridgeErrorRecovery");
            PumpUntil(() => worker.Status == "running" && ReadShared(log).Any(line => line.Contains(BridgeErrorRecovery.WorkerContinuePrompt)), 30000);
            var recoveredRequests = ReadShared(log).Select(l => JsonNode.Parse(l)!).ToArray();
            Check("unexpected provider exit reconnects the same conversation automatically", worker.SessionId == thread
                && recoveredRequests.Count(r => r["method"]!.ToString() == "thread/resume") == 2
                && recoveredRequests.All(r => r["method"]!.ToString() != "thread/start"));
            Check("reconnect sends generic recovery before later queued work", worker.HasQueued
                && recoveredRequests.Last(r => r["method"]!.ToString() == "turn/start").ToJsonString().Contains(BridgeErrorRecovery.WorkerContinuePrompt)
                && !recoveredRequests.Last(r => r["method"]!.ToString() == "turn/start").ToJsonString().Contains("later queued request"));
            Check("automatic reconnect preserves the running durable attempt", work.Tasks.Single(t => t.Id == "crash-recovery")
                is { State: "running", Attempt: 1 });
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
            if (method == "turn/start" && line.Contains("Crash the local recovery fixture"))
            {
                Console.Error.WriteLine("connection reset by the local fixture");
                Console.Error.Flush();
                return 1;
            }
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
