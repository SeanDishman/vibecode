using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeCode.AgentStatus.Mcp;
using VibeCode.AgentStatus.Mcp.Bridge;
using VibeCode.AgentStatus.Mcp.Contracts;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void VerifyGroupAllocation()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check("legacy setup starts with one orchestrator and automatic workers", legacy.BridgeOrchestratorCount == 1 && legacy.BridgeOrchestratorWorkerCounts is null);
        for (var capacity = 1; capacity <= 16; capacity++)
            for (var groups = 1; groups <= BridgeTeamAllocationPolicy.MaximumOrchestrators(capacity); groups++)
            {
                var maximum = BridgeTeamAllocationPolicy.MaximumWorkers(capacity, groups);
                var allocation = BridgeTeamAllocationPolicy.Distribute(maximum, groups);
                BridgeTeamAllocationPolicy.Validate(allocation, capacity);
                Check($"capacity {capacity}, {groups} groups counts every orchestrator", allocation.All(n => n > 0) && allocation.Sum() + groups == capacity + 1);
            }
        RejectAllocation("empty groups rejected", [], 8);
        RejectAllocation("zero worker group rejected", [0, 2], 8);
        RejectAllocation("too many orchestrators rejected", [1, 1, 1], 4);
        RejectAllocation("total beyond capacity rejected", [3, 3], 6);
        var first = FreshGroups(false, "codex", "claude", [1, 2]);
        FreshGroups(false, "claude", "codex", [2, 1]);
        FreshGroups(true, "codex", "claude", [1, 2]);
        var before = first.Vm.BridgePanes.Count;
        try { first.Vm.LaunchBridgeOrchestrators(first.Host, [1], "Too late for a setup exchange"); Check("dispatch closes new orchestrator setup", false); }
        catch (InvalidOperationException) { Check("dispatch closes new orchestrator setup without changing the roster", first.Vm.BridgePanes.Count == before); }
        var planning = FreshGroups(false, "codex", "claude", [1, 2], dispatchWork: false);
        VerifyAdditionalGroups(planning.Vm, planning.Host);
        VerifyLimitsAndDrafts(first.Host);
        VerifyAutomaticGroups(first.Host);
        VerifySetupRenders(first.Host);
    }

    static void RejectAllocation(string label, int[] allocation, int capacity)
    {
        try { BridgeTeamAllocationPolicy.Validate(allocation, capacity); Check(label, false); }
        catch (ArgumentException) { Check(label, true); }
    }

    static (MainViewModel Vm, ChatViewModel Host) FreshGroups(bool secondary, string orchestratorProvider, string workerProvider, int[] allocation, bool dispatchWork = true)
    {
        AppSettings.Current.BridgeOrchestratorCount = 1;
        AppSettings.Current.BridgeOrchestratorWorkerCounts = null;
        var label = $"{(secondary ? "secondary" : "primary")}-{orchestratorProvider}-{workerProvider}";
        var vm = new MainViewModel();
        var host = Chat(label, "codex"); host.Model = "gpt-6-astra"; host.Effort = "ultra";
        vm.Chats.Add(host); Call(vm, "Track", host); Set(vm, secondary ? "SecondaryActiveChat" : "ActiveChat", host);
        host.Start(); Pump(() => host.Status == "idle" && host.SessionId is not null);
        AppSettings.Current.DualMonitorDoubleSessions = secondary;
        var primary = Shell(vm); var shell = secondary ? Shell(vm, primary) : primary;
        var panel = (BridgeSetupPanel)shell.FindName("BridgeSetup");
        panel.Configure(host, 11, advancedOnly: true);
        ((ComboBox)panel.FindName("OrchestratorProviderBox")).SelectedValue = orchestratorProvider;
        var options = (BridgeAgentOptionsPanel)panel.FindName("AgentOptions");
        Choose(options, "OrchestratorModelBox", orchestratorProvider == "codex" ? "gpt-6.1-sol" : "claude-fable-5");
        Effort(options, "OrchestratorEffortBox", orchestratorProvider == "codex" ? "high" : null);
        ((ComboBox)options.FindName("WorkerProviderBox")).SelectedValue = workerProvider;
        Choose(options, "WorkerModelBox", workerProvider == "codex" ? "gpt-6-luna" : "claude-opus-5");
        Effort(options, "WorkerEffortBox", workerProvider == "codex" ? "medium" : "max");
        var centralProvider = orchestratorProvider == "claude" ? "codex" : "claude";
        ((ComboBox)options.FindName("CentralProviderBox")).SelectedValue = centralProvider;
        Choose(options, "CentralModelBox", centralProvider == "codex" ? "gpt-6-astra" : "claude-opus-5");
        Effort(options, "CentralEffortBox", centralProvider == "codex" ? "high" : "max");
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = allocation.Length;
        ((ComboBox)panel.FindName("WorkerCountBox")).SelectedItem = allocation.Sum();
        // Adjust through the same observable rows that the per-group ComboBoxes bind to.
        foreach (var group in panel.Groups) group.WorkerCount = 1;
        for (var index = 0; index < allocation.Length; index++) panel.Groups[index].WorkerCount = allocation[index];
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Coordinate the shared group objective " + label;
        var managerConfig = panel.OrchestratorConfiguration; var workerConfig = panel.WorkerConfiguration;
        Check(label + " editable allocation retains unequal subsets", panel.WorkerAllocation.SequenceEqual(allocation) && panel.WorkerCount == allocation.Sum());
        Call(shell, "OnBridgeSetupStart", panel, EventArgs.Empty);
        Check(label + " actual fresh setup accepts all groups", ((TextBlock)panel.FindName("ErrorText")).Visibility == Visibility.Collapsed);
        var panes = secondary ? vm.SecondaryBridgePanes : vm.BridgePanes;
        foreach (var pane in panes) Chats.Add(pane);
        var managers = panes.Where(p => p.IsBridgeManager).ToArray();
        Check(label + " complete roster and ownership exist before pumping any provider objective", managers.Length == allocation.Length &&
            panes.Count == allocation.Length + allocation.Sum() && panes.Where(p => !p.IsBridgeManager).All(p => managers.Any(m => m.BridgeAgentId == p.BridgeCoordinatorAgentId)));
        Check(label + " central planner starts before either permanent assignment", managers.All(m => !m.Items.OfType<UserItem>().Any()) &&
            Tool(managers[0], "bridge_list_tasks")["central_orchestrator"]?["state"]?.ToString() == "planning");
        Pump(() => panes.All(p => p.Status == "idle" && p.SessionId is not null) && managers.All(p => UserRequests(p).Any()));
        Check(label + " one selected terminal across all groups", panes.All(p => p.BridgeSingleTerminal) && panes.Count(p => p.BridgeTerminalSelected) == 1);
        for (var index = 0; index < managers.Length; index++)
        {
            var manager = managers[index]; var workers = panes.Where(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId).ToArray();
            Check(label + $" group {index + 1} selected role settings and worker subset", Config(manager) == managerConfig && workers.Length == allocation[index] && workers.All(w => Config(w) == workerConfig));
            Check(label + $" group {index + 1} receives only its scoped assignment and count", manager.Items.OfType<UserItem>().Any(item =>
                item.Text.Contains("Coordinate " + allocation[index] + " worker") && item.Text.Contains("Central orchestrator assignment") &&
                item.Text.Contains("Own bounded area " + (index + 1)) && !item.Text.Contains("Coordinate the shared group objective")));
            Check(label + $" group {index + 1} first wire settings", FirstUserSettings(manager) == managerConfig);
            if (manager.Provider == "claude") AssertAcknowledged(manager, label + " manager " + index);
        }
        var listing = Tool(managers[0], "bridge_list_agents");
        VerifyCentralHandoff(host, managers, panel.CentralConfiguration, allocation, label);
        if (!secondary && orchestratorProvider == "codex") RenderCentralNotice(shell, managers[0], "central-complete");
        Check(label + " dispatch waits for every worker plan", !listing["coordination_ready"]!.GetValue<bool>());
        var worker0 = panes.First(p => p.BridgeCoordinatorAgentId == managers[0].BridgeAgentId);
        RejectTool(label + " pre-agreement dispatch blocked", () => Dispatch(managers[0], worker0));
        RejectTool(label + " managers cannot replace central scope", () => Tool(managers[0], "bridge_agree_scope", new()
            { ["plan_version"] = listing["plan_version"]!.DeepClone(), ["scope"] = "Take all groups' work" }));
        RejectTool(label + " empty central worker plans do not unlock dispatch", () => Tool(managers[0], "bridge_set_plan", new()
            { ["plan_version"] = listing["plan_version"]!.DeepClone(), ["steps"] = new JsonArray() }));
        for (var index = 0; index < managers.Length; index++)
        {
            var manager = managers[index];
            PublishCentralPlan(manager, panes.ToArray());
            if (index < managers.Length - 1)
                RejectTool(label + " first group cannot dispatch before the last plan", () => Dispatch(manager, panes.First(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId)));
        }
        Check(label + " all published plans unlock coordination without peer messages", Tool(managers[0], "bridge_list_agents")["coordination_ready"]!.GetValue<bool>());
        RejectTool(label + " cross-group dispatch blocked", () => Dispatch(managers[0], panes.First(p => p.BridgeCoordinatorAgentId == managers[1].BridgeAgentId)));
        RejectTool(label + " peer orchestrator dispatch blocked", () => Dispatch(managers[0], managers[1]));
        if (dispatchWork) foreach (var manager in managers)
            foreach (var worker in panes.Where(p => p.BridgeCoordinatorAgentId == manager.BridgeAgentId))
            {
                Dispatch(manager, worker);
                Pump(() => worker.Status == "idle" && UserRequests(worker).Any());
                Check(label + " owned worker receives settings before task", FirstUserSettings(worker) == workerConfig);
                if (worker.Provider == "claude") AssertAcknowledged(worker, label + " worker " + worker.BridgeLabel);
            }
        if (secondary) Check(label + " secondary group launch preserves primary roster", vm.BridgePanes.Count == 0);
        else VerifySavedGroups(vm, host, allocation);
        return (vm, host);
    }

    static void VerifySavedGroups(MainViewModel vm, ChatViewModel host, int[] allocation)
    {
        vm.SaveBridge();
        var loaded = (AppSettings)CallStatic(typeof(AppSettings), "Load")!;
        Check("disk setup count and unequal allocation persist", loaded.BridgeOrchestratorCount == allocation.Length && loaded.BridgeOrchestratorWorkerCounts!.SequenceEqual(allocation));
        var snapshot = loaded.SavedBridges.Single(b => b.HostSessionId == host.SessionId);
        var restored = new MainViewModel();
        var restoredHost = Chat(host.Cwd, host.Provider, true); restoredHost.SessionId = snapshot.HostSessionId;
        Field(restoredHost, "_session", new LedgerSession()); Field(restoredHost, "_bridgeSessionInitialized", true); restoredHost.Status = "idle"; restored.Chats.Add(restoredHost);
        Call(restored, "Track", restoredHost);
        foreach (var row in snapshot.Peers)
        {
            var peer = Chat(row.Cwd, row.Provider!, true); peer.SessionId = row.SessionId;
            Field(peer, "_session", new LedgerSession()); Field(peer, "_bridgeSessionInitialized", true); peer.Status = "idle"; restored.Chats.Add(peer);
            Call(restored, "Track", peer);
        }
        AppSettings.Current.SavedBridges = loaded.SavedBridges;
        Check("production RestoreBridge restores selected multi-group roster", restored.RestoreBridge(restoredHost));
        var managers = restored.BridgePanes.Where(p => p.IsBridgeManager).ToArray();
        Check("restored group counts preserve unequal allocation", managers.Select(m => restored.BridgePanes.Count(p => p.BridgeCoordinatorAgentId == m.BridgeAgentId)).SequenceEqual(allocation));
        Check("restored worker ownership retains saved agent IDs", managers.All(m => vm.BridgePanes.Any(p => p.SessionId == m.SessionId && p.BridgeAgentId == m.BridgeAgentId)) &&
            restored.BridgePanes.Where(p => !p.IsBridgeManager).All(p => managers.Any(m => m.BridgeAgentId == p.BridgeCoordinatorAgentId)));
        Check("restored role settings match every persisted row", restored.BridgePanes.All(p => Config(p) == (p.SessionId == snapshot.HostSessionId ? snapshot.HostConfiguration : snapshot.Peers.Single(row => row.SessionId == p.SessionId).Configuration)));
        Check("restored shared terminal retains its exact scope agreement", restored.BridgePanes.All(p => p.BridgeSingleTerminal) && Tool(managers[0], "bridge_list_agents")["coordination_ready"]!.GetValue<bool>());
        File.WriteAllText(Path.Combine(Root, "snapshot-" + host.Provider + ".json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void VerifyAdditionalGroups(MainViewModel vm, ChatViewModel host)
    {
        var oldManagers = vm.BridgePanes.Where(p => p.IsBridgeManager).ToArray();
        var oldOwners = vm.BridgePanes.ToDictionary(p => p, p => p.BridgeCoordinatorAgentId);
        var spare = Chat(host.Cwd, "codex", true); spare.Start(); Pump(() => spare.Status == "idle" && spare.SessionId is not null);
        spare.BridgeLabel = "Codex " + (vm.BridgePanes.Count + 1); vm.BridgePanes.Add(spare); Call(vm, "Track", spare);
        var before = vm.BridgePanes.Count;
        var added = vm.LaunchBridgeOrchestrators(host, [1, 2], "Coordinate an additional pair of groups", false,
            "claude", new("claude", "claude-fable-5", null), new("codex", "gpt-6-luna", "medium"));
        foreach (var pane in vm.BridgePanes) Chats.Add(pane);
        Check("add-group path creates requested managers and reuses only unassigned worker", added.Count == 2 && vm.BridgePanes.Count == before + 4 && spare.BridgeCoordinatorAgentId == added[0].BridgeAgentId);
        Check("add-group path preserves old worker ownership", oldOwners.All(pair => pair.Key.BridgeCoordinatorAgentId == pair.Value));
        Check("add-group path creates unequal new worker groups", added.Select(m => vm.BridgePanes.Count(p => p.BridgeCoordinatorAgentId == m.BridgeAgentId)).SequenceEqual(new[] { 1, 2 }));
        Pump(() => vm.BridgePanes.All(p => p.Status == "idle" && p.SessionId is not null) && added.All(p => UserRequests(p).Any()));
        foreach (var manager in added) AssertAcknowledged(manager, "added default-effort manager " + manager.BridgeLabel);
        Check("adding groups preserves multiple-terminal choice", vm.BridgePanes.All(p => !p.BridgeSingleTerminal));
        Check("all old and new orchestrators must agree again", oldManagers.Concat(added).All(p => !(bool)Property(p, "BridgeScopeConfirmed")!));
        Check("additional central split preserves earlier scopes", oldManagers.All(p => p.BridgeOrchestrationScope.StartsWith("Own bounded area")));
        var owners = vm.BridgePanes.ToDictionary(p => p, p => p.BridgeCoordinatorAgentId);
        try { vm.LaunchBridgeOrchestrators(host, [4, 4], "Over capacity"); Check("over-limit add rejected", false); }
        catch (ArgumentException) { Check("over-limit add rejected before roster changes", owners.Count == vm.BridgePanes.Count && owners.All(pair => pair.Key.BridgeCoordinatorAgentId == pair.Value)); }
    }

    static void VerifyLimitsAndDrafts(ChatViewModel host)
    {
        AppSettings.Current.BridgeOrchestratorCount = 2; AppSettings.Current.BridgeOrchestratorWorkerCounts = [1, 2];
        var panel = new BridgeSetupPanel(); panel.Configure(host, 8, advancedOnly: true);
        Check("new setup restores last successful count and allocation", panel.OrchestratorCount == 2 && panel.WorkerAllocation.SequenceEqual(new[] { 1, 2 }));
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Unsent multi-group task";
        panel.Suspend(); panel.Configure(host, 8, advancedOnly: true);
        Check("dismissed setup keeps task and unequal allocation draft", panel.Objective == "Unsent multi-group task" && panel.WorkerAllocation.SequenceEqual(new[] { 1, 2 }));
        panel.Configure(host, 3, advancedOnly: true);
        Check("reduced capacity clamps retained allocation to overall limit", panel.OrchestratorCount == 2 && panel.WorkerAllocation.SequenceEqual(new[] { 1, 1 }) && panel.WorkerCount + panel.OrchestratorCount == 4);
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = 1;
        Check("single-orchestrator selection retains legacy worker count semantics", panel.OrchestratorCount == 1 && panel.WorkerAllocation.Count == 1 && panel.WorkerAllocation[0] == panel.WorkerCount);
        var noRoom = new BridgeSetupPanel(); noRoom.Configure(host, 0, advancedOnly: true);
        ((TextBox)noRoom.FindName("ObjectiveBox")).Text = "No capacity";
        Check("no capacity leaves launch disabled and explains recovery", !((Button)noRoom.FindName("StartButton")).IsEnabled && ((TextBlock)noRoom.FindName("CountNote")).Text.Contains("Settings"));
        panel.Suspend(); noRoom.Suspend();
    }

    static void VerifySetupRenders(ChatViewModel host)
    {
        AppSettings.Current.BridgeOrchestratorCount = 3; AppSettings.Current.BridgeOrchestratorWorkerCounts = [1, 2, 3];
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            var resources = Application.Current.Resources;
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{theme}.xaml", UriKind.Relative)));
            foreach (var width in new[] { 620, 400 })
            {
                var panel = new BridgeSetupPanel(); panel.Configure(host, 11, advancedOnly: true);
                ((TextBox)panel.FindName("ObjectiveBox")).Text = "Build the catalogue, checkout and account settings. Verify the integration across all three groups.";
                var height = width == 620 ? 920 : 650;
                var border = new Border { Child = panel, Background = (Brush)resources["Bg1"] };
                border.Measure(new Size(width, height)); border.Arrange(new Rect(0, 0, width, height)); border.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(border);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(Root, $"setup-{theme.ToLowerInvariant()}-{width}.png"))) encoder.Save(stream);
                Check($"{theme} {width}px count/allocation and sticky footer fit", panel.Groups.Count == 3 &&
                    ((ComboBox)panel.FindName("OrchestratorCountBox")).ActualWidth > 80 && ((Button)panel.FindName("StartButton")).ActualHeight >= 36);
                border.Child = null; panel.Suspend();
            }
            resources.MergedDictionaries.RemoveAt(resources.MergedDictionaries.Count - 1);
        }
    }

    static void VerifyAutomaticGroups(ChatViewModel host)
    {
        AppSettings.Current.BridgeOrchestratorCount = 2; AppSettings.Current.BridgeOrchestratorWorkerCounts = null;
        var panel = new BridgeSetupPanel(); panel.Configure(host, 8, advancedOnly: true);
        ((TextBox)panel.FindName("ObjectiveBox")).Text = "Suggest groups for the catalogue and account settings.";
        var starts = 0; panel.StartRequested += (_, _) => starts++;
        ((Button)panel.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => starts == 1 || ((TextBlock)panel.FindName("ErrorText")).Visibility == Visibility.Visible);
        Check("AI suggestion honors selected orchestrators and creates one allocation per group", starts == 1 && panel.WorkerAllocation.SequenceEqual(new[] { 1, 1 }));
        ((ComboBox)panel.FindName("OrchestratorCountBox")).SelectedItem = 3;
        ((ComboBox)panel.FindName("WorkerCountBox")).SelectedIndex = 0;
        ((Button)panel.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => ((TextBlock)panel.FindName("ErrorText")).Visibility == Visibility.Visible);
        Check("AI answer below selected group minimum stays recoverable without launching", starts == 1 && panel.Objective.Contains("catalogue") && ((ComboBox)panel.FindName("WorkerCountBox")).IsEnabled);
        ((ComboBox)panel.FindName("WorkerCountBox")).SelectedItem = 4;
        ((Button)panel.FindName("StartButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("manual allocation recovers invalid automatic response", starts == 2 && panel.WorkerAllocation.SequenceEqual(new[] { 2, 1, 1 }));
        var dialog = new VibeCode.BridgeOrchestratorWindow(host, 8, panel);
        Check("add-orchestrator window forwards the selected count and unequal allocation", dialog.OrchestratorCount == 3 && dialog.WorkerAllocation.SequenceEqual(new[] { 2, 1, 1 }));
        dialog.Close(); panel.Suspend();
    }

    static JsonObject Tool(ChatViewModel chat, string name, JsonObject? args = null) =>
        BridgeMcpTools.Create((tool, input) => ((Func<string, JsonObject, JsonObject>)Property(chat, "BridgeToolHandler")!)(tool, input))
            .Single(tool => tool.Name == name).Invoke(args ?? new());
    static object? Property(object obj, string name) => obj.GetType().GetProperty(name, F)!.GetValue(obj);
    static void Dispatch(ChatViewModel manager, ChatViewModel worker) => Tool(manager, "bridge_dispatch_task", new()
        { ["recipient"] = worker.BridgeAgentId, ["task_id"] = "central-" + worker.BridgeAgentId,
          ["task_name"] = "Verify owned area", ["message"] = "Verify your owned area and report evidence for the shared goal." });
    static void RejectTool(string label, Action action)
    {
        try { action(); Check(label, false); } catch (StatusValidationException) { Check(label, true); }
    }
}
