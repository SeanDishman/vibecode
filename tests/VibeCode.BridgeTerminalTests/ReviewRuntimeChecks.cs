using System.Text.Json.Nodes;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifyReviewRuntimeChanges()
    {
        var (connectingVm, connecting) = Team("review-during-startup", 2);
        foreach (var agent in connecting) agent.Status = "idle";
        Property(connecting[0], "IsBridgeManager", true);
        Property(connecting[0], "BridgeCoordinatesOnly", true);
        Property(connecting[1], "BridgeCoordinatorAgentId", connecting[0].BridgeAgentId);
        connectingVm.SetBridgeTerminalMode(true);
        foreach (var agent in connecting)
        {
            typeof(ChatViewModel).GetField("_bridgeSessionInitialized", Flags)!.SetValue(agent, false);
            Call(agent, "ApplyBridgeConfiguration", new BridgeAgentConfiguration(agent.Provider, agent.Model, agent.Effort, "low"));
        }
        connectingVm.SetBridgeReviewLevel(connecting[0], "none");
        connectingVm.SetBridgeReviewLevel(connecting[1], "high");
        foreach (var agent in connecting) Call(agent, "CompleteBridgeConfigurationInitialization");
        var startupCurrent = connecting[0].BridgeReviewLevel == "none" && connecting[1].BridgeReviewLevel == "high";

        var (vm, team) = Team("review-queued-assignment", 2);
        foreach (var agent in team) agent.Status = "idle";
        var manager = team[0]; var worker = team[1];
        vm.ConfigureBridgeOrchestrator(manager, 1, "Repair the assigned areas.", true,
            new(manager.Provider, manager.Model, manager.Effort, "normal"),
            new(worker.Provider, worker.Model, worker.Effort, "low"));
        PumpUntil(() => Session(manager).Sent.Count == 1);
        Tool(manager, "bridge_dispatch_task", new() { ["recipient"] = worker.BridgeAgentId,
            ["task_name"] = "Repair first area", ["message"] = "Repair the first assigned area." });
        PumpUntil(() => Session(worker).Sent.Count == 1);
        Tool(manager, "bridge_dispatch_task", new() { ["recipient"] = worker.BridgeAgentId,
            ["task_name"] = "Repair next area", ["message"] = "Repair the next assigned area." });
        Check("a busy worker retains its next durable assignment before review settings change",
            Tool(manager, "bridge_list_tasks")["tasks"]!.AsArray().Any(t => t!["status"]!.ToString() == "queued"));
        vm.SetBridgeReviewLevel(worker, "none");
        worker.Items.Add(new TextItem { Text = "First area repaired and checked." });
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        PumpUntil(() => Session(worker).Sent.Count == 2);
        var sent = Session(worker).Sent[1];
        var queuedCurrent = sent.LastIndexOf("[REVIEW LEVEL: NONE", StringComparison.Ordinal) >
            sent.LastIndexOf("[REVIEW LEVEL: LOW", StringComparison.Ordinal);
        if (!startupCurrent || !queuedCurrent)
            Console.WriteLine($"Review regression evidence: startup manager={connecting[0].BridgeReviewLevel}, worker={connecting[1].BridgeReviewLevel}; " +
                $"queued None offset={sent.LastIndexOf("[REVIEW LEVEL: NONE", StringComparison.Ordinal)}, Low offset={sent.LastIndexOf("[REVIEW LEVEL: LOW", StringComparison.Ordinal)}.");
        Check("review choices changed during startup survive provider initialization", startupCurrent);
        Check("queued assignments receive the latest review setting when sent", queuedCurrent);
        worker.Items.Add(new TextItem { Text = "Next area repaired and checked." });
        Call(worker, "ApplyResult", new JsonObject { ["type"] = "result", ["is_error"] = false });
        var turns = Session(worker).Sent.Count;
        worker.Send("A later goal still uses my saved choice.");
        PumpUntil(() => Session(worker).Sent.Count == turns + 1);
        Check("later goals receive the saved review choice after the staged prelude was consumed",
            Session(worker).Sent.Last().Contains("[REVIEW LEVEL: NONE"));
        foreach (var choice in BridgeReviewPolicy.Choices)
        {
            Check("review instructions stay hidden in resumed user messages at " + choice.Label,
                (string)Call(worker, "StripInjectedPrelude", BridgeReviewPolicy.TurnInstructions(choice.Value, false) + "\n\nMy actual request.")! == "My actual request.");
            Check("older review instruction blocks stay hidden at " + choice.Label,
                (string)Call(worker, "StripInjectedPrelude", BridgeReviewPolicy.Instructions(choice.Value, true) + "\n\nMy actual request.")! == "My actual request.");
        }
    }
}
