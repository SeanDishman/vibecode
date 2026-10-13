using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
    private static JsonArray UltrafastCatalog(bool entitled = true) => new(
        new[] { "gpt-6-astra", "gpt-6.1-sol", "gpt-6-luna" }.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id,
            ["model"] = id,
            ["displayName"] = id,
            ["serviceTiers"] = entitled && id != "gpt-6-luna"
                ? new JsonArray(new JsonObject { ["id"] = "priority" }, new JsonObject { ["id"] = "ultrafast" })
                : new JsonArray(new JsonObject { ["id"] = "priority" }),
            ["supportedReasoningEfforts"] = new JsonArray(new JsonObject { ["reasoningEffort"] = "high" }),
        }).ToArray());

    private static void LoadUltrafastCatalog(ChatViewModel chat, bool entitled = true)
    {
        using var source = new CodexSession(new() { Cwd = _root });
        Call(source, "BuildModels", UltrafastCatalog(entitled));
        chat.Models.Clear();
        foreach (var row in source.Models.OfType<JsonObject>()) chat.Models.Add(new ModelChoice
        {
            Provider = "codex",
            Value = row["value"]!.ToString(),
            Display = row["displayName"]!.ToString(),
            ResolvedModel = row["resolvedModel"]?.ToString(),
            SupportsFastMode = row["supportsFastMode"]?.GetValue<bool>() ?? false,
            SupportsUltrafastMode = row["supportsUltrafastMode"]?.GetValue<bool>() ?? false,
            SupportsEffort = true,
            SupportsAutoMode = true,
            EffortLevels = ["low", "medium", "high", "xhigh", "max", "ultra"],
        });
        chat.RefreshModelPicker();
    }

    private static void VerifyUltrafast()
    {
        AppSettings.Current.FastMode = AppSettings.Current.CodexUltrafastMode = false;
        VerifyUltrafastProtocol();
        VerifyUltrafastPricing();
        var (vm, chats) = Team("ultrafast", 3);
        foreach (var chat in chats) { LoadUltrafastCatalog(chat); chat.Model = "gpt-6.1-sol"; }
        var selected = chats[1];
        selected.SetUltrafastMode(true);
        Check("Ultrafast belongs only to the selected OpenAI chat", selected.UltrafastMode && !selected.FastMode
            && !chats[0].UltrafastMode && !chats[2].UltrafastMode);
        typeof(MainViewModel).GetField("_sessionRestored", Flags)!.SetValue(vm, true);
        Call(vm, "SnapshotSession");
        var saved = AppSettings.Current.OpenChats.Single(c => c.SessionId == selected.SessionId);
        Check("per-chat Ultrafast is serialized", JsonSerializer.Deserialize<OpenChatState>(JsonSerializer.Serialize(saved))!.UltrafastMode);
        var standard = AppSettings.Current.OpenChats.Single(c => c.SessionId == chats[0].SessionId);
        Check("standard sibling remains standard in saved state", !standard.UltrafastMode && standard.FastMode == false);
        Check("legacy snapshots never opt into a new paid tier", !JsonSerializer.Deserialize<OpenChatState>("{}")!.UltrafastMode
            && !JsonSerializer.Deserialize<SavedBridgePane>("{}")!.UltrafastMode);
        var fresh = new ChatViewModel(_root, provider: "codex"); Chats.Add(fresh);
        Check("only new OpenAI chats inherit the speed seed", fresh.UltrafastMode && !fresh.FastMode);
        Check("a saved preference cannot expose Ultrafast before account availability is confirmed",
            !fresh.ShowUltrafastMode && !fresh.CanUltrafastMode);
        Call(fresh, "BeginTurnPricing");
        Check("an unconfirmed account is estimated at standard rates despite the saved preference",
            typeof(ChatViewModel).GetField("_costTierForTurn", Flags)!.GetValue(fresh)?.ToString() == "standard");
        var claude = new ChatViewModel(_root, provider: "claude"); Chats.Add(claude);
        claude.SetUltrafastMode(true);
        Check("Claude never exposes or enables Ultrafast", !claude.ShowUltrafastMode && !claude.UltrafastMode);
        selected.SetFastMode(true);
        Check("choosing Fast clears Ultrafast", selected.FastMode && !selected.UltrafastMode);
        selected.SetUltrafastMode(true);
        Check("choosing Ultrafast clears Fast", selected.UltrafastMode && !selected.FastMode);
        selected.SetUltrafastMode(false);
        Check("turning Ultrafast off returns to standard", !selected.FastMode && !selected.UltrafastMode);
        selected.Model = "gpt-6-luna"; selected.SetUltrafastMode(true);
        Check("unsupported model cannot enable Ultrafast or change models", !selected.UltrafastMode && selected.Model == "gpt-6-luna");
        selected.Model = "gpt-6.1-sol"; LoadUltrafastCatalog(selected, false); selected.SetUltrafastMode(true);
        Check("an account without entitlement hides Ultrafast on a supported model",
            !selected.CanUltrafastMode && !selected.ShowUltrafastMode && !selected.UltrafastMode);
        LoadUltrafastCatalog(selected); selected.SetUltrafastMode(true);
        Call(selected, "BeginTurnPricing");
        selected.SetFastMode(true);
        Check("a mid-turn speed change cannot reprice the dispatched request", typeof(ChatViewModel).GetField("_costTierForTurn", Flags)!.GetValue(selected)?.ToString() == "ultrafast");
        foreach (var model in new[] { "gpt-6-astra", "gpt-6.1-sol" })
        {
            var standardCost = ModelPricing.TurnCost(model, 1000, 200, 300, 100);
            Check(model + " Ultrafast prices all token buckets at 6x", Math.Abs(ModelPricing.TurnCost(model, 1000, 200, 300, 100, "ultrafast") - 6 * standardCost) < 1e-9);
        }
        VerifyUltrafastViews(vm, chats);
        AppSettings.Current.FastMode = AppSettings.Current.CodexUltrafastMode = false;
    }

    private static void VerifyUltrafastProtocol()
    {
        foreach (var kind in new[] { "start", "resume", "fork" })
        {
            using var session = new CodexSession(new()
            {
                Cwd = _root,
                HomeDirectory = Path.Combine(_root, "home-" + kind),
                Model = "gpt-6.1-sol",
                UltrafastMode = true,
                Resume = kind == "start" ? null : "saved",
                ForkSession = kind == "fork",
            });
            var requests = new List<(string Method, JsonObject Args)>();
            Property(session, "RequestFixture", new Func<string, JsonObject?, Task<JsonNode?>>((method, args) =>
            {
                requests.Add((method, args?.DeepClone().AsObject() ?? new()));
                JsonObject result = method == "model/list" ? new() { ["data"] = UltrafastCatalog() }
                    : new()
                    {
                        ["model"] = "gpt-6.1-sol",
                        ["thread"] = new JsonObject { ["id"] = "ultrafast-" + kind, ["turns"] = new JsonArray() },
                        ["turn"] = new JsonObject { ["id"] = "turn-fixture" }
                    };
                return Task.FromResult<JsonNode?>(new JsonObject { ["result"] = result });
            }));
            var init = (Task)Call(session, "InitializeAsync")!;
            PumpUntil(() => init.IsCompleted); init.GetAwaiter().GetResult();
            Check(kind + " explicitly selects Ultrafast", requests.Single(r => r.Method == "thread/" + kind).Args["serviceTier"]?.ToString() == "ultrafast");
            void Turn(string expected)
            {
                var task = (Task)Call(session, "StartTurnAsync", JsonValue.Create("Local speed fixture"))!;
                PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
                var request = requests.Last(r => r.Method == "turn/start").Args;
                Check(kind + " next turn selects " + expected, request["serviceTier"]?.ToString() == expected && request["threadId"]?.ToString() == session.SessionId);
            }
            Turn("ultrafast");
            session.SetFastModeAsync(true).GetAwaiter().GetResult(); Turn("priority");
            session.SetSpeedAsync(false, true).GetAwaiter().GetResult(); Turn("ultrafast");
            session.SetSpeedAsync(false, false).GetAwaiter().GetResult(); Turn("default");
            session.SetSpeedAsync(false, true).GetAwaiter().GetResult();
            session.SetModelAsync("gpt-6-luna").GetAwaiter().GetResult(); Turn("default");
            session.SetModelAsync("gpt-6.1-sol").GetAwaiter().GetResult();
            Call(session, "BuildModels", UltrafastCatalog(false)); Turn("default");
        }
        var supports = typeof(CodexSession).GetMethod("SupportsUltrafastTier", Flags)!;
        bool Cap(JsonObject entry) => (bool)supports.Invoke(null, [entry])!;
        Check("legacy speed metadata supports Ultrafast", Cap(new() { ["additional_speed_tiers"] = new JsonArray("ultrafast") }));
        Check("an explicit empty account tier list denies Ultrafast", !Cap(new() { ["id"] = "gpt-6-astra", ["serviceTiers"] = new JsonArray() }));
        Check("model names never substitute for account entitlement",
            !Cap(new() { ["id"] = "gpt-6-astra" }) && !Cap(new() { ["id"] = "gpt-6.1-sol" }));
        using var unknown = new CodexSession(new() { Cwd = _root, Model = "gpt-6.1-sol", UltrafastMode = true });
        Check("missing catalog cannot send a saved Ultrafast preference",
            Call(unknown, "ServiceTierFor", "gpt-6.1-sol")?.ToString() == "default");
        Call(unknown, "BuildModels", new JsonArray());
        Check("synthetic and offline catalog rows do not grant Ultrafast", unknown.Models.OfType<JsonObject>()
            .All(row => row["supportsUltrafastMode"]?.GetValue<bool>() != true)
            && Call(unknown, "ServiceTierFor", "gpt-6.1-sol")?.ToString() == "default");
    }

    private static void VerifyUltrafastPricing()
    {
        using var session = new CodexSession(new() { Cwd = _root, Model = "gpt-6.1-sol", UltrafastMode = true });
        Property(session, "SessionId", "ultrafast-price");
        Call(session, "RememberTurnPricing", new JsonObject { ["threadId"] = "ultrafast-price", ["model"] = "gpt-6.1-sol", ["serviceTier"] = "ultrafast" });
        var messages = new List<JsonNode>(); session.MessageReceived += m => messages.Add(m.DeepClone());
        void Usage(int count, string? returnedTier = null) => Call(session, "CaptureUsage", new JsonObject
        {
            ["serviceTier"] = returnedTier,
            ["tokenUsage"] = new JsonObject
            {
                ["last"] = new JsonObject { ["inputTokens"] = 1000, ["cachedInputTokens"] = 300, ["outputTokens"] = 100 },
                ["total"] = new JsonObject { ["inputTokens"] = 1000 * count, ["cachedInputTokens"] = 300 * count, ["outputTokens"] = 100 * count },
            },
        }, "ultrafast-price");
        Usage(1);
        double Cost() => messages.Last()["usage"]!["estimated_cost_usd"]!.GetValue<double>();
        Check("request accounting uses Sol Ultrafast and its cache discount", Math.Abs(Cost() - 0.01458) < 1e-9);
        session.SetSpeedAsync(false, false).GetAwaiter().GetResult();
        Usage(2, "default");
        Check("returned standard tier overrides the requested Ultrafast rate", Math.Abs(Cost() - 0.01701) < 1e-9);
    }

    private static void VerifyUltrafastViews(MainViewModel vm, ChatViewModel[] chats)
    {
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                var chat = chats[0]; LoadUltrafastCatalog(chat); chat.Model = "gpt-6.1-sol"; chat.FastMode = false; chat.UltrafastMode = false;
                Call(chat, "RebuildEffortOptions");
                Property(vm, "ActiveChat", chat); Property(vm, "ShowBridge", false);
                var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
                shell.Left = shell.Top = -30000; shell.WindowStartupLocation = WindowStartupLocation.Manual;
                shell.ShowActivated = shell.ShowInTaskbar = false;
                try
                {
                    shell.Show(); shell.UpdateLayout();
                    var popup = (Popup)shell.FindName("ThinkPopup");
                    popup.DataContext = chat;
                    var menu = (FrameworkElement)popup.Child;
                    menu.DataContext = chat;
                    menu.Measure(new Size(360, double.PositiveInfinity)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
                    var button = (ToggleButton)shell.FindName("UltrafastModeToggle");
                    Check(theme + " composer exposes an enabled Ultrafast control", button.IsEnabled && button.Visibility == Visibility.Visible);
                    ToggleSpeed(button);
                    Check(theme + " composer click reaches only its owner", chat.UltrafastMode && !chats[2].UltrafastMode);
                    Check(theme + " lightning controls sit beside effort", button.Content as string == "\u26a1\u26a1"
                        && ReferenceEquals(button.Parent, ((ToggleButton)shell.FindName("ThinkToggle")).Parent));
                    Check(theme + " effort popup contains no speed choices", !Descendants<ButtonBase>(menu)
                        .Any(control => AutomationProperties.GetName(control).Contains("fast mode", StringComparison.OrdinalIgnoreCase)));
                    CaptureUltrafastElement((FrameworkElement)button.Parent, 640, "speed-controls-" + theme.ToLowerInvariant() + ".png");
                    LoadUltrafastCatalog(chat, false);
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(theme + " losing eligibility removes the Ultrafast control even with a saved on preference",
                        button.Visibility == Visibility.Collapsed && !chat.ShowUltrafastMode);
                    CaptureUltrafastElement((FrameworkElement)button.Parent, 640, "speed-controls-ineligible-" + theme.ToLowerInvariant() + ".png");
                    LoadUltrafastCatalog(chat);
                    var pane = (FrameworkElement)((DataTemplate)shell.FindResource("BridgeComposerTemplate")).LoadContent();
                    pane.DataContext = chats[2];
                    pane.Measure(new Size(400, double.PositiveInfinity)); pane.Arrange(new Rect(pane.DesiredSize)); pane.UpdateLayout();
                    var panePopup = Descendants<Popup>(pane).Single(p => p.PlacementTarget is FrameworkElement { Name: "PaneEffortToggle" });
                    var paneMenu = (FrameworkElement)panePopup.Child;
                    Call(chats[2], "RebuildEffortOptions"); paneMenu.DataContext = chats[2];
                    paneMenu.Measure(new Size(320, double.PositiveInfinity)); paneMenu.Arrange(new Rect(paneMenu.DesiredSize)); paneMenu.UpdateLayout();
                    var paneButton = Descendants<ToggleButton>(pane).Single(b => AutomationProperties.GetName(b) == "Pane ultrafast mode");
                    Check(theme + " narrow panes keep both lightning controls beside effort",
                        ReferenceEquals(paneButton.Parent, Descendants<ToggleButton>(pane).Single(b => b.Name == "PaneEffortToggle").Parent));
                    ToggleSpeed(paneButton);
                    Check(theme + " pane control reaches its own chat", chats[2].UltrafastMode && chat.UltrafastMode);
                    Check(theme + " pane effort popup contains no speed choices", !Descendants<ButtonBase>(paneMenu)
                        .Any(control => AutomationProperties.GetName(control).Contains("fast mode", StringComparison.OrdinalIgnoreCase)));
                    CaptureUltrafastElement(pane, 400, "speed-pane-" + theme.ToLowerInvariant() + ".png");
                    var fast = Descendants<ToggleButton>(pane).Single(b => AutomationProperties.GetName(b) == "Pane fast mode");
                    ToggleSpeed(fast);
                    Check(theme + " pane lightning choices are exclusive without changing effort", chats[2].FastMode
                        && !chats[2].UltrafastMode && paneButton.IsChecked == false);
                    ToggleSpeed(fast);
                    Check(theme + " clicking the active lightning returns to standard", !chats[2].FastMode && !chats[2].UltrafastMode);
                    chats[2].UltrafastMode = false;
                }
                finally { shell.Close(); }

                chat.UltrafastMode = false;
                var options = new BridgeAgentOptionsPanel(); options.Configure(chat, "codex"); options.ShowCentralPlanner(true);
                var orchestrator = (ToggleButton)options.FindName("OrchestratorUltrafastModeToggle");
                orchestrator.IsChecked = true;
                Check(theme + " role choices keep Ultrafast independent", options.OrchestratorConfiguration.UltrafastMode
                    && options.OrchestratorConfiguration.FastMode == false && !options.WorkerConfiguration.UltrafastMode
                    && !options.CentralConfiguration.UltrafastMode && !chat.UltrafastMode);
                var configuration = JsonSerializer.Deserialize<BridgeAgentConfiguration>(JsonSerializer.Serialize(options.OrchestratorConfiguration))!;
                Call(chats[1], "ApplyBridgeConfiguration", configuration);
                Check(theme + " saved role configuration reaches its chat", chats[1].UltrafastMode && !chats[1].FastMode);
                ((ToggleButton)options.FindName("WorkerUltrafastModeToggle")).IsChecked = true;
                ((ToggleButton)options.FindName("CentralUltrafastModeToggle")).IsChecked = true;
                Check(theme + " workers and central planner can select Ultrafast", options.WorkerConfiguration.UltrafastMode && options.CentralConfiguration.UltrafastMode);
                var wrapper = new Border { Padding = new Thickness(16), Child = options, Background = (Brush)Application.Current.Resources["Bg0"] };
                CaptureUltrafastElement(wrapper, 460, "ultrafast-roles-" + theme.ToLowerInvariant() + ".png");
                ((ToggleButton)options.FindName("OrchestratorFastModeToggle")).IsChecked = true;
                Check(theme + " role speed choices are exclusive", !options.OrchestratorConfiguration.UltrafastMode && options.OrchestratorConfiguration.FastMode == true);
                var models = (ComboBox)options.FindName("CentralModelBox");
                models.SelectedItem = models.Items.OfType<ModelChoice>().Single(m => m.Value == "gpt-6-luna");
                Check(theme + " unsupported role models clear Ultrafast", !options.CentralConfiguration.UltrafastMode
                    && ((ToggleButton)options.FindName("CentralUltrafastModeToggle")).Visibility == Visibility.Collapsed);
                var unverified = new ChatViewModel(_root, provider: "codex"); Chats.Add(unverified);
                var unavailable = new BridgeAgentOptionsPanel(); unavailable.Configure(unverified, "codex");
                Check(theme + " another login cannot inherit eligibility from the provider-wide catalog",
                    new[] { "Orchestrator", "Worker", "Central" }.All(role =>
                        ((ToggleButton)unavailable.FindName(role + "UltrafastModeToggle")).Visibility == Visibility.Collapsed)
                    && !unavailable.OrchestratorConfiguration.UltrafastMode);
            }
        }
        finally { resources.Clear(); foreach (var resource in original) resources.Add(resource); }
    }

    private static void ToggleSpeed(ToggleButton button) =>
        ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();

    private static void CaptureUltrafastElement(FrameworkElement element, int width, string name)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        var height = (int)Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.Resources["Bg1"], null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
        Console.WriteLine("Rendered " + name);
    }
}
