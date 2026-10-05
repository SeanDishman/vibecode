using System.IO;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyJarvisChatActions()
    {
        const string request = "Close chats containing login or checkout";
        var plan = JarvisPlanParser.Parse(PlanJson("close_chats", request, new()
        { ["terms"] = new JsonArray("login", "checkout"), ["match"] = "any" }), request);
        var action = plan.Actions.Single();
        Check("bulk chat requests parse without a project path or coding task",
            action.Kind == "close_chats" && action.Path == "" && action.ChatFilter!.Terms!.SequenceEqual(["login", "checkout"]));
        RejectJarvis("unscoped close never becomes close all", () => JarvisPlanParser.Parse(PlanJson("close_chats", request), request));
        RejectJarvis("an unresolved single chat cannot open everything", () => JarvisPlanParser.Parse(PlanJson("open_chat", request), request));
        RejectJarvis("blank search terms cannot match every chat", () => JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["terms"] = new JsonArray(" ") }), request));
        RejectJarvis("unknown search modes are rejected", () => JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["terms"] = new JsonArray("login"), ["match"] = "maybe" }), request));
        RejectJarvis("all cannot silently override a filter", () => JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["terms"] = new JsonArray("login"), ["all"] = true }), request));
        RejectJarvis("subdirectories require a directory", () => JarvisPlanParser.Parse(PlanJson("list_chats", request,
            new() { ["include_subdirectories"] = true }), request));
        RejectJarvis("an unquoted action is not authorized", () => JarvisPlanParser.Parse(PlanJson("close_chats", "invented request",
            new() { ["all"] = true }), request));
        RejectJarvis("unsupported script fields are rejected", () => JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["all"] = true, ["command"] = "remove chats" }), request));
        RejectJarvis("invalid boolean fields are rejected", () => JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["all"] = "true" }), request));
        Check("listing needs no filter", JarvisPlanParser.Parse(PlanJson("list_chats", request), request).Actions.Count == 1);
        Check("explicit all-chats selection parses", JarvisPlanParser.Parse(PlanJson("close_chats", request,
            new() { ["all"] = true }), request).Actions.Single().ChatFilter!.All);

        var directory = Path.Combine(_root, "jarvis-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var summary = new JarvisChatSummary("one", "Login repairs", directory, "idle", false);
        Check("word matching ignores case", JarvisChatPolicy.Matches(summary, new(Terms: ["LOGIN"]), []));
        Check("all words can span separate messages", JarvisChatPolicy.Matches(summary,
            new(Terms: ["cache", "timeout"], SearchIn: "messages"), ["Fix the cache.", "Resolve the timeout."]));
        Check("all matching requires every word", !JarvisChatPolicy.Matches(summary,
            new(Terms: ["cache", "missing"], SearchIn: "messages"), ["Fix the cache."]));
        Check("any matching needs just one requested phrase", JarvisChatPolicy.Matches(summary,
            new(Terms: ["cart total", "missing"], Match: "any"), ["Fix cart total rounding."]));
        Check("quoted phrases stay contiguous", !JarvisChatPolicy.Matches(summary,
            new(Terms: ["cart total"]), ["Cart and total are separated."]));
        Check("title-only search excludes messages", !JarvisChatPolicy.Matches(summary,
            new(Terms: ["checkout"], SearchIn: "title"), ["checkout"]));
        Check("message-only search excludes titles", !JarvisChatPolicy.Matches(summary,
            new(Terms: ["Login"], SearchIn: "messages"), []));
        Check("directory comparison normalizes case and separators", JarvisChatPolicy.Matches(summary,
            new(Directory: directory.ToUpperInvariant() + Path.DirectorySeparatorChar), []));
        Check("subdirectories are opt-in", !JarvisChatPolicy.Matches(summary with { Path = Path.Combine(directory, "child") },
            new(Directory: directory), []));
        Check("subdirectory selection includes descendants", JarvisChatPolicy.Matches(summary with { Path = Path.Combine(directory, "child") },
            new(Directory: directory, IncludeSubdirectories: true), []));
        Check("sibling path prefixes are never descendants", !JarvisChatPolicy.Matches(summary with { Path = directory + "-other" },
            new(Directory: directory, IncludeSubdirectories: true), []));
        Check("a chat ID and words are both required when combined", !JarvisChatPolicy.Matches(summary,
            new(ChatId: "different", Terms: ["Login"]), []));

        AppSettings.Current.SecondBrainEnabled = false;
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        var login = AddChat(directory, "Login repair", "idle");
        var checkout = AddChat(directory, "Payment fixes", "running");
        checkout.Items.Add(new TextItem { Text = "The checkout total needs rounding." });
        var locked = AddChat(directory, "Login reference", "idle");
        vm.ToggleChatLock(locked);
        var sibling = AddChat(directory + "-other", "Other work", "idle");
        sibling.Items.Add(new TextItem { Text = "Unrelated work" });
        var child = AddChat(Path.Combine(directory, "child"), "Nested work", "idle");
        Property(vm, "ActiveChat", login);
        var context = ((IJarvisActionHost)vm).GetContext();
        Check("Jarvis receives open chat IDs and lock state without memory", context.OpenChats.Count == 5
            && context.CurrentChatId == login.BridgeAgentId && context.OpenChats.Single(c => c.Id == locked.BridgeAgentId).IsLocked
            && !context.MemoryEnabled && context.MemoryContext == "");
        var host = (IJarvisChatActionHost)vm;
        var listing = Execute("list_chats", new(Directory: directory));
        Check("listing searches every matching sidebar chat", listing.Message.Contains("Found 3") && listing.Message.Contains("Payment fixes"));
        var opened = Execute("open_chat", new(Terms: ["Payment fixes"], SearchIn: "title"));
        Check("open chat activates an existing conversation without creating another", ReferenceEquals(vm.ActiveChat, checkout)
            && vm.Chats.Count == 5 && opened.Message.StartsWith("Opened"));
        var ambiguous = Execute("open_chat", new(Terms: ["Login"], SearchIn: "title"));
        Check("ambiguous open asks for a target without changing selection", ReferenceEquals(vm.ActiveChat, checkout)
            && ambiguous.Message.Contains("More than one"));
        var noMatches = Execute("close_chats", new(Terms: ["nonexistent phrase"]));
        Check("no match has no side effects or invented success", vm.Chats.Count == 5 && noMatches.Message.StartsWith("No open chats match"));
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            try { host.ExecuteChatActionAsync(new("close_chats", "", "", request) { ChatFilter = new(All: true) }, stopped.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Check("cancellation before execution leaves every chat open", vm.Chats.Count == 5);
        }

        // Exercise the actual planner adapter, JSON parser, runtime dispatcher and desktop executor together.
        var fake = new JarvisPlanningSession(PlanJson("close_chats", request,
            new() { ["path"] = directory, ["terms"] = new JsonArray("login", "checkout"), ["match"] = "any" }));
        var service = new JarvisDialogueService(Path.Combine(directory, "planner"), (_, _) => fake);
        var runtime = new JarvisRuntime(service, vm);
        var answer = runtime.SubmitAsync(request, new("codex", null, null), CancellationToken.None);
        PumpUntil(() => answer.IsCompleted);
        var reply = answer.GetAwaiter().GetResult();
        Check("natural-language planning reaches the real bulk-close action", reply.Contains("Closed 2 chats")
            && !vm.Chats.Contains(login) && !vm.Chats.Contains(checkout));
        Check("bulk close stops running sessions", checkout.Status == "closed" && Session(checkout).HasExited);
        Check("bulk close preserves locked and out-of-directory chats", vm.Chats.Contains(locked) && vm.Chats.Contains(sibling)
            && vm.Chats.Contains(child) && reply.Contains("1 locked chat"));
        Check("close preserves history and owned session IDs", AppSettings.Current.OwnedSessions.Contains(checkout.SessionId!)
            && !AppSettings.Current.DeletedSessions.Contains(checkout.SessionId!));
        Check("results use verified receipts instead of a model's success claim", !reply.Contains("unverified model reply"));
        Check("conversation contents stay out of the planning prompt", fake.Prompt!["capability_context"]!["open_chat_count"]!.GetValue<int>() == 5
            && !fake.Prompt.ToJsonString().Contains("checkout total needs rounding"));
        Check("Jarvis remembers the actual action outcome for follow-ups", runtime.History.Last().Text == reply);

        // "Current" must refer to the chat at request time even when the selection changes while planning.
        Property(vm, "ActiveChat", child);
        var delayed = new JarvisRuntime(new JarvisPlanFixture(ctx =>
        {
            Property(vm, "ActiveChat", sibling);
            return new("Closing current.", [new("close_chats", "", "", "Close this chat") { ChatFilter = new(ChatId: "current") }]);
        }), vm);
        var current = delayed.SubmitAsync("Close this chat", new("codex", null, null), CancellationToken.None).GetAwaiter().GetResult();
        Check("current-chat targeting is anchored before planning", !vm.Chats.Contains(child) && vm.Chats.Contains(sibling) && current.Contains("Nested work"));
        var all = Execute("close_chats", new(All: true));
        Check("explicit close-all preserves locks", vm.Chats.Count == 1 && vm.Chats.Contains(locked) && all.Message.Contains("Closed 1 chat"));
        vm.ShutdownJarvis();

        var (bridgeVm, team) = Team("jarvis-locked-bridge", 2);
        team[0].Status = team[1].Status = "idle";
        bridgeVm.ToggleChatLock(team[1]);
        var protectedBridge = ((IJarvisChatActionHost)bridgeVm).ExecuteChatActionAsync(
            new("close_chats", "", "", "Close this bridge") { ChatFilter = new(ChatId: team[0].BridgeAgentId) }, CancellationToken.None).GetAwaiter().GetResult();
        Check("closing a bridge host cannot bypass a peer's lock", bridgeVm.BridgePanes.Count == 2 && team.All(chat => chat.Status == "idle")
            && protectedBridge.Message.Contains("locked chats"));

        ChatViewModel AddChat(string path, string title, string status)
        {
            Directory.CreateDirectory(path);
            var chat = new ChatViewModel(path, title: title, provider: "codex") { Status = status, SessionId = Guid.NewGuid().ToString("N") };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            Chats.Add(chat); vm.Chats.Add(chat); Call(vm, "Track", chat);
            AppSettings.Current.OwnedSessions.Add(chat.SessionId);
            return chat;
        }
        JarvisActionReceipt Execute(string kind, JarvisChatFilter filter) => host.ExecuteChatActionAsync(
            new(kind, filter.Directory ?? "", "", request) { ChatFilter = filter }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string PlanJson(string kind, string quote, JsonObject? fields = null)
    {
        var action = fields ?? new JsonObject();
        action["kind"] = kind; action["request_quote"] = quote;
        return new JsonObject { ["reply"] = "unverified model reply", ["actions"] = new JsonArray(action) }.ToJsonString();
    }

    private static void RejectJarvis(string text, Action action)
    {
        try { action(); } catch (InvalidOperationException) { Check(text, true); return; }
        throw new Exception("FAIL: " + text);
    }

    private sealed class JarvisPlanFixture(Func<JarvisContext, JarvisPlan> plan) : IJarvisPlanner
    {
        public Task<JarvisTurn> PlanAsync(JarvisSelection selection, JarvisContext context,
            IReadOnlyList<JarvisDialogueLine> history, string request, CancellationToken cancellationToken) =>
            Task.FromResult(new JarvisTurn(plan(context), selection.Provider, selection.Model, null));
    }

    private sealed class JarvisPlanningSession(string result) : ICodingSession
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
        public string? SessionId => "jarvis-planner-fixture";
        public bool HasExited { get; private set; }
        public JsonObject? Prompt { get; private set; }
        public void Start() => Initialized?.Invoke();
        public void SendUser(JsonNode content)
        {
            Prompt = JsonNode.Parse(content.GetValue<string>())!.AsObject();
            MessageReceived?.Invoke(new JsonObject { ["type"] = "result", ["result"] = result });
        }
        public Task InterruptAsync() => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject response, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
