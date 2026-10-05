using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    /// <summary>
    /// Real GPT-6 Luna (low effort) turns against the production bridge MCP endpoint. Peers are passive in-process
    /// panes whose edits enter the log through the same capture path a live provider uses. The question in every case
    /// is whether a small model understands the edit log well enough to act on it.
    /// </summary>
    private static void RunLiveEditLogSimulations(string? caseFilter)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live simulations require the current signed-in CODEX_HOME.");
        AppSettings.Current.DefaultProvider = "codex";
        AppSettings.Current.DefaultCodexModel = "gpt-6-luna";
        AppSettings.Current.DefaultCodexEffort = "low";
        AppSettings.Current.SecondBrainEnabled = false;
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        var reportPath = Path.Combine(_root, "luna-edit-log-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        var results = new List<object>();
        var failures = new List<string>();
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            model = "gpt-6-luna", effort = "low",
            method = "Real authenticated Luna turns using the production bridge MCP endpoint and routing. Peer edits are recorded through the same tool-result capture path live providers use; older history is appended to the same ledger.",
            results, failures,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Scenario("normal-who-changed-lines", 3, "plan", (vm, team, actor) =>
        {
            Write(team, "src/retry.ts", 40);
            Write(team, "src/http.ts", 20);
            SeedOld(team[0], "src/retry.ts", 28, 31, "Add request logging", TimeSpan.FromMinutes(50));
            SeedOld(team[0], "src/http.ts", 5, 9, "Add request logging", TimeSpan.FromMinutes(20));
            SeedLive(team[1], "src/retry.ts", 30, 2, "Tune retry backoff");
            return "Read-only bridge question: do not edit files and do not run shell commands. Before anyone changes src/retry.ts " +
                "lines 30-31, find out which bridge agent most recently changed those exact lines and what task that agent was working on. " +
                "Answer in one short sentence with the agent number and the task.";
        }, (actor, team, calls, reply) =>
        {
            Check("who-changed: Luna looks the lines up in the edit log", calls.Any(c => Is(c, "bridge_file_edits")));
            Check("who-changed: names the most recent editor, Agent 2", Regex.IsMatch(reply, @"\b2\b"));
            Check("who-changed: names that agent's task", reply.Contains("backoff", StringComparison.OrdinalIgnoreCase));
        });

        Scenario("normal-ask-before-overwriting", 3, "plan", (vm, team, actor) =>
        {
            Write(team, "src/retry.ts", 40);
            Write(team, "src/http.ts", 20);
            SeedLive(team[0], "src/retry.ts", 10, 5, "Rework retry delays");
            SeedLive(team[1], "src/http.ts", 3, 2, "Add request logging");
            return "You were asked to change the retry delay on line 12 of src/retry.ts. Before editing, check whether another bridge agent " +
                "changed that area recently. If one did, do not edit the file: send that agent one short direct message asking them to " +
                "make the change, then finish. Do not run shell commands.";
        }, (actor, team, calls, reply) =>
        {
            var lookup = Array.FindIndex(calls, c => Is(c, "bridge_file_edits"));
            var sends = calls.Where(c => Is(c, "bridge_send_message")).ToArray();
            Check("ask-first: Luna checks the edit log before acting", lookup >= 0 && (sends.Length == 0 || lookup < Array.IndexOf(calls, sends[0])));
            Check("ask-first: sends exactly one direct message", sends.Length == 1 && calls.All(c => !Is(c, "bridge_broadcast")));
            Check("ask-first: the message goes to Agent 1, who owns those lines", team[0].UnreadPeerMessageCount == 1 && team[1].UnreadPeerMessageCount == 0);
            Check("ask-first: Luna does not edit the file itself", EditTools(actor).Length == 0);
        });

        Scenario("advanced-orchestrator-reviews-worker", 3, "plan", (vm, team, actor) =>
        {
            Property(actor, "IsBridgeManager", true);
            Property(actor, "BridgeCoordinatesOnly", true);
            foreach (var worker in team) Property(worker, "BridgeCoordinatorAgentId", actor.BridgeAgentId);
            vm.SetBridgeTerminalMode(true);
            var work = (BridgeWorkState)Property(actor, "BridgeWork")!;
            work.DispatchStarted = true;
            work.Scopes[actor.BridgeAgentId] = "Login fix and banner polish";
            work.ConfirmedVersions[actor.BridgeAgentId] = work.PlanVersion;
            Write(team, "src/auth/login.ts", 80);
            Write(team, "src/auth/session.ts", 30);
            Write(team, "src/ui/banner.tsx", 20);
            SeedLive(team[1], "src/auth/login.ts", 40, 13, "Fix login redirect");
            SeedLive(team[1], "src/auth/session.ts", 7, 3, "Fix login redirect");
            SeedLive(team[2], "src/ui/banner.tsx", 3, 3, "Polish banner");
            return "Worker Agent 2 reports that its login fix is finished. Before reviewing anything, use the bridge tools to find exactly " +
                "which files and line ranges Agent 2 changed. Reply with only that list. Do not dispatch tasks, send messages, or run commands.";
        }, (actor, team, calls, reply) =>
        {
            Check("orchestrator: Luna reads the worker's edits from the log", calls.Any(c => Is(c, "bridge_file_edits")));
            Check("orchestrator: lists both of Agent 2's files with their lines", reply.Contains("login.ts") && Span(reply, 40, 52)
                && reply.Contains("session.ts") && Span(reply, 7, 9));
            Check("orchestrator: leaves out the other worker's file", !reply.Contains("banner", StringComparison.OrdinalIgnoreCase));
            Check("orchestrator: takes no other action", calls.All(c => !Is(c, "bridge_dispatch_task") && !Is(c, "bridge_send_message") && !Is(c, "bridge_broadcast")));
        }, managerBriefs: true);

        Scenario("advanced-worker-checks-cross-group-lines", 4, "plan", (vm, team, actor) =>
        {
            // Two groups: Agent 1 leads Agent 2 and Luna; Agent 3 leads Agent 4.
            foreach (var manager in new[] { team[0], team[2] })
            { Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true); }
            Property(team[1], "BridgeCoordinatorAgentId", team[0].BridgeAgentId);
            Property(actor, "BridgeCoordinatorAgentId", team[0].BridgeAgentId);
            Property(team[3], "BridgeCoordinatorAgentId", team[2].BridgeAgentId);
            vm.SetBridgeTerminalMode(true);
            var work = (BridgeWorkState)Property(actor, "BridgeWork")!;
            work.DispatchStarted = true;
            foreach (var manager in new[] { team[0], team[2] })
            { work.Scopes[manager.BridgeAgentId] = "Assigned API lanes"; work.ConfirmedVersions[manager.BridgeAgentId] = work.PlanVersion; }
            Write(team, "src/api/client.ts", 60);
            SeedLive(team[3], "src/api/client.ts", 20, 6, "Add user caching");
            SeedLive(team[1], "src/api/types.ts", 4, 2, "Tidy API types");
            Property(actor, "BridgeTaskName", "Rename fetchUser");
            return "Your assignment: rename fetchUser in src/api/client.ts, which is around line 22. Before editing, check whether a teammate " +
                "changed those lines recently. If someone did, message that agent directly to coordinate and do not edit the file. " +
                "Do not run shell commands.";
        }, (actor, team, calls, reply) =>
        {
            Check("cross-group: Luna checks the edit log first", calls.Any(c => Is(c, "bridge_file_edits")));
            Check("cross-group: messages Agent 4, the worker in the other group who changed those lines",
                team[3].UnreadPeerMessageCount == 1 && team.Take(3).All(p => p.UnreadPeerMessageCount == 0) && calls.All(c => !Is(c, "bridge_broadcast")));
            Check("cross-group: does not edit the file", EditTools(actor).Length == 0);
        }, managerBriefs: true);

        Scenario("normal-real-edit-is-recorded", 2, "acceptEdits", (vm, team, actor) =>
        {
            var config = Path.Combine(team[0].Cwd, "src", "config.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "# service settings\nhost = localhost\nretries = 3\ntimeout = 30\nlog = info\n");
            return "In src/config.txt change the line `retries = 3` to `retries = 5`. Change nothing else and run no other commands. " +
                "Then finish with one short sentence.";
        }, (actor, team, calls, reply) =>
        {
            var config = Path.Combine(team[0].Cwd, "src", "config.txt");
            Check("real edit: Luna changed the file", File.ReadAllLines(config).ElementAtOrDefault(2) == "retries = 5");
            var logged = BridgeEditLedger.Read(team[0].Cwd, DateTimeOffset.UtcNow).Where(e => e.AgentId == actor.BridgeAgentId).ToArray();
            Console.WriteLine("real edit log: " + JsonSerializer.Serialize(logged));
            Check("real edit: the app logged it with Luna's identity and the exact line", logged.Length == 1 && logged[0].File == "src/config.txt"
                && logged[0].Lines == "3" && logged[0].Provider == "codex" && logged[0].Label == actor.BridgeLabel);
            var seen = Tool(team[0], "bridge_file_edits", new() { ["path"] = "config.txt", ["start_line"] = 3 });
            Check("real edit: a peer sees who changed line 3 through the MCP tool", seen["matched"]!.GetValue<int>() == 1
                && seen["edits"]![0]!["agent"]!.ToString() == actor.BridgeLabel && seen["edits"]![0]!["in_your_bridge"]!.GetValue<bool>());
            var row = Tool(team[0], "bridge_list_agents")["agents"]!.AsArray().Single(a => a!["agent_id"]!.ToString() == actor.BridgeAgentId)!;
            Check("real edit: the roster shows it under Luna's recent_edits", row["recent_edits"]!.AsArray().Any(r => r!.ToString().StartsWith("src/config.txt: 3 ")));
        });

        Scenario("normal-real-shell-edit-is-recorded", 2, "acceptEdits", (vm, team, actor) =>
        {
            var config = Path.Combine(team[0].Cwd, "src", "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, "[service]\nhost = localhost\nport = 8080\nretries = 3\ntimeout = 30\n");
            return "Using a single PowerShell shell command (not apply_patch), change the line `retries = 3` to `retries = 7` in " +
                "src/settings.ini. Change nothing else, run no other commands, then finish with one short sentence.";
        }, (actor, team, calls, reply) =>
        {
            var config = Path.Combine(team[0].Cwd, "src", "settings.ini");
            Check("real shell edit: Luna changed the file", File.ReadAllLines(config).ElementAtOrDefault(3) == "retries = 7");
            var logged = BridgeEditLedger.Read(team[0].Cwd, DateTimeOffset.UtcNow).Where(e => e.AgentId == actor.BridgeAgentId).ToArray();
            Console.WriteLine("real shell edit log: " + JsonSerializer.Serialize(logged));
            Check("real shell edit: logged once with Luna's identity and the exact line", logged.Length == 1 && logged[0].File == "src/settings.ini"
                && logged[0].Lines == "4" && logged[0].Label == actor.BridgeLabel);
            var seen = Tool(team[0], "bridge_file_edits", new() { ["path"] = "settings.ini", ["start_line"] = 4 });
            Check("real shell edit: a peer sees it through the MCP tool", seen["matched"]!.GetValue<int>() == 1
                && seen["edits"]![0]!["agent"]!.ToString() == actor.BridgeLabel);
        });

        Console.WriteLine("Live edit log report: " + reportPath);
        Check("all Luna edit log scenarios pass", results.Count > 0 && failures.Count == 0);

        void Scenario(string name, int peers, string mode, Func<MainViewModel, ChatViewModel[], ChatViewModel, string> arrange,
            Action<ChatViewModel, ChatViewModel[], JsonObject[], string> verify, bool managerBriefs = false)
        {
            if (caseFilter is not null && !caseFilter.Split(',').Contains(name, StringComparer.Ordinal)) return;
            Console.WriteLine("START: " + name + " / GPT-6 Luna low");
            var watch = Stopwatch.StartNew();
            var (vm, team) = Team("luna-edits-" + name, peers);
            foreach (var peer in team) { peer.Status = "running"; Property(peer, "BridgeTaskState", "working"); }
            var actor = new ChatViewModel(team[0].Cwd, title: "Bridge edit log simulation", provider: "codex")
                { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true, BridgeLabel = "Codex " + (peers + 1) };
            Chats.Add(actor); vm.Chats.Add(actor); vm.BridgePanes.Add(actor); Call(vm, "Track", actor);
            var calls = new List<JsonObject>();
            var handler = (Func<string, JsonObject, JsonObject>)Property(actor, "BridgeToolHandler")!;
            Property(actor, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                var entry = new JsonObject { ["tool"] = tool, ["input"] = input.DeepClone() };
                calls.Add(entry);
                try
                {
                    var result = handler(tool, input); entry["result"] = result.DeepClone();
                    Console.WriteLine(name + " | " + tool + " | " + input.ToJsonString());
                    return result;
                }
                catch (Exception ex) { entry["error"] = ex.Message; Console.WriteLine(name + " | REJECTED " + tool + " | " + ex.Message); throw; }
            }));
            string? failure = null;
            var reply = "";
            var prompt = "";
            try
            {
                prompt = arrange(vm, team, actor);
                if (managerBriefs) Call(vm, "RefreshBridgeManagerBriefs");
                actor.SetMode(mode); actor.Start();
                LiveWaitBudget(() => actor.Status is "idle" or "error", TimeSpan.FromSeconds(90), actor, 60_000);
                Check(name + ": live Luna session initializes", actor.Status == "idle" && actor.Model == "gpt-6-luna");
                var texts = actor.Items.OfType<TextItem>().Count();
                Check(name + ": request submitted", actor.Send(prompt));
                LiveWaitBudget(() => actor.Status is "idle" or "error" && actor.Items.OfType<TextItem>().Count() > texts && !actor.HasQueued,
                    TimeSpan.FromSeconds(240), actor, 60_000);
                Check(name + ": turn finishes successfully", actor.Status == "idle");
                if (!BridgeEditLedger.PendingWrites.Wait(10_000)) throw new TimeoutException("Edit log write did not finish.");
                reply = actor.Items.OfType<TextItem>().LastOrDefault()?.Text ?? "";
                Console.WriteLine(name + " | REPLY | " + reply);
                Check(name + ": no rejected bridge tool calls", calls.All(c => c["error"] is null));
                verify(actor, team, calls.ToArray(), reply);
            }
            catch (Exception ex)
            {
                failure = ex.Message; failures.Add(name + ": " + failure);
                Console.WriteLine("FAILED: " + name + " | " + failure);
            }
            finally
            {
                results.Add(new
                {
                    name, passed = failure is null, failure, prompt, reply, elapsed_seconds = watch.Elapsed.TotalSeconds,
                    input_tokens = actor.TotalIn, output_tokens = actor.TotalOut,
                    bridge_calls = calls.Select(c => c.DeepClone()).ToArray(),
                    tools = AllTools(actor).Select(t => t.Name).ToArray(),
                    edit_log = BridgeEditLedger.Read(team[0].Cwd, DateTimeOffset.UtcNow).Select(e => new { e.File, e.Lines, e.Label, e.Task, e.Role }).ToArray(),
                });
                Save();
                actor.Close();
                foreach (var peer in team) peer.Close();
            }
        }
    }

    private static bool Is(JsonObject call, string tool) => call["tool"]!.ToString() == tool;

    /// <summary>"40-52", "40–52" or "40 to 52" in a model's reply.</summary>
    private static bool Span(string reply, int start, int end) =>
        Regex.IsMatch(reply, $@"\b{start}\s*(-|–|—|to|through)\s*{end}\b");

    private static ToolItem[] AllTools(ChatViewModel chat) =>
        chat.Items.SelectMany(item => item is CompactToolGroupItem group ? group.Tools.Cast<ItemVm>() : new[] { item }).OfType<ToolItem>().ToArray();

    private static ToolItem[] EditTools(ChatViewModel chat) =>
        AllTools(chat).Where(t => t.Name is "Edit" or "MultiEdit" or "Write" or "CodexEdit" or "NotebookEdit").ToArray();

    private static void Write(ChatViewModel[] team, string relative, int lines)
    {
        var path = Path.Combine(team[0].Cwd, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => $"export const value{i} = {i};"));
    }

    /// <summary>A peer's edit landing now, through the live tool-result capture path (Claude-style structuredPatch).</summary>
    private static void SeedLive(ChatViewModel pane, string relative, int start, int count, string task)
    {
        Property(pane, "BridgeTaskName", task);
        var lines = new List<string> { " export const before = 0;" };
        lines.AddRange(Enumerable.Range(0, count).Select(i => $"+export const changed{i} = {i};"));
        lines.Add("-export const old = 0;");
        lines.Add(" export const after = 0;");
        IngestEdit(pane, Guid.NewGuid().ToString("N"), "Edit",
            new() { ["file_path"] = Path.Combine(pane.Cwd, relative.Replace('/', Path.DirectorySeparatorChar)), ["old_string"] = "old", ["new_string"] = "new" },
            new() { ["structuredPatch"] = new JsonArray(Hunk(start - 1, lines.ToArray())) });
    }

    /// <summary>Earlier history: an entry in the same ledger with an older timestamp.</summary>
    private static void SeedOld(ChatViewModel pane, string relative, int start, int end, string task, TimeSpan ago) =>
        BridgeEditLedger.Append(pane.Cwd, new BridgeEditEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12], At = DateTimeOffset.UtcNow - ago, AgentId = pane.BridgeAgentId,
            Agent = int.Parse(pane.BridgeLabel.Split(' ').Last()), Label = pane.BridgeLabel, Provider = pane.Provider, Role = "agent",
            Task = task, File = relative, Ranges = [new BridgeEditRange(start, end, end - start + 1, 1)], Added = end - start + 1, Removed = 1, Tool = "Edit",
        });
}
