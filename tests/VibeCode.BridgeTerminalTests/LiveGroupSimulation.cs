using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunLiveTwoOrchestrators()
    {
        const string name = "two-orchestrators";
        Console.WriteLine("START: two-orchestrators, GPT-6 Luna / low, two groups of one worker.");
        var workspace = Path.Combine(Path.GetTempPath(), "VibeCode-two-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var vm = new MainViewModel();
        var host = new ChatViewModel(workspace, title: "Bridge verification · two groups", provider: "codex")
            { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true };
        host.SetMode("plan"); Chats.Add(host); vm.Chats.Add(host); Call(vm, "Track", host); Property(vm, "ActiveChat", host);
        host.Start();
        LiveWait(() => host.Status is "idle" or "error", TimeSpan.FromSeconds(45), [host]);
        Check(name + ": provider starts", host.Status == "idle");
        vm.ActivateBridge("codex");
        while (vm.BridgePanes.Count < 4) vm.AddBridgeAgent("codex");
        var team = vm.BridgePanes.ToArray();
        foreach (var peer in team.Skip(1)) Chats.Add(peer);
        LiveWait(() => team.All(p => p.Status is "idle" or "error"), TimeSpan.FromSeconds(45), team);
        Check(name + ": every live agent uses Luna at low effort", team.All(p => p.Status == "idle" && p.Model == "gpt-6-luna" && p.Effort == "low"));
        var calls = new List<(string Agent, string Tool, JsonObject Input, JsonObject Result)>();
        foreach (var chat in team)
        {
            var handler = (Func<string, JsonObject, JsonObject>)Property(chat, "BridgeToolHandler")!;
            Property(chat, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                var result = handler(tool, input);
                calls.Add((chat.BridgeAgentId, tool, (JsonObject)input.DeepClone(), (JsonObject)result.DeepClone()));
                var line = $"{name} | {chat.BridgeTerminalIdentity} | {tool} | {input.ToJsonString()}";
                LiveEvents.Add(line); Console.WriteLine(line);
                return result;
            }));
        }
        var shell = (MainWindow)typeof(MainWindow).GetConstructors(Flags).Single(c => c.GetParameters().Length == 3).Invoke([vm, null, true]);
        vm.ConfigureBridgeOrchestrator(team[0], 1, """
            Tiny text-only two-group test. The shared goal is to calculate and independently verify 17+25=42.
            You are one of two peer orchestrators. Your group proposes calculation; Agent 3's group proposes
            independent verification. Exchange the proposed division, read actual replies, and each agree scope.
            Use only bridge tools, no shell/files/web/board edits/native subagents. Keep all messages brief.
            After scopes agree, assign ONLY your own Agent 2: compute 17+25 and report CALCULATION_OK: 42.
            Wait for its real report, inspect it, then record approved with bridge_review_scope including evidence.
            After your own group's actual review, tell the user GROUP_COMPLETE: 42 and stop. The app tracks both reviews.
            Publish your step before sending your ONE setup proposal. Confirm the exact latest plan_version.
            Do not poll or send acknowledgments. Do not invent additional verification assignments.
            """, true);
        vm.ConfigureBridgeOrchestrator(team[2], 1, """
            Tiny text-only two-group test. The shared goal is to calculate and independently verify 17+25=42.
            You are the peer orchestrator of Agent 1. Your group proposes independent verification; Agent 1's
            group proposes calculation. Exchange the proposed division, read actual replies, and each agree scope.
            Use only bridge tools, no shell/files/web/board edits/native subagents. Keep all messages brief.
            After scopes agree, assign ONLY your own Agent 4: check 42-25=17 and report VERIFICATION_OK: 42.
            Wait for its real report, inspect it, then record approved with bridge_review_scope including evidence.
            After your own group's actual review, tell the user GROUP_COMPLETE: 42 and stop. The app tracks both reviews.
            Publish your step before sending your ONE setup proposal. Confirm the exact latest plan_version.
            Do not poll or send acknowledgments. Do not invent additional verification assignments.
            """, true);
        var captured = false;
        var stable = Stopwatch.StartNew();
        LiveWait(() =>
        {
            if (!captured && calls.Any(c => c.Tool == "bridge_dispatch_task"))
            { SaveLiveShell(shell, 1400, 900, name + "-working.png"); captured = true; }
            var done = team.All(c => c.Status == "idle" && !c.HasQueued) &&
                team.Where(c => c.IsBridgeManager).All(c => c.BridgeReviewState == "approved") &&
                team.Where(c => c.IsBridgeManager).All(c => c.Items.OfType<TextItem>().Any(t => t.Text.Contains("GROUP_COMPLETE: 42")));
            if (!done) stable.Restart();
            return done && stable.Elapsed > TimeSpan.FromSeconds(2);
        }, TimeSpan.FromMinutes(3), team);
        var dispatches = calls.Where(c => c.Tool == "bridge_dispatch_task").ToArray();
        Check(name + ": real peer proposals are sent, read and agreed", new[] { team[0], team[2] }.All(coordinator =>
            new[] { "bridge_send_message", "bridge_read_messages", "bridge_agree_scope" }.All(tool => calls.Any(c => c.Agent == coordinator.BridgeAgentId && c.Tool == tool))));
        var lastAgreement = calls.FindIndex(c => c.Tool == "bridge_agree_scope" && c.Result["coordination_ready"]?.ToString() == "true");
        Check(name + ": dispatch begins after both scopes are agreed", lastAgreement >= 0 && calls.FindIndex(c => c.Tool == "bridge_dispatch_task") > lastAgreement);
        Check(name + ": each orchestrator sends exactly one setup message and none after dispatch", new[] { team[0], team[2] }.All(coordinator =>
            calls.Count(c => c.Agent == coordinator.BridgeAgentId && c.Tool == "bridge_send_message") == 1) &&
            !calls.Skip(calls.FindIndex(c => c.Tool == "bridge_dispatch_task")).Any(c => c.Tool == "bridge_send_message" && (c.Agent == team[0].BridgeAgentId || c.Agent == team[2].BridgeAgentId)));
        Check(name + ": both orchestrators confirm the same exact version", calls.Where(c => c.Tool == "bridge_agree_scope").Select(c => c.Input["plan_version"]!.ToString()).Distinct().Count() == 1);
        Check(name + ": each orchestrator dispatches only its own worker", dispatches.Length == 2 && dispatches.All(c =>
            c.Agent == team[0].BridgeAgentId && c.Input["recipient"]?.ToString() == team[1].BridgeAgentId ||
            c.Agent == team[2].BridgeAgentId && c.Input["recipient"]?.ToString() == team[3].BridgeAgentId));
        Check(name + ": both reviews include actual worker results", team[0].BridgeReviewState == "approved" && team[2].BridgeReviewState == "approved" &&
            team[1].Items.OfType<TextItem>().Any(t => t.Text.Contains("CALCULATION_OK: 42")) &&
            team[3].Items.OfType<TextItem>().Any(t => t.Text.Contains("VERIFICATION_OK: 42")));
        Check(name + ": product completion waits for both group reviews", calls.Any(c => c.Tool == "bridge_review_scope" && c.Result["all_scopes_reviewed"]?.ToString() == "true"));
        SaveLiveShell(shell, 1400, 900, name + "-complete.png");
        SaveLiveShell(shell, 1000, 850, name + "-narrow.png");
        File.WriteAllText(Path.Combine(_root, name + "-tools.json"), JsonSerializer.Serialize(calls.Select(c => new { agent = c.Agent, tool = c.Tool, input = c.Input, result = c.Result }), new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_root, name + ".json"), JsonSerializer.Serialize(team.Select(chat => new
        {
            agent = chat.BridgeTerminalIdentity, model = chat.Model, status = chat.Status, task_state = chat.BridgeTaskState,
            coordinator = chat.BridgeCoordinatorAgentId, scope = chat.BridgeOrchestrationScope,
            review_state = chat.BridgeReviewState, review_summary = chat.BridgeReviewSummary,
            input_tokens = chat.TotalIn, output_tokens = chat.TotalOut, messages = chat.Items.OfType<TextItem>().Select(t => t.Text).ToArray(),
        }), new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"COMPLETE: {name}; input={team.Sum(c => c.TotalIn)}, output={team.Sum(c => c.TotalOut)} tokens.");
        vm.CloseBridge(); foreach (var chat in team) chat.Close();
    }
}
