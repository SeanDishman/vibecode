using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    /// <summary>A chat that becomes a regular-bridge host in the middle of a task is asked for its pane title inside that
    /// turn instead of at its next request, and naming the pane never renames the chat. Drives the real Bridge buttons'
    /// code; agent 2 runs on this program's offline Codex fixture.</summary>
    private static void VerifyHostTaskTitle()
    {
        var previousRuntime = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            // Bridge clicked while the chat is deep in a long task, as in the original report.
            var (vm, host) = BridgeHost("host-title-working", "claude", "Build the volume module");
            host.Status = "running";
            vm.ActivateBridge("codex");
            PumpUntil(() => Steerable(host).Steers.Count == 1);
            var note = Steerable(host).Steers.Single();
            var noteText = WireText(note);
            Check("starting a bridge asks a working host for its pane title inside the running turn",
                noteText.Contains(BridgeTaskTitlePolicy.Header) && noteText.Contains(TaskTitleTool("claude")));
            Check("the request is for the pane title only and keeps the chat's name",
                noteText.Contains("does not rename the chat") && noteText.Contains("do not call chat_set_title"));
            Check("the host keeps working: nothing interrupted, queued or sent as a new turn", host.Status == "running"
                && Steerable(host).Sent.Count == 0 && Steerable(host).Interrupts == 0 && !host.HasQueued);
            Check("the request is the app's note, so no chat bubble appears", !host.Items.OfType<UserItem>().Any());
            // Claude echoes a steered line back as a user message; the note must stay hidden there too.
            Call(host, "IngestSdk", new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = note.DeepClone() },
            });
            Check("the echoed note never becomes a chat bubble", !host.Items.OfType<UserItem>().Any());
            var applied = Tool(host, "bridge_set_task_title", new() { ["title"] = "Volume tray popup" });
            Check("the host's title shows beside its name in the pane header",
                applied["applied"]!.GetValue<bool>() && host.BridgeHeaderTaskTitle == "Volume tray popup");
            Check("naming the pane leaves the chat's own title alone", host.Title == "Build the volume module");
            PumpUntil(() => vm.BridgePanes[1].SessionId is not null);   // a peer with a session makes the bridge resumable
            vm.CloseBridge();
            vm.ActivateBridge("codex");
            Check("reopening the saved bridge keeps the host's title without asking again",
                vm.BridgePanes.Count == 2 && ReferenceEquals(vm.BridgePanes[0], host)
                && host.BridgeHeaderTaskTitle == "Volume tray popup" && Steerable(host).Steers.Count == 1);
            vm.CloseBridge();

            // Idle when Bridge is clicked: nothing to steer into, so its next request asks, as for every agent.
            var (idleVm, idle) = BridgeHost("host-title-idle", "claude", "Plan the settings page");
            idleVm.ActivateBridge("codex");
            Settle();
            Check("an idle host gets no mid-turn request", Steerable(idle).Steers.Count == 0);
            Check("the idle host's next request is dispatched", idle.Send("Add a display properties toggle."));
            PumpUntil(() => Steerable(idle).Sent.Count == 1);
            Check("the idle host's next request asks for its pane title",
                Steerable(idle).Sent[0].Contains("Required in this regular bridge") && Steerable(idle).Sent[0].Contains(TaskTitleTool("claude")));
            idleVm.CloseBridge();

            // The second display builds its roster on a separate path.
            var (secondVm, second) = BridgeHost("host-title-second-display", "codex", "Fix the tray icon");
            second.Status = "running";
            Check("a bridge starts on the second display", secondVm.ActivateBridgeOnSecondary(second, "codex"));
            PumpUntil(() => Steerable(second).Steers.Count == 1);
            Check("a working host on the second display is asked too, with its provider's tool name",
                WireText(Steerable(second).Steers[0]).Contains(TaskTitleTool("codex")));
            secondVm.CloseSecondaryBridge();

            // Kimi rejects any message while a turn runs, so its title waits for the next request.
            var (kimiVm, kimi) = BridgeHost("host-title-kimi", "kimi", "Write release notes");
            kimi.Status = "running";
            kimiVm.ActivateBridge("codex");
            Settle();
            Check("a host that cannot take a mid-turn message is not sent one", Steerable(kimi).Steers.Count == 0);
            kimiVm.CloseBridge();
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previousRuntime); }
    }

    private static (MainViewModel, ChatViewModel) BridgeHost(string name, string provider, string title)
    {
        var vm = new MainViewModel();
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        var chat = new ChatViewModel(path, title: title, provider: provider);
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new SteerableSession());
        typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
        Property(chat, "SessionId", Guid.NewGuid().ToString("N"));
        chat.Status = "idle";
        Chats.Add(chat); vm.Chats.Add(chat);
        Call(vm, "Track", chat);
        vm.ActiveChat = chat;
        return (vm, chat);
    }

    private static SteerableSession Steerable(ChatViewModel chat) =>
        (SteerableSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;

    private static string WireText(JsonNode content) => content is JsonValue value ? value.GetValue<string>()
        : string.Join("\n", content.AsArray().OfType<JsonObject>().Select(block => block["text"]?.GetValue<string>()));

    private static void Settle()
    {
        for (var i = 0; i < 5; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>A provider session with a live turn that accepts same-turn messages, like Claude's or Codex's.</summary>
    private sealed class SteerableSession : ICodingSession, ISteerableSession
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
        public bool CanSteer => !HasExited;
        public List<string> Sent { get; } = [];
        public List<JsonNode> Steers { get; } = [];
        public int Interrupts { get; private set; }
        public void Start() { }
        public void SendUser(JsonNode content) => Sent.Add(content.ToJsonString().Replace("\\n", "\n"));
        public Task SteerAsync(JsonNode content) { Steers.Add(content.DeepClone()); return Task.CompletedTask; }
        public Task InterruptAsync() { Interrupts++; return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
