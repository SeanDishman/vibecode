using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static ToolItem Tool(string name, string status, string input, bool error = false) =>
        new() { Id = Guid.NewGuid().ToString("n"), Name = name, Input = JsonNode.Parse(input), Status = status, IsError = error };

    /// <summary>Start a turn on <paramref name="chat"/> and append its tool rows, as a provider stream would.</summary>
    static void Steps(ChatViewModel chat, params ItemVm[] items)
    {
        chat.Items.Add(new UserItem { Text = "Fixture turn" });
        foreach (var item in items) chat.Items.Add(item);
    }

    /// <summary>Every text a card shows, in visual order, so checks read the rendered card rather than the model.</summary>
    static string CardText(Button button)
    {
        var texts = new List<string>();
        void Walk(object node)
        {
            if (node is TextBlock { Text.Length: > 0 } text && text.Visibility == Visibility.Visible) texts.Add(text.Text);
            if (node is DependencyObject element)
                foreach (var child in LogicalTreeHelper.GetChildren(element)) Walk(child);
        }
        Walk(button.Content);
        return string.Join(" | ", texts);
    }

    static void VerifyActivityLines()
    {
        var chat = Chat(@"C:\Projects\Commerce", "Claude 1", "Improve product search", "running", "claude");
        Steps(chat,
            Tool("Read", "done", """{"file_path":"C:\\Projects\\Commerce\\src\\search\\ProductSearch.ts"}"""),
            Tool("Bash", "done", """{"command":"npm test -- search","description":"Run the product search tests"}"""),
            Tool("mcp__vc_bridge_1__bridge_list_agents", "done", "{}"),
            Tool("Edit", "running", """{"file_path":"C:\\Projects\\Commerce\\src\\search\\ProductSearch.ts","old_string":"a","new_string":"b"}"""));
        var node = Only(chat);
        Check("A running tool is what a working agent is doing now", node.Now is { Text: "Editing ProductSearch.ts", Glyph: "\uE70F" });
        Check("The last finished step skips Bridge bookkeeping", node.Last is { Text: "Run the product search tests", Failed: false });

        // Codex runs every command through a full interpreter invocation; the card shows the command itself.
        var build = Tool("Bash", "running",
            """{"command":"\"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command \"npm run build\""}""");
        Steps(chat, build);
        node = Only(chat);
        Check("Interpreter wrappers are removed from live commands", node.Now?.Text == "Running npm run build");
        Check("A row left running by an earlier turn is not reported as live", node.Last?.Text == "Run the product search tests");

        // A running chat parks its "still going" row at the end of the transcript, so rows are held, never indexed.
        build.Status = "error"; build.IsError = true; chat.Status = "idle";
        node = Only(chat);
        Check("A failed step is shown in the past tense and marked failed", node.Last is { Text: "Ran npm run build", Failed: true } && node.Now is null);

        chat.Status = "running";
        var approval = new PermItem { RequestId = "fixture-permission", ToolName = "Bash" };
        chat.Items.Add(approval);
        Check("A pending approval is what the agent is waiting on", Only(chat).Now?.Text == "Waiting for approval · Bash");
        chat.Items.Remove(approval);
        chat.Items.Add(new ThinkingItem { Streaming = true });
        Check("Streaming reasoning reads as thinking", Only(chat).Now?.Text == "Thinking");
        chat.Items.Add(new TextItem { Streaming = true });
        Check("A streaming answer reads as writing", Only(chat).Now?.Text == "Writing a reply");
        chat.Items.Add(Tool("mcp__github__create_issue", "done", """{"title":"Search regression"}"""));
        Check("Other MCP tools are named by server and tool, never raw JSON", Only(chat).Last?.Text == "github · create issue");

        var planner = Chat(@"C:\Projects\Commerce", "Codex 2", "Ship search", "running");
        planner.Todos.Add(new TodoEntry { Status = "completed", Text = "Profile the query" });
        planner.Todos.Add(new TodoEntry { Status = "in_progress", Text = "Cache popular searches" });
        planner.Todos.Add(new TodoEntry { Status = "pending", Text = "Verify relevance" });
        node = Only(planner);
        Check("An agent's todo list gives plan progress and its current step", node is { PlanDone: 1, PlanTotal: 3 } && node.Now?.Text == "Cache popular searches");
        Set(planner, "BridgeActivitySummary", "Cached popular searches; 48 checks pass");
        planner.Status = "idle";
        node = Only(planner);
        Check("An idle Bridge agent's own report is what it last finished", node.Last?.Text == "Cached popular searches; 48 checks pass"
            && node.Reported == node.Last.Text && node.Now is null);

        var busy = Chat(@"C:\Projects\Commerce", "Codex 3", "Read the repository", "running");
        Steps(busy, Tool("Read", "running", """{"file_path":"C:\\Far\\Away.cs"}"""));
        for (var i = 0; i < 400; i++) busy.Items.Add(Tool("Read", "done", $$"""{"file_path":"C:\\Repo\\File{{i}}.cs"}"""));
        node = Only(busy);
        Check("The transcript scan stops at its bound instead of reading a whole long turn", node.Now?.Text == "Working" && node.Last?.Text == "Read File399.cs");
        chat.Close(); planner.Close(); busy.Close();
    }

    static void VerifyFlowModel()
    {
        var opus = WorkflowFlow.For(57);
        var gpt = WorkflowFlow.For(97);
        var fast = WorkflowFlow.For(400);
        Check("Faster models stream faster and denser", opus.PixelsPerSecond < gpt.PixelsPerSecond && gpt.PixelsPerSecond < fast.PixelsPerSecond
            && opus.Spacing > gpt.Spacing && gpt.Spacing > fast.Spacing);
        Check("Packets per second follow public throughput at 20 tokens a packet",
            new[] { 10.0, 57, 97, 250, 400 }.All(t => Math.Abs(WorkflowFlow.For(t).PacketsPerSecond - t / WorkflowFlow.TokensPerPacket) < 1e-9));
        Check("Extreme readings stay inside readable motion", WorkflowFlow.For(5000) is { PixelsPerSecond: WorkflowFlow.MaxSpeed, Spacing: WorkflowFlow.MinSpacing }
            && WorkflowFlow.For(1) is { PixelsPerSecond: WorkflowFlow.MinSpeed, Spacing: WorkflowFlow.MaxSpacing });
        Check("Missing or invalid readings use the neutral unmeasured pace", new double?[] { null, 0, -4, double.NaN, double.PositiveInfinity }
            .All(t => WorkflowFlow.For(t) == WorkflowFlow.Unmeasured) && !WorkflowFlow.Unmeasured.Measured && WorkflowFlow.Unmeasured.Meter == 0);
        Check("The speed meter rises with throughput and stays in range", opus.Meter < gpt.Meter && gpt.Meter < fast.Meter && fast.Meter <= 1
            && WorkflowFlow.For(1).Meter > 0);
        Check("One animation cycle advances exactly one packet", Math.Abs(gpt.Period * gpt.PixelsPerSecond - gpt.Spacing) < 1e-9);
    }

    static void VerifyLiveCards()
    {
        // A model OpenRouter has no reading for, seeded as such so the check makes no request.
        SpeedCache["openai/fixture-unlisted"] = (null, DateTime.UtcNow);
        SeededSpeeds.Add("openai/fixture-unlisted");
        var source = new MainViewModel();
        var opus = Chat(@"C:\Projects\Live", "Claude 1", "Speed up search", "running", "claude");
        var gpt = Chat(opus.Cwd, "Codex 2", "Review search", "running");
        var resting = Chat(opus.Cwd, "Codex 3", "Document search");
        typeof(ChatViewModel).GetField("_model", Flags)!.SetValue(resting, "fixture-unlisted");
        var edit = Tool("Edit", "running", """{"file_path":"C:\\Projects\\Live\\Search.cs"}""");
        Steps(opus, edit);
        var roster = new[] { opus, gpt, resting };
        foreach (var chat in roster) source.Chats.Add(chat);
        var wall = new WorkflowTelemetryWindow(source) { Left = 6200, Top = 200, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        wall.Show(); wall.Refresh(); wall.UpdateLayout();
        var graph = (WorkflowGraph)wall.FindName("Graph");
        Button Card(ChatViewModel chat) => graph.Children.OfType<Button>().Single(b => b.Tag is AgentActivityNode n && n.Id == chat.BridgeAgentId);
        var card = Card(opus);
        var face = card.Content;
        Check("Agent cards say what the agent is doing now", CardText(card).Contains("NOW") && CardText(card).Contains("Editing Search.cs"));
        Check("Cards show each model's public OpenRouter speed", CardText(card).Contains("57 tok/s") && CardText(Card(gpt)).Contains("97 tok/s"));
        Check("A model without a reading says so instead of inventing a speed", CardText(Card(resting)).Contains("\u2014 tok/s"));
        Check("Each working agent streams and idle agents do not", graph.FlowCount == 2);
        var position = (Canvas.GetLeft(card), Canvas.GetTop(card));
        edit.Status = "done";
        opus.Items.Add(Tool("Bash", "running", """{"command":"dotnet test","description":"Run the search tests"}"""));
        wall.Refresh();
        Check("A new step updates the same card in place", ReferenceEquals(Card(opus), card) && ReferenceEquals(card.Content, face)
            && (Canvas.GetLeft(card), Canvas.GetTop(card)) == position);
        Check("The finished step moves to LAST", CardText(card).Contains("Run the search tests") && CardText(card).Contains("Edited Search.cs"));
        Check("The tooltip keeps the full activity", card.ToolTip.ToString()!.Contains("Now: Run the search tests")
            && card.ToolTip.ToString()!.Contains("Last: Edited Search.cs") && card.ToolTip.ToString()!.Contains("57 tok/s on OpenRouter"));
        gpt.Status = "idle"; wall.Refresh();
        Check("An agent that stops working stops streaming", graph.FlowCount == 1);
        AppSettings.Current.TelemetryLiveAnimation = true; graph.UpdateMotion();
        var streams = graph.Children.OfType<System.Windows.Shapes.Path>().ToArray();
        Check("Streams animate only while motion is allowed", graph.IsAnimating == SystemParameters.ClientAreaAnimation && streams.Length == 4
            && streams.All(p => p.Visibility == (graph.IsAnimating ? Visibility.Visible : Visibility.Collapsed)));
        if (graph.IsAnimating)
        {
            PumpFor(250);
            Capture(wall, 1280, 800, "workflow-live-cards.png");
        }
        AppSettings.Current.TelemetryLiveAnimation = false; graph.UpdateMotion();
        Check("Reduced motion hides every packet", !graph.IsAnimating && streams.All(p => p.Visibility == Visibility.Collapsed));
        Capture(wall, 1280, 800, "workflow-live-cards-still.png");
        wall.Close();
        foreach (var chat in roster) chat.Close();
    }
}
