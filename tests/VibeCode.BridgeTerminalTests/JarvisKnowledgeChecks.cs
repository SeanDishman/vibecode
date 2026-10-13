using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyJarvisAppKnowledge()
    {
        var settings = AppSettings.Current;
        settings.SecondBrainEnabled = false;
        settings.JarvisSpeechVolume = 31;
        settings.McpServers.Clear();
        settings.McpServers.Add(new McpServerDefinition
        {
            Name = "knowledge-fixture",
            Url = "https://example.com/mcp?token=private-query",
            Headers = new() { ["Authorization"] = "private-header" },
        });
        var vm = new MainViewModel();
        var chat = new ChatViewModel(_root, title: "Knowledge fixture", provider: "codex") { Status = "idle" };
        vm.Chats.Add(chat);
        Chats.Add(chat);
        Property(vm, "ActiveChat", chat);
        try
        {
            var host = (IJarvisActionHost)vm;
            var initial = host.GetContext();
            Check("host captures active chat configuration independently of Jarvis", initial.CurrentChat is { } active
                && active.Id == chat.BridgeAgentId && active.Provider == chat.Provider
                && active.Model == chat.Model && active.Effort == chat.Effort);

            // Change a preference AFTER context capture, as can happen while a provider is starting.
            settings.JarvisSpeechVolume = 62;
            var before = DateTimeOffset.UtcNow;
            var prompt = Plan(initial, "Jarvis, what version of vibe code am I currently using?", "fixture-resolved-model",
                [new("assistant", "I can't read the installed VibeCode version from this chat.")]);
            var context = prompt["capability_context"]!;
            var app = context["application"]!;
            var assemblyVersion = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Check("version uses the product assembly even under a test host", AppVersion.Current == AppVersion.Parse(assemblyVersion));
            Check("every question receives the same running version as About", app["name"]!.ToString() == "VibeCode"
                && app["running_version"]!.ToString() == AppVersion.Current.ToString());
            Check("knowing the running version does not claim a release check", app["update_status"]!.ToString() == "not_checked"
                && app["latest_release_version"] is null);
            var utc = DateTimeOffset.Parse(app["utc_time"]!.ToString());
            Check("time and timezone are read at dispatch", utc >= before && utc <= DateTimeOffset.UtcNow
                && DateTimeOffset.Parse(app["local_time"]!.ToString()).ToUniversalTime() == utc
                && app["time_zone"]!.ToString() == TimeZoneInfo.Local.Id);
            Check("current host capabilities travel with the question", app["supported_actions"]!.AsArray()
                .Select(value => value!.ToString()).Contains("read_settings")
                && app["supported_actions"]!.AsArray().Select(value => value!.ToString()).Contains("create_chats")
                && !app["supported_actions"]!.AsArray().Select(value => value!.ToString()).Contains("run_command"));
            Check("reported Jarvis provider/model are separate from coding chat metadata", context["jarvis"]!["provider"]!.ToString() == "codex"
                && context["jarvis"]!["reported_model"]!.ToString() == "fixture-resolved-model"
                && context["current_chat"]!["id"]!.ToString() == chat.BridgeAgentId
                && context["current_chat"]!["provider"]!.ToString() == chat.Provider);
            Check("settings are refreshed after provider startup", Preference(context, "JarvisSpeechVolume") == "62"
                && initial.Settings!["preferences"]!.AsArray().Single(row => row!["name"]!.ToString() == "JarvisSpeechVolume")!["value"]!.ToString() == "31");
            Check("fresh app knowledge keeps secret settings redacted", !prompt.ToJsonString().Contains("knowledge-fixture-private-proxy")
                && !prompt.ToJsonString().Contains("private-query") && !prompt.ToJsonString().Contains("private-header"));
            Check("app knowledge is available with Second Brain disabled", !context["second_brain_enabled"]!.GetValue<bool>()
                && !context["memory_allowed_for_chat"]!.GetValue<bool>() && app["running_version"] is not null);
            var navigation = context["settings"]!["navigation"]!.AsArray();
            Check("version guidance uses the current About location", navigation.Single(row => row!["category"]!.ToString() == "About")!["path"]!
                .ToString() == "Settings > General > About");
            Check("memory guidance uses the current Extensions location", navigation.Single(row => row!["category"]!.ToString() == "Extensions")!["path"]!
                .ToString() == "Settings > MCP & extensions > Extensions");
            string? openedCategory = null;
            var about = JarvisSettingsActions.Execute(new("open_settings", "", "", "Open About") { Target = "About" },
                CancellationToken.None, category => openedCategory = category);
            Check("settings receipts and routing share the current location", openedCategory == "About"
                && about.Message.Contains("Settings > General > About"));
            var pane = JarvisSettingsActions.Execute(new("read_settings", "", "", "Read MCP and extensions") { Target = "MCP and extensions" },
                CancellationToken.None);
            Check("current pane names are valid action targets", pane.Message.Contains("SecondBrainEnabled = False", StringComparison.OrdinalIgnoreCase)
                && SettingsNavigation.NormalizeCategory("Bridge & agents") == "Bridge");
            var source = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "VibeCode.Desktop", "SettingsWindow.xaml"));
            var elements = source.Descendants().ToArray();
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            Check("every advertised route targets an existing settings rail and section", SettingsNavigation.Destinations.All(destination =>
                elements.Any(element => element.Name.LocalName == "ListBoxItem"
                    && (string?)element.Attribute("AutomationProperties.Name") == destination.RailName)
                && (destination.SectionName is null || elements.Any(element => (string?)element.Attribute(x + "Name") == destination.SectionName))));

            settings.JarvisSpeechVolume = 73;
            var next = Plan(host.GetContext(), "And which model are you using?", null, []);
            var nextContext = next["capability_context"]!;
            Check("follow-up requests receive changed settings", Preference(nextContext, "JarvisSpeechVolume") == "73");
            Check("unreported default model remains unknown", nextContext["jarvis"]!["reported_model"] is null
                && nextContext["jarvis"]!["requested_model"] is null);
            var defaultModel = Plan(host.GetContext(), "Which model?", "default", []);
            Check("adapter default sentinel is not a specific model", defaultModel["capability_context"]!["jarvis"]!["reported_model"] is null);
            Check("stale conversation is superseded by explicit host-fact instructions", prompt["conversation"]!.AsArray().Count == 1
                && JarvisDialogueService.Instructions.Contains("takes precedence over older conversation claims")
                && JarvisDialogueService.Instructions.Contains("answer version questions directly"));

            // Exercise a new Jarvis session with no selected project as well: app facts are not project facts.
            Property(vm, "ActiveChat", null!);
            var noProject = Plan(host.GetContext(), "What version is VibeCode?", null, [])["capability_context"]!;
            Check("version is available without an active coding chat", noProject["current_chat"] is null
                && noProject["application"]!["running_version"]!.ToString() == AppVersion.Current.ToString());
        }
        finally { vm.ShutdownJarvis(); }

        JsonObject Plan(JarvisContext context, string request, string? reportedModel, IReadOnlyList<JarvisDialogueLine> history)
        {
            var session = new JarvisPlanningSession("{\"reply\":\"Fixture response\",\"actions\":[]}", reportedModel);
            var service = new JarvisDialogueService(Path.Combine(_root, "jarvis-knowledge-planner"), (_, _) => session);
            var pending = service.PlanAsync(new("codex", null, null), context, history, request, CancellationToken.None);
            PumpUntil(() => pending.IsCompleted);
            pending.GetAwaiter().GetResult();
            return session.Prompt!;
        }

        static string Preference(JsonNode context, string name) => context["settings"]!["preferences"]!.AsArray()
            .Single(row => row!["name"]!.ToString() == name)!["value"]!.ToString();
    }

    private static void VerifyJarvisAppKnowledgeLive()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live Jarvis knowledge checks require signed-in CODEX_HOME.");
        var settings = AppSettings.Current;
        settings.SecondBrainEnabled = false;
        var context = new JarvisContext(null, [], _root, false)
        { Settings = JarvisSettingsCatalog.Snapshot(settings) };
        var planner = new JarvisDialogueService(Path.Combine(_root, "live-jarvis-knowledge"));
        var selection = new JarvisSelection("codex", "gpt-6-luna", "low");
        var history = new List<JarvisDialogueLine>
        {
            new("user", "What version of VibeCode am I using?"),
            new("assistant", "I can't read the installed VibeCode version from this chat. You can find it in Settings > About."),
        };
        var report = new JsonArray();
        Run("Jarvis, what version of vibe code am I currently using?", plan => plan.Actions.Count == 0
            && plan.Reply.Contains(AppVersion.Current.ToString(), StringComparison.Ordinal)
            && !plan.Reply.Contains("can't", StringComparison.OrdinalIgnoreCase)
            && !plan.Reply.Contains("cannot", StringComparison.OrdinalIgnoreCase));
        Run("Where are About and Second Brain in Settings? Just explain their locations.", plan => plan.Actions.Count == 0
            && plan.Reply.Contains("General", StringComparison.OrdinalIgnoreCase)
            && plan.Reply.Contains("MCP", StringComparison.OrdinalIgnoreCase)
            && plan.Reply.Contains("extensions", StringComparison.OrdinalIgnoreCase));
        Run("Tell me which VibeCode version I'm running and open About in Settings.", plan =>
            plan.Reply.Contains(AppVersion.Current.ToString(), StringComparison.Ordinal)
            && plan.Actions.Count == 1 && plan.Actions[0] is { Kind: "open_settings", Target: "About" });

        void Run(string request, Func<JarvisPlan, bool> check)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var pending = planner.PlanAsync(selection, context, history, request, stop.Token);
            PumpUntil(() => pending.IsCompleted, 155000);
            var turn = pending.GetAwaiter().GetResult();
            report.Add(new JsonObject
            {
                ["request"] = request,
                ["reply"] = turn.Plan.Reply,
                ["provider"] = turn.Provider,
                ["model"] = turn.Model,
                ["action_count"] = turn.Plan.Actions.Count
            });
            File.WriteAllText(Path.Combine(_root, "live-knowledge-results.json"), report.ToJsonString(new() { WriteIndented = true }));
            Console.WriteLine("Jarvis: " + turn.Plan.Reply);
            Check("live Jarvis: " + request, check(turn.Plan));
            history.Add(new("user", request));
            history.Add(new("assistant", turn.Plan.Reply));
        }
    }
}
