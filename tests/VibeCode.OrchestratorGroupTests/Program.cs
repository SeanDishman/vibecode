using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static readonly BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly string Root = Path.GetFullPath("artifacts/agent2-orchestrator-groups/run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly List<ChatViewModel> Chats = [];
    static readonly List<Window> Windows = [];
    static int Checks, Failures;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("app-server")) return Fixture("codex", args);
        if (args.Contains("--output-format")) return Fixture("claude", args);
        if (args.Contains("--version")) { Console.WriteLine("offline review fixture 1.0"); return 0; }
        ThreadPool.SetMinThreads(32, 32); // Each local CLI fixture has pipe readers; avoid fixture-only starvation.
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Root, "data-" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        Environment.SetEnvironmentVariable("REVIEW_FIXTURE_ROOT", Root);
        Environment.SetEnvironmentVariable("REVIEW_DELAYED_CATALOG", args.Contains("--startup-race-only") ? "1" : null);
        Environment.SetEnvironmentVariable("REVIEW_ASYNC_ACK", args.Contains("--ack-order-only") || args.Contains("--groups") || args.Contains("--fast-mode") ? "1" : null);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(Root, "empty-claude-home"));
        Directory.CreateDirectory(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!);
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_ACCOUNT_STORE", Path.Combine(Root, "empty-claude-accounts"));
        var exe = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", exe);
        Environment.SetEnvironmentVariable("VIBECODE_CLAUDE_PATH", exe);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        app.Resources["BoolVis"] = new BooleanToVisibilityConverter(); app.Resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        AppSettings.Current.AgentMemoryEnabled = false;

        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        AppSettings.Current.BridgeAgentLimit = 12;
        try
        {
            if (args.Contains("--fast-mode"))
            {
                VerifyOrchestratorFastMode();
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed orchestrator fast-mode checks. Local provider fixtures only. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--fresh-roster"))
            {
                FreshTeam(false, "claude", "claude");
                FreshTeam(true, "claude", "claude");
                FreshTeam(false, "claude", "codex", busyHost: true);
                FreshTeam(true, "claude", "codex", busyHost: true);
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed fresh advanced roster checks. Local provider fixtures only. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--planning-recovery"))
            {
                VerifyPlanningRecovery();
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed planning quota recovery checks. Local provider fixtures only. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--central-lifecycle"))
            {
                VerifyCentralParsing();
                VerifyCentralDiagnostics();
                VerifyCentralFailure();
                VerifyCentralCancellation();
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed central lifecycle checks. Local provider fixtures only. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--review-settings"))
            {
                VerifyReviewDelivery(false, "codex", "claude");
                VerifyReviewDelivery(false, "claude", "codex");
                VerifyReviewDelivery(true, "codex", "claude");
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed review settings checks through real provider adapters and local fixtures. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--groups"))
            {
                VerifyCentralParsing();
                VerifyGroupAllocation();
                VerifyCentralFailure();
                VerifyCentralCancellation();
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed group launch, ownership, persistence and setup checks. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--ack-order-only"))
            {
                AcknowledgmentIntegration();
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed asynchronous acknowledgment/default/ordinary-send checks. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            if (args.Contains("--startup-race-only"))
            {
                FreshTeam(false, "claude", "codex");
                Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed delayed-catalog startup checks. Evidence: {Root}");
                return Failures == 0 ? 0 : 1;
            }
            var primary = FreshTeam(false, "codex", "claude");
            FreshTeam(false, "claude", "codex");
            FreshTeam(true, "codex", "claude");
            LaterGroupAndRestore(primary);
            Console.WriteLine($"RESULT: {Checks} passed, {Failures} failed independent team integration checks; local provider fixtures only. Evidence: {Root}");
            return Failures == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException { InnerException: not null } t ? t.InnerException : ex); return 1; }
        finally
        {
            foreach (var chat in Chats.Distinct()) chat.Close();
            foreach (var window in Windows.Where(w => w.IsLoaded).ToArray()) window.Close();
            app.Shutdown();
        }
    }

    static (MainViewModel Vm, ChatViewModel Host) FreshTeam(bool secondary, string orchestratorProvider, string workerProvider,
        string? orchestratorModel = null, bool explicitDefault = false,
        string orchestratorReview = "normal", string workerReview = "normal", bool busyHost = false,
        bool? orchestratorFastMode = null, bool initialFastMode = false)
    {
        var tag = $"{(secondary ? "secondary" : "primary")}-{orchestratorProvider}-{workerProvider}" + (busyHost ? "-busy-host" : "");
        var vm = new MainViewModel();
        var host = Chat(tag, "codex"); host.Model = "gpt-6-astra"; host.Effort = "ultra"; host.FastMode = initialFastMode;
        vm.Chats.Add(host); Set(vm, secondary ? "SecondaryActiveChat" : "ActiveChat", host);
        host.Start(); Pump(() => host.Status == "idle" && host.SessionId is not null);
        if (busyHost) host.Status = "working";
        var originalConfig = Config(host);
        var originalSession = Session(host);
        AppSettings.Current.DualMonitorDoubleSessions = secondary;
        var primary = Shell(vm);
        var shell = secondary ? Shell(vm, primary) : primary;
        var panel = (BridgeSetupPanel)shell.FindName("BridgeSetup");
        panel.Configure(host, 8, advancedOnly: true);
        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = orchestratorProvider;
        var options = (BridgeAgentOptionsPanel)panel.FindName("AgentOptions");
        Choose(options, "OrchestratorModelBox", orchestratorModel ?? (orchestratorProvider == "codex" ? "gpt-6.1-sol" : "claude-opus-5"));
        Effort(options, "OrchestratorEffortBox", explicitDefault ? null : orchestratorProvider == "codex" ? "high" : "xhigh");
        ((ComboBox)options.FindName("OrchestratorReviewBox")).SelectedValue = orchestratorReview;
        ((ComboBox)options.FindName("WorkerProviderBox")).SelectedValue = workerProvider;
        Choose(options, "WorkerModelBox", workerProvider == "codex" ? "gpt-6-luna" : "claude-opus-5");
        Effort(options, "WorkerEffortBox", workerProvider == "codex" ? "medium" : "max");
        ((ComboBox)options.FindName("WorkerReviewBox")).SelectedValue = workerReview;
        if (orchestratorFastMode is { } fastMode)
            ((ToggleButton)options.FindName("OrchestratorFastModeToggle")).IsChecked = fastMode;
        ((ComboBox)panel.FindName("WorkerCountBox")).SelectedItem = 2;
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Review fixture first objective " + tag;
        var orchestrator = panel.OrchestratorConfiguration; var worker = panel.WorkerConfiguration;
        Call(shell, "OnBridgeSetupStart", panel, EventArgs.Empty);
        Check(tag + " actual setup handler accepts selected configs", ((TextBlock)panel.FindName("ErrorText")).Visibility == Visibility.Collapsed);
        var panes = secondary ? vm.SecondaryBridgePanes : vm.BridgePanes;
        foreach (var pane in panes) Chats.Add(pane);
        Pump(() => panes.All(p => (p.Status == "idle" || busyHost && ReferenceEquals(p, host)) && p.SessionId is not null) &&
            panes.Where(p => p.IsBridgeManager).All(p => UserRequests(p).Any()));
        var manager = panes.Single(p => p.IsBridgeManager);
        var workers = panes.Where(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId).ToArray();
        Check(tag + " exact manager and worker configuration", Config(manager) == orchestrator && workers.Length == 2 && workers.All(w => Config(w) == worker));
        Check(tag + " creates exactly the configured team without an independent extra", panes.Count == 3 &&
            panes.All(p => p.IsBridgeManager || p.BridgeCoordinatorAgentId == manager.BridgeAgentId));
        if (host.Provider != orchestratorProvider && (host.Provider != workerProvider || busyHost))
        {
            Check(tag + " original chat remains separate with its model and session", !panes.Contains(host) &&
                vm.Chats.Contains(host) && !host.IsBridgeHost && Config(host) == originalConfig && ReferenceEquals(Session(host), originalSession));
            Check(tag + " configured orchestrator anchors the new bridge", panes[0] == manager && manager.IsBridgeHost && vm.Chats.Contains(manager));
            Check(tag + " original chat receives no team objective", !UserRequests(host).Any());
            if (busyHost) Check(tag + " original work remains active", host.Status == "working");
        }
        Check(tag + " first objective reaches selected session settings", FirstUserSettings(manager) == orchestrator);
        foreach (var peer in workers)
        {
            Check(tag + " worker first objective is accepted", peer.Send("Review fixture worker first objective " + tag));
            Pump(() => peer.Status == "idle" && UserRequests(peer).Any());
            Check(tag + " worker first task uses selected model and effort", FirstUserSettings(peer) == worker);
        }
        if (secondary) Check(tag + " secondary roster leaves primary bridge empty", vm.BridgePanes.Count == 0 && vm.SecondaryBridgePanes.Count == 3);
        return (vm, host);
    }

    static void LaterGroupAndRestore((MainViewModel Vm, ChatViewModel Host) team, bool managerFastMode = false)
    {
        var (vm, host) = team;
        var spare = Chat(host.Cwd, "codex", absolute: true); spare.Model = "gpt-6-astra"; spare.Effort = "ultra";
        spare.Start(); Pump(() => spare.Status == "idle" && spare.SessionId is not null);
        spare.BridgeLabel = "Codex " + (vm.BridgePanes.Count + 1); vm.BridgePanes.Add(spare); Call(vm, "Track", spare);
        var countBefore = vm.BridgePanes.Count;
        var managerConfig = new BridgeAgentConfiguration("claude", "claude-opus-5", "high", FastMode: managerFastMode);
        var workerConfig = new BridgeAgentConfiguration("codex", "gpt-6-luna", "max", FastMode: false);
        var manager = vm.LaunchBridgeOrchestrator(host, 1, "Review fixture added coordinator first objective", true, "claude", managerConfig, workerConfig);
        Chats.Add(manager); Pump(() => manager.Status == "idle" && manager.SessionId is not null && UserRequests(manager).Any());
        Check("additional orchestrator reuses a matching unassigned live worker", vm.BridgePanes.Count == countBefore + 1 && spare.BridgeCoordinatorAgentId == manager.BridgeAgentId);
        Check("reused worker receives selected independent model and effort", Config(spare) == workerConfig);
        Check("added orchestrator first objective uses selected config", FirstUserSettings(manager) == managerConfig);
        Check("reused worker sends first task", spare.Send("Review fixture reused worker first objective")); Pump(() => spare.Status == "idle" && UserRequests(spare).Any());
        Check("reused real session first task uses new model and effort", FirstUserSettings(spare) == workerConfig);
        var later = vm.LaunchBridgeWorker(manager); Chats.Add(later); Pump(() => later.Status == "idle" && later.SessionId is not null);
        Check("later worker inherits coordinator worker policy", Config(later) == workerConfig && later.BridgeCoordinatorAgentId == manager.BridgeAgentId);
        Check("later worker uses real adapter with configuration at thread creation", Session(later) is CodexSession && Requests(later).Any(n => n["method"]?.ToString() == "thread/start" && n["params"]?["model"]?.ToString() == workerConfig.Model));
        later.Effort = null; vm.SaveBridge();
        var saved = AppSettings.Current.SavedBridges.Single(b => b.HostSessionId == host.SessionId);
        var reloaded = (AppSettings)CallStatic(typeof(AppSettings), "Load")!;
        var snapshot = reloaded.SavedBridges.Single(b => b.HostSessionId == host.SessionId);
        Check("real settings save/load preserves exact actual bridge snapshot", JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(snapshot));
        AppSettings.Current.SavedBridges = reloaded.SavedBridges;
        File.WriteAllText(Path.Combine(Root, "saved-snapshot.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        Check("saved snapshot preserves both manager roles and worker policies", snapshot.HostConfiguration == Config(host) && snapshot.HostWorkerConfiguration == host.BridgeWorkerConfiguration &&
            snapshot.Peers.Single(p => p.SessionId == manager.SessionId).WorkerConfiguration == workerConfig);
        var restored = new MainViewModel();
        var restoredHost = Chat(host.Cwd, host.Provider, true); restoredHost.SessionId = snapshot.HostSessionId;
        Field(restoredHost, "_session", new LedgerSession()); restoredHost.Status = "idle"; restored.Chats.Add(restoredHost);
        foreach (var pane in snapshot.Peers)
        {
            var peer = Chat(pane.Cwd, pane.Provider!, true); peer.SessionId = pane.SessionId;
            Field(peer, "_session", new LedgerSession()); peer.Status = "idle"; restored.Chats.Add(peer);
        }
        Check("saved actual roster restores", restored.RestoreBridge(restoredHost));
        Check("restored actual model and effort match every saved row", restored.BridgePanes.All(p => Config(p) == (p.SessionId == snapshot.HostSessionId ? snapshot.HostConfiguration : snapshot.Peers.Single(s => s.SessionId == p.SessionId).Configuration)));
        var restoredManager = restored.BridgePanes.Single(p => p.SessionId == manager.SessionId);
        Check("restored group mapping and future worker settings persist", restoredManager.BridgeWorkerConfiguration == workerConfig &&
            restored.BridgePanes.Single(p => p.SessionId == spare.SessionId).BridgeCoordinatorAgentId == restoredManager.BridgeAgentId);
        Check("explicit default effort survives restore", restored.BridgePanes.Single(p => p.SessionId == later.SessionId).Effort is null);
    }

    static ChatViewModel Chat(string tag, string provider, bool absolute = false) { var path = absolute ? tag : Path.Combine(Root, tag + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); var c = new ChatViewModel(path, provider: provider); Chats.Add(c); return c; }
    static MainWindow Shell(MainViewModel vm, MainWindow? primary = null) { var w = (MainWindow)typeof(MainWindow).GetConstructors(F).Single(c => c.GetParameters().Length == 3).Invoke([vm, primary, true]); Windows.Add(w); return w; }
    static void Choose(BridgeAgentOptionsPanel p, string name, string value) { var b = (ComboBox)p.FindName(name); b.SelectedItem = b.Items.OfType<ModelChoice>().Single(m => m.Value == value); }
    static void Effort(BridgeAgentOptionsPanel p, string name, string? value) { var b = (ComboBox)p.FindName(name); b.SelectedItem = b.Items.OfType<EffortChoice>().Single(e => e.Value == value); }
    static BridgeAgentConfiguration Config(ChatViewModel c) => new(c.Provider, c.Model, c.Effort, c.BridgeReviewLevel, c.FastMode);
    static object Session(ChatViewModel c) => GetField(c, "_session")!;
    static object? GetField(object o, string n) => o.GetType().GetField(n, F)!.GetValue(o);
    static void Field(object o, string n, object? v) => o.GetType().GetField(n, F)!.SetValue(o, v);
    static void Set(object o, string n, object? v) => o.GetType().GetProperty(n, F)!.SetValue(o, v);
    static object? Call(object o, string n, params object?[] a) => o.GetType().GetMethods(F).Single(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(o, a);
    static object? CallStatic(Type t, string n, params object?[] a) => t.GetMethods(F).Single(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(null, a);
    static IEnumerable<T> Descendants<T>(DependencyObject r) where T : DependencyObject { if (r is T t) yield return t; for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(r); i++) foreach (var c in Descendants<T>(System.Windows.Media.VisualTreeHelper.GetChild(r, i))) yield return c; }
    static void Check(string name, bool pass) { if (!pass) { Failures++; Console.WriteLine("FAIL: " + name); } else { Checks++; Console.WriteLine("PASS: " + name); } }
    static void PumpFor(double seconds) { var watch = Stopwatch.StartNew(); Pump(() => watch.Elapsed.TotalSeconds >= seconds); }
    static void Pump(Func<bool> predicate) { var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; timer.Tick += (_, _) => { if (predicate() || watch.Elapsed.TotalSeconds > 40) frame.Continue = false; }; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); } if (!predicate()) { foreach (var chat in Chats.Distinct()) Console.WriteLine($"TIMEOUT: {chat.SessionId} {chat.Provider} status={chat.Status}, model={chat.Model}, effort={chat.Effort}, initialized={GetField(chat, "_bridgeSessionInitialized")}, configTask={((Task)GetField(chat, "_bridgeConfigurationApplication")!).Status}, models={chat.Models.Count}, exited={(GetField(chat, "_session") as ICodingSession)?.HasExited}, banners={string.Join(" | ", chat.Items.OfType<BannerItem>().Select(b => b.Text))}"); throw new Exception("Fixture timeout"); } }
    static List<JsonNode> Requests(ChatViewModel c) => Directory.GetFiles(Root, "trace-*.jsonl").Select(ReadTraceRows).Where(rows => rows.Any(n => n["fixture_session"]?.ToString() == c.SessionId)).SelectMany(rows => rows).ToList();
    static IEnumerable<JsonNode> UserRequests(ChatViewModel c) => Requests(c).Where(n => n["method"]?.ToString() == "turn/start" || n["type"]?.ToString() == "user");
    static BridgeAgentConfiguration FirstUserSettings(ChatViewModel c)
    {
        string? model = null, effort = null;
        var fastMode = false;
        foreach (var n in Requests(c))
        {
            if (n["fixture_args"] is JsonArray args) { var a = args.Select(v => v!.ToString()).ToArray(); var mi = Array.IndexOf(a, "--model"); var ei = Array.IndexOf(a, "--effort"); model = mi < 0 ? null : a[mi + 1]; effort = ei < 0 ? null : a[ei + 1]; fastMode = LaunchFastMode(a); }
            if (n["request"]?["subtype"]?.ToString() == "set_model") { model = n["request"]?["model"]?.ToString() ?? model; effort = n["request"]?["effort"]?.ToString(); }
            if (n["request"]?["subtype"]?.ToString() == "apply_flag_settings" && n["request"]?["settings"]?.AsObject().ContainsKey("effortLevel") == true) effort = n["request"]?["settings"]?["effortLevel"]?.ToString();
            if (n["request"]?["subtype"]?.ToString() == "apply_flag_settings" && n["request"]?["settings"]?["fastMode"] is { } speed) fastMode = speed.GetValue<bool>();
            if (n["method"]?.ToString() == "turn/start") return new(c.Provider, n["params"]?["model"]?.ToString(), n["params"]?["effort"]?.ToString(), ReviewLevelOnWire(n) ?? "normal", n["params"]?["serviceTier"]?.ToString() == "priority");
            if (n["type"]?.ToString() == "user") { Console.WriteLine($"TRACE: {c.SessionId} first task {c.Provider}/{model}/{effort ?? "default"}"); return new(c.Provider, model, effort, ReviewLevelOnWire(n) ?? "normal", fastMode); }
        }
        throw new Exception("No first task trace for " + c.SessionId);
    }

    static bool LaunchFastMode(string[] args)
    {
        var settingsIndex = Array.IndexOf(args, "--settings");
        return settingsIndex >= 0 && JsonNode.Parse(args[settingsIndex + 1])?["fastMode"]?.GetValue<bool>() == true;
    }

    static int Fixture(string provider, string[] args)
    {
        if (provider == "claude" && Environment.GetEnvironmentVariable("REVIEW_ASYNC_ACK") == "1") return AcknowledgmentFixture(args);
        var root = Environment.GetEnvironmentVariable("REVIEW_FIXTURE_ROOT")!;
        var id = "review-" + Environment.ProcessId; var path = Path.Combine(root, "trace-" + Environment.ProcessId + ".jsonl");
        void Log(JsonNode n) => File.AppendAllText(path, n.ToJsonString() + Environment.NewLine);
        void Emit(JsonObject n) { Console.WriteLine(n.ToJsonString()); Console.Out.Flush(); }
        Log(new JsonObject { ["fixture_provider"] = provider, ["fixture_args"] = JsonSerializer.SerializeToNode(args), ["fixture_session"] = id });
        var turnSequence = 0;
        string? quotaRetryReply = null;
        while (Console.ReadLine() is { } line)
        {
            var n = JsonNode.Parse(line)!; Log(n);
            if (provider == "codex")
            {
                if (n["id"] is not { } requestId) continue; var method = n["method"]!.ToString();
                if (method == "thread/resume") { id = n["params"]?["threadId"]?.ToString() ?? id; Log(new JsonObject { ["fixture_session"] = id }); }
                var turnId = method == "turn/start" ? "review-turn-" + ++turnSequence : "";
                var result = method switch { "model/list" => new JsonObject { ["data"] = new JsonArray() },
                    "account/rateLimits/read" => JsonNode.Parse("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":25}}}""")!,
                    "thread/start" or "thread/resume" => new JsonObject { ["thread"] = new JsonObject { ["id"] = id } }, "turn/start" => new JsonObject { ["turn"] = new JsonObject { ["id"] = turnId } }, _ => new JsonObject() };
                Emit(new() { ["id"] = requestId.DeepClone(), ["result"] = result });
                if (method == "turn/start" && Environment.GetEnvironmentVariable("REVIEW_PLANNING_QUOTA") == "1")
                {
                    if (quotaRetryReply is null && (CentralFixtureReply(n) is { } || line.Contains("Suggest between")))
                    {
                        quotaRetryReply = CentralFixtureReply(n) ?? "{\"worker_count\":2,\"reason\":\"One worker for each group.\"}";
                        Emit(new() { ["method"] = "turn/completed", ["params"] = new JsonObject { ["threadId"] = id,
                            ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "failed", ["error"] = new JsonObject
                                { ["message"] = "You've reached your usage limit", ["codexErrorInfo"] = "usageLimitExceeded" } } } });
                        continue;
                    }
                    if (quotaRetryReply is not null)
                    {
                        Emit(new() { ["method"] = "item/completed", ["params"] = new JsonObject { ["threadId"] = id, ["turnId"] = turnId,
                            ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = "recovered-planning", ["text"] = quotaRetryReply } } });
                        Emit(new() { ["method"] = "turn/completed", ["params"] = new JsonObject { ["threadId"] = id,
                            ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "completed" } } });
                        continue;
                    }
                }
                if (method == "turn/start" && line.Contains("Suggest between"))
                    Emit(new() { ["method"] = "item/completed", ["params"] = new JsonObject
                    {
                        ["threadId"] = id, ["turnId"] = turnId, ["item"] = new JsonObject
                        { ["type"] = "agentMessage", ["id"] = "suggestion", ["text"] = "{\"worker_count\":2,\"reason\":\"One worker for each of two groups.\"}" },
                    } });
                if (method == "turn/start" && CentralFixtureReply(n) is { } centralReply)
                    Emit(new() { ["method"] = "item/completed", ["params"] = new JsonObject
                    {
                        ["threadId"] = id, ["turnId"] = turnId, ["item"] = new JsonObject
                        { ["type"] = "agentMessage", ["id"] = "central-assignment", ["text"] = centralReply },
                    } });
                if (method == "turn/start") Emit(new() { ["method"] = "turn/completed", ["params"] = new JsonObject { ["threadId"] = id, ["turn"] = new JsonObject { ["id"] = turnId, ["status"] = "completed" } } });
            }
            else if (n["type"]?.ToString() == "control_request")
            {
                var response = new JsonObject();
                if (n["request"]?["subtype"]?.ToString() == "initialize")
                {
                    response["models"] = new JsonArray(new JsonObject { ["value"] = "claude-opus-5", ["displayName"] = "Opus 5", ["supportedEffortLevels"] = new JsonArray("low", "medium", "high", "xhigh", "max"), ["supportsEffort"] = true, ["supportsAutoMode"] = true });
                    var mi = Array.IndexOf(args, "--model"); Emit(new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = id, ["model"] = mi < 0 ? "claude-opus-5" : args[mi + 1] });
                    if (Environment.GetEnvironmentVariable("REVIEW_DELAYED_CATALOG") == "1") Thread.Sleep(150);
                }
                Emit(new() { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = n["request_id"]!.DeepClone(), ["response"] = response } });
            }
            else if (n["type"]?.ToString() == "user") Emit(new() { ["type"] = "result", ["session_id"] = id, ["is_error"] = false, ["result"] = CentralFixtureReply(n) ?? "Local review fixture complete" });
        }
        return 0;
    }
    sealed class LedgerSession : ICodingSession
    {
#pragma warning disable CS0067
        public event Action<JsonNode>? MessageReceived; public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string>? PermissionCancelled; public event Action<int, string>? Exited; public event Action? Initialized;
#pragma warning restore CS0067
        public JsonArray Commands { get; } = []; public JsonArray Models { get; } = [];
        public string? SessionId => null; public bool HasExited { get; private set; }
        public void Start() { } public void Dispose() => HasExited = true; public void SendUser(JsonNode content) { }
        public Task InterruptAsync() => Task.CompletedTask; public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask; public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
    }
}
