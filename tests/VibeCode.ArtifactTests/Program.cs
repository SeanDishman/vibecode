using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeCode-ArtifactTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(root, "settings"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(root, "codex"));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        ChatViewModel? chat = null;
        Window? window = null;
        try
        {
            chat = new ChatViewModel(root, provider: "codex") { ExcludeFromMemory = true };
            var track = (Action<string>)typeof(ChatViewModel).GetMethod("TrackArtifact", Flags)!
                .CreateDelegate(typeof(Action<string>), chat);
            var list = new ListBox { ItemsSource = chat.Files, DataContext = chat };
            list.SetBinding(ListBox.SelectedItemProperty, new Binding(nameof(ChatViewModel.SelectedFile))
                { Mode = BindingMode.TwoWay });
            window = new Window { Content = list, Width = 400, Height = 240, Left = -32000, Top = -32000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            window.Show();
            Pump();

            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep me");
            track(Path.Combine(root, "keep.txt"));
            var first = chat.Files.Single();
            track("KEEP.txt");
            Check(chat.Files.Count == 1 && first.Writes == 2, "case and relative paths share one entry");
            Check(ReferenceEquals(chat.SelectedFile, first), "selection remains on the existing entry");
            chat.Files.Remove(first); // Rewind removes stale artifacts this way.
            track("keep.txt");
            Check(chat.Files.Count == 1 && chat.Files[0].Writes == 1 && !ReferenceEquals(chat.Files[0], first),
                "removed files can be tracked again");
            chat.ClearFiles();
            track("keep.txt");
            Check(chat.Files.Count == 1 && chat.Files[0].Writes == 1, "clear resets the path index");
            chat.Files[0] = new FileArtifact { Path = "replacement.txt", Writes = 1 };
            track("replacement.txt");
            Check(chat.Files.Count == 1 && chat.Files[0].Writes == 2, "replacement keeps the path index coherent");
            track(" ");
            Check(chat.Files.Count == 1, "blank paths do not enter the panel");

            chat.ClearFiles();
            var paths = new JsonArray();
            for (var i = 0; i < 20_000; i++) paths.Add(Path.Combine(root, $"file-{i}.cs"));
            var ingest = (Action<JsonNode>)typeof(ChatViewModel).GetMethod("IngestSdk", Flags)!
                .CreateDelegate(typeof(Action<JsonNode>), chat);
            var timer = Stopwatch.StartNew();
            ingest(new JsonObject { ["type"] = "system", ["subtype"] = "artifact_update", ["paths"] = paths });
            Check(chat.Files.Count <= 1_000, "large artifact payload leaves a bounded preview panel");
            Check(chat.Files[0].Path.EndsWith("file-19999.cs"), "newest paths remain visible");
            Check(chat.SelectedFile is not null && chat.Files.Contains(chat.SelectedFile), "eviction leaves a valid selection");
            Pump();
            Check(timer.Elapsed < TimeSpan.FromSeconds(5), "20,000 file updates and layout complete in under five seconds");
            Console.WriteLine($"20,000 bound file updates + layout: {timer.ElapsedMilliseconds} ms");
            Check(File.ReadAllText(Path.Combine(root, "keep.txt")) == "keep me", "panel eviction never deletes files");
            VerifyWatcher(root);
            Console.WriteLine($"PASS: {_checks} artifact regression checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window?.Close(); chat?.Close(); app.Shutdown(); }
    }

    private static void VerifyWatcher(string root)
    {
        using var session = new CodexSession(new CodexSessionOptions { Cwd = root });
        var shouldTrack = (Func<string, bool>)typeof(CodexSession).GetMethod("ShouldTrackArtifact", Flags)!
            .CreateDelegate(typeof(Func<string, bool>), session);
        foreach (var directory in new[] { "bin", "OBJ", "bin-test", "obj.release", "node_modules", ".venv",
                     ".tmp-test", ".diagnostics", "publish", "packages", "TestResults" })
            Check(!shouldTrack(Path.Combine(root, "project", directory, "generated.cs")), "ignores " + directory);
        foreach (var file in new[] { "src/main.cs", ".gitignore", ".github/workflows/build.yml", "src/binocular.cs", "docs/package.md" })
            Check(shouldTrack(Path.GetFullPath(Path.Combine(root, file))), "tracks source file " + file);
        Check(!shouldTrack(Path.GetFullPath(Path.Combine(root, "../outside.cs"))), "ignores paths outside workspace");
        Check(!shouldTrack(Path.Combine(root, ".vibecode-bridge.md")), "ignores bridge bookkeeping");

        var changed = (Action<object, FileSystemEventArgs>)typeof(CodexSession).GetMethod("OnArtifactFileChanged", Flags)!
            .CreateDelegate(typeof(Action<object, FileSystemEventArgs>), session);
        var captured = (HashSet<string>)typeof(CodexSession).GetField("_turnCommandFilePaths", Flags)!.GetValue(session)!;
        changed(session, new FileSystemEventArgs(WatcherChangeTypes.Changed, root, "idle.cs"));
        Check(captured.Count == 0, "idle watcher changes are not captured");
        typeof(CodexSession).GetMethod("BeginCommandCapture", Flags)!.Invoke(session, ["command-1"]);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
            changed(session, new FileSystemEventArgs(WatcherChangeTypes.Changed, root, $"source-{i}.cs"));
        Check(captured.Count is > 0 and <= 1_000, "100,000 watcher events produce a bounded update");
        Console.WriteLine($"100,000 watcher events: {timer.ElapsedMilliseconds} ms, {captured.Count} retained paths");
        var retained = captured.Count;
        changed(session, new FileSystemEventArgs(WatcherChangeTypes.Changed, root, "source-0.cs"));
        Check(captured.Count == retained, "duplicate notifications do not grow the batch");
        typeof(CodexSession).GetMethod("BeginRootTurn", Flags)!.Invoke(session, [null]);
        Check(captured.Count == 0, "new turns reset the watcher budget");
        typeof(CodexSession).GetMethod("BeginCommandCapture", Flags)!.Invoke(session, ["command-2"]);
        changed(session, new FileSystemEventArgs(WatcherChangeTypes.Changed, root, "next-turn.cs"));
        Check(captured.Count == 1, "next turn captures files after a saturated turn");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var idle = false;
        var marker = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => { idle = true; frame.Continue = false; }));
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var deadline = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
            new Action(() => frame.Continue = false)), null, 2000, Timeout.Infinite);
        Dispatcher.PushFrame(frame);
        marker.Abort();
        Check(idle, "dispatcher reaches idle after artifact layout");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
