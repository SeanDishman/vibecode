using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyReviewLevels()
    {
        var legacy = JsonSerializer.Deserialize<BridgeAgentConfiguration>(
            "{\"Provider\":\"codex\",\"Model\":null,\"Effort\":null}")!;
        Check("older agent configurations default to Normal review", legacy.ReviewLevel == "normal");

        var (vm, team) = Team("review-levels", 3);
        foreach (var agent in team) agent.Status = "idle";
        var manager = team[0]; var worker = team[1]; var other = team[2];
        Check("new agents default to Normal review", team.All(agent => agent.BridgeReviewLevel == "normal"));
        vm.ConfigureBridgeOrchestrator(manager, 2, "Repair filtering and run its requested checks.", true,
            new("codex", manager.Model, manager.Effort, "none"),
            new("codex", worker.Model, worker.Effort, "low"));
        PumpUntil(() => Session(manager).Sent.Count == 1);
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("startup applies separate review defaults to orchestrator and workers",
            manager.BridgeReviewLevel == "none" && team.Skip(1).All(agent => agent.BridgeReviewLevel == "low") &&
            manager.BridgeWorkerConfiguration!.ReviewLevel == "low");
        var model = other.Model; var effort = other.Effort;
        vm.SetBridgeReviewLevel(other, " HIGH ");
        Check("individual review changes preserve other agents and model settings", other.BridgeReviewLevel == "high" &&
            other.Model == model && other.Effort == effort && worker.BridgeReviewLevel == "low" && manager.BridgeReviewLevel == "none");
        RejectArgument("unknown review choices are rejected", () => vm.SetBridgeReviewLevel(other, "unlimited"));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("review preferences do not purchase an extra model turn", Session(manager).Sent.Count == 1 &&
            Session(worker).Sent.Count == 0 && Session(other).Sent.Count == 0);
        Check("updated agent briefs contain the selected review budgets",
            manager.AppendSystemPrompt!.Contains("[REVIEW LEVEL: NONE") &&
            other.AppendSystemPrompt!.Contains("[REVIEW LEVEL: HIGH") &&
            other.AppendSystemPrompt.Contains("2 pass(es) for this assignment"));
        var agents = Tool(manager, "bridge_list_agents")["agents"]!.AsArray();
        Check("tool roster tells agents each peer's selected review level and limit",
            agents[0]!["review_level"]!.ToString() == "none" && agents[0]!["review_pass_limit"]!.ToString() == "0" &&
            agents[1]!["review_level"]!.ToString() == "low" && agents[1]!["review_pass_limit"]!.ToString() == "1" &&
            agents[2]!["review_level"]!.ToString() == "high" && agents[2]!["review_pass_limit"]!.ToString() == "2");

        var saved = AppSettings.Current.SavedBridges.Single(state => state.HostSessionId == manager.SessionId);
        var snapshot = JsonSerializer.Deserialize<SavedBridgeState>(JsonSerializer.Serialize(saved))!;
        Check("saved bridges persist per-agent review choices and worker launch defaults",
            snapshot.HostConfiguration!.ReviewLevel == "none" && snapshot.HostWorkerConfiguration!.ReviewLevel == "low" &&
            snapshot.Peers.Single(peer => peer.SessionId == worker.SessionId).Configuration!.ReviewLevel == "low" &&
            snapshot.Peers.Single(peer => peer.SessionId == other.SessionId).Configuration!.ReviewLevel == "high");
        var resumedVm = new MainViewModel();
        var resumed = team.Select(original =>
        {
            var chat = new ChatViewModel(original.Cwd, provider: original.Provider) { Status = "idle" };
            typeof(ChatViewModel).GetField("_session", Flags)!.SetValue(chat, new FakeSession());
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(chat, true);
            Property(chat, "SessionId", original.SessionId!);
            Chats.Add(chat); resumedVm.Chats.Add(chat); Call(resumedVm, "Track", chat);
            return chat;
        }).ToArray();
        Check("resuming a team restores every agent's review choice", resumedVm.RestoreBridge(resumed[0]) &&
            resumed[0].BridgeReviewLevel == "none" && resumed[1].BridgeReviewLevel == "low" && resumed[2].BridgeReviewLevel == "high" &&
            resumed[2].AppendSystemPrompt!.Contains("[REVIEW LEVEL: HIGH"));
        var replacement = new ChatViewModel(other.Cwd, provider: other.Provider);
        Chats.Add(replacement);
        Call(vm, "CopyBridgeTerminalState", other, replacement);
        Check("account replacement keeps the individual review choice", replacement.BridgeReviewLevel == "high");
        Call(replacement, "ApplyBridgeConfiguration", new BridgeAgentConfiguration("codex", model, effort, "obsolete"));
        Check("unrecognized saved review values safely fall back to Normal", replacement.BridgeReviewLevel == "normal");

        Tool(manager, "bridge_dispatch_task", new() { ["recipient"] = worker.BridgeAgentId, ["task_name"] = "Repair filtering",
            ["task_id"] = "repair-filtering", ["message"] = "Correct filtering and run the explicitly requested boundary checks." });
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Check("assignments include the worker's review limit and retain required checks",
            Session(worker).Sent[0].Contains("[REVIEW LEVEL: LOW") && Session(worker).Sent[0].Contains("1 pass(es) for this assignment") &&
            Session(worker).Sent[0].Contains("checks explicitly requested by the user still apply"));
        Reject("None still waits for active worker assignments", () => Tool(manager, "bridge_review_scope", new()
            { ["verdict"] = "approved", ["summary"] = "No extra inspection requested." }));
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = true });
        PumpUntil(() => Session(manager).Sent.Count == 2);
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Reject("None cannot mark failed work complete", () => Tool(manager, "bridge_review_scope", new()
            { ["verdict"] = "approved", ["summary"] = "The assignment failed." }));
        worker.Status = "idle";
        Tool(manager, "bridge_retry_task", new() { ["task_id"] = "repair-filtering" });
        PumpUntil(() => Session(worker).Sent.Count == 2);
        worker.Items.Add(new TextItem { Text = "Filtering repaired; the requested boundary checks passed. Extra review was limited to one quick pass." });
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        PumpUntil(() => Session(manager).Sent.Count == 3);
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var done = Tool(manager, "bridge_review_scope", new() { ["verdict"] = "approved",
            ["summary"] = "Required repair and requested checks complete. Extra review was disabled." });
        Check("None records skipped review instead of claiming a passed inspection", done["review_state"]!.ToString() == "skipped" &&
            done["all_scopes_reviewed"]!.ToString() == "true" && manager.BridgeReviewLabel == "Review skipped" && manager.BridgeTaskState == "completed");
        Call(manager, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        Check("ending a turn preserves completion with review disabled", manager.BridgeTaskState == "completed");
        var before = Session(manager).Sent.Count;
        var receipt = Tool(other, "bridge_send_message", new() { ["recipient"] = manager.BridgeAgentId, ["message"] = "Retain this context for the next goal." });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check("completed scopes with None do not wake for optional peer chatter",
            receipt["delivered"]![0]!["notification"]!.ToString() == "mailbox_only" && Session(manager).Sent.Count == before);
        vm.SetBridgeReviewLevel(manager, "normal");
        Check("enabling review cannot reuse a skipped inspection as a passed review",
            Tool(manager, "bridge_list_agents")["all_scopes_reviewed"]!.ToString() == "false" &&
            manager.BridgeReviewState == "pending" && manager.BridgeReviewLabel == "Final review pending" && manager.BridgeTaskState == "waiting");

        var (multiVm, groups) = Team("mixed-review-levels", 4);
        foreach (var agent in groups) agent.Status = "idle";
        foreach (var coordinator in new[] { groups[0], groups[2] })
        { Property(coordinator, "IsBridgeManager", true); Property(coordinator, "BridgeCoordinatesOnly", true); }
        Property(groups[1], "BridgeCoordinatorAgentId", groups[0].BridgeAgentId);
        Property(groups[3], "BridgeCoordinatorAgentId", groups[2].BridgeAgentId);
        multiVm.SetBridgeTerminalMode(true);
        multiVm.SetBridgeReviewLevel(groups[0], "none");
        multiVm.SetBridgeReviewLevel(groups[2], "high");
        Tool(groups[0], "bridge_send_message", new() { ["recipient"] = groups[2].BridgeAgentId, ["message"] = "Catalogue is my scope; search is yours." });
        Tool(groups[2], "bridge_send_message", new() { ["recipient"] = groups[0].BridgeAgentId, ["message"] = "Agreed; I own search." });
        Tool(groups[0], "bridge_read_messages"); Tool(groups[2], "bridge_read_messages");
        Tool(groups[0], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Catalogue" });
        Tool(groups[2], "bridge_agree_scope", new() { ["plan_version"] = 1, ["scope"] = "Search" });
        PumpUntil(() => Session(groups[0]).Sent.Count > 0);
        Call(groups[0], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var first = Tool(groups[0], "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Catalogue complete; extra review disabled." });
        Check("a skipped group still waits for the enabled group's review", first["review_state"]!.ToString() == "skipped" &&
            first["all_scopes_reviewed"]!.ToString() == "false");
        Check("another group's review does not wake an orchestrator", Session(groups[2]).Sent.Count == 0);
        Check("the second group can independently start its own review", groups[2].Send("Review your completed search scope."));
        for (var turn = 0; groups[2].HasQueued && turn < 4; turn++)
        {
            var sent = Session(groups[2]).Sent.Count;
            Call(groups[2], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
            PumpUntil(() => Session(groups[2]).Sent.Count > sent);
        }
        Call(groups[2], "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var final = Tool(groups[2], "bridge_review_scope", new() { ["verdict"] = "approved", ["summary"] = "Search and integration inspected; checks passed." });
        Check("mixed review levels complete after each group records its configured result",
            final["all_scopes_reviewed"]!.ToString() == "true" && groups[0].BridgeReviewState == "skipped" && groups[2].BridgeReviewState == "approved");
        Check("skipped review remains visible without a cross-group notification", groups[0].BridgeReviewState == "skipped" &&
            !Session(groups[0]).Sent.Any(message => message.Contains("All orchestrators completed their configured reviews")));
    }
}
