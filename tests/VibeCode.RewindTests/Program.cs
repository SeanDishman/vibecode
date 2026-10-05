using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<ChatViewModel> Chats = [];
    private static readonly List<TurnRollbackCheckpoint> Checkpoints = [];
    private static int _checks;
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "vibecode-rewind-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, "settings"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        try
        {
            var run = Run();
            PumpUntil(() => run.IsCompleted);
            run.GetAwaiter().GetResult();
            VerifyConfirmationAndComposers();
            Console.WriteLine($"PASS: {_checks} rewind checks across Claude, Codex, Kimi, Grok and GLM; normal chats and Bridge panes. No live model calls.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: not null } wrapped
                ? wrapped.InnerException : error);
            return 1;
        }
        finally
        {
            foreach (var chat in Chats) chat.Close();
            foreach (var checkpoint in Checkpoints) checkpoint.Complete();
            app.Shutdown();
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(_root).StartsWith("vibecode-rewind-tests-", StringComparison.Ordinal))
                Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task Run()
    {
        foreach (var provider in new[] { "claude", "codex", "kimi", "grok", "glm" })
            foreach (var bridge in new[] { false, true })
                await VerifyStopAndCascade(provider, bridge);
        await VerifyFileConflict();
        await VerifyTextOnlyAndSteerOverlap();
    }

    private static async Task VerifyStopAndCascade(string provider, bool bridge)
    {
        var label = provider + (bridge ? " bridge" : " chat");
        var chat = Chat(label, provider, bridge);
        var session = Session(chat);
        var path = Path.Combine(chat.Cwd, "source.cs");
        File.WriteAllText(path, "original\n");
        var history = new TextItem { Text = "Earlier conversation stays." };
        chat.Items.Add(history);
        var first = Prompt(chat, "First prompt", path, "first\n", completed: true);
        chat.Items.Add(new TextItem { Text = "First reply" });
        var second = Prompt(chat, "Second prompt", path, "second\n", completed: false);
        chat.Items.Add(new TextItem { Text = "Second reply" });
        Field(chat, "_activeRollback", Checkpoint(second));
        chat.Draft = "unsent draft";
        chat.Status = "running";
        Check(label + ": queued follow-up accepted", chat.Send("Queued follow-up"));
        session.OnInterrupt = () => chat.Status = "idle";

        var rewind = chat.StopAndUndoPromptAsync(first);
        await Task.Delay(80); // allow the idle-status FlushQueue to run while the checkpoint is still unsealed
        Check(label + ": stop reaches provider", session.Interrupts == 1);
        Check(label + ": idle does not dispatch during rewind", session.Sent.Count == 0 && chat.HasQueued);
        Check(label + ": queue action disabled during rewind", !chat.CanSendQueuedNow);
        Check(label + ": source unchanged until seal", File.ReadAllText(path) == "second\n");
        Check(label + ": new input held during rewind", chat.Send("Typed while rewinding") && session.Sent.Count == 0);
        Check(label + ": overlapping rewind rejected", !(await chat.StopAndUndoPromptAsync(second)).Success
            && !chat.UndoPrompt(second).Success);
        await Task.Run(Checkpoint(second).Complete);
        var result = await rewind;
        Check(label + ": both prompts restored newest first", result.Success && result.RestoredFiles == 2
            && File.ReadAllText(path) == "original\n");
        Check(label + ": older conversation retained", chat.Items.Contains(history));
        Check(label + ": prompts and replies removed", !chat.Items.Contains(first) && !chat.Items.Contains(second)
            && !chat.Items.OfType<TextItem>().Any(row => row.Text is "First reply" or "Second reply"));
        Check(label + ": queued input and draft preserved", chat.HasQueued && chat.Draft == "unsent draft"
            && chat.Items.OfType<QueuedItem>().Single().Text.Contains("Typed while rewinding"));
        session.OnSend = () => Check(label + ": next dispatch sees restored files", File.ReadAllText(path) == "original\n");
        await WaitUntil(() => session.Sent.Count == 1);
        Check(label + ": next provider input receives rewind note", session.Sent[0].Contains("restored the local workspace")
            && session.Sent[0].Contains("Queued follow-up") && session.Sent[0].Contains("Typed while rewinding"));
        SealActive(chat);
    }

    private static async Task VerifyFileConflict()
    {
        var chat = Chat("file-conflict", "codex", true);
        var path = Path.Combine(chat.Cwd, "source.cs");
        File.WriteAllText(path, "original");
        var prompt = Prompt(chat, "Edit file", path, "agent edit", completed: true);
        chat.Draft = "keep my draft";
        chat.Status = "idle";
        File.WriteAllText(path, "user's later edit");
        var result = await chat.StopAndUndoPromptAsync(prompt);
        Check("conflict: later user edit preserved", !result.Success && File.ReadAllText(path) == "user's later edit");
        Check("conflict: transcript and draft preserved", chat.Items.Contains(prompt) && chat.Draft == "keep my draft");

        var peer = Chat("peer-conflict", "kimi", true, chat.Cwd);
        var peerPrompt = Prompt(peer, "Peer edit", path, "peer edit", completed: true);
        result = await chat.StopAndUndoPromptAsync(prompt);
        Check("bridge: newer peer checkpoint blocks owner rewind", !result.Success && File.ReadAllText(path) == "peer edit"
            && chat.Items.Contains(prompt) && peer.Items.Contains(peerPrompt));
        Check("bridge: peer can undo its own latest prompt", (await peer.StopAndUndoPromptAsync(peerPrompt)).Success
            && File.ReadAllText(path) == "user's later edit");
    }

    private static async Task VerifyTextOnlyAndSteerOverlap()
    {
        var chat = Chat("text-only", "codex", false);
        var prompt = Prompt(chat, "No files changed", null, null, completed: true);
        chat.Status = "idle";
        Field(chat, "_steerSubmitting", true);
        Check("steer: accepted message cannot land after transcript removal", !(await chat.StopAndUndoPromptAsync(prompt)).Success
            && chat.Items.Contains(prompt));
        Field(chat, "_steerSubmitting", false);
        var result = await chat.StopAndUndoPromptAsync(prompt);
        Check("text-only: latest prompt rewinds without file changes", result.Success && result.RestoredFiles == 0
            && !chat.Items.Contains(prompt) && Session(chat).Interrupts == 0);
    }

    private static ChatViewModel Chat(string name, string provider, bool bridge, string? cwd = null)
    {
        cwd ??= Path.Combine(_root, name.Replace(' ', '-'));
        Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, provider: provider, accountId: "offline-rewind-test")
        {
            BridgeLabel = bridge ? "Agent 3" : "",
        };
        Field(chat, "_session", new FakeSession());
        chat.Status = "idle";
        Chats.Add(chat);
        return chat;
    }

    private static UserItem Prompt(ChatViewModel chat, string text, string? path, string? after, bool completed)
    {
        var checkpoint = TurnRollbackCheckpoint.Begin(chat.Cwd);
        Checkpoints.Add(checkpoint);
        if (!checkpoint.CaptureSucceeded) throw new Exception(checkpoint.UnavailableReason);
        if (path is not null)
        {
            checkpoint.TrackPath(path);
            File.WriteAllText(path, after);
        }
        if (completed) checkpoint.Complete();
        var prompt = new UserItem { Text = text, Owner = chat };
        Call(prompt, "AttachRollbackCheckpoint", checkpoint);
        chat.Items.Add(prompt);
        return prompt;
    }

    private static TurnRollbackCheckpoint Checkpoint(UserItem prompt) =>
        (TurnRollbackCheckpoint)typeof(UserItem).GetProperty("RollbackCheckpoint", Hidden)!.GetValue(prompt)!;
    private static FakeSession Session(ChatViewModel chat) => (FakeSession)typeof(ChatViewModel).GetField("_session", Hidden)!.GetValue(chat)!;
    private static void Field(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Hidden)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static void Property(object target, string name, object? value) => target.GetType().GetProperty(name, Hidden)!.SetValue(target, value);
    private static void SealActive(ChatViewModel chat)
    {
        if (typeof(ChatViewModel).GetField("_activeRollback", Hidden)!.GetValue(chat) is TurnRollbackCheckpoint checkpoint)
        {
            Checkpoints.Add(checkpoint);
            checkpoint.Complete();
            Field(chat, "_activeRollback", null);
        }
        chat.Status = "idle";
    }
    private static void Check(string text, bool condition)
    {
        if (!condition) throw new Exception("FAIL: " + text);
        _checks++;
        Console.WriteLine("PASS: " + text);
    }
    private static async Task WaitUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Provider dispatch did not finish.");
            await Task.Delay(10);
        }
    }
    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(25)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("Rewind fixture did not finish.");
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
        public Action? OnInterrupt { get; set; }
        public Action? OnSend { get; set; }
        public void Start() { }
        public void SendUser(JsonNode content) { OnSend?.Invoke(); Sent.Add(content.ToJsonString().Replace("\\n", "\n")); }
        public Task InterruptAsync() { Interrupts++; OnInterrupt?.Invoke(); return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
