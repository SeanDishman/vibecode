using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeCode;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifySharedTerminalScrolling(Grid host, Window window, MainWindow shell)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var agents = new[]
        {
            new ChatViewModel(Environment.CurrentDirectory, provider: "codex") { BridgeLabel = "Agent 1" },
            new ChatViewModel(Environment.CurrentDirectory, provider: "codex") { BridgeLabel = "Agent 2" },
        };
        using var model = new BridgeSharedTerminalViewModel(_ => { }, (_, _) => { });
        typeof(BridgeSharedTerminalViewModel).GetMethod("Reconcile", flags)!.Invoke(model, [agents]);
        var originalSize = new Size(window.Width, window.Height);
        try
        {
            window.Width = 1100;
            window.Height = 650;
            for (var i = 0; i < 80; i++)
                agents[i % 2].Items.Add(new TextItem { Text = $"Agent update {i}.\n\nThe current check passed." });
            var empty = new TextItem();
            agents[0].Items.Add(empty);
            var reply = new TextItem { Text = "The next agent reply is starting." };
            agents[1].Items.Add(reply);
            var panel = (BridgeSharedTerminal)shell.FindName("BridgeSharedPanel");
            ((Panel)panel.Parent).Children.Remove(panel);
            panel.Resources.MergedDictionaries.Add(shell.Resources);
            panel.DataContext = model;
            panel.Visibility = Visibility.Visible;
            host.Children.Add(panel);
            Pump("actual shared-terminal control loads with interleaved agent messages");
            var list = (ListBox)panel.FindName("SharedTranscript");
            var scroll = FindScrollViewer(list)!;
            scroll.ScrollChanged += (_, change) =>
            {
                if (!ReferenceEquals(change.OriginalSource, scroll)) return;
                ScrollEvents.Enqueue($"shared offset={change.VerticalOffset:0.##} delta={change.VerticalChange:0.##} " +
                    $"extent={change.ExtentHeight:0.##} delta={change.ExtentHeightChange:0.##} viewport={change.ViewportHeight:0.##}");
                while (ScrollEvents.Count > 16) ScrollEvents.Dequeue();
            };
            Check(AtBottom(scroll), $"actual single terminal starts pinned ({Position(scroll)})");
            Check(FindVisual<MarkdownView>(list) is not null, "single-terminal regression renders real markdown message templates");

            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            Pump("single-terminal manual arrival settles");
            for (var delta = 0; delta < 4; delta++)
            {
                reply.Append("\n\n" + string.Join("\n\n", Enumerable.Repeat("More results arrived from the agent.", 12)));
                Pump("actual shared-terminal reply grows");
                Check(AtBottom(scroll), $"single terminal follows growing agent replies, delta {delta} ({Position(scroll)})");
            }

            empty.Append("An initially empty reply now contains the other agent's results.");
            Pump("actual shared activity refreshes when an empty message gets content");
            Check(AtBottom(scroll), "shared activity filter refresh keeps following the bottom");
            var thinking = new ThinkingItem { Text = "The agent is checking its result." };
            agents[0].Items.Add(thinking);
            Pump("single-terminal thought process appears");
            thinking.Append("\n" + new string('x', 600));
            Pump("single-terminal thought process grows");
            Check(AtBottom(scroll), "visible thought-process growth keeps following the bottom");
            agents[0].Items.Remove(thinking);
            Pump("finished thought process is removed");
            Check(AtBottom(scroll), "shrinking shared activity stays pinned");

            scroll.ScrollToVerticalOffset(300);
            Pump("single-terminal reader moves to older messages");
            Check(!AtBottom(scroll), "single-terminal reader can detach from live output");
            agents[0].Items.Add(new TextItem { Text = "New output while the user reads an earlier reply." });
            reply.Append("\n\nThe reply continues while the user reads.");
            Pump("shared output grows while reading older messages");
            Check(!AtBottom(scroll), "actual shared-terminal output leaves the reader detached");

            for (var visit = 0; visit < 3; visit++)
            {
                panel.Visibility = Visibility.Collapsed;
                Pump("single terminal is hidden while another chat is selected");
                agents[1].Items.Add(new TextItem { Text = $"Latest background result on return {visit}." });
                panel.Visibility = Visibility.Visible;
                Pump("returning to the same single terminal lays out its newest activity");
                Check(AtBottom(scroll), $"return {visit} opens the single terminal at the latest activity ({Position(scroll)})");
                reply.Append("\n\nA live update after returning to the bridge.");
                Pump("streaming continues after returning to the single terminal");
                Check(AtBottom(scroll), "returning to the bridge resumes following live output");
                scroll.ScrollToVerticalOffset(300);
                Pump("reader can scroll up again after reopening the bridge");
                Check(!AtBottom(scroll), "reopened single terminal still allows reading earlier activity");
            }

            using (var anotherModel = new BridgeSharedTerminalViewModel(_ => { }, (_, _) => { }))
            {
                typeof(BridgeSharedTerminalViewModel).GetMethod("Reconcile", flags)!.Invoke(anotherModel, [new[] { agents[1] }]);
                panel.DataContext = anotherModel;
                Pump("a different bridge model replaces the visible single terminal");
                Check(AtBottom(scroll), "switching bridge conversations opens the newest activity");
                panel.DataContext = model;
                Pump("the original bridge conversation is restored");
                Check(AtBottom(scroll), "restoring the original bridge conversation opens the newest activity");
            }
            scroll.ScrollToBottom();
            Pump("single-terminal reader returns to the bottom");
            agents[1].Items.Add(new TextItem { Text = "Another agent sends its latest result." });
            Pump("actual shared output arrives after returning to the bottom");
            Check(AtBottom(scroll), "actual shared terminal resumes following new messages");
        }
        finally
        {
            host.Children.Clear();
            foreach (var agent in agents) agent.Close();
            window.Width = originalSize.Width;
            window.Height = originalSize.Height;
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
