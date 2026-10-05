using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using VibeCode;
using VibeCode.UI;

internal static partial class Program
{
    // Inspect the completed Luna reports with fake sessions: visual confirmation makes no model calls.
    private static void ReplayLiveResults()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "shared-agent-panel", "live-simulations", "two-orchestrators.json");
        var results = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var (vm, team) = Team("live-result-replay", 4);
        foreach (var chat in team) chat.Status = "idle";
        team[0].IsBridgeManager = team[2].IsBridgeManager = true;
        Property(team[0], "BridgeCoordinatesOnly", true); Property(team[2], "BridgeCoordinatesOnly", true);
        Property(team[1], "BridgeCoordinatorAgentId", team[0].BridgeAgentId);
        Property(team[3], "BridgeCoordinatorAgentId", team[2].BridgeAgentId);
        Call(vm, "ReconcileBridgeGroups", (object)team);
        for (var i = 0; i < team.Length; i++)
        {
            var result = results[i]!;
            var chat = team[i];
            chat.Items.Clear(); chat.Status = "idle"; chat.Model = "gpt-6-luna"; chat.Effort = "low";
            Property(chat, "BridgeTaskState", result["task_state"]!.GetValue<string>());
            Property(chat, "BridgeReviewState", result["review_state"]!.GetValue<string>());
            Property(chat, "BridgeReviewSummary", result["review_summary"]!.GetValue<string>());
            Property(chat, "BridgeScopeConfirmed", true);
            Property(chat, "BridgeOrchestrationScope", result["scope"]!.GetValue<string>());
            Property(chat, "BridgeTaskName", i % 2 == 0 ? "Final review" : i == 1 ? "Calculate sum" : "Verify subtraction");
            Property(chat, "BridgeActivitySummary", i % 2 == 0 ? result["review_summary"]!.GetValue<string>() : "Completed the assigned arithmetic check.");
            if (i % 2 == 0) chat.Items.Add(new DividerItem { Label = "Final review passed · Both orchestrator groups completed" });
            chat.Items.Add(new TextItem { Text = result["messages"]!.AsArray().Last()!.GetValue<string>() });
        }
        foreach (var peer in team.Skip(1)) vm.Chats.Remove(peer);
        team[0].Title = "GPT Luna verification · Completed reviews";
        Property(vm, "ActiveChat", team[0]); Property(vm, "ShowBridge", true);
        vm.SetBridgeTerminalMode(true); vm.SelectBridgeTerminal(team[2]);
        foreach (var theme in new[] { "Dark", "Cli" })
        {
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/VibeCode;component/Themes/{theme}.xaml", UriKind.Relative)));
            var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
            SaveLiveShell(shell, 1400, 900, $"luna-result-replay-{theme.ToLowerInvariant()}-wide.png");
            SaveLiveShell(shell, 1000, 850, $"luna-result-replay-{theme.ToLowerInvariant()}-narrow.png");
            var panel = (BridgeSharedTerminal)shell.FindName("BridgeSharedPanel");
            CheckTranscriptBounds((ListBox)panel.FindName("SharedTranscript"), theme + " actual Luna result replay");
            Check(theme + ": replay shows both review approvals and four completed agents",
                Descendants<TextBlock>(panel).Count(t => t.Text == "Review passed") == 2 &&
                Descendants<TextBlock>(panel).Count(t => t.Text == "Completed") == 4);
        }
    }

    private static void CheckTranscriptBounds(ListBox list, string name)
    {
        var scroller = Descendants<ScrollViewer>(list).First();
        var entries = Descendants<Grid>(list).Where(g => g.Name == "EntryRoot").ToArray();
        Check(name + ": activity stays within the current viewport after resizing", entries.Length > 0 &&
            scroller.ExtentWidth <= scroller.ViewportWidth + 1 && entries.All(g =>
            {
                var x = g.TranslatePoint(new Point(0, 0), list).X;
                return x >= 0 && x + g.ActualWidth <= list.ActualWidth + 1;
            }));
    }
}
