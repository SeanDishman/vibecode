using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.UI;

internal static partial class Program
{
    static void AcknowledgmentIntegration()
    {
        var first = FreshTeam(false, "claude", "codex");
        var coordinator = first.Vm.BridgePanes.Single(p => p.IsBridgeManager);
        AssertAcknowledged(coordinator, "fresh xhigh orchestrator");

        var defaults = FreshTeam(false, "claude", "codex", "claude-fable-5", explicitDefault: true);
        var defaultCoordinator = defaults.Vm.BridgePanes.Single(p => p.IsBridgeManager);
        AssertAcknowledged(defaultCoordinator, "fresh different-model default-effort orchestrator");
        Check("stale startup model does not replace selected Fable/default", Config(defaultCoordinator) == new BridgeAgentConfiguration("claude", "claude-fable-5", null, FastMode: false));
        Check("explicit default is sent in model and flag controls", Requests(defaultCoordinator).Any(n => n["request"]?["subtype"]?.ToString() == "set_model" &&
                n["request"]!.AsObject().ContainsKey("effort") && n["request"]?["effort"] is null) &&
            Requests(defaultCoordinator).Any(n => n["request"]?["subtype"]?.ToString() == "apply_flag_settings" &&
                n["request"]?["settings"]?.AsObject().ContainsKey("effortLevel") == true && n["request"]?["settings"]?["effortLevel"] is null));

        var secondary = FreshTeam(true, "codex", "claude");
        foreach (var worker in secondary.Vm.SecondaryBridgePanes.Where(p => !p.IsBridgeManager))
            AssertAcknowledged(worker, "secondary max worker");

        var vm = first.Vm;
        var spare = Chat(first.Host.Cwd, "claude", absolute: true); spare.Model = "claude-opus-5"; spare.Effort = "xhigh";
        spare.Start(); Pump(() => spare.Status == "idle" && spare.SessionId is not null && (bool)GetField(spare, "_bridgeSessionInitialized")!);
        spare.BridgeLabel = "Claude " + (vm.BridgePanes.Count + 1); vm.BridgePanes.Add(spare); Call(vm, "Track", spare);
        var workerConfiguration = new BridgeAgentConfiguration("claude", "claude-fable-5", null, FastMode: false);
        var managerConfiguration = new BridgeAgentConfiguration("codex", "gpt-6.1-sol", "high", FastMode: false);
        var manager = vm.LaunchBridgeOrchestrator(first.Host, 1, "Review acknowledged reused worker coordinator", true,
            "codex", managerConfiguration, workerConfiguration); Chats.Add(manager);
        Check("initialized Claude worker is reused for explicit default policy", spare.BridgeCoordinatorAgentId == manager.BridgeAgentId);
        Check("retargeted worker immediately accepts first objective", spare.Send("Review acknowledged retargeted worker first objective"));
        Pump(() => spare.Status == "idle" && UserRequests(spare).Any());
        AssertAcknowledged(spare, "reused model change and xhigh-to-default worker");
        Check("reused worker wire model and effort match explicit policy", EffectiveAtFirstUser(spare) == workerConfiguration);

        var later = vm.LaunchBridgeWorker(manager); Chats.Add(later);
        Check("later Claude worker accepts objective during startup", later.Send("Review acknowledged later startup worker first objective"));
        Pump(() => later.Status == "idle" && UserRequests(later).Any());
        AssertAcknowledged(later, "later worker objective submitted during initialization");
        Check("later worker first objective sees inherited default effort", EffectiveAtFirstUser(later) == workerConfiguration);
        foreach (var provider in new[] { "codex", "claude" }) OrdinarySend(provider);
    }

    static void AssertAcknowledged(ChatViewModel chat, string label)
    {
        var observed = Requests(chat).First(n => n["fixture_user_received"]?.GetValue<bool>() == true);
        Check(label + " first task observes applied model AND effort", EffectiveAtFirstUser(chat) == Config(chat));
        Check(label + " first task follows every pending configuration ACK", observed["pending_controls"]!.GetValue<int>() == 0);
    }

    static BridgeAgentConfiguration EffectiveAtFirstUser(ChatViewModel chat)
    {
        var observed = Requests(chat).First(n => n["fixture_user_received"]?.GetValue<bool>() == true);
        return new(chat.Provider, observed["effective_model"]?.ToString(), observed["effective_effort"]?.ToString(), FastMode: observed["effective_fast_mode"]?.GetValue<bool>() == true);
    }

    static void OrdinarySend(string provider)
    {
        var chat = Chat("ordinary-" + provider, provider); chat.Model = provider == "codex" ? "gpt-6.1-sol" : "claude-opus-5";
        chat.Effort = provider == "codex" ? "high" : "xhigh"; var expected = Config(chat);
        chat.Start(); Check(provider + " ordinary chat accepts send during startup", chat.Send("Review ordinary first message"));
        Pump(() => chat.Status == "idle" && UserRequests(chat).Any());
        Check(provider + " ordinary first request retains launch model and effort", provider == "codex" ? FirstUserSettings(chat) == expected : EffectiveAtFirstUser(chat) == expected);
        Check(provider + " ordinary chat has no pending bridge configuration gate", !chat.IsBridgeAgent && GetField(chat, "_bridgeLaunchConfiguration") is null &&
            ((Task)GetField(chat, "_bridgeConfigurationApplication")!).IsCompletedSuccessfully);
        Check(provider + " ordinary chat accepts later send", chat.Send("Review ordinary second message"));
        Pump(() => chat.Status == "idle" && UserRequests(chat).Count() == 2);
        if (provider == "codex")
        {
            var second = UserRequests(chat).Last();
            Check(provider + " ordinary second request retains settings", second["params"]?["model"]?.ToString() == expected.Model && second["params"]?["effort"]?.ToString() == expected.Effort);
        }
        else
        {
            var second = Requests(chat).Where(n => n["fixture_user_received"]?.GetValue<bool>() == true).Last();
            Check(provider + " ordinary second request retains settings", second["effective_model"]?.ToString() == expected.Model && second["effective_effort"]?.ToString() == expected.Effort);
        }
    }

    // The reader remains live while ACKs are delayed. Applying fixture state only at ACK
    // catches a user request sent early, even if the controls were correctly ordered on stdin.
    static int AcknowledgmentFixture(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("REVIEW_FIXTURE_ROOT")!;
        var sessionId = "review-" + Environment.ProcessId; var path = Path.Combine(root, "trace-" + Environment.ProcessId + ".jsonl");
        var gate = new object(); var pending = new HashSet<string>();
        var modelIndex = Array.IndexOf(args, "--model"); var effortIndex = Array.IndexOf(args, "--effort");
        string? model = modelIndex < 0 ? "claude-opus-5" : args[modelIndex + 1];
        string? effort = effortIndex < 0 ? null : args[effortIndex + 1];
        var fastMode = LaunchFastMode(args);
        void Log(JsonObject row) { row["fixture_utc"] = DateTime.UtcNow.ToString("O"); File.AppendAllText(path, row.ToJsonString() + Environment.NewLine); }
        void Emit(JsonObject row) { Console.WriteLine(row.ToJsonString()); Console.Out.Flush(); }
        lock (gate) Log(new() { ["fixture_provider"] = "claude", ["fixture_args"] = JsonSerializer.SerializeToNode(args), ["fixture_session"] = sessionId,
            ["fixture_mode"] = "live reader, apply settings on delayed acknowledgment" });
        while (Console.ReadLine() is { } line)
        {
            var node = JsonNode.Parse(line)!.AsObject();
            lock (gate)
            {
                Log(node);
                if (node["type"]?.ToString() == "control_request")
                {
                    var request = node["request"]!.AsObject(); var requestId = node["request_id"]!.ToString();
                    var subtype = request["subtype"]?.ToString();
                    // Background MCP connection probes can overlap a turn. Only configuration controls
                    // must be acknowledged before the first objective reaches the provider.
                    if (subtype is "initialize" or "set_model" or "apply_flag_settings" or "set_permission_mode")
                        pending.Add(requestId);
                    var response = new JsonObject();
                    if (subtype == "initialize")
                    {
                        Emit(new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["model"] = model });
                        response["models"] = new JsonArray(new[] { "claude-opus-5", "claude-fable-5" }.Select(value => (JsonNode)new JsonObject {
                            ["value"] = value, ["displayName"] = value, ["supportedEffortLevels"] = new JsonArray("low", "medium", "high", "xhigh", "max"),
                            ["supportsEffort"] = true, ["supportsAutoMode"] = true, ["supportsFastMode"] = value == "claude-opus-5" }).ToArray());
                    }
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(subtype == "initialize" ? 350 : 180);
                        lock (gate)
                        {
                            if (subtype == "set_model") { model = request["model"]?.ToString() ?? model; effort = request["effort"]?.ToString(); }
                            if (subtype == "apply_flag_settings" && request["settings"]?.AsObject().ContainsKey("effortLevel") == true)
                                effort = request["settings"]?["effortLevel"]?.ToString();
                            if (subtype == "apply_flag_settings" && request["settings"]?["fastMode"] is { } speed)
                                fastMode = speed.GetValue<bool>();
                            pending.Remove(requestId);
                            Emit(new() { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response } });
                            Log(new() { ["fixture_ack_emitted"] = requestId, ["subtype"] = subtype, ["effective_model"] = model, ["effective_effort"] = effort, ["effective_fast_mode"] = fastMode });
                        }
                    });
                }
                else if (node["type"]?.ToString() == "user")
                {
                    Log(new() { ["fixture_user_received"] = true, ["effective_model"] = model, ["effective_effort"] = effort, ["effective_fast_mode"] = fastMode, ["pending_controls"] = pending.Count });
                    Emit(new() { ["type"] = "result", ["session_id"] = sessionId, ["is_error"] = false, ["result"] = CentralFixtureReply(node) ?? "Local asynchronous acknowledgment fixture complete" });
                }
            }
        }
        return 0;
    }
}
