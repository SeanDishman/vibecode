using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<ChatViewModel> Chats = [];
    private static string Root = "";
    private static int Checks;

    [STAThread]
    private static int Main()
    {
        Root = Path.Combine(Environment.CurrentDirectory, "artifacts", "compact-recovery", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(Root, "settings"));
        Environment.SetEnvironmentVariable("KIMI_CODE_HOME", Path.Combine(Root, "empty-kimi"));
        Environment.SetEnvironmentVariable("KIMI_SHARE_DIR", Path.Combine(Root, "empty-kimi-share"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(Root, "empty-codex"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        Field(UsageService.Instance, "_lastFetch", DateTime.UtcNow);
        try
        {
            PolicyChecks();
            NativeCompactionChecks();
            ChatChecks();
            ClaudeLimitQueueChecks();
            BridgeBookkeepingChecks();
            RecoveryLifecycleChecks();
            ProviderAllowanceChecks();
            CompactionGuardChecks();
            SettingsCheck();
            SubagentOrbChecks();
            AdvancedSetupChecks();
            Console.WriteLine($"PASS: {Checks} compact/recovery checks. Artifacts: {Root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { foreach (var chat in Chats) chat.Close(); app.Shutdown(); }
    }

    private static void PolicyChecks()
    {
        foreach (var text in new[] { "You've hit your limit · resets 3am", "You've reached your 5-hour usage limit.",
            "You've reached your weekly (7-day) usage limit", "You've reached your monthly usage limit for this billing cycle.",
            "HTTP 429", "rate_limit_error", "usageLimitExceeded", "insufficient_quota", "Too many requests" })
            Check("recognizes provider quota: " + text, UsageLimitRecovery.IsLimitError(Error(text)));
        // Claude Code 2.1.286 wording, taken from its limit-message builder and from real Bridge transcripts.
        foreach (var text in new[] { "You've hit your session limit · resets 5:40pm (America/Chicago)",
            "You've hit your weekly limit · resets Oct 6, 3pm (America/Chicago)",
            "You've hit your Opus limit · resets Oct 6, 3pm (America/Chicago) · progress saved",
            "You've hit your Sonnet limit", "You've hit your Fable limit", "You've hit your usage credit limit",
            "You've hit your org's monthly spend limit · ask your admin to raise it at claude.ai/admin-settings/usage",
            "You've hit your team's shared budget · raise it at claude.ai/admin-settings/usage",
            "You're out of usage credits · resets 5:40pm (America/Chicago)" })
            Check("recognizes Claude Code limit wording: " + text, UsageLimitRecovery.IsLimitError(Error(text)));
        var claudeEnvelope = Error("You've hit your session limit · resets 5:40pm (America/Chicago)");
        claudeEnvelope["subtype"] = "success";
        Check("recognizes Claude's is_error result with subtype success", UsageLimitRecovery.IsLimitError(claudeEnvelope));
        foreach (var text in new[] { "401 Unauthorized", "context length exceeded", "context window limit reached", "Unknown model", "Permission denied", "500 Internal server error",
            "Prompt is too long", "You've hit your context limit" })
            Check("does not auto-recover unrelated error: " + text, !UsageLimitRecovery.IsLimitError(Error(text)));
        var beforeReset = new DateTimeOffset(DateTime.Today.AddHours(17).AddMinutes(31));
        var claudeReset = new DateTimeOffset(DateTime.Today.AddHours(17).AddMinutes(40));
        Check("Claude session reset time is parsed", UsageLimitRecovery.RetryAt(claudeEnvelope, beforeReset) == claudeReset);
        Check("Claude reset time is parsed before a progress-saved suffix",
            UsageLimitRecovery.RetryAt(Error("You've hit your session limit · resets 5:40pm (America/Chicago) · progress saved"), beforeReset) == claudeReset);
        var streamedReset = Error("You've hit your session limit · resets 5:40pm (America/Chicago)");
        streamedReset["resetsAt"] = 1791153600;
        Check("streamed unix reset outranks the printed minute", UsageLimitRecovery.RetryAt(streamedReset,
            DateTimeOffset.FromUnixTimeSeconds(1791153000)) == DateTimeOffset.FromUnixTimeSeconds(1791153600));
        var now = DateTimeOffset.Now;
        Check("unknown usage is inconclusive", UsageRecoverySnapshot.Unknown.Available is null);
        var blocked = UsageRecoverySnapshot.FromWindows([(100, now.AddHours(1)), (100, now.AddDays(2))]);
        Check("waits for all exhausted windows", blocked.Available == false && blocked.ResetsAt == now.AddDays(2));
        Check("empty reading never grants allowance", UsageRecoverySnapshot.FromWindows([]).Available is null);
        Check("Retry-After delay is honored", UsageLimitRecovery.RetryAt(Error("Try again in 15 minutes"), now) == now.AddMinutes(15));
        var structured = Error("Not available");
        structured["provider_error"] = new JsonObject { ["status"] = 429, ["retry_after_seconds"] = 42 };
        Check("structured 429 and retry delay are recognized by recovery policy", UsageLimitRecovery.IsLimitError(structured)
            && UsageLimitRecovery.RetryAt(structured, now) == now.AddSeconds(42));
        var buckets = JsonNode.Parse("""{"rateLimits":{"limitId":"codex","primary":{"usedPercent":100,"resetsAt":2000000000}},"rateLimitsByLimitId":{"base_model_inference":{"primary":{"usedPercent":25}}}}""");
        Check("Codex primary pool stays exhausted", UsageLimitRecovery.CodexSnapshot(buckets, "gpt-6-astra").Available == false);
        Check("Codex reserve uses its own pool", UsageLimitRecovery.CodexSnapshot(buckets, "gpt-reserve").Available == true);
        Check("Codex missing pool is unknown", UsageLimitRecovery.CodexSnapshot(new JsonObject(), "gpt-6-astra").Available is null);
        var onHour = UsageLimitRecovery.ParseReset("Oct 4, 4pm (America/Chicago)", new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-5)));
        Check("Claude on-the-hour reset is parsed as a time", onHour?.Year == 2026 && onHour?.Hour == 16);
    }

    private static void NativeCompactionChecks()
    {
        using var codex = new CodexSession(new CodexSessionOptions { Cwd = Root });
        Field(codex, "_launchPending", true);
        Property(codex, "SessionId", "test-thread");
        string? method = null;
        JsonObject? payload = null;
        Property(codex, "RequestFixture", new Func<string, JsonObject?, Task<JsonNode?>>((m, p) =>
        { method = m; payload = p; return Task.FromResult<JsonNode?>(new JsonObject { ["result"] = new JsonObject() }); }));
        codex.CompactAsync().GetAwaiter().GetResult();
        Check("Codex sends native thread compaction instead of turn/start", method == "thread/compact/start" && payload?["threadId"]?.ToString() == "test-thread");
        var compactBoundaries = 0;
        codex.MessageReceived += node => { if (node["subtype"]?.ToString() == "compact_boundary") compactBoundaries++; };
        var compactItem = new JsonObject { ["id"] = "compact", ["type"] = "contextCompaction" };
        Call(codex, "TranslateStarted", compactItem, null);
        Check("Codex does not report compaction success before completion", compactBoundaries == 0);
        Call(codex, "TranslateCompleted", compactItem, null);
        Check("Codex completed compaction produces the history boundary", compactBoundaries == 1);

        using var claude = new ClaudeSession(new ClaudeSessionOptions { Cwd = Root });
        // No child is launched. Only the serializer's process-alive guard needs a live process handle.
        Field(claude, "_proc", Process.GetCurrentProcess());
        try { claude.CompactAsync("Keep the current task").GetAwaiter().GetResult(); }
        finally { Field(claude, "_proc", null); }
        var channel = (Channel<string>)typeof(ClaudeSession).GetField("_writeQueue", Flags)!.GetValue(claude)!;
        Check("Claude sends raw slash command with no app prelude", channel.Reader.TryRead(out var line)
            && JsonNode.Parse(line)?["message"]?["content"]?.ToString() == "/compact Keep the current task");
    }

    private static void ChatChecks()
    {
        AppSettings.Current.ContinueAfterLimitResets = true;
        foreach (var provider in new[] { "claude", "codex", "kimi", "grok", "glm" })
        foreach (var role in new[] { "chat", "bridge", "advanced-worker", "advanced-orchestrator" })
        {
            var (chat, session) = NewChat(provider + "-" + role, provider);
            if (role != "chat") chat.BridgeLabel = "Worker 2";
            if (role.StartsWith("advanced-"))
            {
                Field(chat, "_bridgeSessionInitialized", true);
                Call(chat, "ApplyBridgeConfiguration", new BridgeAgentConfiguration(provider, null, null));
            }
            if (role == "advanced-orchestrator")
            {
                chat.IsBridgeManager = true;
                Property(chat, "BridgeCoordinatesOnly", true);
            }
            SetProbe(chat, () => Task.FromResult(new UsageRecoverySnapshot(false, DateTimeOffset.Now.AddHours(1))));
            End(chat, Error("You've reached your usage limit"));
            Check(provider + " " + role + " schedules recovery", chat.WaitingForLimitReset);
            CheckRecovery(chat);
            Check(provider + " " + role + " stays parked while exhausted", session.Sent.Count == 0);
            Field(chat, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
            SetProbe(chat, () => Task.FromResult(new UsageRecoverySnapshot(true)));
            CheckRecovery(chat);
            Pump(() => session.Sent.Count == 1);
            CheckRecovery(chat);
            Check(provider + " " + role + " continues once with existing context", session.Sent.Count == 1 && session.Sent[0].Contains("Continue the interrupted task"));
        }

        var (disabled, ds) = NewChat("disabled");
        AppSettings.Current.ContinueAfterLimitResets = false;
        End(disabled, Error("rate limit"));
        SetProbe(disabled, () => Task.FromResult(new UsageRecoverySnapshot(true)));
        CheckRecovery(disabled);
        Check("setting off sends no continuation", !disabled.WaitingForLimitReset && ds.Sent.Count == 0);
        AppSettings.Current.ContinueAfterLimitResets = true;
        var (cancelled, cs) = NewChat("cancelled");
        End(cancelled, Error("rate limit"));
        cancelled.Interrupt();
        CheckRecovery(cancelled);
        Check("Stop cancels waiting recovery", !cancelled.WaitingForLimitReset && cs.Sent.Count == 0);

        var (racing, rs) = NewChat("manual-race");
        var completion = new TaskCompletionSource<UsageRecoverySnapshot>();
        SetProbe(racing, () => completion.Task);
        End(racing, Error("rate limit"));
        var checking = (Task)Call(racing, "CheckLimitRecoveryAsync")!;
        Check("new prompt accepted while quota check is pending", racing.Send("Use my revised instructions."));
        completion.SetResult(new UsageRecoverySnapshot(true));
        Pump(() => checking.IsCompleted && rs.Sent.Count == 1);
        Check("late quota reading cannot send a second prompt", !racing.WaitingForLimitReset && rs.Sent.Count == 1 && rs.Sent[0].Contains("revised instructions"));

        var (unknown, us) = NewChat("unknown-usage");
        End(unknown, Error("rate limit"));
        SetProbe(unknown, () => Task.FromResult(UsageRecoverySnapshot.Unknown));
        CheckRecovery(unknown);
        Check("unknown quota does not immediately retry", us.Sent.Count == 0);
        Field(unknown, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
        Field(unknown, "_limitRecoveryRetryAt", DateTimeOffset.MinValue);
        CheckRecovery(unknown);
        Pump(() => us.Sent.Count == 1);
        Check("unknown fixture resumes after its fallback deadline", us.Sent.Count == 1 && !unknown.WaitingForLimitReset);

        var (hinted, hs) = NewChat("retry-after");
        End(hinted, Error("rate limit; retry after 120 seconds"));
        Field(hinted, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
        SetProbe(hinted, () => Task.FromResult(new UsageRecoverySnapshot(true)));
        CheckRecovery(hinted);
        Check("Retry-After is honored even when other allowance is available", hs.Sent.Count == 0);
        hinted.Close();
        CheckRecovery(hinted);
        Check("closed chat cannot resume", !hinted.WaitingForLimitReset && hs.Sent.Count == 0);

        var (compactRetry, crs) = NewChat("compact-limit");
        Check("compact starts before quota failure", compactRetry.Send("/compact"));
        Check("prompt queues behind compact", compactRetry.Send("Next task"));
        End(compactRetry, Error("rate limit"));
        Field(compactRetry, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
        SetProbe(compactRetry, () => Task.FromResult(new UsageRecoverySnapshot(true)));
        CheckRecovery(compactRetry);
        Check("quota recovery retries compact before the queued task", crs.Compactions == 2 && crs.Sent.Count == 0 && compactRetry.HasQueued);
        End(compactRetry, new JsonObject { ["type"] = "result", ["is_error"] = false, ["result"] = "Not enough messages to compact." });
        Pump(() => crs.Sent.Count == 1);
        Check("queued work resumes after compact and displays provider explanation", crs.Sent[0].Contains("Next task")
            && compactRetry.Items.OfType<BannerItem>().Any(b => b.Text == "Not enough messages to compact."));

        foreach (var provider in new[] { "claude", "codex" })
        {
            var (chat, session) = NewChat("compact-" + provider, provider);
            chat.Prelude = "Retain this bridge assignment for the next model turn.";
            Check(provider + " exposes compact", chat.Commands.Any(c => c.Name == "compact"));
            Check(provider + " accepts compact", chat.Send("/compact"));
            Check(provider + " uses native interface without model prompt", session.Compactions == 1 && session.Sent.Count == 0 && chat.Prelude is not null);
            Check(provider + " refuses concurrent compaction", !chat.Send("/compact") && session.Compactions == 1);
            End(chat, new JsonObject { ["type"] = "result", ["is_error"] = false });
            Check(provider + " finishes compact normally", chat.Status == "idle" && !chat.WaitingForLimitReset);
        }
        var (queued, qs) = NewChat("extended-queue");
        queued.SetExtendedQueueEnabled(true);
        Check("extended prompt accepted", queued.Send("First queued task"));
        Pump(() => qs.Sent.Count == 1);
        End(queued, Error("rate limit"));
        Check("failed extended head retained", queued.ExtendedQueuePausedForUsage && queued.HasQueued);
        Field(queued, "_limitRecoveryEarliestResume", DateTimeOffset.MinValue);
        SetProbe(queued, () => Task.FromResult(new UsageRecoverySnapshot(true)));
        CheckRecovery(queued);
        Pump(() => qs.Sent.Count == 2);
        Check("extended recovery retries preserved prompt, without extra nudge", qs.Sent[1].Contains("First queued task") && !qs.Sent[1].Contains("Continue the interrupted task"));
    }

    private static void BridgeBookkeepingChecks()
    {
        var (worker, _) = NewChat("bridge-ledger");
        worker.BridgeLabel = "Worker 2";
        var task = new BridgeWorkTask { OwnerId = worker.BridgeAgentId, Title = "Pending implementation", State = "running" };
        var work = new BridgeWorkState { Tasks = [task] };
        Property(worker, "BridgeWork", work);
        var vm = new MainViewModel();
        var live = Activator.CreateInstance(typeof(MainViewModel).GetNestedType("LiveBridge", Flags)!, nonPublic: true)!;
        Property(live, "Panes", new BridgePaneCollection { worker });
        Property(live, "Errored", new HashSet<ChatViewModel>());
        Property(live, "Peers", new PeerTrafficLedger());
        ((System.Collections.IDictionary)typeof(MainViewModel).GetField("_parkedBridges", Flags)!.GetValue(vm)!)[worker] = live;
        worker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChatViewModel.Status)) return;
            Call(vm, "OnBridgePaneStatusChanged", worker);
            Call(vm, "OnBridgeManagerStatusChanged", worker);
        };
        worker.Status = "running";
        End(worker, Error("usage limit"));
        Check("Advanced Bridge retains assignment while waiting for quota", task.State == "running" && worker.BridgeTaskState == "waiting");
        Check("quota does not announce the bridge lane as unowned", ((HashSet<ChatViewModel>)live.GetType().GetProperty("Errored")!.GetValue(live)!).Count == 0);
        worker.Interrupt();
        worker.Status = "idle";
        task.State = "running";
        worker.Items.Add(new TextItem { Text = "Old answer that must not finish the new task." });
        Check("bridge compaction accepted", worker.Send("/compact"));
        End(worker, new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("compaction cannot complete an Advanced Bridge task using an old answer", task.State == "running");
    }

    private static void SettingsCheck()
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        app.Resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        app.Resources["BoolVis"] = new BooleanToVisibilityConverter();
        var window = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowActivated = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var toggle = (ToggleButton)window.FindName("ContinueAfterLimitResetsToggle");
            Check("global setting is loaded and keyboard focusable", toggle.IsChecked == true && toggle.Focusable);
            toggle.IsChecked = false;
            Check("global setting persists off", !AppSettings.Current.ContinueAfterLimitResets && SavedRecoveryPreference() == false);
            toggle.IsChecked = true;
            Check("global setting persists on", AppSettings.Current.ContinueAfterLimitResets && SavedRecoveryPreference() == true);
            var reloaded = (AppSettings)typeof(AppSettings).GetMethod("Load", Flags)!.Invoke(null, null)!;
            Check("global recovery preference survives settings reload", reloaded.ContinueAfterLimitResets);
            var panel = (FrameworkElement)window.FindName("PaneGeneral");
            var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(Root, "settings-general.png")); encoder.Save(output);
        }
        finally { window.Close(); }
    }

    private static bool? SavedRecoveryPreference() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))?
        [nameof(AppSettings.ContinueAfterLimitResets)]?.GetValue<bool>();

    private static (ChatViewModel Chat, FakeSession Session) NewChat(string name, string provider = "codex")
    {
        var cwd = Path.Combine(Root, name); Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, provider: provider) { ExcludeFromMemory = true };
        var session = new FakeSession(); Field(chat, "_session", session); chat.Status = "idle";
        Chats.Add(chat); return (chat, session);
    }
    private static JsonObject Error(string text) => new() { ["type"] = "result", ["is_error"] = true, ["result"] = text };
    private static void End(ChatViewModel chat, JsonObject result) => Call(chat, "ApplyResult", result);
    private static void SetProbe(ChatViewModel chat, Func<Task<UsageRecoverySnapshot>> probe) => Property(chat, "RecoveryUsageFixture", probe);
    private static void CheckRecovery(ChatViewModel chat) { var task = (Task)Call(chat, "CheckLimitRecoveryAsync")!; Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Check(string message, bool condition) { if (!condition) throw new Exception("FAIL: " + message); Checks++; Console.WriteLine("PASS: " + message); }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static void Field(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static void Property(object target, string name, object? value) => target.GetType().GetProperty(name, Flags)!.SetValue(target, value);
    private static void Pump(Func<bool> done)
    {
        if (done()) return;
        var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("Fixture did not complete.");
    }
    private sealed class FakeSession : ICodingSession, ICompactableSession
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
        public string? SessionId => "fixture-session";
        public bool HasExited { get; private set; }
        public List<string> Sent { get; } = [];
        public List<string?> CompactInstructions { get; } = [];
        public Exception? CompactError;
        public int Compactions;
        public void Start() { }
        public void SendUser(JsonNode content) => Sent.Add(content is JsonArray a ? string.Join("\n", a.Select(n => n?["text"]?.ToString())) : content.ToString());
        public Task CompactAsync(string? instructions = null)
        {
            Compactions++; CompactInstructions.Add(instructions);
            return CompactError is null ? Task.CompletedTask : Task.FromException(CompactError);
        }
        public Task InterruptAsync() => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
