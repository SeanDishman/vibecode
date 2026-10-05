using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static readonly Type BridgeEndpoint = typeof(MainViewModel).Assembly.GetType("VibeCode.Services.BridgeMcpConnection")!;
    private static string TaskTitleTool(string provider) => (string)BridgeEndpoint.GetMethod("TaskTitleToolNameFor", Flags)!.Invoke(null, [provider])!;
    private static bool TitlePermission(string provider, string tool, JsonNode? input) =>
        (bool)BridgeEndpoint.GetMethod("IsChatTitlePermission", Flags)!.Invoke(null, [provider, tool, input])!;

    private static void VerifyBridgeTaskTitles()
    {
        var contract = BridgeMcpTools.Create((_, _) => new JsonObject()).Single(t => t.Name == "bridge_set_task_title");
        var schema = contract.Definition["inputSchema"]!;
        Check("the task title tool is part of the shared MCP contract with one 40-character title argument",
            schema["properties"]!["title"]!["maxLength"]!.GetValue<int>() == BridgeMcpTools.TaskTitleMaxLength
            && schema["required"]!.AsArray().Single()!.ToString() == "title" && schema["properties"]!.AsObject().Count == 1);
        Check("server instructions keep titles to whole-task changes in regular bridges",
            BridgeMcpTools.Instructions.Contains("bridge_set_task_title") && BridgeMcpTools.Instructions.Contains("never for sub-steps")
            && BridgeMcpTools.Instructions.Contains("Orchestrators and workers skip it"));
        Check("the once-only chat naming rules are unchanged", BridgeMcpTools.ChatTitleInstructions.Contains("You may name a chat only once")
            && BridgeMcpTools.Instructions.StartsWith(BridgeMcpTools.ChatTitleInstructions, StringComparison.Ordinal));

        var (vm, team) = Team("task-title", 3);
        foreach (var chat in team) chat.Status = "idle";
        var agent = team[0];
        Check("a fresh regular pane uses self-titles and shows no placeholder", agent.BridgeHeaderTaskTitle == "" && agent.BridgeTaskName == "Ready"
            && (bool)Property(agent, "UsesBridgeTaskTitle")!);
        Check("first user turn is dispatched", agent.Send("Please repair the login redirect after sign-in."));
        PumpUntil(() => Session(agent).Sent.Count == 1);
        var first = Session(agent).Sent[0];
        Check("an untitled regular pane must title its task in that same turn", first.Contains(BridgeTaskTitlePolicy.Header)
            && first.Contains("Required in this regular bridge") && first.Contains(TaskTitleTool("codex")) && first.Contains("Never retitle for sub-steps")
            && first.Contains("instead of one umbrella title"));
        Check("the chat-name reminder still rides separately", first.Contains("[VIBECODE CHAT TITLE]"));
        var applied = Tool(agent, "bridge_set_task_title", new() { ["title"] = "  Fix login\n redirect " });
        Check("a title is applied, normalized and shown beside the name", applied["applied"]!.GetValue<bool>()
            && agent.BridgeHeaderTaskTitle == "Fix login redirect" && agent.BridgeTaskName == "Fix login redirect");
        var unchanged = Tool(agent, "bridge_set_task_title", new() { ["title"] = "fix LOGIN redirect" });
        Check("repeating the title is a no-op that tells the model to stop", !unchanged["applied"]!.GetValue<bool>()
            && unchanged["reason"]!.ToString().Contains("already your title"));
        Check("each further distinct task in the same turn may retitle", Tool(agent, "bridge_set_task_title", new() { ["title"] = "Verify reset emails" })["applied"]!.GetValue<bool>()
            && Tool(agent, "bridge_set_task_title", new() { ["title"] = "Add password reset" })["applied"]!.GetValue<bool>());
        var fourth = Tool(agent, "bridge_set_task_title", new() { ["title"] = "Narrate a sub-step" });
        Check("a fourth change in one turn is declined without an error", !fourth["applied"]!.GetValue<bool>()
            && fourth["reason"]!.ToString().Contains("3 times") && agent.BridgeHeaderTaskTitle == "Add password reset");
        Reject("five-word task titles are rejected", () => Tool(agent, "bridge_set_task_title", new() { ["title"] = "one two three four five" }));
        Reject("task titles over 40 characters are rejected", () => Tool(agent, "bridge_set_task_title", new() { ["title"] = new string('x', 41) }));
        Reject("control characters are rejected", () => Tool(agent, "bridge_set_task_title", new() { ["title"] = "Bad\0title" }));
        Reject("a model cannot title another pane", () => Tool(agent, "bridge_set_task_title", new() { ["title"] = "Other pane", ["recipient"] = team[1].BridgeAgentId }));
        var placeholder = Tool(team[1], "bridge_set_task_title", new() { ["title"] = "ready" });
        Check("the roster placeholder cannot become a title", !placeholder["applied"]!.GetValue<bool>() && team[1].BridgeHeaderTaskTitle == "");
        Check("titles are scoped to the caller", team[1].BridgeTaskName == "Ready" && team[2].BridgeTaskName == "Ready");
        FinishWork(agent, "Repaired the redirect.");
        Check("follow-up turn is dispatched", agent.Send("Also double-check the redirect for expired sessions."));
        PumpUntil(() => Session(agent).Sent.Count == 2);
        var second = Session(agent).Sent[1];
        Check("a titled pane is told to keep its title unless the overall task changes", second.Contains(BridgeTaskTitlePolicy.Header)
            && second.Contains("Your pane title is") && second.Contains("Add password reset") && !second.Contains("Required in this regular bridge")
            && second.Contains("pick the case that fits") && second.Contains("Keep the title; no call is needed") && second.Contains("Several distinct tasks")
            && second.Contains(TaskTitleTool("codex")));
        Check("the per-turn change cap resets on the next user turn", Tool(agent, "bridge_set_task_title", new() { ["title"] = "Check expired sessions" })["applied"]!.GetValue<bool>());
        FinishWork(agent, "Checked expired sessions.");
        Check("the title reminder is stripped from resumed user messages", (string)Call(agent, "StripInjectedPrelude",
            BridgeTaskTitlePolicy.TurnReminder("Fix login redirect", "codex") + "\n\nMy actual request.")! == "My actual request.");
        Check("visible prompt rows keep only the typed text", agent.Items.OfType<UserItem>().All(u => !u.Text.Contains(BridgeTaskTitlePolicy.Header)));
        var listTool = TaskTitleTool("codex").Replace("bridge_set_task_title", "bridge_list_agents");
        Call(agent, "IngestMessagePayload", "assistant", new JsonObject { ["content"] = new JsonArray(
            new JsonObject { ["type"] = "tool_use", ["id"] = "title-card", ["name"] = TaskTitleTool("codex"), ["input"] = new JsonObject { ["title"] = "Fix login" } },
            new JsonObject { ["type"] = "tool_use", ["id"] = "roster-card", ["name"] = listTool, ["input"] = new JsonObject() }) }, null, true, null);
        var shownTools = agent.Items.OfType<ToolItem>().Concat(agent.Items.OfType<CompactToolGroupItem>().SelectMany(g => g.Tools)).ToArray();
        Check("title calls never add a transcript card; the header is their display", shownTools.All(t => t.Id != "title-card") && shownTools.Any(t => t.Id == "roster-card"));
        Call(agent, "IngestMessagePayload", "assistant", new JsonObject { ["content"] = new JsonArray(
            new JsonObject { ["type"] = "tool_use", ["id"] = "lookalike-card", ["name"] = "mcp__untrusted__bridge_set_task_title", ["input"] = new JsonObject { ["title"] = "x" } }) }, null, true, null);
        Check("a lookalike third-party title tool still shows its card", agent.Items.OfType<CompactToolGroupItem>().SelectMany(g => g.Tools).Any(t => t.Id == "lookalike-card")
            || agent.Items.OfType<ToolItem>().Any(t => t.Id == "lookalike-card"));
        Check("the activity summary still accepts a task_name for compatibility", Tool(team[2], "bridge_report_activity", new()
            { ["summary"] = "Reviewing the checkout flow", ["task_name"] = "Review checkout" })["task_name"]!.ToString() == "Review checkout"
            && team[2].BridgeHeaderTaskTitle == "Review checkout");
        Call(vm, "SaveBridge", vm.BridgePanes, ".vibecode-bridge.md");
        var saved = AppSettings.Current.SavedBridges.Single(s => s.HostSessionId == agent.SessionId);
        Check("saved bridges keep each agent's title", saved.HostTaskName == "Check expired sessions" && saved.Peers.Any(p => p.TaskName == "Review checkout"));

        var (avm, advanced) = Team("task-title-advanced", 2);
        foreach (var chat in advanced) chat.Status = "idle";
        Property(advanced[0], "IsBridgeManager", true);
        Property(advanced[0], "BridgeCoordinatesOnly", true);
        avm.AssignBridgeWorker(advanced[1], advanced[0]);
        foreach (var chat in advanced) { chat.Items.Clear(); chat.Status = "idle"; }
        Check("Advanced Bridge orchestrators and workers never self-title", !(bool)Property(advanced[0], "UsesBridgeTaskTitle")! && !(bool)Property(advanced[1], "UsesBridgeTaskTitle")!);
        var before = Session(advanced[1]).Sent.Count;
        Check("an advanced worker turn is dispatched", advanced[1].Send("Implement the search results page."));
        PumpUntil(() => Session(advanced[1]).Sent.Count == before + 1);
        Check("advanced workers get no task-title reminder", !Session(advanced[1]).Sent.Last().Contains(BridgeTaskTitlePolicy.Header));
        var workerTitle = Tool(advanced[1], "bridge_set_task_title", new() { ["title"] = "Search results page" });
        var managerTitle = Tool(advanced[0], "bridge_set_task_title", new() { ["title"] = "Coordinate search work" });
        Check("advanced panes are declined with a reason, not an error", !workerTitle["applied"]!.GetValue<bool>() && !managerTitle["applied"]!.GetValue<bool>()
            && workerTitle["reason"]!.ToString().Contains("Advanced Bridge") && advanced[0].BridgeTaskName != "Coordinate search work");

        foreach (var provider in new[] { "claude", "codex", "kimi", "glm" })
            Check(provider + " skips permission only for the exact built-in task-title tool", TitlePermission(provider, TaskTitleTool(provider), new JsonObject { ["title"] = "Fix login" }));
        Check("grok skips permission only for an inline use_tool call to the exact task-title tool", TitlePermission("grok", "use_tool",
            new JsonObject { ["tool_name"] = TaskTitleTool("grok"), ["tool_input"] = new JsonObject { ["title"] = "Fix login" } }));
        Check("a lookalike third-party title tool still needs permission", !TitlePermission("claude", "mcp__untrusted__bridge_set_task_title", null)
            && !TitlePermission("grok", "use_tool", new JsonObject { ["tool_name"] = "untrusted__bridge_set_task_title", ["tool_input"] = new JsonObject { ["title"] = "x" } }));
        agent.SetMode("plan");
        var cards = agent.Items.OfType<PermItem>().Count();
        Call(agent, "OnPermissionRequested", new VibeCode.Protocol.PermissionRequest
            { RequestId = "task-title-permission", ToolName = TaskTitleTool(agent.Provider), Input = new JsonObject { ["title"] = "Fix login" } });
        Check("the task-title tool never shows a permission card in Plan mode", agent.Items.OfType<PermItem>().Count() == cards
            && Session(agent).PermissionResponses.Last()["behavior"]!.ToString() == "allow");
        var registration = (McpServerDefinition)Call(Call(agent, "EnsureBridgeMcp")!, "Registration")!;
        var start = (ProcessStartInfo)typeof(VibeCode.Protocol.ClaudeSession).GetMethod("CreateStartInfo", Flags)!.Invoke(null,
            [new VibeCode.Protocol.ClaudeSessionOptions { Cwd = agent.Cwd, PermissionMode = "plan", McpServers = [registration] }])!;
        var launch = start.ArgumentList.ToList();
        var allowed = launch.IndexOf("--allowedTools");
        Check("Claude pre-approves exactly the chat-name and task-title tools", allowed >= 0 && launch[allowed + 1].EndsWith("__chat_set_title")
            && launch[allowed + 2] == TaskTitleTool("claude") && launch[allowed + 3].StartsWith("--", StringComparison.Ordinal));
        Check("Codex approves the task-title tool through the bridge projection", McpCatalog.BuildCodexProjection([registration]).ConfigOverrides
            .Any(c => c.Contains("bridge_set_task_title") && c.Contains("approval_mode")));
        VerifyHostTaskTitle();
    }

    private static void SetRates(ChatViewModel chat, string text)
    {
        typeof(ChatViewModel).GetField("_tokenRatesText", Flags)!.SetValue(chat, text);
        typeof(ChatViewModel).GetField("_hasRecentTokenUsage", Flags)!.SetValue(chat, true);
        Call(chat, "Raise", nameof(ChatViewModel.TokenRatesText));
        Call(chat, "Raise", nameof(ChatViewModel.HasTokenRates));
    }

    private static FrameworkElement StripChild(BridgeHeaderStrip strip, string path) => strip.Children.OfType<FrameworkElement>().Single(child =>
        child is TextBlock text && BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path.Path == path);
    private static FrameworkElement StripTitle(BridgeHeaderStrip strip) => strip.Children.OfType<FrameworkElement>()
        .Single(child => BridgeHeaderStrip.GetSlot(child) == BridgeHeaderStrip.Slot.Title);
    private static FrameworkElement StripInfo(BridgeHeaderStrip strip) => strip.Children.OfType<FrameworkElement>().Single(child => child.Name == "PaneInfoButton");
    // The strip hides a child by giving it an empty layout slot (its layout clip), so ActualWidth still reports the
    // desired width. What the user sees is the slot.
    private static Rect Slot(FrameworkElement element) => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(element);
    private static bool Shown(FrameworkElement element) => Slot(element).Width > 0.5;

    /// <summary>Checks invariants that must hold at any width, and returns the strip for width-specific checks.</summary>
    private static BridgeHeaderStrip CheckHeader(MainWindow shell, ChatViewModel pane, string label)
    {
        var strip = Descendants<BridgeHeaderStrip>((DependencyObject)shell.Content).Single(s => ReferenceEquals(s.DataContext, pane));
        var header = (FrameworkElement)VisualTreeHelper.GetParent(strip);
        bool Whole(FrameworkElement child) => Math.Abs(Slot(child).Width - child.DesiredSize.Width) < 0.5;
        var shown = strip.Children.OfType<FrameworkElement>().Where(Shown).ToArray();
        var contentRight = shown.Length == 0 ? 0 : shown.Max(c => Slot(c).Right);
        var buttons = header is DockPanel dock ? dock.Children.OfType<FrameworkElement>().Where(c => !ReferenceEquals(c, strip) && c.ActualWidth > 0).ToArray() : [];
        var buttonsLeft = buttons.Length == 0 ? double.MaxValue : buttons.Min(b => b.TransformToAncestor(header).Transform(new Point(0, 0)).X);
        Console.WriteLine($"{label}: strip {strip.ActualWidth:0.#}px, content {contentRight:0.#}px, compact={strip.IsCompact}");
        foreach (var child in strip.Children.OfType<FrameworkElement>())
            Console.WriteLine($"    {BridgeHeaderStrip.GetSlot(child),-8} {child.GetType().Name,-10} {child.Visibility,-9} slot={Slot(child).Width:0.##} desired={child.DesiredSize.Width:0.##} {(child as TextBlock)?.Text?.Replace('\n', '/')}");
        Check(label + ": header content never runs under the pane buttons", contentRight <= strip.ActualWidth + 0.5
            && strip.TransformToAncestor(header).Transform(new Point(contentRight, 0)).X <= buttonsLeft + 0.5);
        Check(label + ": the agent name is never trimmed", Whole(StripChild(strip, "BridgeLabel")));
        Check(label + ": the header stays one line", strip.ActualHeight < 26);
        var info = StripInfo(strip);
        Check(label + ": the info icon takes keyboard focus only while it shows", info.Focusable == strip.IsCompact);
        var wide = strip.Children.OfType<FrameworkElement>().Where(c => BridgeHeaderStrip.GetSlot(c) == BridgeHeaderStrip.Slot.Wide).ToArray();
        if (strip.IsCompact)
            Check(label + ": compact headers fold the readout into a whole info icon", Shown(info) && Whole(info) && wide.All(w => !Shown(w)));
        else
            Check(label + ": a header with room shows its full readout and no info icon", !Shown(info) && !Shown(StripChild(strip, nameof(ChatViewModel.WorkingStatus)))
                && wide.Where(w => w.Visibility == Visibility.Visible).All(w => Shown(w) && Whole(w)));
        return strip;
    }

    private static void RenderHeader(BridgeHeaderStrip strip, string file)
    {
        var header = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(strip));
        const double scale = 2;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(header), null, new Rect(0, 0, header.ActualWidth, header.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(header.ActualWidth * scale), (int)Math.Ceiling(header.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, file)); png.Save(output);
        Console.WriteLine("Rendered " + file);
    }

    private static ToolTip OpenInfoCard(BridgeHeaderStrip strip)
    {
        var info = StripInfo(strip);
        var tip = (ToolTip)info.ToolTip;
        tip.PlacementTarget = info;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        tip.Measure(new Size(420, 600)); tip.Arrange(new Rect(tip.DesiredSize)); tip.UpdateLayout();
        return tip;
    }

    private static string CardText(ToolTip tip) => string.Join("\n", Descendants<TextBlock>(tip)
        .Where(t => t.IsVisible || IsInVisibleRow(t)).Select(t => t.Text));
    private static bool IsInVisibleRow(TextBlock text)
    {
        for (DependencyObject? node = text; node is not null and not ToolTip; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static void VerifyBridgeHeaderStrip()
    {
        var resources = Application.Current.Resources;
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            resources.MergedDictionaries.Clear();
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{theme}.xaml", UriKind.Relative)));
            var (vm, team) = Team("header-" + theme, 3);
            foreach (var chat in team) { chat.Items.Clear(); chat.Status = "idle"; }
            Property(vm, "ActiveChat", team[0]);
            Property(vm, "ShowBridge", true);
            var busy = team[0];
            Tool(busy, "bridge_set_task_title", new() { ["title"] = "Bridge header info hover" });
            Tool(busy, "bridge_report_activity", new() { ["summary"] = "Moving overflowing header stats into a hover card" });
            busy.Status = "running";
            busy.ThinkingTokens = 2_900;
            busy.TotalIn = 4_300_000; busy.TotalOut = 26_400; busy.TotalTokens = 4_326_400; busy.Cost = 3.4512;
            SetRates(busy, "read 1.2k/s · 48.0k/min\nwrite 28/s · 1.6k/min");
            Property(busy, "SupervisionStatus", "working · 6m");
            Tool(team[1], "bridge_set_task_title", new() { ["title"] = "Fix login redirect" });
            team[1].TotalIn = 210_000; team[1].TotalOut = 3_100; team[1].TotalTokens = 213_100; team[1].Cost = 0.42;
            var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);

            CaptureShell(shell, 3000, 820, $"bridge-header-wide-{theme}.png");
            var wide = CheckHeader(shell, busy, theme + " wide busy pane");
            Check(theme + ": with room the busy pane keeps the full inline readout", !wide.IsCompact && Shown(StripChild(wide, nameof(ChatViewModel.WorkingText)))
                && Shown(StripChild(wide, nameof(ChatViewModel.TokensText))) && Shown(StripChild(wide, nameof(ChatViewModel.CostText))) && Shown(StripTitle(wide)));
            CheckHeader(shell, team[1], theme + " wide idle pane");
            var untitled = CheckHeader(shell, team[2], theme + " wide untitled pane");
            Check(theme + ": an untitled pane shows no placeholder title", StripTitle(untitled).Visibility == Visibility.Collapsed);
            RenderHeader(wide, $"bridge-header-wide-strip-{theme}.png");

            CaptureShell(shell, 1500, 820, $"bridge-header-medium-{theme}.png");
            var medium = CheckHeader(shell, busy, theme + " medium busy pane");
            var mediumTitle = (TextBlock)StripTitle(medium);
            var natural = new FormattedText("· Bridge header info hover", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(mediumTitle.FontFamily, mediumTitle.FontStyle, mediumTitle.FontWeight, mediumTitle.FontStretch), mediumTitle.FontSize, Brushes.White, 1).Width;
            Check(theme + ": a title that fits gets its whole natural width, so it is never ellipsized",
                Slot(mediumTitle).Width - mediumTitle.Margin.Left >= natural - 1);
            Check(theme + ": a medium pane shows name · title · thinking · (i)", medium.IsCompact && Shown(StripTitle(medium))
                && ((TextBlock)StripChild(medium, nameof(ChatViewModel.WorkingStatus))).Text == "thinking" && Shown(StripChild(medium, nameof(ChatViewModel.WorkingStatus))));
            RenderHeader(medium, $"bridge-header-compact-strip-{theme}.png");
            var idle = CheckHeader(shell, team[1], theme + " medium idle pane");
            Check(theme + ": idle panes show no status word", !Shown(StripChild(idle, nameof(ChatViewModel.WorkingStatus))));

            CaptureShell(shell, 1000, 760, $"bridge-header-narrow-{theme}.png");
            var narrow = CheckHeader(shell, busy, theme + " narrow busy pane");
            Check(theme + ": the narrowest pane keeps its name and the info icon; the title and status word give way first",
                narrow.IsCompact && Shown(StripInfo(narrow)) && (!Shown(StripChild(narrow, nameof(ChatViewModel.WorkingStatus))) ? !Shown(StripTitle(narrow)) : true));
            RenderHeader(narrow, $"bridge-header-narrow-strip-{theme}.png");

            var tip = OpenInfoCard(narrow);
            var card = CardText(tip);
            Console.WriteLine($"{theme} info card (DataContext {(ReferenceEquals(tip.DataContext, busy) ? "pane" : tip.DataContext?.GetType().Name ?? "null")}):\n" + card);
            Check(theme + ": the info card shows everything the strip dropped", card.Contains("Codex 1") && card.Contains("Bridge header info hover")
                && card.Contains("thinking · 2.9k tokens") && card.Contains("4.3M read · 26.4k write") && card.Contains("write 28/s")
                && card.Contains("$3.4512") && card.Contains("working · 6m") && card.Contains("Moving overflowing header stats"));
            RenderMetadataPopup(tip, $"bridge-header-info-card-{theme}.png");
            Property(busy, "SupervisionAlert", true);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var glyph = Descendants<TextBlock>(StripInfo(narrow)).Single();
            Check(theme + ": a stalled agent's info icon turns amber", ((SolidColorBrush)glyph.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["Amber"]).Color);
            Property(busy, "SupervisionAlert", false);

            busy.ThinkingTokens = 0;
            Check(theme + ": the compact status follows the live state", busy.WorkingStatus == "working…");
            busy.Status = "idle";
            CaptureShell(shell, 3000, 820, $"bridge-header-wide-again-{theme}.png");
            Check(theme + ": widening the window restores the full readout", !CheckHeader(shell, busy, theme + " restored busy pane").IsCompact);
        }
    }

    private static void LiveWaitBudget(Func<bool> done, TimeSpan timeout, ChatViewModel chat, double outputBudget)
    {
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        Exception? error = null;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (chat.TotalOut > outputBudget) throw new InvalidOperationException("Simulation output budget exceeded.");
                if (done() || watch.Elapsed > timeout) frame.Continue = false;
            }
            catch (Exception ex) { error = ex; frame.Continue = false; }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (error is not null) throw error;
        if (!done()) throw new TimeoutException($"Live turn did not finish within {timeout}; status {chat.Status}.");
    }

    private static void RunLiveTaskTitleSimulations()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live simulations require the current signed-in CODEX_HOME.");
        AppSettings.Current.DefaultProvider = "codex";
        AppSettings.Current.DefaultCodexModel = "gpt-6-luna";
        AppSettings.Current.DefaultCodexEffort = "low";
        AppSettings.Current.SecondBrainEnabled = false;
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        var reportPath = Path.Combine(_root, "luna-task-titles-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        var results = new List<object>();
        var failures = new List<string>();
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            model = "gpt-6-luna", effort = "low",
            method = "Real authenticated Luna turns in Codex plan mode (read-only sandbox, no approvals) using the production bridge MCP endpoint and routing. Peers are passive in-process sessions.",
            results, failures,
        }, new JsonSerializerOptions { WriteIndented = true }));

        (ChatViewModel Actor, List<JsonObject> Calls) Live(MainViewModel vm, string cwd, string label)
        {
            var actor = new ChatViewModel(cwd, title: "Bridge task title simulation", provider: "codex")
                { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true, BridgeLabel = label };
            Chats.Add(actor); vm.Chats.Add(actor); vm.BridgePanes.Add(actor); Call(vm, "Track", actor);
            var calls = new List<JsonObject>();
            var handler = (Func<string, JsonObject, JsonObject>)Property(actor, "BridgeToolHandler")!;
            Property(actor, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                var entry = new JsonObject { ["tool"] = tool, ["input"] = input.DeepClone(), ["turn"] = results.Count };
                calls.Add(entry);
                try
                {
                    var result = handler(tool, input); entry["result"] = result.DeepClone();
                    Console.WriteLine(label + " | " + tool + " | " + input.ToJsonString() + " -> " + result.ToJsonString());
                    return result;
                }
                catch (Exception ex) { entry["error"] = ex.Message; Console.WriteLine(label + " | REJECTED " + tool + " | " + ex.Message); throw; }
            }));
            return (actor, calls);
        }

        void Turn(ChatViewModel actor, List<JsonObject> calls, string name, string request, Action<JsonObject[], string[]> verify)
        {
            Console.WriteLine("START: " + name + " / GPT-6 Luna low");
            var watch = Stopwatch.StartNew();
            var firstCall = calls.Count;
            var texts = actor.Items.OfType<TextItem>().Count();
            var (inputBefore, outputBefore) = (actor.TotalIn, actor.TotalOut);
            string? failure = null;
            JsonObject[] titles = [];
            try
            {
                Check(name + ": request submitted", actor.Send(request));
                LiveWaitBudget(() => actor.Status is "idle" or "error" && actor.Items.OfType<TextItem>().Count() > texts && !actor.HasQueued,
                    TimeSpan.FromSeconds(240), actor, 60_000);
                Check(name + ": turn finishes successfully", actor.Status == "idle");
                var turnCalls = calls.Skip(firstCall).ToArray();
                titles = turnCalls.Where(c => c["tool"]!.ToString() == "bridge_set_task_title").ToArray();
                Check(name + ": no rejected bridge tool calls", turnCalls.All(c => c["error"] is null));
                Check(name + ": at most one activity report, so the title is not drowned in status cards",
                    turnCalls.Count(c => c["tool"]!.ToString() == "bridge_report_activity") <= 1);
                Check(name + ": no task-title card appears in the transcript", actor.Items.OfType<ToolItem>()
                    .Concat(actor.Items.OfType<CompactToolGroupItem>().SelectMany(g => g.Tools)).All(t => !t.Name.EndsWith("__bridge_set_task_title", StringComparison.Ordinal)));
                verify(titles, titles.Where(c => c["result"]?["applied"]?.GetValue<bool>() == true).Select(c => c["result"]!["title"]!.ToString()).ToArray());
            }
            catch (Exception ex)
            {
                failure = ex.Message; failures.Add(name + ": " + failure);
                Console.WriteLine("FAILED: " + name + " | " + failure);
            }
            finally
            {
                results.Add(new
                {
                    name, passed = failure is null, failure, request, header_title = actor.BridgeHeaderTaskTitle,
                    title_calls = titles.Select(c => c.DeepClone()).ToArray(), elapsed_seconds = watch.Elapsed.TotalSeconds,
                    input_tokens = actor.TotalIn - inputBefore, output_tokens = actor.TotalOut - outputBefore,
                    bridge_calls = calls.Skip(firstCall).Select(c => c.DeepClone()).ToArray(),
                    tools = actor.Items.OfType<ToolItem>().Select(t => t.Name).ToArray(),
                    reply = actor.Items.OfType<TextItem>().LastOrDefault()?.Text,
                });
                Save();
            }
        }

        static bool Mentions(string title, params string[] words) => words.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
        static int Words(string title) => title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Regular bridge: a live Luna pane beside two passive peers, in a small read-only fixture project.
        var (vm, peers) = Team("luna-task-title", 2);
        foreach (var peer in peers) peer.Status = "idle";
        var cwd = peers[0].Cwd;
        File.WriteAllText(Path.Combine(cwd, "orders.py"), "def order_total(items, discount):\n    subtotal = sum(i['price'] * i['qty'] for i in items)\n    # discount is a fraction such as 0.1 for 10%\n    return subtotal + subtotal * discount\n");
        File.WriteAllText(Path.Combine(cwd, "notes_a.txt"), "TODO: add pagination\nShip the search page\nTODO: cache results\nTODO: write release notes\n");
        File.WriteAllText(Path.Combine(cwd, "notes_b.txt"), "Customers will recieve a confirmation email after checkout.\n");
        var (actor, calls) = Live(vm, cwd, "Codex 3");
        Call(vm, "RefreshBridgeManagerBriefs");
        actor.SetMode("plan"); actor.Start();
        LiveWaitBudget(() => actor.Status is "idle" or "error", TimeSpan.FromSeconds(60), actor, 60_000);
        Check("regular-bridge Luna session starts", actor.Status == "idle" && actor.Model == "gpt-6-luna" && (bool)Property(actor, "UsesBridgeTaskTitle")!);

        Turn(actor, calls, "fresh-multistep-task", "Review orders.py in this project for bugs: read the file, find the defect in the total calculation, and explain the fix in two sentences. Read-only: do not edit files, use the web, or start subagents.",
            (titles, applied) =>
            {
                Check("fresh task: exactly one title call, before or during the work, and no sub-step retitles", titles.Length == 1 && applied.Length == 1);
                Check("fresh task: the title is one to four words about the review", Words(applied[0]) <= 4 && Mentions(applied[0], "order", "total", "bug", "discount", "review"));
                Check("fresh task: the header shows the title", actor.BridgeHeaderTaskTitle == applied[0]);
            });
        var firstTitle = actor.BridgeHeaderTaskTitle;
        // Title cards are never shown, so an unchanged-title call is a silent no-op; what must not happen is a retitle.
        Turn(actor, calls, "same-task-follow-up", "Same review: in one sentence, say whether order_total needs a unit test for the discount. No file changes.",
            (_, applied) => Check("follow-up: the title stays on the same overall task", applied.Length == 0 && actor.BridgeHeaderTaskTitle == firstTitle));
        Turn(actor, calls, "two-distinct-tasks", "Two separate tasks, in order. First: count the TODO lines in notes_a.txt. Second: find the misspelled word in notes_b.txt. Read the files; do not edit anything. Answer both in one short reply.",
            (titles, applied) =>
            {
                Check("two tasks: one applied title per task", applied.Length == 2);
                Check("two tasks: the titles follow the tasks in order", Mentions(applied[0], "todo", "count", "notes_a", "notes a")
                    && Mentions(applied[1], "spell", "typo", "word", "notes_b", "notes b"));
                Check("two tasks: titles stay within four words", applied.All(t => Words(t) <= 4));
            });
        Turn(actor, calls, "new-unrelated-task", "New, unrelated request: in two sentences, explain how you would add a dark mode toggle to a settings page. No file reads or edits are needed.",
            (titles, applied) =>
            {
                Check("new task: exactly one retitle", applied.Length == 1);
                Check("new task: the title names the new work", Words(applied[0]) <= 4 && Mentions(applied[0], "dark", "theme", "mode", "toggle"));
            });

        // Render the live result: the real pane header with Luna's own title at wide and compact widths.
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        Property(vm, "ActiveChat", peers[0]);
        Property(vm, "ShowBridge", true);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        CaptureShell(shell, 3000, 820, "luna-live-header-wide.png");
        RenderHeader(CheckHeader(shell, actor, "live wide"), "luna-live-header-wide-strip.png");
        CaptureShell(shell, 1000, 760, "luna-live-header-narrow.png");
        var liveStrip = CheckHeader(shell, actor, "live narrow");
        RenderHeader(liveStrip, "luna-live-header-narrow-strip.png");
        if (liveStrip.IsCompact) RenderMetadataPopup(OpenInfoCard(liveStrip), "luna-live-info-card.png");
        actor.Close();

        // Advanced Bridge worker: titled by its assignment, so Luna must not self-title.
        var (avm, team) = Team("luna-task-title-advanced", 1);
        team[0].Status = "idle";
        Property(team[0], "IsBridgeManager", true);
        Property(team[0], "BridgeCoordinatesOnly", true);
        var acwd = team[0].Cwd;
        File.Copy(Path.Combine(cwd, "orders.py"), Path.Combine(acwd, "orders.py"));
        var (worker, workerCalls) = Live(avm, acwd, "Codex 2");
        // AssignBridgeWorker refuses a session that is still starting; set the group link the way the broadcast runs do.
        Property(worker, "BridgeCoordinatorAgentId", team[0].BridgeAgentId);
        Call(avm, "RefreshBridgeManagerBriefs");
        worker.SetMode("plan"); worker.Start();
        LiveWaitBudget(() => worker.Status is "idle" or "error", TimeSpan.FromSeconds(60), worker, 60_000);
        Check("advanced worker Luna session starts", worker.Status == "idle" && !(bool)Property(worker, "UsesBridgeTaskTitle")!);
        Turn(worker, workerCalls, "advanced-worker", "Review orders.py for the defect in the total calculation and explain the fix in two sentences. Read-only: do not edit files, use the web, or start subagents.",
            (_, applied) => Check("advanced worker: never self-titled (assignment names stay)", applied.Length == 0 && worker.BridgeHeaderTaskTitle == ""));
        worker.Close();

        Save();
        Console.WriteLine("Live task-title report: " + reportPath);
        Check("all live Luna task-title scenarios pass", failures.Count == 0);
    }
}
