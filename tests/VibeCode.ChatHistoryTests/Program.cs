using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly List<ChatViewModel> Opened = new();
    private static int _checks;
    private static string _root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--cleanup-existing")) return CleanupExisting();
        _root = Path.Combine(Path.GetTempPath(), "vibecode-chat-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        foreach (var (key, folder) in new[] { ("VIBECODE_DATA_DIR", "settings"), ("CLAUDE_CONFIG_DIR", "claude"),
                     ("VIBECODE_CLAUDE_ACCOUNT_STORE", "claude-accounts"), ("GEMINI_CLI_HOME", "gemini-home"), ("VIBECODE_CODEX_LEGACY_HOME", "codex"), ("VIBECODE_CODEX_ACCOUNT_STORE", "codex-accounts") })
            Environment.SetEnvironmentVariable(key, Path.Combine(_root, folder));
        Environment.SetEnvironmentVariable("VIBECODE_HIDDEN", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.NotifyOnTurnEnd = AppSettings.Current.NotifyOnAwaitingInput = false;
        try
        {
            Retention();
            Catalog();
            StartupCleanup();
            HistoryRefresh();
            Sidebar();
            Console.WriteLine($"PASS: {_checks} chat history checks; no model calls.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException : error);
            return 1;
        }
        finally
        {
            foreach (var chat in Opened) chat.Close();
            app.Shutdown();
        }
    }

    private static void Retention()
    {
        var settings = new AppSettings();
        var old = Entry("old", "work", 8);
        var fresh = Entry("fresh", "work", 1);
        var edge = Entry("edge", "os", 7);
        var locked = Entry("locked", "os", 20);
        var draft = Entry("draft", "os", 20);
        var busy = Entry("busy", "os", 20);
        var reused = Entry("reused", "work", 20);
        var sessions = new[] { old, fresh, edge, locked, draft, busy, reused };
        settings.OwnedSessions.UnionWith(sessions.Select(s => s.SessionId));
        settings.OpenChats.Add(new OpenChatState { SessionId = "old", Provider = "claude" });
        settings.OpenChats.Add(new OpenChatState { SessionId = "draft", Draft = "Unsent work" });
        settings.ChatMetadata["claude:locked"] = new() { IsLocked = true };
        settings.ChatMetadata["claude:reused"] = new() { LastActivityAt = Now.AddHours(-2) };
        settings.RecentDirectories.Add(new() { Cwd = "old-dir", LastUsed = Now.AddDays(-8) });
        settings.RecentDirectories.Add(new() { Cwd = "fresh-dir", LastUsed = Now.AddDays(-1) });
        var removed = ChatHistoryRetention.Apply(settings, sessions, Now, new HashSet<string> { "busy" });
        Check("only inactive, unprotected chat expires", removed.SetEquals(new[] { "old" }));
        Check("seven-day boundary is retained", !ChatHistoryRetention.Expired(Now.AddDays(-7), Now));
        Check("one tick beyond seven days expires", ChatHistoryRetention.Expired(Now.AddDays(-7).AddTicks(-1), Now));
        Check("unknown activity is never guessed", !ChatHistoryRetention.Expired(null, Now));
        Check("expired tab and ownership are removed", settings.OpenChats.All(c => c.SessionId != "old") && !settings.OwnedSessions.Contains("old"));
        Check("deletion is durable and idempotent", settings.DeletedSessions.Contains("old") && ChatHistoryRetention.Apply(settings, Array.Empty<SessionEntry>(), Now).Count == 0);
        Check("old directory suggestion is removed", settings.RecentDirectories.Select(d => d.Cwd).SequenceEqual(new[] { "fresh-dir" }));
        Check("maintenance interval is six hours", ChatHistoryRetention.CheckInterval == TimeSpan.FromHours(6));

        var bridges = new AppSettings();
        bridges.SavedBridges.Add(new() { HostSessionId = "host", Provider = "claude", Cwd = _root,
            SavedAt = Now.LocalDateTime, Peers = new() { new() { SessionId = "peer", Provider = "codex", Cwd = _root } } });
        var oldBridge = new[] { Entry("host", "work", 8), Entry("peer", "work", 8) with { Provider = "codex" } };
        Check("recent autosave does not reset actual chat age", ChatHistoryRetention.Apply(bridges, oldBridge, Now).Count == 2 && bridges.SavedBridges.Count == 0);
        bridges.SavedBridges.Add(new() { HostSessionId = "host2", Provider = "claude", Cwd = _root,
            SavedAt = Now.LocalDateTime, Peers = new() { new() { SessionId = "peer2", Provider = "codex", Cwd = _root } } });
        Check("recent bridge member keeps its roster intact", ChatHistoryRetention.Apply(bridges,
            new[] { Entry("host2", "work", 8), Entry("peer2", "work", 1) with { Provider = "codex" } }, Now).Count == 0);
    }

    private static void Catalog()
    {
        var claudeProject = Path.Combine(_root, "claude", "projects", "fixture");
        Directory.CreateDirectory(claudeProject);
        var transcript = Path.Combine(claudeProject, "claude-fixture.jsonl");
        File.WriteAllText(transcript, Line(new JsonObject { ["type"] = "user", ["cwd"] = Path.Combine(_root, "work"),
            ["message"] = new JsonObject { ["content"] = "Fixture chat" } }) +
            Line(new JsonObject { ["type"] = "assistant", ["timestamp"] = Now.AddHours(-4).ToString("O") }));
        File.SetLastWriteTimeUtc(transcript, Now.AddHours(-1).UtcDateTime);
        var codexId = "00000000-0000-0000-0000-000000000001";
        var account = Path.Combine(_root, "codex-accounts", "fixture");
        var rollouts = Path.Combine(account, "home", "sessions");
        Directory.CreateDirectory(rollouts);
        File.WriteAllText(Path.Combine(account, "account.json"), "{\"Id\":\"fixture-login\"}");
        File.WriteAllText(Path.Combine(rollouts, "rollout-" + codexId + ".jsonl"),
            Line(new JsonObject { ["type"] = "session_meta", ["payload"] = new JsonObject { ["id"] = codexId, ["cwd"] = Path.Combine(_root, "os") } }) +
            Line(new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "user_message", ["message"] = "Codex fixture" } }) +
            Line(new JsonObject { ["type"] = "event_msg", ["timestamp"] = Now.AddHours(-2).ToString("O"), ["payload"] = new JsonObject { ["type"] = "agent_message", ["message"] = "Done" } }));
        var sessions = SessionCatalog.ListProjects(int.MaxValue, new HashSet<string> { "claude-fixture", codexId }).SelectMany(p => p.Sessions).ToList();
        Check("catalog includes Codex history with owning account", sessions.Any(s => s.SessionId == codexId && s.Provider == "codex" && s.AccountId == "fixture-login"));
        Check("AI reply timestamp is independent of file mtime", sessions.Single(s => s.SessionId == "claude-fixture").LastAssistantAt == Now.AddHours(-4));
        Check("Codex AI timestamp comes from rollout", sessions.Single(s => s.SessionId == codexId).LastAssistantAt == Now.AddHours(-2));
        Check("catalog respects ownership before displaying histories", SessionCatalog.ListProjects(allowedSessionIds: new HashSet<string>()).Count == 0);
    }

    private static void HistoryRefresh()
    {
        var settings = AppSettings.Current;
        settings.OwnedSessions.UnionWith(new[] { "claude-fixture", "00000000-0000-0000-0000-000000000001" });
        settings.ChatMetadata["claude:claude-fixture"] = new() { IsLocked = true };
        var vm = new MainViewModel();
        Wait((Task)Call(vm, "RefreshChatHistoryAsync", false)!);
        Check("retained conversations remain available in project history", vm.Projects.Sum(p => p.Sessions.Count) == 2);
        Check("retained conversations remain available in recent history", vm.RecentSessions.Count == 2);
        Check("history refresh does not open sidebar rows or folders, including locked history", vm.ChatGroups.IsEmpty && vm.ChatGroups.Groups!.Count == 0);
    }

    private static void Sidebar()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var vm = new MainViewModel();
        var first = Chat(vm, "work", "Newest reply", "first", Now.AddMinutes(-1));
        var second = Chat(vm, "work", "Earlier reply", "second", Now.AddHours(-3));
        var other = Chat(vm, "os", "Kernel build", "third", Now.AddHours(-2));
        var duplicateName = Chat(vm, "another/work", "Other work directory", "fourth", Now.AddHours(-4));
        var archived = Entry("saved", "work", 0) with { Title = "Saved conversation", LastAssistantAt = Now.AddMinutes(-30) };
        var historyOnly = Entry("saved-only", "history-only", 0) with { Title = "History only conversation" };
        AppSettings.Current.OwnedSessions.UnionWith(new[] { "saved", "saved-only", "first" });
        Set(vm, "_catalogChats", new List<SessionEntry> { archived, archived with { SessionId = "first" }, historyOnly });
        Call(vm, "RefreshSidebarChats");
        var groups = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().ToList();
        Check("one group per full path, even with duplicate leaf names", groups.Count == 3);
        Check("folder label shows final path component", ((SidebarDirectory)groups[0].Name).Name == "work");
        Check("only open chats appear, ordered by last AI reply", groups[0].Items.Cast<object>().SequenceEqual(new object[] { first, second }));
        Check("folder counts include only open chats", groups[0].ItemCount == 2 && vm.ChatGroups.Cast<object>().Count() == 4);
        Check("saved conversations do not create sidebar folders", !groups.Any(g => ((SidebarDirectory)g.Name).Name == "history-only"));
        vm.CloseChat(first);
        Call(vm, "RefreshSidebarChats");
        Check("closing a catalog-backed chat keeps it out of the sidebar after refresh", vm.ChatGroups.Cast<object>().Count() == 3 && !vm.ChatGroups.OfType<ChatViewModel>().Any(c => c.SessionId == "first"));
        Check("closing a chat preserves its history ownership", AppSettings.Current.OwnedSessions.Contains("first") && !AppSettings.Current.DeletedSessions.Contains("first"));
        first = Chat(vm, "work", "Newest reply", "first", Now.AddMinutes(-1));
        Check("reopening a saved chat adds exactly one live row", vm.ChatGroups.OfType<ChatViewModel>().Count(c => c.SessionId == "first") == 1);
        var reopened = Chat(vm, "history-only", "History only conversation", "saved-only", Now.AddHours(-5));
        Check("opening a saved chat creates its folder", vm.ChatGroups.Groups!.Count == 4);
        vm.CloseChat(reopened);
        Call(vm, "RefreshSidebarChats");
        Check("closing the last open chat removes its folder despite retained history", vm.ChatGroups.Groups!.Count == 3 && !vm.ChatGroups.OfType<ChatViewModel>().Any(c => c.SessionId == "saved-only"));
        var folder = (SidebarDirectory)groups[0].Name;
        folder.Open = false;
        var originalReply = first.SidebarActivityAt;
        Call(first, "RecordAssistantActivity");
        Check("replaying a resumed chat does not make it recent", first.SidebarActivityAt == originalReply);
        Call(other, "RecordUserActivity");
        Call(vm, "RefreshSidebarChats");
        Check("sending alone does not outrank the latest AI reply", ReferenceEquals(vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().First().Items[0], first));
        Call(other, "RecordAssistantActivity");
        Call(vm, "RefreshSidebarChats");
        Check("new AI reply promotes the chat and folder", ReferenceEquals(vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().First().Items[0], other));
        Check("collapse choice survives reordering", !folder.Open);
        folder.Open = true;
        vm.ActiveChat = first;
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Hidden).Single(c => c.GetParameters().Length == 3).Invoke(new object?[] { vm, null, true });
        _ = new WindowInteropHelper(shell).EnsureHandle();
        foreach (var size in new[] { new Size(1400, 900), new Size(960, 600) })
        {
            shell.Width = size.Width;
            shell.Height = size.Height;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)shell.Content;
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            var texts = Descendants<TextBlock>(content).Select(t => t.Text).ToArray();
            Check($"{size.Width}px sidebar renders folder labels without Projects section", texts.Contains("work") && texts.Contains("os") && !texts.Contains("PROJECTS") && !texts.Contains("CHATS"));
            Check($"{size.Width}px sidebar renders open chats", texts.Contains("Newest reply") && texts.Contains("Earlier reply"));
            Check($"{size.Width}px sidebar hides saved chats and history-only folders", !texts.Contains("Saved conversation") && !texts.Contains("History only conversation") && !texts.Contains("history-only"));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var destination = Path.GetFullPath(Path.Combine("artifacts", "sidebar-visibility-20261012", $"sidebar-{size.Width}.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var stream = File.Create(destination);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            Console.WriteLine("Screenshot: " + destination);
        }

        // Pinned chats collect into one section above every folder, whatever their folder or age.
        vm.TogglePin(duplicateName);   // oldest reply, alone in its folder
        vm.TogglePin(second);          // older than the newest chat in its folder
        var sections = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().ToList();
        var pinned = (SidebarDirectory)sections[0].Name;
        Check("pinned chats form the first section, above every folder", pinned.IsPinned && pinned.Name == "Pinned" &&
            sections[0].Items.Cast<object>().SequenceEqual(new object[] { second, duplicateName }));
        Check("a pinned chat appears only in the Pinned section", sections.Count == 3 && vm.ChatGroups.Cast<object>().Count() == 4 &&
            sections.Skip(1).All(g => !((SidebarDirectory)g.Name).IsPinned && !g.Items.Contains(second) && !g.Items.Contains(duplicateName)));
        Call(first, "RecordUserActivity");
        Call(first, "RecordAssistantActivity");
        Call(vm, "RefreshSidebarChats");
        sections = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().ToList();
        Check("the newest AI reply cannot lift a folder above the Pinned section",
            ((SidebarDirectory)sections[0].Name).IsPinned && ReferenceEquals(sections[1].Items[0], first));
        vm.TogglePin(second);
        sections = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().ToList();
        Check("unpinning returns a chat to its folder in reply order",
            sections[0].Items.Cast<object>().SequenceEqual(new object[] { duplicateName }) &&
            sections[1].Items.Cast<object>().SequenceEqual(new object[] { first, second }));
        Check("pinned chats also lead the saved chat order", vm.Chats.First() == duplicateName);

        foreach (var size in new[] { new Size(1400, 900), new Size(960, 600) })
        {
            shell.Width = size.Width;
            shell.Height = size.Height;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)shell.Content;
            content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
            var sidebar = Descendants<ItemsControl>(content).Single(list => AutomationProperties.GetName(list) == "Chats by folder");
            var rendered = Descendants<GroupItem>(sidebar).Select(item => (Section: (SidebarDirectory)((CollectionViewGroup)item.DataContext).Name,
                Top: item.TransformToAncestor(content).Transform(new Point()).Y, Item: item)).ToList();
            Check($"{size.Width}px sidebar draws the Pinned section above every folder", rendered.Count == 3 &&
                rendered[0].Section.IsPinned && rendered.Skip(1).All(r => !r.Section.IsPinned && r.Top > rendered[0].Top));
            var newChatHere = rendered.Select(r => Descendants<Button>(r.Item)
                .Single(b => AutomationProperties.GetName(b) == "New chat in this folder")).ToList();
            Check($"{size.Width}px only folders offer New chat here", newChatHere[0].Visibility == Visibility.Collapsed &&
                newChatHere.Skip(1).All(b => b.Visibility == Visibility.Visible));
            var texts = Descendants<TextBlock>(rendered[0].Item).Select(t => t.Text).ToArray();
            Check($"{size.Width}px Pinned section is labelled and lists the pinned chat", texts.Contains("Pinned") && texts.Contains("Other work directory"));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var destination = Path.GetFullPath(Path.Combine("artifacts", "pinned-sidebar-20261012", $"sidebar-pinned-{size.Width}.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var stream = File.Create(destination);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            Console.WriteLine("Screenshot: " + destination);
        }

        var existingChats = vm.Chats.ToArray();
        var closeBatch = Enumerable.Range(0, 10).Select(index =>
            Chat(vm, NoDirectoryWorkspace.Folder, "Close batch " + index, "close-batch-" + index, Now)).ToArray();
        foreach (var chat in closeBatch) AppSettings.Current.OwnedSessions.Add(chat.SessionId!);
        closeBatch[0].Status = "working";
        var closeGroup = vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().Single(g => g.Items.Contains(closeBatch[0]));
        ((SidebarDirectory)closeGroup.Name).Open = false;
        var menu = (ContextMenu)shell.FindResource("ChatGroupMenu");
        menu.DataContext = closeGroup;
        var closeAll = menu.Items.OfType<MenuItem>().Single();
        Check("group context menu contains only Close all", closeAll.Header as string == "Close all");
        closeAll.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("Close all closes all ten chats, including a working chat in a collapsed group",
            closeBatch.All(chat => chat.Status == "closed" && !vm.Chats.Contains(chat)));
        Check("Close all removes the empty No directory group",
            !vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().Any(g => ((SidebarDirectory)g.Name).Name == "No directory"));
        Check("Close all preserves other folders and pinned chats", vm.Chats.SequenceEqual(existingChats));
        Check("closing a group keeps its sessions resumable without deleting history", closeBatch.All(chat =>
            AppSettings.Current.OwnedSessions.Contains(chat.SessionId!) && !AppSettings.Current.DeletedSessions.Contains(chat.SessionId!)));

        var protectedChat = Chat(vm, "protected-group", "Locked", "group-locked", Now);
        var unlockedChat = Chat(vm, "protected-group", "Unlocked", "group-unlocked", Now);
        vm.ToggleChatLock(protectedChat);
        vm.CloseChatGroup(vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().Single(g => g.Items.Contains(protectedChat)));
        Check("bulk close respects existing chat locks while closing the rest",
            vm.Chats.Contains(protectedChat) && protectedChat.Status == "idle" && unlockedChat.Status == "closed");
        vm.ToggleChatLock(protectedChat);
        vm.CloseChatGroup(vm.ChatGroups.Groups!.Cast<CollectionViewGroup>().Single(g => g.Items.Contains(protectedChat)));
        Check("unlocking then closing the group removes its final row", !vm.Chats.Contains(protectedChat));
    }

    private static ChatViewModel Chat(MainViewModel vm, string folder, string title, string id, DateTimeOffset reply)
    {
        var cwd = Path.Combine(_root, folder);
        Directory.CreateDirectory(cwd);
        AppSettings.Current.ChatMetadata["claude:" + id] = new() { LastActivityAt = reply, LastAssistantAt = reply };
        var chat = new ChatViewModel(cwd, id, title: title, provider: "claude", accountId: "offline-history-test");
        chat.Status = "idle";
        Opened.Add(chat);
        vm.Chats.Add(chat);
        return chat;
    }

    private static void StartupCleanup()
    {
        var settings = AppSettings.Current;
        var path = Path.Combine(_root, "claude", "projects", "fixture", "expired-at-boot.jsonl");
        File.WriteAllText(path, Line(new JsonObject { ["type"] = "user", ["cwd"] = Path.Combine(_root, "expired-folder"),
            ["message"] = new JsonObject { ["content"] = "Old conversation" } }));
        File.SetLastWriteTimeUtc(path, Now.AddDays(-8).UtcDateTime);
        settings.OwnedSessions.Add("expired-at-boot");
        settings.OpenChats.Add(new() { SessionId = "expired-at-boot", Cwd = Path.Combine(_root, "expired-folder") });
        settings.RecentDirectories.Add(new() { Cwd = Path.Combine(_root, "expired-folder"), LastUsed = Now.AddDays(-8) });
        settings.Save();
        var vm = new MainViewModel();
        vm.LoadProjects(); // Exercise the real startup overlap: an initial scan may already be in flight.
        Wait(vm.InitializeChatHistoryAsync());
        Check("startup expires existing history before restore", settings.DeletedSessions.Contains("expired-at-boot") && settings.OpenChats.Count == 0);
        Check("empty old folder is absent from both lists", vm.Projects.Count == 0 && vm.ChatGroups.IsEmpty);
        Check("provider transcript is not deleted with the app history entry", File.Exists(path));
        settings.Save();
        var disk = JsonNode.Parse(File.ReadAllText(Path.Combine(AppSettings.Dir, "settings.json")))!;
        Check("saving again cannot resurrect expired tab", disk["OpenChats"]!.AsArray().Count == 0);
        Check("saving again cannot resurrect expired directory suggestion", disk["RecentDirectories"]!.AsArray().Count == 0);
        var timer = (DispatcherTimer)typeof(MainViewModel).GetField("_historyCleanupTimer", Hidden)!.GetValue(vm)!;
        Check("one six-hour maintenance timer is running", timer.IsEnabled && timer.Interval == TimeSpan.FromHours(6));
        timer.Stop();
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static SessionEntry Entry(string id, string directory, int days) => new(id, id, Path.Combine(_root, directory), Now.AddDays(-days).LocalDateTime, null);
    private static string Line(JsonObject value) => value.ToJsonString() + "\n";
    private static void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); _checks++; }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static int CleanupExisting()
    {
        var settings = AppSettings.Current;
        var allowed = settings.ShowOnlyOwnedSessions ? settings.OwnedSessions.Except(settings.DeletedSessions, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var sessions = SessionCatalog.ListProjects(int.MaxValue, allowed).SelectMany(p => p.Sessions).ToList();
        // The installed app is still running the previous build; leave its open panes to the new startup sweep.
        var protectedIds = settings.OpenChats.Select(c => c.SessionId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in settings.SavedBridges.Where(b => b.ReconnectOnStartup is not false))
        {
            protectedIds.Add(bridge.HostSessionId);
            protectedIds.UnionWith(bridge.Peers.Select(p => p.SessionId).OfType<string>());
        }
        var source = Path.Combine(AppSettings.Dir, "settings.json");
        var backup = Path.Combine(AppSettings.Dir, "settings.before-chat-retention-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        if (File.Exists(source)) File.Copy(source, backup, overwrite: false);
        var removed = ChatHistoryRetention.Apply(settings, sessions, DateTimeOffset.UtcNow, protectedIds);
        if (settings.TrySave() is { } error) { Console.Error.WriteLine(error.Message); return 1; }
        Console.WriteLine($"Removed {removed.Count} expired chats from VibeCode history. Protected {protectedIds.Count} open or connected sessions.");
        Console.WriteLine("Settings backup: " + backup);
        return 0;
    }
}
