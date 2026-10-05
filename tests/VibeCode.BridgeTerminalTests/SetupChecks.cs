using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifySetupStartup()
    {
        var (vm, team) = Team("connecting-workers", 3);
        team[0].Status = "idle";
        Check("connecting workers contribute to the available capacity", vm.MaximumOrchestratorWorkers(team[0], false) == 8);
        vm.ConfigureBridgeOrchestrator(team[0], 2, "Implement and verify account switching.", false);
        Check("connecting peers are reused without launching extra workers", vm.BridgePanes.Count == 3 &&
            team.Skip(1).All(p => p.BridgeCoordinatorAgentId == team[0].BridgeAgentId));
        Check("multiple terminal choice survives team configuration", vm.BridgePanes.All(p => !p.BridgeSingleTerminal));

        var (waitingVm, waiting) = Team("refreshing-coordinator", 2);
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(waiting[0], null);
        waitingVm.ConfigureBridgeOrchestrator(waiting[0], 1, "Coordinate once sign-in is refreshed.", true);
        Check("orchestrator objective survives credential refresh before session creation", waiting[0].HasQueued &&
            waiting[0].Items.OfType<QueuedItem>().Any(q => q.Text.Contains("Coordinate once sign-in is refreshed")));
        var readySession = new FakeSession();
        typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(waiting[0], readySession);
        waiting[0].Status = "idle";
        PumpUntil(() => readySession.Sent.Count == 1);
        Check("queued orchestrator goal is delivered after initialization", readySession.Sent[0].Contains("Coordinate once sign-in is refreshed"));

        var (failedVm, failed) = Team("disconnected-coordinator", 2);
        failed[0].Status = "error";
        try { failedVm.ConfigureBridgeOrchestrator(failed[0], 1, "Do not mutate this team.", true); throw new Exception("Disconnected orchestrator accepted"); }
        catch (InvalidOperationException) { }
        Check("disconnected coordinator fails before roles or roster change", !failed[0].IsBridgeManager &&
            failed[1].BridgeCoordinatorAgentId is null && failedVm.BridgePanes.Count == 2);
        Check("fresh team capacity excludes an original chat that cannot fill a selected role",
            failedVm.MaximumFreshOrchestratorWorkers == 8);

        var previous = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            var (groupVm, group) = Team("preserve-group-layout", 2);
            foreach (var agent in group) agent.Status = "idle";
            groupVm.SetBridgeTerminalMode(false);
            var coordinator = groupVm.LaunchBridgeOrchestrator(group[0], 2, "Coordinate existing workers.");
            Chats.Add(coordinator);
            Check("adding an orchestrator preserves multiple terminals", groupVm.BridgePanes.Count == 3 &&
                groupVm.BridgePanes.All(p => !p.BridgeSingleTerminal));
            PumpUntil(() => coordinator.Status is "idle" or "error");
            Check("new orchestrator starts through provider adapter", coordinator.Status == "idle" &&
                group.All(p => p.BridgeCoordinatorAgentId == coordinator.BridgeAgentId));
            groupVm.CloseBridge();
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previous); }
    }

    private static void VerifyBridgeSetupViews(ChatViewModel host)
    {
        // These cases exercise the legacy fresh-install defaults; saved multi-group setup has its own checks.
        AppSettings.Current.BridgeOrchestratorCount = 1;
        AppSettings.Current.BridgeOrchestratorWorkerCounts = null;
        T Find<T>(BridgeSetupPanel panel, string name) => (T)panel.FindName(name);
        void Click(BridgeSetupPanel panel, string name) => Find<Button>(panel, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        BridgeSetupPanel Panel(bool advanced = false)
        {
            var panel = new BridgeSetupPanel();
            panel.Configure(host, 8, advancedOnly: advanced);
            return panel;
        }

        var regular = Panel();
        Check("regular bridge hides task and orchestration controls", !regular.IsAdvanced &&
            Find<FrameworkElement>(regular, "AdvancedFields").Visibility == Visibility.Collapsed);
        Check("regular provider dropdown includes all supported providers", Find<ComboBox>(regular, "PeerProviderBox")
            .Items.OfType<BridgeSetupPanel.ProviderChoice>().Select(p => p.Id).Order()
            .SequenceEqual(new[] { "claude", "codex", "grok", "kimi", "glm" }.Order()));
        var starts = 0;
        regular.StartRequested += (_, _) => starts++;
        foreach (var provider in BridgeSetupPanel.Providers)
        {
            Find<ComboBox>(regular, "PeerProviderBox").SelectedValue = provider.Id;
            Click(regular, "StartButton");
            Check("regular starts the selected provider " + provider.Id, regular.Provider == provider.Id && !regular.SingleTerminal);
        }
        Check("regular bridge starts without an orchestrator task", starts == 5);
        Find<ComboBox>(regular, "PeerProviderBox").SelectedValue = "claude";
        CaptureSetup(regular, 460, "start-bridge-regular.png");

        var panel = Panel();
        Find<RadioButton>(panel, "AdvancedChoice").IsChecked = true;
        var options = Find<BridgeAgentOptionsPanel>(panel, "AgentOptions");
        var orchestratorReview = (ComboBox)options.FindName("OrchestratorReviewBox");
        var workerReview = (ComboBox)options.FindName("WorkerReviewBox");
        Check("team setup exposes review choices separately from reasoning effort",
            orchestratorReview.Items.OfType<BridgeReviewChoice>().Select(choice => choice.Label).SequenceEqual(new[] { "None", "Low", "Normal", "High" }) &&
            (string)orchestratorReview.SelectedValue == host.BridgeReviewLevel);
        var originalModel = panel.OrchestratorConfiguration.Model;
        var originalEffort = panel.OrchestratorConfiguration.Effort;
        orchestratorReview.SelectedValue = "none";
        workerReview.SelectedValue = "low";
        Check("setup review choices are independent for orchestrators and workers",
            panel.OrchestratorConfiguration.ReviewLevel == "none" && panel.WorkerConfiguration.ReviewLevel == "low" &&
            panel.OrchestratorConfiguration.Model == originalModel && panel.OrchestratorConfiguration.Effort == originalEffort);
        var objective = Find<TextBox>(panel, "ObjectiveBox");
        Check("advanced choice reveals team settings in the same panel", panel.IsAdvanced &&
            Find<FrameworkElement>(panel, "AdvancedFields").Visibility == Visibility.Visible);
        Check("blank task prevents starting and has no Suggest action", panel.FindName("SuggestButton") is null &&
            !Find<Button>(panel, "StartButton").IsEnabled);
        Check("automatic sizing remains available when starting a completed task", panel.AutomaticWorkers);
        Check("task microphone is labeled and available", Find<Button>(panel, "MicButton").IsEnabled &&
            AutomationProperties.GetName(Find<Button>(panel, "MicButton")) == "Dictate team task");
        CaptureSetup(panel, 460, "start-bridge-advanced-empty.png");
        objective.Text = "Repair account switching and verify bridge message delivery.";
        Find<ComboBox>(panel, "WorkerCountBox").SelectedItem = 3;
        Find<RadioButton>(panel, "SeparateTerminalsChoice").IsChecked = true;
        Check("manual count and terminal choice are preserved", panel.WorkerCount == 3 && !panel.SingleTerminal &&
            Find<TextBlock>(panel, "TeamSizeText").Text == "1 orchestrator + 3 workers");
        Find<ComboBox>(panel, "OrchestratorProviderBox").SelectedValue = "kimi";
        Check("advanced provider determines orchestrator selection", panel.Provider == "kimi");
        var workerProvider = (ComboBox)options.FindName("WorkerProviderBox");
        workerProvider.SelectedValue = "claude";
        workerProvider.SelectedValue = host.Provider;
        Check("changing model providers keeps the role's selected review amount",
            panel.OrchestratorConfiguration.ReviewLevel == "none" && panel.WorkerConfiguration.ReviewLevel == "low");
        Find<RadioButton>(panel, "RegularChoice").IsChecked = true;
        Find<RadioButton>(panel, "AdvancedChoice").IsChecked = true;
        Check("switching modes preserves typed task and team settings", panel.Objective.StartsWith("Repair account") && panel.WorkerCount == 3);
        panel.Suspend();
        panel.Configure(host, 8);
        Check("dismissing and reopening setup preserves the unsent task", panel.Objective.StartsWith("Repair account") &&
            panel.IsAdvanced && panel.WorkerCount == 3 && panel.Provider == "kimi");
        Check("dismissing and reopening preserves both review choices", panel.OrchestratorConfiguration.ReviewLevel == "none" &&
            panel.WorkerConfiguration.ReviewLevel == "low");
        CaptureSetup(panel, 460, "start-bridge-advanced.png");
        Descendants<ScrollViewer>(panel).First().ScrollToEnd();
        CaptureSetup(panel, 460, "start-bridge-advanced-review-settings.png");
        var reviewTop = workerReview.TransformToAncestor(panel).Transform(new Point()).Y;
        Check("worker review dropdown stays accessible above the setup footer", reviewTop >= 60 &&
            reviewTop + workerReview.ActualHeight < panel.ActualHeight - 50 && workerReview.ActualHeight >= 36);
        Descendants<ScrollViewer>(panel).First().ScrollToTop();
        var content = (ScrollViewer)objective.Template.FindName("PART_ContentHost", objective);
        Check("task text begins at the top instead of floating in the middle", content.VerticalAlignment == VerticalAlignment.Top &&
            objective.GetRectFromCharacterIndex(0).Top <= 14 && objective.ActualHeight >= objective.MinHeight &&
            objective.ActualHeight <= objective.MaxHeight);
        Capture(panel, 400, 420, "start-bridge-advanced-small.png");
        var taskRect = objective.GetRectFromCharacterIndex(0);
        Check("compact panel keeps caret and footer usable", taskRect.Top <= 14 && Find<Button>(panel, "StartButton").ActualHeight >= 36);

        var previous = Environment.GetEnvironmentVariable("VIBECODE_CODEX_PATH");
        Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"));
        try
        {
            var automatic = Panel(true);
            Find<TextBox>(automatic, "ObjectiveBox").Text = "Fix login and test account switching.";
            var requested = 0;
            automatic.StartRequested += (_, _) => requested++;
            Click(automatic, "StartButton");
            Check("automatic start shows pending state", !Find<Button>(automatic, "StartButton").IsEnabled);
            PumpUntil(() => requested == 1 || Find<TextBlock>(automatic, "ErrorText").Visibility == Visibility.Visible);
            Check("automatic start waits for a real planning adapter response", requested == 1 && automatic.WorkerCount == 2);
            Find<TextBox>(automatic, "ObjectiveBox").Text = "invalid team size fixture";
            Find<ComboBox>(automatic, "WorkerCountBox").SelectedIndex = 0;
            Click(automatic, "StartButton");
            PumpUntil(() => Find<TextBlock>(automatic, "ErrorText").Visibility == Visibility.Visible);
            Check("invalid AI answer keeps task and offers manual recovery", requested == 1 &&
                automatic.Objective == "invalid team size fixture" && Find<ComboBox>(automatic, "WorkerCountBox").IsEnabled &&
                Find<TextBlock>(automatic, "ErrorText").Text.Contains("Choose a worker count"));
            Find<ComboBox>(automatic, "WorkerCountBox").SelectedItem = 1;
            Click(automatic, "StartButton");
            Check("manual count recovers from failed AI suggestion", requested == 2 && automatic.WorkerCount == 1);
            automatic.Suspend();
        }
        finally { Environment.SetEnvironmentVariable("VIBECODE_CODEX_PATH", previous); }

        var setup = new BridgeOrchestratorWindow(host, 4);
        var setupPanel = (BridgeSetupPanel)setup.FindName("SetupPanel");
        Find<TextBox>(setupPanel, "ObjectiveBox").Text = "Review the team’s work and finish remaining fixes.";
        Find<ComboBox>(setupPanel, "WorkerCountBox").SelectedItem = 2;
        Check("add-orchestrator dialog uses the same task and microphone controls", setup.WorkerCount == 2 &&
            setupPanel.IsAdvanced && Find<Button>(setupPanel, "MicButton").IsEnabled);
        var setupContent = (FrameworkElement)setup.Content;
        setup.Content = null;
        CaptureSetup(setupContent, 500, "orchestrator-setup.png");
        setup.Close();

        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Cli.xaml", UriKind.Relative)));
        var cli = Panel();
        CaptureSetup(cli, 460, "start-bridge-regular-cli.png");
        var cliAdvanced = Panel();
        Find<RadioButton>(cliAdvanced, "AdvancedChoice").IsChecked = true;
        Find<TextBox>(cliAdvanced, "ObjectiveBox").Text = "Build a store catalogue and check the filtering.";
        CaptureSetup(cliAdvanced, 460, "start-bridge-advanced-cli.png");
        Descendants<ScrollViewer>(cliAdvanced).First().ScrollToEnd();
        CaptureSetup(cliAdvanced, 460, "start-bridge-advanced-review-settings-cli.png");
        Check("CLI theme fits the advanced fields at natural height", cliAdvanced.ActualHeight > 500);
        resources.MergedDictionaries.RemoveAt(resources.MergedDictionaries.Count - 1);
        regular.Suspend(); panel.Suspend(); cli.Suspend(); cliAdvanced.Suspend();
    }

    private static void CaptureSetup(FrameworkElement panel, int width, string name)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        Capture(panel, width, Math.Min(720, (int)Math.Ceiling(panel.DesiredSize.Height)), name);
    }
}
