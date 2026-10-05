using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyChatMetadata()
    {
        var (vm, team) = Team("chat-metadata", 2);
        vm.BridgePanes.Clear(); // Both are ordinary chats, with the real built-in MCP handler.
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        var chat = team[0]; var other = team[1];
        chat.Status = other.Status = "idle";
        Property(vm, "ActiveChat", other);
        AppSettings.Current.OwnedSessions.Add(chat.SessionId!);
        chat.Items.Add(new TextItem { Text = "Work and transcript that must survive accidental removal." });
        var count = chat.Items.Count;
        Check("existing chats default to unlocked", !chat.IsLocked && chat.CanCloseChat);
        vm.ToggleChatLock(chat);
        Check("lock updates the menu and close action without selecting the chat", chat.IsLocked && !chat.CanCloseChat && chat.ChatLockLabel == "Unlock chat" && ReferenceEquals(vm.ActiveChat, other));
        vm.CloseChat(chat); vm.DeleteChat(chat); vm.RemoveBridgePane(chat);
        Check("locked chat survives all removal entry points", vm.Chats.Contains(chat) && chat.Status == "idle" && !AppSettings.Current.DeletedSessions.Contains(chat.SessionId!));
        Check("blocked removal preserves transcript and ownership", chat.Items.OfType<TextItem>().Any(t => t.Text.Contains("must survive")) && AppSettings.Current.OwnedSessions.Contains(chat.SessionId!));
        Check("repeated blocked actions produce one useful notice", chat.Items.Count == count + 1);
        Check("hover text includes the full working directory and lock status", chat.ChatDirectoryToolTip.Contains(chat.Cwd) && chat.ChatDirectoryToolTip.Contains("Locked"));
        var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("VIBECODE_DATA_DIR")!, "settings.json");
        var disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))!;
        Check("locking saves both open-chat and session metadata immediately", disk.OpenChats.Single(c => c.SessionId == chat.SessionId).Metadata!.IsLocked && disk.ChatMetadata[ChatMetadata.Key(chat.Provider, chat.SessionId!)].IsLocked);
        var resumed = new ChatViewModel(chat.Cwd, resume: chat.SessionId, provider: chat.Provider);
        Chats.Add(resumed);
        Check("resuming a locked session restores its protection", resumed.IsLocked);
        Check("locked chats still accept normal work", chat.Send("Continue checking the existing work."));
        PumpUntil(() => Session(chat).Sent.Count == 1);
        FinishWork(chat, "The existing work is intact.");
        Check("an unnamed chat requests its title in the current turn", Session(chat).Sent.Last().Contains("[VIBECODE CHAT TITLE]"));
        Check("the naming reminder stays out of resumed user messages", (string)Call(chat, "StripInjectedPrelude",
            "[VIBECODE CHAT TITLE]\nName this chat.\n\nMy actual request.")! == "My actual request.");

        var pipe = (string)Property(Call(chat, "EnsureBridgeMcp")!, "PipeName")!;
        var staleView = new ChatViewModel(chat.Cwd, resume: chat.SessionId, provider: chat.Provider);
        Chats.Add(staleView); Call(vm, "Track", staleView);
        var renamed = BridgeMcpClient.InvokeAsync(pipe, "chat_set_title", new() { ["title"] = "  Protect\n important\t chats  " });
        PumpUntil(() => renamed.IsCompleted);
        Check("real MCP transport names an ordinary locked chat", renamed.GetAwaiter().GetResult()["applied"]!.GetValue<bool>() && chat.Title == "Protect important chats" && chat.IsLocked);
        Check("naming is scoped to the caller", other.Title != chat.Title);
        Check("a view opened before naming cannot overwrite the saved title",
            !Tool(staleView, "chat_set_title", new() { ["title"] = "Stale view replacement" })["applied"]!.GetValue<bool>() && staleView.Title == chat.Title);
        var repeated = Tool(chat, "chat_set_title", new() { ["title"] = "Different task now" });
        Check("a second AI naming call cannot change the first title", !repeated["applied"]!.GetValue<bool>() && chat.Title == "Protect important chats");
        Check("a repeated call explains that only the user can rename", repeated["reason"]!.ToString().Contains("Only the user"));
        Call(chat, "SetGeneratedChatTitle", "Bypass the MCP guard");
        Check("the view model also enforces one AI title", chat.Title == "Protect important chats");
        Reject("six-word AI titles are rejected", () => Tool(chat, "chat_set_title", new() { ["title"] = "one two three four five six" }));
        Reject("empty AI titles are rejected", () => Tool(chat, "chat_set_title", new() { ["title"] = " \n " }));
        Reject("oversized AI titles are rejected", () => Tool(chat, "chat_set_title", new() { ["title"] = new string('x', 101) }));
        Reject("control characters are rejected", () => Tool(chat, "chat_set_title", new() { ["title"] = "Bad\0name" }));
        Reject("a model cannot target a different chat", () => Tool(chat, "chat_set_title", new() { ["title"] = "Other chat", ["recipient"] = other.SessionId }));
        Check("invalid titles leave the saved name intact", chat.Title == "Protect important chats");
        chat.Send("Continue the same task.");
        PumpUntil(() => Session(chat).Sent.Count == 2);
        FinishWork(chat, "Continued the task.");
        Check("named chats get a preserve-title notice instead of a naming request",
            !Session(chat).Sent.Last().Contains("This chat still needs") && Session(chat).Sent.Last().Contains("Do not call chat_set_title"));
        disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))!;
        Check("AI names persist in the open chat and history metadata", disk.OpenChats.Single(c => c.SessionId == chat.SessionId).Title == chat.Title && disk.ChatMetadata[ChatMetadata.Key(chat.Provider, chat.SessionId!)].Title == chat.Title);
        var namedResume = new ChatViewModel(chat.Cwd, resume: chat.SessionId, title: "Old provider title", provider: chat.Provider);
        Chats.Add(namedResume);
        Check("app names survive reopening from an old provider transcript title", namedResume.Title == chat.Title && namedResume.IsLocked);
        Call(vm, "Track", namedResume);
        Check("AI title protection survives reopening", !Tool(namedResume, "chat_set_title", new() { ["title"] = "Change after reopening" })["applied"]!.GetValue<bool>() && namedResume.Title == chat.Title);
        var oldTitle = other.Title;
        using (var held = File.Open(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Reject("storage failure cannot report a saved name", () => Tool(other, "chat_set_title", new() { ["title"] = "Unsaved name" }));
        Check("failed name persistence restores the previous title", other.Title == oldTitle);
        Check("a failed save does not consume the one successful naming call", !(bool)Property(other, "HasGeneratedTitle")!);
        vm.RenameChat(chat, "My exact manually chosen working title");
        var manual = Tool(chat, "chat_set_title", new() { ["title"] = "Automatic replacement" });
        Check("AI naming preserves manual titles even above five words", !manual["applied"]!.GetValue<bool>() && chat.Title == "My exact manually chosen working title");
        vm.RenameChat(chat, "My second manually chosen working title");
        Check("the user can rename a chat repeatedly", chat.Title == "My second manually chosen working title");

        var namingTool = "mcp__" + McpCatalog.RuntimeServerName(new McpServerDefinition { Id = "vibecode-bridge-v1", Name = "bridge" }) + "__chat_set_title";
        chat.SetMode("plan");
        var permissions = chat.Items.OfType<PermItem>().Count();
        Call(chat, "OnPermissionRequested", new VibeCode.Protocol.PermissionRequest
        { RequestId = "title-permission", ToolName = namingTool, Input = new JsonObject { ["title"] = "Name this chat" } });
        Check("the built-in title tool does not require permission in Plan mode", chat.Items.OfType<PermItem>().Count() == permissions
            && Session(chat).PermissionResponses.Last()["behavior"]!.ToString() == "allow");
        Call(chat, "OnPermissionRequested", new VibeCode.Protocol.PermissionRequest
        { RequestId = "untrusted-title-permission", ToolName = "mcp__untrusted__chat_set_title", Input = new JsonObject { ["title"] = "Name this chat" } });
        Check("a third-party lookalike naming tool still requires permission", chat.Items.OfType<PermItem>().Count() == permissions + 1);
        var manualResume = new ChatViewModel(chat.Cwd, resume: chat.SessionId, provider: chat.Provider);
        Chats.Add(manualResume); vm.Chats.Add(manualResume); Call(vm, "Track", manualResume);
        Check("manual title protection survives reopening", !Tool(manualResume, "chat_set_title", new() { ["title"] = "Still protected" })["applied"]!.GetValue<bool>() && manualResume.Title == chat.Title);
        vm.Chats.Remove(manualResume);

        var blank = new ChatViewModel(chat.Cwd, provider: "codex");
        Chats.Add(blank); vm.Chats.Add(blank); Call(vm, "Track", blank);
        vm.ToggleChatLock(blank);
        disk = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))!;
        Check("a locked new chat survives snapshots before it has a provider ID or draft", disk.OpenChats.Any(c => c.SessionId is null && c.Metadata?.IsLocked == true));
        var restoredBlank = new ChatViewModel(chat.Cwd, provider: "codex");
        Chats.Add(restoredBlank);
        Call(restoredBlank, "RestoreChatMetadata", disk.OpenChats.Single(c => c.SessionId is null).Metadata);
        Check("a never-started chat restores its lock", restoredBlank.IsLocked);
        blank.SessionId = "pending-chat-now-connected";
        Check("a lock follows a new provider session ID", AppSettings.Current.ChatMetadata[ChatMetadata.Key(blank.Provider, blank.SessionId)].IsLocked);

        var baseline = new Dictionary<string, ChatMetadata> { ["codex:a"] = new() { IsLocked = true } };
        var mine = new Dictionary<string, ChatMetadata> { ["codex:a"] = new() { IsLocked = false } };
        var theirs = new Dictionary<string, ChatMetadata>(baseline) { ["codex:b"] = new() { Title = "Another window's title" } };
        var merged = (Dictionary<string, ChatMetadata>)typeof(ChatMetadata).GetMethod("Merge", Flags)!.Invoke(null, [mine, theirs, baseline])!;
        Check("settings merge retains explicit unlocks and other windows' chat names", !merged["codex:a"].IsLocked && merged.ContainsKey("codex:b"));
        var replacement = new ChatViewModel(chat.Cwd, provider: "codex");
        Chats.Add(replacement); Call(vm, "ReplaceLivePane", chat, replacement);
        Check("replacing a provider session preserves lock and manual title", replacement.IsLocked && replacement.Title == chat.Title);

        RenderChatMetadata(vm, chat, other);
        vm.ToggleChatLock(chat);
        vm.CloseChat(chat);
        Check("explicit unlock restores ordinary close behavior", !vm.Chats.Contains(chat) && chat.Status == "closed");
        Check("closing an unlocked chat does not tombstone its transcript", !AppSettings.Current.DeletedSessions.Contains(chat.SessionId!));
        vm.ToggleChatLock(other); vm.ToggleChatLock(other);
        vm.DeleteChat(other);
        Check("explicit unlock restores deliberate deletion", !vm.Chats.Contains(other) && AppSettings.Current.DeletedSessions.Contains(other.SessionId!));
        VerifyClaudeNaming(vm, chat.Cwd);
        VerifyAllProviderNaming(vm, chat.Cwd);
    }

    private static void VerifyClaudeNaming(MainViewModel vm, string directory)
    {
        var claude = new ChatViewModel(directory, provider: "claude") { Status = "idle", SessionId = Guid.NewGuid().ToString("N") };
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(claude, new FakeSession());
        typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(claude, true);
        Chats.Add(claude); vm.Chats.Add(claude); Call(vm, "Track", claude);
        var endpoint = Call(claude, "EnsureBridgeMcp")!;
        var registration = (McpServerDefinition)Call(endpoint, "Registration")!;
        var fullName = "mcp__" + registration.RuntimeName + "__chat_set_title";
        var options = new VibeCode.Protocol.ClaudeSessionOptions
        {
            Cwd = directory, PermissionMode = "plan", McpServers = [registration],
            AppendSystemPrompt = BridgeMcpTools.ChatTitleInstructions,
        };
        var start = (System.Diagnostics.ProcessStartInfo)typeof(VibeCode.Protocol.ClaudeSession).GetMethod("CreateStartInfo", Flags)!.Invoke(null, [options])!;
        var args = start.ArgumentList.ToList();
        var allowed = args.IndexOf("--allowedTools");
        Check("Claude launch approves the exact built-in naming tool", allowed >= 0 && args[allowed + 1] == fullName);
        Check("Claude naming permission does not grant every bridge tool", args[allowed + 1] != "mcp__" + registration.RuntimeName);
        Check("Claude receives name-once instructions before starting work", args[args.IndexOf("--append-system-prompt") + 1].Contains("BEFORE starting the task")
            && args[args.IndexOf("--append-system-prompt") + 1].Contains("Never rename it later"));
        var config = JsonNode.Parse(File.ReadAllText(args[args.IndexOf("--mcp-config") + 1]))!;
        Check("Claude's MCP registration matches the tool identity in the reminder", config["mcpServers"]![registration.RuntimeName] is not null);
        var dialogue = (System.Diagnostics.ProcessStartInfo)typeof(VibeCode.Protocol.ClaudeSession).GetMethod("CreateStartInfo", Flags)!.Invoke(null,
            [new VibeCode.Protocol.ClaudeSessionOptions { Cwd = directory, McpServers = [registration], DialogueOnly = true }])!;
        Check("Jarvis dialogue sessions do not gain chat-naming tool permission", !dialogue.ArgumentList.Contains("--allowedTools"));
        claude.SetMode("plan");
        claude.Send("Please repair the login form validation.");
        PumpUntil(() => Session(claude).Sent.Count == 1);
        Check("Claude gets the exact tool name and discovery guidance in the user turn", Session(claude).Sent[0].Contains(fullName)
            && Session(claude).Sent[0].Contains("FIRST tool call") && Session(claude).Sent[0].Contains("discover it with tool search"));
        Call(claude, "OnPermissionRequested", new VibeCode.Protocol.PermissionRequest
        { RequestId = "claude-title", ToolName = fullName, Input = new JsonObject { ["title"] = "Repair login validation" } });
        Check("Claude naming bypasses Ask/Plan permission cards", Session(claude).PermissionResponses.Last()["behavior"]!.ToString() == "allow"
            && !claude.Items.OfType<PermItem>().Any());
        var named = BridgeMcpClient.InvokeAsync((string)Property(endpoint, "PipeName")!, "chat_set_title", new() { ["title"] = "Repair login validation" });
        PumpUntil(() => named.IsCompleted);
        Check("Claude's ordinary chat endpoint applies the summary title", named.GetAwaiter().GetResult()["applied"]!.GetValue<bool>() && claude.Title == "Repair login validation");
        var renamed = Tool(claude, "chat_set_title", new() { ["title"] = "New unrelated task" });
        Check("Claude cannot rename the chat on a later task", !renamed["applied"]!.GetValue<bool>() && claude.Title == "Repair login validation");
    }

    private static void RenderChatMetadata(MainViewModel vm, ChatViewModel locked, ChatViewModel unlocked)
    {
        var resources = Application.Current.Resources;
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            resources.MergedDictionaries.Clear();
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{theme}.xaml", UriKind.Relative)));
            var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
            CaptureShell(shell, 1100, 760, "chat-lock-" + theme + ".png");
            var lockedRow = Descendants<Button>((DependencyObject)shell.Content).First(b => ReferenceEquals(b.DataContext, locked) && b.ContextMenu is not null);
            var unlockedRow = Descendants<Button>((DependencyObject)shell.Content).First(b => ReferenceEquals(b.DataContext, unlocked) && b.ContextMenu is not null);
            var menu = lockedRow.ContextMenu;
            menu.PlacementTarget = lockedRow;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(theme + ": context menu targets the right-clicked chat without selecting it", ReferenceEquals(menu.DataContext, locked) && ReferenceEquals(vm.ActiveChat, unlocked));
            Check(theme + ": Delete is disabled on a locked chat", !menu.Items.OfType<MenuItem>().Last().IsEnabled);
            menu.PlacementTarget = unlockedRow;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(theme + ": the shared menu follows a different chat and re-enables Delete", ReferenceEquals(menu.DataContext, unlocked) && menu.Items.OfType<MenuItem>().Last().IsEnabled);
            Check(theme + ": the locked row has a native lock icon", Descendants<TextBlock>(lockedRow).Any(t => t.Text == "\uE72E" && t.Visibility == Visibility.Visible));
            var tip = (ToolTip)lockedRow.ToolTip;
            tip.PlacementTarget = lockedRow;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            tip.Measure(new Size(520, 300)); tip.Arrange(new Rect(tip.DesiredSize)); tip.UpdateLayout();
            Check(theme + ": directory hover does not depend on selecting the chat", ((TextBlock)tip.Content).Text.Contains(locked.Cwd) && ReferenceEquals(vm.ActiveChat, unlocked));
            RenderMetadataPopup(tip, "chat-directory-" + theme + ".png");
            menu.PlacementTarget = lockedRow;
            menu.Measure(new Size(320, 600)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
            RenderMetadataPopup(menu, "chat-menu-" + theme + ".png");
        }
    }

    private static void RenderMetadataPopup(FrameworkElement element, string file)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, file)); png.Save(output);
    }

    private static void RunLiveChatMetadata(bool useClaude = false)
    {
        if (!useClaude && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME"))) throw new InvalidOperationException("Live test requires signed-in CODEX_HOME.");
        var providerName = useClaude ? "Claude" : "Luna";
        AppSettings.Current.DefaultCodexModel = "gpt-6-luna";
        AppSettings.Current.DefaultCodexEffort = "low";
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        var workspace = Path.Combine(_root, "naming-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var vm = new MainViewModel();
        var chat = new ChatViewModel(workspace, provider: useClaude ? "claude" : "codex")
        { Model = useClaude ? "default" : "gpt-6-luna", Effort = useClaude ? null : "low", ExcludeFromMemory = true };
        Chats.Add(chat); vm.Chats.Add(chat); Call(vm, "Track", chat);
        chat.SetMode("plan"); chat.Start();
        LiveWait(() => chat.Status is "idle" or "error", TimeSpan.FromSeconds(45), [chat]);
        Check(providerName + " naming test starts an ordinary chat", chat.Status == "idle" && vm.BridgePanes.Count == 0);
        var calls = new List<JsonObject>();
        var handler = (Func<string, JsonObject, JsonObject>)Property(chat, "BridgeToolHandler")!;
        Property(chat, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
        {
            var result = handler(tool, input);
            calls.Add(new() { ["tool"] = tool, ["input"] = input.DeepClone(), ["result"] = result.DeepClone() });
            Console.WriteLine(tool + " | " + input.ToJsonString());
            return result;
        }));
        vm.ToggleChatLock(chat);
        Check("locked " + providerName + " chat accepts work", chat.Send("Text-only exercise: explain how to validate an order total using 17+25, with the correct answer in one sentence. Do not read or change files, use shell/web tools, or launch agents."));
        LiveWait(() => chat.Status == "idle" && calls.Any(c => c["tool"]!.ToString() == "chat_set_title") && chat.Items.OfType<TextItem>().Any(t => t.Text.Contains("42")), TimeSpan.FromSeconds(120), [chat]);
        Check(providerName + " uses the naming MCP tool within its existing turn", calls.Count(c => c["tool"]!.ToString() == "chat_set_title") == 1);
        Check(providerName + " generates a meaningful title of at most five words", chat.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5 && (chat.Title.Contains("order", StringComparison.OrdinalIgnoreCase) || chat.Title.Contains("total", StringComparison.OrdinalIgnoreCase)));
        Check("automatic naming preserves the lock", chat.IsLocked);
        Check("naming uses no shell or native subagents", chat.Items.OfType<ToolItem>().All(t => !t.Name.Contains("Bash", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("shell", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("spawn_agent", StringComparison.OrdinalIgnoreCase)));
        File.WriteAllText(Path.Combine(_root, "chat-naming-" + providerName.ToLowerInvariant() + ".json"), JsonSerializer.Serialize(new
        { model = chat.Model, title = chat.Title, locked = chat.IsLocked, calls, messages = chat.Items.OfType<TextItem>().Select(t => t.Text) }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
