using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void SeedUsageFixtures()
    {
        // Only the test's isolated log, never the user's usage history.
        if (!System.IO.Path.GetFullPath(UsageLog.FilePath).StartsWith(Output + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Usage fixture path is not isolated.");
        UsageLog.Instance.Clear();
        var now = DateTimeOffset.Now;
        double[] input = [10_000, 4_000, 12_000, 23_000, 16_000, 29_000, 42_000, 17_000, 31_000, 48_000, 26_000, 34_000];
        for (var i = 0; i < input.Length; i++)
            UsageLog.Instance.Record(new UsageEntry { At = now.AddMinutes(-58 + i * 5),
                Provider = i % 2 == 0 ? "codex" : "claude", Model = i % 2 == 0 ? "gpt-6.1" : "claude-opus-5",
                Input = input[i], CacheRead = input[i] / 2, Output = input[i] / 10,
                Project = @"C:\Projects\Commerce", SessionId = "workflow-fixture-" + i });
    }

    static void Activity(ChatViewModel chat, DateTimeOffset? at) =>
        typeof(ChatViewModel).GetField("_lastAgentActivityAt", Flags)!.SetValue(chat, at);

    static void VerifyRecentActivityAndUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var chat = Chat(@"C:\Recent", "Codex 1", "Recently active");
        bool Visible(DateTimeOffset at) => AgentActivitySnapshot.Build([chat], at).Count > 0;
        Activity(chat, now.AddMinutes(-9));
        Check("Agents active nine minutes ago remain on the wall", Visible(now));
        Activity(chat, now.AddMinutes(-10));
        Check("Agents remain visible immediately before ten minutes", Visible(now.AddTicks(-1)));
        Check("Idle agents disappear at exactly ten minutes", !Visible(now));
        Activity(chat, now.AddMinutes(-30));
        Check("Chats inactive for thirty minutes are excluded", !Visible(now));
        chat.Status = "running"; Activity(chat, now.AddHours(-3));
        Check("Long-running turns stay visible beyond an hour", Visible(now));
        chat.Status = "idle";
        Check("A just-finished turn remains recently active", Visible(DateTimeOffset.UtcNow));
        Activity(chat, null);
        typeof(ChatViewModel).GetField("_lastActivityAt", Flags)!.SetValue(chat, now);
        Check("Opening or messaging an unused chat does not qualify as agent activity", !Visible(now));
        chat.Status = "starting";
        Check("Booting a CLI alone does not qualify as recent work", !Visible(now));
        chat.Status = "idle";
        typeof(ChatViewModel).GetField("_lastAssistantAt", Flags)!.SetValue(chat, now.AddMinutes(-7));
        Check("Older sessions can use a genuine assistant activity timestamp", Visible(now));
        typeof(ChatViewModel).GetField("_lastAssistantAt", Flags)!.SetValue(chat, now.AddMinutes(-12));
        Check("Legacy assistant timestamps also expire after ten minutes", !Visible(now));
        chat.Close();

        var rows = new[] {
            new UsageEntry { At = now.AddHours(-1), Model = "gpt-6.1", Input = 100, CacheRead = 200, CacheWrite = 50, Output = 25 },
            new UsageEntry { At = now, Model = "claude-opus-5", Input = 20, Output = 5 },
            new UsageEntry { At = now.AddHours(-1).AddTicks(-1), Model = "gpt-6.1", Input = 10_000 },
            new UsageEntry { At = now.AddSeconds(1), Model = "gpt-6.1", Input = 10_000 },
        };
        var usage = WorkflowUsageSnapshot.Build(rows, now);
        Check("Totals include cache tiers once and exclude stale/future rows", usage.Report.Total == 400 && usage.Report.TotalIn == 370 && usage.Report.Output == 30);
        Check("Both graphs and total cover the exact same hour", usage.Buckets.Sum(b => b.Total) == usage.Report.Total
            && usage.Report.Models.Sum(m => m.Total) == usage.Report.Total);
        Check("Hourly token history is independent of the ten-minute agent window", usage.Report.Total == 400
            && usage.TokensPerMinute == 400.0 / 60 && usage.TokensPerTurn == 200 && usage.RecentTokensPerMinute == 5);
        Check("Partial five-minute edge buckets retain boundary tokens", usage.Buckets.Count == 13 && usage.Buckets.First().Total == 375 && usage.Buckets.Last().Total == 25);
        Check("No activity produces zero-filled charts", WorkflowUsageSnapshot.Build([], now).Buckets.All(b => b.Total == 0));
        var shifted = WorkflowUsageSnapshot.Build(rows.Select(e => new UsageEntry { At = e.At.ToOffset(TimeSpan.FromHours(-6)), Model = e.Model,
            Input = e.Input, CacheRead = e.CacheRead, CacheWrite = e.CacheWrite, Output = e.Output }).ToArray(), now.ToOffset(TimeSpan.FromHours(9)));
        Check("Clock offsets cannot change the token totals or bucket distribution", shifted.Buckets.Select(b => b.Total).SequenceEqual(usage.Buckets.Select(b => b.Total)));

        var source = new MainViewModel();
        var recent = Chat(@"C:\Projects\Recent", "Codex 1", "Active project", "running");
        var old = Chat(@"C:\Projects\Old", "Claude 2", "Old project"); Activity(old, now.AddMinutes(-30));
        source.Chats.Add(recent); source.Chats.Add(old);
        var speedSubscribers = SpeedSubscribers;
        var wall = new WorkflowTelemetryWindow(source) { Left = 6200, Top = 200, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        wall.Show(); wall.Refresh();
        Check("The open workflow wall listens for shared OpenRouter updates", SpeedSubscribers == speedSubscribers + 1);
        Check("The wall uses cached public readings without a network request",
            ((HashSet<string>)typeof(ModelSpeedService).GetField("_inFlight", Flags)!.GetValue(ModelSpeedService.Instance)!).Count == 0
            && ((ItemsControl)wall.FindName("ModelSpeeds")).Items.Count == 2);
        var graph = (WorkflowGraph)wall.FindName("Graph");
        var timeline = (HourlyThroughput)wall.FindName("TokenTimeline");
        var models = (ModelTokenBarChart)wall.FindName("ModelTokens");
        var totalBefore = models.Report!.Total;
        Check("The rendered wall omits stale projects and agents", graph.NodeCount == 2);
        Check("Opening the wall watches provisional streaming usage", LiveTurnTelemetry.Instance.IsWatched);
        var owner = new object();
        LiveTurnTelemetry.Instance.Report(owner, "codex", "gpt-6.1", 100, 0, 50, 25, "live-fixture", recent.Cwd);
        wall.Refresh();
        Check("Provisional usage is added once to the visible total", models.Report.Total == totalBefore + 175);
        LiveTurnTelemetry.Instance.Report(owner, "codex", "gpt-6.1", 100, 0, 50, 25, "live-fixture", recent.Cwd);
        wall.Refresh();
        Check("Repeated streaming snapshots do not inflate totals", models.Report.Total == totalBefore + 175);
        LiveTurnTelemetry.Instance.Clear(owner);
        UsageLog.Instance.Record(new UsageEntry { At = DateTimeOffset.Now, Provider = "codex", Model = "gpt-6.1", Input = 100, CacheRead = 50, Output = 25 });
        wall.Refresh();
        Check("Committed usage replaces the provisional row without a jump", models.Report.Total == totalBefore + 175);
        Check("Rendered model and timeline totals agree", timeline.Hours!.Sum(b => b.Total) == models.Report.Total);
        ((TextBox)wall.FindName("Search")).Text = "No matching agent";
        wall.Refresh();
        Check("Map filtering keeps the explicitly all-project usage totals", graph.NodeCount == 0 && models.Report.Total == totalBefore + 175);
        ((TextBox)wall.FindName("Search")).Clear();
        Capture(wall, 1920, 1080, "workflow-recent-usage.png");
        var wallScroll = (ScrollViewer)wall.FindName("WallScroll");
        var panel = (Border)wall.FindName("UsagePanel");
        var map = (Border)wall.FindName("MapFrame");
        var body = (Grid)wall.FindName("WallBody");
        var rail = (Grid)wall.FindName("AnalyticsRail");
        var speedPanel = (Border)wall.FindName("SpeedPanel");
        Check("Full-screen map and usage panel fit together", wallScroll.ScrollableHeight < 1);
        Check("Analytics occupy about one fifth of the workspace", panel.ActualWidth * panel.ActualHeight / (wallScroll.ActualWidth * wallScroll.ActualHeight) is > .18 and < .22);
        var corner = panel.TranslatePoint(new Point(panel.ActualWidth, panel.ActualHeight), body);
        Check("Analytics dock at the bottom right beside the map", Grid.GetColumn(rail) == 1 && Math.Abs(corner.Y - map.ActualHeight) < 2
            && Math.Abs(corner.X + rail.Margin.Right - body.ActualWidth) < 2);
        var speedsTop = speedPanel.TranslatePoint(new Point(), body).Y;
        var tokensTop = panel.TranslatePoint(new Point(), body).Y;
        Check("Public performance fills the old gap and raises the token panel", Math.Abs(speedsTop) < 1 && tokensTop < map.ActualHeight * .27
            && Math.Abs(tokensTop - speedPanel.ActualHeight - speedPanel.Margin.Bottom) < 2);
        Check("Both model speed readings are visible at wall size", ((ScrollViewer)wall.FindName("SpeedScroll")).ScrollableHeight < 1);
        Capture(wall, 1280, 800, "workflow-speeds-desktop.png");
        Check("Normal desktop size shows two model speeds without scrolling", ((ScrollViewer)wall.FindName("SpeedScroll")).ScrollableHeight < 1);
        Capture(wall, 760, 520, "workflow-recent-compact.png");
        wallScroll.ScrollToVerticalOffset(rail.TranslatePoint(new Point(), body).Y); PumpFor(60);
        Capture(wall, 760, 520, "workflow-speeds-compact.png");
        Check("Compact model speeds keep their heading and readings reachable", speedPanel.TranslatePoint(new Point(), wallScroll).Y >= -.5
            && ((ScrollViewer)wall.FindName("SpeedScroll")).ScrollableHeight < 1);
        ((ScrollViewer)wall.FindName("WallScroll")).ScrollToBottom(); PumpFor(60);
        Check("Compact analytics keep their heading within the visible viewport", panel.TranslatePoint(new Point(), wallScroll).Y >= -.5
            && panel.ActualHeight <= wallScroll.ViewportHeight + 1);
        Capture(wall, 760, 520, "workflow-usage-compact.png");
        recent.Status = "idle"; Activity(recent, DateTimeOffset.UtcNow.AddMinutes(-10).AddMilliseconds(400));
        wall.Refresh();
        Check("A finished agent stays visible until its ten minutes elapse", graph.NodeCount == 2);
        PumpFor(1500);
        Check("The real refresh timer removes the expired agent and directory", graph.NodeCount == 0);
        wall.Close();
        Check("Closing the wall releases the shared OpenRouter event subscription", SpeedSubscribers == speedSubscribers);
        Check("Closing the workflow wall releases its streaming usage watcher", !LiveTurnTelemetry.Instance.IsWatched);
        recent.Close(); old.Close();
    }
}
