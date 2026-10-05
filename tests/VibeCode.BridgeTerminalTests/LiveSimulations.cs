using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static readonly List<string> LiveEvents = [];

    private static void RunLiveSimulations(bool singleCase, bool onlyGroups = false, int? workflowCase = null)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live simulations require the current signed-in CODEX_HOME.");
        AppSettings.Current.DefaultProvider = "codex";
        AppSettings.Current.DefaultCodexModel = "gpt-6-luna";
        AppSettings.Current.DefaultCodexEffort = "low";
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        Application.Current.Resources["BoolVis"] = new BooleanToVisibilityConverter();
        Application.Current.Resources["ShowIf"] = new NonEmptyToVisibilityConverter();

        if (workflowCase is { } index)
        {
            RunLunaWorkflowCase(index);
            File.WriteAllLines(Path.Combine(_root, $"workflow-{index:00}-events.txt"), LiveEvents);
            return;
        }

        if (onlyGroups)
        {
            RunLiveTwoOrchestrators();
            File.WriteAllLines(Path.Combine(_root, "live-events-groups.txt"), LiveEvents);
            return;
        }

        RunLiveCase(singleCase ? "one-worker-confirmed" : "one-worker", 1, "SINGLE_WORKER_OK: 42", """
            Run a tiny text-only bridge smoke test. Use exactly one worker. Assign it one combined task:
            calculate 17 + 25 and independently verify the result by subtraction. It must use bridge_report_activity
            with a short task name, then finish with SINGLE_WORKER_OK: 42 only after both checks.
            The single combined assignment includes final verification; do not assign a second verification pass.
            After its real report arrives, relay that exact marker and stop. Use only bridge tools, no shell,
            files, web, native subagents, or status-board edits for this tiny simulation. Keep all replies brief.
            """);
        if (!singleCase) RunLiveCase("two-workers", 2, "PEER_HANDOFF_OK: 42", """
            Run a tiny text-only bridge communication smoke test with two workers. Keep replies brief and use
            only bridge tools (no shell, files, web, native subagents, or board edits).
            First dispatch Agent 2: compute 19 + 23, report a short task name using bridge_report_activity,
            send RESULT=42 to Agent 3 using bridge_send_message, then finish. Include Agent 3's stable ID in its task.
            After Agent 2 reports completion, dispatch Agent 3: use bridge_read_messages, confirm the actual
            incoming RESULT=42 message, mark it answered with bridge_mark_message, independently check 19+23,
            report a short task name, then finish with PEER_HANDOFF_OK: 42. If the message was already read in
            a mailbox notification turn, it remains in the retained inbox and can be verified there.
            Agent 3's assignment IS final verification. Once its actual result arrives, relay PEER_HANDOFF_OK: 42
            and stop; do not assign further verification or send acknowledgments that wake more turns.
            """);
        if (!singleCase) RunLiveCase("review-feedback", 2, "REVIEW_FEEDBACK_OK: 42", """
            Run a tiny text-only final-review test with your two workers. Use only bridge tools; no shell, files,
            web, board edits or native subagents. Keep replies and work orders short.
            First assign Agent 2 the task Create review fixture: return exactly DRAFT_SUM=41. This intentionally
            incorrect candidate is test data, not the true answer to 17+25. Wait for that worker's real result.
            Independently review it against 17+25=42. Record changes_requested with bridge_review_scope and
            a concrete finding that the draft is off by one. Dispatch a correction to Agent 2: independently
            calculate 17+25 and return CORRECTED_SUM=42. Wait for its real report.
            Then assign Agent 3 an independent final review of the corrected result: verify 17+25=42 and
            42-25=17, publish a short task_name, and return REVIEW_PASS: 42 after both checks pass.
            After its actual report, record approved with bridge_review_scope and the evidence. Finish with
            REVIEW_FEEDBACK_OK: 42. Do not assign more work or send acknowledgments after approval.
            """);
        if (!singleCase) RunLiveTwoOrchestrators();
        File.WriteAllLines(Path.Combine(_root, "events.txt"), LiveEvents);
    }

    private static void RunLiveCase(string name, int workers, string marker, string objective, Action<ChatViewModel[]>? prepare = null)
    {
        var caseWatch = Stopwatch.StartNew();
        Console.WriteLine($"START: {name}, GPT-6 Luna / low, {workers} worker(s).");
        var workspace = Path.Combine(Path.GetTempPath(), "VibeCode-bridge-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var vm = new MainViewModel();
        var host = new ChatViewModel(workspace, title: "Bridge verification · " + name, provider: "codex")
            { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true };
        // Plan mode keeps the tiny arithmetic tasks read-only; built-in bridge MCP tools stay available.
        host.SetMode("plan");
        Chats.Add(host); vm.Chats.Add(host); Call(vm, "Track", host); Property(vm, "ActiveChat", host);
        host.Start();
        LiveWait(() => host.Status is "idle" or "error", TimeSpan.FromSeconds(45), [host]);
        Check(name + ": account and provider initialize", host.Status == "idle");
        Console.WriteLine("LIVE MODELS: " + string.Join(", ", host.Models.Select(m => m.Value)));
        Check(name + ": requested Luna at low effort", host.Model == "gpt-6-luna" && host.Effort == "low");
        if (name == "one-worker")
        {
            var suggestion = BridgeTeamSuggestionService.SuggestAsync(host, "Calculate 17 + 25 and check it by subtraction.", 2);
            LiveWait(() => suggestion.IsCompleted, TimeSpan.FromSeconds(60), [host]);
            var choice = suggestion.GetAwaiter().GetResult();
            Check("live AI suggests one worker for a tiny task", choice.WorkerCount == 1);
            Console.WriteLine("SUGGESTION: " + choice.WorkerCount + " · " + choice.Reason);
        }
        vm.ActivateBridge("codex");
        while (vm.BridgePanes.Count < workers + 1) vm.AddBridgeAgent("codex");
        var team = vm.BridgePanes.ToArray();
        foreach (var peer in team.Skip(1)) Chats.Add(peer);
        LiveWait(() => team.All(p => p.Status is "idle" or "error"), TimeSpan.FromSeconds(45), team);
        Check(name + ": exact live team starts on Luna", team.Length == workers + 1 && team.All(p => p.Status == "idle" && p.Model == "gpt-6-luna"));
        prepare?.Invoke(team);
        var calls = new List<(string Agent, string Tool, JsonObject Input, JsonObject Result)>();
        foreach (var chat in team)
        {
            var handler = (Func<string, JsonObject, JsonObject>)Property(chat, "BridgeToolHandler")!;
            Property(chat, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                JsonObject result;
                try { result = handler(tool, input); }
                catch (Exception ex)
                {
                    Console.WriteLine($"TOOL_REJECTED | {name} | {chat.BridgeTerminalIdentity} | {tool} | {ex.Message}");
                    throw;
                }
                calls.Add((chat.BridgeTerminalIdentity, tool, (JsonObject)input.DeepClone(), (JsonObject)result.DeepClone()));
                var line = $"{name} | {chat.BridgeTerminalIdentity} | {tool} | {input.ToJsonString()}";
                LiveEvents.Add(line); Console.WriteLine(line);
                return result;
            }));
        }
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3)
            .Invoke([vm, null, true]);
        vm.ConfigureBridgeOrchestrator(host, workers, objective, true);
        var captured = false;
        var stable = Stopwatch.StartNew();
        bool HasVerifiedResult()
        {
            if (!name.StartsWith("workflow-", StringComparison.Ordinal))
                return host.Items.OfType<TextItem>().Any(t => t.Text.Contains(marker));
            // Assert the durable outcome and actual answer, not an arbitrary prose marker
            // which a model can omit even after correctly completing and reviewing the work.
            var answer = marker[(marker.LastIndexOf(' ') + 1)..];
            var finalText = host.Items.OfType<TextItem>().LastOrDefault()?.Text ?? "";
            return host.BridgeReviewState == "approved" &&
                Property(host, "BridgeWork") is BridgeWorkState work && work.Tasks.Count > 0 &&
                work.Tasks.All(t => t.State == "completed") &&
                System.Text.RegularExpressions.Regex.IsMatch(finalText, @"\b" + answer + @"\b");
        }
        LiveWait(() =>
        {
            if (!captured && calls.Any(c => c.Tool == "bridge_dispatch_task"))
            {
                SaveLiveShell(shell, 1400, 900, name + "-working.png");
                captured = true;
            }
            var finished = team.All(c => c.Status == "idle" && !c.HasQueued)
                && HasVerifiedResult();
            if (!finished) stable.Restart();
            return finished && stable.Elapsed > TimeSpan.FromSeconds(2);
        }, TimeSpan.FromMinutes(3), team);
        File.WriteAllText(Path.Combine(_root, name + "-diagnostic.json"), JsonSerializer.Serialize(team.Select(chat => new
        {
            agent = chat.BridgeTerminalIdentity, state = chat.Status, review = chat.BridgeReviewState,
            messages = chat.Items.OfType<TextItem>().Select(t => t.Text).ToArray(),
            tools = chat.Items.OfType<ToolItem>().Select(t => new { t.Name, t.Status, t.Result }).ToArray(),
        }), new JsonSerializerOptions { WriteIndented = true }));
        Check(name + ": coordinator returns verified result", HasVerifiedResult());
        Check(name + ": assignments used real MCP", calls.Count(c => c.Tool == "bridge_dispatch_task") >= workers);
        Check(name + ": workers published task labels", team.Skip(1).All(c => c.BridgeTaskName is not ("Ready" or "Awaiting assignment")));
        Check(name + ": actual final review is recorded", calls.Any(c => c.Tool == "bridge_review_scope" && c.Input["verdict"]?.ToString() == "approved") && host.BridgeReviewState == "approved");
        if (name.EndsWith("-confirmed", StringComparison.Ordinal))
            Check("coordinator yields instead of repeatedly polling", calls.Count(c => c.Agent == "Agent 1" && c.Tool is "bridge_list_agents" or "bridge_read_messages") <= 4);
        if (name == "two-workers")
            Check("live peer handoff was sent, read, and acknowledged", new[] { "bridge_send_message", "bridge_read_messages", "bridge_mark_message" }.All(t => calls.Any(c => c.Tool == t)));
        if (name == "review-feedback")
        {
            Check("live review identifies the intentionally defective draft", calls.Any(c => c.Tool == "bridge_review_scope" && c.Input["verdict"]?.ToString() == "changes_requested"));
            Check("review feedback causes a real correction assignment", calls.Count(c => c.Tool == "bridge_dispatch_task" && c.Input["recipient"]?.ToString() == team[1].BridgeAgentId) >= 2);
            Check("independent reviewer returns actual verification evidence", team[2].Items.OfType<TextItem>().Any(t => t.Text.Contains("REVIEW_PASS: 42")));
        }
        var idleBefore = team.Select(c => c.Items.OfType<UserItem>().Count()).ToArray();
        Tool(host, "bridge_send_message", new() { ["recipient"] = team[1].BridgeAgentId, ["message"] = "Retained test context for a future assignment; no reply needed." });
        var quiet = Stopwatch.StartNew();
        LiveWait(() => quiet.Elapsed > TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), team);
        Check(name + ": messaging a finished worker starts no extra model turn", team[1].Status == "idle" &&
            team[1].Items.OfType<UserItem>().Count() == idleBefore[1] && team[1].UnreadPeerMessageCount == 1);
        SaveLiveShell(shell, 1400, 900, name + "-complete.png");
        host.Draft = "Unsent draft retained while switching";
        foreach (var chat in team)
        {
            Call(shell, "OnSelectBridgeTerminal", new Button { DataContext = chat }, new RoutedEventArgs(Button.ClickEvent));
            Check(name + ": switch to " + chat.BridgeTerminalIdentity, chat.BridgePaneShown && team.Count(p => p.BridgePaneShown) == 1);
        }
        Check(name + ": switching preserves draft and provider", host.Draft == "Unsent draft retained while switching" && team.All(c => c.Status == "idle"));
        SaveLiveShell(shell, 900, 700, name + "-worker-narrow.png");
        var transcript = team.Select(chat => new
        {
            agent = chat.BridgeTerminalIdentity, task = chat.BridgeTaskName, model = chat.Model,
            status = chat.Status, input_tokens = chat.TotalIn, output_tokens = chat.TotalOut,
            task_state = chat.BridgeTaskState, review_state = chat.BridgeReviewState, review_summary = chat.BridgeReviewSummary,
            messages = chat.Items.OfType<TextItem>().Select(t => t.Text).ToArray(),
        }).ToArray();
        File.WriteAllText(Path.Combine(_root, name + ".json"), JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_root, name + "-tools.json"), JsonSerializer.Serialize(calls.Select(c => new { agent = c.Agent, tool = c.Tool, input = c.Input, result = c.Result }), new JsonSerializerOptions { WriteIndented = true }));
        var finalPlan = Tool(host, "bridge_list_tasks");
        Check(name + ": durable plan has no incomplete steps", finalPlan["total_steps"]!.GetValue<int>() > 0 && finalPlan["completed_steps"]!.GetValue<int>() == finalPlan["total_steps"]!.GetValue<int>());
        Check(name + ": no unnecessary shell commands or native subagents", team.SelectMany(c => c.Items.OfType<ToolItem>()).All(t =>
            !t.Name.Equals("Bash", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("shell", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("spawn_agent", StringComparison.OrdinalIgnoreCase)));
        if (name.StartsWith("workflow-", StringComparison.Ordinal)) AssertLunaWorkflow(int.Parse(name[9..]), calls, finalPlan);
        File.WriteAllText(Path.Combine(_root, name + "-plan.json"), finalPlan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_root, name + "-metrics.json"), JsonSerializer.Serialize(new
        {
            name, model = "gpt-6-luna", effort = "low", workers, elapsed_seconds = caseWatch.Elapsed.TotalSeconds,
            input_tokens = team.Sum(c => c.TotalIn), output_tokens = team.Sum(c => c.TotalOut), bridge_calls = calls.Count,
            completed_steps = finalPlan["completed_steps"]!.GetValue<int>(), passed = true,
            process_peak_bytes = Process.GetCurrentProcess().PeakWorkingSet64,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"COMPLETE: {name}; input={team.Sum(c => c.TotalIn)}, output={team.Sum(c => c.TotalOut)} tokens.");
        vm.CloseBridge();
        foreach (var chat in team) chat.Close();
    }

    private static void LiveWait(Func<bool> done, TimeSpan timeout, ChatViewModel[] team)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        Exception? error = null;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (team.Sum(c => c.TotalOut) > 6500) throw new InvalidOperationException("Simulation output budget exceeded.");
                if (done() || watch.Elapsed > timeout) frame.Continue = false;
            }
            catch (Exception ex) { error = ex; frame.Continue = false; }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (error is not null) throw error;
        if (!done())
        {
            foreach (var chat in team)
            {
                Console.WriteLine($"TIMEOUT: {chat.BridgeTerminalIdentity}, {chat.Status}, {chat.BridgeTaskName}");
                foreach (var text in chat.Items.OfType<TextItem>().TakeLast(2)) Console.WriteLine(text.Text);
                foreach (var banner in chat.Items.OfType<BannerItem>().TakeLast(2)) Console.WriteLine(banner.Text);
            }
            throw new TimeoutException("Live bridge simulation did not finish within " + timeout);
        }
    }

    private static void SaveLiveShell(MainWindow shell, int width, int height, string name)
    {
        shell.Width = width; shell.Height = height;
        shell.Measure(new Size(width, height)); shell.Arrange(new Rect(0, 0, width, height)); shell.UpdateLayout();
        var root = (FrameworkElement)shell.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        if (!name.EndsWith("-working.png", StringComparison.Ordinal))
        {
            var panel = (BridgeSharedTerminal)shell.FindName("BridgeSharedPanel");
            var list = (ListBox)panel.FindName("SharedTranscript");
            if (list.Items.Count > 0) list.ScrollIntoView(list.Items[list.Items.Count - 1]);
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var scroll in Descendants<ScrollViewer>(list).Take(1)) scroll.ScrollToEnd();
            root.UpdateLayout();
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
        Console.WriteLine("Rendered " + name);
    }
}
