using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static void VerifyAdvancedWorkflow()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new MainViewModel();
        var first = Chat(@"C:\Projects\Advanced", "Codex 1", "Build checkout", "running");
        var second = Chat(first.Cwd, "Claude 2", "Verify checkout", "running", "claude");
        var workers = new[] {
            Chat(first.Cwd, "Codex 3", "Implement payment API", "running"),
            Chat(first.Cwd, "Claude 4", "Check payment failures", "idle", "claude"),
            Chat(first.Cwd, "Codex 5", "Exercise checkout flow", "running"),
        };
        first.IsBridgeHost = true;
        first.IsBridgeManager = second.IsBridgeManager = true;
        Set(first, "BridgeCoordinatesOnly", true); Set(second, "BridgeCoordinatesOnly", true);
        using var activity = new CentralPlannerActivity(new("codex", "gpt-6.1", "high"));
        activity.SetStage("Dividing checkout implementation and verification"); activity.Flush();
        var central = new BridgeCentralPlan { Provider = "codex", Model = "gpt-6.1", Activity = activity, LastActivityAt = now };
        var work = new BridgeWorkState { CentralPlan = central };
        var roster = new[] { first, second }.Concat(workers).ToArray();
        foreach (var chat in roster) { Set(chat, "BridgeWork", work); source.BridgePanes.Add(chat); }
        Set(workers[0], "BridgeCoordinatorAgentId", first.BridgeAgentId);
        Set(workers[1], "BridgeCoordinatorAgentId", first.BridgeAgentId);
        Set(workers[2], "BridgeCoordinatorAgentId", second.BridgeAgentId);
        source.Chats.Add(first);
        var agents = AgentActivitySnapshot.Capture(source).Single().Agents;
        var planner = agents.Single(a => a.IsCentralPlanner);
        Check("An active central planner is captured once outside the chat roster", agents.Count == 6 && planner.State == AgentActivityState.Working);
        Check("Each orchestrator connects to its central planner", agents.Where(a => !a.IsCentralPlanner && a.Chat.IsBridgeManager).All(a => a.ParentAgentId == planner.Id));
        Check("Workers retain only their assigned orchestrator", agents.Single(a => a.Id == workers[0].BridgeAgentId).ParentAgentId == first.BridgeAgentId
            && agents.Single(a => a.Id == workers[2].BridgeAgentId).ParentAgentId == second.BridgeAgentId);
        typeof(MainViewModel).GetMethod("ParkActiveBridge", Flags)!.Invoke(source, [true]);
        Check("Parked Advanced Bridges retain their central planner and workers", AgentActivitySnapshot.Capture(source).Single().Agents.Count == 6);
        source.Chats.Remove(first);
        Check("A parked host outside the sidebar still belongs to the workflow roster", AgentActivitySnapshot.Capture(source).Single().Agents.Count == 6);
        source.Chats.Add(first);
        Check("Opening the planner navigates to its existing Bridge", source.OpenWorkflowAgent(planner.Chat) && source.BridgePanes.Contains(first));
        Check("Planner navigation does not launch a chat session", typeof(ChatViewModel).GetField("_session", Flags)!.GetValue(first) is null);
        var wall = new WorkflowTelemetryWindow(source) { Left = 6200, Top = 200, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        wall.Show(); wall.Refresh();
        var graph = (WorkflowGraph)wall.FindName("Graph");
        Capture(wall, 1920, 1080, "workflow-advanced-wall.png");
        CheckLayout(graph);
        Check("The graph renders all central and group management edges", graph.ManagedConnectionCount == 5);
        Capture(wall, 1280, 800, "workflow-advanced-desktop.png"); CheckLayout(graph);
        Capture(wall, 760, 520, "workflow-advanced-compact.png"); CheckLayout(graph);
        activity.Dispose(); central.Activity = null; central.State = "completed";
        central.LastActivityAt = now.AddMinutes(-9);
        var restored = System.Text.Json.JsonSerializer.Deserialize<BridgeWorkState>(System.Text.Json.JsonSerializer.Serialize(work))!;
        Check("Completed planner recency survives saving the Bridge", restored.CentralPlan!.LastActivityAt == central.LastActivityAt && restored.CentralPlan.Activity is null);
        Check("Completed planning remains visible for its recent activity window", AgentActivitySnapshot.Build(roster, now).Single().Agents.Any(a => a.IsCentralPlanner && a.Status == "Completed"));
        central.LastActivityAt = now.AddMinutes(-10);
        Check("Central planners expire at ten minutes just like chat agents", !AgentActivitySnapshot.Build(roster, now).Single().Agents.Any(a => a.IsCentralPlanner));
        first.Status = "idle"; Activity(first, now.AddMinutes(-30));
        var remaining = AgentActivitySnapshot.Build(roster, now).Single().Agents;
        Check("Working children cannot keep an inactive orchestrator visible", !remaining.Any(a => a.Id == first.BridgeAgentId)
            && remaining.Any(a => a.Id == workers[0].BridgeAgentId));
        wall.Close();
        foreach (var chat in roster) chat.Close();
    }
}
