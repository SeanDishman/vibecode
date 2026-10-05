using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VibeCode;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static string _root = "";
    private static readonly List<ChatViewModel> Chats = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("app-server")) return RunProviderFixture();
        var recoveryChild = args.Contains("--crash-seed") || args.Contains("--crash-resume");
        var live = args.Contains("--live-simulations");
        var taskTitles = args.Contains("--task-title-only") || args.Contains("--task-title");
        var editLog = args.Contains("--edit-log-only") || args.Contains("--edit-log");
        _root = Path.Combine(Environment.CurrentDirectory, "artifacts", taskTitles ? "agent6-bridge-header" : editLog ? "agent6-edit-log" : "shared-agent-panel", live ? "live-simulations" : "verification");
        // Lets a concurrent run keep its renders and logs apart from evidence other agents already saved.
        if (!recoveryChild && Environment.GetEnvironmentVariable("VIBECODE_TEST_OUTPUT") is { Length: > 0 } output) _root = Path.GetFullPath(output);
        if (recoveryChild) _root = args[1];
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, recoveryChild ? "data" : "settings-" + Guid.NewGuid().ToString("N")));
        if (live) Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Environment.GetEnvironmentVariable("CODEX_HOME"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.BridgePeerMessaging = true;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        AppSettings.Current.BridgeAgentLimit = 9;
        try
        {
            if (args.Contains("--setup-only"))
            {
                VerifySetupStartup();
                Console.WriteLine($"PASS: {_checks} bridge setup startup and capacity checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--shared-composer-only"))
            {
                VerifySharedUsage();
                VerifyViews();
                Console.WriteLine($"PASS: {_checks} shared composer, usage and rendering checks. No live model calls.");
                return 0;
            }
            if (recoveryChild) return RunCrashRecoveryChild(args[0]);
            if (args.Contains("--pricing-only"))
            {
                VerifyFastPricing();
                Console.WriteLine($"PASS: {_checks} fast-mode pricing and usage accounting checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--edit-log-only"))
            {
                VerifyBridgeEditLog();
                Console.WriteLine($"PASS: {_checks} bridge edit log line, retention, capture and MCP checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--agent-status-only"))
            {
                VerifyBridgeAgentStatus();
                Console.WriteLine($"PASS: {_checks} bridge agent status MCP checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--fast-mode-isolation-only"))
            {
                VerifyFastModeIsolation();
                Console.WriteLine($"PASS: {_checks} per-chat fast mode checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--codex-user-input-only"))
            {
                VerifyCodexUserInput();
                Console.WriteLine($"PASS: {_checks} Codex request_user_input checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--codex-user-input-live"))
            {
                VerifyCodexUserInputLive();
                Console.WriteLine($"PASS: {_checks} live Codex request_user_input checks (one short Codex turn).");
                return 0;
            }
            if (args.Contains("--task-title-only"))
            {
                VerifyBridgeTaskTitles();
                VerifyBridgeHeaderStrip();
                Console.WriteLine($"PASS: {_checks} bridge task title, MCP and pane header checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--chat-metadata-only"))
            {
                VerifyChatMetadata();
                Console.WriteLine($"PASS: {_checks} chat protection, naming, persistence and UI checks.");
                return 0;
            }
            if (args.Contains("--jarvis-only"))
            {
                VerifyJarvisChatActions();
                Console.WriteLine($"PASS: {_checks} Jarvis parser, search, runtime and desktop chat action checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--jarvis-capabilities-only"))
            {
                VerifyJarvisSettingsAndNewChats();
                Console.WriteLine($"PASS: {_checks} Jarvis settings, MCP persistence and multi-chat simulations. No live model calls.");
                return 0;
            }
            if (args.Contains("--jarvis-actions-only"))
            {
                VerifyJarvisDesktopActions();
                Console.WriteLine($"PASS: {_checks} Jarvis desktop action and provider schema checks.");
                return 0;
            }
            if (args.Contains("--jarvis-desktop-only"))
            {
                VerifyJarvisDesktopActions();
                VerifyJarvisVoiceAndWindow();
                Console.WriteLine($"PASS: {_checks} Jarvis desktop, voice lifecycle and window checks. No live model calls or physical microphone recording.");
                return 0;
            }
            if (args.Contains("--transport-only"))
            {
                VerifyConcurrentBridgeTransport();
                return 0;
            }
            if (args.Contains("--broadcast-only"))
            {
                VerifyBroadcastMessaging();
                VerifyMessaging();
                VerifyProtocolAndProjection();
                Console.WriteLine($"PASS: {_checks} bridge broadcast, direct messaging and MCP checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--workflow-only"))
            {
                VerifyDurableWorkflow();
                Console.WriteLine($"PASS: {_checks} durable orchestration checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--crash-recovery-only"))
            {
                VerifyCrashRecovery();
                Console.WriteLine($"PASS: {_checks} bridge crash recovery checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--layout-restoration-only"))
            {
                VerifyLayoutRestoration();
                Console.WriteLine($"PASS: {_checks} bridge layout restoration checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--thinking-visibility-only"))
            {
                VerifyThinkingVisibility();
                Console.WriteLine($"PASS: {_checks} thinking visibility checks. No live model calls.");
                return 0;
            }
            if (args.Contains("--review-settings-only"))
            {
                VerifyReviewRuntimeChanges();
                Console.WriteLine($"PASS: {_checks} review settings runtime checks. No live model calls.");
                return 0;
            }
            if (live)
            {
                if (args.Contains("--edit-log"))
                {
                    var editCase = Array.IndexOf(args, "--edit-log-case");
                    RunLiveEditLogSimulations(editCase < 0 ? null : args[editCase + 1]);
                    Console.WriteLine($"PASS: {_checks} live GPT-6 Luna bridge edit log checks.");
                    return 0;
                }
                if (args.Contains("--task-title"))
                {
                    RunLiveTaskTitleSimulations();
                    Console.WriteLine($"PASS: {_checks} live GPT-6 Luna bridge task title checks.");
                    return 0;
                }
                if (args.Contains("--broadcast"))
                {
                    var broadcastFilter = Array.IndexOf(args, "--broadcast-case");
                    RunLiveBroadcastSimulations(broadcastFilter < 0 ? null : args[broadcastFilter + 1], args.Contains("--broadcast-stress"));
                    Console.WriteLine($"PASS: {_checks} live GPT-6 Luna broadcast decision and delivery checks.");
                    return 0;
                }
                if (args.Contains("--jarvis-capabilities"))
                {
                    RunLiveJarvisCapabilities(args.Contains("--ambiguity-only"));
                    Console.WriteLine($"PASS: {_checks} live Jarvis capability planning and isolated execution checks.");
                    return 0;
                }
                if (args.Contains("--jarvis-desktop"))
                {
                    RunLiveJarvisDesktop();
                    Console.WriteLine($"PASS: {_checks} live Jarvis desktop planning checks.");
                    return 0;
                }
                if (args.Contains("--chat-metadata"))
                {
                    RunLiveChatMetadata(args.Contains("--claude"));
                    Console.WriteLine($"PASS: {_checks} real {(args.Contains("--claude") ? "Claude" : "Luna")} chat naming checks.");
                    return 0;
                }
                var caseFlag = Array.IndexOf(args, "--workflow-case");
                RunLiveSimulations(args.Contains("--single-case"), args.Contains("--two-groups"), caseFlag >= 0 ? int.Parse(args[caseFlag + 1]) : null);
                Console.WriteLine($"PASS: {_checks} live GPT Luna simulation checks.");
                return 0;
            }
            VerifyBroadcastMessaging();
            VerifyMessaging();
            VerifyTerminals();
            VerifyLayoutRestoration();
            VerifyCrashRecovery();
            VerifySharedActivity();
            VerifyThinkingVisibility();
            VerifySharedUsage();
            VerifyOrchestratorGroups();
            VerifyMessagingRecovery();
            VerifyMailboxRetention();
            VerifyAgentAvailabilityAndReview();
            VerifyReviewLevels();
            VerifyReviewRuntimeChanges();
            VerifyOrchestrator();
            VerifySetupStartup();
            VerifySuggestions();
            VerifyProtocolAndProjection();
            VerifyFreshTeamStartup();
            VerifyViews();
            VerifyBridgeTaskTitles();
            VerifyBridgeHeaderStrip();
            VerifyBridgeEditLog();
            VerifyBridgeAgentStatus();
            VerifyFastModeIsolation();
            VerifyCodexUserInput();
            if (args.Contains("--replay-live")) ReplayLiveResults();
            Console.WriteLine($"PASS: {_checks} bridge terminal, orchestrator, MCP and rendering checks. No live model calls.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException : ex);
            return 1;
        }
        finally
        {
            foreach (var chat in Chats) chat.Close();
            app.Shutdown();
        }
    }

    private static void VerifyMessaging()
    {
        var (vm, team) = Team("messaging", 4);
        foreach (var chat in team) chat.Status = "running";
        var a = team[0]; var b = team[1]; var c = team[2];
        var listing = Tool(a, "bridge_list_agents");
        Check("roster identifies caller and every peer", listing["self_id"]!.ToString() == a.BridgeAgentId && listing["agents"]!.AsArray().Count == 4);
        var sent = Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Which response field contains the record ID?" });
        Check("send returns a queued receipt", sent["delivered"]!.AsArray().Count == 1 && sent["delivered"]![0]!["status"]!.ToString() == "queued");
        Check("busy recipient is not interrupted", Session(b).Interrupts == 0 && Session(b).Sent.Count == 0 && b.Status == "running");
        var incoming = Tool(b, "bridge_read_messages")["messages"]!.AsArray()[0]!;
        var id = incoming["message_id"]!.ToString();
        Check("inbox ties message to stable sender and marks read", incoming["sender_id"]!.ToString() == a.BridgeAgentId && incoming["status"]!.ToString().StartsWith("read"));
        Reject("caller cannot spoof sender", () => Tool(a, "bridge_send_message", new()
            { ["recipient"] = b.BridgeAgentId, ["message"] = "spoof", ["sender_id"] = c.BridgeAgentId }));
        Reject("sender cannot acknowledge outgoing message", () => Tool(a, "bridge_mark_message", new() { ["message_id"] = id }));
        Tool(b, "bridge_mark_message", new() { ["message_id"] = id });
        Check("acknowledgment is visible to sender", Tool(a, "bridge_read_messages")["messages"]![0]!["status"]!.ToString().StartsWith("answered"));
        Reject("duplicate send is rejected", () => Tool(a, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Which response field contains the record ID?" }));
        var (_, other) = Team("other-roster", 2);
        Reject("cross-roster recipient is rejected", () => Tool(c, "bridge_send_message", new() { ["recipient"] = other[0].BridgeAgentId, ["message"] = "Wrong bridge" }));
        AppSettings.Current.BridgePeerMessaging = false;
        Reject("messaging pause is enforced", () => Tool(c, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Paused" }));
        AppSettings.Current.BridgePeerMessaging = true;
        Tool(c, "bridge_broadcast", BroadcastArgs("The shared schema now includes the documented ID.", a, b, team[3]));
        Check("broadcast reaches only same-roster peers", Tool(b, "bridge_read_messages")["messages"]!.AsArray().Count == 2);
        // Idle workers retain useful peer context without starting another model turn.
        b.Draft = "unsent user draft";
        Tool(c, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "Can you verify the field type before I change the client?" });
        Call(b, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("idle worker inbox does not wake a model or disturb the draft", Session(b).Sent.Count == 0 && b.Draft == "unsent user draft" && b.UnreadPeerMessageCount == 1);
        Check("retained message is available to the next assignment", Tool(b, "bridge_read_messages")["messages"]!.AsArray().Any(m => m?["message"]?.ToString().Contains("verify the field type") == true));
        var pipe = (string)Property(Call(a, "EnsureBridgeMcp")!, "PipeName")!;
        var request = Task.Run(() => BridgeMcpClient.InvokeAsync(pipe, "bridge_list_agents", new()));
        PumpUntil(() => request.IsCompleted);
        Check("real named pipe resolves caller roster", request.GetAwaiter().GetResult()["self_id"]!.ToString() == a.BridgeAgentId);
        vm.RemoveBridgePane(c);
        Check("departed session loses roster access", Tool(c, "bridge_list_agents")["joined"]!.ToString() == "false");
        Reject("departed sender cannot send", () => Tool(c, "bridge_send_message", new() { ["recipient"] = b.BridgeAgentId, ["message"] = "stale" }));
    }

    private static void VerifyTerminals()
    {
        var (vm, team) = Team("terminals", 3);
        team[0].Draft = "first draft"; team[1].Draft = "second draft";
        foreach (var chat in team) chat.Status = "running";
        vm.SetBridgeTerminalMode(true);
        Check("single-terminal mode shows one chat", team.Count(p => p.BridgePaneShown) == 1 && team[0].BridgePaneShown);
        vm.SelectBridgeTerminal(team[1]);
        Check("selected chat and side panel stay aligned", team[1].BridgePaneShown && ReferenceEquals(vm.BridgePanelChat, team[1]));
        Check("switching keeps sessions and drafts", team[0].Draft == "first draft" && team[1].Draft == "second draft" && team.All(p => p.Status == "running" && !Session(p).HasExited));
        Tool(team[1], "bridge_report_activity", new() { ["summary"] = "Repairing the account switch path", ["task_name"] = "Fix account switch" });
        Check("agent task names replace provider-only tab labels", team[1].BridgeTerminalLabel == "Agent 2 · Fix account switch");
        Reject("task names stay within five words", () => Tool(team[1], "bridge_report_activity", new() { ["summary"] = "Too long", ["task_name"] = "one two three four five six" }));
        vm.SetBridgeTerminalMode(false);
        Check("separate layout shows all agents", team.All(p => p.BridgePaneShown));
        vm.SetBridgeTerminalMode(true);
        Check("return to single layout retains selection", team[1].BridgePaneShown);
        vm.RemoveBridgePane(team[1]);
        Check("removing the selected peer leaves a visible chat", vm.BridgePanes.Count(p => p.BridgePaneShown) == 1);
        Check("remaining labels follow renumbering", team[2].BridgeTerminalIdentity == "Agent 2");
        var (_, fresh) = Team("replacement", 1);
        Call(vm, "ReplaceLivePane", team[2], fresh[0]);
        Check("account replacement preserves terminal mode", fresh[0].BridgeSingleTerminal);
        vm.CloseBridge();
        Check("bridge host retires terminal-only state", !team[0].BridgeSingleTerminal && !team[0].BridgeCoordinatesOnly && team[0].BridgeTaskName == "Ready");
    }

    private static void VerifyOrchestrator()
    {
        var (vm, team) = Team("orchestrator", 3);
        foreach (var chat in team) chat.Status = "idle";
        vm.ConfigureBridgeOrchestrator(team[0], 2, "Repair login and verify session restoration.", true);
        PumpUntil(() => Session(team[0]).Sent.Count > 0);
        Check("worker count excludes coordinator", vm.BridgePanes.Count == 3 && team.Count(p => p.IsBridgeManager) == 1);
        Check("only coordinator gets initial objective", Session(team[0]).Sent.Count == 1 && Session(team[1]).Sent.Count == 0 && Session(team[2]).Sent.Count == 0);
        Check("orchestrator brief delegates with MCP", team[0].AppendSystemPrompt!.Contains("bridge_dispatch_task") && team[0].AppendSystemPrompt!.Contains("do not implement"));
        Reject("worker cannot dispatch as manager", () => Tool(team[1], "bridge_dispatch_task", new()
            { ["recipient"] = team[2].BridgeAgentId, ["task_name"] = "Fix login", ["message"] = "Unauthorized assignment" }));
        Tool(team[0], "bridge_dispatch_task", new()
            { ["recipient"] = team[1].BridgeAgentId, ["task_id"] = "repair-login", ["task_name"] = "Repair login", ["message"] = "Fix the login state handling in auth.cs; verify logout and login." });
        PumpUntil(() => Session(team[1]).Sent.Count > 0);
        Check("MCP dispatch enters worker session with its durable task ID", Session(team[1]).Sent.Count == 1 &&
            Session(team[1]).Sent[0].Contains("Task repair-login") && Session(team[1]).Sent[0].Contains("Fix the login state handling"));
        Check("dispatch gives worker a short task label", team[1].BridgeTaskName == "Repair login");
        team[1].Items.Add(new TextItem { Text = "Fixed auth.cs and verified logout/login with regression coverage." });
        Call(team[1], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("worker report queues back to busy coordinator", team[0].HasQueued && Session(team[0]).Interrupts == 0);
        Call(vm, "SaveBridge", vm.BridgePanes, ".vibecode-bridge.md");
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == team[0].SessionId);
        Check("saved bridge remembers layout and orchestrator", saved.SingleTerminal && saved.HostCoordinatesOnly && saved.Peers[0].TaskName == "Repair login");
        RejectArgument("requested worker count is bounded before changing team", () => vm.ConfigureBridgeOrchestrator(team[0], 99, "Too many", true));
        Check("invalid count did not expand team", vm.BridgePanes.Count == 3);
        // Parked rosters keep their own view selection and dispatch scope.
        Call(vm, "ParkActiveBridge", true);
        var parkedListing = Tool(team[0], "bridge_list_agents");
        Check("parked coordinator still sees its workers", parkedListing["agents"]!.AsArray().Count == 3);
        vm.SelectBridgeTerminal(team[2]);
        Check("parked selection stays within that team", team[2].BridgePaneShown && !team[0].BridgePaneShown);
    }

    private static void VerifySuggestions()
    {
        Check("suggestion parses a bounded worker count", BridgeTeamSuggestionService.TryParse("{\"worker_count\":2,\"reason\":\"One implementation lane and one verification lane.\"}", 8, out var result) && result!.WorkerCount == 2);
        Check("oversized AI suggestion rejected", !BridgeTeamSuggestionService.TryParse("{\"worker_count\":50,\"reason\":\"Many.\"}", 8, out _));
        Check("invalid suggestion rejected", !BridgeTeamSuggestionService.TryParse("{\"worker_count\":\"three\",\"reason\":\"Bad.\"}", 8, out _));
    }

    private static void VerifyProtocolAndProjection()
    {
        var (_, team) = Team("registration", 1);
        var endpoint = Call(team[0], "EnsureBridgeMcp")!;
        var registration = (McpServerDefinition)Call(endpoint, "Registration")!;
        Check("built-in helper is enabled for every CLI provider", registration.UseClaude && registration.UseCodex && registration.UseKimi && registration.UseGrok && registration.Arguments.Last() == "--bridge-mcp");
        Check("Codex projection approves only known bridge tools", McpCatalog.BuildCodexProjection([registration]).ConfigOverrides.Any(c => c.Contains("bridge_dispatch_task") && c.Contains("approval_mode")));
        Check("Claude receives built-in endpoint", McpCatalog.BuildClaudeConfig([registration]).ToJsonString().Contains("--bridge-mcp"));
        Check("Kimi receives built-in endpoint", McpCatalog.BuildKimiAcpServers([registration]).ToJsonString().Contains("--bridge-mcp"));
        Check("Grok receives built-in endpoint", McpCatalog.BuildGrokAcpServers([registration]).ToJsonString().Contains("--bridge-mcp"));
        using var glm = new GlmSession(new GlmSessionOptions { Cwd = _root, ApiKeys = ["offline-fixture"], BridgeMcpPipe = (string)Property(endpoint, "PipeName")! });
        Check("GLM advertises the same bridge contract", ((JsonArray)Call(glm, "ToolSchema")!).Any(t => t?["function"]?["name"]?.ToString() == "bridge_dispatch_task"));
        var host = new AgentStatusMcpHost(BridgeMcpTools.Create((_, _) => new JsonObject { ["ok"] = true }), serverName: "vibecode-bridge", instructions: BridgeMcpTools.Instructions);
        var init = host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject
            { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "fixture", ["version"] = "1" } } });
        Check("MCP initializes with bridge instructions", init?["result"]?["instructions"]?.ToString().Contains("bridge_send_message") == true);
        host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        var listing = host.Dispatch(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" });
        Check("MCP tools list includes chat naming, task titles, broadcast, scope agreement, final review, agent status and file edit history", listing?["result"]?["tools"]?.AsArray().Count == 17 &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_agent_status") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_file_edits") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_set_task_title") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_broadcast") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "chat_set_title") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_agree_scope") == true &&
            listing?["result"]?["tools"]?.AsArray().Any(t => t?["name"]?.ToString() == "bridge_review_scope") == true);
        VerifyBundledMcp(registration, team[0].BridgeAgentId);
    }

    private static void VerifyViews()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var (vm, team) = Team("render", 4);
        foreach (var chat in team) chat.Status = "idle";
        // Rendering uses an existing team; never start a real planning provider from this fixture.
        foreach (var manager in new[] { team[0], team[2] })
        {
            Property(manager, "IsBridgeManager", true);
            Property(manager, "BridgeCoordinatesOnly", true);
        }
        vm.AssignBridgeWorker(team[1], team[0]);
        vm.AssignBridgeWorker(team[3], team[2]);
        foreach (var chat in team) { chat.Items.Clear(); chat.Status = "idle"; }
        foreach (var peer in team.Skip(1)) vm.Chats.Remove(peer);
        team[0].Title = "Store website · Bridge";
        Tool(team[0], "bridge_send_message", new() { ["recipient"] = team[2].BridgeAgentId, ["message"] = "My group owns catalogue and checkout. Yours owns marketing and search." });
        Tool(team[2], "bridge_send_message", new() { ["recipient"] = team[0].BridgeAgentId, ["message"] = "Agreed: marketing and search are my group's scope." });
        Tool(team[0], "bridge_read_messages"); Tool(team[2], "bridge_read_messages");
        Tool(team[0], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Catalogue and checkout" });
        Tool(team[2], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Marketing and product search" });
        string[] names = ["Coordinate store", "Build catalogue", "Coordinate discovery", "Implement search"];
        for (var i = 0; i < team.Length; i++)
            Tool(team[i], "bridge_report_activity", new() { ["summary"] = i % 2 == 0 ? "Scope agreed; coordinating my worker's progress" : "Implementing and checking the assigned area", ["task_name"] = names[i] });
        foreach (var chat in team) { chat.Items.Clear(); chat.Status = "idle"; }
        vm.SetBridgeTerminalMode(true);
        vm.SetBridgeReviewLevel(team[0], "none");
        vm.SetBridgeReviewLevel(team[1], "low");
        vm.SetBridgeReviewLevel(team[2], "high");
        var source = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "VibeCode.Desktop", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = new BridgeSharedTerminal { DataContext = vm.SharedBridgeTerminal };
        Capture(view, 960, 480, "agent-panel-wide.png");
        Capture(view, 640, 440, "agent-panel-narrow.png");
        Check("agent roster binds task names", Descendants<TextBlock>(view).Any(t => t.Text == "Build catalogue"));
        Check("agent roster retains all participants", ((ListBox)view.FindName("AgentRoster")).Items.Count == 4);
        VerifyBridgeSetupViews(team[0]);

        Property(vm, "ActiveChat", team[0]);
        Property(vm, "ShowBridge", true);
        foreach (var chat in team) chat.Status = "idle";
        team[0].Items.Add(new UserItem { Text = "Build a store website. Split the work between your two groups." });
        team[0].Items.Add(new DividerItem { Label = "Both orchestrator groups agreed their scope before assigning work" });
        foreach (var agent in team)
            agent.Items.Add(new ThinkingItem { Text = "Private fixture reasoning retained in the original chat.", Streaming = false });
        team[0].Items.Add(new TextItem { Text = "We agreed the division: my group owns catalogue and checkout. Agent 3 coordinates marketing and search. I assigned catalogue implementation to Agent 2." });
        var web = new CompactToolGroupItem(CompactToolGroupItem.WebKind, true);
        web.Add(new ToolItem { Id = "preview-web", Name = "WebSearch", Status = "done", Input = new JsonObject { ["query"] = "catalogue filtering" } });
        web.Add(new ToolItem { Id = "preview-agents", Name = "bridge_list_agents", Status = "done" });
        team[0].Items.Add(web);
        team[2].Items.Add(new TextItem { Text = "Agreed. Agent 4 is implementing search while I review the marketing pages. Each group reports to its own orchestrator." });
        team[1].Items.Add(new TextItem { Text = "The catalogue is ready. Product filtering and empty states pass verification." });
        var bash = new CompactToolGroupItem(CompactToolGroupItem.BashKind, true);
        bash.Add(new ToolItem { Id = "preview-command", Name = "Bash", Input = new JsonObject { ["command"] = "dotnet build" }, Status = "done", Result = "Build succeeded." });
        team[1].Items.Add(bash);
        var bridgeRead = new ToolItem { Id = "preview-bridge-read", Name = "bridge_read_messages", Status = "done", Result = "No unread messages." };
        team[1].Items.Add(bridgeRead);
        team[3].Items.Add(new TextItem { Text = "Search results and the empty-results state are implemented. I'm checking keyboard navigation." });
        team[1].Items.Add(new DividerItem { Label = "Message from Codex 3" });
        team[2].Items.Add(new DividerItem { Label = "Message queued for 1 bridge agent(s)" });
        foreach (var agent in team.Skip(1)) agent.Items.Add(new PendingItem());
        team[3].Items.Add(new TextItem());
        vm.SelectBridgeTerminal(team[1]);
        team[1].Model = "gpt-6-astra";
        team[1].Effort = "max";
        team[1].Mode = "bypassPermissions";
        team[1].Draft = "Also verify the catalogue on a narrow screen.";
        double[] reads = [5_400_000, 200_000, 300_000, 400_000];
        double[] writes = [80_500, 1_000, 2_000, 3_000];
        double[] costs = [14.6616, 0.1, 0.2, 0.15];
        for (var i = 0; i < team.Length; i++)
        {
            team[i].TotalIn = reads[i]; team[i].TotalOut = writes[i];
            team[i].TotalTokens = reads[i] + writes[i]; team[i].Cost = costs[i];
        }
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3)
            .Invoke([vm, null, true]);
        CaptureShell(shell, 1600, 950, "bridge-shell-dark-wide.png");
        var sharedPanel = (BridgeSharedTerminal)shell.FindName("BridgeSharedPanel");
        var sharedList = (ListBox)sharedPanel.FindName("SharedTranscript");
        Check("orchestrated shared terminal renders no thinking rows", sharedList.Items.Cast<BridgeTranscriptEntry>()
            .All(entry => entry.Item is not ThinkingItem) && Descendants<ContentControl>(sharedPanel).All(c => c.Content is not ThinkingItem));
        var reviewPickers = Descendants<ComboBox>(sharedPanel).Where(picker => picker.Name == "AgentReviewLevelPicker").ToArray();
        Check("each worker and orchestrator has its own review control", reviewPickers.Length == 4 &&
            reviewPickers.All(picker => (string)picker.SelectedValue == ((ChatViewModel)picker.DataContext).BridgeReviewLevel));
        var workerReview = reviewPickers.Single(picker => ReferenceEquals(picker.DataContext, team[1]));
        workerReview.SelectedValue = "high";
        Check("roster review control changes only its agent", team[1].BridgeReviewLevel == "high" &&
            team[0].BridgeReviewLevel == "none" && team[2].BridgeReviewLevel == "high" && team[3].BridgeReviewLevel == "normal" &&
            AppSettings.Current.SavedBridges.Single(saved => saved.HostSessionId == team[0].SessionId)
                .Peers.Single(peer => peer.SessionId == team[1].SessionId).Configuration!.ReviewLevel == "high");
        workerReview.SelectedValue = "low";
        Check("real shell mounts one shared terminal", ((FrameworkElement)shell.FindName("BridgeSharedPanel")).Visibility == Visibility.Visible &&
            ((FrameworkElement)shell.FindName("BridgePaneList")).Visibility == Visibility.Collapsed);
        Check("shared panel realizes exactly one composer for its target", Descendants<TextBox>(sharedPanel).Count(t => t.Name == "PaneInput") == 1 &&
            Descendants<TextBox>(sharedPanel).Single(t => t.Name == "PaneInput").Text == team[1].Draft);
        var usageReadout = Descendants<TextBlock>(sharedPanel).Single(t => t.Name == "SharedUsageTotals");
        var recipient = Descendants<ComboBox>(sharedPanel).Single(picker => picker.Name == "SharedRecipientPicker");
        Check("single terminal shows combined totals beside the recipient in the composer controls",
            usageReadout.Text == "6.3M read · 86.5k write · $15.1116" && usageReadout.Visibility == Visibility.Visible &&
            ReferenceEquals(usageReadout.DataContext, vm.SharedBridgeTerminal) && ReferenceEquals(usageReadout.Parent, recipient.Parent));
        CheckSharedComposerLayout(sharedPanel, "dark wide", singleControlRow: true);
        Check("single terminal does not show per-second or per-minute token counters", Descendants<TextBlock>(sharedPanel)
            .All(t => !t.Text.Contains("/min") && !t.Text.Contains("/sec") && !t.Text.Contains("/s")));
        var command = Descendants<ContentControl>(sharedPanel).Single(c => ReferenceEquals(c.Content, bash));
        var commandBody = (Grid)command.Parent;
        var commandRow = (Grid)commandBody.Parent;
        var agentHeader = commandRow.Children.OfType<StackPanel>().Single();
        Check("single-terminal Bash rows retain their bounded content column inside the agent group", command.ActualWidth > 100 &&
            Math.Abs(command.ActualWidth - Math.Min(760, commandBody.ActualWidth)) < 1 &&
            Grid.GetColumn(commandBody) == 1 && Grid.GetColumn(agentHeader) == 0 && agentHeader.Visibility == Visibility.Collapsed);
        Check("consecutive Bash and bridge tools have no divider between them", ((Border)commandRow.Parent).BorderThickness == new Thickness(0));
        var bridgeCommand = Descendants<ContentControl>(sharedPanel).Single(c => ReferenceEquals(c.Content, bridgeRead));
        var bridgeRow = (Grid)((Grid)bridgeCommand.Parent).Parent;
        var rowBorder = (Border)bridgeRow.Parent;
        Check("shared agent groups have one trailing divider and no repeated tool labels", rowBorder.BorderThickness == new Thickness(0, 0, 0, 1) &&
            bridgeRow.Children.OfType<StackPanel>().Single().Visibility == Visibility.Collapsed);
        var agentHeaders = Descendants<StackPanel>(sharedPanel)
            .Where(header => header.Name == "EntryAgentHeader" && header.Visibility == Visibility.Visible).ToArray();
        Check("interleaved shared activity displays one agent name and role per group", agentHeaders.Length == 4 &&
            agentHeaders.Count(header => Descendants<TextBlock>(header).Any(t => t.Name == "EntryRole" && t.Text == "Orchestrator")) == 2 &&
            agentHeaders.Count(header => Descendants<TextBlock>(header).Any(t => t.Name == "EntryRole" && t.Text == "Worker")) == 2);
        Check("shared shell has no routing notices or idle rows", sharedList.Items.Cast<BridgeTranscriptEntry>()
            .All(entry => entry.Item is not (DividerItem or PendingItem) && entry.HasContent));
        VerifyDividerPreference(shell, sharedPanel, rowBorder);
        var copied = (string)Call(shell, "SharedConversationText", vm.SharedBridgeTerminal)!;
        Check("shared conversation copy includes each agent's visible activity", copied.Contains("Agent 2:") && copied.Contains("Agent 3:") && copied.Contains("Agent 4:"));
        CaptureShell(shell, 1100, 850, "bridge-shell-dark-narrow.png");
        CaptureShell(shell, 1000, 850, "bridge-shell-dark-minimum.png");
        CheckSharedComposerLayout(sharedPanel, "dark minimum", singleControlRow: false);
        CheckTranscriptBounds(sharedList, "narrow shared activity");
        vm.SharedBridgeTerminal.Review(team[1]);
        CaptureShell(shell, 1400, 900, "bridge-shell-review.png");
        Check("review keeps the full roster and filters the existing viewport", sharedList.Items.Cast<BridgeTranscriptEntry>().All(p => ReferenceEquals(p.Owner, team[1])) &&
            ((ListBox)sharedPanel.FindName("AgentRoster")).Items.Count == 4 && ReferenceEquals(sharedList.ItemsSource, vm.SharedBridgeTerminal.Activity));
        Check("copying an agent review follows the visible filter", !((string)Call(shell, "SharedConversationText", vm.SharedBridgeTerminal)!).Contains("Agent 3:"));
        vm.SharedBridgeTerminal.ShowAll();
        var recipientPicker = Descendants<ComboBox>(sharedPanel).Single(picker => picker.Name == "SharedRecipientPicker");
        recipientPicker.SelectedItem = team[2];
        Check("composer recipient picker selects the correct conversation", team[2].BridgeTerminalSelected && ReferenceEquals(vm.BridgePanelChat, team[2]));
        shell.UpdateLayout();
        Check("changing prompt recipient retains activity from all agents", sharedList.Items.Count == vm.SharedBridgeTerminal.Activity.Cast<BridgeTranscriptEntry>().Count() &&
            Descendants<TextBox>(sharedPanel).Single(t => t.Name == "PaneInput").DataContext == team[2]);
        var borderlessTheme = (ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative));
        resources.MergedDictionaries.Add(borderlessTheme);
        var borderlessShell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        vm.SelectBridgeTerminal(team[1]);
        CaptureShell(borderlessShell, 1642, 1004, "bridge-shell-borderless-wide.png");
        CheckSharedComposerLayout((BridgeSharedTerminal)borderlessShell.FindName("BridgeSharedPanel"), "borderless wide", singleControlRow: true);
        CaptureShell(borderlessShell, 1000, 850, "bridge-shell-borderless-minimum.png");
        CheckSharedComposerLayout((BridgeSharedTerminal)borderlessShell.FindName("BridgeSharedPanel"), "borderless minimum", singleControlRow: false);
        resources.MergedDictionaries.Remove(borderlessTheme);
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Cli.xaml", UriKind.Relative)));
        var cliShell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        CaptureShell(cliShell, 1600, 950, "bridge-shell-cli-wide.png");
        CheckSharedComposerLayout((BridgeSharedTerminal)cliShell.FindName("BridgeSharedPanel"), "CLI wide", singleControlRow: true);
        CaptureShell(cliShell, 1100, 850, "bridge-shell-cli-narrow.png");
        vm.SharedBridgeTerminal.Review(team[1]);
        shell.UpdateLayout();
        var sender = Descendants<Button>(sharedPanel).Single(b => b.DataContext == team[1] && b.Style == shell.FindResource("SendButton"));
        sender.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => Session(team[1]).Sent.Any(s => s.Contains("Also verify the catalogue on a narrow screen")));
        Check("review follow-up sends through the shared composer only to its selected agent", team[1].Draft == "" &&
            team.Where(p => !ReferenceEquals(p, team[1])).All(p => Session(p).Sent.All(s => !s.Contains("Also verify the catalogue on a narrow screen"))));
    }

    private static void CheckSharedComposerLayout(BridgeSharedTerminal panel, string label, bool singleControlRow)
    {
        var usage = Descendants<TextBlock>(panel).Single(t => t.Name == "SharedUsageTotals");
        var recipient = Descendants<ComboBox>(panel).Single(t => t.Name == "SharedRecipientPicker");
        var frame = Descendants<Border>(panel).Single(b => b.Name == "BridgeComposerFrame");
        var mode = Descendants<System.Windows.Controls.Primitives.ToggleButton>(panel).Single(b => b.Name == "PaneModeToggle");
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(frame).TransformBounds(new Rect(element.RenderSize));
        var usageBounds = Bounds(usage);
        var recipientBounds = Bounds(recipient);
        Check(label + " totals share the recipient row without overlapping", Math.Abs(usageBounds.Top + usageBounds.Height / 2 -
            recipientBounds.Top - recipientBounds.Height / 2) < 2 && usageBounds.Left >= recipientBounds.Right && usageBounds.Right <= frame.ActualWidth - 8);
        Check(label + " composer has a complete outline", frame.BorderThickness == new Thickness(1));
        if (singleControlRow)
            Check(label + " settings and totals fit on one controls row", Math.Abs(Bounds(mode).Top + Bounds(mode).Height / 2 -
                usageBounds.Top - usageBounds.Height / 2) < 2);
    }

    private static void CaptureShell(Window shell, int width, int height, string name)
    {
        shell.Width = width; shell.Height = height;
        shell.Measure(new Size(width, height)); shell.Arrange(new Rect(0, 0, width, height)); shell.UpdateLayout();
        var root = (FrameworkElement)shell.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
        Console.WriteLine("Rendered " + name);
    }

    private static (MainViewModel, ChatViewModel[]) Team(string name, int count, string provider = "codex")
    {
        var vm = new MainViewModel();
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var team = Enumerable.Range(1, count).Select(i =>
        {
            var chat = new ChatViewModel(path, provider: provider) { BridgeLabel = (provider == "claude" ? "Claude " : "Codex ") + i };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
            Property(chat, "SessionId", Guid.NewGuid().ToString("N"));
            Chats.Add(chat); vm.Chats.Add(chat); vm.BridgePanes.Add(chat);
            Call(vm, "Track", chat);
            return chat;
        }).ToArray();
        return (vm, team);
    }

    private static JsonObject Tool(ChatViewModel chat, string name, JsonObject? args = null) =>
        BridgeMcpTools.Create((tool, input) => ((Func<string, JsonObject, JsonObject>)Property(chat, "BridgeToolHandler")!)(tool, input))
            .Single(t => t.Name == name).Invoke(args ?? new());
    private static FakeSession Session(ChatViewModel chat) => (FakeSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Flags)
        .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
    private static object? Property(object obj, string name) => obj.GetType().GetProperty(name, Flags)!.GetValue(obj);
    private static void Property(object obj, string name, object value) => obj.GetType().GetProperty(name, Flags)!.SetValue(obj, value);
    private static void Check(string text, bool condition)
    {
        if (!condition) throw new Exception("FAIL: " + text);
        _checks++; Console.WriteLine("PASS: " + text);
    }
    private static void Reject(string text, Action action)
    {
        try { action(); } catch (StatusValidationException) { Check(text, true); return; }
        throw new Exception("FAIL: " + text);
    }
    private static void RejectArgument(string text, Action action)
    {
        try { action(); } catch (ArgumentException) { Check(text, true); return; }
        throw new Exception("FAIL: " + text);
    }
    private static void PumpUntil(Func<bool> done, int timeoutMilliseconds = 12000)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromMilliseconds(timeoutMilliseconds)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("Fixture did not complete.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }
    private static void Capture(FrameworkElement view, int width, int height, string name)
    {
        var host = new Border { Child = view, Background = (Brush)Application.Current.Resources["Bg0"] };
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
        host.Child = null;
        Console.WriteLine("Rendered " + name);
    }

    private sealed class FakeSession : ICodingSession
    {
#pragma warning disable CS0067
        public event Action<JsonNode>? MessageReceived;
        public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string>? PermissionCancelled;
        public event Action<int, string>? Exited;
        public event Action? Initialized;
#pragma warning restore CS0067
        public JsonArray Commands { get; } = new();
        public JsonArray Models { get; } = new();
        public string? SessionId => null;
        public bool HasExited { get; private set; }
        public List<string> Sent { get; } = [];
        public int Interrupts { get; private set; }
        public List<JsonObject> PermissionResponses { get; } = [];
        public void Start() { }
        public void SendUser(JsonNode content) => Sent.Add(content.ToJsonString().Replace("\\n", "\n"));
        public Task InterruptAsync() { Interrupts++; return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) => PermissionResponses.Add(result);
        public void Dispose() => HasExited = true;
    }
}
