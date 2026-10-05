using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    static string? ReviewLevelOnWire(JsonNode request) => Regex.Matches(request.ToJsonString(),
        @"\[REVIEW LEVEL: (NONE|LOW|NORMAL|HIGH)").Cast<Match>().LastOrDefault()?.Groups[1].Value.ToLowerInvariant();

    static string UserTextOnWire(JsonNode request)
    {
        var content = request["params"]?["input"] ?? request["message"]?["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return content is JsonArray parts ? string.Join("\n", parts.Where(part => part?["type"]?.ToString() == "text")
            .Select(part => part!["text"]!.ToString())) : "";
    }

    static void VerifyReviewDelivery(bool secondary, string orchestratorProvider, string workerProvider)
    {
        var (vm, _) = FreshTeam(secondary, orchestratorProvider, workerProvider,
            orchestratorReview: "none", workerReview: "low");
        var panes = secondary ? vm.SecondaryBridgePanes : vm.BridgePanes;
        var manager = panes.Single(agent => agent.IsBridgeManager);
        var worker = panes.First(agent => agent.BridgeCoordinatorAgentId == manager.BridgeAgentId);
        var unchangedWorker = panes.Single(agent => agent.BridgeCoordinatorAgentId == manager.BridgeAgentId && agent != worker);
        var tag = $"{(secondary ? "secondary" : "primary")}-{orchestratorProvider}/{workerProvider}";
        var managerModel = manager.Model; var managerEffort = manager.Effort;
        var workerModel = worker.Model; var workerEffort = worker.Effort;
        Check(tag + " setup review budgets reach both provider inputs",
            ReviewLevelOnWire(UserRequests(manager).First()) == "none" && ReviewLevelOnWire(UserRequests(worker).First()) == "low");
        foreach (var agent in new[] { manager, worker })
        {
            var visible = (string)CallStatic(typeof(ChatViewModel), "StripInjectedPrelude", UserTextOnWire(UserRequests(agent).First()))!;
            Check(tag + " provider replay hides review instructions for " + agent.BridgeTerminalIdentity,
                !visible.Contains("[REVIEW LEVEL:") && !visible.Contains("[BRIDGE REVIEW SETTINGS]") && visible.Contains("Review fixture"));
        }
        foreach (var choice in BridgeReviewPolicy.Choices)
        {
            var managerTurns = UserRequests(manager).Count(); var workerTurns = UserRequests(worker).Count();
            vm.SetBridgeReviewLevel(manager, choice.Value);
            vm.SetBridgeReviewLevel(worker, choice.Value);
            PumpFor(0.15);
            Check(tag + " changing to " + choice.Label + " does not start an extra provider turn",
                UserRequests(manager).Count() == managerTurns && UserRequests(worker).Count() == workerTurns);
            Check(tag + " review choices leave models, effort and other workers intact",
                manager.Model == managerModel && manager.Effort == managerEffort && worker.Model == workerModel &&
                worker.Effort == workerEffort && unchangedWorker.BridgeReviewLevel == "low");
            Check(tag + " accepts the orchestrator's next goal at " + choice.Label,
                manager.Send("Review setting fixture goal at " + choice.Label));
            Check(tag + " accepts the worker's next assignment at " + choice.Label,
                worker.Send("Review setting fixture assignment at " + choice.Label));
            Pump(() => manager.Status == "idle" && !manager.HasQueued && worker.Status == "idle" && !worker.HasQueued &&
                UserRequests(manager).Count() > managerTurns && UserRequests(worker).Count() > workerTurns);
            var managerRequest = UserRequests(manager).Last(); var workerRequest = UserRequests(worker).Last();
            Check(tag + " real adapter delivers the current " + choice.Label + " review budgets",
                ReviewLevelOnWire(managerRequest) == choice.Value && ReviewLevelOnWire(workerRequest) == choice.Value &&
                managerRequest.ToJsonString().Contains($"{choice.PassLimit} pass(es) for this goal") &&
                workerRequest.ToJsonString().Contains($"{choice.PassLimit} pass(es) for this assignment"));
        }
        AppSettings.Current.Save();
        var reloaded = (AppSettings)CallStatic(typeof(AppSettings), "Load")!;
        var snapshot = reloaded.SavedBridges.Single(bridge => bridge.HostSessionId == panes[0].SessionId);
        string? SavedLevel(ChatViewModel agent) => agent.SessionId == snapshot.HostSessionId ? snapshot.HostConfiguration?.ReviewLevel
            : snapshot.Peers.Single(peer => peer.SessionId == agent.SessionId).Configuration?.ReviewLevel;
        Check(tag + " independent review choices persist to actual settings storage",
            SavedLevel(manager) == "high" && SavedLevel(worker) == "high" && SavedLevel(unchangedWorker) == "low");
    }
}
