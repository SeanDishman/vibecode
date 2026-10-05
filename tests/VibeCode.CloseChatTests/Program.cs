using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

/// <summary>
/// Both X buttons that close a chat - the sidebar row's and the chat header's - ask "Are you sure?" first. The checks
/// click the real buttons in a real MainWindow and answer the real message box from another thread. Offline: every
/// chat runs on a fake session and settings live in a temp folder.
/// </summary>
internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string DialogTitle = "Close chat";
    private const int IdYes = 6, IdNo = 7;
    private static readonly List<ChatViewModel> Chats = [];
    private static readonly string Screenshots = Path.Combine(Path.GetTempPath(), "vibecode-close-chat-confirmation");
    private static int _checks;
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "vibecode-close-chat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Screenshots);
        Environment.SetEnvironmentVariable("VIBECODE_DATA_DIR", Path.Combine(_root, "settings"));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        try
        {
            Verify();
            Console.WriteLine($"PASS: {_checks} close-chat confirmation checks. No live model calls.");
            Console.WriteLine("Dialog screenshots: " + Screenshots);
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
                && Path.GetFileName(_root).StartsWith("vibecode-close-chat-tests-", StringComparison.Ordinal))
                Directory.Delete(_root, recursive: true);
        }
    }

    private static void Verify()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var vm = new MainViewModel();
        var active = Chat(vm, "Active chat");
        var other = Chat(vm, "Other chat");
        var untitled = Chat(vm, "Soon untitled");
        var locked = Chat(vm, "Locked chat");
        untitled.Title = "";
        vm.ToggleChatLock(locked);
        Property(vm, "ActiveChat", active);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Hidden)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([vm, null, true]);
        shell.Width = 1400;
        shell.Height = 900;
        _ = new WindowInteropHelper(shell).EnsureHandle();
        Layout(shell);

        // Sidebar row X, answered No: nothing changes, and the click doesn't fall through to opening that row.
        var dialog = Click(RowClose(shell, other), yes: false, "sidebar-row.png");
        Check("sidebar X asks before closing", dialog is not null);
        Check("dialog is titled Close chat and names the chat",
            dialog!.Title == DialogTitle && dialog.Message == "Are you sure you want to close \"Other chat\"?");
        Check("dialog offers Yes and No, Yes as default like Delete chat",
            dialog.Buttons.SequenceEqual(["&Yes", "&No"]) && dialog.DefaultButton == IdYes);
        Check("No keeps the chat open with its session alive",
            vm.Chats.Contains(other) && other.Status == "idle" && !Session(other).HasExited);
        Check("the X click does not also open the row", ReferenceEquals(vm.ActiveChat, active));

        // Sidebar row X, answered Yes: closed exactly as before.
        dialog = Click(RowClose(shell, other), yes: true);
        Check("Yes closes the sidebar chat and stops its session",
            dialog is not null && !vm.Chats.Contains(other) && other.Status == "closed" && Session(other).HasExited);
        Check("closing another row leaves the open chat selected", ReferenceEquals(vm.ActiveChat, active));

        // Header X during a turn: the dialog says the agent will be stopped.
        active.Status = "running";
        Layout(shell);
        var headerClose = HeaderClose(shell);
        dialog = Click(headerClose, yes: false, "header-working.png");
        Check("header X asks before closing", dialog is { Title: DialogTitle });
        Check("a turn in flight is disclosed", dialog!.Message ==
            "Are you sure you want to close \"Active chat\"?\n\nClaude is still working and will be stopped.");
        Check("No leaves the working chat running", vm.Chats.Contains(active) && active.Status == "running"
            && !Session(active).HasExited && ReferenceEquals(vm.ActiveChat, active));

        // A chat whose CLI is still booting has no turn to stop, and an empty title reads as "this chat".
        Property(vm, "ActiveChat", untitled);
        untitled.Status = "starting";
        Layout(shell);
        dialog = Click(HeaderClose(shell), yes: false, "header-untitled.png");
        Check("an untitled chat is called this chat, with no working notice while starting",
            dialog?.Message == "Are you sure you want to close this chat?");
        Check("No keeps the untitled chat", vm.Chats.Contains(untitled) && untitled.Status == "starting");

        // Header X answered Yes: closed, and the selection moves on as CloseChat always did.
        Property(vm, "ActiveChat", active);
        active.Status = "idle";
        Layout(shell);
        dialog = Click(HeaderClose(shell), yes: true);
        Check("Yes on the header X closes the open chat", dialog is not null && !vm.Chats.Contains(active)
            && active.Status == "closed" && Session(active).HasExited);
        Check("the selection moves to a remaining chat", vm.ActiveChat is { } next && vm.Chats.Contains(next));

        // Locked: both buttons are unavailable, and the handler asks nothing - CloseChat refuses and explains.
        Property(vm, "ActiveChat", locked);
        Layout(shell);
        headerClose = HeaderClose(shell);
        var rowClose = RowClose(shell, locked);
        Check("a locked chat's X buttons are disabled", !headerClose.IsEnabled && !rowClose.IsEnabled);
        var banners = locked.Items.Count;
        var silent = AnswerDialog(yes: false, screenshot: null, timeoutMs: 1500);
        Call(shell, "OnCloseActiveChat", headerClose, new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => silent.IsCompleted);
        Check("a locked chat is not asked about", silent.GetAwaiter().GetResult() is null);
        Check("a locked chat stays open with the existing unlock notice", vm.Chats.Contains(locked)
            && locked.Status == "idle" && locked.Items.Count == banners + 1
            && locked.Items.Last() is BannerItem { Text: var notice } && notice.Contains("Unlock chat"));
    }

    /// <summary>Clicks through UI Automation, which queues the click the way a real mouse-up does (the handler opens a
    /// modal dialog, so it must not run inline), then answers whatever dialog appears.</summary>
    private static DialogCapture? Click(Button button, bool yes, string? screenshot = null)
    {
        Check("the X button is clickable", button.IsEnabled);
        var answer = AnswerDialog(yes, screenshot, timeoutMs: 8000);
        var invoke = (IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke);
        invoke.Invoke();
        PumpUntil(() => answer.IsCompleted);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return answer.GetAwaiter().GetResult();
    }

    private static Button RowClose(MainWindow shell, ChatViewModel chat)
    {
        var row = Descendants<Button>((DependencyObject)shell.Content)
            .First(button => ReferenceEquals(button.DataContext, chat) && button.ContextMenu is not null);
        return Descendants<Button>(row).Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Close chat");
    }

    /// <summary>The X at the end of the chat header, right after the Artifacts &amp; todos toggle.</summary>
    private static Button HeaderClose(MainWindow shell)
    {
        var panelToggle = (ToggleButton)shell.FindName("PanelToggle");
        var close = ((Panel)panelToggle.Parent).Children.OfType<Button>().Last();
        if (close.Content as string != "") throw new Exception("FAIL: the header X button was not found.");
        return close;
    }

    /// <summary>Watches the UI thread for the confirmation, records it, optionally saves a picture, then answers.
    /// Completes with null when no dialog shows up in time.</summary>
    private static Task<DialogCapture?> AnswerDialog(bool yes, string? screenshot, int timeoutMs)
    {
        var uiThread = GetCurrentThreadId();
        var done = new TaskCompletionSource<DialogCapture?>();
        var watcher = new Thread(() =>
        {
            try { done.SetResult(Watch()); }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true };
        watcher.SetApartmentState(ApartmentState.STA);   // WPF imaging for the screenshot
        watcher.Start();
        return done.Task;

        DialogCapture? Watch()
        {
            var watch = Stopwatch.StartNew();
            nint dialog = 0;
            while (dialog == 0 && watch.ElapsedMilliseconds < timeoutMs)
            {
                EnumThreadWindows(uiThread, (window, _) =>
                {
                    if (IsWindowVisible(window) && WindowText(window) == DialogTitle) dialog = window;
                    return true;
                }, 0);
                if (dialog == 0) Thread.Sleep(10);
            }
            if (dialog == 0) return null;
            Thread.Sleep(250);   // let it finish painting before the picture
            var buttons = new List<string>();
            EnumChildWindows(dialog, (child, _) =>
            {
                if (ClassName(child) == "Button") buttons.Add(WindowText(child));
                return true;
            }, 0);
            var capture = new DialogCapture(WindowText(dialog),
                WindowText(GetDlgItem(dialog, 0xFFFF)).Replace("\r\n", "\n"),   // the message box's text control
                buttons,
                (int)(SendMessage(dialog, 0x0400, 0, 0).ToInt64() & 0xffff));   // DM_GETDEFID
            if (screenshot is not null) SavePicture(dialog, Path.Combine(Screenshots, screenshot));
            PostMessage(dialog, 0x0111, yes ? IdYes : IdNo, 0);   // WM_COMMAND
            return capture;
        }
    }

    private static void SavePicture(nint window, string path)
    {
        GetWindowRect(window, out var bounds);
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var previous = SelectObject(memory, bitmap);
        try
        {
            PrintWindow(window, memory, 2);   // PW_RENDERFULLCONTENT
            SelectObject(memory, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    private static ChatViewModel Chat(MainViewModel vm, string title)
    {
        var cwd = Path.Combine(_root, title.Replace(' ', '-'));
        Directory.CreateDirectory(cwd);
        var chat = new ChatViewModel(cwd, title: title, provider: "claude", accountId: "offline-close-test");
        Field(chat, "_session", new FakeSession());
        chat.Status = "idle";
        Chats.Add(chat);
        vm.Chats.Add(chat);
        return chat;
    }

    private static void Layout(MainWindow shell)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)shell.Content;
        content.Measure(new Size(1400, 900));
        content.Arrange(new Rect(0, 0, 1400, 900));
        content.UpdateLayout();
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

    private static FakeSession Session(ChatViewModel chat) => (FakeSession)typeof(ChatViewModel).GetField("_session", Hidden)!.GetValue(chat)!;
    private static void Field(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void Property(object target, string name, object? value) => target.GetType().GetProperty(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Hidden)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);

    private static void Check(string text, bool condition)
    {
        if (!condition) throw new Exception("FAIL: " + text);
        _checks++;
        Console.WriteLine("PASS: " + text);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || watch.Elapsed > TimeSpan.FromSeconds(25)) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!done()) throw new TimeoutException("The close confirmation fixture did not finish.");
    }

    private static string WindowText(nint window)
    {
        var text = new StringBuilder(4096);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string ClassName(nint window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    private sealed record DialogCapture(string Title, string Message, List<string> Buttons, int DefaultButton);
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    private delegate bool EnumerateWindow(nint window, nint state);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, EnumerateWindow callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

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
        public void Start() { }
        public void SendUser(JsonNode content) { }
        public Task InterruptAsync() => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode) => Task.CompletedTask;
        public Task SetModelAsync(string? model, string? effort = null) => Task.CompletedTask;
        public void RespondPermission(string requestId, JsonObject result, string? toolUseId) { }
        public void Dispose() => HasExited = true;
    }
}
