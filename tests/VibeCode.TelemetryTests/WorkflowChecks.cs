using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static int Checks;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static void Check(string name, bool result)
    { if (!result) throw new Exception("FAIL: " + name); Checks++; Console.WriteLine("PASS: " + name); }
    static void Set(object target, string property, object value) => target.GetType().GetProperty(property, Flags)!.SetValue(target, value);
    static ChatViewModel Chat(string directory, string label, string task, string status = "idle", string provider = "codex")
    {
        var chat = new ChatViewModel(directory, provider: provider) { Title = task, BridgeLabel = label, Status = status };
        typeof(ChatViewModel).GetField("_model", Flags)!.SetValue(chat, provider == "claude" ? "Opus" : "GPT");
        typeof(ChatViewModel).GetField("_lastAgentActivityAt", Flags)!.SetValue(chat, DateTimeOffset.UtcNow);
        return chat;
    }
    static AgentActivityNode Only(ChatViewModel chat) => AgentActivitySnapshot.Build([chat]).Single().Agents.Single();

    static void VerifySnapshots()
    {
        var first = Chat(@"C:\Work\Store\", "Codex 1", "Search products", "running");
        var same = Chat(@"c:\work\STORE", "Claude 2", "Review checkout", provider: "claude");
        var other = Chat(@"D:\Client\Store", "Codex 3", "Ship storefront");
        var scratch = Chat(NoDirectoryWorkspace.Folder, "", "An unrelated question");
        var closed = Chat(@"C:\Old", "Codex 4", "Closed", "closed");
        var projects = AgentActivitySnapshot.Build([first, same, other, scratch, closed, first]);
        Check("Path casing and trailing separators group together", projects.Single(p => p.Directory.StartsWith("C:")).Agents.Count == 2);
        Check("Same directory name on another drive stays separate", projects.Count(p => p.Name == "Store") == 2);
        Check("Duplicate hosts are counted once and closed chats are excluded", projects.Sum(p => p.Agents.Count) == 4);
        Check("Scratch conversations have their own no-directory group", projects.Last().Name == "No directory" && projects.Last().Directory == "");
        Check("Running agents are working", Only(first).State == AgentActivityState.Working);
        first.AuthNeeded = true;
        Check("Sign-in takes precedence over running", Only(first).State == AgentActivityState.Waiting);
        first.AuthNeeded = false; first.Status = "error";
        Check("Errors do not appear working", Only(first).State == AgentActivityState.Error);
        first.Status = "idle"; Set(first, "BridgeTaskState", "completed");
        Check("Completed agents stay visible without working animation", Only(first).State == AgentActivityState.Idle && Only(first).Status == "Completed");
        Set(first, "BridgeTaskState", "waiting");
        Check("Orchestrator waiting state is represented", Only(first).State == AgentActivityState.Waiting);
        Check("Account names are absent by default", Only(same).Account.Length == 0);
        typeof(ChatViewModel).GetField("_accountLabel", Flags)!.SetValue(same, "Development account");
        Set(same, "ShowBridgeAccountLabel", true);
        Check("Existing Bridge account disambiguation is respected", Only(same).Account == "Development account");
        Check("A missing source has an honest empty snapshot", AgentActivitySnapshot.Capture(null).Count == 0);
        var work = new BridgeWorkState(); work.PausedWorkers.Add(first.BridgeAgentId);
        Set(first, "BridgeWork", work); Set(first, "BridgeCoordinatorAgentId", "manager");
        first.Status = "running";
        Check("A requested stop is not reported as already stopped", Only(first).State == AgentActivityState.Paused && Only(first).Status == "Stopping");
        first.Status = "idle";
        Check("A stopped worker remains visible", Only(first).State == AgentActivityState.Paused && Only(first).Status == "Stopped");
        first.Close(); same.Close(); other.Close(); scratch.Close(); closed.Close();
    }

    static void VerifyNavigation()
    {
        var source = new MainViewModel();
        var host = Chat(@"C:\Projects\Desktop", "Codex 1", "Coordinate changes");
        var peer = Chat(host.Cwd, "Claude 2", "Review changes", "running", "claude");
        source.Chats.Add(host); source.BridgePanes.Add(host); source.BridgePanes.Add(peer);
        Check("Wall resolves its owning shell's live view model", ReferenceEquals(AgentActivitySnapshot.ResolveSource(new Window { DataContext = source }), source));
        Check("Bridge peers absent from the sidebar are captured", AgentActivitySnapshot.Capture(source).Single().Agents.Count == 2);
        typeof(MainViewModel).GetMethod("ParkActiveBridge", Flags)!.Invoke(source, [true]);
        Check("Parked Bridge agents remain visible", source.BridgePanes.Count == 0 && AgentActivitySnapshot.Capture(source).Single().Agents.Count == 2);
        peer.BridgeMinimized = true;
        Check("Clicking a parked worker reopens its Bridge", source.OpenWorkflowAgent(peer) && source.ShowBridge && source.BridgePanes.Contains(peer));
        Check("Worker selection restores minimized panes", !peer.BridgeMinimized && ReferenceEquals(source.BridgePanelChat, peer));
        host.BridgeExpanded = true; peer.BridgeVisible = false;
        source.OpenWorkflowAgent(peer);
        Check("Expanded peers cannot hide the selected worker", peer.BridgeVisible && !host.BridgeExpanded);
        Check("Navigation never starts a provider", typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(peer) is null);
        peer.Status = "closed";
        Check("Closed nodes cannot reopen a chat", !source.OpenWorkflowAgent(peer));
        var outside = Chat(@"C:\Other", "", "No longer open");
        Check("Stale nodes cannot select removed chats", !source.OpenWorkflowAgent(outside));
        host.Close(); peer.Close(); outside.Close();
    }

    static void VerifyWall()
    {
        var source = new MainViewModel();
        var fixture = new[]
        {
            Chat(@"C:\Projects\Commerce", "Codex 1", "Improve product search", "running"),
            Chat(@"C:\Projects\Commerce", "Claude 2", "Review checkout flow", "running", "claude"),
            Chat(@"C:\Projects\Commerce", "Codex 3", "Update inventory API"),
            Chat(@"C:\Projects\Commerce", "Claude 4", "Verify payment states", provider: "claude"),
            Chat(@"C:\Projects\Desktop", "Codex 1", "Build the workflow wall", "running"),
            Chat(@"C:\Projects\Desktop", "Claude 2", "Check keyboard navigation", "error", "claude"),
            Chat(@"C:\Projects\Website", "Codex 1", "Improve the pricing page", "running"),
            Chat(@"C:\Projects\Website", "Claude 2", "Review accessibility", provider: "claude"),
            Chat(NoDirectoryWorkspace.Folder, "", "Plan the next release"),
        };
        fixture[3].AuthNeeded = true;
        foreach (var chat in fixture) source.Chats.Add(chat);
        var wall = new WorkflowTelemetryWindow(source) { Left = 6200, Top = 200, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        wall.Show(); wall.Refresh();
        var graph = (WorkflowGraph)wall.FindName("Graph");
        var search = (TextBox)wall.FindName("Search");
        var filter = (ComboBox)wall.FindName("ProjectFilter");
        var working = (CheckBox)wall.FindName("WorkingOnly");
        Capture(wall, 1280, 800, "workflow-desktop.png");
        Check("New wall has its own product name", wall.Title == "Workflow Telemetry Wall");
        Check("All open directories and root agents are represented", graph.NodeCount == 13);
        Check("Node buttons have accessible names", graph.Children.OfType<Button>().All(b => AutomationProperties.GetName(b).Length > 0));
        CheckLayout(graph);
        var oldButton = graph.Children.OfType<Button>().First(b => b.Tag is AgentActivityNode);
        wall.Refresh();
        Check("Unchanged refresh preserves node controls and focus", graph.Children.Contains(oldButton));
        search.Text = "checkout";
        Check("Task search filters agents and their project", graph.NodeCount == 2);
        search.Clear(); filter.SelectedValue = @"C:\Projects\Desktop";
        Check("Project filter keeps the selected directory only", graph.NodeCount == 3);
        filter.SelectedValue = "*"; working.IsChecked = true;
        Check("Working filter excludes waiting, errors and idle", graph.Children.OfType<Button>().Count(b => b.Tag is AgentActivityNode) == 4);
        working.IsChecked = false;
        search.Text = "no matching task";
        Capture(wall, 760, 520, "workflow-filter-empty.png");
        Check("Empty filter gives recovery instructions", ((TextBlock)wall.FindName("EmptyTitle")).Text == "No matching agents");
        search.Clear();
        Capture(wall, 760, 520, "workflow-compact.png");
        CheckLayout(graph);
        Capture(wall, 1920, 1080, "workflow-wall.png");
        CheckLayout(graph);
        AppSettings.Current.TelemetryLiveAnimation = true;
        graph.UpdateMotion();
        Check("Motion follows the system preference", graph.IsAnimating == SystemParameters.ClientAreaAnimation);
        wall.Hide();
        Check("A hidden wall stops animation", !graph.IsAnimating);
        wall.Show();
        graph.UpdateMotion();
        AppSettings.Current.TelemetryLiveAnimation = false; graph.UpdateMotion();
        Check("Motion preference stops the animation timer", !graph.IsAnimating);
        var cli = (ResourceDictionary)Application.LoadComponent(new Uri("/VibeCode;component/Themes/Cli.xaml", UriKind.Relative));
        Application.Current.Resources.MergedDictionaries.Add(cli);
        Capture(wall, 1100, 720, "workflow-cli.png");
        Application.Current.Resources.MergedDictionaries.Remove(cli);
        var updatedButton = graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityNode n && n.Id == fixture[0].BridgeAgentId);
        fixture[0].Status = "idle"; wall.Refresh();
        Check("A completed turn updates its existing graph node", ((AgentActivityNode)updatedButton.Tag).State == AgentActivityState.Idle);
        source.Chats.Clear(); wall.Refresh();
        Capture(wall, 1280, 800, "workflow-empty.png");
        Check("Closing all chats removes every graph node", graph.NodeCount == 0);
        Check("Empty workspace explains how to populate the wall", ((TextBlock)wall.FindName("EmptyTitle")).Text == "No recent agent activity");
        for (var i = 0; i < 150; i++)
            source.Chats.Add(Chat(@"C:\Scale\Project" + i / 10, "Codex " + i, "Task " + i));
        wall.Refresh(); wall.UpdateLayout();
        Check("Large rosters are scrollable without dropping agents", graph.NodeCount == 165 && graph.Height > wall.Height);
        CheckLayout(graph);
        wall.Close();
        Check("Closing the wall releases its animation timer", !graph.IsAnimating);
        foreach (var chat in fixture.Concat(source.Chats)) chat.Close();
    }

    static void CheckLayout(WorkflowGraph graph)
    {
        var rects = graph.Children.OfType<Button>().Select(b => new Rect(Canvas.GetLeft(b), Canvas.GetTop(b), b.Width, b.Height)).ToArray();
        Check("Nodes stay inside the canvas", rects.All(r => r.Left >= 0 && r.Right <= graph.ActualWidth + 1 && r.Top >= 0 && r.Bottom <= graph.Height + 1));
        Check("Node rectangles never overlap", !rects.SelectMany((r, i) => rects.Skip(i + 1).Select(other => r.IntersectsWith(other))).Any(x => x));
    }
}
