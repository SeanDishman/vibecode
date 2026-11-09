using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private const string ToolDumpMarker = "HIDDEN_TOOL_RESULT_FIXTURE";

    private static ToolItem TranscriptFixtureTool(string id) => new()
    {
        Id = id, Name = "mcp__vc_bridge__bridge_list_agents", Status = "done",
        Input = new JsonObject { ["description"] = ToolDumpMarker },
        Result = JsonSerializer.Serialize(new { OkayOutput = "{\"joined\":true,\"description\":\"" + ToolDumpMarker + "\"}" }),
    };

    private static void VerifyTerminalTextTranscript()
    {
        var (vm, team) = Team("terminal-text-transcript", 2);
        var host = team[0];
        var user = new UserItem { Text = "Check the project and explain what changed." };
        var reply = new TextItem { Text = "I am checking the project." };
        var literal = new TextItem { Text = "The response body is {\"ready\":true}." };
        host.Items.Add(user);
        host.Items.Add(reply);
        team[1].Items.Add(literal);
        var request = new PermItem
        {
            RequestId = "text-only-permission", ToolName = "Bash",
            Input = new JsonObject { ["command"] = "dotnet test", ["description"] = "Run project checks" },
        };
        host.Items.Add(request);
        var resolved = new PermItem { RequestId = "text-only-resolved", ToolName = "Read", State = "allow" };
        host.Items.Add(resolved);
        var tool = TranscriptFixtureTool("raw-result");
        host.Items.Add(tool);
        var group = new CompactToolGroupItem(CompactToolGroupItem.BrowseKind, true);
        group.Tools.Add(TranscriptFixtureTool("group-result"));
        host.Items.Add(group);
        host.Items.Add(new SubagentItem { ThreadId = "worker-fixture", Task = ToolDumpMarker, Status = "running" });
        // More hidden entries than the viewport limit must not displace the conversation or break live text updates.
        for (var i = 0; i < 300; i++) host.Items.Add(TranscriptFixtureTool("burst-" + i));
        host.Items.Add(new BannerItem { Level = "error", Text = "Account needs sign-in." });

        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
        var snapshot = terminal.Snapshot();
        Check("tool traffic does not displace earlier user or agent messages", snapshot.Rows!.Any(r => r.Text == user.Text)
            && snapshot.Rows!.Any(r => r.Text == reply.Text));
        Check("assistant text containing JSON remains ordinary conversation",
            snapshot.Rows!.Any(r => r.Kind == "reply" && r.Text == literal.Text));
        Check("individual, grouped and subagent tools never enter terminal packets",
            snapshot.Rows!.All(r => r.Kind != "tool") && !JsonSerializer.Serialize(snapshot).Contains(ToolDumpMarker));
        Check("pending approvals remain actionable while resolved requests stay out of the transcript",
            snapshot.Rows!.Any(r => r.Kind == "request" && r.Text.Contains("dotnet test") && r.Text.Contains("/allow "))
            && snapshot.Rows!.All(r => r.Id != (long)Property(resolved, "DisplaySequence")!));
        Check("human-facing errors remain visible", snapshot.Rows!.Any(r => r.Kind == "error" && r.Text == "Account needs sign-in."));
        Check("filtering preserves the full backend tool history",
            host.Items.Contains(tool) && host.Items.Contains(group) && tool.Result!.Contains(ToolDumpMarker)
            && group.Tools[0].Result!.Contains(ToolDumpMarker) && host.Items.OfType<ToolItem>().Count() == 301);

        Call(terminal, "ObserveVisibleState");
        var dirty = typeof(TerminalSession).GetField("_dirty", Flags)!;
        dirty.SetValue(terminal, false);
        tool.Result += " more streamed output";
        group.Tools.Add(TranscriptFixtureTool("new-group-result"));
        Check("hidden tool updates do not schedule terminal refreshes", !(bool)dirty.GetValue(terminal)!);
        reply.Text += " The files are ready.";
        Check("replies older than a burst of tools still stream to the terminal",
            (bool)dirty.GetValue(terminal)! && terminal.Snapshot().Rows!.Any(r => r.Text == reply.Text));
        request.State = "allow";
        Check("answered requests disappear without changing the saved request",
            terminal.Snapshot().Rows!.All(r => r.Id != (long)Property(request, "DisplaySequence")!) && host.Items.Contains(request));
        foreach (var agent in team) agent.Close();
    }

    private static void VerifyQuietTerminalConsole(TerminalSession terminal, Process client)
    {
        var prompt = new UserItem { Text = "Check the project files." };
        var reply = new TextItem { Text = "I checked the project. The files are ready." };
        terminal.Host.Items.Add(prompt);
        terminal.Host.Items.Add(reply);
        for (var i = 0; i < 300; i++) terminal.Host.Items.Add(TranscriptFixtureTool("console-tool-" + i));
        ConsoleCapture? capture = null;
        PumpUntil(() =>
        {
            capture = ProbeConsole(client, "quiet-transcript");
            return capture.Text.Contains(reply.Text);
        });
        Check("real console shows conversation after hundreds of tool events",
            capture!.Text.Contains(prompt.Text) && capture.Text.Contains(reply.Text));
        Check("real console omits raw tool results and tool row labels",
            !capture!.Text.Contains(ToolDumpMarker) && !capture.Text.Contains("OkayOutput")
            && !capture.Text.Contains("bridge_list_agents") && !capture.Text.Contains("· tool"));
        CaptureNativeConsole(new IntPtr(capture!.Window), "terminal-text-only.png");
    }
}
