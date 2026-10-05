using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var dataPath = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "agent1-codex-message-actions",
            "test-data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", dataPath);
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppSettings.Current.AgentMemoryEnabled = false;
        try
        {
            Run().GetAwaiter().GetResult();
            QueuedMessageLayout.Run();
            Console.WriteLine($"PASS: {_checks} Codex steering checks; submitted messages queue first, steer keeps the turn running, and rejected guidance remains available");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task Run()
    {
        var requests = new List<(string Method, JsonObject Parameters)>();
        using var session = new CodexSession(new CodexSessionOptions { Cwd = Environment.CurrentDirectory });
        typeof(CodexSession).GetProperty("SessionId")!.SetValue(session, "thread-1");
        typeof(CodexSession).GetProperty("RequestFixture", Hidden)!.SetValue(session,
            (Func<string, JsonObject?, Task<JsonNode?>>)((method, parameters) =>
            {
                requests.Add((method, parameters!.DeepClone().AsObject()));
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["result"] = new JsonObject { ["turnId"] = "turn-1" },
                });
            }));
        typeof(CodexSession).GetMethod("HandleNotificationFixture", Hidden)!.Invoke(session,
            ["turn/started", new JsonObject
            {
                ["threadId"] = "thread-1",
                ["turn"] = new JsonObject { ["id"] = "turn-1" },
            }]);
        Require(session.CanSteer, "active root turn is not steerable");

        var chat = new ChatViewModel(Environment.CurrentDirectory, provider: "codex", accountId: "offline-steer-test");
        typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(chat, session);
        chat.Status = "running";
        Require(chat.CanSteer, "queued messages cannot steer the active Codex turn");
        try
        {
            var accepted = await chat.SteerAsync("Focus on failing tests.");
            Require(accepted && chat.Status == "running" && !chat.HasQueued,
                "steering started or queued another turn");
            Require(chat.Items.OfType<UserItem>().Single().Text == "Focus on failing tests.",
                "accepted steer is missing from the transcript");
            Require(requests.Count == 1 && requests[0].Method == "turn/steer",
                "steering did not use turn/steer");
            var p = requests[0].Parameters;
            Require(p["threadId"]?.GetValue<string>() == "thread-1"
                    && p["expectedTurnId"]?.GetValue<string>() == "turn-1"
                    && p["input"]?[0]?["text"]?.GetValue<string>() == "Focus on failing tests.",
                "steering request lost its turn precondition or text");
            Require(p["model"] is null && p["cwd"] is null,
                "steering tried to override active turn settings");

            typeof(CodexSession).GetProperty("RequestFixture", Hidden)!.SetValue(session,
                (Func<string, JsonObject?, Task<JsonNode?>>)((_, _) => throw new InvalidOperationException("stale turn")));
            chat.Draft = "Keep this draft";
            Require(!await chat.SteerAsync("Keep this draft"), "rejected steer was reported as sent");
            Require(chat.Draft == "Keep this draft" && chat.Items.OfType<UserItem>().Count() == 1,
                "rejected steer consumed the draft or created a user message");
            Require(chat.Items.OfType<BannerItem>().Any(b => b.Level == "error"),
                "rejected steer did not explain how to recover");

            typeof(CodexSession).GetProperty("RequestFixture", Hidden)!.SetValue(session,
                (Func<string, JsonObject?, Task<JsonNode?>>)((method, parameters) =>
                {
                    requests.Add((method, parameters!.DeepClone().AsObject()));
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["result"] = new JsonObject { ["turnId"] = "turn-1" },
                    });
                }));
            Queue("Queued guidance");
            var queued = chat.Items.OfType<QueuedItem>().Single();
            Require(queued.QueueActionText == "Send message now", "Codex follow-up action does not match the requested label");
            Require(await chat.SteerQueuedAsync(queued) && !chat.HasQueued && !chat.Items.Contains(queued),
                "accepted queued steer remained pending for a second dispatch");
            Require(chat.Items.OfType<UserItem>().Last().Text == "Queued guidance"
                    && requests.Last().Method == "turn/steer",
                "queued guidance was not added to the active turn");

            Queue("Restore this queued guidance");
            var rejected = chat.Items.OfType<QueuedItem>().Single();
            typeof(CodexSession).GetProperty("RequestFixture", Hidden)!.SetValue(session,
                (Func<string, JsonObject?, Task<JsonNode?>>)((_, _) =>
                {
                    Queue("Follow-up typed during steering");
                    chat.Status = "idle";
                    typeof(ChatViewModel).GetMethod("FlushQueue", Hidden, null, Type.EmptyTypes, null)!
                        .Invoke(chat, null);
                    Require(chat.HasQueued, "turn completion dispatched the queue before steering was resolved");
                    chat.Status = "running";
                    throw new InvalidOperationException("stale turn");
                }));
            Require(!await chat.SteerQueuedAsync(rejected), "rejected queued steer was reported as accepted");
            var restored = chat.Items.OfType<QueuedItem>().ToArray();
            Require(restored.Length == 2 && ReferenceEquals(restored[0], rejected)
                    && restored[0].IsQueueHead && !restored[1].IsQueueHead
                    && restored[1].Text == "Follow-up typed during steering",
                "rejected steering lost queued text or changed FIFO order");
            Require(chat.Items.OfType<UserItem>().Count() == 2,
                "rejected queued steering added a duplicate transcript message");

            typeof(CodexSession).GetMethod("HandleNotificationFixture", Hidden)!.Invoke(session,
                ["turn/completed", new JsonObject
                {
                    ["threadId"] = "thread-1",
                    ["turn"] = new JsonObject { ["id"] = "turn-1", ["status"] = "completed" },
                }]);
            Require(!chat.CanSteer, "steering remained available after the root turn completed");
            using var sendFixture = new QueueSessionFixture();
            typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(chat, sendFixture);
            try
            {
                Require(chat.SendQueuedNow(restored[0]) && sendFixture.Interrupts == 1,
                    "Send message now did not request interruption before starting a new turn");
                Require(chat.HasQueued && !chat.CanSendQueuedNow && !chat.CanSteer,
                    "Send message now allowed another action before the interrupted turn settled");
            }
            finally { typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(chat, session); }
        }
        finally { chat.Close(); }

        void Queue(string text)
        {
            // The normal submission path is provider-neutral; the fake transport stays entirely offline.
            using var transport = new QueueSessionFixture();
            typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(chat, transport);
            try
            {
                var before = requests.Count;
                Require(chat.Send(text) && chat.HasQueued, "submitting during a turn did not leave a queued message");
                Require(transport.Sends == 0 && requests.Count == before,
                    "normal submission steered or dispatched the message before the user chose an action");
            }
            finally { typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(chat, session); }
        }
    }

    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class QueueSessionFixture : ICodingSession
    {
#pragma warning disable CS0067
        public event Action<JsonNode>? MessageReceived;
        public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string>? PermissionCancelled;
        public event Action<int, string>? Exited;
        public event Action? Initialized;
#pragma warning restore CS0067
        public JsonArray Commands { get; } = [];
        public JsonArray Models { get; } = [];
        public string? SessionId => "offline-queue";
        public bool HasExited { get; private set; }
        public int Sends { get; private set; }
        public int Interrupts { get; private set; }
        public void Start() { }
        public void SendUser(JsonNode content) => Sends++;
        public Task InterruptAsync() { Interrupts++; return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
