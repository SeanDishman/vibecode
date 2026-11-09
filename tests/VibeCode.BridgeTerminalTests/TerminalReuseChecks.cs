using System.Windows.Threading;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.Terminal;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyTerminalAgentReuse()
    {
        var limit = AppSettings.Current.BridgeAgentLimit;
        AppSettings.Current.BridgeAgentLimit = 4;
        var (vm, team) = RobotTeam("terminal-reuse", 4, "codex");
        var host = team[0];
        host.IsBridgeHost = true;
        foreach (var agent in team) agent.Status = "idle";
        Property(host, "AgentTeamName", "Sage");
        Property(team[3], "AgentTeamName", "Cedar");
        team[1].Items.Add(new TextItem { Text = "The earlier task is complete." });
        using var terminal = new TerminalSession(vm, host, Dispatcher.CurrentDispatcher);
        try
        {
            var originalId = host.BridgeAgentId;
            var originalSession = host.SessionId;
            Check("terminal reuses a finished agent even at the team limit", terminal.HandleInput("Reuse the quiet team").Accepted
                && vm.TerminalTeamRoster(host).Count == 4);
            PumpUntil(() => Session(host).Sent.Count == 1);
            Check("quiet team reuses agent one with its existing identity and session", host.BridgeAgentId == originalId
                && host.SessionId == originalSession && Session(host).Sent[0].Contains("Reuse the quiet team"));
            Check("reused agent receives the handoff and new shared team context", Session(host).Sent[0].Contains("no new agent was summoned")
                && Session(host).Sent[0].Contains("The earlier task is complete."));

            team[1].Status = "running";
            typeof(ChatViewModel).GetField("_workEndedAt", Flags)!.SetValue(team[2], DateTime.UtcNow.AddMinutes(-5));
            typeof(ChatViewModel).GetField("_workEndedAt", Flags)!.SetValue(team[3], DateTime.UtcNow);
            Check("terminal accepts a prompt while another agent works", terminal.HandleInput("Use the most recent finisher").Accepted);
            Check("immediate next prompt claims another free agent", terminal.HandleInput("Do separate follow-up work").Accepted);
            PumpUntil(() => Session(team[3]).Sent.Count == 1 && Session(team[2]).Sent.Count == 1);
            Check("most recently finished agent takes the first follow-up", Session(team[3]).Sent[0].Contains("Use the most recent finisher"));
            Check("agent preparing an accepted prompt cannot take a second handoff", Session(team[2]).Sent[0].Contains("Do separate follow-up work")
                && vm.TerminalTeamRoster(host).Count == 4 && team.All(a => !a.HasQueued));
            var denied = terminal.HandleInput("Keep this unsent prompt");
            Check("all busy at the limit refuses the send and keeps the draft", !denied.Accepted && denied.Text!.Contains("draft is kept")
                && vm.TerminalTeamRoster(host).Count == 4 && team.All(a => !a.HasQueued));
            Check("each accepted prompt was delivered exactly once", team.Sum(a => Session(a).Sent.Count) == 3);

            host.Status = "idle";
            Call(host, "OnPermissionRequested", new PermissionRequest
            { RequestId = "reuse-question", ToolName = "AskUserQuestion", ToolUseId = "reuse-q", Input = CodexColorQuestion() });
            Check("an agent awaiting an answer is not treated as finished", !terminal.HandleInput("Do not take over the waiting agent").Accepted
                && host.Items.OfType<PermItem>().Last().IsPending && Session(host).Sent.Count == 1);
            var question = host.Items.OfType<PermItem>().Last();
            terminal.HandleInput($"/answer {Property(question, "DisplaySequence")} Blue");
            host.Status = "running";
            host.Send("Already queued work");
            host.Status = "idle";
            Check("an agent with queued work is not considered available", !terminal.HandleInput("Do not jump ahead of queued work").Accepted
                && host.HasQueued && vm.TerminalTeamRoster(host).Count == 4);
        }
        finally
        {
            AppSettings.Current.BridgeAgentLimit = limit;
            foreach (var agent in team) agent.Close();
        }

        var (clickedVm, clicked) = BridgeHost("terminal-existing-reuse", "claude", "Existing chat");
        var enabled = AppSettings.Current.TerminalEnabled;
        AppSettings.Current.TerminalEnabled = true;
        try
        {
            clicked.Items.Add(new TextItem { Text = "Keep this earlier conversation." });
            var provider = Steerable(clicked);
            var session = clicked.SessionId;
            using var opened = new TerminalSession(clickedVm, clickedVm.OpenChatInTerminal(clicked), Dispatcher.CurrentDispatcher);
            Check("a chat opened in terminal keeps its available agent", opened.HandleInput("Continue this existing chat").Accepted
                && clickedVm.TerminalTeamRoster(clicked).Count == 1);
            PumpUntil(() => provider.Sent.Count == 1);
            Check("opened chat retains its history and provider session", ReferenceEquals(Steerable(clicked), provider)
                && clicked.SessionId == session && provider.Sent[0].Contains("Continue this existing chat")
                && clicked.Items.OfType<TextItem>().Any(t => t.Text == "Keep this earlier conversation."));
        }
        finally { AppSettings.Current.TerminalEnabled = enabled; clicked.Close(); }
    }
    private static (MainViewModel, ChatViewModel[]) RobotTeam(string name, int count, string provider)
    {
        var (vm, team) = Team(name, count, provider);
        foreach (var pane in team) Property(pane, "IsAgentTeamMember", true);
        return (vm, team);
    }
}
