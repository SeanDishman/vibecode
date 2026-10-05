using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void RunLiveBroadcastSimulations(string? caseFilter = null, bool stress = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")))
            throw new InvalidOperationException("Live simulations require the current signed-in CODEX_HOME.");
        AppSettings.Current.DefaultProvider = "codex";
        AppSettings.Current.DefaultCodexModel = "gpt-6-luna";
        AppSettings.Current.DefaultCodexEffort = "low";
        AppSettings.Current.SecondBrainEnabled = false;
        AppSettings.Current.AgentMemoryEnabled = false;
        AppSettings.Current.AgentSwarmsEnabled = false;

        AppSettings.Current.McpServers.Clear();
        AppSettings.Current.BridgeAgentLimit = 17;
        var results = new List<object>();
        var failures = new List<string>();
        var reportPath = Path.Combine(_root, (stress ? "luna-broadcast-stress-" : "luna-broadcast-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        foreach (var advanced in new[] { false, true })
            foreach (var scenario in stress
                ? new[] { "large-two", "paused", "unavailable-peer", "incoming-question", "incoming-fyi", "stale-recipient", "partial-failure" }
                : new[] { "one-peer", "two-peers", "three-peers", "all-peers", "routine-fyi", "unknown-audience", "already-shared" })
                Run(advanced, false, scenario);
        if (stress)
        {
            foreach (var scenario in new[] { "small-two", "small-three", "large-all" }) Run(false, false, scenario);
            if (caseFilter is null || caseFilter.Contains("exchange", StringComparison.Ordinal))
                foreach (var advanced in new[] { false, true })
                {
                    var exchange = RunLunaMailboxExchange(advanced);
                    results.Add(exchange);
                    if (!exchange["passed"]!.GetValue<bool>()) failures.Add(exchange["name"] + ": " + exchange["failure"]);
                    Save();
                }
        }
        else { Run(true, true, "three-peers"); Run(true, true, "locked-orchestrator"); }
        Console.WriteLine("Live broadcast report: " + reportPath);
        Check("all Luna messaging scenarios pass", results.Count > 0 && failures.Count == 0);

        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            model = "gpt-6-luna", effort = "low",
            method = "Real authenticated Luna turns using the production MCP endpoint and bridge routing. Decision cases use passive recipient sessions; exchange cases use two live Luna sessions across three turns.",
            results, failures,
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Run(bool advanced, bool orchestrator, string scenario)
        {
            var name = (advanced ? orchestrator ? "advanced-orchestrator" : "advanced-worker" : "normal") + "-" + scenario;
            if (caseFilter is not null && !caseFilter.Split(',').Contains(name, StringComparer.Ordinal)) return;
            Console.WriteLine("START: " + name + " / GPT-6 Luna low");
            var watch = Stopwatch.StartNew();
            var peerCount = scenario.StartsWith("large-", StringComparison.Ordinal) ? 16 : scenario == "small-two" ? 2 : scenario == "small-three" ? 3 : 5;
            var (vm, peers) = Team("luna-" + name, peerCount);
            foreach (var peer in peers)
            {
                peer.Status = "running";
                Property(peer, "BridgeTaskState", "working");
                peer.Draft = "Unsent peer draft";
            }
            var actor = new ChatViewModel(peers[0].Cwd, title: "Bridge messaging simulation", provider: "codex")
            { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true, BridgeLabel = "Codex " + (peerCount + 1) };
            Chats.Add(actor); vm.Chats.Add(actor); vm.BridgePanes.Add(actor); Call(vm, "Track", actor);
            if (advanced)
            {
                var lead = orchestrator ? actor : peers[0];
                foreach (var manager in new[] { lead, peers[3] })
                { Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true); }
                foreach (var peer in peers.Where(p => !p.IsBridgeManager))
                    Property(peer, "BridgeCoordinatorAgentId", (ReferenceEquals(peer, peers[4]) ? peers[3] : lead).BridgeAgentId);
                if (!orchestrator) Property(actor, "BridgeCoordinatorAgentId", lead.BridgeAgentId);
                vm.SetBridgeTerminalMode(true);
            }
            Tool(actor, "bridge_list_agents");
            string? incomingId = null;
            string? failedMailboxPath = null;
            if (scenario == "partial-failure")
            {
                Tool(peers[4], "bridge_read_messages");
                failedMailboxPath = ((BridgeMailboxStore.Mailbox)Property(peers[4], "PeerMailbox")!).FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(failedMailboxPath)!);
                File.WriteAllText(failedMailboxPath, "Simulation: temporarily replaced mailbox metadata.");
            }
            if (scenario == "paused") AppSettings.Current.BridgePeerMessaging = false;
            if (scenario == "unavailable-peer") peers[3].Status = "error";
            if (advanced)
            {
                var work = (BridgeWorkState)Property(actor, "BridgeWork")!;
                work.DispatchStarted = true;
                foreach (var manager in vm.BridgePanes.Where(p => p.IsBridgeManager))
                {
                    work.Scopes[manager.BridgeAgentId] = "Maintain assigned schema consumers";
                    work.ConfirmedVersions[manager.BridgeAgentId] = work.PlanVersion;
                }
            }
            Call(vm, "RefreshBridgeManagerBriefs");
            var calls = new List<JsonObject>();
            var changedAvailability = false;
            var handler = (Func<string, JsonObject, JsonObject>)Property(actor, "BridgeToolHandler")!;
            Property(actor, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                var entry = new JsonObject { ["tool"] = tool, ["input"] = input.DeepClone() };
                calls.Add(entry);
                try
                {
                    var result = handler(tool, input); entry["result"] = result.DeepClone();
                    if (scenario == "stale-recipient" && tool == "bridge_list_agents" && !changedAvailability)
                    { peers[4].Status = "error"; changedAvailability = true; }
                    if (failedMailboxPath is not null && (tool is "bridge_broadcast" or "bridge_send_message") && result["failures"] is JsonArray failed && failed.Count > 0)
                    { File.Move(failedMailboxPath, failedMailboxPath + ".fixture-held"); failedMailboxPath = null; }
                    Console.WriteLine(name + " | " + tool + " | " + input.ToJsonString());
                    return result;
                }
                catch (Exception ex) { entry["error"] = ex.Message; throw; }
            }));
            string? failure = null;
            string request = "";
            try
            {
                actor.SetMode("plan"); actor.Start();
                LiveWait(() => actor.Status is "idle" or "error", TimeSpan.FromSeconds(45), [actor]);
                Check(name + ": real Luna session starts", actor.Status == "idle" && actor.Model == "gpt-6-luna");
                if (scenario is "incoming-question" or "incoming-fyi")
                {
                    var receipt = Tool(peers[1], "bridge_send_message", new() { ["recipient"] = actor.BridgeAgentId,
                        ["message"] = scenario == "incoming-question" ? "Only I need this: what is the validated retry budget for our client?" : "The shared integration check passed; no action or reply is needed. This is retained context only." });
                    incomingId = receipt["delivered"]![0]!["message_id"]!.ToString();
                }
                var details = scenario switch
                {
                    "one-peer" => "You verified the response field changed to record_id. Only Agent 2 consumes that response and needs to update its current code. The other four peers are unaffected. Communicate the actionable finding.",
                    "two-peers" => "Urgent: the required timeout changed to 45 seconds. Only Agent 2 and Agent 3 own affected clients and need this value to unblock their work. All other peers are unaffected. Communicate this finding.",
                    "three-peers" => "The shared schema now requires record_id. Agent 2, Agent 3 and Agent 5 each own a consumer that is blocked until they adopt that field. This is an important shared integration change needed immediately by those three peers. The coordinators, if present, oversee these dependencies. Communicate the finding efficiently.",
                    "all-peers" => "The shared validation command was broken, blocking every other agent's current task. You verified its replacement is dotnet test SchemaChecks.csproj. Every other agent must use this new command immediately to resume work, including coordinators verifying their groups. Notify the whole bridge of this important actionable fix efficiently, once.",
                    "routine-fyi" => "Your routine local formatting check passed. All five peers might be interested, but this changes nothing for their work, creates no dependency or action item, and nobody asked for a notification. Record any useful activity and finish.",
                    "unknown-audience" => "You have an unverified hunch that a naming convention might affect other agents. You have not identified any affected peer, confirmed a change, or found an actionable blocker. Decide whether any communication is justified at this point, and finish.",
                    "already-shared" => "The important record_id schema change was already communicated to every affected peer and successfully delivered. There is no new information, question or work to assign. Decide whether any additional communication is useful, and finish.",
                    "locked-orchestrator" => "You are an orchestrator after worker dispatch. Only the other orchestrator, Agent 4, would benefit from a minor scope clarification; no worker needs it and no task is blocked. Decide whether any communication is appropriate under the bridge rules. Do not route the clarification through workers.",
                    "large-two" => "This is a large bridge. A critical timeout correction to 45 seconds affects ONLY Agent 2 and Agent 3. All other agents are unrelated. Notify the affected owners efficiently.",
                    "small-two" or "small-three" => "Every other agent in this small bridge owns a consumer blocked by the required record_id field. Notify all the other agents of this important shared schema requirement, using the current bridge rules.",
                    "large-all" => "Every other agent in this large bridge is blocked by a shared validation command failure. The verified replacement is dotnet test SchemaChecks.csproj. Notify all of them once with this important fix, efficiently.",
                    "paused" => "Agents 2, 3 and 5 need a shared schema update to use record_id, but peer messaging is paused by the user. Respect the current communication settings, explain any limitation briefly, and finish. Do not use files or another channel to bypass the pause.",
                    "unavailable-peer" => "Agents 2, 3 and 5 own blocked consumers that must adopt record_id immediately. Agent 4 is unrelated and unavailable. Communicate the actionable update to the affected owners and finish; do not retry or relay to unavailable agents.",
                    "incoming-question" => "Handle the retained incoming peer question in your mailbox. You independently verified that the retry budget is 5. Answer the peer who asked, handle the message, and finish. No other agent needs this answer.",
                    "incoming-fyi" => "Handle the retained incoming informational message in your mailbox and finish. It contains no question, action item or request for a reply; there is no new information to communicate.",
                    "stale-recipient" => "Only Agents 2, 3 and 5 own consumers needing the required record_id field. Notify the currently reachable affected peers. Agent 5 may become unavailable during this exercise; do not relay to anyone on its behalf. If availability changes, use the current roster and finish without polling.",
                    "partial-failure" => "All five peers are blocked until they use the corrected validation command dotnet test SchemaChecks.csproj. Notify all of them efficiently and ensure each receives the update only once. In this fault-injection simulation, one mailbox may temporarily fail; it recovers immediately after the failed delivery returns, so a single direct retry to that failed peer is appropriate. Do not repeat successful deliveries.",
                    _ => throw new InvalidOperationException(scenario),
                };
                request = "Text-only messaging simulation. Use the built-in bridge tools and current roster to decide how to handle this situation efficiently. " +
                    "Perform the useful communication, if any, then finish with a single sentence. Do not use shell, files, web, native subagents, new work assignments or polling. " +
                    "No code changes, calculations or extra verification are needed. " + details;
                Check(name + ": scenario submitted", actor.Send(request));
                LiveWait(() => actor.Status is "idle" or "error" && actor.Items.OfType<TextItem>().Any(), TimeSpan.FromSeconds(120), [actor]);
                Check(name + ": turn finishes successfully", actor.Status == "idle");
                var sends = calls.Where(c => c["tool"]!.ToString() == "bridge_send_message" && c["error"] is null).ToArray();
                var broadcasts = calls.Where(c => c["tool"]!.ToString() == "bridge_broadcast" && c["error"] is null).ToArray();
                var affected = scenario switch
                {
                    "one-peer" => new[] { peers[1] },
                    "two-peers" => new[] { peers[1], peers[2] },
                    "three-peers" => new[] { peers[1], peers[2], peers[4] },
                    "all-peers" => peers,
                    "large-two" or "stale-recipient" => new[] { peers[1], peers[2] },
                    "small-two" or "small-three" or "large-all" or "partial-failure" => peers,
                    "unavailable-peer" => new[] { peers[1], peers[2], peers[4] },
                    "incoming-question" => new[] { peers[1] },
                    _ => Array.Empty<ChatViewModel>(),
                };
                var broadcast = broadcasts.Length == 1 && sends.Length == 0 && affected.Length >= 3;
                var direct = broadcasts.Length == 0 && sends.Length == affected.Length;
                var repairedPartial = scenario == "partial-failure" && broadcasts.Length == 1 && sends.Length == 1;
                Check(name + ": chooses a permitted efficient message scope", broadcast || direct || repairedPartial);
                var expectedDirect = repairedPartial ? 1 : direct ? affected.Length : 0;
                string StableId(string address) => calls.Where(c => c["tool"]!.ToString() == "bridge_list_agents" && c["result"] is not null)
                    .SelectMany(c => c["result"]!["agents"]!.AsArray()).FirstOrDefault(p => p!["agent_id"]!.ToString() == address || p["message_recipient"]?.ToString() == address)?["agent_id"]?.ToString() ?? address;
                var directRecipients = sends.Select(c => StableId(c["input"]!["recipient"]!.ToString())).Order().ToArray();
                Check(name + ": direct messages reach only the affected peers", directRecipients.SequenceEqual((repairedPartial ? new[] { peers[4] } : affected.Take(expectedDirect)).Select(p => p.BridgeAgentId).Order()));
                if (broadcasts.Length == 1)
                {
                    var call = broadcasts[0];
                    var named = call["input"]!["affected_agent_ids"]!.AsArray().Select(p => StableId(p!.ToString())).ToArray();
                    Check(name + ": names real affected peers without padding", named.Length >= 3 && named.Distinct().Count() == named.Length && named.All(id => affected.Any(p => p.BridgeAgentId == id)));
                    Check(name + ": broadcast has an importance reason", call["input"]!["reason"]!.ToString().Length > 0);
                    var skipped = (orchestrator ? 1 : 0) + (scenario == "unavailable-peer" ? 1 : 0);
                    Check(name + ": real broadcast delivery obeys the role restrictions", call["result"]!["delivered"]!.AsArray().Count == peers.Length - skipped - (repairedPartial ? 1 : 0) && call["result"]!["failures"]!.AsArray().Count == (repairedPartial ? 1 : 0) && call["result"]!["skipped"]!.AsArray().Count == skipped && call["result"]!["delivery_complete"]!.GetValue<bool>() == !repairedPartial);
                }
                foreach (var peer in peers)
                {
                    var inbox = Tool(peer, "bridge_read_messages")["messages"]!.AsArray();
                    var expected = repairedPartial ? 1 : broadcast ? (orchestrator || scenario == "unavailable-peer") && ReferenceEquals(peer, peers[3]) ? 0 : 1
                        : affected.Take(expectedDirect).Contains(peer) ? 1 : 0;
                    Check(name + ": actual inbox count for " + peer.BridgeLabel, inbox.Count(m => m!["direction"]!.ToString() == "incoming" && m["sender_id"]!.ToString() == actor.BridgeAgentId) == expected);
                }
                var rejected = calls.Where(c => c["error"] is not null).ToArray();
                Check(name + ": only the injected availability race may reject one attempt", rejected.Length == 0 || scenario == "stale-recipient" && rejected.Length == 1 &&
                    (rejected[0]["error"]!.ToString().Contains("cannot receive") || rejected[0]["error"]!.ToString() == "No messages delivered. Session is not accepting input."));
                if (incomingId is not null)
                {
                    Check(name + ": reads and handles the actual incoming message", calls.Any(c => c["tool"]!.ToString() == "bridge_read_messages") && calls.Any(c => c["tool"]!.ToString() == "bridge_mark_message" && c["input"]!["message_id"]!.ToString() == incomingId));
                    if (scenario == "incoming-question") Check(name + ": reply carries the verified answer", sends.Single()["input"]!["message"]!.ToString().Contains('5'));
                }
                Check(name + ": bounded discovery and no polling", calls.Count(c => c["tool"]!.ToString() == "bridge_list_agents") <= 3 && calls.Count(c => c["tool"]!.ToString() == "bridge_list_tasks") <= 1 && calls.Count(c => c["tool"]!.ToString() == "bridge_read_messages") <= 2 && calls.Count <= Math.Max(stress ? 11 : 9, expectedDirect + 5));
                Check(name + ": no unnecessary tools or assignments", calls.All(c => c["tool"]!.ToString() is "chat_set_title" or "bridge_set_task_title" or "bridge_list_agents" or "bridge_list_tasks" or "bridge_read_messages" or "bridge_mark_message" or "bridge_report_activity" or "bridge_send_message" or "bridge_broadcast") &&
                    actor.Items.OfType<ToolItem>().All(t => !t.Name.Contains("exec_command", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("shell", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("spawn_agent", StringComparison.OrdinalIgnoreCase)));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(name + ": no peer model wakeups or draft changes", peers.All(p => Session(p).Sent.Count == 0 && p.Draft == "Unsent peer draft"));
            }
            catch (Exception ex)
            {
                failure = ex.Message; failures.Add(name + ": " + failure);
                Console.WriteLine("FAILED: " + name + " | " + failure);
            }
            finally
            {
                results.Add(new
                {
                    name, passed = failure is null, failure, request, model = actor.Model, effort = actor.Effort,
                    elapsed_seconds = watch.Elapsed.TotalSeconds, input_tokens = actor.TotalIn, output_tokens = actor.TotalOut,
                    bridge_calls = calls.Count, calls,
                    messages = actor.Items.OfType<TextItem>().Select(t => t.Text).ToArray(),
                    peer_model_turns = peers.Sum(p => Session(p).Sent.Count),
                });
                Save();
                AppSettings.Current.BridgePeerMessaging = true;
                actor.Close(); foreach (var peer in peers) peer.Close();
            }
        }
    }
}
