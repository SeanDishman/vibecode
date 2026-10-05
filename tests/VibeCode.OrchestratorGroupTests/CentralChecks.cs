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
    static JsonNode[] ReadTraceRows(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var lastLine = text.LastIndexOf('\n');
        return lastLine < 0 ? [] : text[..lastLine].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
    }

    static string? CentralInput(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith("[CENTRAL ORCHESTRATOR INPUT]\n")) return text;
        if (node is JsonObject obj) return obj.Select(p => CentralInput(p.Value)).FirstOrDefault(s => s is not null);
        if (node is JsonArray array) return array.Select(CentralInput).FirstOrDefault(s => s is not null);
        return null;
    }

    static string? CentralFixtureReply(JsonNode request)
    {
        if (CentralInput(request) is not { } input) return null;
        if (Environment.GetEnvironmentVariable("REVIEW_CENTRAL_FAILURE") == "invalid") return "{\"assignments\":[]}";
        if (Environment.GetEnvironmentVariable("REVIEW_CENTRAL_DELAY") == "1") Thread.Sleep(1200);
        var groups = JsonNode.Parse(input[(input.IndexOf('\n') + 1)..])!["groups"]!.AsArray();
        return new JsonObject { ["assignments"] = new JsonArray(groups.Select((group, index) => (JsonNode)new JsonObject
        {
            ["agent_id"] = group!["agent_id"]!.DeepClone(), ["task_name"] = "Deliver area " + (index + 1),
            ["scope"] = group["existing_scope"]?.ToString() ?? "Own bounded area " + (index + 1) + " for " + group["agent_id"],
            ["exclusions"] = "Leave the other bounded areas to the other groups.",
            ["verification"] = "Run focused checks for this assigned area.",
            ["handoffs"] = "Share interfaces with peer workers; group one owns the final integration check.",
        }).ToArray()) }.ToJsonString();
    }

    static void PublishCentralPlan(ChatViewModel manager, ChatViewModel[] panes)
    {
        var current = Tool(manager, "bridge_list_tasks");
        Tool(manager, "bridge_set_plan", new()
        {
            ["plan_version"] = current["plan_version"]!.DeepClone(),
            ["steps"] = new JsonArray(panes.Where(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = "central-" + p.BridgeAgentId, ["title"] = "Verify assigned area", ["owner_id"] = p.BridgeAgentId,
            }).ToArray()),
        });
    }

    static void VerifyCentralHandoff(ChatViewModel host, ChatViewModel[] managers, BridgeAgentConfiguration config, int[] allocation, string label)
    {
        var central = Tool(managers[0], "bridge_list_tasks")["central_orchestrator"]!;
        Check(label + " central session removed after full handoff", central["state"]!.ToString() == "completed" && central["removed"]!.GetValue<bool>() &&
            managers.Any(m => m.Items.OfType<BannerItem>().Any(b => b.Text.Contains("Central orchestrator has been removed") && b.Text.Contains("task done"))));
        Check(label + " central model and thinking follow independent settings", central["provider"]!.ToString() == config.Provider &&
            central["model"]?.ToString() == config.Model && central["effort"]?.ToString() == config.Effort);
        Check(label + " central persists exact and distinct scope owners", central["assignments"]!.AsArray().Count == managers.Length &&
            managers.Select(m => m.BridgeOrchestrationScope).Distinct().Count() == managers.Length);
        var traces = Directory.GetFiles(Root, "trace-*.jsonl").Select(path => (Path: path, Rows: ReadTraceRows(path)))
            .Where(trace => trace.Rows.Any(n => CentralInput(n) is { } text && text.Contains(managers[0].BridgeAgentId))).ToArray();
        Check(label + " exactly one isolated central provider turn", traces.Length == 1 && traces[0].Rows.Count(n => CentralInput(n) is not null) == 1);
        var trace = traces.Single();
        var request = trace.Rows.Single(n => CentralInput(n) is not null);
        var input = CentralInput(request)!;
        var groups = JsonNode.Parse(input[(input.IndexOf('\n') + 1)..])!["groups"]!.AsArray();
        Check(label + " central receives original task and allocation", groups.Select(g => g!["worker_count"]!.GetValue<int>()).SequenceEqual(allocation) &&
            groups.All(g => g!["objective"]!.ToString().Contains("shared group objective")));
        if (config.Provider == "codex")
        {
            var parameters = request["params"]!;
            Check(label + " real Codex central request uses selected settings and assignment schema", parameters["model"]?.ToString() == config.Model &&
                parameters["effort"]?.ToString() == config.Effort && parameters["outputSchema"]?["properties"]?["assignments"] is JsonObject);
            var args = trace.Rows.First()["fixture_args"]!.AsArray().Select(v => v!.ToString()).ToArray();
            Check(label + " central Codex tools and subagents disabled", args.Contains("features.shell_tool=false") && args.Contains("features.multi_agent=false"));
        }
        else
        {
            var args = trace.Rows.First()["fixture_args"]!.AsArray().Select(v => v!.ToString()).ToArray();
            var effortIndex = Array.IndexOf(args, "--effort");
            Check(label + " real Claude central launch uses selected settings and no tools", args[Array.IndexOf(args, "--model") + 1] == config.Model &&
                (effortIndex < 0 ? null : args[effortIndex + 1]) == config.Effort && args.Contains("--tools") && args[Array.IndexOf(args, "--tools") + 1] == "");
        }
        var pid = int.Parse(Path.GetFileNameWithoutExtension(trace.Path)["trace-".Length..]);
        bool Closed() { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
        Pump(Closed);
        Check(label + " central process exits before group work", Closed());
    }

    static void VerifyCentralParsing()
    {
        BridgeOrchestratorGroup[] groups = [new("a", 3, "Build and verify mobile remote coding"), new("b", 3, "Build and verify mobile remote coding")];
        var request = new JsonObject { ["text"] = "[CENTRAL ORCHESTRATOR INPUT]\n" + JsonSerializer.Serialize(new
        {
            groups = groups.Select(g => new { agent_id = g.AgentId, worker_count = g.WorkerCount, objective = g.Objective })
        }) };
        var valid = CentralFixtureReply(request)!;
        Check("central accepts a complete distinct division", BridgeCentralOrchestratorService.TryParse(valid, groups, out var parsed) && parsed.Count == 2);
        var json = JsonNode.Parse(valid)!;
        json["assignments"]![1]!["agent_id"] = "a";
        Check("central rejects duplicate or missing owners", !BridgeCentralOrchestratorService.TryParse(json.ToJsonString(), groups, out _));
        json = JsonNode.Parse(valid)!; json["assignments"]![1]!["scope"] = json["assignments"]![0]!["scope"]!.ToString().ToUpperInvariant();
        Check("central rejects duplicate scope text", !BridgeCentralOrchestratorService.TryParse(json.ToJsonString(), groups, out _));
        json = JsonNode.Parse(valid)!; json["assignments"]![0]!["verification"] = "";
        Check("central rejects missing verification", !BridgeCentralOrchestratorService.TryParse(json.ToJsonString(), groups, out _));
        json = JsonNode.Parse(valid)!; json["assignments"]![0]!["scope"] = groups[0].Objective;
        Check("central rejects assigning the entire objective unchanged", !BridgeCentralOrchestratorService.TryParse(json.ToJsonString(), groups, out _));
        Check("central rejects malformed JSON", !BridgeCentralOrchestratorService.TryParse("not a division", groups, out _));
        var work = new BridgeWorkState { CentralPlan = new() { State = "planning" } };
        work.Recover();
        Check("recovery never reruns an interrupted central session", work.CentralPlan.State == "interrupted");
        var completed = new BridgeWorkState { CentralPlan = new() { State = "completed", Assignments = parsed.ToList(), PublishedPlans = ["a", "b"] } };
        var restored = JsonSerializer.Deserialize<BridgeWorkState>(JsonSerializer.Serialize(completed))!;
        restored.Recover();
        Check("completed central division survives recovery without a new planner", restored.CentralPlan!.State == "completed" &&
            restored.CentralPlan.Assignments.SequenceEqual(parsed) && restored.CentralPlan.PublishedPlans.SetEquals(new[] { "a", "b" }));
    }

    static void VerifyCentralFailure()
    {
        Environment.SetEnvironmentVariable("REVIEW_CENTRAL_FAILURE", "invalid");
        try
        {
            var vm = new MainViewModel();
            var host = Chat("central-failure", "codex"); host.Model = "gpt-6.1-sol"; host.Effort = "high";
            Call(vm, "Track", host);
            vm.Chats.Add(host); Set(vm, "ActiveChat", host); host.Start(); Pump(() => host.Status == "idle" && host.SessionId is not null);
            vm.ActivateBridge("codex");
            var managers = vm.ConfigureBridgeOrchestrators(host, [1, 1], "Split login and storage verification", true);
            foreach (var pane in vm.BridgePanes) Chats.Add(pane);
            Pump(() => Tool(host, "bridge_list_tasks")["central_orchestrator"]?["state"]?.ToString() == "failed");
            Check("invalid central output starts neither permanent objective", managers.All(m => !m.Items.OfType<UserItem>().Any()));
            Check("failure is visible with retry guidance", host.Items.OfType<BannerItem>().Any(b => b.Text.Contains("Worker dispatch is paused") && b.Text.Contains("Start a new team")));
            Check("failure names the planner and exactly what was wrong with its answer", host.Items.OfType<BannerItem>().Any(b =>
                b.Text.Contains("high effort") && b.Text.Contains("0 assignment(s) for 2 orchestrators")));
            RenderCentralNotice(Shell(vm), host, "central-failure");
            RejectTool("failed handoff cannot be bypassed by dispatch", () => Dispatch(host, vm.BridgePanes.First(p => p.BridgeCoordinatorAgentId == host.BridgeAgentId)));
            RejectTool("failed handoff cannot be bypassed by publishing", () => PublishCentralPlan(host, vm.BridgePanes.ToArray()));
        }
        finally { Environment.SetEnvironmentVariable("REVIEW_CENTRAL_FAILURE", null); }
    }

    /// <summary>A failed split says where it stopped and why instead of only "timed out", and the planner is cut off
    /// only once it has actually gone quiet (or hit the 30-minute ceiling).</summary>
    /// <summary>Orchestrators are useless without bridge tools. Each Claude agent launches the single-file VibeCode.exe
    /// as its bridge helper (~1.6 s idle, 3-5 s when a Bridge starts several agents at once); the inherited 5 s budget
    /// dropped the bridge for some agents with CONNECT_TIMEOUT, so the launch budget must stay realistic.</summary>
    static void VerifyBridgeMcpLaunchBudget()
    {
        var type = typeof(BridgeCentralOrchestratorService).Assembly.GetType("VibeCode.Services.BridgeMcpConnection")!;
        var connection = Activator.CreateInstance(type, F, null, new object[] { Dispatcher.CurrentDispatcher,
            (Func<string, JsonObject, JsonObject>)((_, _) => new JsonObject()) }, null)!;
        var registration = (McpServerDefinition)type.GetMethod("Registration", F)!.Invoke(connection, null)!;
        Check("bridge helper gets at least 30 s to start", registration.StartupTimeoutSeconds >= 30);
        Check("bridge tool calls outlast the bridge's own 30 s request limit", registration.ToolTimeoutSeconds > 30);
        var start = (ProcessStartInfo)typeof(VibeCode.Protocol.ClaudeSession).GetMethod("CreateStartInfo", F)!
            .Invoke(null, new object[] { new VibeCode.Protocol.ClaudeSessionOptions { Cwd = Root, McpServers = [registration] } })!;
        Check("Claude agents launch with a 30 s MCP connect budget", int.Parse(start.Environment["MCP_TIMEOUT"]!) >= 30_000);
        ((IDisposable)connection).Dispose();
    }

    static void VerifyCentralDiagnostics()
    {
        VerifyBridgeMcpLaunchBudget();
        const string Planner = "The central orchestrator (Claude Code · Opus 5.5 · max effort)";
        var type = typeof(BridgeCentralOrchestratorService).Assembly.GetType("VibeCode.Services.CentralPlannerProgress")!;
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        object New() => Activator.CreateInstance(type, F, null, new object?[] { (Func<DateTime>)(() => now) }, null)!;
        object? Invoke(object target, string method, params object?[] args) => type.GetMethod(method, F)!.Invoke(target, args);
        string? Limit(object progress) => (string?)Invoke(progress, "CheckLimits", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
        string Timeout(object progress) => (string)Invoke(progress, "DescribeTimeout", Planner)!;

        var never = New();
        now = now.AddMinutes(5);
        Check("central planner that never starts is stopped after five quiet minutes", Limit(never) == "inactive");
        Check("it is reported as never starting, naming the planner",
            Timeout(never).Contains("never finished starting") && Timeout(never).Contains("max effort"));

        now = now.AddHours(1);
        var silent = New();
        Invoke(silent, "Ready"); Invoke(silent, "Sent");
        now = now.AddMinutes(5);
        Limit(silent);
        Check("central planner that got the task and sent nothing back points at usage limits",
            Timeout(silent).Contains("sent nothing back") && Timeout(silent).Contains("usage"));

        now = now.AddHours(1);
        var thinking = New();
        Invoke(thinking, "Ready"); Invoke(thinking, "Sent");
        for (var minute = 1; minute <= 12; minute++)
        {
            now = now.AddMinutes(1);
            Invoke(thinking, "Observe", JsonNode.Parse($$"""{"type":"system","subtype":"thinking_tokens","estimated_tokens":{{minute * 2000}}}"""));
        }
        Check("central planner still thinking after 12 minutes is not cut off", Limit(thinking) is null);
        now = now.AddMinutes(5);
        Check("central planner is stopped once it goes quiet for five minutes", Limit(thinking) == "inactive");
        Check("a stall reports how long it worked and how much it thought", Timeout(thinking).Contains("went silent")
            && Timeout(thinking).Contains("24,000 thinking tokens") && Timeout(thinking).Contains("worked for 12 min"));

        now = now.AddHours(1);
        var marathon = New();
        Invoke(marathon, "Ready"); Invoke(marathon, "Sent");
        for (var minute = 1; minute <= 30; minute++)
        {
            now = now.AddMinutes(1);
            Invoke(marathon, "Observe", JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"still planning"}]}}"""));
        }
        Check("central planner busy for 30 minutes hits the work ceiling", Limit(marathon) == "work");
        Check("the ceiling message advises a lower effort", Timeout(marathon).Contains("still working after 30 min")
            && Timeout(marathon).Contains("lower the central orchestrator's effort"));

        var exited = (string)Invoke(New(), "DescribeExit", Planner, 1, "Error: not logged in")!;
        Check("an exited planner reports its exit code and last error output", exited.Contains("code 1") && exited.Contains("not logged in"));
        var errored = (string)Invoke(New(), "DescribeError", Planner,
            JsonNode.Parse("""{"type":"result","subtype":"error","is_error":true,"result":"Claude usage limit reached"}"""))!;
        Check("a provider error is quoted instead of a generic usage hint", errored.Contains("Claude usage limit reached"));

        BridgeOrchestratorGroup[] groups = [new("a", 2, "Ship the mobile app"), new("b", 2, "Ship the mobile app")];
        string Reason(string text)
        {
            BridgeCentralOrchestratorService.TryParse(text, groups, out _, out var reason);
            return reason;
        }
        Check("an unusable handoff names the broken rule",
            Reason("""{"assignments":[{"agent_id":"a"}]}""").Contains("1 assignment(s) for 2 orchestrators")
            && Reason("no json here").Contains("no JSON") && Reason("{oops}").Contains("malformed"));
    }

    static void VerifyCentralCancellation()
    {
        Environment.SetEnvironmentVariable("REVIEW_CENTRAL_DELAY", "1");
        try
        {
            var vm = new MainViewModel();
            var host = Chat("central-cancel", "codex"); host.Model = "gpt-6.1-sol"; host.Effort = "high";
            Call(vm, "Track", host); vm.Chats.Add(host); Set(vm, "ActiveChat", host);
            host.Start(); Pump(() => host.Status == "idle" && host.SessionId is not null); vm.ActivateBridge("codex");
            var managers = vm.ConfigureBridgeOrchestrators(host, [1, 1], "Divide implementation and integration checks", true);
            var panes = vm.BridgePanes.ToArray(); foreach (var pane in panes) Chats.Add(pane);
            var central = ((BridgeWorkState)Property(host, "BridgeWork")!).CentralPlan!;
            string? trace = null;
            Pump(() => (trace = Directory.GetFiles(Root, "trace-*.jsonl").FirstOrDefault(path => ReadTraceRows(path)
                .Any(n => CentralInput(n)?.Contains(host.BridgeAgentId) == true))) is not null);
            Check("closing test observes an active central planner", central.State == "planning");
            vm.CloseBridge();
            Pump(() => central.State == "interrupted" && central.Cancellation is null);
            Check("closing bridge cancels central handoff without dispatch", managers.All(m => !m.Items.OfType<UserItem>().Any()) &&
                panes.All(p => !p.Items.OfType<UserItem>().Any()));
            var pid = int.Parse(Path.GetFileNameWithoutExtension(trace!)["trace-".Length..]);
            bool Closed() { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
            Pump(Closed);
            Check("closing bridge disposes the temporary provider process", Closed());
        }
        finally { Environment.SetEnvironmentVariable("REVIEW_CENTRAL_DELAY", null); }
    }

    static void RenderCentralNotice(MainWindow shell, ChatViewModel manager, string label)
    {
        var notice = manager.Items.OfType<BannerItem>().Last(b => b.Text.Contains("Central orchestrator"));
        foreach (var width in new[] { 400, 800 })
        {
            var content = new ContentControl { Content = notice,
                ContentTemplate = (DataTemplate)shell.FindResource(new DataTemplateKey(typeof(BannerItem))) };
            var border = new Border { Child = content, Padding = new Thickness(16), Background = (Brush)shell.FindResource("Bg1") };
            border.Measure(new Size(width, double.PositiveInfinity));
            var height = (int)Math.Ceiling(border.DesiredSize.Height);
            border.Arrange(new Rect(0, 0, width, height)); border.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(Root, $"{label}-{width}.png")); encoder.Save(stream);
            var text = Descendants<TextBlock>(content).Single(t => t.Text == notice.Text);
            Check(label + $" notice wraps at {width}px", text.TextWrapping == TextWrapping.Wrap && text.ActualHeight > 0 && text.ActualWidth <= width - 32);
        }
    }
}
