using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeCode;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyComposerViews()
    {
        var resources = Application.Current.Resources;
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/VibeCode;component/Themes/Dark.xaml", UriKind.Relative)));
        resources["BoolVis"] = new BooleanToVisibilityConverter();
        resources["ShowIf"] = new NonEmptyToVisibilityConverter();
        var vm = new MainViewModel();
        var host = NewFake("ui-host");
        var peer = NewFake("ui-peer");
        host.Title = "Goal command verification";
        host.SessionId = "goal-test-host";
        peer.SessionId = "goal-test-peer";
        host.BridgeLabel = "Codex 1";
        peer.BridgeLabel = "Codex 2";
        vm.Chats.Add(host);
        SetProperty(vm, "ActiveChat", host);
        var sampleGoal = new ChatGoal(Guid.NewGuid().ToString("N"), "Finish the implementation and verify the result.");
        host.Items.Add(new UserItem { Text = "/goal " + sampleGoal.Text });
        host.Items.Add(new TextItem { Text = "Implementation is ready. I still need to verify the result." });
        host.Items.Add(new UserItem { Text = GoalPolicy.BuildCheck(sampleGoal) });
        var done = new TextItem { Text = "Verification passed. The goal is complete.\n" + GoalPolicy.CompleteMarker(sampleGoal) };
        done.HideGoalStatus(GoalPolicy.CompleteMarker(sampleGoal));
        host.Items.Add(done);
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
            .Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        MainWindow? cliShell = null;
        try
        {
            ShowOffscreen(shell);
            var input = (TextBox)shell.FindName("InputBox");
            var popup = (Popup)shell.FindName("SlashPopup");
            var list = (ListBox)shell.FindName("SlashList");
            // Render the actual popup child without putting a native popup over the user's desktop.
            popup.Placement = PlacementMode.Absolute;
            popup.HorizontalOffset = popup.VerticalOffset = -32000;
            Layout(shell);
            input.Text = "/";
            Check("normal composer popup contains /goal and /compact", popup.IsOpen
                && list.Items.Cast<CommandChoice>().Select(command => command.Name).Order().SequenceEqual(new[] { "compact", "goal" }));
            CaptureElement((FrameworkElement)popup.Child, 600, 116, "goal-menu-dark.png");
            list.SelectedItem = list.Items.Cast<CommandChoice>().Single(command => command.Name == "goal");
            Call(shell, "AcceptSlash");
            Check("normal slash selection inserts goal syntax", input.Text == "/goal ");
            input.Text = "/compact";
            Check("compact command is discoverable", popup.IsOpen && list.Items.Count == 1 && ((CommandChoice)list.Items[0]).Name == "compact");
            foreach (var removed in new[] { "init", "plan", "review", "status", "usage", "help", "mcp", "tasks" })
            {
                input.Text = "/" + removed;
                Check("removed command has no menu entry: " + removed, !popup.IsOpen && list.Items.Count == 0);
            }
            input.Text = "";
            CaptureElement(shell, 1280, 850, "goal-normal-dark.png");
            Check("goal check card hides internal model instructions", host.Items.OfType<UserItem>().Last().AgentBody == "Did you finish your goal?\n\n" + sampleGoal.Text);
            var queuedGoal = new QueuedItem { Owner = host, Text = "/goal A replacement goal" };
            SetProperty(queuedGoal, "IsQueueHead", true);
            var queueRow = (FrameworkElement)((DataTemplate)shell.FindResource(new DataTemplateKey(typeof(QueuedItem)))).LoadContent();
            queueRow.DataContext = queuedGoal;
            queueRow.Measure(new Size(600, 200));
            queueRow.Arrange(new Rect(0, 0, 600, 200));
            queueRow.UpdateLayout();
            Check("queued goals wait for their own turn instead of offering Steer", Descendants<Button>(queueRow).Single(b => b.Content?.ToString() == "Steer").Visibility == Visibility.Collapsed);

            vm.BridgePanes.Add(host);
            vm.BridgePanes.Add(peer);
            SetProperty(vm, "ShowBridge", true);
            vm.SetBridgeTerminalMode(false);
            Layout(shell);
            var bridge = (ItemsControl)shell.FindName("BridgePaneList");
            var bridgeInputs = Descendants<TextBox>(bridge).Where(t => t.Name == "PaneInput").ToArray();
            Check("split bridge realizes both provider composers", bridgeInputs.Length == 2);
            foreach (var bridgeInput in bridgeInputs)
            {
                var chat = (ChatViewModel)bridgeInput.DataContext;
                bridgeInput.SetCurrentValue(TextBox.TextProperty, "/");
                Call(shell, "ShowSlashCommands", bridgeInput, chat);
                Check("bridge popup targets its own composer", popup.PlacementTarget == bridgeInput && list.Items.Count == 2);
                list.SelectedItem = list.Items.Cast<CommandChoice>().Single(command => command.Name == "compact");
                Call(shell, "AcceptSlash");
                Check("bridge compact selection preserves its draft binding", bridgeInput.Text == "/compact " && chat.Draft == "/compact " && BindingOperations.IsDataBound(bridgeInput, TextBox.TextProperty));
                chat.Draft = "";
            }

            vm.SetBridgeTerminalMode(true);
            vm.SelectBridgeTerminal(peer);
            Layout(shell);
            var shared = (FrameworkElement)shell.FindName("BridgeSharedPanel");
            var sharedInput = Descendants<TextBox>(shared).Single(t => t.Name == "PaneInput");
            sharedInput.SetCurrentValue(TextBox.TextProperty, "/g");
            Call(shell, "ShowSlashCommands", sharedInput, peer);
            Call(shell, "AcceptSlash");
            Check("single-terminal bridge accepts /goal for selected target", peer.Draft == "/goal " && popup.PlacementTarget == sharedInput);
            sharedInput.SetCurrentValue(TextBox.TextProperty, "/c");
            Call(shell, "ShowSlashCommands", sharedInput, peer);
            Check("single-terminal bridge advertises native compaction for the selected target", popup.IsOpen
                && list.Items.Cast<CommandChoice>().Select(c => c.Name).SequenceEqual(new[] { "compact" }));
            Call(shell, "AcceptSlash");
            Check("single-terminal bridge inserts compact without breaking its target binding", peer.Draft == "/compact "
                && sharedInput.Text == "/compact " && BindingOperations.IsDataBound(sharedInput, TextBox.TextProperty));
            vm.SelectBridgeTerminal(host);
            Layout(shell);
            Check("slash selection keeps shared target-switch bindings intact", sharedInput.DataContext == host && sharedInput.Text == host.Draft && BindingOperations.IsDataBound(sharedInput, TextBox.TextProperty));
            // A second bounded visual pass checks the IDE's CLI theme too.
            resources.MergedDictionaries.Clear();
            resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/VibeCode;component/Themes/Cli.xaml", UriKind.Relative)));
            cliShell = (MainWindow)typeof(MainWindow).GetConstructors(Flags)
                .Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
            ShowOffscreen(cliShell);
            Layout(cliShell);
            var cliInput = Descendants<TextBox>((FrameworkElement)cliShell.FindName("BridgeSharedPanel"))
                .Single(t => t.Name == "PaneInput");
            var cliPopup = (Popup)cliShell.FindName("SlashPopup");
            cliPopup.Placement = PlacementMode.Absolute;
            cliPopup.HorizontalOffset = cliPopup.VerticalOffset = -32000;
            cliInput.SetCurrentValue(TextBox.TextProperty, "/");
            Call(cliShell, "ShowSlashCommands", cliInput, host);
            var cliList = (ListBox)cliShell.FindName("SlashList");
            Check("CLI theme exposes the same goal and compact commands", cliPopup.IsOpen
                && cliList.Items.Cast<CommandChoice>().Select(c => c.Name).Order().SequenceEqual(new[] { "compact", "goal" }));
            CaptureElement((FrameworkElement)cliPopup.Child, 600, 116, "goal-menu-cli.png");
            cliPopup.IsOpen = false;
            CaptureElement(cliShell, 1280, 850, "goal-shared-cli.png");
        }
        finally { cliShell?.Close(); shell.Close(); }
    }

    private static void SetProperty(object target, string name, object? value) => target.GetType().GetProperty(name, Flags)!.SetValue(target, value);
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return descendant;
    }
    private static void Layout(FrameworkElement element)
    {
        if (element is Window window) element = (FrameworkElement)window.Content;
        element.Measure(new Size(1280, 850));
        element.Arrange(new Rect(0, 0, 1280, 850));
        element.UpdateLayout();
    }
    private static void CaptureElement(FrameworkElement element, int width, int height, string file)
    {
        if (element is Window window) element = (FrameworkElement)window.Content;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_root, file));
        encoder.Save(output);
    }

    private static void ShowOffscreen(Window window)
    {
        window.ShowInTaskbar = window.ShowActivated = false;
        window.Left = window.Top = -32000;
        window.Width = 1280;
        window.Height = 850;
        window.Show();
    }
}
