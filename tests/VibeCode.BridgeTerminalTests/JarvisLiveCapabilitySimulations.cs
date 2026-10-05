using System.IO;
using System.Reflection;
using System.Text.Json;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunLiveJarvisCapabilities(bool ambiguityOnly = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live test requires signed-in CODEX_HOME.");
        var settings = AppSettings.Current;
        settings.SecondBrainEnabled = false;
        settings.AgentSwarmsEnabled = false;

        settings.DefaultProvider = "codex";
        settings.DefaultCodexModel = null;
        settings.McpServers.Clear();
        settings.TrySave();
        var (vm, anchor) = Team("jarvis-live-capabilities", 1);
        anchor[0].Status = "idle";
        Property(vm, "ActiveChat", anchor[0]);
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        var directory = anchor[0].Cwd;
        var planner = new JarvisDialogueService(Path.Combine(_root, "jarvis-live-dialogue-" + Guid.NewGuid().ToString("N")));
        var selection = new JarvisSelection("codex", "gpt-6-luna", "low");
        var history = new List<JarvisDialogueLine>();
        var desktop = new JarvisSimulationDesktop();
        var report = new List<object>();
        string? openedCategory = null;
        vm.JarvisSettingsRequested += value => openedCategory = value;
        var reportPath = Path.Combine(_root, "jarvis-capability-simulations" + (ambiguityOnly ? "-ambiguity" : "") + ".json");
        if (ambiguityOnly)
        {
            Run("Open three new chats in this directory.", plan => plan.Actions.Count == 0 && plan.Reply.Length > 0,
                execute: false, noDirectory: true);
            SaveReport(); vm.ShutdownJarvis(); return;
        }

        Run("Can you edit my VibeCode settings and configure an MCP server?", plan => plan.Actions.Count == 0
            && plan.Reply.Length > 30 && !plan.Reply.Contains("cannot", StringComparison.OrdinalIgnoreCase)
            && !plan.Reply.Contains("can't", StringComparison.OrdinalIgnoreCase), execute: false);
        Run($"Can you open the folder {directory} in File Explorer?", plan => plan.Actions.Count == 1
            && plan.Actions[0].Kind == "open_path" && JarvisPathPolicy.PathsEqual(plan.Actions[0].Target, directory));
        Run("Can you open https://example.com in my browser?", plan => plan.Actions.Count == 1
            && plan.Actions[0] is { Kind: "open_url", Target: "https://example.com" });
        Run("Open Notepad and open https://example.org in my browser.", plan => plan.Actions.Count == 2
            && plan.Actions.Any(action => action.Kind == "open_app" && action.Target.Equals("Notepad", StringComparison.OrdinalIgnoreCase))
            && plan.Actions.Any(action => action.Kind == "open_url" && action.Target == "https://example.org"));
        Run("Can you open three chats in this directory?", plan => plan.Actions.Count == 1
            && plan.Actions[0].Kind == "create_chats" && plan.Actions[0].Count == 3
            && JarvisPathPolicy.PathsEqual(plan.Actions[0].Path, directory));
        Check("live three-chat request creates three additional initialized chats", vm.Chats.Count == 4
            && vm.Chats.All(chat => chat.Cwd == directory && chat.Status == "idle")
            && vm.Chats.All(chat => !chat.Items.OfType<UserItem>().Any()));
        Run("Can you turn off your spoken replies and set your voice volume to 50 percent?", plan => plan.Actions.Count == 1
            && plan.Actions[0].Kind == "update_settings"
            && plan.Actions[0].Changes.Any(change => change.Name == "JarvisVoiceEnabled" && change.Value == "false")
            && plan.Actions[0].Changes.Any(change => change.Name == "JarvisSpeechVolume" && change.Value == "50"));
        Check("live voice-settings request persists its actual changes", !settings.JarvisVoiceEnabled
            && LoadJarvisSettings().JarvisSpeechVolume == 50);
        Run("Read my JarvisSpeechVolume setting.", plan => plan.Actions.Count == 1
            && plan.Actions[0].Kind == "read_settings");
        Run("Configure a remote MCP server named docs at https://example.com/mcp for Codex only.", plan => plan.Actions.Count == 1
            && plan.Actions[0].Kind == "configure_mcp" && plan.Actions[0].Target == "docs");
        Check("live MCP setup uses the supplied URL and provider selection", settings.McpServers.Single().Url == "https://example.com/mcp"
            && settings.McpServers.Single().UseCodex && !settings.McpServers.Single().UseClaude
            && !settings.McpServers.Single().UseKimi && !settings.McpServers.Single().UseGrok);
        var identity = settings.McpServers.Single().Id;
        Run("Disable the docs MCP server.", plan => plan.Actions.Count == 1 && plan.Actions[0].Kind == "configure_mcp");
        Check("live disable preserves the existing MCP endpoint", !settings.McpServers.Single().Enabled
            && settings.McpServers.Single().Id == identity && settings.McpServers.Single().Url == "https://example.com/mcp");
        Run("Set the docs MCP server tool timeout to 90 seconds.", plan => plan.Actions.Count == 1 && plan.Actions[0].Kind == "configure_mcp");
        Check("live MCP partial update preserves the rest of the definition", settings.McpServers.Single().ToolTimeoutSeconds == 90
            && !settings.McpServers.Single().Enabled && settings.McpServers.Single().Id == identity);
        Run("Enable it again.", plan => plan.Actions.Count == 1 && plan.Actions[0].Kind == "configure_mcp");
        Check("live pronoun follow-up uses verified action history", settings.McpServers.Single().Enabled && settings.McpServers.Single().Id == identity);
        Run("Open VibeCode's MCP server settings.", plan => plan.Actions.Count == 1
            && plan.Actions[0] is { Kind: "open_settings", Target: "MCP servers" });
        Check("live settings navigation reaches the category callback", openedCategory == "MCP servers");
        Run("Configure an MCP server for me.", plan => plan.Actions.Count == 0 && plan.Reply.Contains('?'), execute: false);
        Run("Open three new chats in this directory.", plan => plan.Actions.Count == 0 && plan.Reply.Length > 0,
            execute: false, noDirectory: true);
        Check("all live simulations preserve disabled Second Brain", !settings.SecondBrainEnabled);
        SaveReport();
        Console.WriteLine("Simulation report: " + reportPath);
        Chats.AddRange(vm.Chats.Where(chat => !Chats.Contains(chat)));
        vm.ShutdownJarvis();

        void SaveReport() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            selection, planning = "Real authenticated Jarvis model",
            execution = "Real isolated settings/MCP/chat hosts; Windows app/browser launches simulated at the OS boundary",
            scenarios = report,
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Run(string request, Func<JarvisPlan, bool> check, bool execute = true, bool noDirectory = false)
        {
            Console.WriteLine("Jarvis simulation: " + request);
            var context = ((IJarvisActionHost)vm).GetContext();
            if (noDirectory) context = context with { CurrentProject = null, CurrentChatId = null, KnownProjects = [], OpenChats = [] };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var turn = planner.PlanAsync(selection, context, noDirectory ? [] : history, request, stop.Token);
            PumpUntil(() => turn.IsCompleted, 155000);
            var plan = turn.GetAwaiter().GetResult().Plan;
            File.WriteAllText(Path.Combine(_root, "jarvis-last-plan.json"), JsonSerializer.Serialize(new { request, plan }, new JsonSerializerOptions { WriteIndented = true }));
            Check("live planning: " + request, check(plan));
            var answer = plan.Reply;
            var previousRuntime = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
            try
            {
                // The planner above used the real provider. Only blank chat startup is redirected to the
                // existing local protocol fixture, so no extra model work or coding task runs in these new chats.
                Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
                if (execute)
                {
                    var runtime = new JarvisRuntime(new JarvisPlanFixture(_ => plan), vm, desktop);
                    var result = runtime.SubmitAsync(request, selection, stop.Token);
                    PumpUntil(() => result.IsCompleted, 155000);
                    answer = result.GetAwaiter().GetResult();
                    Check("live action receipts: " + request, plan.Actions.Count == 0 || answer != plan.Reply);
                }
            }
            finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previousRuntime); }
            report.Add(new { request, plan, answer });
            SaveReport();
            if (!noDirectory) { history.Add(new("user", request)); history.Add(new("assistant", answer)); }
        }
    }

    private sealed class JarvisSimulationDesktop : IJarvisDesktopActions
    {
        public Task<JarvisActionReceipt> ExecuteAsync(JarvisAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JarvisDesktopPolicy.Validate(action.Kind, action.Target);
            return Task.FromResult(new JarvisActionReceipt(action.Kind, action.Target,
                "Simulated Windows launch: " + action.Kind + " " + action.Target));
        }
    }
}
