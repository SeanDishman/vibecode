using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<ChatViewModel> Chats = [];
    private static int Checks;
    private static string Root = "";

    [STAThread]
    private static int Main()
    {
        Root = Path.Combine(Environment.CurrentDirectory, "artifacts", "bridge-usage",
            "checks-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
        Directory.CreateDirectory(Root);
        foreach (var (name, folder) in new[]
        {
            ("VIBECODE_DATA_DIR", "data"), ("VIBECODE_CLAUDE_ACCOUNT_STORE", "claude-accounts"),
            ("CLAUDE_CONFIG_DIR", "claude-home"), ("VIBECODE_CODEX_ACCOUNT_STORE", "codex-accounts"),
            ("VIBECODE_CODEX_LEGACY_HOME", "codex-home"), ("KIMI_CODE_HOME", "kimi-home"),
            ("KIMI_SHARE_DIR", "kimi-share"),
        }) Environment.SetEnvironmentVariable(name, Path.Combine(Root, folder));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        AppSettings.Current.AgentMemoryEnabled = AppSettings.Current.AgentSwarmsEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;

        AppSettings.Current.TelemetryLiveAnimation = false;
        try
        {
            VerifyLiveViewsAndCompletion();
            VerifyActualModelAndUnfinishedUsage();
            VerifyHudLifecycle();
            VerifyKimiInterruptedBaseline();
            PumpUntil(() => File.Exists(UsageLog.FilePath)
                && File.ReadLines(UsageLog.FilePath).Count() == UsageLog.Instance.Count);
            Check("every completed or interrupted usage row reaches durable history", File.ReadLines(UsageLog.FilePath).Count() == UsageLog.Instance.Count);
            Console.WriteLine($"PASS: {Checks} Bridge usage checks. No provider sessions or model generations. Evidence: {Root}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException : ex); return 1; }
        finally
        {
            foreach (var chat in Chats) chat.Close();
            app.Shutdown();
        }
    }

    private static ChatViewModel Fable(string name, string? role = null, string selectedModel = "claude-fable-5-1")
    {
        var chat = new ChatViewModel(Root, resume: Guid.NewGuid().ToString(), accountId: "test-claude", provider: "claude")
        {
            Model = selectedModel, ExcludeFromMemory = true, BridgeLabel = name, Status = "running",
        };
        chat.IsBridgeManager = role == "orchestrator";
        Chats.Add(chat);
        Call(chat, "BeginTurnPricing");
        return chat;
    }

    private static JsonObject Usage(int multiplier = 1) => new()
    {
        ["input_tokens"] = 200 * multiplier, ["cache_creation_input_tokens"] = 30 * multiplier,
        ["cache_read_input_tokens"] = 20 * multiplier, ["output_tokens"] = 10 * multiplier,
    };

    private static void Feed(ChatViewModel chat, string id = "msg-1") => Call(chat, "CaptureClaudeAssistantUsage",
        new JsonObject { ["id"] = id, ["model"] = "claude-fable-5-1[1m]", ["usage"] = Usage() }, true);

    private static string Text(FrameworkElement view, string name) => ((TextBlock)view.FindName(name)).Text;
    private static UsageRow[] Rows(SettingsWindow settings) => ((System.Collections.IEnumerable)((ItemsControl)settings.FindName("UsageTable")).ItemsSource).Cast<UsageRow>().ToArray();

    private static void VerifyLiveViewsAndCompletion()
    {
        using var watch = LiveTurnTelemetry.Instance.Watch();
        UsageLog.Instance.Record("codex", "gpt-6-luna", 100, 0, 0, 10, 0.002, false, "seed", Root);
        var team = new[] { Fable("Claude 1"), Fable("Claude 2", "orchestrator"), Fable("Claude 3", "worker") };
        foreach (var chat in team) { Feed(chat); Feed(chat); }
        Check("regular and Advanced Bridge live usage includes each agent once",
            LiveTurnTelemetry.Instance.Merge(UsageLog.Instance.Entries()).Sum(e => e.Total) == 890);
        var settings = new SettingsWindow();
        typeof(SettingsWindow).GetField("_ready", Hidden)!.SetValue(settings, true);
        ((RadioButton)settings.FindName("RangeToday")).IsChecked = true;
        ((FrameworkElement)settings.FindName("PaneUsage")).Visibility = Visibility.Visible;
        Call(settings, "RefreshUsage");
        var hud = new UsageHudWindow();
        try
        {
            Check("Today's actual Settings table shows Fable 5.1 while Bridge turns are running", Rows(settings).Any(r => r.Display == "Fable 5.1"));
            Check("Today's Settings totals include live cache tiers and every Bridge role",
                Text(settings, "UsageTileIn") == UsageAnalytics.Tokens(850) && Text(settings, "UsageTileOut") == UsageAnalytics.Tokens(40));
            Call(hud, "Refresh");
            Check("compact telemetry counts the same live Bridge tokens", Text(hud, "TileTokens") == UsageAnalytics.Tokens(890));
            Feed(team[0], "msg-2");
            PumpUntil(() => Text(settings, "UsageTileIn") == UsageAnalytics.Tokens(1100));
            Check("open Settings usage refreshes while a Bridge turn continues", Text(settings, "UsageTileOut") == UsageAnalytics.Tokens(50));
            Call(hud, "Refresh");
            Check("compact telemetry matches the updated live total", Text(hud, "TileTokens") == UsageAnalytics.Tokens(1150));

            for (var i = 0; i < team.Length; i++) Call(team[i], "ApplyResult", new JsonObject { ["usage"] = Usage(i == 0 ? 2 : 1), ["total_cost_usd"] = 0 });
            PumpUntil(() => UsageLog.Instance.Count == 4 && Rows(settings).Any(r => r.Display == "Fable 5.1" && r.Turns == "3"));
            Check("completed regular and Advanced Bridge turns retain Fable's actual model",
                UsageLog.Instance.Entries().Count(e => e.Model == "claude-fable-5-1") == 3);
            Check("committed turns replace live usage without double-counting",
                UsageLog.Instance.Entries().Sum(e => e.Total) == 1150 && LiveTurnTelemetry.Instance.Merge(UsageLog.Instance.Entries()).Sum(e => e.Total) == 1150);
            Check("live rows are removed after Bridge completion", LiveTurnTelemetry.Instance.StreamingCount == 0);
            Check("Today's report retains Fable 5.1 after completion", UsageAnalytics.Build(UsageLog.Instance.Entries(), UsageWindow.Today).Models.Single(m => m.Model == "claude-fable-5-1").Total == 1040);
            Check("CSV history includes every Bridge agent", UsageLog.Instance.ExportCsv(Path.Combine(Root, "usage.csv")) == 4 && File.ReadAllText(Path.Combine(Root, "usage.csv")).Contains("claude-fable-5-1"));
        }
        finally { settings.Close(); hud.Close(); }
    }

    private static void VerifyActualModelAndUnfinishedUsage()
    {
        using var watch = LiveTurnTelemetry.Instance.Watch();
        var actual = Fable("Claude actual-model", selectedModel: "claude-opus-5-5");
        Feed(actual);
        Check("live usage follows the model reported by the provider", LiveTurnTelemetry.Instance.Merge([]).Single().Model == "claude-fable-5-1");
        Call(actual, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        Check("completed usage follows the provider's actual Fable model", UsageLog.Instance.Entries().Last().Model == "claude-fable-5-1");

        var closing = Fable("Claude closing", "worker"); Feed(closing);
        var before = UsageLog.Instance.Count;
        closing.Close(); closing.Close();
        Check("closing an unfinished Bridge preserves consumed usage exactly once", UsageLog.Instance.Count == before + 1 && UsageLog.Instance.Entries().Last().Total == 260);
        Call(closing, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        Check("a late completion cannot duplicate already-closed Bridge usage", UsageLog.Instance.Count == before + 1);
        Check("closed Bridge usage is no longer a live duplicate", LiveTurnTelemetry.Instance.StreamingCount == 0);

        var failing = Fable("Claude error", "orchestrator"); Feed(failing);
        before = UsageLog.Instance.Count;
        Call(failing, "ApplyResult", new JsonObject { ["is_error"] = true, ["result"] = "Fixture provider exited", ["usage"] = new JsonObject() });
        Check("a provider error without final usage retains observed Bridge consumption", UsageLog.Instance.Count == before + 1 && UsageLog.Instance.Entries().Last().Total == 260);
        Check("failed Bridge usage is counted once", LiveTurnTelemetry.Instance.StreamingCount == 0);
        Call(failing, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        Check("a late final report cannot duplicate preserved error usage", UsageLog.Instance.Count == before + 1);
        Call(failing, "IngestSdk", new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject
        {
            ["id"] = "late-message", ["role"] = "assistant", ["model"] = "claude-fable-5-1",
            ["usage"] = Usage(), ["content"] = new JsonArray(),
        } });
        Check("queued SDK messages cannot resurrect already-preserved live usage", LiveTurnTelemetry.Instance.StreamingCount == 0);
        Call(failing, "BeginTurnPricing"); failing.Status = "running"; Feed(failing, "msg-retry");
        Call(failing, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        Check("new Bridge work is still counted after recovering from an error", UsageLog.Instance.Count == before + 2);

        Feed(failing, "before-compact");
        Call(failing, "ApplyResult", new JsonObject { ["is_error"] = true, ["result"] = "Fixture interruption" });
        var compactSession = new FixtureSession();
        typeof(ChatViewModel).GetField("_session", Hidden)!.SetValue(failing, compactSession);
        before = UsageLog.Instance.Count;
        Check("native compaction can start after preserving an interrupted Bridge", failing.Send("/compact") && compactSession.Compactions == 1);
        Feed(failing, "compact-message");
        Call(failing, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        Check("native compaction records fresh usage and settles normally after an interrupted turn",
            UsageLog.Instance.Count == before + 1 && failing.Status == "idle" && !failing.IsCompactingContext);

        var zero = Fable("Claude zero"); Feed(zero);
        before = UsageLog.Instance.Count;
        Call(zero, "ApplyResult", new JsonObject { ["usage"] = Usage(0) }); zero.Close();
        Check("an explicit final zero corrects provisional usage without inventing a record", UsageLog.Instance.Count == before);
        var empty = Fable("Claude empty"); before = UsageLog.Instance.Count; empty.Close();
        Check("closing a Bridge before any usage creates no record", UsageLog.Instance.Count == before);
    }

    private static void VerifyHudLifecycle()
    {
        Check("closing usage views releases their live telemetry watchers", !LiveTurnTelemetry.Instance.IsWatched);
        var chat = Fable("Claude HUD-only"); Feed(chat);
        var hud = new UsageHudWindow();
        Call(hud, "OnLoaded", hud, new RoutedEventArgs());
        try
        {
            PumpUntil(() => LiveTurnTelemetry.Instance.StreamingCount == 1
                && Text(hud, "TileTokens") == UsageAnalytics.Tokens(UsageLog.Instance.Entries().Sum(e => e.Total) + 260));
            Check("opening compact telemetry alone picks up an already-running Bridge", LiveTurnTelemetry.Instance.IsWatched);
            Call(chat, "ApplyResult", new JsonObject { ["usage"] = Usage() });
        }
        finally { hud.Close(); }
        Check("closing compact telemetry releases its watcher", !LiveTurnTelemetry.Instance.IsWatched);
    }

    private static void VerifyKimiInterruptedBaseline()
    {
        var chat = new ChatViewModel(Root, resume: Guid.NewGuid().ToString(), provider: "kimi")
        {
            Model = "kimi-k2.7-code", Status = "running", BridgeLabel = "Kimi worker", ExcludeFromMemory = true,
        };
        Chats.Add(chat); Call(chat, "BeginTurnPricing");
        var before = UsageLog.Instance.Entries().Sum(e => e.Total);
        Call(chat, "ApplySystem", new JsonObject { ["subtype"] = "usage_update", ["usage"] = Usage() }, null);
        Call(chat, "ApplyResult", new JsonObject { ["is_error"] = true, ["result"] = "Fixture interruption" });
        Call(chat, "BeginTurnPricing"); chat.Status = "running";
        Call(chat, "ApplyResult", new JsonObject { ["usage"] = Usage(), ["session_usage"] = Usage(2) });
        Check("Kimi's next session snapshot excludes usage already saved after interruption",
            UsageLog.Instance.Entries().Sum(e => e.Total) - before == 520);
    }

    private sealed class FixtureSession : ICodingSession, ICompactableSession
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
        public string? SessionId => "usage-fixture";
        public bool HasExited { get; private set; }
        public int Compactions;
        public void Start() { }
        public void SendUser(JsonNode content) { }
        public Task CompactAsync(string? instructions = null) { Compactions++; return Task.CompletedTask; }
        public Task InterruptAsync() => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }

    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    private static void Check(string name, bool okay) { if (!okay) throw new InvalidOperationException("FAIL: " + name); Checks++; Console.WriteLine("PASS: " + name); }
    private static void PumpUntil(Func<bool> done)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Usage view did not refresh.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
