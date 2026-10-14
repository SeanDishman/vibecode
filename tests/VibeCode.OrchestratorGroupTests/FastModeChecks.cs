using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void VerifyOrchestratorFastMode()
    {
        AppSettings.Current.FastMode = false;
        VerifyFastModeSetup();
        var codex = FreshTeam(false, "codex", "claude", orchestratorFastMode: true);
        var claude = FreshTeam(false, "claude", "codex", orchestratorFastMode: true);
        var normal = FreshTeam(true, "codex", "claude", orchestratorFastMode: false, initialFastMode: true);
        var fastWorkers = FreshTeam(false, "claude", "codex", orchestratorFastMode: false, initialFastMode: true);
        Check("Codex orchestrator priority does not enable Claude workers", codex.Host.FastMode &&
            codex.Vm.BridgePanes.Where(p => !p.IsBridgeManager).All(p => !p.FastMode));
        var claudeManager = claude.Vm.BridgePanes.Single(p => p.IsBridgeManager);
        Check("Claude orchestrator speed does not enable Codex workers", claudeManager.FastMode &&
            claude.Vm.BridgePanes.Where(p => !p.IsBridgeManager).All(p => !p.FastMode));
        AssertAcknowledged(claudeManager, "fast Claude orchestrator");
        Check("normal Codex orchestrator clears the host's previous priority tier", !normal.Host.FastMode &&
            FirstUserSettings(normal.Host).FastMode == false);
        Check("normal orchestrator preserves existing fast workers", !fastWorkers.Vm.BridgePanes.Single(p => p.IsBridgeManager).FastMode &&
            fastWorkers.Vm.BridgePanes.Where(p => !p.IsBridgeManager).All(p => p.FastMode));
        AssertAcknowledged(fastWorkers.Vm.BridgePanes.Single(p => p.IsBridgeManager), "normal Claude orchestrator");

        foreach (var manager in new[] { codex.Host, claudeManager })
        {
            Call(manager, "ApplyBridgeConfiguration", Config(manager) with { FastMode = false });
            Check(manager.Provider + " accepts task immediately after clearing fast mode", manager.Send("Verify normal orchestrator speed"));
            Pump(() => manager.Status == "idle" && UserRequests(manager).Count() == 2);
            var observed = manager.IsCodex ? UserRequests(manager).Last() : Requests(manager).Last(n => n["fixture_user_received"]?.GetValue<bool>() == true);
            Check(manager.Provider + " next task uses normal speed", manager.IsCodex
                ? observed["params"]?["serviceTier"]?.ToString() == "default"
                : observed["effective_fast_mode"]?.GetValue<bool>() == false && observed["pending_controls"]?.GetValue<int>() == 0);
        }

        Call(codex.Host, "ApplyBridgeConfiguration", Config(codex.Host) with { FastMode = true });
        LaterGroupAndRestore(codex, managerFastMode: true);
        var legacy = JsonSerializer.Deserialize<BridgeAgentConfiguration>("""{"Provider":"codex","Model":"gpt-6.1-sol","Effort":"high"}""")!;
        var oldChat = Chat("legacy-speed", "codex"); oldChat.FastMode = true;
        Call(oldChat, "ApplyBridgeConfiguration", legacy);
        Check("older saved role without speed preserves restored chat preference", legacy.FastMode is null && oldChat.FastMode);
        var unsupported = Chat("unsupported-speed", "claude");
        Call(unsupported, "ApplyBridgeConfiguration", new BridgeAgentConfiguration("claude", "claude-fable-5", null, FastMode: true));
        Check("unsupported fast mode leaves the selected model unchanged", !unsupported.FastMode && unsupported.Model == "claude-fable-5");
        Check("role speed changes do not alter global new-chat defaults", !AppSettings.Current.FastMode);
        VerifyCentralFastMode();
    }

    static void VerifyCentralFastMode()
    {
        AppSettings.Current.BridgeOrchestratorCount = 1;
        var host = Chat("central-fast-mode", "codex"); host.Model = "gpt-6.1-sol"; host.FastMode = false;
        var panel = new BridgeSetupPanel(); panel.Configure(host, 8, advancedOnly: true);
        var options = (BridgeAgentOptionsPanel)panel.FindName("AgentOptions");
        var fields = (FrameworkElement)options.FindName("CentralFields");
        var toggle = (ToggleButton)options.FindName("CentralFastModeToggle");
        Check("one orchestrator does not show an unused central planner", fields.Visibility == Visibility.Collapsed);
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = 2;
        Choose(options, "CentralModelBox", "gpt-6.1-sol");
        var orchestrator = panel.OrchestratorConfiguration;
        var worker = panel.WorkerConfiguration;
        Check("two orchestrators expose the central planner's own speed control", fields.Visibility == Visibility.Visible
            && toggle.Visibility == Visibility.Visible && toggle.IsEnabled && toggle.IsChecked != true);
        var automation = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle);
        automation.Toggle();
        Check("central speed is independent of the orchestrators, workers and host", panel.CentralConfiguration.FastMode == true
            && panel.OrchestratorConfiguration == orchestrator && panel.WorkerConfiguration == worker && !host.FastMode);
        var factory = typeof(BridgeTeamSuggestionService).GetMethod("CreateSession", F)!;
        foreach (var provider in new[] { "codex", "claude" })
        {
            ((ComboBox)options.FindName("CentralProviderBox")).SelectedValue = provider;
            Choose(options, "CentralModelBox", provider == "codex" ? "gpt-6.1-sol" : "claude-opus-5");
            foreach (var enabled in new[] { true, false })
            {
                toggle.IsChecked = enabled;
                using var session = (VibeCode.Protocol.ICodingSession)factory.Invoke(null,
                    new object?[] { host, null, panel.CentralConfiguration, true, null, null })!;
                var transportOptions = session.GetType().GetField("_options", F)!.GetValue(session)!;
                Check(provider + " central session receives explicit fast mode " + enabled,
                    (bool)transportOptions.GetType().GetProperty("FastMode")!.GetValue(transportOptions)! == enabled);
            }
            toggle.IsChecked = true;
        }
        ((ComboBox)options.FindName("CentralProviderBox")).SelectedValue = "glm";
        Check("unsupported central providers hide and clear speed", toggle.Visibility == Visibility.Collapsed && panel.CentralConfiguration.FastMode == false);
        ((ComboBox)options.FindName("CentralProviderBox")).SelectedValue = "codex";
        Check("central provider changes retain each provider's speed choice", toggle.IsChecked == true && panel.CentralConfiguration.FastMode == true);
        panel.Suspend(); panel.Configure(host, 8, advancedOnly: true);
        Check("reopening setup retains central speed", panel.CentralConfiguration.FastMode == true);
        var resources = Application.Current.Resources.MergedDictionaries;
        var original = resources.ToArray();
        try
        {
            foreach (var theme in new[] { "Dark", "Borderless", "Cli" })
            {
                resources.Clear();
                resources.Add((ResourceDictionary)Application.LoadComponent(new Uri(
                    $"/VibeCode;component/Themes/{(theme == "Cli" ? "Cli" : "Dark")}.xaml", UriKind.Relative)));
                if (theme == "Borderless") resources.Add((ResourceDictionary)Application.LoadComponent(new Uri(
                    "/VibeCode;component/Themes/Borderless.xaml", UriKind.Relative)));
                RenderFastMode(panel, "central-fast-" + theme.ToLowerInvariant() + ".png", 460, 720, "CentralFastModeToggle");
            }
        }
        finally { resources.Clear(); foreach (var dictionary in original) resources.Add(dictionary); }
        RenderFastMode(panel, "central-fast-compact.png", 400, 420, "CentralFastModeToggle");
        var top = toggle.TransformToAncestor(panel).Transform(new Point()).Y;
        Check("the central speed switch remains reachable in compact setup", top >= 40 && top + toggle.ActualHeight < panel.ActualHeight - 50);
        var models = (ComboBox)options.FindName("CentralModelBox");
        var noFast = new ModelChoice { Provider = "codex", Value = "fixture-standard-only", Display = "Standard speed", SupportsFastMode = false };
        models.ItemsSource = new[] { noFast }; models.SelectedItem = noFast;
        Check("unsupported central models disable speed without replacing the chosen model", !toggle.IsEnabled && toggle.IsChecked == false
            && panel.CentralConfiguration.FastMode == false && panel.CentralConfiguration.Model == noFast.Value);
        AppSettings.Current.BridgeOrchestratorCount = 1;
    }

    static void VerifyFastModeSetup()
    {
        var host = Chat("fast-mode-ui", "codex"); host.Model = "gpt-6.1-sol"; host.FastMode = false;
        var panel = new BridgeSetupPanel(); panel.Configure(host, 8, advancedOnly: true);
        var options = (BridgeAgentOptionsPanel)panel.FindName("AgentOptions");
        Choose(options, "OrchestratorModelBox", "gpt-6.1-sol");
        ((ComboBox)panel.FindName("WorkerCountBox")).SelectedItem = 2;
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Coordinate the workers and review their changes.";
        var toggle = (ToggleButton)options.FindName("OrchestratorFastModeToggle");
        var worker = panel.WorkerConfiguration;
        var model = panel.OrchestratorConfiguration.Model;
        Check("advanced Codex setup exposes normal speed initially", toggle.Visibility == Visibility.Visible && toggle.IsEnabled && toggle.IsChecked != true);
        RenderFastMode(panel, "orchestrator-fast-off.png");
        Check("speed is a compact lightning control beside effort", toggle.Content as string == "\u26a1"
            && ReferenceEquals(toggle.Parent, ((ComboBox)options.FindName("OrchestratorEffortBox")).Parent));
        var peer = new ToggleButtonAutomationPeer(toggle);
        var automation = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
        automation.Toggle();
        RenderFastMode(panel, "orchestrator-fast-on.png");
        Check("lightning toggle announces fast mode on", automation.ToggleState == ToggleState.On
            && AutomationProperties.GetName(toggle) == "Orchestrator fast mode");
        Check("speed toggle preserves model and worker settings", panel.OrchestratorConfiguration.FastMode == true &&
            panel.OrchestratorConfiguration.Model == model && panel.WorkerConfiguration == worker && !host.FastMode);
        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = "glm";
        Check("other providers hide the speed toggle", toggle.Visibility == Visibility.Collapsed && panel.OrchestratorConfiguration.FastMode == false);
        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = "codex";
        Check("returning to provider restores its speed choice", toggle.IsChecked == true && panel.OrchestratorConfiguration.FastMode == true);
        panel.Suspend(); panel.Configure(host, 8, advancedOnly: true);
        Check("reopening setup preserves the unsent speed choice", panel.OrchestratorConfiguration.FastMode == true);
        RenderFastMode(panel, "orchestrator-fast-compact.png", 400, 420);
        var top = toggle.TransformToAncestor(panel).Transform(new Point()).Y;
        Check("compact panel keeps toggle reachable above the footer", top >= 40 && top + toggle.ActualHeight < panel.ActualHeight - 50 && toggle.ActualHeight >= 36);

        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = "claude";
        // A live capability catalog is authoritative, including an explicit unsupported model.
        var noFast = new ModelChoice { Provider = "claude", Value = "fixture-no-fast", Display = "No fast mode", SupportsFastMode = false };
        var models = (ComboBox)options.FindName("OrchestratorModelBox");
        models.ItemsSource = new[] { noFast }; models.SelectedItem = noFast;
        RenderFastMode(panel, "orchestrator-fast-unavailable.png");
        Check("unsupported model disables fast mode without switching models", !toggle.IsEnabled && toggle.IsChecked == false &&
            panel.OrchestratorConfiguration.Model == noFast.Value && toggle.ToolTip.ToString()!.Contains("unavailable"));
        panel.Suspend();
        var regular = new BridgeSetupPanel(); regular.Configure(host, 8);
        Check("regular bridge keeps the advanced speed control hidden", ((FrameworkElement)regular.FindName("AdvancedFields")).Visibility == Visibility.Collapsed);
        regular.Suspend();
    }

    static void RenderFastMode(BridgeSetupPanel panel, string name, int width = 460, int height = 720, string toggleName = "OrchestratorFastModeToggle")
    {
        var surface = new Border { Child = panel, Background = (Brush)Application.Current.Resources["Bg0"] };
        surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
        var toggle = (FrameworkElement)((BridgeAgentOptionsPanel)panel.FindName("AgentOptions")).FindName(toggleName);
        var scroll = Descendants<ScrollViewer>(panel).First();
        var top = toggle.TransformToAncestor(scroll).Transform(new Point()).Y;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + top - 100);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(Root, name)); encoder.Save(output);
        surface.Child = null;
        Console.WriteLine("Rendered " + name);
    }
}
