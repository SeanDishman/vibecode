using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

/// <summary>
/// Back-to-back thinking blocks show as ONE "Thought process" row. Feeds the real ingest code the event shapes Claude
/// Code emits - streamed live, and replayed from a saved transcript - offline, then renders a transcript to look at.
/// </summary>
internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<ChatViewModel> Chats = [];
    private static readonly string Pictures = Path.Combine(Path.GetTempPath(), "vibecode-thinking-rows");
    private static int _checks, _ids;
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "vibecode-thinking-row-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Pictures);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, "settings"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        try
        {
            StreamedBlocksShareARow();
            OneFinalMessageForBothBlocks();
            ReplayedHistorySharesARow();
            VisibleRowsKeepThoughtsApart();
            HiddenToolCallsDoNotSplitARow();
            RenderedTranscript();
            Console.WriteLine($"PASS: {_checks} thinking row checks. No live model calls.");
            Console.WriteLine("Rendered transcript: " + Pictures);
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
            foreach (var chat in Chats.Where(chat => chat.Status != "closed")) chat.Close();
            app.Shutdown();
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(_root).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(_root).StartsWith("vibecode-thinking-row-tests-", StringComparison.Ordinal))
                Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Claude Code's live order: each block streams, then its finished copy arrives as its own message.</summary>
    private static void StreamedBlocksShareARow()
    {
        var chat = Chat("streamed");
        Ingest(chat, MessageStart("m1"));
        Ingest(chat, BlockStart(0, Thinking("")));
        Ingest(chat, ThinkingDelta(0, "Check the firewall first."));
        Ingest(chat, BlockStop(0));
        Ingest(chat, Assistant("m1", Thinking("Check the firewall first.")));
        Ingest(chat, BlockStart(1, Thinking("")));
        Ingest(chat, ThinkingDelta(1, "Then reload it"));
        var row = Rows<ThinkingItem>(chat).Single();
        Check("live: the second thinking block joins the first row instead of adding one", chat.Items.Count == 1);
        Check("live: the row shows both blocks while the second one streams",
            row.Text == "Check the firewall first.\n\nThen reload it" && row.Streaming && row.Header == "Thinking…");
        Ingest(chat, ThinkingDelta(1, " and test SSH."));
        Ingest(chat, BlockStop(1));
        Ingest(chat, Assistant("m1", Thinking("Then reload it and test SSH, carefully.")));
        Check("live: a block's finished text replaces that block alone",
            row.Text == "Check the firewall first.\n\nThen reload it and test SSH, carefully.");
        Check("live: the row settles as one Thought process", !row.Streaming && row.Header == "Thought process" && row.HasText);
        ToolCall(chat, "m1", 2, "Bash", new JsonObject { ["command"] = "sudo ufw reload" });
        Check("live: the tool call still gets its own row below the thoughts",
            chat.Items.Count == 2 && ReferenceEquals(chat.Items[0], row) && chat.Items[1] is CompactToolGroupItem);
    }

    /// <summary>Some adapters send one finished message for the whole reply, after every block has streamed.</summary>
    private static void OneFinalMessageForBothBlocks()
    {
        var chat = Chat("one-final-message");
        Ingest(chat, MessageStart("m2"));
        Ingest(chat, BlockStart(0, Thinking("")));
        Ingest(chat, ThinkingDelta(0, "First"));
        Ingest(chat, BlockStop(0));
        Ingest(chat, BlockStart(1, Thinking("")));
        Ingest(chat, ThinkingDelta(1, "Second"));
        Ingest(chat, BlockStop(1));
        Ingest(chat, Assistant("m2", Thinking("First, finished."), Thinking("Second, finished.")));
        var row = Rows<ThinkingItem>(chat).Single();
        Check("one message for both: each finished block lands in its own place",
            row.Text == "First, finished.\n\nSecond, finished." && !row.Streaming);
    }

    /// <summary>Reopening a chat replays its saved transcript, one block per entry and nothing streamed.</summary>
    private static void ReplayedHistorySharesARow()
    {
        var chat = Chat("history");
        Replay(chat, "assistant", Message("m3", Thinking("Old first thought.")));
        Replay(chat, "assistant", Message("m3", Thinking("Old second thought.")));
        var id = "tool-" + ++_ids;
        Replay(chat, "assistant", Message("m3", ToolUse(id, "Bash", new JsonObject { ["command"] = "ls" })));
        Replay(chat, "user", ToolResult(id));
        Replay(chat, "assistant", Message("m4", Thinking("Old third thought.")));
        Replay(chat, "assistant", Message("m4", Text("Done.")));
        var thoughts = Rows<ThinkingItem>(chat);
        Check("history: back-to-back blocks reopen as one row",
            thoughts.Count == 2 && thoughts[0].Text == "Old first thought.\n\nOld second thought.");
        Check("history: a thought after a tool call keeps its own row", thoughts[1].Text == "Old third thought.");
    }

    private static void VisibleRowsKeepThoughtsApart()
    {
        var chat = Chat("apart");
        Replay(chat, "assistant", Message("m5", Thinking("Before the answer.")));
        Replay(chat, "assistant", Message("m5", Text("Here is what I found.")));
        Replay(chat, "assistant", Message("m5", Thinking("After the answer.")));
        Check("text between thoughts keeps them in separate rows", Rows<ThinkingItem>(chat).Count == 2);
        chat.Items.Add(new UserItem { Text = "Next question", Owner = chat });
        Replay(chat, "assistant", Message("m6", Thinking("A new turn's thought.")));
        var thoughts = Rows<ThinkingItem>(chat);
        Check("a new prompt starts a new row", thoughts.Count == 3 && thoughts[2].Text == "A new turn's thought."
            && thoughts[1].Text == "After the answer.");
    }

    /// <summary>Task status updates are folded into the Todos panel and never shown, so the thoughts on either side
    /// of one sit next to each other on screen.</summary>
    private static void HiddenToolCallsDoNotSplitARow()
    {
        var chat = Chat("hidden-tool");
        StreamThinking(chat, "m7", 0, "Mark the task done.");
        ToolCall(chat, "m7", 1, "TaskUpdate", new JsonObject { ["taskId"] = "1", ["status"] = "completed" });
        StreamThinking(chat, "m8", 0, "Now deploy.");
        var row = Rows<ThinkingItem>(chat).Single();
        Check("a hidden task update between thoughts leaves one row",
            row.Text == "Mark the task done.\n\nNow deploy." && chat.Items.Count == 1);
    }

    private static void RenderedTranscript()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var vm = new MainViewModel();
        var chat = Chat("rendered");
        vm.Chats.Add(chat);
        chat.Items.Add(new UserItem { Text = "Reload the firewall and make sure nothing broke.", Owner = chat });
        StreamThinking(chat, "r1", 0, "The user wants the firewall reloaded. Check the current rules first.");
        StreamThinking(chat, "r1", 1, "Reloading is safe as long as port 22 stays open.");
        ToolCall(chat, "r1", 2, "Bash", new JsonObject { ["command"] = "sudo ufw status && sudo ufw reload" });
        StreamThinking(chat, "r2", 0, "The reload finished. Now confirm SSH and the site still answer.");
        StreamThinking(chat, "r2", 1, "One command can check both.");
        StreamThinking(chat, "r2", 2, "Use a short timeout so a dead port fails fast.");
        ToolCall(chat, "r2", 3, "Bash", new JsonObject { ["command"] = "ssh -o ConnectTimeout=5 vps true && curl -sI https://example.com" });
        StreamThinking(chat, "r3", 0, "Both responded.");
        StreamText(chat, "r3", 1, "SSH and the site both still work after reloading the firewall.");
        Property(vm, "ActiveChat", chat);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Hidden)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Width = 1400;
        shell.Height = 900;
        _ = new WindowInteropHelper(shell).EnsureHandle();
        Layout(shell);

        var list = (ListBox)shell.FindName("MsgList");
        var thoughts = Rows<ThinkingItem>(chat);
        var toggles = Descendants<ToggleButton>(list).Where(toggle => toggle.Name == "thToggle").ToList();
        Check("rendered: six thinking blocks show as three Thought process rows",
            thoughts.Count == 3 && toggles.Count == 3 && toggles.Select(toggle => toggle.DataContext).SequenceEqual(thoughts));
        Check("rendered: each row reads Thought process", toggles.All(toggle =>
            Descendants<TextBlock>(toggle).Any(text => text.Text == "Thought process")));
        Render(shell, Path.Combine(Pictures, "collapsed.png"));

        toggles[1].IsChecked = true;
        Layout(shell);
        var body = Descendants<TextBox>(list).Single(box => ReferenceEquals(box.DataContext, thoughts[1]));
        Check("rendered: opening a row shows every block in it, a blank line apart",
            ((FrameworkElement)body.Parent).Visibility == Visibility.Visible && body.Text ==
            "The reload finished. Now confirm SSH and the site still answer.\n\nOne command can check both.\n\n" +
            "Use a short timeout so a dead port fails fast.");
        Render(shell, Path.Combine(Pictures, "expanded.png"));
    }

    // ---------- Claude Code stream-json shapes ----------

    private static void StreamThinking(ChatViewModel chat, string message, int index, string text)
    {
        if (index == 0) Ingest(chat, MessageStart(message));
        Ingest(chat, BlockStart(index, Thinking("")));
        Ingest(chat, ThinkingDelta(index, text));
        Ingest(chat, BlockStop(index));
        Ingest(chat, Assistant(message, Thinking(text)));
    }

    private static void StreamText(ChatViewModel chat, string message, int index, string text)
    {
        Ingest(chat, BlockStart(index, Text("")));
        Ingest(chat, Stream(new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = index,
            ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text },
        }));
        Ingest(chat, BlockStop(index));
        Ingest(chat, Assistant(message, Text(text)));
    }

    private static void ToolCall(ChatViewModel chat, string message, int index, string name, JsonObject input)
    {
        var id = "tool-" + ++_ids;
        Ingest(chat, BlockStart(index, ToolUse(id, name, new JsonObject())));
        Ingest(chat, BlockStop(index));
        Ingest(chat, Assistant(message, ToolUse(id, name, input)));
        Ingest(chat, ToolResult(id));
    }

    private static JsonObject Stream(JsonObject ev) => new() { ["type"] = "stream_event", ["event"] = ev };
    private static JsonObject MessageStart(string id) => Stream(new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject { ["id"] = id, ["role"] = "assistant", ["content"] = new JsonArray() },
    });
    private static JsonObject BlockStart(int index, JsonObject block) =>
        Stream(new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block });
    private static JsonObject ThinkingDelta(int index, string text) => Stream(new JsonObject
    {
        ["type"] = "content_block_delta", ["index"] = index,
        ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = text },
    });
    private static JsonObject BlockStop(int index) => Stream(new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
    private static JsonObject Assistant(string id, params JsonObject[] blocks) =>
        new() { ["type"] = "assistant", ["message"] = Message(id, blocks) };
    private static JsonObject Message(string id, params JsonObject[] blocks) =>
        new() { ["id"] = id, ["role"] = "assistant", ["content"] = new JsonArray(blocks) };
    private static JsonObject Thinking(string text) => new() { ["type"] = "thinking", ["thinking"] = text, ["signature"] = "sig" };
    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonObject ToolUse(string id, string name, JsonObject input) =>
        new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };
    private static JsonObject ToolResult(string id) => new()
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "ok" }),
        },
    };

    private static void Ingest(ChatViewModel chat, JsonObject node) => Call(chat, "IngestSdk", node);

    /// <summary>The history path: what reopening a chat runs for each saved transcript entry.</summary>
    private static void Replay(ChatViewModel chat, string type, JsonObject node)
    {
        var message = node["type"]?.GetValue<string>() == "user" ? node["message"]! : node;
        Call(chat, "IngestMessagePayload", type, message, null, false, null);
    }

    // ---------- fixture ----------

    private static ChatViewModel Chat(string name)
    {
        var cwd = Path.Combine(_root, name);
        Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, title: name, provider: "claude", accountId: "offline-thinking-test");
        Chats.Add(chat);
        return chat;
    }

    private static List<T> Rows<T>(ChatViewModel chat) => chat.Items.OfType<T>().ToList();

    private static void Layout(MainWindow shell)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)shell.Content;
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Render(MainWindow shell, string path)
    {
        Layout(shell);
        var bitmap = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)shell.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Property(object target, string name, object? value) => target.GetType().GetProperty(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Hidden)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);

    private static void Check(string text, bool condition)
    {
        if (!condition) throw new Exception("FAIL: " + text);
        _checks++;
        Console.WriteLine("PASS: " + text);
    }
}
