using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<ChatViewModel> Chats = [];
    private static string _root = "";
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Environment.CurrentDirectory, "artifacts", "goal-command",
            (args.Contains("--live") ? "live-" : "offline-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, "settings"));
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", Path.Combine(_root, "empty-kimi-home"));
        Environment.SetEnvironmentVariable("KIMI_SHARE_DIR", Path.Combine(_root, "empty-kimi-share"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(_root, "empty-codex-home"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        AppSettings.Current.McpServers.Clear();
        try
        {
            if (args.Contains("--live")) RunLive();
            else if (args.Contains("--ui-only")) VerifyComposerViews();
            else { RunOffline(); VerifyComposerViews(); }
            Console.WriteLine($"PASS: {_checks} {(args.Contains("--live") ? "live GPT-5.6 Luna simulation" : "offline goal") } checks.");
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

    private static void RunOffline()
    {
        foreach (var provider in new[] { "codex", "claude", "glm", "kimi", "grok" })
        {
            var menu = new ChatViewModel(_root, provider: provider) { ExcludeFromMemory = true };
            Chats.Add(menu);
            Check(provider + " composer exposes /goal and supported native commands", menu.Commands[0].Name == "goal"
                && menu.Commands.Count == (provider is "claude" or "codex" ? 2 : 1)
                && menu.Commands.Any(c => c.Name == "compact") == (provider is "claude" or "codex"));
        }
        Check("goal accepts multiline text and case-insensitive command", GoalPolicy.TryParseCommand(" /GOAL\nBuild it\nVerify it", out var text) && text == "Build it\nVerify it");
        Check("goal does not intercept similarly named commands", !GoalPolicy.TryParseCommand("/goals later", out _));

        var plain = NewFake("plain");
        Send(plain, "ordinary prompt", 1);
        End(plain, "done");
        Quiet(plain, 1, "ordinary chat does not auto-prompt");
        Check("empty goal is rejected without starting a turn", !plain.Send("/goal  ") && plain.Goal is null && Session(plain).Sent.Count == 1);

        var chat = NewFake("repeat-and-complete");
        Send(chat, "/goal Complete both steps", 1);
        var goal = chat.Goal!;
        Check("goal is stored separately from slash syntax", goal.Text == "Complete both steps" && !goal.Paused && !goal.Completed);
        Check("goal command is converted to a provider-neutral prompt", !Session(chat).Sent[0].Contains("/goal") && Session(chat).Sent[0].Contains("[VIBECODE ACTIVE GOAL]"));
        var normalized = (string)Call(chat, "StripInjectedPrelude", Session(chat).Sent[0])!;
        Check("history echo removes the goal context", normalized == goal.Text);
        var initial = End(chat, "First step only.\n" + GoalPolicy.CompleteMarker(goal));
        Check("a remembered protocol marker is hidden on normal replies too", initial.RenderText == "First step only.");
        WaitSent(chat, 2);
        Check("only an actual goal-check reply can complete the goal", !chat.Goal!.Completed);
        Check("automatic check asks the actual goal", Session(chat).Sent[1].Contains(GoalPolicy.CheckHeader) && Session(chat).Sent[1].Contains(goal.Text));
        Check("automatic check has a readable transcript card", chat.Items.OfType<UserItem>().Last().AgentKind == "Goal check");
        Check("checks are system-injected prompts", (bool)Call(chat, "IsSystemInjectedPrompt", chat.Items.OfType<UserItem>().Last().Text)!);
        End(chat, "Still unfinished.");
        WaitSent(chat, 3);
        Check("another premature stop causes another check", !chat.Goal!.Completed);
        End(chat, "[VIBECODE_GOAL_COMPLETE:" + Guid.NewGuid().ToString("N") + "]");
        WaitSent(chat, 4);
        Check("a marker for another goal cannot stop this goal", !chat.Goal!.Completed);
        var complete = End(chat, "Both steps verified.\n" + GoalPolicy.CompleteMarker(goal));
        Check("verified completion settles the goal", chat.Goal is { Completed: true, Paused: false });
        Check("internal completion line is hidden, response remains", complete.RenderText == "Both steps verified." && complete.Text.Contains(goal.Id));
        Quiet(chat, 4, "completed goal stops further checks");

        var queued = NewFake("queued-priority");
        Send(queued, "/goal First goal", 1);
        Check("user guidance queues while working", queued.Send("Use the corrected value"));
        Check("replacement goal queues separately", queued.Send("/goal Replacement goal") && queued.HasQueued);
        Check("message after the new goal stays separate", queued.Send("Then verify the result"));
        End(queued, "Stopped early");
        WaitSent(queued, 2);
        Check("queued human guidance wins over automatic checks", Session(queued).Sent[1].Contains("Use the corrected value") && !Session(queued).Sent[1].Contains(GoalPolicy.CheckHeader));
        End(queued, "Guidance applied");
        WaitSent(queued, 3);
        Check("new queued goal replaces the prior goal at dispatch", queued.Goal!.Text == "Replacement goal");
        End(queued, "Goal work started");
        WaitSent(queued, 4);
        Check("post-goal human message follows the replacement", Session(queued).Sent[3].Contains("Then verify the result") && queued.Goal!.Text == "Replacement goal");
        queued.Interrupt();
        End(queued, "Stopped.");
        Check("manual stop pauses goal and interrupts the provider", queued.Goal!.Paused && Session(queued).Interrupts == 1);
        Quiet(queued, 4, "goal check does not override Stop");

        var waiting = NewFake("needs-input");
        Send(waiting, "/goal Use the user's chosen color", 1);
        End(waiting, "Which color?");
        WaitSent(waiting, 2);
        End(waiting, "Please provide a color.\n" + GoalPolicy.WaitingMarker(waiting.Goal!));
        Check("needs-user-input pauses without pretending completion", waiting.Goal is { Paused: true, Completed: false });
        Quiet(waiting, 2, "waiting for an answer does not spin");
        Send(waiting, "Turquoise", 3);
        Check("user answer resumes the same goal", waiting.Goal is { Paused: false, Completed: false });
        var answered = End(waiting, "Color accepted\n" + GoalPolicy.CompleteMarker(waiting.Goal!));
        Check("user-answer reply hides remembered goal metadata without skipping confirmation", answered.RenderText == "Color accepted" && !waiting.Goal!.Completed);
        WaitSent(waiting, 4);
        End(waiting, "Finished.\n" + GoalPolicy.CompleteMarker(waiting.Goal!));

        var draft = NewFake("draft");
        Send(draft, "/goal Finish after my draft", 1);
        draft.Draft = "I am still typing";
        End(draft, "Stopped early");
        Quiet(draft, 1, "unsent draft suppresses automatic checks");
        draft.Draft = "";
        WaitSent(draft, 2);
        Check("clearing the unsent draft resumes checks", Session(draft).Sent[1].Contains(GoalPolicy.CheckHeader));
        draft.Interrupt(); End(draft, "Stopped");

        var attachment = NewFake("attachment");
        Send(attachment, "/goal Finish after the attachment", 1);
        attachment.Attachments.Add(new Attachment { Kind = "text", FileName = "notes.txt", Text = "Unsent notes" });
        End(attachment, "Early stop");
        Quiet(attachment, 1, "staged attachments suppress automatic checks");
        attachment.Attachments.Clear();
        WaitSent(attachment, 2);
        End(attachment, "Done.\n" + GoalPolicy.CompleteMarker(attachment.Goal!));

        var canceled = NewFake("canceled-queue");
        Send(canceled, "/goal Continue after canceled follow-up", 1);
        Check("follow-up accepts while the goal runs", canceled.Send("Unsent follow-up"));
        End(canceled, "Early stop");
        Check("queued follow-up can be canceled before dispatch", canceled.CancelQueued(canceled.Items.OfType<QueuedItem>().Single()));
        WaitSent(canceled, 2);
        Check("canceling the last queued message resumes the goal check", Session(canceled).Sent[1].Contains(GoalPolicy.CheckHeader));
        End(canceled, "Done.\n" + GoalPolicy.CompleteMarker(canceled.Goal!));

        var batch = NewFake("queue-batch");
        Send(batch, "/goal Original batch goal", 1);
        batch.Send("Guidance before replacement");
        batch.Send("/goal New batch goal");
        batch.Send("Guidance after replacement");
        Check("send-queued-now accepts the waiting human messages", batch.SendQueuedNow(batch.Items.OfType<QueuedItem>().First()));
        End(batch, "Interrupted to send queue");
        WaitSent(batch, 2);
        Check("send-queued-now preserves the goal boundary", !Session(batch).Sent[1].Contains("New batch goal"));
        End(batch, "First guidance received");
        WaitSent(batch, 3);
        Check("batched goal still replaces only its own objective", batch.Goal!.Text == "New batch goal");
        End(batch, "New goal started");
        WaitSent(batch, 4);
        batch.Interrupt(); End(batch, "Stopped");

        var error = NewFake("error");
        Send(error, "/goal Finish despite early stops", 1);
        End(error, "Provider unavailable", failed: true);
        Check("provider failures pause the goal", error.Goal!.Paused && error.Status == "error");
        Quiet(error, 1, "provider errors cannot create a retry storm");

        var extended = NewFake("extended");
        extended.SetExtendedQueueEnabled(true);
        Send(extended, "/goal Goal with extended queue enabled", 1);
        Check("extended queue does not wrap or swallow /goal", extended.Goal!.Text == "Goal with extended queue enabled");
        End(extended, "Early stop");
        WaitSent(extended, 2);
        End(extended, "Done.\n" + GoalPolicy.CompleteMarker(extended.Goal!));
        Quiet(extended, 2, "goal checks are not user queue entries");

        var restored = NewFake("restored");
        var stored = new ChatGoal(Guid.NewGuid().ToString("N"), "Persisted objective");
        var state = JsonSerializer.Deserialize<OpenChatState>(JsonSerializer.Serialize(new OpenChatState { Cwd = restored.Cwd, Goal = stored }))!;
        restored.RestoreGoal(state.Goal);
        WaitSent(restored, 1);
        Check("restored active goal checks after idle initialization", restored.Goal == stored && Session(restored).Sent[0].Contains(stored.Text));
        End(restored, "Done.\n" + GoalPolicy.CompleteMarker(stored));
        Quiet(restored, 1, "restored completion stays settled");
        var peer = JsonSerializer.Deserialize<SavedBridgePane>(JsonSerializer.Serialize(new SavedBridgePane { Goal = stored with { Paused = true } }))!;
        restored.RestoreGoal(peer.Goal);
        Quiet(restored, 1, "saved bridge pause is preserved");
        restored.RestoreGoal(new ChatGoal("bad-id", "bad record"));
        Check("malformed saved goal cannot start checks", restored.Goal is null);

        var vm = new MainViewModel();
        vm.Chats.Add(chat);
        Field(vm, "_sessionRestored", true);
        Call(vm, "SnapshotSession");
        Check("session snapshots include completed goal state", AppSettings.Current.OpenChats.Single().Goal == chat.Goal);

        var closed = NewFake("closed");
        Send(closed, "/goal Pending when closed", 1);
        End(closed, "Early stop");
        var closedSession = Session(closed);
        closed.Close();
        var closing = Stopwatch.StartNew();
        Pump(() => closing.Elapsed > TimeSpan.FromMilliseconds(1400), TimeSpan.FromSeconds(3));
        Check("closing cancels a pending check", closedSession.Sent.Count == 1);
        Check("test history stays outside real application data", Environment.GetEnvironmentVariable("VIBECODE_DATA_DIR")!.StartsWith(_root));
    }

    private static ChatViewModel NewFake(string name)
    {
        var cwd = Path.Combine(_root, name);
        Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, provider: "codex") { ExcludeFromMemory = true };
        Field(chat, "_session", new FakeSession());
        chat.Status = "idle";
        Chats.Add(chat);
        return chat;
    }

    private static void Send(ChatViewModel chat, string text, int count)
    {
        Check("prompt accepted: " + text, chat.Send(text));
        WaitSent(chat, count);
    }

    private static void WaitSent(ChatViewModel chat, int count) => Pump(() => Session(chat).Sent.Count >= count && chat.Status == "running", TimeSpan.FromSeconds(12));
    private static void Quiet(ChatViewModel chat, int count, string name)
    {
        var elapsed = Stopwatch.StartNew();
        Pump(() => elapsed.Elapsed > TimeSpan.FromMilliseconds(1400), TimeSpan.FromSeconds(3));
        Check(name, Session(chat).Sent.Count == count);
    }

    private static TextItem End(ChatViewModel chat, string reply, bool failed = false)
    {
        var item = new TextItem { Text = reply };
        chat.Items.Add(item);
        Call(chat, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = failed, ["result"] = failed ? reply : "", ["subtype"] = failed ? "error" : "success" });
        return item;
    }

    private static void Pump(Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > timeout) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("Condition did not complete within " + timeout);
    }

    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Field(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static ICodingSession CodingSession(ChatViewModel chat) => (ICodingSession)typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(chat)!;
    private static FakeSession Session(ChatViewModel chat) => (FakeSession)CodingSession(chat);
    private static void Check(string name, bool passed)
    {
        if (!passed) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
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
        public void Start() { }
        public void SendUser(JsonNode content) => Sent.Add(content is JsonArray array
            ? string.Join("\n\n", array.Select(n => n?["text"]?.ToString()).Where(s => s is not null))
            : content.ToString());
        public Task InterruptAsync() { Interrupts++; return Task.CompletedTask; }
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
