using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void SubagentOrbChecks()
    {
        foreach (var provider in new[] { "claude", "codex", "grok", "kimi", "glm" })
        {
            var (chat, session) = NewChat("orb-" + provider, provider);
            var child = new SubagentItem { ThreadId = "child", Status = "running" };
            chat.Subagents.Add(child);
            Check(provider + " idle parent shows active child orb", !chat.IsWorking && chat.ShowWorkingText
                && chat.ChatListIsWorking && chat.Items.OfType<PendingItem>().Single().Quiet && !chat.CanInterrupt);
            Check(provider + " active child does not block a new parent prompt", chat.Send("Continue while the child is finishing."));
            Pump(() => session.Sent.Count == 1);
            End(chat, new JsonObject { ["type"] = "result", ["is_error"] = false });
            Check(provider + " parent completion keeps child orb", chat.Status == "idle" && chat.Items.OfType<PendingItem>().Count() == 1);
            child.Status = "completed";
            Check(provider + " last child completion clears activity", !chat.ShowWorkingText && !chat.ChatListIsWorking && !chat.Items.OfType<PendingItem>().Any());
            child.Status = "running";
            chat.Subagents.Clear();
            Check(provider + " clearing roster clears activity", !chat.ShowWorkingText && !chat.Items.OfType<PendingItem>().Any());
            chat.Subagents.Add(child);
            chat.Close();
            Check(provider + " close clears child orb", !chat.ShowWorkingText && !chat.Items.OfType<PendingItem>().Any());
        }
        var (bridgeChat, _) = NewChat("shared-orb");
        using var shared = new BridgeSharedTerminalViewModel(_ => { }, (_, _) => { });
        Call(shared, "Reconcile", (object)new[] { bridgeChat });
        var view = new BridgeSharedTerminal { DataContext = shared };
        view.Measure(new Size(960, 560)); view.Arrange(new Rect(0, 0, 960, 560)); view.UpdateLayout();
        var orb = VisualDescendants<OrbitSpinner>(view).Single(o => AutomationProperties.GetName(o) == "Agents are working");
        Check("real shared terminal starts with its idle orb hidden", !orb.Spin && orb.Visibility == Visibility.Collapsed);
        var notifications = new List<bool>();
        shared.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(shared.HasWorkingAgents)) notifications.Add(shared.HasWorkingAgents); };
        var worker = new SubagentItem { ThreadId = "shared-child", Status = "running" };
        bridgeChat.Subagents.Add(worker);
        Check("shared terminal orb notifies bindings when a child starts", shared.HasWorkingAgents && notifications.Contains(true));
        Pump(() => orb.Spin && orb.Visibility == Visibility.Visible);
        Check("real shared terminal binding lights the child activity orb", orb.Spin && orb.Visibility == Visibility.Visible);
        var second = new SubagentItem { ThreadId = "second-shared-child", Status = "running" };
        bridgeChat.Subagents.Add(second);
        worker.Status = "completed";
        Check("one child finishing cannot clear another child's orb", shared.HasWorkingAgents && bridgeChat.ActiveSubagentCount == 1
            && bridgeChat.Items.OfType<PendingItem>().Single().Quiet);
        notifications.Clear();
        second.Status = "completed";
        Check("shared terminal orb notifies bindings after the last child completes", !shared.HasWorkingAgents && notifications.Contains(false));
        Pump(() => !orb.Spin && orb.Visibility == Visibility.Collapsed);
        Check("real shared terminal binding hides the orb after the last completion", !orb.Spin && orb.Visibility == Visibility.Collapsed);
        worker.Status = "running";
        worker.Status = "interrupted";
        Check("abandoned children cannot keep the chat thinking", !bridgeChat.ShowWorkingText && !shared.HasWorkingAgents);
        worker.Status = "running";
        Check("restoring an active child restores its orb", bridgeChat.ShowWorkingText && shared.HasWorkingAgents);
        bridgeChat.Subagents.Remove(worker);
        worker.Status = "completed"; worker.Status = "running";
        Check("removed child updates cannot revive the orb", !bridgeChat.ShowWorkingText && !shared.HasWorkingAgents);
        view.DataContext = null;
        BridgeSidebarOrbChecks();
    }

    private static void BridgeSidebarOrbChecks()
    {
        var (host, _) = NewChat("orb-sidebar-host");
        var (peer, _) = NewChat("orb-sidebar-peer");
        var vm = new MainViewModel();
        vm.BridgePanes.Add(host); vm.BridgePanes.Add(peer);
        Call(vm, "Track", host); Call(vm, "Track", peer);
        var child = new SubagentItem { ThreadId = "sidebar-child", Status = "running" };
        peer.Subagents.Add(child);
        Check("visible bridge sidebar follows a peer's active child", host.ChatListIsWorking && !host.ShowWorkingText && !host.IsWorking && !peer.IsWorking);
        child.Status = "completed";
        Check("visible bridge sidebar clears when the peer's last child finishes", !host.ChatListIsWorking);
        vm.BridgePanes.Clear();
        var parked = Activator.CreateInstance(typeof(MainViewModel).GetNestedType("LiveBridge", Flags)!, nonPublic: true)!;
        Property(parked, "Panes", new BridgePaneCollection { host, peer });
        var bridges = ReadField<System.Collections.IDictionary>(vm, "_parkedBridges");
        bridges[host] = parked;
        child.Status = "running";
        Check("background bridge sidebar also follows child activity", host.ChatListIsWorking && !host.ShowWorkingText);
        child.Status = "completed";
        Check("background bridge sidebar clears after the last child finishes", !host.ChatListIsWorking);
        bridges.Clear(); host.Close(); peer.Close();
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var descendant in VisualDescendants<T>(VisualTreeHelper.GetChild(root, index))) yield return descendant;
    }

    private static void AdvancedSetupChecks()
    {
        var (host, _) = NewChat("setup", "claude");
        AppSettings.Current.BridgeOrchestratorCount = 2;
        AppSettings.Current.BridgeOrchestratorWorkerCounts = [2, 2];
        var panel = new BridgeSetupPanel();
        panel.Configure(host, 10, advancedOnly: true);
        var options = (BridgeAgentOptionsPanel)panel.FindName("AgentOptions");
        var workers = (ComboBox)options.FindName("WorkerProviderBox");
        var central = (ComboBox)options.FindName("CentralProviderBox");
        workers.SelectedValue = "glm";
        central.SelectedValue = "codex";
        SelectRoleModel(options, "OrchestratorModelBox", "claude-opus-5");
        SelectRoleEffort(options, "OrchestratorEffortBox", "xhigh");
        SelectRoleModel(options, "WorkerModelBox", "glm-5.3");
        SelectRoleEffort(options, "WorkerEffortBox", "high");
        SelectRoleModel(options, "CentralModelBox", "gpt-6.1-sol");
        SelectRoleEffort(options, "CentralEffortBox", "high");
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Build the account settings and usage dashboard. Verify both flows together.";
        Check("Claude orchestrator, GLM workers and Codex planner have independent explicit settings",
            panel.OrchestratorConfiguration == new BridgeAgentConfiguration("claude", "claude-opus-5", "xhigh")
            && panel.WorkerConfiguration == new BridgeAgentConfiguration("glm", "glm-5.3", "high")
            && panel.CentralConfiguration == new BridgeAgentConfiguration("codex", "gpt-6.1-sol", "high"));
        Check("multiple orchestrators expose central planner", ((FrameworkElement)options.FindName("CentralFields")).Visibility == Visibility.Visible);
        var choice = panel.CentralConfiguration;
        central.SelectedValue = "kimi"; central.SelectedValue = "codex";
        Check("central planner retains its provider-specific model and effort", panel.CentralConfiguration == choice);
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = 1;
        Check("single orchestrator hides unused central planner", ((FrameworkElement)options.FindName("CentralFields")).Visibility == Visibility.Collapsed);
        panel.Configure(host, 10, advancedOnly: true, hasOtherOrchestrators: true);
        Check("adding a group to existing orchestrators exposes central planner", ((FrameworkElement)options.FindName("CentralFields")).Visibility == Visibility.Visible
            && panel.WorkerConfiguration.Provider == "glm" && panel.CentralConfiguration == choice);
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = 2;
        foreach (var (width, height) in new[] { (960, 780), (860, 560) })
        {
            var border = new Border { Child = panel, Background = (Brush)Application.Current.Resources["Bg1"] };
            border.Measure(new Size(width, height)); border.Arrange(new Rect(0, 0, width, height)); border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(Root, $"advanced-bridge-{width}.png"))) encoder.Save(file);
            var start = (Button)panel.FindName("StartButton");
            Check(width + "px setup keeps Start reachable", start.ActualHeight >= 36 && start.TranslatePoint(new Point(), panel).Y + start.ActualHeight <= height);
            Check(width + "px setup gives role models room", ((ComboBox)options.FindName("OrchestratorModelBox")).ActualWidth > 160);
            border.Child = null;
        }
        panel.Suspend();
        VerifyGlmWorkerTeam();

        var factory = typeof(BridgeTeamSuggestionService).GetMethod("CreateSession", Flags)!;
        foreach (var provider in new[] { "claude", "codex", "kimi", "grok", "glm" })
        {
            central.SelectedValue = provider;
            var models = (ComboBox)options.FindName("CentralModelBox");
            var selected = models.Items.OfType<ModelChoice>().First(m => m.Value != "default");
            models.SelectedItem = selected;
            var efforts = (ComboBox)options.FindName("CentralEffortBox");
            var effort = efforts.Items.OfType<EffortChoice>().FirstOrDefault(e => e.Value is not null);
            efforts.SelectedItem = effort ?? efforts.Items.OfType<EffortChoice>().First();
            var configuration = options.CentralConfiguration;
            using var session = (ICodingSession)factory.Invoke(null, new object?[] { host, null, configuration, true, null, null })!;
            Check(provider + " planner uses its own native adapter", session.GetType().Name.Equals(provider + "Session", StringComparison.OrdinalIgnoreCase));
            var inner = session is GrokSession ? session.GetType().GetField("_inner", Flags)!.GetValue(session)! : session;
            var field = inner.GetType().GetFields(Flags).First(f => f.FieldType.Name.EndsWith("SessionOptions"));
            var actual = field.GetValue(inner)!;
            var model = actual.GetType().GetProperty("Model")!.GetValue(actual) as string;
            Check(provider + " planner receives an explicitly selected non-default model", model == selected.Value && model != "default");
            var actualEffort = actual.GetType().GetProperty("Effort")!.GetValue(actual) as string;
            Check(provider + " planner receives the selected effort", actualEffort == effort?.Value);
        }
    }

    private static void SelectRoleModel(BridgeAgentOptionsPanel panel, string name, string model)
    {
        var box = (ComboBox)panel.FindName(name);
        box.SelectedItem = box.Items.OfType<ModelChoice>().Single(m => m.Value == model);
    }
    private static void SelectRoleEffort(BridgeAgentOptionsPanel panel, string name, string? effort)
    {
        var box = (ComboBox)panel.FindName(name);
        box.SelectedItem = box.Items.OfType<EffortChoice>().Single(e => e.Value == effort);
    }

    private static void VerifyGlmWorkerTeam()
    {
        var (manager, session) = NewChat("claude-glm-team", "claude");
        var (first, _) = NewChat("glm-worker-one", "glm");
        var (second, _) = NewChat("glm-worker-two", "glm");
        var vm = new MainViewModel();
        vm.BridgePanes.Add(manager); vm.BridgePanes.Add(first); vm.BridgePanes.Add(second);
        foreach (var pane in vm.BridgePanes) Field(pane, "_bridgeSessionInitialized", true);
        vm.ConfigureBridgeOrchestrators(manager, [2], "Implement account settings and verify the flow", true,
            new("claude", null, null), new("glm", "glm-5.3", null));
        Pump(() => session.Sent.Count == 1);
        Check("Claude orchestrator assigns only selected GLM workers", manager.IsBridgeManager
            && first.BridgeCoordinatorAgentId == manager.BridgeAgentId && second.BridgeCoordinatorAgentId == manager.BridgeAgentId
            && first.Provider == "glm" && second.Provider == "glm" && first.Model == "glm-5.3"
            && second.Model == "glm-5.3" && session.Sent[0].Contains("Coordinate 2 worker"));
    }
}
