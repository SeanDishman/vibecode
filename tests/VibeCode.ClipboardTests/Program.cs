using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private const string Address = "http://192.168.1.252:8766/";
    private static int _checks;
    private static string? _copied;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeCode-ClipboardTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(root, "settings"));
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_LEGACY_HOME", Path.Combine(root, "codex"));
        AppSettings.Current.AgentMemoryEnabled = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative),
        });
        TextEditMenu.Install();
        var viewer = new ProbeMarkdownView
        {
            IsToolBarVisible = false, MarkdownStyleName = "Sasabune", Focusable = true,
            IsTabStop = false, IsSelectionEnabled = true,
        };
        var window = new Window
        {
            Content = viewer, Width = 800, Height = 400, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        };
        // Capture and cancel AFTER WPF creates its clipboard formats. This exercises the failing native
        // serialization path without replacing the user's clipboard or involving a clipboard manager.
        DataObject.AddCopyingHandler(viewer, CaptureCopy);
        try
        {
            viewer.Markdown = $"LAN Share is running: [{Address}]({Address}). Open it from a device on the same network. " +
                              "I used port 8766 because 8765 was already occupied; the URL returned HTTP 200.";
            window.Show();
            Pump();
            var link = Links(viewer.Document!).Single();
            Check(new TextRange(link.ContentStart, link.ContentEnd).Text == Address, "LAN address renders as a link");
            viewer.Selection.Select(link.ElementStart, link.ElementEnd);
            Copy(viewer, "selected LAN link copies without throwing");
            Check(_copied == Address, "copying the link preserves its port and trailing slash");

            // Use the same command and explicit target as the screenshot's right-click menu.
            typeof(TextEditMenu).GetMethod("EnsureOurMenu", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [viewer]);
            var menuCopy = viewer.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy"));
            viewer.Selection.Select(viewer.Document!.ContentStart, viewer.Document.ContentEnd);
            _copied = null;
            ((RoutedCommand)menuCopy.Command).Execute(menuCopy.CommandParameter, menuCopy.CommandTarget);
            Check(_copied == viewer.Selection.Text, "right-click Copy preserves the full LAN message");
            Activate(viewer, link, TranscriptLinkKind.Web, Address, null, "LAN link still opens after copying");

            viewer.Markdown = "[Open source](src/main.cs:42) and **bold text**.\n\n" +
                              "```cs\nvar answer = 42;\nConsole.WriteLine(answer);\n```\n\n" +
                              "1. [Web link](https://example.com/path?q=1#section)\n" +
                              "2. [Section](#section)\n\n" +
                              "| Name | Link |\n| --- | --- |\n| docs | [Docs](https://example.com/docs) |";
            viewer.LinkBaseDirectory = Path.GetTempPath();
            Pump();
            ApplicationCommands.SelectAll.Execute(null, viewer);
            Copy(viewer, "Ctrl+C command copies links, code, lists and tables");
            Check(_copied!.Contains("var answer = 42;") && _copied.Contains("Console.WriteLine(answer);"),
                "selection copy retains fenced code");
            var links = Links(viewer.Document!).ToArray();
            Check(links.Length == 4, "nested list and table links are present");
            Activate(viewer, links[0], TranscriptLinkKind.LocalPath, Path.Combine(Path.GetTempPath(), "src", "main.cs"),
                42, "file link preserves the project directory and line number");
            Activate(viewer, links[1], TranscriptLinkKind.Web, "https://example.com/path?q=1#section", null,
                "web link preserves query and fragment");
            Activate(viewer, links[2], TranscriptLinkKind.Anchor, "#section", null, "anchor link still navigates");

            viewer.Engine = new MdXaml.Markdown();
            Pump();
            ApplicationCommands.SelectAll.Execute(null, viewer);
            Copy(viewer, "replacing the markdown engine keeps native copy working");
            Activate(viewer, Links(viewer.Document!).First(), TranscriptLinkKind.LocalPath,
                Path.Combine(Path.GetTempPath(), "src", "main.cs"), 42, "engine replacement keeps navigation working");

            viewer.Markdown = $"Streamed reply: [LAN Share]({Address})";
            Pump();
            ApplicationCommands.SelectAll.Execute(null, viewer);
            Copy(viewer, "a streamed replacement document can be copied");
            Activate(viewer, Links(viewer.Document!).Single(), TranscriptLinkKind.Web, Address, null,
                "streamed replacement links still navigate");

            viewer.Document = new FlowDocument(new Paragraph(new Hyperlink(new Run("Restored link"))
            {
                CommandParameter = new Uri(Address),
            }));
            Pump();
            ApplicationCommands.SelectAll.Execute(null, viewer);
            Copy(viewer, "a restored FlowDocument can be copied");
            Activate(viewer, Links(viewer.Document).Single(), TranscriptLinkKind.Web, Address, null,
                "restored links still navigate with URI parameters");

            VerifyLinkMenus(window, viewer, root);
            VerifyRecycledMenu(window, root);
            Console.WriteLine($"PASS: {_checks} clipboard and link checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static void VerifyRecycledMenu(Window window, string root)
    {
        var firstChat = new ChatViewModel(root, provider: "codex", accountId: "clipboard-test") { ExcludeFromMemory = true };
        var secondChat = new ChatViewModel(root, provider: "codex", accountId: "clipboard-test") { ExcludeFromMemory = true };
        var first = new TextItem { Text = "First message" };
        var second = new TextItem { Text = "Second message" };
        firstChat.Items.Add(first);
        secondChat.Items.Add(second);
        var text = new TextBox { IsReadOnly = true, DataContext = first, Text = first.Text };
        var host = new ContentControl { DataContext = firstChat, Content = text };
        window.Content = host;
        Pump();
        string? copied = null;
        Action<string> capture = value => copied = value;
        text.ContextMenu = (ContextMenu)typeof(TextEditMenu).GetMethod("BuildFor", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [text, capture])!;
        var messageCopy = text.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy message"));
        var conversationCopy = text.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copy whole conversation"));
        try
        {
            messageCopy.Command.Execute(null);
            Check(copied == first.Text, "Copy message uses the initial message");
            conversationCopy.Command.Execute(null);
            Check(copied!.Contains(first.Text), "Copy conversation uses the initial chat");

            // Recycling changes the data contexts while retaining the text host and its context menu.
            text.DataContext = second;
            text.Text = second.Text;
            host.DataContext = secondChat;
            Pump();
            messageCopy.Command.Execute(null);
            Check(copied == second.Text, "Copy message uses the recycled control's current message");
            conversationCopy.Command.Execute(null);
            Check(copied!.Contains(second.Text) && !copied.Contains(first.Text),
                "Copy conversation uses the recycled control's current chat");

            text.DataContext = null;
            host.DataContext = null;
            messageCopy.Command.Execute(null);
            conversationCopy.Command.Execute(null);
            Check(copied.Contains(second.Text), "detached menus leave the clipboard unchanged");
        }
        finally
        {
            firstChat.Close();
            secondChat.Close();
        }
    }

    private static void Copy(MarkdownView viewer, string assertion)
    {
        _copied = null;
        Check(ApplicationCommands.Copy.CanExecute(null, viewer), "native Copy is enabled for the selection");
        ApplicationCommands.Copy.Execute(null, viewer);
        Check(_copied == viewer.Selection.Text, assertion);
    }

    private static void CaptureCopy(object sender, DataObjectCopyingEventArgs args)
    {
        args.CancelCommand();
        _copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
        Check(!string.IsNullOrEmpty(_copied), "native copy creates plain text");
        Check(args.DataObject.GetDataPresent(DataFormats.Rtf), "native copy keeps rich text formatting");
        Check(args.DataObject.GetData(DataFormats.Xaml) is string, "native copy creates serializable XAML");
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes((string)args.DataObject.GetData(DataFormats.Xaml)));
        var document = new FlowDocument();
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        range.Load(payload, DataFormats.Xaml);
        Check(range.Text.TrimEnd('\r', '\n') == _copied!.TrimEnd('\r', '\n'), "rich clipboard text can be pasted back");
    }

    private static void Activate(ProbeMarkdownView viewer, Hyperlink link, TranscriptLinkKind kind,
        string address, int? line, string assertion)
    {
        viewer.Opened = null;
        var peer = new HyperlinkAutomationPeer(link);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        Check(viewer.Opened == new TranscriptLink(kind, address, line), assertion);
        Check(viewer.Error is null, "navigation does not show an error");
    }

    private static IEnumerable<Hyperlink> Links(DependencyObject element)
    {
        if (element is Hyperlink link) yield return link;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var descendant in Links(child)) yield return descendant;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException(assertion);
        _checks++;
    }

    private sealed class ProbeMarkdownView : MarkdownView
    {
        public TranscriptLink? Opened;
        public string? Error;
        protected override void OpenLinkTarget(TranscriptLink target) => Opened = target;
        protected override void ReportLinkError(string message) => Error = message;
    }
}
