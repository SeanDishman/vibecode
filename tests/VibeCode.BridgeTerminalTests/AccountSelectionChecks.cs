using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
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
    private static void IsolateBridgeAccountStores()
    {
        var accountRoot = Path.Combine(_root, "accounts", Guid.NewGuid().ToString("N"));
        foreach (var variable in new[] { "CLAUDE_CONFIG_DIR", "VIBECODE_CLAUDE_ACCOUNT_STORE",
                     "VIBECODE_CODEX_ACCOUNT_STORE", "VIBECODE_CODEX_LEGACY_HOME", "GEMINI_CLI_HOME"})
        {
            var path = Path.Combine(accountRoot, variable);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(variable, path);
        }
        var exe = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        foreach (var provider in new[] { "CODEX", "CLAUDE" })
            Environment.SetEnvironmentVariable("VIBECODE_" + provider + "_PATH", exe);
    }

    private static void SeedBridgeCodexAccount(string id, string name, bool usable = true)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE")!, id);
        Directory.CreateDirectory(Path.Combine(path, "home"));
        File.WriteAllText(Path.Combine(path, "account.json"), JsonSerializer.Serialize(new
        { Id = id, Name = name, Plan = "plus", HomeFolder = "home" }));
        if (usable) File.WriteAllText(Path.Combine(path, "home", "auth.json"),
            JsonSerializer.Serialize(new { tokens = new { account_id = id } }));
    }

    private static void VerifyBridgeAccounts()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var keys = ApiKeyAccountService.Instance;
        var cwd = Path.Combine(_root, "account-bridge-project");
        Directory.CreateDirectory(cwd);
        var host = new ChatViewModel(cwd, provider: "codex", accountId: "personal");
        Chats.Add(host);
        var panel = new BridgeSetupPanel();
        panel.Configure(host, 8);
        var providerBox = (ComboBox)panel.FindName("PeerProviderBox");
        var box = (ComboBox)panel.FindName("PeerAccountBox");
        var start = (Button)panel.FindName("StartButton");
        void ChooseProvider(string id) => providerBox.SelectedItem = providerBox.Items.OfType<BridgeProviderChoice>().Single(p => p.Id == id);
        Check("no accounts explains how to connect and prevents launch", !box.HasItems && !start.IsEnabled &&
            panel.PeerProvider is null && !providerBox.IsEnabled &&
            ((TextBlock)panel.FindName("AccountNote")).Visibility == Visibility.Visible);
        Check("providers without an account say so", providerBox.Items.OfType<BridgeProviderChoice>()
            .All(p => !p.IsUsable && p.Detail == "No account added"));
        CaptureAccountSetup(panel, "bridge-accounts-empty.png");

        SeedBridgeCodexAccount("personal", "Personal workspace");
        SeedBridgeCodexAccount("work", "Work workspace");
        SeedBridgeCodexAccount("expired", "Reconnect this account", usable: false);
        Check("fixture selects the personal Codex default", CodexAccountService.Instance.Select("personal") == CodexAccountOutcome.Ok);
        var fixtures = new Dictionary<string, ApiKeyAccount>();
        foreach (var provider in ApiKeyAccountService.Providers)
            fixtures[provider] = keys.Add(provider, "fixture-key-" + provider, provider.ToUpperInvariant() + " API account");
        var secondKey = keys.Add("codex", "fixture-other-codex-key", "Second Codex API account");
        fixtures["claude"].Label = "Research workspace with a deliberately long account name for checking narrow dropdown layouts";

        panel.Configure(host, 8);
        var providers = providerBox.Items.OfType<BridgeProviderChoice>().ToArray();
        Check("provider picker lists every provider and enables only signed-in choices", providers.Select(p => p.Id)
            .SequenceEqual(BridgeSetupPanel.Providers.Select(p => p.Id))
            && providers.All(p => p.IsUsable) && providerBox.IsEnabled);
        Check("provider picker starts on the host's provider and counts its accounts",
            panel.PeerProvider?.Id == "codex" && providers.Single(p => p.Id == "codex").Detail == "5 accounts" &&
            providers.Single(p => p.Id == "claude").Detail == "1 account" && providers.Single(p => p.Id == "glm").Detail == "1 account");
        var choices = box.Items.OfType<BridgeAccountChoice>().ToArray();
        Check("account picker lists only the chosen provider's accounts", choices.Length == 5 && choices.All(a => a.Provider == "codex"));
        Check("every saved account is still reachable through its provider", BridgeAccountChoice.Load().Count == fixtures.Count + 4 &&
            providers.Where(p => p.IsUsable).All(p => BridgeAccountChoice.For(p.Id).Count > 0));
        Check("host account is initially selected even when an API key is the default", panel.PeerAccount?.AccountId == "personal");
        Check("unusable accounts are identified", choices.Single(a => a.AccountId == "expired") is { IsUsable: false } unavailable && unavailable.Detail.Contains("Sign in"));
        Check("account picker has an accessible name", AutomationProperties.GetName(box) == "Bridge partner AI account");
        Check("provider picker has an accessible name", AutomationProperties.GetName(providerBox) == "Bridge partner AI provider");
        ChooseProvider("glm");
        Check("choosing another provider swaps in only its accounts", panel.Provider == "glm" &&
            box.Items.OfType<BridgeAccountChoice>().Select(a => a.AccountId).SequenceEqual([fixtures["glm"].Id]) &&
            panel.PeerAccount?.AccountId == fixtures["glm"].Id && start.IsEnabled);
        ChooseProvider("codex");
        Check("returning to the host's provider selects the host account again", panel.PeerAccount?.AccountId == "personal");
        box.SelectedItem = choices.Single(a => a.AccountId == "work");
        Check("another account can be chosen for the same provider", panel.Provider == "codex" && panel.RequirePeerAccount().AccountId == "work");
        ((RadioButton)panel.FindName("AdvancedChoice")).IsChecked = true;
        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = "claude";
        Check("advanced provider choice remains independent", panel.Provider == "claude");
        ((RadioButton)panel.FindName("RegularChoice")).IsChecked = true;
        panel.Configure(host, 8);
        Check("account choice survives switching modes and reopening", panel.PeerAccount?.AccountId == "work");
        CaptureAccountSetup(panel, "bridge-accounts-selected.png");
        box.ApplyTemplate();
        box.IsDropDownOpen = true;
        box.UpdateLayout();
        var popup = (Popup)box.Template.FindName("PART_Popup", box);
        var dropdown = (FrameworkElement)popup.Child;
        dropdown.Measure(new Size(436, 320)); dropdown.Arrange(new Rect(0, 0, 436, 320)); dropdown.UpdateLayout();
        CaptureAccountDropdown(dropdown, 436, 320, "bridge-accounts-dropdown.png");
        box.IsDropDownOpen = false;

        ChooseProvider("claude");
        Check("the Claude API key is listed under Claude only", box.Items.OfType<BridgeAccountChoice>()
            .Select(a => a.AccountId).Order().SequenceEqual(new[] { fixtures["claude"].Id }.Order()));
        box.SelectedItem = box.Items.OfType<BridgeAccountChoice>().Single(a => a.AccountId == fixtures["claude"].Id);
        CaptureAccountSetup(panel, "bridge-accounts-long-name.png");
        box.IsDropDownOpen = true;
        box.UpdateLayout();
        dropdown = (FrameworkElement)popup.Child;
        dropdown.Measure(new Size(436, 320)); dropdown.Arrange(new Rect(0, 0, 436, dropdown.DesiredSize.Height)); dropdown.UpdateLayout();
        var item = (ComboBoxItem)box.ItemContainerGenerator.ContainerFromItem(box.SelectedItem);
        item.ApplyTemplate();
        item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check("long account names stay within the dropdown width", item.DesiredSize.Width <= box.ActualWidth);
        CaptureAccountDropdown(dropdown, 436, 320, "bridge-accounts-long-dropdown.png");
        box.IsDropDownOpen = false;
        ChooseProvider("codex");
        box.SelectedItem = box.Items.OfType<BridgeAccountChoice>().Single(a => a.AccountId == "work");
        providerBox.ApplyTemplate();
        providerBox.IsDropDownOpen = true;
        providerBox.UpdateLayout();
        var providerDropdown = (FrameworkElement)((Popup)providerBox.Template.FindName("PART_Popup", providerBox)).Child;
        providerDropdown.Measure(new Size(436, 320)); providerDropdown.Arrange(new Rect(0, 0, 436, 320)); providerDropdown.UpdateLayout();
        CaptureAccountDropdown(providerDropdown, 436, 320, "bridge-providers-dropdown.png");
        providerBox.IsDropDownOpen = false;

        var duplicateA = keys.Add("claude", "duplicate-fixture-one", "Shared name");
        var duplicateB = keys.Add("claude", "duplicate-fixture-two", "Shared name");
        var duplicates = BridgeAccountChoice.Load().Where(a => a.AccountId == duplicateA.Id || a.AccountId == duplicateB.Id).ToArray();
        Check("matching account names can be distinguished", duplicates.Length == 2 && duplicates[0].Label != duplicates[1].Label);
        keys.Remove(duplicateA.Id);
        keys.Remove(duplicateB.Id);

        foreach (var provider in new[] { "claude", "codex" })
        {
            var account = fixtures[provider];
            var chosen = new ProcessStartInfo();
            Check(provider + " applies the explicitly selected API key", keys.ApplyTo(chosen, provider, account.Id));
            var variable = provider == "claude" ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";
            Check(provider + " uses that key rather than another account", chosen.Environment[variable] == "fixture-key-" + provider);
            var subscription = new ProcessStartInfo(); subscription.Environment[variable] = "ambient-fixture-key";
            Check(provider + " subscription selection excludes global and inherited API keys", !keys.ApplyTo(subscription, provider, "subscription-id") && !subscription.Environment.ContainsKey(variable));
            var chat = new ChatViewModel(cwd, accountId: account.Id, provider: provider);
            Chats.Add(chat);
            Check(provider + " retains and labels the chosen API account", chat.AccountId == account.Id && chat.AccountLabel == account.Title);
        }
        var codexProcess = CodexEnvironment.CreateAppServerStartInfo(cwd, Path.Combine(_root, "codex-env"), accountId: fixtures["codex"].Id);
        Check("Codex launch environment honors the account override", codexProcess.Environment["OPENAI_API_KEY"] == "fixture-key-codex");
        var claudeProcess = (ProcessStartInfo)typeof(ClaudeSession).GetMethod("CreateStartInfo", Flags)!
            .Invoke(null, [new ClaudeSessionOptions { Cwd = cwd, AccountId = fixtures["claude"].Id }])!;
        Check("Claude launch environment honors the account override", claudeProcess.Environment["ANTHROPIC_API_KEY"] == "fixture-key-claude");
        foreach (var secondary in new[] { false, true })
            foreach (var hostProvider in new[] { "codex", "claude" })
                VerifyBridgeAccountLaunch(secondary, hostProvider, "work");
        VerifyBridgeAccountLaunch(false, "codex", fixtures["codex"].Id);
        Check("bridge choices leave global account selections unchanged", CodexAccountService.Instance.ActiveId == "personal" && keys.SelectedId("codex") == secondKey.Id);

        var ready = new BridgeAccountChoice("codex", "ready", "Ready", "OpenAI Codex · Plus");
        var signedOut = new BridgeAccountChoice("codex", "signed-out", "Signed out", "OpenAI Codex · Sign in again", IsUsable: false);
        Check("only a real choice between signed-in accounts asks the user", BridgeAccountChoice.OnlyUsable([ready, signedOut]) == ready &&
            BridgeAccountChoice.OnlyUsable([ready, ready with { AccountId = "other" }]) is null && BridgeAccountChoice.OnlyUsable([signedOut]) is null);
        foreach (var secondary in new[] { false, true })
            VerifyBridgeAddAgentAccounts(secondary, chooseFromSubmenu: true);
        Check("adding agents leaves global account selections unchanged", CodexAccountService.Instance.ActiveId == "personal" && keys.SelectedId("codex") == secondKey.Id);

        VerifyBridgeAccountHeaderLabels(fixtures["claude"]);

        box.SelectedItem = box.Items.OfType<BridgeAccountChoice>().Single(a => a.AccountId == secondKey.Id);
        keys.Remove(secondKey.Id);
        try { panel.RequirePeerAccount(); throw new Exception("Removed account was accepted"); }
        catch (InvalidOperationException) { Check("removed accounts are rejected before creating a bridge", true); }
        panel.Suspend();

        // Leave "work" as the only signed-in Codex account. Add agent must use it without asking, even though the
        // host still runs as (now signed-out) "personal", which is what the old provider default would have picked.
        keys.Remove(fixtures["codex"].Id);
        File.Delete(Path.Combine(Environment.GetEnvironmentVariable("VIBECODE_CODEX_ACCOUNT_STORE")!, "personal", "home", "auth.json"));
        Check("fixture leaves exactly one signed-in Codex account", BridgeAccountChoice.OnlyUsable(BridgeAccountChoice.For("codex"))?.AccountId == "work");
        foreach (var secondary in new[] { false, true })
            VerifyBridgeAddAgentAccounts(secondary, chooseFromSubmenu: false);
    }

    /// <summary>Header Add agent: provider first, then that provider's account only when there are several.</summary>
    private static void VerifyBridgeAddAgentAccounts(bool secondary, bool chooseFromSubmenu)
    {
        var vm = new MainViewModel();
        var cwd = Path.Combine(_root, "add-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var host = new ChatViewModel(cwd, provider: "codex", accountId: "personal") { Model = "default" };
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(host, new FakeSession());
        host.SetMode("bypassPermissions", remember: false);
        vm.Chats.Add(host); Chats.Add(host); vm.ActiveChat = host;
        Call(vm, "Track", host);
        AppSettings.Current.DualMonitorDoubleSessions = secondary;
        MainWindow Shell(MainWindow? primary = null) => (MainWindow)typeof(MainWindow).GetConstructors(Flags)
            .Single(c => c.GetParameters().Length == 3).Invoke([vm, primary, true]);
        var primary = Shell();
        var shell = secondary ? Shell(primary) : primary;
        if (secondary) Property(vm, "SecondaryActiveChat", host);
        var setup = (BridgeSetupPanel)shell.FindName("BridgeSetup");
        setup.Configure(host, 8);
        ((Button)setup.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var panes = secondary ? vm.SecondaryBridgePanes : vm.BridgePanes;
        var surface = secondary ? "secondary" : "primary";
        Check($"a two-agent Codex bridge is running on the {surface} shell", panes.Count == 2 && panes[1].IsCodex);

        var add = LogicalDescendants<Button>(shell).Single(b => AutomationProperties.GetName(b) == "Add bridge agent");
        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var menu = add.ContextMenu!;
        var rows = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag);
        var codex = rows["codex"].Items.OfType<MenuItem>().ToArray();
        Check("Add agent still starts with one row per provider", rows.Keys.Order().SequenceEqual(BridgeSetupPanel.Providers.Select(p => p.Id).Order()));
        Check("a provider with one signed-in account stays a single click", !rows["claude"].HasItems && !rows["glm"].HasItems);
        if (chooseFromSubmenu)
        {
            Check("Add agent asks which Codex account when several are signed in", codex.Length == 5 &&
                codex.All(i => i.Tag is BridgeAccountChoice { Provider: "codex" }) &&
                codex.Single(i => ((BridgeAccountChoice)i.Tag).AccountId == "expired").IsEnabled == false &&
                AutomationProperties.GetName(codex.Single(i => ((BridgeAccountChoice)i.Tag).AccountId == "work")).StartsWith("Work workspace"));
            codex.Single(i => ((BridgeAccountChoice)i.Tag).AccountId == "work").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        else
        {
            Check("one signed-in Codex account adds without an account question", codex.Length == 0);
            rows["codex"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        menu.IsOpen = false;
        Check($"the {surface} bridge gains exactly one Codex agent on the chosen account", panes.Count == 3 && panes[2].IsCodex &&
            panes[2].AccountId == "work" && host.AccountId == "personal");
        var peer = panes[2];
        Chats.Add(panes[1]); Chats.Add(peer);
        PumpUntil(() => peer.Status is "idle" or "error");
        var session = typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(peer)!;
        var options = (CodexSessionOptions)typeof(CodexSession).GetField("_options", Flags)!.GetValue(session)!;
        Check("the added agent starts in the chosen account's own Codex home", peer.Status == "idle" &&
            options.AccountId == "work" && options.HomeDirectory == CodexAccountService.Instance.HomeFor("work"));

        if (chooseFromSubmenu && !secondary)
        {
            // The shared terminal's Add worker menu builds its provider rows with the same helper.
            var row = new MenuItem { Tag = "codex" };
            var picks = new List<BridgeAccountChoice>();
            typeof(MainWindow).GetMethod("AttachBridgeAccountMenu", Flags)!
                .Invoke(null, [row, BridgeAccountChoice.Load(), new Action<BridgeAccountChoice>(picks.Add)]);
            var choice = new RoutedEventArgs(MenuItem.ClickEvent);
            row.Items.OfType<MenuItem>().Single(i => ((BridgeAccountChoice)i.Tag).AccountId == "work").RaiseEvent(choice);
            Check("Add worker offers the same per-account submenu and reports one pick", row.Items.Count == 5 &&
                picks.Count == 1 && picks[0].AccountId == "work" && choice.Handled);
            var worker = vm.LaunchBridgeWorker(host, "codex", accountId: picks[0].AccountId);
            Chats.Add(worker);
            PumpUntil(() => worker.Status is "idle" or "error");
            var workerOptions = (CodexSessionOptions)typeof(CodexSession).GetField("_options", Flags)!
                .GetValue(typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(worker)!)!;
            Check("a worker added from that menu runs on the chosen account", panes.Count == 4 && panes.Contains(worker) &&
                worker.AccountId == "work" && worker.Status == "idle" && workerOptions.AccountId == "work");
        }
        foreach (var pane in panes.ToArray()) pane.Close();
        setup.Suspend();
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
    }

    private static void VerifyBridgeAccountLaunch(bool secondary, string hostProvider, string peerId)
    {
        var vm = new MainViewModel();
        var cwd = Path.Combine(_root, "launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var host = new ChatViewModel(cwd, provider: hostProvider, accountId: "personal") { Model = "default" };
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(host, new FakeSession());
        host.SetMode("bypassPermissions", remember: false);
        vm.Chats.Add(host); Chats.Add(host); vm.ActiveChat = host;
        Call(vm, "Track", host);
        AppSettings.Current.DualMonitorDoubleSessions = secondary;
        MainWindow Shell(MainWindow? primary = null) => (MainWindow)typeof(MainWindow).GetConstructors(Flags)
            .Single(c => c.GetParameters().Length == 3).Invoke([vm, primary, true]);
        var primary = Shell();
        var shell = secondary ? Shell(primary) : primary;
        if (secondary) Property(vm, "SecondaryActiveChat", host);
        var setup = (BridgeSetupPanel)shell.FindName("BridgeSetup");
        setup.Configure(host, 8);
        var providers = (ComboBox)setup.FindName("PeerProviderBox");
        Check($"a {hostProvider} host starts on its own provider", setup.PeerProvider?.Id == hostProvider);
        providers.SelectedItem = providers.Items.OfType<BridgeProviderChoice>().Single(p => p.Id == "codex");
        var accounts = (ComboBox)setup.FindName("PeerAccountBox");
        accounts.SelectedItem = accounts.Items.OfType<BridgeAccountChoice>().Single(a => a.Provider == "codex" && a.AccountId == peerId);
        ((Button)setup.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var panes = secondary ? vm.SecondaryBridgePanes : vm.BridgePanes;
        Check($"{hostProvider} to Codex bridge starts on {(secondary ? "secondary" : "primary")} shell", panes.Count == 2);
        var peer = panes[1]; Chats.Add(peer);
        Check("only same-provider peers on different accounts gain header names",
            panes.All(p => p.ShowBridgeAccountLabel == (hostProvider == "codex")));
        Check("the new peer uses the chosen account and the host keeps its identity", peer.AccountId == peerId && host.AccountId == "personal" && peer.Mode == host.Mode);
        PumpUntil(() => peer.Status is "idle" or "error");
        Check("the selected peer starts through the local Codex adapter", peer.Status == "idle");
        var session = typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(peer)!;
        var options = (CodexSessionOptions)typeof(CodexSession).GetField("_options", Flags)!.GetValue(session)!;
        Check("the account reaches session options and its isolated home", options.AccountId == peerId && options.HomeDirectory == CodexAccountService.Instance.HomeFor(peerId));
        peer.Close(); host.Close(); setup.Suspend();
    }

    private static void VerifyBridgeAccountHeaderLabels(ApiKeyAccount firstClaude)
    {
        firstClaude.Label = "Aaron";
        var otherClaude = ApiKeyAccountService.Instance.Add("claude", "header-fixture-key", "Blake");
        var vm = new MainViewModel();
        ChatViewModel Pane(string provider, string account, int number)
        {
            var chat = new ChatViewModel(_root, provider: provider, accountId: account)
            { BridgeLabel = (provider == "claude" ? "Claude " : "Codex ") + number, Status = "idle" };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            Property(chat, "BridgeTaskName", "Check account names");
            Call(vm, "Track", chat);
            Chats.Add(chat);
            return chat;
        }
        var a = Pane("claude", firstClaude.Id, 1);
        var a2 = Pane("claude", firstClaude.Id, 2);
        var c = Pane("codex", "personal", 3);
        var c2 = Pane("codex", "personal", 4);
        foreach (var pane in new[] { a, a2, c, c2 }) vm.BridgePanes.Add(pane);
        vm.Chats.Add(a); vm.ActiveChat = a;
        Property(vm, "ShowBridge", true);
        Call(vm, "RaiseBridgeUi");
        Check("multiple saved accounts do not add header labels to a single-account provider",
            vm.BridgePanes.All(p => !p.ShowBridgeAccountLabel));
        var d = Pane("codex", "work", 4);
        Call(vm, "ReplaceLivePane", c2, d);
        Check("an account move labels only the provider with distinct accounts",
            c.ShowBridgeAccountLabel && d.ShowBridgeAccountLabel && !a.ShowBridgeAccountLabel && !a2.ShowBridgeAccountLabel);
        var b = Pane("claude", otherClaude.Id, 2);
        Call(vm, "ReplaceLivePane", a2, b);
        Check("each same-provider account is named using its own login",
            vm.BridgePanes.All(p => p.ShowBridgeAccountLabel) && a.AccountLabel == "Aaron" && b.AccountLabel == "Blake"
            && c.AccountLabel == "Personal workspace" && d.AccountLabel == "Work workspace");

        var secondaryHost = Pane("codex", "personal", 1);
        vm.Chats.Add(secondaryHost);
        AppSettings.Current.DualMonitorDoubleSessions = true;
        Property(vm, "SecondaryActiveChat", secondaryHost);
        Check("a second Bridge can use the same provider on one account",
            vm.ActivateBridgeOnSecondary(secondaryHost, "codex", "personal"));
        foreach (var pane in vm.SecondaryBridgePanes.Where(p => !Chats.Contains(p))) Chats.Add(pane);
        Check("account labels are scoped to their own Bridge",
            vm.BridgePanes.All(p => p.ShowBridgeAccountLabel) && vm.SecondaryBridgePanes.All(p => !p.ShowBridgeAccountLabel));

        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
            .Single(ctor => ctor.GetParameters().Length == 3).Invoke([vm, null, true]);
        try
        {
            foreach (var width in new[] { 1640, 1000 })
            {
                CaptureShell(shell, width, 900, $"bridge-account-headers-{width}.png");
                foreach (var pane in vm.BridgePanes)
                {
                    var strip = CheckHeader(shell, pane, $"account header {width} {pane.BridgeLabel}");
                    var label = strip.Children.OfType<TextBlock>().Single(t => t.Name == "PaneAccountLabel");
                    Check("account name stays visible beside its agent", label.Visibility == Visibility.Visible
                        && label.Text == "\u00b7 " + pane.AccountLabel && Shown(label));
                }
                RenderHeader(CheckHeader(shell, a, "account header capture"), $"bridge-account-strip-{width}.png");
            }
            vm.RemoveBridgePane(b);
            Check("closing the other Claude account removes only Claude's label",
                !a.ShowBridgeAccountLabel && c.ShowBridgeAccountLabel && d.ShowBridgeAccountLabel);
            vm.RemoveBridgePane(d);
            Check("one account per provider hides all header names again", vm.BridgePanes.All(p => !p.ShowBridgeAccountLabel));
        }
        finally { shell.Close(); }
        foreach (var pane in vm.BridgePanes.ToArray()) pane.Close();
        foreach (var pane in vm.SecondaryBridgePanes.ToArray()) pane.Close();
        a2.Close(); c2.Close();
        ApiKeyAccountService.Instance.Remove(otherClaude.Id);
    }

    private static void CaptureAccountSetup(BridgeSetupPanel panel, string name)
    {
        // A real presentation source is needed for ComboBox selection templates to relayout after rebinding.
        // Keep the fixture off-screen; it has no app startup handler or provider activity.
        var border = new Border { Child = panel, Background = (Brush)Application.Current.Resources["Bg0"] };
        var window = new Window
        {
            Content = border,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -20000,
            Top = -20000,
        };
        try
        {
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            CaptureShell(window, 480, (int)Math.Ceiling(panel.ActualHeight), name);
        }
        finally { border.Child = null; window.Close(); }
    }

    private static void CaptureAccountDropdown(FrameworkElement dropdown, int width, int height, string name)
    {
        // Keep the live popup tree attached so capturing it cannot disturb ComboBox's selection template.
        dropdown.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(dropdown);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, name)); encoder.Save(output);
        Console.WriteLine("Rendered " + name);
    }
}
