using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyDurableWorkflow()
    {
        VerifyBusyMailboxAndExpiry();
        VerifyTaskDependenciesAndRecovery();
        VerifyOneShotOrchestrators();
        VerifyStorageFailuresAndReassignment();
        VerifyConcurrentBridgeTransport();
        VerifyGraphSimulations();
    }

    private static void VerifyBusyMailboxAndExpiry()
    {
        var workspace = Path.Combine(_root, "durable-mail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var runId = Guid.NewGuid().ToString("N");
        var store = new BridgeMailboxStore(workspace, runId);
        var boxes = Enumerable.Range(1, 9).Select(n => store.Register(n, "Agent " + n, "stable-" + n)).ToArray();
        var watch = Stopwatch.StartNew();
        var receipts = new HashSet<string>();
        for (var i = 0; i < 224; i++)
            receipts.Add(store.Deliver(boxes[1 + i % 8], boxes[0], $"Dependency result {i}: value={i * 17}. " + new string('x', 180), 1, DateTimeOffset.UtcNow).Id);
        var deliveryMs = watch.Elapsed.TotalMilliseconds;
        Check("busy nine-agent team retains all 224 unread results", boxes[0].Messages.Count == 224 && boxes[0].Messages.All(m => m.ReadAt is null));
        var readIds = new HashSet<string>();
        string? cursor = null;
        bool more;
        watch.Restart();
        do
        {
            var page = store.ReadPage(boxes[0], cursor, 50, DateTimeOffset.UtcNow, out more);
            Check("bounded page contains only acknowledged incoming results", page.Count <= 50 && page.All(m => m.ReadAt.HasValue));
            foreach (var message in page) Check("pagination never duplicates a result", readIds.Add(message.Id));
            cursor = page.Last().Id;
        } while (more);
        Check("pagination reaches every busy-team result", receipts.SetEquals(readIds));
        var pageMs = watch.Elapsed.TotalMilliseconds;
        var recovered = new BridgeMailboxStore(workspace, runId);
        var inbox = recovered.Register(1, "Agent 1", "stable-1");
        Check("restart preserves exact message IDs and read receipts", inbox.Messages.Count == 224 && inbox.Messages.All(m => receipts.Contains(m.Id) && m.ReadAt.HasValue));
        var id = inbox.Messages[0].Id;
        recovered.Mark(inbox, id, true, DateTimeOffset.UtcNow);
        var again = new BridgeMailboxStore(workspace, runId);
        var againInbox = again.Register(1, "Agent 1", "stable-1");
        Check("answered receipt survives another recovery", againInbox.Messages.Single(m => m.Id == id).AnsweredAt.HasValue);
        again.DeleteAgent("stable-2");
        Check("chat deletion removes its message bodies from surviving inboxes", againInbox.Messages.All(m => m.From.AgentId != "stable-2"));
        var departed = again.Register(3, "Agent 3", "stable-3");
        again.Close(departed);
        Check("removing a pane preserves historical messages until chat deletion", againInbox.Messages.Any(m => m.From.AgentId == "stable-3"));
        again.DeleteAgent("stable-3");
        Check("deleting an already removed chat also purges its historical messages", againInbox.Messages.All(m => m.From.AgentId != "stable-3"));
        again.Detach(againInbox);
        Check("another active view protects a mailbox even if the newest view detached", BridgeMailboxStore.CleanupExpired(workspace, DateTimeOffset.UtcNow.AddDays(8)) == 0);
        foreach (var box in boxes) store.Detach(box);
        recovered.Detach(inbox);
        again.Detach(againInbox);
        var runDir = Path.GetDirectoryName(againInbox.FilePath)!;
        var bytes = Directory.EnumerateFiles(runDir).Sum(path => new FileInfo(path).Length);
        Check("busy-team on-disk storage stays bounded", bytes < 2 * 1024 * 1024);
        Check("inactive mailbox remains available before seven days", BridgeMailboxStore.CleanupExpired(workspace, DateTimeOffset.UtcNow.AddDays(6)) == 0 && Directory.Exists(runDir));
        Check("inactive mailbox is removed after seven days", BridgeMailboxStore.CleanupExpired(workspace, DateTimeOffset.UtcNow.AddDays(8)) == 1 && !Directory.Exists(runDir));

        var protectedStore = new BridgeMailboxStore(workspace);
        var active = protectedStore.Register(1, "Active");
        Check("active mailbox is never expired", BridgeMailboxStore.CleanupExpired(workspace, DateTimeOffset.UtcNow.AddDays(8)) == 0);
        protectedStore.Detach(active);
        var userFile = Path.Combine(Path.GetDirectoryName(active.FilePath)!, "user-notes.txt");
        File.WriteAllText(userFile, "Keep my data");
        Check("cleanup never deletes a directory containing unowned files", BridgeMailboxStore.CleanupExpired(workspace, DateTimeOffset.UtcNow.AddDays(8)) == 0 && File.ReadAllText(userFile) == "Keep my data");

        File.WriteAllText(Path.Combine(_root, "mailbox-performance.json"), JsonSerializer.Serialize(new
        { agents = 9, messages = 224, delivery_ms = deliveryMs, page_read_ms = pageMs, disk_bytes = bytes,
            notes = "Actual production store I/O on this machine. No model turns, timers or polling needed for delivery." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"MEASURED: 224 durable deliveries {deliveryMs:F1} ms; paginated reads {pageMs:F1} ms; retained metadata {bytes:N0} bytes.");
    }

    private static (MainViewModel Vm, ChatViewModel[] Team) WorkTeam(string name, int count = 3)
    {
        var result = Team(name, count);
        foreach (var agent in result.Item2) agent.Status = "idle";
        var manager = result.Item2[0];
        Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true);
        foreach (var worker in result.Item2.Skip(1)) Property(worker, "BridgeCoordinatorAgentId", manager.BridgeAgentId);
        result.Item1.SetBridgeTerminalMode(true);
        return result;
    }

    private static JsonObject PlanStep(string id, string title, ChatViewModel worker, params string[] dependencies) => new()
    {
        ["id"] = id, ["title"] = title, ["owner_id"] = worker.BridgeAgentId,
        ["dependencies"] = new JsonArray(dependencies.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        ["files"] = new JsonArray("shared.cs"),
    };
    private static JsonObject Assignment(string id, ChatViewModel worker, string title = "Verify result") => new()
    { ["recipient"] = worker.BridgeAgentId, ["task_id"] = id, ["task_name"] = title, ["message"] = "Calculate 17+25 and verify by subtraction." };

    private static void FinishWork(ChatViewModel worker, string report = "Verified: 17+25=42 and 42-25=17.")
    {
        worker.Items.Add(new TextItem { Text = report });
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void VerifyTaskDependenciesAndRecovery()
    {
        var (vm, team) = WorkTeam("workflow");
        var manager = team[0]; var a = team[1]; var b = team[2];
        var plan = new JsonObject { ["plan_version"] = 1, ["steps"] = new JsonArray(PlanStep("calculate", "Calculate result", a), PlanStep("verify", "Verify result", b, "calculate")) };
        Reject("workers cannot publish the plan", () => Tool(a, "bridge_set_plan", plan));
        Tool(manager, "bridge_set_plan", plan);
        Check("published plan has a new version and real steps", Tool(manager, "bridge_list_tasks")["plan_version"]!.GetValue<int>() == 2 && vm.SharedBridgeTerminal!.TotalSteps == 2);
        Reject("stale plan write cannot overwrite newer work", () => Tool(manager, "bridge_set_plan", plan));
        Reject("cyclic plan is rejected before mutation", () => Tool(manager, "bridge_set_plan", new()
        { ["plan_version"] = 2, ["steps"] = new JsonArray(PlanStep("cycle-a", "Cycle A", a, "cycle-b"), PlanStep("cycle-b", "Cycle B", b, "cycle-a")) }));
        var altered = Assignment("verify", b); altered["dependencies"] = new JsonArray();
        Reject("dispatch cannot erase a published dependency", () => Tool(manager, "bridge_dispatch_task", altered));
        var queued = Tool(manager, "bridge_dispatch_task", Assignment("verify", b));
        Check("dependent assignment is saved without waking its worker", queued["status"]!.ToString() == "queued" && Session(b).Sent.Count == 0);
        Tool(manager, "bridge_dispatch_task", Assignment("verify", b));
        Check("duplicate task ID cannot double-dispatch", Tool(manager, "bridge_list_tasks")["tasks"]!.AsArray().Count == 2 && Session(b).Sent.Count == 0);
        var different = Assignment("verify", b); different["message"] = "Different assignment";
        Reject("same task ID cannot hide different instructions", () => Tool(manager, "bridge_dispatch_task", different));
        Reject("unstarted dependency cannot claim completion", () => Tool(b, "bridge_update_task", new()
        { ["task_id"] = "verify", ["status"] = "completed", ["summary"] = "Not actually started" }));
        Tool(manager, "bridge_dispatch_task", Assignment("calculate", a, "Calculate result"));
        PumpUntil(() => Session(a).Sent.Count == 1);
        Check("current plan step is shown independently of activity", vm.SharedBridgeTerminal!.CurrentPlanStep.Contains("Calculate result") && vm.SharedBridgeTerminal.CompletedSteps == 0);
        Tool(a, "bridge_report_activity", new() { ["summary"] = "Still calculating" });
        Check("activity never increments plan completion", vm.SharedBridgeTerminal!.CompletedSteps == 0);
        Reject("plan cannot be silently rewritten after dispatch", () => Tool(manager, "bridge_set_plan", new()
        { ["plan_version"] = 2, ["steps"] = new JsonArray() }));
        Reject("another worker cannot complete this assignment", () => Tool(b, "bridge_update_task", new()
        { ["task_id"] = "calculate", ["status"] = "completed", ["summary"] = "Spoofed" }));
        Tool(a, "bridge_update_task", new() { ["task_id"] = "calculate", ["status"] = "completed", ["summary"] = "Computed 42", ["evidence"] = "17+25=42; 42-25=17" });
        PumpUntil(() => Session(b).Sent.Count == 1);
        Check("dependency completion releases exactly one queued assignment", vm.SharedBridgeTerminal!.CompletedSteps == 1 && Session(b).Sent.Count == 1);
        Check("overlapping file activity is permitted and visible", Tool(manager, "bridge_list_tasks")["tasks"]!.AsArray().All(t => t!["files"]![0]!.ToString() == "shared.cs") &&
            Session(b).Sent[0].Contains("other agents may edit the same files"));
        Tool(b, "bridge_update_task", new() { ["task_id"] = "verify", ["status"] = "blocked", ["summary"] = "Need a dependency clarification" });
        FinishWork(b, "Blocked on a clarification.");
        Check("a finished model turn cannot turn a blocked task into completed work", Tool(manager, "bridge_list_tasks")["completed_steps"]!.GetValue<int>() == 1);
        Reject("unfinished durable work blocks approval", () => Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Premature" }));
        var original = Tool(manager, "bridge_list_tasks");
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == manager.SessionId);
        var restored = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(saved.Work))!;
        restored.Recover();
        foreach (var chat in team) Property(chat, "BridgeWork", restored);
        Check("recovery preserves task identity, instructions, evidence and dependencies", restored.Tasks[0].Evidence.Contains("42-25") && restored.Tasks[1].Dependencies.SequenceEqual(new[] { "calculate" }) && restored.Tasks[1].Instruction.Length > 0);
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "verify" });
        PumpUntil(() => Session(b).Sent.Count == 2);
        Check("explicit retry preserves ID and warns about previous side effects", Session(b).Sent[^1].Contains("Task verify") && Session(b).Sent[^1].Contains("RECOVERY:"));
        Tool(b, "bridge_update_task", new() { ["task_id"] = "verify", ["status"] = "completed", ["summary"] = "Simple independent check passed" });
        Check("simple tasks can complete without evidence", vm.SharedBridgeTerminal!.CompletedSteps == 2 && restored.Tasks[1].Evidence == "");
        FinishWork(a); FinishWork(b);
        var recoveredAgain = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(restored))!;
        recoveredAgain.Tasks.Add(new BridgeWorkTask { Id = "uncertain", State = "dispatching", Instruction = "May already have written output" });
        recoveredAgain.Tasks.Add(new BridgeWorkTask { Id = "waiting", State = "queued", Instruction = "Queued before crash" });
        recoveredAgain.Recover();
        Check("crash recovery never automatically replays uncertain or queued work", recoveredAgain.Tasks.Where(t => t.Id is "uncertain" or "waiting").All(t => t.State == "interrupted"));
        RenderWorkflow(vm);
    }

    private static void VerifyOneShotOrchestrators()
    {
        var (vm, team) = WorkTeam("setup-only", 4);
        var a = team[0]; var aw = team[1]; var b = team[2]; var bw = team[3];
        Property(b, "IsBridgeManager", true); Property(b, "BridgeCoordinatesOnly", true);
        Property(bw, "BridgeCoordinatorAgentId", b.BridgeAgentId);
        a.Status = b.Status = "running";
        Tool(a, "bridge_set_plan", new() { ["plan_version"] = 1, ["steps"] = new JsonArray(PlanStep("a-step", "First group", aw)) });
        Tool(b, "bridge_set_plan", new() { ["plan_version"] = 2, ["steps"] = new JsonArray(PlanStep("b-step", "Second group", bw, "a-step")) });
        Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "I calculate first; your group verifies. shared.cs is editable by both groups." });
        Tool(b, "bridge_send_message", new() { ["recipient"] = a.BridgeAgentId, ["message"] = "My group verifies your result, after a-step. Shared editing remains allowed." });
        Reject("orchestrator may send only one setup message per peer", () => Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Second setup message" }));
        Tool(a, "bridge_read_messages"); Tool(b, "bridge_read_messages");
        Reject("multiple orchestrators must acknowledge an exact version", () => Tool(a, "bridge_agree_scope", new() { ["scope"] = "Calculate" }));
        Reject("old plan confirmation is rejected", () => Tool(a, "bridge_agree_scope", new() { ["scope"] = "Calculate", ["plan_version"] = 2 }));
        Tool(a, "bridge_agree_scope", new() { ["scope"] = "Calculate", ["plan_version"] = 3 });
        Reject("one confirmation cannot start shared work", () => Tool(a, "bridge_dispatch_task", Assignment("a-step", aw)));
        Tool(b, "bridge_agree_scope", new() { ["scope"] = "Verify", ["plan_version"] = 3 });
        Check("all orchestrators confirm the exact same plan", Tool(a, "bridge_list_agents")["coordination_ready"]!.GetValue<bool>());
        Tool(a, "bridge_dispatch_task", Assignment("a-step", aw));
        Reject("dispatch permanently closes direct orchestrator communication", () => Tool(b, "bridge_send_message", new() { ["recipient"] = a.BridgeAgentId, ["message"] = "Post-dispatch chatter" }));
        Tool(aw, "bridge_send_message", new() { ["recipient"] = bw.BridgeAgentId, ["message"] = "Worker dependency result: 42" });
        Check("workers still communicate across orchestrator groups", Tool(bw, "bridge_read_messages")["messages"]![0]!["message"]!.ToString().Contains("42"));
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == a.SessionId).Work!;
        var restore = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(saved))!;
        restore.Recover();
        foreach (var chat in team) { Property(chat, "BridgeWork", restore); Property(chat, "BridgeCoordinationRoster", ""); }
        Check("saved setup confirmations survive recovery", Tool(a, "bridge_list_agents")["coordination_ready"]!.GetValue<bool>() && restore.SetupMessages.Count == 2);
        Reject("recovery cannot reopen orchestrator messaging", () => Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Recovered chatter" }));
        Check("recovered assignment stays interrupted until explicit retry", Tool(a, "bridge_list_tasks")["tasks"]![0]!["status"]!.ToString() == "interrupted");
    }

    private static void VerifyGraphSimulations()
    {
        var watch = Stopwatch.StartNew();
        for (var seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            var tasks = Enumerable.Range(0, 100).Select(i => new BridgeWorkTask { Id = "task-" + i,
                Dependencies = Enumerable.Range(0, i).Where(_ => random.Next(20) == 0).Select(n => "task-" + n).ToList() }).ToList();
            BridgeWorkState.ValidateGraph(tasks);
            tasks[0].Dependencies.Add("task-99"); tasks[99].Dependencies.Add("task-0");
            var rejected = false;
            try { BridgeWorkState.ValidateGraph(tasks); } catch (InvalidOperationException) { rejected = true; }
            Check($"dependency simulation {seed}: valid graph accepted and cycle rejected", rejected);
        }
        Console.WriteLine($"MEASURED: 150 randomized 100-task dependency simulations: {watch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static void VerifyStorageFailuresAndReassignment()
    {
        var (vm, team) = WorkTeam("storage-failure");
        var manager = team[0]; var worker = team[1]; var replacement = team[2];
        var settingsPath = (string)typeof(AppSettings).GetProperty("FilePath", Flags)!.GetValue(null)!;
        AppSettings.Current.Save();
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Reject("storage failure prevents a new assignment from executing", () => Tool(manager, "bridge_dispatch_task", Assignment("save-failure", worker)));
            Check("failed journal write never prompts the worker", Session(worker).Sent.Count == 0);
        }
        Tool(manager, "bridge_dispatch_task", Assignment("save-failure", worker));
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Check("retrying a lost dispatch receipt saves and starts just one assignment", Tool(manager, "bridge_list_tasks")["tasks"]!.AsArray().Count == 1);
        worker.Status = "error";
        Check("provider failure records a failed durable task", Tool(manager, "bridge_list_tasks")["tasks"]![0]!["status"]!.ToString() == "failed");
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "save-failure", ["recipient"] = replacement.BridgeAgentId });
        PumpUntil(() => Session(replacement).Sent.Count == 1);
        Check("replacement worker recovers the same assignment identity", Tool(manager, "bridge_list_tasks")["tasks"]![0]!["owner_id"]!.ToString() == replacement.BridgeAgentId && Session(replacement).Sent[0].Contains("Task save-failure"));

        var workspace = Path.Combine(_root, "mail-fault-" + Guid.NewGuid().ToString("N"));
        var run = Guid.NewGuid().ToString("N");
        var store = new BridgeMailboxStore(workspace, run);
        var a = store.Register(1, "A", "a"); var b = store.Register(2, "B", "b");
        store.Deliver(a, b, "Setup division", 1, DateTimeOffset.UtcNow, setupMessage: true);
        var restarted = new BridgeMailboxStore(workspace, run);
        var restoredA = restarted.Register(1, "A", "a"); var restoredB = restarted.Register(2, "B", "b");
        var blocked = false;
        try { restarted.Deliver(restoredA, restoredB, "Another proposal after recovery", 1, DateTimeOffset.UtcNow, setupMessage: true); }
        catch (InvalidOperationException) { blocked = true; }
        Check("mail journal itself enforces one-shot setup across a settings-save crash window", blocked && restoredB.Messages.Count == 1);
        File.WriteAllText(restoredB.FilePath, "User replaced this file");
        var refused = false;
        try { restarted.Deliver(restoredA, restoredB, "Must not overwrite user content", 1, DateTimeOffset.UtcNow); }
        catch (IOException) { refused = true; }
        Check("mailbox write failure preserves both prior messages and user files", refused && restoredB.Messages.Count == 1 && File.ReadAllText(restoredB.FilePath) == "User replaced this file");
    }

    private static void RenderWorkflow(MainViewModel vm)
    {
        Property(vm, "ActiveChat", vm.BridgePanes[0]);
        Property(vm, "ShowBridge", true);
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        Application.Current.Resources["BoolVis"] = new BooleanToVisibilityConverter();
        Application.Current.Resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var shell = (VibeCode.MainWindow)typeof(VibeCode.MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        SaveLiveShell(shell, 1100, 720, "workflow-progress-dark.png");
        var panel = (BridgeSharedTerminal)shell.FindName("BridgeSharedPanel");
        foreach (var expander in Descendants<Expander>(panel)) expander.IsExpanded = true;
        SaveLiveShell(shell, 900, 720, "workflow-progress-narrow.png");
        Check("plan UI binds actual complete step counts", vm.SharedBridgeTerminal!.PlanProgressText == "2 of 2 steps complete");
    }

    private static void VerifyConcurrentBridgeTransport()
    {
        var (_, team) = WorkTeam("concurrent-transport");
        var pipe = (string)Property(Call(team[0], "EnsureBridgeMcp")!, "PipeName")!;
        var watch = Stopwatch.StartNew();
        var calls = Enumerable.Range(0, 24).Select(_ => VibeCode.AgentStatus.Mcp.Bridge.BridgeMcpClient.InvokeAsync(pipe, "bridge_list_tasks", new())).ToArray();
        PumpUntil(() => calls.All(t => t.IsCompleted), 35000);
        Check("24 simultaneous named-pipe calls each receive the correct bridge receipt", calls.All(t => t.IsCompletedSuccessfully && t.Result["plan_version"]!.GetValue<int>() == 1));
        Console.WriteLine($"MEASURED: 24 simultaneous local bridge calls: {watch.Elapsed.TotalMilliseconds:F1} ms.");
    }
}
