using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static JsonObject RunLunaMailboxExchange(bool advanced)
    {
        var name = (advanced ? "advanced" : "normal") + "-live-exchange";
        Console.WriteLine("START: " + name + " / two real GPT-6 Luna sessions");
        var watch = Stopwatch.StartNew();
        var (vm, passive) = Team(name, 4);
        foreach (var peer in passive) peer.Status = "running";
        var actors = Enumerable.Range(5, 2).Select(number => new ChatViewModel(passive[0].Cwd, provider: "codex")
        { Model = "gpt-6-luna", Effort = "low", ExcludeFromMemory = true, BridgeLabel = "Codex " + number }).ToArray();
        foreach (var actor in actors) { Chats.Add(actor); vm.Chats.Add(actor); vm.BridgePanes.Add(actor); Call(vm, "Track", actor); }
        if (advanced)
        {
            foreach (var manager in new[] { passive[0], passive[2] })
            { Property(manager, "IsBridgeManager", true); Property(manager, "BridgeCoordinatesOnly", true); }
            Property(passive[1], "BridgeCoordinatorAgentId", passive[0].BridgeAgentId);
            Property(passive[3], "BridgeCoordinatorAgentId", passive[2].BridgeAgentId);
            Property(actors[0], "BridgeCoordinatorAgentId", passive[0].BridgeAgentId);
            Property(actors[1], "BridgeCoordinatorAgentId", passive[2].BridgeAgentId);
            vm.SetBridgeTerminalMode(true);
        }
        Tool(actors[0], "bridge_list_agents");
        if (advanced)
        {
            var work = (BridgeWorkState)Property(actors[0], "BridgeWork")!;
            work.DispatchStarted = true;
            foreach (var manager in new[] { passive[0], passive[2] })
            { work.Scopes[manager.BridgeAgentId] = "Own assigned retry settings"; work.ConfirmedVersions[manager.BridgeAgentId] = work.PlanVersion; }
        }
        Call(vm, "RefreshBridgeManagerBriefs");
        var calls = new List<JsonObject>();
        foreach (var actor in actors)
        {
            var handler = (Func<string, JsonObject, JsonObject>)Property(actor, "BridgeToolHandler")!;
            Property(actor, "BridgeToolHandler", new Func<string, JsonObject, JsonObject>((tool, input) =>
            {
                var call = new JsonObject { ["agent"] = actor.BridgeLabel, ["tool"] = tool, ["input"] = input.DeepClone() };
                calls.Add(call);
                try
                {
                    var result = handler(tool, input); call["result"] = result.DeepClone();
                    Console.WriteLine(name + " | " + actor.BridgeLabel + " | " + tool + " | " + input.ToJsonString());
                    return result;
                }
                catch (Exception ex) { call["error"] = ex.Message; throw; }
            }));
        }
        string? failure = null;
        try
        {
            foreach (var actor in actors) { actor.SetMode("plan"); actor.Start(); }
            LiveWait(() => actors.All(p => p.Status is "idle" or "error"), TimeSpan.FromSeconds(45), actors);
            Check(name + ": both live Luna sessions initialize", actors.All(p => p.Status == "idle" && p.Model == "gpt-6-luna"));
            const string scope = "Text-only bridge exercise. Only bridge tools; no shell, files, web, assignments, native subagents or polling. Finish after the useful action. ";
            Turn(actors[0], scope + "You need the validated retry budget from Agent 6 only. Ask that peer once through the bridge, then finish your turn. The value is unknown to you until the reply arrives; no other peer needs this question.");
            Check(name + ": question reaches the other real Luna inbox without waking it", actors[1].UnreadPeerMessageCount == 1 && !actors[1].Items.OfType<UserItem>().Any());
            Turn(actors[1], scope + "Handle the actual question in your retained inbox. You independently verified that the retry budget is 5. Reply concisely only to the sender, mark the question handled, and finish.");
            Check(name + ": response does not start another requester turn", actors[0].Items.OfType<UserItem>().Count() == 1 && actors[0].UnreadPeerMessageCount == 1);
            Turn(actors[0], scope + "Read and handle the actual incoming answer. State the verified retry budget in your final answer. There is no follow-up question or new information for any peer.");
            var messages = calls.Where(c => c["tool"]!.ToString() == "bridge_send_message").ToArray();
            Check(name + ": exactly one question and one direct answer", messages.Length == 2 && messages.Select(c => c["agent"]!.ToString()).Distinct().Count() == 2 && calls.All(c => c["tool"]!.ToString() != "bridge_broadcast"));
            Check(name + ": both incoming messages are handled", calls.Count(c => c["tool"]!.ToString() == "bridge_mark_message") == 2 && actors.All(p => p.UnreadPeerMessageCount == 0));
            Check(name + ": requester uses the actual received value", actors[0].Items.OfType<TextItem>().Last().Text.Contains('5'));
            Check(name + ": unrelated inboxes stay empty", passive.All(p => p.UnreadPeerMessageCount == 0));
            Check(name + ": no failed messaging calls", calls.All(c => c["error"] is null));
            Check(name + ": no polling or acknowledgment loop", calls.Count <= 21 && calls.Count(c => c["tool"]!.ToString() == "bridge_read_messages") <= 3 && actors.Sum(p => p.Items.OfType<UserItem>().Count()) == 3);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(name + ": no passive model wakeups", passive.All(p => Session(p).Sent.Count == 0));
        }
        catch (Exception ex) { failure = ex.Message; Console.WriteLine("FAILED: " + name + " | " + failure); }
        var report = new JsonObject
        {
            ["name"] = name, ["passed"] = failure is null, ["failure"] = failure,
            ["model"] = "gpt-6-luna", ["live_agents"] = 2, ["live_turns"] = actors.Sum(p => p.Items.OfType<UserItem>().Count()),
            ["elapsed_seconds"] = watch.Elapsed.TotalSeconds, ["bridge_calls"] = calls.Count,
            ["input_tokens"] = actors.Sum(p => p.TotalIn), ["output_tokens"] = actors.Sum(p => p.TotalOut),
            ["calls"] = new JsonArray(calls.Select(c => (JsonNode?)c.DeepClone()).ToArray()),
            ["peer_model_turns"] = passive.Sum(p => Session(p).Sent.Count),
            ["messages"] = new JsonArray(actors.Select(p => (JsonNode?)new JsonObject
            { ["agent"] = p.BridgeLabel, ["text"] = string.Join("\n", p.Items.OfType<TextItem>().Select(t => t.Text)) }).ToArray()),
        };
        File.WriteAllText(Path.Combine(_root, name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var actor in actors) actor.Close();
        foreach (var peer in passive) peer.Close();
        return report;

        void Turn(ChatViewModel actor, string prompt)
        {
            var before = actor.Items.OfType<TextItem>().Count();
            Check(name + ": submits " + actor.BridgeLabel + " turn", actor.Send(prompt));
            LiveWait(() => actor.Status is "idle" or "error" && actor.Items.OfType<TextItem>().Count() > before, TimeSpan.FromSeconds(120), actors);
            Check(name + ": completes " + actor.BridgeLabel + " turn", actor.Status == "idle");
        }
    }
}
